using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;

namespace BandItPlus.Origins
{
    // Origin-keyed encyclopedia prose for the player hero + clan. Written into the
    // native Hero/Clan encyclopedia pages by EncyclopediaHeroIncludePatch. Restored
    // 07-16 from the shipped DLL after it was accidentally removed with the origin
    // screens (it had been bundled in one of those files); text is unchanged.
    internal static class BanditChronicleCodex
    {
        private static string LinkHero(Hero h)
        {
            return "<a style=\"Link.Hero\" href=\"event:" + h.EncyclopediaLink + "\">" + h.Name?.ToString() + "</a>";
        }

        private static string LinkClan(Clan c)
        {
            return "<a style=\"Link\" href=\"event:" + c.EncyclopediaLink + "\">" + c.Name?.ToString() + "</a>";
        }

        internal static string BuildHeroStory(Hero hero, Clan clan, BanditOriginBehavior.BanditOrigin origin)
        {
            string heroName = hero.Name != null ? hero.Name.ToString() : BandItPlus.Localization.Get("bp_introstoryvm_012", "The stranger");
            bool isFemale = hero.IsFemale;
            string heCap = isFemale ? BandItPlus.Localization.Get("bp_hco_001", "She") : BandItPlus.Localization.Get("bp_hco_002", "He");
            string he = isFemale ? BandItPlus.Localization.Get("bp_hco_003", "she") : BandItPlus.Localization.Get("bp_hco_004", "he");
            string him = isFemale ? BandItPlus.Localization.Get("bp_hco_005", "her") : BandItPlus.Localization.Get("bp_hco_006", "him");
            string clanLink = LinkClan(clan);

            switch (origin)
            {
                case BanditOriginBehavior.BanditOrigin.OutlawBlood:
                {
                    var t = new TextObject("{=bp_encstory_001}{HERO} was raised at those fires — born to the road the way other folk are born to a trade. The family name was a warrant in three districts before {HE} could hold a knife, and nobody asked the heir. The chronicle does not romance it: outlaw blood is no destiny. It is an inheritance, and like most inheritances it arrived with debts attached.\n\nWhat the law calls a record, the camps call a pedigree. The clans of the wilds remember the name, the fires burn warmer for it, and peace — where it must be bought — comes at half the lawman's rate. Where the lords read a brand, the road reads a birthright.\n\n{HE_CAP} rides now under the banner of {CLAN}, and the wilds keep the ledger.\n\n— so records the Royal Codex, in the company of outlaws.");
                    t.SetTextVariable("HERO", heroName);
                    t.SetTextVariable("HE", he);
                    t.SetTextVariable("HE_CAP", heCap);
                    t.SetTextVariable("CLAN", clanLink);
                    return t.ToString() + BuildHeroSaga(hero);
                }
                case BanditOriginBehavior.BanditOrigin.Lawkeeper:
                {
                    var t = new TextObject("{=bp_encstory_002}{HERO} wore a badge once, and hunted these very banners through the cold — collected the bounties, kept the watch, and learned to the pace exactly where the law's writ gives out. A mile from every wall, as it happens. The chronicle records the turning without judgment: hunt wolves long enough and you learn the wolf-paths, and some who learn them do not come home by the road they left on.\n\nThe crown's ledgers still hold the old commendations, filed one page from the new warrants; the clerks see no reason to use a different ink. The camps, for their part, neither forgive nor forget a lawkeeper. Peace costs double at their fires, and every hand stays where the chief can see it. Trust is sold to former lawmen at former-lawman rates.\n\n{HE_CAP} rides now under the banner of {CLAN}, and both sides of the law keep the ledger open.\n\n— so records the Royal Codex, in the company of outlaws.");
                    t.SetTextVariable("HERO", heroName);
                    t.SetTextVariable("HE_CAP", heCap);
                    t.SetTextVariable("CLAN", clanLink);
                    return t.ToString() + BuildHeroSaga(hero);
                }
                default:
                {
                    var t = new TextObject("{=bp_encstory_003}{HERO} came to Calradia as no one at all — owing nothing, owed nothing, free as only the unremembered are free. No name worth a warrant, no grudge worth a song, no coat cut to fit. The chronicle met {HIM} the way the towns did: briefly, and without learning much.\n\nThat is the drifter's whole inheritance, and it spends better than it sounds. The camps warm to such folk quickly, for there is no history to forgive; the clerks of three capitals have no page waiting. Yet. The road is patient about its paperwork, and it has never once failed to open a file.\n\n{HE_CAP} rides now under the banner of {CLAN}, and the road keeps its own accounts.\n\n— so records the Royal Codex, in the company of outlaws.");
                    t.SetTextVariable("HERO", heroName);
                    t.SetTextVariable("HIM", him);
                    t.SetTextVariable("HE_CAP", heCap);
                    t.SetTextVariable("CLAN", clanLink);
                    return t.ToString() + BuildHeroSaga(hero);
                }
            }
        }

