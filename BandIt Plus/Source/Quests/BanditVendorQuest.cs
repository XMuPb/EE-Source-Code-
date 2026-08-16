using System;
using System.Reflection;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.LogEntries;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace BandItPlus.Quests
{
    // Wave 4.11-fix v52 (2026-05-08): vanilla journal integration for vendor quests.
    // Mirrors BanditTrustQuest's shape but tracks a per-vendor-type-per-culture quest
    // instead of a per-culture chief quest.
    //
    // QuestGiver = the bandit clan's leader Hero (chief). Vendors aren't Heroes
    // (they're random NPCs from the charPool), but QuestBase requires a Hero giver,
    // so we use the chief as a stand-in. The Title + journal entries reference the
    // VENDOR's authored name (from VendorProfiles) so the player sees "Old Brae's
    // order" or similar.
    //
    // Lifecycle (matches BanditTrustQuest exactly):
    //   - Created in HideoutVendorDialog.AcceptFood/GearVendorQuest, registered via
    //     QuestManager.AddQuest (called by StartQuest internally)
    //   - HourlyTick refreshes progress bars from MobileParty.MainParty.ItemRoster
    //   - OnCompleteWithSuccess (triggered by FinishWithSuccess) calls
    //     ForceCompleteJournalProgress so the bar archives at N/N
    //
    // Saveable type ID = 3 (registered in BandItPlusSaveableTypeDefiner alongside
    // WalkableHideoutEncounter=1 and BanditTrustQuest=2).
    public class BanditVendorQuest : QuestBase
    {
        [SaveableField(1)]
        private string _cultureId;

        [SaveableField(2)]
        private int _vendorTier;

        // "food" or "gear" — selects which vendor profile + quest-spec lookup to use.
        [SaveableField(3)]
        private string _vendorKind;

        // Idempotent guard so we don't double-subscribe when DoRegisterEvents is called
        // both by RegisterEvents (if the contract works) AND by InitializeQuestOnGameLoad
        // / InitJournalEntries (belt-and-suspenders). Runtime-only — not [SaveableField].
        private bool _eventsSubscribed;

        public string CultureId => _cultureId;
        public int VendorTier => _vendorTier;
        public string VendorKind => _vendorKind;

        // Public wrapper for the protected CompleteQuestWithSuccess so the dialog
        // consequence (DeliverFoodVendorQuest / DeliverGearVendorQuest) can finalize.
        public void FinishWithSuccess() => CompleteQuestWithSuccess();

        public BanditVendorQuest(string questId, Hero questGiver, CampaignTime duration,
            int rewardGold, string cultureId, int vendorTier, string vendorKind)
            : base(questId, questGiver, duration, rewardGold)
        {
            _cultureId = cultureId;
            _vendorTier = vendorTier;
            _vendorKind = vendorKind;
            SuppressQuestGiverPortrait();
        }

        // Wave 4.11-fix v53 (2026-05-08): null out the inherited _questGiver field via
        // reflection AFTER QuestBase ctor finishes. Vendors aren't Heroes, so showing the
        // chief's portrait in the "Quest Given By" panel is misleading. The constructor
        // requires a non-null Hero (engine null-guards there), so we pass the chief to
        // satisfy construction THEN null the field. Bannerlord's quest UI null-checks
        // QuestGiver before rendering the portrait — null = panel suppressed.
        // Called from ctor + InitializeQuestOnGameLoad + InitJournalEntries because
        // save/load restores the field; nulling at every hook keeps it suppressed.
        private void SuppressQuestGiverPortrait()
        {
            try
            {
                var f = typeof(QuestBase).GetField("_questGiver",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                f?.SetValue(this, null);
            }
            catch { /* defensive — if engine changed the field name, fall back to chief portrait */ }
        }

        public override TextObject Title
        {
            get
            {
                string vendorName = ResolveVendorName();
                return new TextObject("{=bp_hcp_001}Bring supplies to {VENDOR} (Tier {TIER})")
                    .SetTextVariable("VENDOR", vendorName)
                    .SetTextVariable("TIER", _vendorTier);
            }
        }

        public override bool IsRemainingTimeHidden => true;

        // v51 lesson — IsSpecialQuest reads `!string.IsNullOrEmpty(SpecialQuestType)`.
        // We INTENTIONALLY do NOT override SpecialQuestType here. Vendor quests are
        // side errands, so blue/issue-style icons are correct. This matches the blue
        // "!" NPC nameplate marker that vendors get (v45). Chief quests use gold (v51).

        protected override void RegisterEvents()
        {
            // 2026-06-04: vanilla QuestBase calls this on quest creation AND on game load,
            // but the contract is unreliable for some quest shapes — so DoRegisterEvents is
            // also called from InitializeQuestOnGameLoad + InitJournalEntries as a
            // belt-and-suspenders measure. The internal helper guards against
            // double-registration via _eventsSubscribed.
            DoRegisterEvents("RegisterEvents");
        }

        private void DoRegisterEvents(string caller)
        {
            int step = 0;
            try
            {
                if (_eventsSubscribed)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditVendorQuest.DoRegisterEvents(" + caller + "): already subscribed, skipping");
                    return;
                }
                step = 1; CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, UpdateProgress);
                step = 2; CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                step = 3; CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
                step = 99;
                _eventsSubscribed = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditVendorQuest.DoRegisterEvents(" + caller + "): ALL 3 subscribed OK ("
                    + _cultureId + " " + _vendorKind + " T" + _vendorTier + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditVendorQuest.DoRegisterEvents(" + caller + ") FAIL at step=" + step
                    + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try { UpdateProgress(); } catch { }
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try { UpdateProgress(); } catch { }
        }

        // Canonical hourly-tick override — engine-driven, mirrors BanditTrustQuest's path.
        protected override void HourlyTick()
        {
            try { UpdateProgress(); } catch { }
        }

        // Iterates the vendor quest's item spec, finds matching JournalLog entries
        // (skipping description-only entries via Range>0 filter, same v46 fix as
        // BanditTrustQuest), updates each entry's CurrentProgress from the player's
        // roster.
        public void UpdateProgress()
        {
            try
            {
                var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec(_vendorKind, _vendorTier);
                if (spec == null || spec.Items == null) return;
                var entries = JournalEntries;
                if (entries == null) return;

                int idx = 0;
                int updated = 0;
                int firstHave = -1, firstTarget = -1;
                string firstId = null;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.Range <= 0) continue;       // skip description logs
                    if (idx >= spec.Items.Length) break;
                    var (itemId, count) = spec.Items[idx];
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                    if (item != null)
                    {
                        int have = MobileParty.MainParty?.ItemRoster?.GetItemNumber(item) ?? 0;
                        entry.UpdateCurrentProgress(Math.Min(have, count));
                        if (idx == 0) { firstHave = have; firstTarget = count; firstId = itemId; }
                        updated++;
                    }
                    idx++;
                }

                // (Throttled diagnostic log removed in v60 cleanup.)
                _ = updated; _ = firstHave; _ = firstTarget; _ = firstId;
            }
            catch { }
        }

        // Force-set discrete entries to N/N at completion time, bypassing inventory read
        // (DeliverFood/GearVendorQuest deducts items BEFORE this fires, so a roster read
        // would return 0). Same v49 lesson as BanditTrustQuest.
        public void ForceCompleteJournalProgress()
        {
            try
            {
                var entries = JournalEntries;
                if (entries == null) return;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.Range <= 0) continue;
                    entry.UpdateCurrentProgress(entry.Range);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditVendorQuest.ForceCompleteJournalProgress: " + _cultureId
                    + " " + _vendorKind + " T" + _vendorTier);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditVendorQuest.ForceCompleteJournalProgress fail: " + ex.Message);
            }
        }

        protected override void OnCompleteWithSuccess()
        {
            try { ForceCompleteJournalProgress(); } catch { }
        }

        protected override void OnFinalize() { }
        protected override void OnTimedOut() { }
        protected override void InitializeQuestOnGameLoad()
        {
            // v53: re-null _questGiver after save-load restores the chief Hero into it.
            SuppressQuestGiverPortrait();
            DoRegisterEvents("InitializeQuestOnGameLoad");
        }
        protected override void SetDialogs() { /* dialog tokens registered in HideoutVendorDialog */ }

        // Public so callers (HideoutVendorDialog) can populate journal entries after
        // QuestManager.AddQuest registers the quest. Mirrors BanditTrustQuest pattern —
        // adding logs in the constructor pre-registration causes them to be lost.
        public void InitJournalEntries()
        {
            try
            {
                // v53: belt-and-braces — null portrait field before journal logs render,
                // in case OnQuestStarted resurrected it.
                SuppressQuestGiverPortrait();

                var spec = BandItPlus.BanditDialogManager.GetVendorQuestSpec(_vendorKind, _vendorTier);
                if (spec == null || spec.Items == null) return;

                string vendorName = ResolveVendorName();
                AddLog(new TextObject("{=bp_hcp_002}{VENDOR}: \"{DISPLAY}. Bring me what I asked, and the stall opens up to you.\"")
                    .SetTextVariable("VENDOR", vendorName)
                    .SetTextVariable("DISPLAY", spec.Display));

                foreach (var (itemId, count) in spec.Items)
                {
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(itemId);
                    if (item == null) continue;
                    int have = MobileParty.MainParty?.ItemRoster?.GetItemNumber(item) ?? 0;
                    if (have > count) have = count;
                    AddDiscreteLog(
                        new TextObject("{=bp_hcp_003}Bring {COUNT} {ITEM} to {VENDOR}")
                            .SetTextVariable("COUNT", count)
                            .SetTextVariable("ITEM", item.Name)
                            .SetTextVariable("VENDOR", vendorName),
                        item.Name,
                        have,
                        count);
                }

                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditVendorQuest.InitJournalEntries: " + _cultureId + " " + _vendorKind
                    + " T" + _vendorTier + " items=" + spec.Items.Length);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditVendorQuest.InitJournalEntries EXCEPTION: " + ex.Message);
            }
            DoRegisterEvents("InitJournalEntries");
        }

        // Pulls the vendor's authored name from VendorProfiles. Falls back to a generic
        // "Food vendor" / "Gear vendor" label if no profile is registered for the culture.
        private string ResolveVendorName()
        {
            try
            {
                var profile = _vendorKind == "food"
                    ? VendorProfiles.GetFood(_cultureId)
                    : VendorProfiles.GetGear(_cultureId);
                if (!string.IsNullOrEmpty(profile?.Name)) return profile.Name;
            }
            catch { }
            return _vendorKind == "food"
                ? BandItPlus.Localization.Get("bp_vendorquest_001", "the food vendor")
                : BandItPlus.Localization.Get("bp_vendorquest_002", "the gear vendor");
        }
    }
}
