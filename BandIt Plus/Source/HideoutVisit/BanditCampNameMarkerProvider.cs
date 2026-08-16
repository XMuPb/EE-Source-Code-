using System;
using System.Collections.Generic;
using SandBox.ViewModelCollection.Missions.NameMarker;
using SandBox.ViewModelCollection.Missions.NameMarker.Targets;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;

namespace BandItPlus.HideoutVisit
{
    // Native vanilla Alt-nameplate provider for the bandit camp.
    //
    // Plugs into SandBox's MissionNameMarkerFactory context stack. When the village mission
    // (used by our walkable hideout) instantiates MissionGauntletNameMarkerView, it queries
    // the factory for active providers and asks each one to populate marker target VMs.
    //
    // We populate one MissionAgentMarkerTargetVM per Boss/Vendor agent — the same VM type
    // vanilla uses for villager Notables. The base class does world-to-screen projection,
    // distance fade, and IsEnabled gating off the Alt key automatically. We only have to
    // hand it an Agent.
    //
    // Subclasses MissionNameMarkerProvider:
    //   - public override CreateMarkers(...) is required (abstract on base)
    //   - protected override OnInitialize/OnTick/OnDestroy are optional virtuals; the base
    //     handles the public Initialize/Destroy/Tick wrappers and lifecycle bookkeeping
    //
    // Lifecycle is driven by HideoutAltLabelBehavior pushing/popping a factory context.
    public class BanditCampNameMarkerProvider : MissionNameMarkerProvider
    {
        public override void CreateMarkers(List<MissionNameMarkerTargetBaseVM> markers)
        {
            try
            {
                var beh = Campaign.Current?.GetCampaignBehavior<HideoutVisitNpcSpawnBehavior>();
                if (beh == null) return;

                // BUG H9 FIX (2026-04-30): Snapshot agents to a local list BEFORE iterating.
                // GetLabelAgents() iterates a live `_npcRoles` dictionary on the spawn behavior.
                // If an agent dies, is removed, or `_npcRoles` is mutated mid-tick (e.g. by the
                // wander tick or a Harmony patch on AgentRemoved), the foreach throws
                // InvalidOperationException ("Collection was modified...") which propagates up
                // and kills the entire marker refresh. Snapshotting decouples our iteration
                // from the live dictionary's mutation timeline. Cheap (43 entries max).
                var snapshot = new List<Agent>(64);
                try
                {
                    foreach (var ag in beh.GetLabelAgents())
                    {
                        if (ag != null) snapshot.Add(ag);
                    }
                }
                catch (Exception snapEx)
                {
                    HideoutPeacefulVisitState.Log("BanditCampNameMarkerProvider: snapshot fail (will skip this refresh): " + snapEx.Message);
                    return;
                }

                // Wave 4.6.6: figure out if the chief has a quest available for the player
                // ONCE, so we can prefix his nameplate with a quest marker. The marker mirrors
                // vanilla's "quest-giver has new quest" indicator — gives the player a visual
                // cue for which NPC to talk to.
                bool chiefHasOfferableQuest = false;
                // Wave 4.11-fix v44 (2026-05-08): per-vendor quest markers. After the
                // chief's gate is open (chief_trust >= 1), each vendor independently
                // shows a "!" marker when they have something for the player —
                // either a next-tier quest to offer, or an outstanding quest waiting
                // for delivery. At vendor_trust >= 3 with no active quest, the marker
                // disappears (the relationship is fully built).
                bool foodVendorHasMark = false;
                bool gearVendorHasMark = false;
                // Wave 4.12 (2026-05-11): slaver marker + name override. Same shape as
                // food/gear; the cached slaverProfileName is also used to overwrite the
                // Alt-nameplate label since Agent.Name on the slaver was resolving to
                // the CharacterObject template name ("Forest Bandits Hero") instead of
                // the reflected _name override. Looking up SlaverProfiles directly here
                // is the most robust path — avoids depending on Agent.Name fallback order
                // staying stable between Bannerlord 1.2.x and 1.3.x.
                bool slaverHasMark = false;
                string slaverProfileName = null;
                try
                {
                    var bdm = Campaign.Current?.GetCampaignBehavior<BanditDialogManager>();
                    var hideout = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                    string cultureId = hideout?.Culture?.StringId
                        ?? hideout?.OwnerClan?.Culture?.StringId;
                    if (bdm != null && !string.IsNullOrEmpty(cultureId))
                    {
                        int trust = bdm.GetCultureTrust(cultureId);
                        // Chief: quest available when trust < 3 (3-tier ladder T1/T2/T3).
                        // Wave 4.6: original `trust < 2` matched a 2-tier ladder.
                        // Wave 4.11 added the T3 chief trust quest pool, but this check
                        // wasn't updated — caused the chief marker to disappear after T2
                        // completion even though a T3 quest was waiting to be offered.
                        // Wave 4.12-fix (2026-05-11): bumped to `trust < 3` to match the
                        // 3-tier ladder, aligning with the food/gear/slaver marker checks.
                        chiefHasOfferableQuest = trust < 3;

                        // Vendors: only after chief has accepted the player (trust >= 1).
                        // Marker fires when vendor can offer a NEW quest (vendor_trust < 3
                        // and no quest active) OR when there's an outstanding quest the
                        // player has accepted (any tier > 0). Both states give the player
                        // a reason to walk to that vendor.
                        if (trust >= 1)
                        {
                            int foodTrust = bdm.GetFoodVendorTrust(cultureId);
                            int foodQuest = bdm.GetFoodVendorQuestTier(cultureId);
                            foodVendorHasMark =
                                (foodTrust < 3 && foodQuest == 0)   // can offer next tier
                                || (foodQuest > 0);                  // quest in progress

                            int gearTrust = bdm.GetGearVendorTrust(cultureId);
                            int gearQuest = bdm.GetGearVendorQuestTier(cultureId);
                            // Wave 4.12-fix13 (2026-05-11): gear vendor marker hidden until
                            // food vendor Tier-2 is done — gear is inaccessible before that
                            // (per Wave 4.12-fix12 dialog gate), so marking him as
                            // "has work for you" is misleading.
                            int foodTrustForGearGate = bdm.GetFoodVendorTrust(cultureId);
                            if (foodTrustForGearGate < 2)
                            {
                                gearVendorHasMark = false;
                            }
                            else
                            {
                                gearVendorHasMark =
                                    (gearTrust < 3 && gearQuest == 0)
                                    || (gearQuest > 0);
                            }
                        }

                        // Slaver: only after chief Tier-3 (the slaver only spawns then,
                        // so checking trust>=3 is belt-and-braces). Same marker shape:
                        // mark when can-offer-next OR active-quest.
                        if (trust >= 3
                            && BandItPlus.Cultures.SlaverProfiles.ByCultureId.TryGetValue(cultureId, out var sProfile))
                        {
                            slaverProfileName = sProfile.Name;
                            int slaverTrust = bdm.GetSlaverTrust(cultureId);
                            int slaverQuest = bdm.GetActiveSlaverQuestTier(cultureId);
                            slaverHasMark =
                                (slaverTrust < 3 && slaverQuest == 0)
                                || (slaverQuest > 0);
                        }
                    }
                }
                catch { /* defensive — falls through to no marker */ }

                int added = 0;
                foreach (var ag in snapshot)
                {
                    if (ag == null) continue;
                    if (!ag.IsActive() || ag.Health <= 0f) continue;

                    var vm = new MissionAgentMarkerTargetVM(ag);

                    // Force the displayed name from agent.Name (which propagates from our
                    // reflected _name field set by AssignUniqueName, e.g. "Mountain Bandit
                    // Brigand I"). Some base implementations of GetName() consult character
                    // data we don't have for our hideout NPCs, so we override the public
                    // Name property after construction to guarantee the right text appears.
                    var nm = ag.Name?.ToString();

                    // Wave 4.12 (2026-05-11): slaver name override. The reflected _name
                    // write in HideoutVisitNpcSpawnBehavior is silently ignored on the
                    // slaver in 1.3.x — Agent.Name resolves to the CharacterObject template
                    // name ("Forest Bandits Hero") instead. Direct profile lookup here
                    // is robust regardless of Agent.Name property internals.
                    if (slaverProfileName != null && beh.IsAgentSlaverVendor(ag))
                    {
                        nm = slaverProfileName;
                    }

                    // Wave 4.6.7d: when the chief has an offerable quest, populate the
                    // Quests MBBindingList with a marker item so vanilla's gold
                    // GameMenu.QuestMarker brush paints over his head. NameMarker.xml binds
                    // <ListPanel DataSource="{Quests}"> with QuestMarkerBrushWidget per
                    // item — adding an item triggers rendering. Inner type discovered
                    // and item constructed via reflection.
                    if (beh.IsAgentBoss(ag) && chiefHasOfferableQuest)
                    {
                        // Wave 4.6.7 (final): steady gold "!" with brighter color via brush
                        // override (GUI/Brushes/BandItPlusQuestMarker.xml color_factor=1.5).
                        // No pulse — marker provider doesn't refresh fast enough to drive a
                        // visible flicker, and vanilla pulse needs a real StoryModeQuestBase.
                        try { vm.IsTracked = true; } catch { }
                        TryAddGoldenQuestMarker(vm);
                    }
                    // Wave 4.11-fix v44: vendor-quest markers — same brush as the chief, so
                    // the player can scan the camp on Alt-hold and immediately see which
                    // vendors have new quests to offer or quests-in-progress to deliver.
                    else if (beh.IsAgentFoodVendor(ag) && foodVendorHasMark)
                    {
                        try { vm.IsTracked = true; } catch { }
                        TryAddGoldenQuestMarker(vm, blueVariant: true);   // v45: blue "!"
                    }
                    else if (beh.IsAgentGearVendor(ag) && gearVendorHasMark)
                    {
                        try { vm.IsTracked = true; } catch { }
                        TryAddGoldenQuestMarker(vm, blueVariant: true);   // v45: blue "!"
                    }
                    // Wave 4.12 (2026-05-11): slaver quest marker. Blue "!" like food/gear
                    // vendors — signals to the player on Alt-hold that the slaver has
                    // either a quest to offer or an in-progress quest to deliver to.
                    else if (beh.IsAgentSlaverVendor(ag) && slaverHasMark)
                    {
                        try { vm.IsTracked = true; } catch { }
                        TryAddGoldenQuestMarker(vm, blueVariant: true);
                    }

                    if (!string.IsNullOrEmpty(nm))
                    {
                        // BUG M16 FIX (2026-04-30): log on Name-set failure instead of silently
                        // swallowing. If a future game patch makes `MissionAgentMarkerTargetVM.Name`
                        // read-only (computed in OnTick from agent data), the previous bare
                        // `catch { }` would silently empty all our nameplate names — players
                        // would see blank labels with no diagnostic. Now we get one log line per
                        // agent so the regression is immediately visible.
                        try { vm.Name = nm; }
                        catch (Exception nameEx)
                        {
                            HideoutPeacefulVisitState.Log("BanditCampNameMarkerProvider: vm.Name set failed for '" + nm + "': " + nameEx.Message);
                        }
                    }

                    markers.Add(vm);
                    added++;
                }
                HideoutPeacefulVisitState.Log("BanditCampNameMarkerProvider.CreateMarkers: added " + added + " markers");
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("BanditCampNameMarkerProvider.CreateMarkers EXCEPTION: " + ex.Message);
            }
        }

