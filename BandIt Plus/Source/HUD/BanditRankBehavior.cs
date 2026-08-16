using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BandItPlus.HUD
{
    // Wave 4.28.0 — Infamy / Bandit Rank backend that drives the campaign-map HUD
    // ribbon (tier badge + rank name under the crest). Infamy climbs from bandit
    // deeds — slaying war bands, earning a clan's peace, raids, pledges. Crossing a
    // threshold promotes the player up the ladder (Footpad → Scourge of Calradia)
    // with a toast. Persisted as a single int (no SaveableTypeDefiner / container
    // definition needed). The HUD VM reads everything through Instance.
    public class BanditRankBehavior : CampaignBehaviorBase
    {
        private static BanditRankBehavior _instance;
        public static BanditRankBehavior Instance => _instance;

        private int _infamy; // saved

        // tier thresholds + names (index 0..5 → Tier 1..5 + Maxed)
        private static readonly int[] THRESH = { 0, 150, 400, 800, 1400, 2200 };
        private static readonly string[] RANKS =
        {
            BandItPlus.Localization.Get("bp_rankbehavior_001", "Footpad"),
            BandItPlus.Localization.Get("bp_rankbehavior_002", "Reaver"),
            BandItPlus.Localization.Get("bp_rankbehavior_003", "Brigand-Captain"),
            BandItPlus.Localization.Get("bp_rankbehavior_004", "Outlaw Lord"),
            BandItPlus.Localization.Get("bp_rankbehavior_005", "Bandit Warlord"),
            BandItPlus.Localization.Get("bp_rankbehavior_006", "Scourge of Calradia")
        };

        public int Infamy => _infamy;
        public int Tier
        {
            get { int t = 0; for (int i = 0; i < THRESH.Length; i++) if (_infamy >= THRESH[i]) t = i; return t; }
        }
        public string RankName => RANKS[Tier];
        // HUD sprite id for the current tier (hud_icons category, once Exported).
        public string TierSpriteName => Tier >= 5 ? "bp_tiermaxed" : "bp_tier" + (Tier + 1);
        public int InfamyToNext { get { int t = Tier; return t >= 5 ? 0 : THRESH[t + 1] - _infamy; } }
        public float TierProgress
        {
            get { int t = Tier; if (t >= 5) return 1f; int lo = THRESH[t], hi = THRESH[t + 1]; return (float)(_infamy - lo) / (hi - lo); }
        }
        public string GoldDisplay { get { try { return (Hero.MainHero?.Gold ?? 0).ToString("N0"); } catch { return "0"; } } }

        public override void RegisterEvents()
        {
            _instance = this;
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, OnPartyDestroyed);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_bpInfamy", ref _infamy);
        }

        // Public entry — kills feed it here; other systems (peace earned, pledge
        // fulfilled, raids) also call this to credit infamy.
        public void AddInfamy(int amount, string reason)
        {
            if (amount <= 0) return;
            int before = Tier;
            _infamy += amount;
            Log("infamy +" + amount + " (" + reason + ") -> " + _infamy + " [" + RankName + "]");
            if (Tier > before) OnRankUp(Tier);
        }

        private void OnRankUp(int tier)
        {
            try
            {
                // Gated by the Console HUD chapter ("Rank-up notifications").
                if (MCMSettings.Instance == null || MCMSettings.Instance.ShowRankUpToast)
                    BandItPlus.UI.BanditToastScreen.Show(
                        new TextObject("{=bp_hcm_001}Rank Up — {RANK}").SetTextVariable("RANK", RANKS[tier]).ToString(),
                        new TextObject("{=bp_hcm_002}Your name spreads down every road. You are now a {RANK}.").SetTextVariable("RANK", RANKS[tier]).ToString(),
                        "event:/ui/notification/relation");
            }
            catch { }
            Log("RANK UP -> " + RANKS[tier] + " (tier " + (tier + 1) + ")");
        }

        // Bandit war bands slain by the player feed your infamy (bigger band = more).
        private void OnPartyDestroyed(MobileParty party, PartyBase destroyer)
        {
            try
            {
                if (party == null || party.MapFaction == null || !party.MapFaction.IsBanditFaction) return;

                bool playerKill = destroyer != null
                    && (destroyer == PartyBase.MainParty
                        || (destroyer.MobileParty != null && destroyer.MobileParty == MobileParty.MainParty)
                        || (destroyer.LeaderHero != null && destroyer.LeaderHero == Hero.MainHero));
                if (!playerKill)
                {
                    try
                    {
                        var pme = TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent;
                        playerKill = pme != null && party.Party != null && party.Party.MapEvent == pme;
                    }
                    catch { }
                }
                if (!playerKill) return;

                int men = party.MemberRoster != null ? party.MemberRoster.TotalManCount : 0;
                int gain = men >= 8 ? 15 : (men >= 3 ? 6 : 0);
                if (gain > 0) AddInfamy(gain, "slew a " + party.MapFaction.Name + " band");
            }
            catch (Exception ex) { Log("OnPartyDestroyed error: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Rank] " + msg); }
            catch { }
        }
    }
}
