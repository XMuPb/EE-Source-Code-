using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;
using BandItPlus.Cultures;

namespace BandItPlus.Quests
{
    // 2026-07-03 — chief SIGNATURE quest engine. One bespoke job per named chief,
    // offered at trust T2, once per campaign. All personality lives in
    // CultureProfile.Signature (SignatureQuestSpec); this class is the shared
    // mechanical spine: spawn a synthesized target party near the culture's
    // hideout, confront it (parley choices or battle — dialog lines live in
    // BanditDialogManager), return to the chief for the closing beat.
    //
    // Division of labour (mirrors pledge/alliance):
    // - BanditDialogManager owns every conversation line, the trust/peace gating
    //   and the _signatureState once-per-campaign record. Its consequences call
    //   into this quest's public Mark*/Conclude* methods.
    // - This quest owns the journal, the target party lifecycle (spawn, track,
    //   deferred despawn, leak-proof finalize) and battle/third-party detection.
    //
    // SAVEABLE: offset 9 in BandItPlusSaveableTypeDefiner. Registered
    // unconditionally so saves load with the MCM toggle in either state.
    public class BanditSignatureQuest : QuestBase
    {
        // Stage machine: "travel" (target alive, confrontation pending)
        //   -> "return" (resolved peacefully OR battle won OR third-party kill; chief waits)
        //   -> "done-honored" / "done-betrayed" (terminal, set just before Complete*)
        //   plus "betrayed" (bribe taken; waiting for the encounter to close so the
        //   target can despawn safely before the fail-out).
        [SaveableField(1)] private string _cultureId;
        [SaveableField(2)] private string _stage;
        [SaveableField(3)] private string _outcome;   // "" / "full" / "half" / "battle" / "othercause"
        [SaveableField(4)] private string _flavor;    // Borchu pre-battle pick, Khalid stock pick
        [SaveableField(5)] private MobileParty _targetParty;
        [SaveableField(6)] private bool _demandFailed;
        [SaveableField(7)] private bool _pendingDespawn;

        private bool _eventsSubscribed;

        public string CultureId => _cultureId;
        public string Stage => _stage;
        public string Outcome => _outcome;
        public string Flavor { get => _flavor; set => _flavor = value; }
        public MobileParty TargetParty => _targetParty;
        public bool DemandFailed { get => _demandFailed; set => _demandFailed = value; }

        public BanditSignatureQuest(string questId, Hero questGiver, string cultureId)
            : base(questId, questGiver, CampaignTime.Never, 0)
        {
            _cultureId = cultureId;
            _stage = "travel";
            _outcome = "";
            _flavor = "";
        }

        public SignatureQuestSpec Spec
        {
            get
            {
                CultureProfile p;
                if (_cultureId != null
                    && CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out p))
                    return p.Signature;
                return null;
            }
        }

        public override TextObject Title
        {
            get
            {
                var spec = Spec;
                return new TextObject(spec != null && !string.IsNullOrEmpty(spec.Title)
                    ? spec.Title : "{=bp_signaturequest_001}The Chief's Work");
            }
        }

        // No timer — the chief waits. Gold journal icon like the other chief quests.
        public override bool IsRemainingTimeHidden => true;
        public override string SpecialQuestType => "BanditSignatureQuest";

        protected override void SetDialogs() { }
        protected override void OnTimedOut() { }

        protected override void RegisterEvents()
        {
            DoRegisterEvents();
        }

        protected override void InitializeQuestOnGameLoad()
        {
            DoRegisterEvents();
            EnsureTargetTracked();  // AddTrackedObject does not survive save/load
        }

