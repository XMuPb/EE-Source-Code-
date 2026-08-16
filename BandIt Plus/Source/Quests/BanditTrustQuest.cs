using System;
using System.Collections.Generic;
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
    // Wave 4.7: vanilla journal integration for trust quests. When the chief offers
    // a trust quest and the player accepts, BanditDialogManager instantiates this
    // QuestBase subclass and registers it via Campaign.Current.QuestManager.AddQuest().
    // The Q-key journal then shows the quest with title, description, and reward.
    //
    // QuestGiver = the bandit clan's leader hero (hideout.OwnerClan.Leader). Falls back
    // to Hero.MainHero if the clan has no leader (rare, but defensive).
    //
    // The quest object is informational — actual completion logic lives in
    // BanditDialogManager.OnCompleteTrustQuest, which calls CompleteQuestWithSuccess()
    // on this object and clears the journal entry.
    public class BanditTrustQuest : QuestBase
    {
        [SaveableField(1)]
        private string _cultureId;

        [SaveableField(2)]
        private int _questTier;

        // Wave 4.7e: which variant from the tier-pool the chief offered the player.
        // Snapshotted at acceptance time so re-rolls in the dialog don't change the
        // accepted quest's items/quantities.
        [SaveableField(3)]
        private int _variantIdx;

        // Read-only accessors so BanditDialogManager can identify the quest among
        // all active quests in QuestManager.Quests when handover happens.
        public string CultureId => _cultureId;
        public int QuestTier => _questTier;
        public int VariantIdx => _variantIdx;

        // Public wrapper around protected QuestBase.CompleteQuestWithSuccess so the
        // dialog manager can finalize the journal entry on turn-in.
        public void FinishWithSuccess() => CompleteQuestWithSuccess();

        public BanditTrustQuest(string questId, Hero questGiver, CampaignTime duration,
            int rewardGold, string cultureId, int questTier, int variantIdx = 0)
            : base(questId, questGiver, duration, rewardGold)
        {
            _cultureId = cultureId;
            _questTier = questTier;
            _variantIdx = variantIdx;
        }

        public override TextObject Title
        {
            get
            {
                string cultureName = HumanizeCulture(_cultureId);
                return new TextObject("{=bp_hcp_009}Earn the trust of the {CULTURE} (Tier {TIER})")
                    .SetTextVariable("CULTURE", cultureName)
                    .SetTextVariable("TIER", _questTier);
            }
        }

        public override bool IsRemainingTimeHidden => true;

        protected override void RegisterEvents()
        {
            // 2026-06-04: vanilla QuestBase calls this on quest creation AND on game load.
            // Belt-and-suspenders: DoRegisterEvents is also called from InitJournalEntries
            // (after QuestManager.OnQuestStarted) and InitializeQuestOnGameLoad. The
            // internal _eventsSubscribed guard prevents double-registration.
            DoRegisterEvents("RegisterEvents");
        }

        // Idempotent guard so we don't double-subscribe when DoRegisterEvents is called
        // both by RegisterEvents (if the contract works) AND by InitJournalEntries /
        // InitializeQuestOnGameLoad (belt-and-suspenders).
        private bool _eventsSubscribed;

        private void DoRegisterEvents(string caller)
        {
            int step = 0;
            try
            {
                if (_eventsSubscribed)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditTrustQuest.DoRegisterEvents(" + caller + "): already subscribed, skipping");
                    return;
                }
                // Wave 4.7c: hourly tick refreshes the progress bars on each tracked item.
                step = 1; CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, UpdateProgress);
                // Wave 4.11-fix v42 (2026-05-08): hourly tick alone is insufficient — it doesn't
                // fire while the player is inside a hideout mission (campaign time freezes), so
                // the bar can show a stale value at the moment the player picks "I have your X".
                // Fix: also refresh after combat (loot picked up) and on settlement entry
                // (player returning to deliver). On-session-launch refresh handles stale saves.
                step = 2; CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
                step = 3; CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
                // (v47 startup-confirmation log removed in v60 cleanup. The DLL-cache
                // verification it provided is no longer load-bearing.)
                // (Settlement-enter listener intentionally omitted — entering a settlement
                // advances campaign hours, so the existing HourlyTickEvent fires naturally
                // on every settlement transition. Plus the OnCompleteTrustQuest pre-finish
                // refresh in BanditDialogManager guarantees a final bar value at completion.)
                step = 99;
                _eventsSubscribed = true;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest.DoRegisterEvents(" + caller + "): ALL 3 subscribed OK (culture='"
                    + (_cultureId ?? "<null>") + "' tier=" + _questTier + ")");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest.DoRegisterEvents(" + caller + ") FAIL at step=" + step
                    + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Wave 4.11-fix v42: bar refresh after combat (loot acquired).
        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try { UpdateProgress(); } catch { /* defensive */ }
        }

        // Wave 4.11-fix v42: bar refresh on save load — clears stale snapshots from the
        // previous play session so the player sees current values immediately.
        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try { UpdateProgress(); } catch { /* defensive */ }
        }

        // Updates each journal entry's progress bar based on the player's current
        // inventory / gold. Called hourly + when the player accepts the quest.
        // Wave 4.11-fix v46 (2026-05-08): root cause of "bar stuck at 0/N" — the
        // iteration assumed entries[idx] aligned with ItemIds[idx], but
        // InitJournalEntries adds a leading description log via AddLog(IntroText)
        // BEFORE any AddDiscreteLog, so entries[0] is non-discrete and the discrete
        // trackers start at entries[1]. The old loop walked the description, then
        // broke (idx=1 >= ItemIds.Length=1), and the actual tracker was never
        // touched. Fix: skip entries with TaskNumberLimit <= 0 (description logs
        // have no target; only discrete logs do). This correctly indexes whatever
        // mix of intro/discrete entries the quest happens to have.
        public void UpdateProgress()
        {
            try
            {
                var trustQuest = GetActiveTrustQuestData();
                if (trustQuest == null) return;
                var entries = JournalEntries;
                if (entries == null) return;

                if (trustQuest.IsGoldQuest)
                {
                    int goldNow = Math.Min(Hero.MainHero?.Gold ?? 0, trustQuest.GoldAmount);
                    foreach (var entry in entries)
                    {
                        if (entry == null) continue;
                        if (entry.Range <= 0) continue;       // skip description logs (Range=0)
                        entry.UpdateCurrentProgress(goldNow);
                    }
                    return;
                }

                if (trustQuest.ItemIds == null) return;
                int idx = 0;
                int updated = 0;
                int firstHave = -1;
                int firstTarget = -1;
                string firstId = null;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.Range <= 0) continue;       // skip description logs (Range=0)
                    if (idx >= trustQuest.ItemIds.Length) break;
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(trustQuest.ItemIds[idx]);
                    if (item != null)
                    {
                        int have = MobileParty.MainParty?.ItemRoster?.GetItemNumber(item) ?? 0;
                        int target = (idx < trustQuest.Counts.Length) ? trustQuest.Counts[idx] : 1;
                        entry.UpdateCurrentProgress(Math.Min(have, target));
                        if (idx == 0) { firstHave = have; firstTarget = target; firstId = item.StringId; }
                        updated++;
                    }
                    idx++;
                }

                // (v46 throttled diagnostic log removed in v60 cleanup. Bar update
                // now fires reliably via HourlyTick override + MapEventEnded listener.
                // Re-enable from v46 if a future regression needs evidence.)
                _ = updated; _ = firstHave; _ = firstTarget; _ = firstId;
            }
            catch { /* defensive */ }
        }

        private Cultures.TrustQuest GetActiveTrustQuestData()
        {
            if (string.IsNullOrEmpty(_cultureId)) return null;
            if (!CultureProfileRegistry.ByCultureId.TryGetValue(_cultureId, out var profile)) return null;
            if (profile == null) return null;
            // Wave 4.7e: prefer Pool[_variantIdx] for randomized quests; fall back to
            // legacy singleton for cultures not yet migrated to multi-variant pools.
            Cultures.TrustQuest[] pool = _questTier == 1 ? profile.TrustQuestTier1Pool
                                       : _questTier == 2 ? profile.TrustQuestTier2Pool
                                       : _questTier == 3 ? profile.TrustQuestTier3Pool
                                       : null;
            if (pool != null && pool.Length > 0)
            {
                int idx = _variantIdx;
                if (idx < 0 || idx >= pool.Length) idx = 0;
                return pool[idx];
            }
            return _questTier == 1 ? profile.TrustQuestTier1
                 : _questTier == 2 ? profile.TrustQuestTier2
                 : null;
        }

        // Wave 4.11-fix v49 (2026-05-08): force the journal bar to show N/N at completion
        // regardless of current inventory state. Called from BanditDialogManager.OnCompleteTrustQuest
        // immediately before FinishWithSuccess, AFTER DeductActiveQuestPayment has removed
        // the items from inventory. UpdateProgress() can't be used here — it reads the
        // post-deduction roster (now zero) and would freeze the bar at 0/N. This sets
        // CurrentProgress = Range directly on each discrete entry, so the bar archives
        // showing the completed state.
        public void ForceCompleteJournalProgress()
        {
            try
            {
                var entries = JournalEntries;
                if (entries == null) return;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.Range <= 0) continue;       // skip description logs
                    entry.UpdateCurrentProgress(entry.Range);
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest.ForceCompleteJournalProgress: " + _cultureId
                    + " T" + _questTier + " — discrete entries set to N/N");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest.ForceCompleteJournalProgress fail: " + ex.Message);
            }
        }

        // Wave 4.11-fix v48 (2026-05-08): canonical hourly-tick override. QuestBase's
        // framework calls HourlyTick() automatically for every active quest each campaign
        // hour — this is the documented entry point per BUTR's QuestBase API page. The
        // CampaignEvents.HourlyTickEvent subscription in RegisterEvents was a parallel
        // path that may not have been firing for QuestBase-derived classes. This override
        // is belt-and-braces with that subscription: whichever fires first refreshes the
        // bar; throttling in UpdateProgress prevents log spam either way.
        protected override void HourlyTick()
        {
            try { UpdateProgress(); } catch { /* defensive */ }
        }

        // Wave 4.11-fix v51 (2026-05-08): mark this as a "special" quest so Bannerlord
        // renders its journal sidebar icon as GOLD (story-quest style) instead of BLUE
        // (issue-quest default). The IsSpecialQuest getter is NOT virtual — it's
        // implemented as `IsSpecialQuest => !string.IsNullOrEmpty(SpecialQuestType)`
        // (verified by decompiling the getter IL). To flip it, override the underlying
        // SpecialQuestType (which IS virtual) and return any non-empty string. The
        // string value is also used as a category tag in some UI surfaces.
        public override string SpecialQuestType => "BanditTrustQuest";

        // Wave 4.11-fix v50 (2026-05-08): canonical OnCompleteWithSuccess override per
        // TaleWorlds' QuestBase lifecycle pattern. Engine calls this any time
        // CompleteQuestWithSuccess() fires, regardless of trigger source — ensures the
        // journal bar is finalized at N/N even if completion is initiated by a code path
        // other than BanditDialogManager.OnCompleteTrustQuest (e.g. a future scripted
        // completion, time-based completion, or alternate dialog branch).
        protected override void OnCompleteWithSuccess()
        {
            try { ForceCompleteJournalProgress(); } catch { /* defensive */ }
        }

        protected override void OnFinalize() { /* nothing to clean up */ }

        protected override void OnTimedOut() { /* won't fire — IsRemainingTimeHidden=true */ }

        protected override void InitializeQuestOnGameLoad()
        {
            // logs persist via base class; no rebuild needed.
            // Belt-and-suspenders: re-attempt event subscription on game load in case
            // the engine's RegisterEvents contract didn't fire post-deserialization.
            DoRegisterEvents("InitializeQuestOnGameLoad");
        }

        protected override void SetDialogs() { /* chief's existing dialog tokens drive the conversation */ }

        // Wave 4.7c: must be called by BanditDialogManager AFTER QuestManager.OnQuestStarted
        // registration. Adding logs in the constructor (pre-registration) caused them to
        // be lost. Public so the dialog manager can call it explicitly.
        public void InitJournalEntries()
        {
            try
            {
                var trustQuest = GetActiveTrustQuestData();
                if (trustQuest == null)
                {
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditTrustQuest.InitJournalEntries: trustQuest data null for " + _cultureId + " tier " + _questTier);
                    return;
                }
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest.InitJournalEntries: adding logs for " + _cultureId + " tier " + _questTier
                    + " (gold=" + trustQuest.IsGoldQuest + ", items=" + (trustQuest.ItemIds?.Length ?? 0) + ")");

                // Description log (text only — no progress bar).
                AddLog(new TextObject(trustQuest.IntroText ?? "{=bp_trustquest_001}The chief expects something from you."));

                if (trustQuest.IsGoldQuest)
                {
                    int now = Math.Min(Hero.MainHero?.Gold ?? 0, trustQuest.GoldAmount);
                    AddDiscreteLog(
                        new TextObject("{=bp_hcp_010}Bring {AMOUNT} denars to the chief")
                            .SetTextVariable("AMOUNT", trustQuest.GoldAmount),
                        new TextObject("{=bp_trustquest_002}Denars"),
                        now,
                        trustQuest.GoldAmount);
                    return;
                }

                if (trustQuest.ItemIds == null || trustQuest.Counts == null) return;
                for (int i = 0; i < trustQuest.ItemIds.Length; i++)
                {
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(trustQuest.ItemIds[i]);
                    if (item == null)
                    {
                        // Wave 4.7d: id-mismatch diagnostic. Dump every ItemObject whose
                        // StringId contains tokens from the missing id so we can see the
                        // real vanilla naming convention (e.g. "iron_ore" vs "ore" vs "iron_ingot1").
                        try
                        {
                            string needle = trustQuest.ItemIds[i] ?? string.Empty;
                            var allItems = MBObjectManager.Instance.GetObjectTypeList<ItemObject>();
                            int matchCount = 0;
                            var sb = new System.Text.StringBuilder();
                            foreach (var io in allItems)
                            {
                                if (io == null || string.IsNullOrEmpty(io.StringId)) continue;
                                bool match = false;
                                foreach (var token in needle.Split('_'))
                                {
                                    if (token.Length >= 3 && io.StringId.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                    { match = true; break; }
                                }
                                if (!match) continue;
                                if (matchCount++ > 25) break;
                                sb.Append(io.StringId).Append(", ");
                            }
                            BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                                "BanditTrustQuest: item lookup FAILED for id='" + needle
                                + "' — candidates [" + sb.ToString().TrimEnd(',', ' ') + "]");
                        }
                        catch { }
                        continue;
                    }
                    int target = (i < trustQuest.Counts.Length) ? trustQuest.Counts[i] : 1;
                    int have = MobileParty.MainParty?.ItemRoster?.GetItemNumber(item) ?? 0;
                    if (have > target) have = target;
                    BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                        "BanditTrustQuest: AddDiscreteLog item='" + item.StringId + "' have=" + have + "/" + target);
                    AddDiscreteLog(
                        new TextObject("{=bp_hcp_011}Bring {COUNT} {ITEM} to the chief")
                            .SetTextVariable("COUNT", target)
                            .SetTextVariable("ITEM", item.Name),
                        item.Name,
                        have,
                        target);
                }
                int entryCount = JournalEntries?.Count ?? -1;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest: post-init JournalEntries.Count=" + entryCount);
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "BanditTrustQuest.InitJournalEntries EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
            // Belt-and-suspenders: trigger event subscription here too. Called by
            // BanditDialogManager AFTER QuestManager.OnQuestStarted, which is the
            // safest moment to guarantee subscription. The idempotent guard inside
            // DoRegisterEvents skips if RegisterEvents already fired.
            DoRegisterEvents("InitJournalEntries");
        }

        // "bp_frost_reavers" -> "Frost Reavers". Mirrors the helper in BanditDialogManager
        // but kept local so this file is self-contained.
        private static string HumanizeCulture(string cultureId)
        {
            if (string.IsNullOrEmpty(cultureId)) return BandItPlus.Localization.Get("bp_trustquest_003", "Bandits");
            string id = cultureId.StartsWith("bp_") ? cultureId.Substring(3) : cultureId;
            var parts = id.Split('_');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) sb.Append(parts[i].Substring(1));
            }
            return sb.Length > 0 ? sb.ToString() : BandItPlus.Localization.Get("bp_trustquest_003", "Bandits");
        }
    }
}