        // Living Chronicle — the "saga" section appended after the origin passage:
        // one dated chronicler paragraph per unlocked milestone, in unlock order.
        // id -> full English prose. This inline is the EN source of truth AND the fallback,
        // mirroring BuildHeroStory's origin passages — so the saga renders correctly even if the
        // module-string table hasn't registered these ids. The {=bp_chronicle_<id>} key supplies
        // the translation (RU) when the table IS loaded.
        private static readonly (string id, string en)[] SagaChapters =
        {
            ("first_parley",   "◆  The First Parley — {CTX} raised the white flag, and {HERO} rode in under it without drawing steel. The chronicle marks the day; the roads marked the precedent. A stranger who sits at a chief's fire does not leave one."),
            ("kin_oath",       "◆  Kin & Oath — the {CTX} spoke for {HERO} at last. A company that answers to no crown now answers, in some measure, to one name. Sworn kin cost more than hired swords, and are worth the difference."),
            ("open_camp",      "◆  The Open Camp — the watch at {CTX} stood aside and let {HERO} walk in past the stakes. A camp that opens its gate has decided something about {HERO} no lord ever will."),
            ("horn_answered",  "◆  The Horn Answered — {CTX} burned to {HERO}'s horn, and the tithe it owed some distant lord it paid, that season, to the road instead. The chronicle does not moralize. It only tallies."),
            ("blood_for_blood","◆  Blood for Blood — {CTX} changed hands at the point of {HERO}'s siege, and a banner the heralds had no entry for flew above a wall that had only ever known a king's. The clerks will want a fresh page."),
        };

        // Render one saga chapter to final text — used by both the inline fallback below and the
        // EE-Core journal push. Returns null for an unknown id.
        internal static string RenderChapter(string id, string heroName, string ctx, double day)
        {
            string en = null;
            foreach (var c in SagaChapters) if (c.id == id) { en = c.en; break; }
            if (en == null) return null;
            var t = new TextObject("{=bp_chronicle_" + id + "}" + en);
            t.SetTextVariable("HERO", heroName ?? "");
            t.SetTextVariable("CTX", ctx ?? "");
            return t.ToString();
            // note: EE-Core date-stamps journal entries; the inline fallback lists them in unlock order.
        }

        internal static string BuildHeroSaga(Hero hero)
        {
            // When EE-Core is present it renders the chronicle as an editable page journal —
            // skip the inline append so the saga isn't painted twice.
            if (BandItPlus.Integration.EeBridge.Available) return "";

            var beh = BanditChronicleBehavior.Instance;
            if (beh == null) return "";
            var unlocked = beh.GetUnlockedOrdered();
            if (unlocked.Count == 0) return "";

            string heroName = hero?.Name != null ? hero.Name.ToString() : BandItPlus.Localization.Get("bp_introstoryvm_012", "The stranger");
            var sb = new System.Text.StringBuilder();
            sb.Append(BandItPlus.Localization.Get("bp_chronicle_saga_header", "\n\n— AND THE ROADS KEPT WRITING —\n"));
            foreach (var entry in unlocked)
            {
                string line = RenderChapter(entry.id, heroName, entry.ctx, entry.day);
                if (line == null) continue;
                sb.Append("\n").Append(line);
            }
            return sb.ToString();
        }

        internal static string BuildClanStory(Hero hero, Clan clan)
        {
            string clanName = clan.Name != null ? clan.Name.ToString() : BandItPlus.Localization.Get("bp_introstoryvm_013", "This banner");
            var t = new TextObject("{=bp_encstory_004}{CLAN_NAME} flies in no king's muster-roll. Count it among the strange banners: a company raised from the road's leavings — deserters with sergeants' voices, poachers who read a forest like scripture, reckoners who can price a caravan at half a mile. The chronicle lists it between the Highwaymen and the Frost Reavers, and sees no reason to use a different ink.\n\nIts charter is the Law of the Wilds. Its treasury is, broadly speaking, other people's. Its history is short and being lengthened almost nightly, in the only script the roads respect. At its head rides {HERO_LINK} — a name the lords who post the bounties have lately begun to spell correctly.\n\nHunger first, grievance second, custom third: so runs the old rule of how banners are born. This one is somewhere past grievance, and gaining on custom.\n\n— so records the Royal Codex, in the company of outlaws.");
            t.SetTextVariable("CLAN_NAME", clanName);
            t.SetTextVariable("HERO_LINK", LinkHero(hero));
            return t.ToString();
        }
    }
}
