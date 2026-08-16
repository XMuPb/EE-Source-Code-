using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Localization;

namespace BandItPlus.BanditPower
{
    // v158 (2026-05-16): raises the troop-size limit for bandit/looter parties so
    // BanditPowerBehavior can grow them past vanilla caps without troops
    // deserting. Vanilla's PartySizeLimitModel makes any party over its limit
    // shed men — so growing a party is pointless unless the limit grows too.
    //
    // Delegates to the vanilla model for every non-bandit party, and is a pure
    // pass-through when the feature is toggled off in MCM. Registered via
    // CampaignGameStarter.AddModel in SubModuleClassEntry.OnGameStart (last
    // model registered for a type wins).
    public class BanditPartySizeModel : DefaultPartySizeLimitModel
    {
        private static readonly TextObject _bonusText = new TextObject("{=bp_partysizemodel_001}BandIt Plus — Bandit Power");

        public override ExplainedNumber GetPartyMemberSizeLimit(PartyBase party, bool includeDescriptions)
        {
            var result = base.GetPartyMemberSizeLimit(party, includeDescriptions);
            try
            {
                var mp = party?.MobileParty;
                if (mp == null || mp.MapFaction == null || !mp.MapFaction.IsBanditFaction)
                    return result;

                var s = MCMSettings.Instance;
                if (s == null || !s.EnableMod || !s.EnableBanditPower) return result;

                int cap = BanditPowerBehavior.CurrentPartySizeCap();
                if (cap > result.ResultNumber)
                    result.Add(cap - result.ResultNumber, _bonusText);
            }
            catch { /* never let a model throw — fall back to vanilla result */ }
            return result;
        }
    }
}