        // Wave 4.6.7d: populate the VM's Quests MBBindingList with one item so vanilla's
        // QuestMarkerBrushWidget renders the gold "!" sprite above the boss's nameplate.
        // Reflection-driven because the inner type isn't in our compile-time imports.
        private static bool _questsTypeLogged;
        private static bool _flagsLogged;
        // Wave 4.11-fix v45 (2026-05-08): blueVariant parameter switches the marker color.
        // Chief callers pass false (default) — keeps the gold "!" they've always had.
        // Vendor callers pass true — gets the BLUE "!" via Bannerlord's AvailableIssue
        // flag, which renders the icon_issue_* sprite from GameMenu.QuestMarker brush.
        // This visually distinguishes the chief's storyline-tier quest (gold = main arc)
        // from the vendor's per-tier supply quests (blue = side errand).
        private static void TryAddGoldenQuestMarker(MissionAgentMarkerTargetVM vm, bool blueVariant = false)
        {
            try
            {
                var t = vm.GetType();
                var questsProp = t.GetProperty("Quests");
                if (questsProp == null) return;
                var listObj = questsProp.GetValue(vm);
                if (listObj == null) {
                    HideoutPeacefulVisitState.Log("TryAddGoldenQuestMarker: Quests list null");
                    return;
                }
                var listType = questsProp.PropertyType;
                if (!listType.IsGenericType) return;
                var innerType = listType.GetGenericArguments()[0];

                if (!_questsTypeLogged)
                {
                    _questsTypeLogged = true;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("TryAddGoldenQuestMarker: inner=" + innerType.FullName);
                    foreach (var p in innerType.GetProperties(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance))
                    {
                        sb.Append("  inner P ").Append(p.PropertyType.Name).Append(' ').Append(p.Name);
                        sb.Append(" {get=").Append(p.CanRead).Append(" set=").Append(p.CanWrite).AppendLine("}");
                    }
                    foreach (var c in innerType.GetConstructors(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance))
                    {
                        sb.Append("  ctor params=").Append(c.GetParameters().Length).Append(": ");
                        foreach (var pp in c.GetParameters()) sb.Append(pp.ParameterType.Name).Append(' ');
                        sb.AppendLine();
                    }
                    HideoutPeacefulVisitState.Log(sb.ToString());
                }

                // Wave 4.6.7e: confirmed inner type is QuestMarkerVM with one constructor:
                // (IssueQuestFlags flag, TextObject title, TextObject hint). Build via
                // reflection because IssueQuestFlags isn't in our compile-time imports.
                object item = null;
                var ctors = innerType.GetConstructors();
                System.Reflection.ConstructorInfo ctor = null;
                foreach (var c in ctors)
                {
                    if (c.GetParameters().Length == 3) { ctor = c; break; }
                }
                if (ctor == null)
                {
                    HideoutPeacefulVisitState.Log("TryAddGoldenQuestMarker: no 3-arg ctor found");
                    return;
                }
                try
                {
                    var ps = ctor.GetParameters();
                    var flagType = ps[0].ParameterType; // IssueQuestFlags enum
                    var enumValues = Enum.GetValues(flagType);

                    // Log all enum values once so we can pick the right flag.
                    if (!_flagsLogged)
                    {
                        _flagsLogged = true;
                        var sb = new System.Text.StringBuilder();
                        sb.Append("IssueQuestFlags values: ");
                        foreach (var v in enumValues)
                        {
                            sb.Append(v.ToString()).Append("=").Append(Convert.ToInt32(v)).Append(", ");
                        }
                        HideoutPeacefulVisitState.Log(sb.ToString());
                    }

                    // Bannerlord 1.2.x IssueQuestFlags enum (logged):
                    //   None=0, AvailableIssue=1 (renders BLUE), ActiveIssue=2 (gold "!"),
                    //   ActiveStoryQuest=4, TrackedIssue=8, TrackedStoryQuest=16.
                    // ActiveIssue gives the gold/yellow ! (vanilla quest-giver tracking icon).
                    // Confirmed via Native/GUI/Brushes/GameMenu.xml: GameMenu.QuestMarker
                    // brush has 5 layers — Issue variants (Available/Active/Tracked) all use
                    // BLUE icon_issue_* sprites; STORY variants use GOLD icon_story_quest_*
                    // sprites. ActiveStoryQuest = sprite "icon_story_quest_active" = gold "!".
                    // Wave 4.11-fix v45: chief gets gold (ActiveStoryQuest sprite),
                    // vendor gets blue (AvailableIssue sprite). Bannerlord's
                    // QuestMarkerBrushWidget reads the flag to pick which layer of
                    // GameMenu.QuestMarker brush renders.
                    string[] preferredNames = blueVariant
                        ? new[] { "AvailableIssue", "ActiveIssue" }
                        : new[] { "ActiveStoryQuest", "TrackedStoryQuest" };
                    object flagValue = null;
                    foreach (var name in preferredNames)
                    {
                        foreach (var v in enumValues)
                        {
                            if (v.ToString() == name) { flagValue = v; break; }
                        }
                        if (flagValue != null) break;
                    }
                    // Fallback: first non-zero.
                    if (flagValue == null)
                    {
                        foreach (var v in enumValues)
                        {
                            if (Convert.ToInt32(v) != 0) { flagValue = v; break; }
                        }
                        if (flagValue == null && enumValues.Length > 0) flagValue = enumValues.GetValue(0);
                    }
                    HideoutPeacefulVisitState.Log("TryAddGoldenQuestMarker: picked flag=" + flagValue + (blueVariant ? " (blue/vendor)" : " (gold/chief)"));
                    var title = blueVariant
                        ? new TaleWorlds.Localization.TextObject("{=bp_campnamemarkerprovider_001}Speak with the vendor")
                        : new TaleWorlds.Localization.TextObject("{=bp_campnamemarkerprovider_002}Speak with the chief");
                    var hint = blueVariant
                        ? new TaleWorlds.Localization.TextObject("{=bp_campnamemarkerprovider_003}This vendor has a bring-me-X quest for you.")
                        : new TaleWorlds.Localization.TextObject("{=bp_campnamemarkerprovider_004}This bandit chief has work for you.");
                    item = ctor.Invoke(new object[] { flagValue, title, hint });
                }
                catch (Exception ctorEx)
                {
                    HideoutPeacefulVisitState.Log("TryAddGoldenQuestMarker: ctor invoke fail: " + ctorEx.Message);
                    return;
                }

                // Clear any existing items so we don't accumulate per tick (each tick
                // creates a fresh VM, but defensive).
                var clearMethod = listType.GetMethod("Clear");
                try { clearMethod?.Invoke(listObj, null); } catch { }

                var addMethod = listType.GetMethod("Add", new[] { innerType });
                if (addMethod != null)
                {
                    addMethod.Invoke(listObj, new[] { item });
                }
            }
            catch (Exception ex)
            {
                HideoutPeacefulVisitState.Log("TryAddGoldenQuestMarker fail: " + ex.Message);
            }
        }

        protected override void OnInitialize(Mission mission)
        {
            HideoutPeacefulVisitState.Log("BanditCampNameMarkerProvider: OnInitialize fired");
        }

        protected override void OnDestroy(Mission mission)
        {
            HideoutPeacefulVisitState.Log("BanditCampNameMarkerProvider: OnDestroy fired");
        }
    }
}