        private void DoRegisterEvents()
        {
            if (_eventsSubscribed) return;
            _eventsSubscribed = true;
            try
            {
                CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnMobilePartyDestroyed);
                CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            }
            catch (Exception ex)
            {
                Log("[ERR] DoRegisterEvents " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Start
        // ------------------------------------------------------------------

        public static BanditSignatureQuest FindActive()
        {
            try
            {
                return Campaign.Current?.QuestManager?.Quests?
                    .OfType<BanditSignatureQuest>().FirstOrDefault(q => q.IsOngoing);
            }
            catch { return null; }
        }

        public static BanditSignatureQuest FindActiveForCulture(string cultureId)
        {
            var q = FindActive();
            return q != null && q._cultureId == cultureId ? q : null;
        }

        // Exclusivity hook for BanditDialogManager.IsBpEncounter — the friendly
        // roadside tree must never fire on the quest target.
        public static bool IsSignatureTargetParty(MobileParty party)
        {
            if (party == null) return false;
            var q = FindActive();
            return q != null && q._targetParty == party;
        }

        // Creates the journal quest + spawns the target party. Returns null (and
        // spawns nothing) on any failure — caller logs and aborts the offer.
        public static BanditSignatureQuest Begin(string cultureId, Hero chief)
        {
            try
            {
                if (FindActive() != null) return null;   // one signature at a time
                CultureProfile profile;
                if (!CultureProfileRegistry.ByCultureId.TryGetValue(cultureId, out profile)
                    || profile.Signature == null)
                    return null;

                Settlement seed = ResolveSeedHideout(cultureId, chief);
                if (seed == null)
                {
                    Log("[ERR] Begin: no hideout found for culture " + cultureId);
                    return null;
                }

                MobileParty target = SpawnTargetParty(profile.Signature, cultureId, seed);
                if (target == null) return null;

                string questId = "bp_signature_" + cultureId + "_" + ((long)CampaignTime.Now.ToHours);
                var quest = new BanditSignatureQuest(questId, chief, cultureId);
                quest._targetParty = target;

                // StartQuest (NOT QuestManager.OnQuestStarted): decompile-verified —
                // StartQuest sets Ongoing, calls RegisterEvents (kill detection lives
                // there) and dispatches OnQuestStarted itself. The OnQuestStarted-only
                // shortcut leaves event subscriptions dead until the next reload,
                // which is exactly the 07-03 "killed him but quest didn't update" bug.
                quest.StartQuest();
                quest.InitJournalEntries();
                quest.EnsureTargetTracked();

                Log("started " + questId + " target=" + target.StringId
                    + " troops=" + target.MemberRoster.TotalManCount
                    + " seed=" + seed.StringId);
                return quest;
            }
            catch (Exception ex)
            {
                Log("[ERR] Begin " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // Journal opens with a proper story block (07-03 feedback: one bare
        // objective line read thin) — the chief's authored offer speech, quoted,
        // then the crisp objective as its own entry. The journal renders plain
        // text only, so structure carries the flavor, not markup.
        public void InitJournalEntries()
        {
            var spec = Spec;
            if (spec == null) return;
            try
            {
                if (!string.IsNullOrEmpty(spec.OfferProse))
                {
                    string chiefName = QuestGiver != null ? QuestGiver.Name.ToString() : null;
                    if (chiefName == null)
                    {
                        CultureProfile prof;
                        chiefName = CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out prof)
                            ? prof.ChiefName : BandItPlus.Localization.Get("bp_signaturequest_002", "The chief");
                    }
                    AddLog(new TextObject("{=bp_hcp_004}{CHIEF}, at the fire:\n\n\"{PROSE}\"")
                        .SetTextVariable("CHIEF", chiefName)
                        .SetTextVariable("PROSE", spec.OfferProse));
                }
            }
            catch (Exception ex)
            {
                Log("InitJournalEntries story block " + ex.GetType().Name + ": " + ex.Message);
            }
            if (!string.IsNullOrEmpty(spec.JournalObjectiveText))
                AddLog(new TextObject(spec.JournalObjectiveText));
        }

        private void EnsureTargetTracked()
        {
            try
            {
                if (IsOngoing && _stage == "travel" && _targetParty != null && _targetParty.IsActive)
                    AddTrackedObject(_targetParty);
            }
            catch (Exception ex)
            {
                Log("EnsureTargetTracked " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Target party spawn (mirrors BanditAllianceQuest.SpawnCh2TargetParty)
        // ------------------------------------------------------------------

        // Seed priority (07-03 NRE fix): the accept happens AT a chief camp in the
        // common case, so prefer the settlement the player is standing in, then the
        // chief's own home, then an infested (live) hideout — never a dormant slot
        // picked blind off Settlement.All. Dormant hideouts also put the target on
        // the wrong side of the map.
        private static Settlement ResolveSeedHideout(string cultureId, Hero chief)
        {
            try
            {
                Settlement here = Settlement.CurrentSettlement;
                if (IsCultureHideout(here, cultureId)) { Log("seed=current settlement " + here.StringId); return here; }
                Settlement home = chief != null ? chief.HomeSettlement : null;
                if (IsCultureHideout(home, cultureId)) { Log("seed=chief home " + home.StringId); return home; }

                Settlement any = null;
                foreach (Settlement s in Settlement.All)
                {
                    if (!IsCultureHideout(s, cultureId)) continue;
                    if (any == null) any = s;
                    bool infested = false;
                    try { infested = s.Hideout.IsInfested; } catch { }
                    if (infested) { Log("seed=infested hideout " + s.StringId); return s; }
                }
                if (any != null) Log("seed=fallback dormant hideout " + any.StringId);
                return any;
            }
            catch (Exception ex)
            {
                Log("ResolveSeedHideout " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // 07-03 root cause: WarPartyComponent.OnInitialize runs Clan.OnWarPartyAdded —
        // a null clan is a guaranteed NRE in 1.4.5+. Vanilla always resolves the
        // bandit clan from Clan.BanditFactions; so do we.
        public static Clan ResolveBanditClan(string cultureId)
        {
            try
            {
                foreach (Clan c in Clan.BanditFactions)
                    if (c != null && c.Culture != null && c.Culture.StringId == cultureId) return c;
                foreach (Clan c in Clan.All)
                    if (c != null && c.IsBanditFaction && c.Culture != null && c.Culture.StringId == cultureId) return c;
            }
            catch (Exception ex) { Log("ResolveBanditClan " + ex.GetType().Name + ": " + ex.Message); }
            return null;
        }

        private static bool IsCultureHideout(Settlement s, string cultureId)
        {
            try
            {
                if (s == null || !s.IsHideout || s.Hideout == null) return false;
                string cid = null;
                try { cid = BanditDialogManager.GetCultureIdForHideout(s); } catch { }
                return cid == cultureId;
            }
            catch { return false; }
        }

        private static MobileParty SpawnTargetParty(SignatureQuestSpec spec, string cultureId, Settlement seed)
        {
            try
            {
                // Spawn OUT ON THE ROADS, not at the camp doorstep (07-03 feedback:
                // a khan's column parked at the hideout entrance breaks the fiction).
                // Try random directions at 9 / 6 / 3 map units, terrain-validated;
                // the doorstep stays as last-resort fallback. The quest tracker
                // means the player never loses the party regardless.
                Vec2 anchor = seed.GetPosition2D;
                CampaignVec2 spawnPos = new CampaignVec2(anchor + new Vec2(0.5f, 0.5f), /* isOnLand: */ true);
                bool valid = false;
                float[] distances = { 9f, 6f, 3f };
                for (int d = 0; d < distances.Length && !valid; d++)
                {
                    float baseAngle = MBRandom.RandomFloat * 6.2832f;
                    for (int a = 0; a < 6 && !valid; a++)
                    {
                        float ang = baseAngle + a * 1.0472f;   // 6 spokes, 60 degrees apart
                        Vec2 cand2 = anchor + new Vec2(
                            (float)Math.Cos(ang) * distances[d],
                            (float)Math.Sin(ang) * distances[d]);
                        CampaignVec2 cand = new CampaignVec2(cand2, true);
                        try
                        {
                            var face = Campaign.Current.MapSceneWrapper.GetFaceIndex(cand);
                            if (face.IsValid()) { spawnPos = cand; valid = true; }
                        }
                        catch { }
                    }
                }
                if (!valid)
                {
                    try
                    {
                        var face = Campaign.Current.MapSceneWrapper.GetFaceIndex(spawnPos);
                        valid = face.IsValid();
                    }
                    catch { }
                    if (!valid) spawnPos = new CampaignVec2(anchor, true);
                    Log("road spawn found no valid face — falling back to camp doorstep");
                }

                CultureObject culture = seed.Culture;
                PartyTemplateObject template = null;
                if (culture != null)
                    template = culture.BanditBossPartyTemplate ?? culture.DefaultPartyTemplate;
                if (template == null)
                    template = MBObjectManager.Instance.GetObject<PartyTemplateObject>("looters_template");
                if (template == null)
                {
                    Log("[ERR] SpawnTargetParty: no template for " + cultureId);
                    return null;
                }

                Clan banditClan = ResolveBanditClan(cultureId);
                if (banditClan == null)
                {
                    Log("[ERR] SpawnTargetParty: no bandit clan for culture " + cultureId);
                    return null;
                }
                string partyId = "bp_sig_target_" + cultureId + "_" + ((int)CampaignTime.Now.ToHours);
                Log("spawn: seed=" + seed.StringId + " pos=(" + spawnPos.X.ToString("F1") + ","
                    + spawnPos.Y.ToString("F1") + ") valid=" + valid + " template=" + template.StringId
                    + " clan=" + banditClan.StringId);
                MobileParty party;
                try
                {
                    party = BanditPartyComponent.CreateBanditParty(
                        partyId, banditClan, seed.Hideout, /* isBossParty: */ false,
                        template, spawnPos);
                }
                catch (Exception cex)
                {
                    string top = cex.StackTrace != null
                        ? cex.StackTrace.Split('\n')[0].Trim() : "no stack";
                    Log("[ERR] CreateBanditParty threw " + cex.GetType().Name + ": " + cex.Message + " @ " + top);
                    return null;
                }
                if (party == null)
                {
                    Log("[ERR] SpawnTargetParty: CreateBanditParty returned null");
                    return null;
                }

                try
                {
                    party.Party.SetCustomName(new TextObject(
                        string.IsNullOrEmpty(spec.TargetPartyName) ? "{=bp_signaturequest_003}Marked Column" : spec.TargetPartyName));
                }
                catch (Exception nex)
                {
                    Log("SetCustomName " + nex.GetType().Name + ": " + nex.Message);
                }

                // Size to ~0.7x the player's healthy count, floor 20, cap 45. The
                // 07-03 field test proved 0.9x makes the strength-demand (1.2x)
                // near-impossible: both numbers keyed to the same scale. At 0.7x a
                // healthy party passes the demand (~1.4x) and a battered one gets
                // laughed at — the intended drama.
                try
                {
                    int desired = 25;
                    if (MobileParty.MainParty != null)
                        desired = (int)(MobileParty.MainParty.MemberRoster.TotalHealthyCount * 0.7f);
                    if (desired < 20) desired = 20;
                    if (desired > 45) desired = 45;
                    int guard = 0;
                    while (party.MemberRoster.TotalManCount < desired && guard < 200)
                    {
                        guard++;
                        foreach (var stack in template.Stacks)
                        {
                            if (party.MemberRoster.TotalManCount >= desired) break;
                            if (stack.Character != null)   // PartyTemplateStack is a struct
                                party.MemberRoster.AddToCounts(stack.Character, 1);
                        }
                    }
                }
                catch (Exception sex)
                {
                    Log("size top-up " + sex.GetType().Name + ": " + sex.Message + " (default size kept)");
                }

                try
                {
                    if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(true);
                    party.SetMoveModeHold();
                }
                catch (Exception aex)
                {
                    Log("Ai/hold setup " + aex.GetType().Name + ": " + aex.Message);
                }

                return party;
            }
            catch (Exception ex)
            {
                string top = ex.StackTrace != null ? ex.StackTrace.Split('\n')[0].Trim() : "no stack";
                Log("[ERR] SpawnTargetParty " + ex.GetType().Name + ": " + ex.Message + " @ " + top);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Resolution (called from BanditDialogManager consequences)
        // ------------------------------------------------------------------

        // Peaceful comply (toll paid, standard yielded/bought...). Gold lands now;
        // the target despawns once the encounter closes; the chief waits.
        public void MarkResolvedPeaceful(string outcome, int goldToPlayer)
        {
            try
            {
                _outcome = outcome;
                _stage = "return";
                if (goldToPlayer > 0)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, goldToPlayer, false);
                _pendingDespawn = true;
                var spec = Spec;
                if (spec != null && !string.IsNullOrEmpty(spec.JournalReturnText))
                    AddLog(new TextObject(spec.JournalReturnText));
                Log("resolved peacefully outcome=" + outcome + " gold=" + goldToPlayer);
            }
            catch (Exception ex)
            {
                Log("[ERR] MarkResolvedPeaceful " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Bribe taken (Khalid). Quest fails once the encounter closes and the
        // target despawns — completing mid-encounter with the party alive risks
        // finalize destroying the party under the encounter's feet.
        public void MarkBetrayed(int bribeGold)
        {
            try
            {
                _outcome = "betrayed";
                _stage = "betrayed";
                if (bribeGold > 0)
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, bribeGold, false);
                _pendingDespawn = true;
                Log("betrayed: bribe=" + bribeGold + " — fail-out deferred to encounter close");
            }
            catch (Exception ex)
            {
                Log("[ERR] MarkBetrayed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Chief's closing beat done — pay nothing here (manager pays rewards, it
        // owns the spec + chief); just close the journal cleanly.
        public void ConcludeHonored(string closingLog)
        {
            try
            {
                _stage = "done-honored";
                if (!string.IsNullOrEmpty(closingLog)) AddLog(new TextObject(closingLog));
                CompleteQuestWithSuccess();
                Log("concluded honored, outcome=" + _outcome + " flavor=" + _flavor);
            }
            catch (Exception ex)
            {
                Log("[ERR] ConcludeHonored " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        private void OnMobilePartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            try
            {
                if (party == null || party != _targetParty || !IsOngoing) return;
                if (_stage != "travel")
                {
                    // Our own deferred despawn (peaceful/betrayed paths) — not a kill.
                    _targetParty = null;
                    return;
                }

                bool playerKill = false;
                try
                {
                    playerKill =
                        (destroyer != null && destroyer == PartyBase.MainParty)
                        || (destroyer != null && destroyer.MobileParty == MobileParty.MainParty)
                        || (party.Party != null && party.Party.MapEvent != null
                            && party.Party.MapEvent == MapEvent.PlayerMapEvent);
                }
                catch { }

                var spec = Spec;
                _targetParty = null;
                _stage = "return";
                if (playerKill)
                {
                    _outcome = "battle";
                    int loot = spec != null ? spec.BattleLootGold : 0;
                    if (loot > 0)
                    {
                        GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, loot, false);
                        InformationManager.DisplayMessage(new InformationMessage(
                            new TextObject("{=bp_hcp_005}Recovered {LOOT} denars from the wreck.")
                                .SetTextVariable("LOOT", loot).ToString(), Colors.Yellow));
                    }
                    if (spec != null && !string.IsNullOrEmpty(spec.JournalReturnText))
                        AddLog(new TextObject(spec.JournalReturnText));
                    Log("target destroyed by player -> battle outcome, loot=" + (spec != null ? spec.BattleLootGold : 0));
                }
                else
                {
                    _outcome = "othercause";
                    AddLog(new TextObject("{=bp_signaturequest_004}The road finished the work before you reached it. The chief will still want the telling."));
                    Log("target destroyed by third party -> othercause outcome");
                }
            }
            catch (Exception ex)
            {
                Log("[ERR] OnMobilePartyDestroyed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Catches the beaten-but-not-wiped case: the player wins the field battle
        // but survivors keep the party alive, so MobilePartyDestroyed never fires.
        // A won player battle against the target counts — the remnant despawns.
        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (mapEvent == null || _stage != "travel" || !IsOngoing || _targetParty == null) return;
                if (!mapEvent.IsPlayerMapEvent) return;
                bool involved = false;
                try
                {
                    foreach (var p in mapEvent.InvolvedParties)
                        if (p == _targetParty.Party) { involved = true; break; }
                }
                catch { }
                if (!involved) return;
                bool playerWon = false;
                try { playerWon = mapEvent.WinningSide == mapEvent.PlayerSide; } catch { }
                if (!playerWon) return;

                var spec = Spec;
                _stage = "return";
                _outcome = "battle";
                _pendingDespawn = true;   // remnant cleaned up once the encounter closes
                int loot = spec != null ? spec.BattleLootGold : 0;
                if (loot > 0)
                {
                    GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, loot, false);
                    InformationManager.DisplayMessage(new InformationMessage(
                        new TextObject("{=bp_hcp_005}Recovered {LOOT} denars from the wreck.")
                            .SetTextVariable("LOOT", loot).ToString(), Colors.Yellow));
                }
                if (spec != null && !string.IsNullOrEmpty(spec.JournalReturnText))
                    AddLog(new TextObject(spec.JournalReturnText));
                Log("player won map event vs target (survivors="
                    + (_targetParty.MemberRoster != null ? _targetParty.MemberRoster.TotalManCount : 0)
                    + ") -> battle outcome");
            }
            catch (Exception ex)
            {
                Log("[ERR] OnMapEventEnded " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnTick(float dt)
        {
            if (!_pendingDespawn) return;
            try
            {
                // Never destroy the party while the player still stands in its
                // encounter — wait for the map to come back.
                if (PlayerEncounter.Current != null
                    && PlayerEncounter.EncounteredMobileParty != null
                    && PlayerEncounter.EncounteredMobileParty == _targetParty)
                    return;

                _pendingDespawn = false;
                if (_targetParty != null && _targetParty.IsActive)
                {
                    DestroyPartyAction.Apply(null, _targetParty);
                    Log("target party despawned (deferred)");
                }
                _targetParty = null;

                if (_stage == "betrayed" && IsOngoing)
                {
                    _stage = "done-betrayed";
                    AddLog(new TextObject("{=bp_signaturequest_005}You took their coin and told the chief nothing. Some ledgers never close."));
                    CompleteQuestWithFail();
                    Log("concluded betrayed (fail-out after encounter close)");
                }
            }
            catch (Exception ex)
            {
                Log("[ERR] OnTick despawn " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void OnFinalize()
        {
            try
            {
                // Leak-proof every exit: success, betrayal, journal abandon, cancel.
                if (_targetParty != null && _targetParty.IsActive)
                {
                    DestroyPartyAction.Apply(null, _targetParty);
                    Log("target party destroyed in OnFinalize");
                }
                _targetParty = null;

                // Abandon/cancel (stage never reached a "done-*" terminal): free the
                // once-per-campaign slot so the chief can offer the job again.
                if (_stage != null && !_stage.StartsWith("done"))
                {
                    var mgr = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                    if (mgr != null) mgr.SetSignatureState(_cultureId, 0);
                    Log("finalized without conclusion (stage=" + _stage + ") — signature slot reopened");
                }
            }
            catch (Exception ex)
            {
                Log("[ERR] OnFinalize " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Sig] " + msg); }
            catch { }
        }
    }
}
