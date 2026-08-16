using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace BandItPlus.UI
{
    // 2026-07-01 (Bandit-King GUI, Task 4): the data payload for the reusable
    // BanditKingBrief premium panel + per-event factories + a main-thread-safe
    // Show() entry point. The panel VM (BanditKingBriefVM) reads this POCO; the
    // caller task (next) wires the six campaign events to these factories.
    //
    // Row tuple = (icon spriteId, label, value). Portrait/Banner are pulled from
    // the live hero/clan by the VM (both null-guarded).
    public class BanditKingBriefData
    {
        public string ArtSprite;
        public Hero PortraitHero;
        public Clan BannerClan;
        public string Title, Subline, Flavor, Stakes, KName, KType;
        // POLISH: SectionLabel = the "— THE RISING —" style divider above the rows;
        // WhyText = the 2-4 sentence "why it's happening" cause block under the rows.
        public string SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_001", "— WHAT THE SCOUTS REPORT —");
        public string WhyText;
        // POLISH (2026-07-01): the authored long-form STORY that fills the panel's
        // middle scrollable column. StorySectionLabel = the "— THE MADNESS —" style
        // header above the prose; StoryText = the multi-paragraph tale (verbatim).
        public string StorySectionLabel;
        public string StoryText;
        // Row tuple carries a per-row ACCENT color ("#RRGGBBAA") alongside the sprite.
        public List<(string icon, string label, string value, string accent)> Rows = new List<(string, string, string, string)>();
        // Visual pass (2026-07-01): colored category TAG chips shown in the header
        // band under the leader name (2 per event, e.g. "TOTAL WAR" + "OATH OF ASH").
        public List<(string text, string accent)> Tags = new List<(string, string)>();
        public string PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_002", "Continue");
        public string SecondaryText;            // null → single button
        public Action OnPrimary;
        public Action OnSecondary;
        // 2026-07-02 (sound pass): fire-and-forget 2D sound played when the panel
        // opens. Ids verified present in the game's v1.4.6 DLL string tables.
        public string SoundId;

        // ---- POLISH accent palette (mirrors BpJailbreakBrief per-row accents) ----
        private const string CRIMSON = "#E0584CFF"; // war / assault / stakes
        private const string GOLD    = "#F0CC68FF"; // the host / crown / reward
        private const string GREEN   = "#84C878FF"; // lieutenants / allies
        private const string BLUE    = "#6FA8D8FF"; // fleet / sea
        private const string TEAL    = "#5FC8B4FF"; // culture / people
        private const string AMBER   = "#E8A24AFF"; // war-chest gold
        private const string STONE   = "#B6A981FF"; // home / base / stone
        private const string VIOLET  = "#B48CD8FF"; // years hidden / the past
        private const string ASH     = "#C8BC97FF"; // scattered / retaken

        // The three rebellion-arc events (Rise/Fall/Crush) can be routed to the
        // passive BanditToast instead of the full panel via the MCM toggle
        // RebellionPanelsAsToasts. Rises/Offer/Marches always show the full panel.
        // Set true by ForRebellion / ForCityFalls / ForCrushed.
        public bool IsRebellionEvent;

        // convenience row-adder that guards nulls (never throws on bad live data).
        // accent defaults to gold when omitted so the value reads as the crown's.
        private void Row(string icon, string label, string value, string accent = "#F0CC68FF")
        {
            try { Rows.Add((icon ?? "", label ?? "", value ?? "", accent ?? "#F0CC68FF")); } catch { }
        }

        private void Tag(string text, string accent = "#F0CC68FF")
        {
            try { Tags.Add((text ?? "", accent ?? "#F0CC68FF")); } catch { }
        }

        // ---- per-event factories --------------------------------------------
        // These are STUBBED for now (art id + placeholder rows). The real live-
        // data wiring (host size, target fief, loyalty, garrison) lands in the
        // caller task. Every property read stays guarded so a half-built holdout
        // can never crash the factory.

        public static BanditKingBriefData ForRises(Clan holdout, MobileParty king, int kingdomsAtWar, int ships)
        {
            var d = new BanditKingBriefData
            {
                ArtSprite = "banditking_mad",
                PortraitHero = SafeLeader(holdout),
                BannerClan = holdout,
                Title = BandItPlus.Localization.Get("bp_kingbriefdata_003", "The Mad King Rises"),
                Subline = SafeName(holdout),
                Flavor = BandItPlus.Localization.Get("bp_kingbriefdata_004", "A crowned madman gathers the broken and the desperate beneath a stolen banner. Word runs ahead of him on every road: the wilds have birthed a king who answers to no one."),
                Stakes = BandItPlus.Localization.Get("bp_kingbriefdata_005", "Every throne in the land is now his to shatter."),
                KName = SafeLeaderName(holdout),
                KType = BandItPlus.Localization.Get("bp_kingbriefdata_006", "Bandit King"),
                SoundId = "event:/ui/notification/war_declared",
                SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_007", "— THE RISING —"),
                PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_008", "So it begins"),
                WhyText = BandItPlus.Localization.Get("bp_kingbriefdata_009", "Years alone in the wilds curdled his ambition into madness. Cast out and left for dead, he has no crown to inherit and no lord to kneel to — so he will forge his own from fire and fear. He would sooner rule the ashes of Calradia than bend a knee to any throne that once forgot him."),
                StorySectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_010", "— THE MADNESS —"),
                StoryText = BandItPlus.Localization.Get("bp_kingbriefdata_011", "He was a man once — a chief of thieves who lost every war worth losing and crawled into the deep wilds to let the years rot him hollow. Somewhere between one starving winter and the next, the last of the man burned away and left only the wanting. He came down out of the trees with a crown that was never his, stolen from a dead king's brow and still dark with another man's blood.\n\nNo priest anointed him. He crowned himself by torchlight and swore the oath that shook every court in Calradia: war, not on one throne but on all of them, every crowned neck between the seas pulled down into the ash. The broken heard him and came crawling. He spares only the friends of outlaws; all others are kindling. A madman would rather rule the ashes."),
            };
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_012", "TOTAL WAR"), CRIMSON);
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_013", "OATH OF ASH"), AMBER);
            d.Row("SPPerks\\OneHandedShieldWall", BandItPlus.Localization.Get("bp_kingbriefdata_014", "War declared on"), new TaleWorlds.Localization.TextObject("{=bp_kbunit_kingdoms}{N} kingdoms").SetTextVariable("N", kingdomsAtWar).ToString(), CRIMSON);
            d.Row("SPPerks\\LeadershipMakeADifference", BandItPlus.Localization.Get("bp_kingbriefdata_015", "The host"), SafeMen(king), GOLD);
            d.Row("SPPerks\\LeadershipCombatTips", BandItPlus.Localization.Get("bp_kingbriefdata_016", "Lieutenants"), SafeLieutenants(holdout), GREEN);
            d.Row("SPPerks\\ScoutingForestKin", BandItPlus.Localization.Get("bp_kingbriefdata_017", "Fleet"), new TaleWorlds.Localization.TextObject("{=bp_kbunit_ships}{N} ships").SetTextVariable("N", ships).ToString(), BLUE);
            d.Row("SPPerks\\TradeSpringOfGold", BandItPlus.Localization.Get("bp_kingbriefdata_018", "War-chest"), SafeGoldTier(holdout), AMBER);
            return d;
        }

        public static BanditKingBriefData ForOffer(Clan holdout)
        {
            var d = new BanditKingBriefData
            {
                ArtSprite = "banditking_offer",
                PortraitHero = SafeLeader(holdout),
                BannerClan = holdout,
                Title = BandItPlus.Localization.Get("bp_kingbriefdata_019", "The Mad King's Offer"),
                Subline = SafeName(holdout),
                Flavor = BandItPlus.Localization.Get("bp_kingbriefdata_020", "He remembers you. Of all the swords in Calradia he sends for yours, and offers a place at his side when the kingdoms burn."),
                Stakes = BandItPlus.Localization.Get("bp_kingbriefdata_021", "Stand with him, or be counted among his enemies."),
                KName = SafeLeaderName(holdout),
                KType = BandItPlus.Localization.Get("bp_kingbriefdata_022", "Bandit King"),
                SoundId = "event:/ui/notification/peace_offer",
                SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_023", "— WHAT HE PUTS ON THE TABLE —"),
                PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_024", "Stand with him"),
                SecondaryText = BandItPlus.Localization.Get("bp_kingbriefdata_025", "Refuse"),
                WhyText = BandItPlus.Localization.Get("bp_kingbriefdata_026", "He offers YOU because you crossed his path and lived — a rare thing, and the only kind of respect a madman keeps. Stand with him and you march under the crown that burns the map; refuse, and you join the long list of thrones he means to pull down. There is no third road he will honor."),
                StorySectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_027", "— THE MAD KING'S OFFER —"),
                StoryText = BandItPlus.Localization.Get("bp_kingbriefdata_028", "He sends no heralds, no gold, no honeyed words. Vaykos comes himself, out of the wet dark where the campfires die, the stolen crown black with old blood upon his brow. He knows your name — heard it whispered along the smuggler roads where honest men fear to breathe. One of the wolves. He sets his blade across his knees: every throne in Calradia is a lie, every king a thief who was luckier than you.\n\nHe means to pull them all down until only ash remains and he alone stands crowned upon it — and in that ash, he would have you beside him. His brothers as your brothers, every gate his war breaks yours to plunder. But mark him: he spares only friends. Refuse, and you become a throne to be torn down. There is only the offered hand, or the offered grave."),
                // OnPrimary / OnSecondary supplied by the caller task.
            };
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_029", "ALLIANCE"), GREEN);
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_030", "ULTIMATUM"), CRIMSON);
            d.Row("SPPerks\\CharmSelfPromoter", BandItPlus.Localization.Get("bp_kingbriefdata_031", "He wants"), BandItPlus.Localization.Get("bp_kingbriefdata_032", "Your blade sworn to the crown"), GOLD);
            d.Row("SPPerks\\LeadershipInspiringLeader", BandItPlus.Localization.Get("bp_kingbriefdata_033", "Stand with him"), BandItPlus.Localization.Get("bp_kingbriefdata_034", "War on the kingdoms, a share of the ruin"), GREEN);
            d.Row("SPPerks\\OneHandedShieldWall", BandItPlus.Localization.Get("bp_kingbriefdata_035", "Refuse him"), BandItPlus.Localization.Get("bp_kingbriefdata_036", "Counted among his enemies"), CRIMSON);
            return d;
        }

        public static BanditKingBriefData ForMarches(Clan holdout, MobileParty king, Settlement target, double captureAtHours)
        {
            var d = new BanditKingBriefData
            {
                ArtSprite = "banditking_marching",
                PortraitHero = SafeLeader(holdout),
                BannerClan = holdout,
                Title = BandItPlus.Localization.Get("bp_kingbriefdata_037", "The Mad King Marches"),
                Subline = SafeName(holdout),
                Flavor = BandItPlus.Localization.Get("bp_kingbriefdata_038", "The host moves. A settlement lies in its path, and the crownless king means to have it — walls, granaries, and all the terror its fall will spread."),
                Stakes = BandItPlus.Localization.Get("bp_kingbriefdata_039", "If it falls, his reign spreads."),
                KName = SafeLeaderName(holdout),
                KType = BandItPlus.Localization.Get("bp_kingbriefdata_040", "Bandit King"),
                SoundId = "event:/ui/mission/horns/move",
                SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_041", "— THE MARCH ON THE WALLS —"),
                PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_042", "Watch it unfold"),
                WhyText = BandItPlus.Localization.Get("bp_kingbriefdata_043", "This fief, not another, because it is soft and it is near — a granary to feed his horde and a wall to shelter behind while the kingdoms muster. Each town he swallows makes the next easier. He does not march to hold land; he marches so that no lord may sleep easy while he draws breath."),
                StorySectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_044", "— THE KING MARCHES —"),
                StoryText = BandItPlus.Localization.Get("bp_kingbriefdata_045", "First the birds go quiet. Then the smoke — a smudge on the morning that thickens by noon until the whole eastern sky wears mourning. The garrison knows what climbs the road before the outriders bring word: a black banner, sun-faded to the grey of old blood, and beneath it a tide no herald counted twice and lived. Vaykos comes crowned, the circlet he took from a dead man's brow sitting crooked on his skull.\n\nBehind him march the broken things of Calradia — deserters, debtors, men the lords forgot, gathered the way rot gathers flies. Bar the gate; it will not hold. A horn sounds below — not a summons to parley, for the King parleys with no throne. He has sworn to pull down every crown until only ash answers to him. The walls are stone, and stone remembers nothing. Watch the banner come."),
            };
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_046", "SIEGE"), AMBER);
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_047", "INCOMING"), CRIMSON);
            d.Row("SPPerks\\OneHandedShieldWall", BandItPlus.Localization.Get("bp_kingbriefdata_048", "Target"), SafeSettlement(target), CRIMSON);
            d.Row("SPPerks\\LeadershipMakeADifference", BandItPlus.Localization.Get("bp_kingbriefdata_049", "The force"), SafeMen(king), GOLD);
            d.Row("SPPerks\\ScoutingVanguard", BandItPlus.Localization.Get("bp_kingbriefdata_050", "Assault in"), SafeEta(captureAtHours), BLUE);
            d.Row("SPPerks\\OneHandedShieldBash", BandItPlus.Localization.Get("bp_kingbriefdata_051", "Defenders"), SafeGarrison(target), GREEN);
            d.Row("SPPerks\\StewardMasterOfWarcraft", BandItPlus.Localization.Get("bp_kingbriefdata_052", "Prize"), SafeSettlementOwner(target), STONE);
            return d;
        }

        // 2026-07-01 data-accuracy fix: a rebellion RESETS the settlement's loyalty to 100,
        // so reading s.Town.Loyalty live here (as the old SafeLoyalty(s) did) wrongly showed
        // 100/100. Callers now CAPTURE the pre-reset loyalty and pass it in. Pass < 0 (e.g.
        // -1f) when the break value is unknown → the "Loyalty at break" row is omitted.
        public static BanditKingBriefData ForRebellion(Settlement s, MobileParty rebels, Clan formerOwner, float loyaltyAtBreak)
        {
            var d = new BanditKingBriefData
            {
                ArtSprite = "rebellion_start",
                PortraitHero = null,
                BannerClan = SafeOwner(s) ?? formerOwner,
                Title = BandItPlus.Localization.Get("bp_kingbriefdata_053", "The People Rise"),
                Subline = SafeSettlement(s),
                Flavor = BandItPlus.Localization.Get("bp_kingbriefdata_054", "Bled dry and pushed too far, the townsfolk take up arms against their lords. The tax-men are dragged into the square, and the gate-bars come down."),
                Stakes = BandItPlus.Localization.Get("bp_kingbriefdata_055", "The banner over the walls may soon be a bandit's."),
                KName = SafeSettlement(s),
                KType = BandItPlus.Localization.Get("bp_kingbriefdata_056", "Uprising"),
                SoundId = "event:/ui/notification/settlement_rebellion",
                SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_057", "— WHY THE TOWN BROKE —"),
                PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_058", "Let them rise"),
                IsRebellionEvent = true,
                WhyText = BandItPlus.Localization.Get("bp_kingbriefdata_059", "The town broke because loyalty finally ran out. Taxed past bearing and shielded by no one, the commons decided a bandit's chaos could be no worse than a lord's neglect. Where order abandons a people, the crownless king finds his gladdest recruits — and a mad crown is happy to bless the ruin."),
                StorySectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_060", "— THE PEOPLE RISE —"),
                StoryText = BandItPlus.Localization.Get("bp_kingbriefdata_061", "They bled the town white before it broke. Season upon season the outlaws came down from the hills while the lord counted his silver behind his gate. Grain taken. Daughters taken. A town does not rise while it can still crawl — and this one had forgotten how. The spark was small: a boy hanged at the marketwall for stealing back his father's bread.\n\nWhen the rope went taut the square went silent, and then it did not. Torches from the thatch, scythes off the barn wall, until the lord's men bled the same red as everyone else. But look what the smoke has drawn — riders on the ridgeline, patient, counting. Vaykos knows a town that has murdered its master; such a people kneel to any crown that calls them kin. The banner they raised in fury may yet be stitched with his."),
            };
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_062", "UPRISING"), CRIMSON);
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_063", "LOYALTY BROKEN"), VIOLET);
            d.Row("SPPerks\\OneHandedShieldWall", BandItPlus.Localization.Get("bp_kingbriefdata_064", "Settlement"), SafeSettlement(s), CRIMSON);
            d.Row("SPPerks\\LeadershipMakeADifference", BandItPlus.Localization.Get("bp_kingbriefdata_065", "The rising"), SafeMen(rebels), GOLD);
            // Use the CAPTURED pre-reset loyalty, not the live (post-reset = 100) value.
            // < 0 means the caller couldn't obtain it → omit the row rather than show a wrong number.
            if (loyaltyAtBreak >= 0f)
                d.Row("SPPerks\\TradeContentTrades", BandItPlus.Localization.Get("bp_kingbriefdata_066", "Loyalty at break"), Math.Round(loyaltyAtBreak, 0) + " \\ 100", TEAL);
            d.Row("SPPerks\\CharmSelfPromoter", BandItPlus.Localization.Get("bp_kingbriefdata_067", "Prosperity"), SafeProsperity(s), AMBER);
            return d;
        }

        public static BanditKingBriefData ForCityFalls(Settlement s, string cultureName, int addedGarrison)
        {
            var d = new BanditKingBriefData
            {
                ArtSprite = "rebellion_victory",
                PortraitHero = null,
                BannerClan = SafeOwner(s),
                Title = BandItPlus.Localization.Get("bp_kingbriefdata_068", "The City Falls"),
                Subline = SafeSettlement(s),
                Flavor = BandItPlus.Localization.Get("bp_kingbriefdata_069", "The gates are thrown open and a new banner climbs the keep. The old lord's colors are torn down and burned in the square below."),
                Stakes = BandItPlus.Localization.Get("bp_kingbriefdata_070", "It is theirs now — for as long as they can hold it."),
                KName = SafeSettlement(s),
                KType = cultureName ?? BandItPlus.Localization.Get("bp_kingbriefdata_071", "Bandits"),
                SoundId = "event:/ui/notification/settlement_owner_change",
                SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_072", "— THE SPOILS OF THE FALL —"),
                PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_073", "So it stands"),
                IsRebellionEvent = true,
                WhyText = BandItPlus.Localization.Get("bp_kingbriefdata_074", "It fell because no relief came in time — the walls were thin, the garrison thinner, and the will to die for a distant lord thinner still. Now a fresh garrison mans the ramparts and the storehouses feed new masters. Holding it will be the harder war; every kingdom nearby will want this prize back."),
                StorySectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_075", "— THE CITY FALLS —"),
                StoryText = BandItPlus.Localization.Get("bp_kingbriefdata_076", "The gate groans, then gives. Wood that stood a hundred winters splinters like a rotten tooth, and through the wound they come — the broken and the starving, blades bare, torches high. No trumpets. Only the low animal roar of men who have nothing left to lose, and a king in the wild who taught them there was glory in the taking. By the well a merchant kneels in his own spilled goods. Vaykos spares only the friends of the outlaws, and this town had none.\n\nWatch the keep. See the old colors torn down and trampled into the mud, and in their place a stolen banner climbing rope by rope against the wind. One more throne rendered to ash, as he swore. Somewhere in the smoke, laughing, the King of Thieves counts the world he means to unmake — hearth by hearth, until only ash answers to him."),
            };
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_077", "CONQUEST"), GOLD);
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_078", "BLACK BANNER"), CRIMSON);
            d.Row("SPPerks\\OneHandedShieldWall", BandItPlus.Localization.Get("bp_kingbriefdata_079", "Settlement"), SafeSettlement(s), CRIMSON);
            d.Row("SPPerks\\LeadershipMakeADifference", BandItPlus.Localization.Get("bp_kingbriefdata_080", "Seized by"), cultureName ?? BandItPlus.Localization.Get("bp_kingbriefdata_081", "Bandits"), GOLD);
            d.Row("SPPerks\\OneHandedShieldBash", BandItPlus.Localization.Get("bp_kingbriefdata_082", "Garrison raised"), new TaleWorlds.Localization.TextObject("{=bp_kbunit_troops}+{N} troops").SetTextVariable("N", addedGarrison).ToString(), GREEN);
            d.Row("SPPerks\\StewardAgrarian", BandItPlus.Localization.Get("bp_kingbriefdata_083", "Prosperity"), SafeProsperity(s), AMBER);
            return d;
        }

        public static BanditKingBriefData ForCrushed(Settlement s, Clan retaker)
        {
            var d = new BanditKingBriefData
            {
                ArtSprite = "rebellion_defeat",
                PortraitHero = SafeLeader(retaker),
                BannerClan = retaker,
                Title = BandItPlus.Localization.Get("bp_kingbriefdata_084", "The Rebellion Crushed"),
                Subline = SafeSettlement(s),
                Flavor = BandItPlus.Localization.Get("bp_kingbriefdata_085", "Order returns at the point of a sword. The uprising is scattered to the wind, and the gallows in the square are not left empty."),
                Stakes = BandItPlus.Localization.Get("bp_kingbriefdata_086", "The banner of a kingdom flies over the walls once more."),
                KName = SafeClanName(retaker),
                KType = BandItPlus.Localization.Get("bp_kingbriefdata_087", "Retaken"),
                SoundId = "event:/ui/notification/peace",
                SectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_088", "— THE RECKONING —"),
                PrimaryText = BandItPlus.Localization.Get("bp_kingbriefdata_089", "It is done"),
                IsRebellionEvent = true,
                WhyText = BandItPlus.Localization.Get("bp_kingbriefdata_090", "It ended because a kingdom finally spared the men to end it. The rebels held a town but never an army; against disciplined ranks and siege-craft, ardor was not enough. The crown reclaims its walls — but the wilds still whisper the mad king's name, and where one fire is stamped out, another will catch."),
                StorySectionLabel = BandItPlus.Localization.Get("bp_kingbriefdata_091", "— THE RISING IS CRUSHED —"),
                StoryText = BandItPlus.Localization.Get("bp_kingbriefdata_092", "They came at first light with iron on their breath. Lances lowered, the crown's cavalry broke the rebel line the way a hammer breaks a clay pot. The stolen banner Vaykos raised above the ash went down into the mud, and horses walked over it, and no one thought to lift it again. The desperate who followed the false king carry away nothing but wounds and a promise turned to smoke.\n\nAnd the throne stands. Torchlight climbs the old stone once more, and the crown Vaykos coveted sits where it has always sat, warm with another man's brow. Order restored — that grey, ordinary word — laid back over Calradia like a shroud. For now. The wilds are wide, the broken remember who broke them, and in the dark a crownless madman still whispers to the ash. A rising is not a war. It is only the first cut."),
            };
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_093", "ORDER RESTORED"), BLUE);
            d.Tag(BandItPlus.Localization.Get("bp_kingbriefdata_094", "RISING BROKEN"), ASH);
            d.Row("SPPerks\\OneHandedShieldWall", BandItPlus.Localization.Get("bp_kingbriefdata_095", "Settlement"), SafeSettlement(s), CRIMSON);
            d.Row("SPPerks\\StewardMasterOfWarcraft", BandItPlus.Localization.Get("bp_kingbriefdata_096", "Retaken by"), SafeClanName(retaker), GOLD);
            d.Row("SPPerks\\CharmParade", BandItPlus.Localization.Get("bp_kingbriefdata_097", "Restored to"), SafeCulture(retaker), TEAL);
            d.Row("SPPerks\\RogueryNoRestForTheWicked", BandItPlus.Localization.Get("bp_kingbriefdata_098", "The rebels"), BandItPlus.Localization.Get("bp_kingbriefdata_099", "scattered"), ASH);
            return d;
        }

        // ---- guarded live-data readers --------------------------------------
        private static Hero SafeLeader(Clan c) { try { return c?.Leader; } catch { return null; } }
        private static string SafeName(Clan c) { try { return c?.Name?.ToString() ?? ""; } catch { return ""; } }
        private static string SafeClanName(Clan c) { try { return c?.Name?.ToString() ?? "—"; } catch { return "—"; } }
        private static string SafeLeaderName(Clan c) { try { return c?.Leader?.Name?.ToString() ?? SafeClanName(c); } catch { return "—"; } }
        private static string SafeSettlement(Settlement s) { try { return s?.Name?.ToString() ?? "—"; } catch { return "—"; } }
        private static Clan SafeOwner(Settlement s) { try { return s?.OwnerClan; } catch { return null; } }
        private static string SafeMen(MobileParty p) { try { return new TaleWorlds.Localization.TextObject("{=bp_kbunit_men}{N} men").SetTextVariable("N", p?.MemberRoster?.TotalManCount ?? 0).ToString(); } catch { return "—"; } }

        // ---- POLISH: richer live reads (all null-guarded, never throw) ----------
        private static string SafeCulture(Clan c)
        {
            try { return c?.Culture?.Name?.ToString() ?? c?.Culture?.GetName()?.ToString() ?? "—"; }
            catch { try { return c?.Culture?.Name?.ToString() ?? "—"; } catch { return "—"; } }
        }

        private static string SafeGold(Clan c)
        {
            try { return (c?.Leader?.Gold ?? 0).ToString("N0") + " denars"; }
            catch { return "—"; }
        }

        // POLISH (2026-07-01): the raw denar count (e.g. "10,001,052 denars") read
        // ugly in the War-chest row, so this returns an evocative magnitude tier
        // instead. Same param type + null-guarding as SafeGold; reads the same gold.
        private static string SafeGoldTier(Clan c)
        {
            try
            {
                int gold = c?.Leader?.Gold ?? 0;
                if (gold >= 1000000) return BandItPlus.Localization.Get("bp_kingbriefdata_100", "A warlord's hoard");
                if (gold >= 250000)  return BandItPlus.Localization.Get("bp_kingbriefdata_101", "A king's ransom");
                if (gold >= 50000)   return BandItPlus.Localization.Get("bp_kingbriefdata_102", "Heavy coffers");
                if (gold >= 10000)   return BandItPlus.Localization.Get("bp_kingbriefdata_103", "A raider's purse");
                return BandItPlus.Localization.Get("bp_kingbriefdata_104", "Lean and hungry");
            }
            catch { return BandItPlus.Localization.Get("bp_kingbriefdata_105", "Unknown"); }
        }

        // The king's seat of power: the first settlement the clan holds, else the
        // hideout / home settlement of the leader, else the clan-name fallback.
        private static string SafeHome(Clan c)
        {
            try
            {
                if (c == null) return "—";
                var settlements = c.Settlements;
                if (settlements != null)
                    foreach (var s in settlements)
                        if (s != null) return s.Name?.ToString() ?? "—";
                var home = c.Leader?.HomeSettlement ?? c.HomeSettlement;
                if (home != null) return home.Name?.ToString() ?? "—";
                return BandItPlus.Localization.Get("bp_kingbriefdata_106", "the wilds");
            }
            catch { return BandItPlus.Localization.Get("bp_kingbriefdata_107", "the wilds"); }
        }

        private static string SafeYearsHidden(Clan c)
        {
            try
            {
                var founded = c?.LastFactionChangeTime ?? CampaignTime.Now;
                double years = (CampaignTime.Now.ToDays - founded.ToDays) / TaleWorlds.CampaignSystem.CampaignTime.DaysInYear;
                if (years < 0.0) years = 0.0;
                return Math.Round(years, 1) + " years";
            }
            catch { return "—"; }
        }

        private static string SafeSettlementOwner(Settlement s)
        {
            try { return s?.OwnerClan?.Name?.ToString() ?? BandItPlus.Localization.Get("bp_kingbriefdata_108", "the crown's own"); }
            catch { return "—"; }
        }

        private static string SafeProsperity(Settlement s)
        {
            try
            {
                if (s?.Town != null) return Math.Round(s.Town.Prosperity, 0).ToString("N0");
                if (s?.Village != null) return Math.Round(s.Village.Hearth, 0).ToString("N0") + " hearths";
                return "—";
            }
            catch { return "—"; }
        }

        private static string SafeLieutenants(Clan c)
        {
            try
            {
                int n = c?.WarPartyComponents?.Count ?? 0;
                if (n > 0) n -= 1; // exclude the king's own party
                return new TaleWorlds.Localization.TextObject("{=bp_kbunit_lieutenants}{N} lieutenants").SetTextVariable("N", n).ToString();
            }
            catch { return "—"; }
        }

        private static string SafeGarrison(Settlement s)
        {
            try { return new TaleWorlds.Localization.TextObject("{=bp_kbunit_defenders}{N} defenders").SetTextVariable("N", s?.Town?.GarrisonParty?.MemberRoster?.TotalManCount ?? 0).ToString(); }
            catch { return "—"; }
        }

        private static string SafeLoyalty(Settlement s)
        {
            try { return s?.Town != null ? Math.Round(s.Town.Loyalty, 0) + " / 100" : "—"; }
            catch { return "—"; }
        }

        private static string SafeEta(double captureAtHours)
        {
            try
            {
                double days = (captureAtHours - CampaignTime.Now.ToHours) / 24.0;
                if (days < 0.0) days = 0.0;
                return new TaleWorlds.Localization.TextObject("{=bp_kbunit_days}{N} days").SetTextVariable("N", Math.Round(days, 1).ToString()).ToString();
            }
            catch { return "—"; }
        }
    }

    // Main-thread-safe entry point. Builds the VM and opens the panel.
    //
    // STEP A (2026-07-01): the Mad King / rebellion events fire on Harmony/campaign
    // background threads (HourlyTick, MapEventEnded, Sack). A Gauntlet layer MUST be
    // pushed on the main thread, so Show() ENQUEUES the actual open onto
    // SubModuleClassEntry.EnqueueMainThread — the SAME queue drained every frame in
    // OnApplicationTick that the siege + popups already use. Never opens inline.
    public static class BanditKingBriefManager
    {
        public static void Show(BanditKingBriefData data)
        {
            try
            {
                if (data == null) return;

                // Marshal the entire decision (toggle read + toast-vs-panel + open)
                // onto the guaranteed-main-thread engine tick. Building the VM and
                // pushing the layer must never happen on a background thread.
                BandItPlus.SubModuleClassEntry.EnqueueMainThread(() => ShowOnMainThread(data));
            }
            catch (Exception ex)
            {
                try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-KingBrief] Show enqueue fail: " + ex.GetType().Name + ": " + ex.Message); } catch { }
            }
        }

        // Runs on the main thread (drained from OnApplicationTick).
        private static void ShowOnMainThread(BanditKingBriefData data)
        {
            try
            {
                if (data == null) return;

                // Honor the MCM master toggle (plain console prop; defaults true).
                bool show = true;
                try { show = MCMSettings.Instance == null || MCMSettings.Instance.ShowMadKingPanels; } catch { }
                if (!show)
                {
                    try { TaleWorlds.Library.InformationManager.DisplayMessage(
                        new TaleWorlds.Library.InformationMessage(data.Title ?? BandItPlus.Localization.Get("bp_kingbriefdata_109", "Bandit King"))); } catch { }
                    return;
                }

                // STEP D — rebellion trio (Rise/Fall/Crush) → passive toast when
                // RebellionPanelsAsToasts is on. Rises/Offer/Marches always full panels.
                bool asToast = false;
                try { asToast = MCMSettings.Instance != null && MCMSettings.Instance.RebellionPanelsAsToasts; } catch { }
                if (asToast && data.IsRebellionEvent)
                {
                    try
                    {
                        BandItPlus.UI.BanditToastScreen.Show(
                            data.Title ?? BandItPlus.Localization.Get("bp_kingbriefdata_110", "Bandit Plus"),
                            ToastBody(data));
                    }
                    catch (Exception tEx)
                    {
                        try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-KingBrief] toast fallback fail: " + tEx.Message); } catch { }
                    }
                    return;
                }

                BanditKingBriefScreen.Open(data);
            }
            catch (Exception ex)
            {
                try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-KingBrief] Show fail: " + ex.GetType().Name + ": " + ex.Message); } catch { }
            }
        }

        // One-line summary for the toast fallback: prefer the subline (settlement /
        // clan name), else the flavor's first clause.
        private static string ToastBody(BanditKingBriefData data)
        {
            try
            {
                if (!string.IsNullOrEmpty(data.Subline)) return data.Subline;
                if (!string.IsNullOrEmpty(data.Stakes)) return data.Stakes;
                return data.Flavor ?? "";
            }
            catch { return ""; }
        }
    }
}
