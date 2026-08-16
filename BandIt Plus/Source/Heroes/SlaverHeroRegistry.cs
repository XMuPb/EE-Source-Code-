using System;
using BandItPlus.Cultures;
using TaleWorlds.CampaignSystem;

namespace BandItPlus.Heroes
{
    // Wave 4.12 (2026-05-10): SLAVER HERO REGISTRY — Phase 7.1.
    //
    // CampaignBehaviorBase that registers the singleton Instance at session launch so
    // BanditSlaverQuest.OnCompleteWithSuccess + HideoutSlaverDialog.BuildSlaverHeroPool
    // can reach it. Currently returns null for all Hero lookups — full Hero synthesis
    // (mirroring BanditChiefRegistry.cs's CreateSpecialHero + reflection pattern) is
    // deferred to a future wave. Null returns are gracefully handled by all callers:
    //   - OnCompleteWithSuccess skips the +5 relation toast
    //   - BuildSlaverHeroPool simply doesn't append a T3 unique-named hero to the pool
    //
    // Bannerlord 1.2.x AND 1.3.x compatibility: subscribes only to OnSessionLaunchedEvent
    // (canonical event in both versions). Future-wave Hero synthesis will use
    // Hero.CreateSpecialHero(template) which is also confirmed-stable across both.
    public class SlaverHeroRegistry : CampaignBehaviorBase
    {
        private static SlaverHeroRegistry _instance;
        public static SlaverHeroRegistry Instance => _instance;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                _instance = this;
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "SlaverHeroRegistry.OnSessionLaunched: Instance set (" + SlaverProfiles.ByCultureId.Count + " culture profiles loaded)");
                // Future wave: synthesize the slaver Hero + T3 unique Hero per culture here
                // using Hero.CreateSpecialHero(template) — pattern from BanditChiefRegistry.
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "SlaverHeroRegistry.OnSessionLaunched fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public Hero GetSlaver(string cultureId) => null;
        public Hero GetT3Hero(string cultureId) => null;
        public Hero EnsureSlaverNamed(Hero candidate, string cultureId) => candidate;
    }
}
