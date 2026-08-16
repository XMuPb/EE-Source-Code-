using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace EditableEncyclopedia.ChronicleNoters
{
    /// <summary>One label/value pair in the right pane's INFO grid.</summary>
    public class ChronicleInfoRowVM : ViewModel
    {
        // ── Label → sprite lookup ─────────────────────────────────────────────────────
        // ONLY sprite names verified to exist in the shipped game are listed here. A sprite
        // name that does not resolve renders as a BLANK SQUARE with no error and no log line,
        // so never add a name here without first finding it in a shipped prefab under
        // Modules\*\GUI\. Every name below passed ALL THREE gates:
        //   1. present as a <Name> entry in Native\GUI\NativeSpriteData.xml;
        //   2. <CategoryName>ui_group1</CategoryName> in that same entry. THIS GATE IS THE
        //      ONE THAT BITES. Bannerlord loads sprite sheets BY CATEGORY, so a sprite that
        //      exists, is icon-sized and is drawn by a shipped prefab STILL renders as a
        //      blank square if its category is not loaded on the current screen. Every icon
        //      that renders correctly in this panel is ui_group1; two that were not
        //      (Barter\War -> ui_barter, Encyclopedia\star_outline -> ui_encyclopedia) were
        //      invisible in-game for months and were replaced on 2026-08-02. There is NO star
        //      glyph anywhere in ui_group1 — the only star_* sprites the game ships live in
        //      ui_encyclopedia and ui_mpmission, so do not go looking for one again.
        //   3. drawn by a shipped prefab under Modules\*\GUI\Prefabs\ (directly via Sprite="",
        //      or through a Brush= whose brush layer names it) at ICON scale — every observed
        //      widget size is between 12 and 56 px. Sprites that vanilla only ever renders at
        //      200 px+ are illustrations and would smear in the 16x16 slot, so they are out:
        //      Encyclopedia\clan | kingdom | settlements (223x514 page art, never in a prefab),
        //      encounter_caravan (445x805), General\CharacterCreation\culture_flag_small
        //      (163x349), Encyclopedia\subpage_notable_portrait (drawn at 212x400 and larger).
        //      Also out because no shipped prefab uses them at all — they are only reachable
        //      through the code-applied Clan.Lord.Status brush: SPGeneral\Clan\Status\Married
        //      and ...\icon_ischild, plus General\TroopTierIcons\icon_tier_1.
        // "@2x" is part of the sprite NAME, not a scale modifier — do not strip it.
        //
        // Keys are the exact label strings EditableEncyclopediaApi.Get*InfoStatsOrdered emits
        // (HeroInfoStats.cs and EditableEncyclopediaAPI.cs) — 57 distinct labels in total.
        // OrdinalIgnoreCase + exact key match, never substring, so a future "Wealth Rank" row
        // does NOT inherit the coin.
        // Any label not listed here gets no icon at all — a row is allowed to have none.
        // Deliberately iconless: "Occupation". Its values span Lord / Merchant / Artisan /
        // Gang Leader / Wanderer / Headman, and ui_group1 has no generic "profession" glyph.
        // The two nearest candidates were both rejected on 2026-08-02: General\Mission\noble
        // is a CROWN (it would collide with leader_crown_icon on "Ruler"/"Leader" and is
        // simply wrong for every non-noble), and SPGeneral\GeneralFlagIcons\civillian is a
        // group of three peasants, which reads as "commoners", not "occupation".
        private static readonly Dictionary<string, string> LabelToSprite =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ── Money ────────────────────────────────────────────────────────────────
                { "Wealth",          "General\\Icons\\Coin@2x" },
                { "Daily Income",    "General\\Icons\\Coin@2x" },
                // Vanilla's own "tax" stat icon in ProfitStatItem.xml, drawn at 40x40.
                { "Trade Tax",       "SPGeneral\\MapOverlay\\Settlement\\icon_tax_big" },

                { "Influence",       "General\\Icons\\Influence@2x" },

                // ── People under arms ────────────────────────────────────────────────────
                { "Troops",          "General\\Icons\\Party@2x" },
                { "Total Troops",    "General\\Icons\\Party@2x" },   // kingdom-page spelling
                { "Mercenaries",     "General\\Icons\\Party@2x" },
                { "Companions",      "General\\Icons\\Party@2x" },
                { "Parties",         "General\\Icons\\Party@2x" },

                { "Morale",          "General\\Icons\\Morale@2x" },

                { "Garrison",        "General\\Icons\\Garrison" },   // settlement-page spelling
                { "Garrisons",       "General\\Icons\\Garrison" },
                { "Total Garrisons", "General\\Icons\\Garrison" },   // kingdom-page spelling

                { "Lords",           "General\\Icons\\Army" },
                { "Militia",         "General\\Icons\\Militia" },

                // ── Fiefs ────────────────────────────────────────────────────────────────
                { "Towns",           "General\\Icons\\Walls" },
                { "Castles",         "General\\Icons\\Walls" },
                { "Wall Level",      "General\\Icons\\Walls" },
                { "Holdings",        "General\\Icons\\Walls" },
                { "Bound To",        "General\\Icons\\Walls" },      // village's parent town
                // Vanilla's village stat icon in ProfitStatItem.xml, drawn at 40x40.
                { "Villages",        "SPGeneral\\MapOverlay\\Settlement\\icon_village_big" },
                { "Bound Villages",  "SPGeneral\\MapOverlay\\Settlement\\icon_village_big" },

                { "Prosperity",      "General\\Icons\\Prosperity" },
                { "Hearths",         "General\\Icons\\Prosperity" }, // village-side prosperity
                { "Food",            "General\\Icons\\Food" },
                { "Loyalty",         "SPGeneral\\MapOverlay\\Settlement\\icon_loyalty" },
                { "Security",        "SPGeneral\\MapOverlay\\Settlement\\icon_security" },

                // TownManagement.xml pairs this with its production rows, drawn at 30x30.
                { "Produces",         "SPGeneral\\TownManagement\\production_icon" },
                { "Village Produces", "SPGeneral\\TownManagement\\production_icon" },
                { "Workshops",        "General\\Icons\\ShopIcons\\smithy_small" },
                // Trade icon from the game-menu option list (GameMenuItem.xml, 56x42).
                { "Caravans",         "SPGeneral\\GameMenu\\trade_icon" },
                // Vanilla's "Crime" stat icon (GameOverScreen.xml, 25x25) — alleys are the
                // criminal holdings, so the underworld glyph is the honest one here.
                { "Alleys",           "SPGeneral\\MapOverlay\\Settlement\\icon_crime" },

                // ── Named people ─────────────────────────────────────────────────────────
                { "Ruler",           "SPGeneral\\SPScoreboard\\leader_crown_icon" },
                { "Leader",          "SPGeneral\\SPScoreboard\\leader_crown_icon" },
                // Mission HUD's own-character icon (Mission.MainAgentHUD.HeroHealthBar.Icon,
                // drawn at 35x35) — a plain person glyph, so it carries every person-valued row.
                { "Owner",           "General\\Mission\\hero_icon" },
                { "Governor",        "General\\Mission\\hero_icon" },
                { "Spouse",          "General\\Mission\\hero_icon" },
                { "Children",        "General\\Mission\\hero_icon" },
                { "Notables",        "General\\Mission\\hero_icon" },
                { "Supporters",      "General\\Mission\\hero_icon" },

                // ── Banner-bearing bodies ────────────────────────────────────────────────
                // EquipmentType.Image brush banner glyph (EncyclopediaUnitPage.xml, 35x35).
                { "Clan",            "General\\EquipmentIcons\\equipment_type_banner" },
                { "Clans",           "General\\EquipmentIcons\\equipment_type_banner" },
                { "Kingdom",         "General\\EquipmentIcons\\equipment_type_banner" },
                { "Faction",         "General\\EquipmentIcons\\equipment_type_banner" },
                { "Culture",         "General\\EquipmentIcons\\equipment_type_banner" },

                // ── War record ───────────────────────────────────────────────────────────
                // 2026-08-02: was Barter\War (63x63) — a real sprite, but category ui_barter,
                // so it only loads on the barter screen and painted BLANK here. Replaced with
                // vanilla's own CROSSED-SWORDS "hostile action" game-menu icon (56x42,
                // ui_group1, GameMenu.Icon brush layer "HostileAction" in
                // Native\GUI\Brushes\GameMenu.xml:241, drawn by GameMenuItem.xml at 56x42 —
                // the exact size and family as SPGeneral\GameMenu\trade_icon on "Caravans"
                // above). Crossed swords is the most literal "at war" glyph in the category.
                { "At War",          "SPGeneral\\GameMenu\\hostileaction_icon" },
                { "At War With",     "SPGeneral\\GameMenu\\hostileaction_icon" },
                { "Active Wars",     "SPGeneral\\GameMenu\\hostileaction_icon" },
                // Sword glyph from EquipmentType.Image (EncyclopediaUnitPage.xml, 35x35).
                { "Battles",         "General\\EquipmentIcons\\equipment_type_one_handed" },
                // Vanilla's "Kill" stat icon on the game-over screen, drawn at 25x25.
                { "Kills",           "General\\Mission\\PersonalKillfeed\\kill_icon" },
                // Vanilla's "Tournament" stat icon on the same screen, drawn at 25x25.
                { "Tournaments",     "General\\Icons\\icon_tournament_square" },

                // ── Standing ─────────────────────────────────────────────────────────────
                // 2026-08-02: was Encyclopedia\star_outline (39x39) — category ui_encyclopedia,
                // so it painted BLANK on this map-screen panel, and ui_group1 ships NO star at
                // all. Replaced with the Order-of-Battle "high tier" filter chevron (36x35,
                // ui_group1, brush OrderOfBattle.Filter.HighTier in Native\GUI\Brushes\
                // Order.xml:906). An upward rank chevron is the honest read for both a renown
                // score and a hall ranking, and it is the same 36x35 General\EquipmentIcons\*
                // family already carrying "Clan"/"Kingdom" (banner) and "Battles" (sword).
                { "Renown",          "General\\EquipmentIcons\\oob_filter_high_tier" },
                { "Hall Rank",       "General\\EquipmentIcons\\oob_filter_high_tier" },
                // 2026-08-02: previously unmapped. This is the one true map-pin teardrop in
                // ui_group1 (32x45), drawn by Native\GUI\Prefabs\Mission\FormationMarker.xml:25
                // at 22x36 and by the NameMarker.Distance.Icon brush — icon scale, right
                // category, and it means "a place" and nothing else.
                { "Location",        "General\\Mission\\namemarker_distance" },
                // Parley/negotiation glyph off the party nameplate, drawn at 33x33.
                { "Relation",        "General\\Icons\\parley_icon" },
                // Status is derived from IsDead/IsWounded/IsPrisoner, so the health icon fits.
                { "Status",          "General\\Icons\\Health@2x" },
            };

        /// <summary>Sprite for a label, or empty when the label has no real matching icon.</summary>
        private static string SpriteForLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return string.Empty;
            string sprite;
            return LabelToSprite.TryGetValue(label.Trim(), out sprite) ? sprite : string.Empty;
        }

        private string _label = string.Empty;
        private string _value = string.Empty;
        private string _iconSprite = string.Empty;

        public ChronicleInfoRowVM(string label, string value)
        {
            _label = label ?? string.Empty;
            _value = value ?? string.Empty;
            _iconSprite = SpriteForLabel(_label);
        }

        [DataSourceProperty]
        public string Label
        {
            get { return _label; }
            set
            {
                if (_label != value)
                {
                    _label = value ?? string.Empty;
                    OnPropertyChanged("Label");
                    // Keep the icon in step with the label. The populator only ever constructs
                    // rows, but Label is publicly settable, so a stale sprite is possible without
                    // this and would paint the wrong icon rather than none.
                    IconSprite = SpriteForLabel(_label);
                }
            }
        }

        [DataSourceProperty]
        public string Value
        {
            get { return _value; }
            set { if (_value != value) { _value = value ?? string.Empty; OnPropertyChanged("Value"); } }
        }

        /// <summary>Sprite name for the small icon before the label; empty when the row has none.</summary>
        [DataSourceProperty]
        public string IconSprite
        {
            get { return _iconSprite; }
            set
            {
                if (_iconSprite != value)
                {
                    _iconSprite = value ?? string.Empty;
                    OnPropertyChanged("IconSprite");
                    OnPropertyChanged("HasIcon");
                }
            }
        }

        /// <summary>False for rows with no matching shipped icon — hides the icon wrapper entirely.</summary>
        [DataSourceProperty]
        public bool HasIcon { get { return !string.IsNullOrEmpty(_iconSprite); } }
    }
}
