using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;
using BandItPlus.HideoutVisit;

namespace BandItPlus.BanditRaid
{
    // Kingdom-less rebel-clan siege: a dedicated independent clan captures + holds a settlement.
    // No Kingdom is ever created, so the diplomacy/discontinuation crash surface (which the temp-kingdom
    // approach hit five different ways) never exists. The clan owns a fief => it is a valid map faction
    // that FactionDiscontinuation never destroys; its leader is non-null with seeded relations, so the
    // relation models never deref a null Leader. See spec 2026-06-29-bandit-siege-v2-rebel-clan-design.
    public static class BanditRebelSiegeHelper
    {
        private static FieldInfo _cultureIsBanditField; // BasicCultureObject.<IsBandit>k__BackingField
        private static bool _probed;

        private static void Log(string m) { try { HideoutPeacefulVisitState.Log("[BP-Siege] " + m); } catch { } }

        private static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try { _cultureIsBanditField = typeof(BasicCultureObject).GetField("<IsBandit>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic); }
            catch (Exception ex) { Log("RebelSiege probe threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // === Salvaged from BanditKingdomFactory: full non-bandit culture clone ===
        private static void CopyDeclaredFields(Type t, object src, object dst)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                try { f.SetValue(dst, f.GetValue(src)); } catch { }
            }
        }

        // FULL non-bandit copy of a bandit culture (every field, incl. feats — a partial copy NREs HasFeat in
        // the speed model) + IsBandit=false. Identity preserved (only BasicCultureObject/CultureObject fields).
        public static CultureObject CloneCultureNonBandit(CultureObject src)
        {
            EnsureProbed();
            try
            {
                if (src == null || _cultureIsBanditField == null) return null;
                var clone = MBObjectManager.Instance.CreateObject<CultureObject>("bp_rebel_culture_" + src.StringId);
                if (clone == null) return null;
                CopyDeclaredFields(typeof(BasicCultureObject), src, clone);
                CopyDeclaredFields(typeof(CultureObject), src, clone);
                _cultureIsBanditField.SetValue(clone, false); // not a bandit culture
                return clone;
            }
            catch (Exception ex) { Log("CloneCultureNonBandit threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // === Reflection plumbing ===
        private static object InvokeStatic(string typeName, string method, params object[] args)
        {
            var t = typeof(Clan).Assembly.GetType(typeName);
            if (t == null) { Log("type missing: " + typeName); return null; }
            foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == method && m.GetParameters().Length == args.Length)
                    return m.Invoke(null, args);
            Log("static method missing: " + typeName + "." + method + "/" + args.Length);
            return null;
        }

        private static void TrySet(object o, string prop, object val)
        {
            try
            {
                var p = o.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(o, val); return; }
                var f = o.GetType().GetField("<" + prop + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                f?.SetValue(o, val);
            }
            catch { }
        }

        private static void TrySetTier(Clan clan, int tier)
        {
            try { var f = typeof(Clan).GetField("_tier", BindingFlags.Instance | BindingFlags.NonPublic); f?.SetValue(clan, tier); } catch { }
        }

        // === Create the dedicated kingdom-less holdout clan + its synthesized chief ===
        public static Clan CreateHoldoutClan(CultureObject banditCulture, Settlement near, out Hero chief)
        {
            chief = null;
            Clan clan = null; // tracked so ANY failure path can destroy the partial clan (no zombie)
            try
            {
                if (banditCulture == null) { Log("CreateHoldoutClan: null culture"); return null; }
                var template = ResolveChiefTemplate(banditCulture);
                if (template == null) { Log("CreateHoldoutClan: no chief template"); return null; }

                clan = Clan.CreateClan("bp_rebel_clan_" + (near != null ? near.StringId : "x") + "_" + Clan.All.Count);
                if (clan == null) { Log("CreateHoldoutClan: CreateClan returned null"); return null; }

                // A fresh Clan.CreateClan has a NULL Name — set it IMMEDIATELY or the clan is malformed
                // (every getter that touches Name/encyclopedia/marriage tick NREs, and our own logs too).
                var nm = new TextObject("{=bp_hcr_019}{NAME} Holdout")
                    .SetTextVariable("NAME", banditCulture.Name != null ? banditCulture.Name.ToString() : BandItPlus.Localization.Get("bp_hcr_018", "Bandit"));
                try { clan.ChangeClanName(nm, nm); } catch (Exception e) { Log("ChangeClanName: " + e.Message); }

                // NATIVE: the clan adopts the TARGET settlement's REAL culture (as the engine's rebellion clans do),
                // so the native town economy (patrols/caravans/militia) finds real troop templates and never NREs.
                // The bandit identity lives in the name, banner, and the heroes' Mountain-Bandits culture — not here.
                // Fall back to a non-bandit clone of the bandit culture only if the target somehow has no culture.
                var realCulture = (near != null && near.Culture != null) ? near.Culture : CloneCultureNonBandit(banditCulture);
                if (realCulture != null) clan.Culture = realCulture;

                chief = CreateChiefHero(template, near, clan);
                if (chief == null) { Log("CreateHoldoutClan: chief creation failed — destroying partial clan"); SafeDestroyClan(clan); return null; }

                clan.SetLeader(chief);                    // sets clan._leader AND chief.Clan = clan
                // Fixed, iconic name for the recurring Bandit King (was a random culture name each siege).
                try { chief.SetName(new TextObject("{=bp_rebelsiegehelper_001}Vaykos the Bandit King"), new TextObject("{=bp_rebelsiegehelper_002}Vaykos")); } catch (Exception e) { Log("SetName: " + e.Message); }
                SetHoldoutBanner(clan);                   // proper sigil banner (EnsureBanner gave a plain one)
                TrySet(clan, "IsNoble", true);
                TrySetTier(clan, 6);                      // a powerful Tier-6 bandit holdout
                try { GiveGoldAction.ApplyBetweenCharacters(null, chief, 10000000, true); } catch (Exception e) { Log("gold: " + e.Message); } // maxed wealth
                try { chief.ChangeState(Hero.CharacterStates.Active); } catch (Exception e) { Log("active: " + e.Message); }

                // Diplomacy-safety: neutral relation with every kingdom leader so relation models never deref
                // unexpected nulls.
                foreach (var k in Kingdom.All)
                    if (k != null && k.Leader != null) { try { chief.SetPersonalRelation(k.Leader, 0); } catch { } }
                // The holdout chief is the bandit "king": allied (+100) with every bandit chief — fills the
                // encyclopedia Friends list and keeps the looter/bandit factions friendly to him.
                foreach (var c in Clan.All)
                    if (c != null && c.IsBanditFaction && c.Leader != null && c.Leader != chief)
                        { try { chief.SetPersonalRelation(c.Leader, 100); } catch { } }

                MaxOutChief(chief); // formidable bandit king: maxed attributes, skills, focus, and perks
                PolishChief(chief, banditCulture); // culture display, mark-known, bandit-chief Friends, encyclopedia backstory
                SetHomeSettlement(chief, near);     // Home = the settlement he is taking (else encyclopedia shows "Home: none")
                DeclareWarOnAllKingdoms(clan);      // at war with EVERY realm immediately (fills the Wars list), but never bandits

                // OnClanCreated is an INSTANCE method on CampaignEventDispatcher.Instance (not static) — fire it
                // so behaviors/caches register the new clan.
                InvokeDispatcher("OnClanCreated", clan);

                Log("holdout clan created '" + clan.StringId + "' name=" + SafeName(clan)
                    + " chief=" + SafeName(chief) + " leader=" + (clan.Leader != null ? SafeName(clan.Leader) : "NULL"));
                return clan;
            }
            catch (Exception ex)
            {
                Log("CreateHoldoutClan threw " + ex.GetType().Name + ": " + ex.Message + " — destroying partial clan");
                SafeDestroyClan(clan);
                chief = null;
                return null;
            }
        }

        // Null-safe name read (Hero or Clan). A fresh clan / odd hero can have a null Name.
        private static string SafeName(object o)
        {
            try { var p = o?.GetType().GetProperty("Name"); var v = p?.GetValue(o); return v != null ? v.ToString() : "?"; }
            catch { return "?"; }
        }

        // Destroy a partially-created clan so it never lingers as a zombie that crashes the daily ticks.
        private static void SafeDestroyClan(Clan clan)
        {
            try
            {
                if (clan == null || clan.IsEliminated) return;
                var m = typeof(DestroyClanAction).GetMethod("Apply", BindingFlags.Static | BindingFlags.Public);
                m?.Invoke(null, new object[] { clan });
            }
            catch (Exception ex) { Log("SafeDestroyClan: " + ex.Message); }
        }

        // Invoke an instance method on CampaignEventDispatcher.Instance (signature-robust over arity).
        private static void InvokeDispatcher(string method, params object[] leading)
        {
            try
            {
                var disp = typeof(Clan).Assembly.GetType("TaleWorlds.CampaignSystem.CampaignEventDispatcher");
                var inst = disp?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (disp == null || inst == null) { Log("CampaignEventDispatcher.Instance missing"); return; }
                foreach (var m in disp.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != method) continue;
                    var ps = m.GetParameters();
                    if (ps.Length < leading.Length) continue;
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        args[i] = i < leading.Length ? leading[i]
                                : ps[i].HasDefaultValue ? ps[i].DefaultValue
                                : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    m.Invoke(inst, args);
                    return;
                }
                Log("dispatcher method missing: " + method);
            }
            catch (Exception ex) { Log("InvokeDispatcher " + method + " threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Vanilla notable templates are IsHero=true + properly clothed, so the encyclopedia indexes the chief as
        // a proper Hero (portrait + age), not a silhouette. Gang leaders are bandit-flavored; merchant_empire is
        // the safe fallback. PROVEN by BanditChiefRegistry (which uses this exact chain) — culture.BanditBoss is
        // NOT a hero template and renders incompletely.
        private static CharacterObject ResolveChiefTemplate(CultureObject culture)
        {
            var mgr = MBObjectManager.Instance;
            return mgr.GetObject<CharacterObject>("gangleader_aserai")
                ?? mgr.GetObject<CharacterObject>("gangleader_empire")
                ?? mgr.GetObject<CharacterObject>("merchant_empire")
                ?? (culture != null ? culture.BanditBoss : null);
        }

        // HeroCreator.CreateSpecialHero(template, bornSettlement, faction, supporterOfClan, age). Mirrors the
        // proven BanditChiefRegistry call: 1st Clan = our clan (faction), 2nd Clan = null (supporterOf), int = age.
        private static Hero CreateChiefHero(CharacterObject template, Settlement near, Clan clan)
        {
            try
            {
                var t = typeof(Campaign).Assembly.GetType("TaleWorlds.CampaignSystem.HeroCreator");
                if (t == null) { Log("HeroCreator type missing"); return null; }
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "CreateSpecialHero") continue;
                    var ps = m.GetParameters();
                    var args = new object[ps.Length];
                    bool clanFilled = false;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        if (pt == typeof(CharacterObject)) args[i] = template;
                        else if (pt == typeof(Settlement)) args[i] = near;
                        else if (pt == typeof(Clan)) { args[i] = clanFilled ? null : clan; clanFilled = true; } // faction, then supporterOf=null
                        else if (pt == typeof(int)) args[i] = 45; // age — real birthday => encyclopedia shows an age, not "???"
                        else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                        else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                        else args[i] = null;
                    }
                    var h = m.Invoke(null, args) as Hero;
                    if (h != null) return h;
                }
                Log("CreateSpecialHero: no usable overload");
            }
            catch (Exception ex) { Log("CreateChiefHero threw " + ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            return null;
        }

        // Make the bandit king formidable: max attributes (10 each), every skill (huge XP) + full focus, and
        // best-effort all perks. Uses the proven HeroDeveloper API (AddAttribute/AddSkillXp/AddFocus); perks
        // via reflection since AddPerk's presence varies by engine version. All defensive — any miss is logged.
        private static void MaxOutChief(Hero chief)
        {
            try
            {
                var dev = chief?.HeroDeveloper;
                if (dev == null) { Log("MaxOutChief: no HeroDeveloper"); return; }
                var mgr = MBObjectManager.Instance;
                try { foreach (var a in mgr.GetObjectTypeList<CharacterAttribute>()) { try { dev.AddAttribute(a, 10, false); } catch { } } } catch { }
                try { foreach (var s in mgr.GetObjectTypeList<SkillObject>()) { try { dev.AddSkillXp(s, 1000000f); } catch { } try { dev.AddFocus(s, 5, false); } catch { } } } catch { }
                var addPerk = typeof(HeroDeveloper).GetMethod("AddPerk", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (addPerk != null)
                    try { foreach (var p in mgr.GetObjectTypeList<PerkObject>()) { try { addPerk.Invoke(dev, new object[] { p }); } catch { } } } catch { }
                Log("maxed bandit king: attributes/skills/focus/perks");
            }
            catch (Exception ex) { Log("MaxOutChief threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // A proper sigil'd clan banner (EnsureBanner copied a plain one; a vassal's banner also gets recolored
        // to the liege's). Deterministic seed from the clan id so the same holdout keeps the same banner.
        private static void SetHoldoutBanner(Clan clan)
        {
            try
            {
                if (clan == null) return;
                clan.Banner = Banner.CreateRandomClanBanner(clan.StringId != null ? clan.StringId.GetHashCode() : 12345);
            }
            catch (Exception ex) { Log("SetHoldoutBanner threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // After capture: turn the holdout into a PERMANENT INDEPENDENT BANDIT faction. Setting IsBanditFaction
        // removes it from the diplomacy AI so kingdoms never recruit it as a vassal/mercenary (the root cause of
        // "Vassal of the Western Empire") and it stops inheriting a liege's wars. Must run AFTER ApplyBySiege
        // (which excludes bandit clans). Also leaves any kingdom it may already have joined, and sets Tier 6.
        public static void MakeIndependentBanditHoldout(Clan clan)
        {
            try
            {
                if (clan == null) return;
                if (clan.Kingdom != null)
                {
                    try
                    {
                        foreach (var m in typeof(ChangeKingdomAction).GetMethods(BindingFlags.Static | BindingFlags.Public))
                        {
                            if (m.Name != "ApplyByLeaveKingdom") continue;
                            var ps = m.GetParameters(); var args = new object[ps.Length]; args[0] = clan;
                            for (int i = 1; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                            m.Invoke(null, args); break;
                        }
                    }
                    catch (Exception e) { Log("leave kingdom: " + e.Message); }
                }
                // DO NOT set IsBanditFaction=true: BanditSpawnCampaignBehavior.GetInfestedHideoutCount(clan)
                // looks the clan up in its hideout-count dictionary and throws KeyNotFoundException for our
                // unregistered clan (crash every hourly tick). Stay a NON-bandit independent faction; the
                // SiegeKingdomJoinBlockPatch keeps kingdoms from recruiting it as a vassal, and the
                // ClanNavalCapabilityPatch covers the marriage tick. Thematic "bandit" comes from name/banner/
                // culture clone + the bandit-allied King, not the engine flag.
                try { typeof(Clan).GetField("_tier", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, 6); } catch { }
                DeclareWarOnAllKingdoms(clan); // at war with every realm, but never with other bandits (Friends)
                Log("holdout: independent NON-bandit faction (Tier 6), kingdom=" + (clan.Kingdom != null ? clan.Kingdom.Name.ToString() : "none"));
            }
            catch (Exception ex) { Log("MakeIndependentBanditHoldout threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Mirror BanditChiefRegistry's hero polish so the holdout chief renders fully in the encyclopedia:
        // override the culture (the gangleader/merchant template gives "Empire"), mark him KNOWN (else portrait/
        // age/relation show "???"), befriend every bandit chief, and write a rich backstory for BOTH the chief
        // and his faction.
        private static void PolishChief(Hero chief, CultureObject banditCulture)
        {
            if (chief == null) return;
            try { SetHeroCulture(chief, banditCulture); } catch (Exception e) { Log("culture: " + e.Message); }
            try { chief.SetHasMet(); } catch { }
            try { chief.IsKnownToPlayer = true; } catch { }
            try { chief.ChangeState(Hero.CharacterStates.Active); } catch { }
            try { SeedBanditFriends(chief); } catch (Exception e) { Log("friends: " + e.Message); }
            try { EquipChief(chief); } catch (Exception e) { Log("equip: " + e.Message); }
            try { chief.EncyclopediaText = new TextObject("{=*}" + BuildChiefBackstory()); } catch (Exception e) { Log("chief encyclopedia: " + e.Message); }
            try { if (chief.Clan != null) SetClanEncyclopediaText(chief.Clan, BuildClanBackstory()); } catch (Exception e) { Log("clan encyclopedia: " + e.Message); }
        }

        // Reflection-set the culture on the hero + its CharacterObject (the template's Empire culture otherwise
        // shows). Mirrors BanditChiefRegistry's at-create culture write.
        private static void SetHeroCulture(Hero hero, CultureObject culture)
        {
            if (hero == null || culture == null) return;
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
            for (var t = hero.GetType(); t != null; t = t.BaseType)
                foreach (var f in t.GetFields(BF))
                    if (f.FieldType == typeof(BasicCultureObject) || f.FieldType == typeof(CultureObject))
                        try { f.SetValue(hero, culture); } catch { }
            if (hero.CharacterObject != null)
                for (var t = hero.CharacterObject.GetType(); t != null; t = t.BaseType)
                    foreach (var f in t.GetFields(BF))
                        if (f.FieldType == typeof(BasicCultureObject) || f.FieldType == typeof(CultureObject))
                            try { f.SetValue(hero.CharacterObject, culture); } catch { }
        }

        // +100 relation with every BandIt Plus bandit chief — their clans are IsBanditFaction=false, so a flag
        // filter misses them; enumerate the registry instead. Fills the encyclopedia Friends list.
        private static void SeedBanditFriends(Hero chief)
        {
            var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
            if (reg == null) return;
            foreach (var other in reg.GetAllChiefHeroes())
                if (other != null && other != chief && other.IsAlive)
                    try { chief.SetPersonalRelation(other, 100); } catch { }
        }

        private static void SetClanEncyclopediaText(Clan clan, string text)
        {
            try { typeof(Clan).GetField("<EncyclopediaText>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(clan, new TextObject("{=*}" + text)); }
            catch (Exception ex) { Log("SetClanEncyclopediaText threw " + ex.Message); }
        }

        private static string BuildChiefBackstory()
        {
            // {CHIEFS} is replaced with the live roster BEFORE wrapping in TextObject (TextObject treats {...}
            // as a variable, so the token must be gone — and no other braces appear in the prose).
            var __t = new TextObject("{=bp_kinglore_001}" + CHIEF_LORE);
            __t.SetTextVariable("CHIEFS", BuildChiefList());
            return __t.ToString();
        }

        // Live roster of the bandit chieftains who serve the King — pulled from the registry at capture time.
        private static string BuildChiefList()
        {
            var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
            if (reg == null) return BandItPlus.Localization.Get("bp_rebelsiegehelper_003", "outlaw chiefs beyond the counting.");
            string chiefs = ""; bool first = true; int n = 0;
            foreach (var kv in reg.GetAllChiefByCulture())
            {
                var c = kv.Value; if (c == null || c.Name == null) continue;
                string cul = kv.Key != null ? BandItPlus.Origins.BanditOriginBehavior.CultureDisplay(kv.Key) : BandItPlus.Localization.Get("bp_hcr_020", "the wilds");
                chiefs += (first ? "" : ", ") + new TextObject("{=bp_hcr_021}{NAME} of the {CULTURE}").SetTextVariable("NAME", c.Name).SetTextVariable("CULTURE", cul).ToString(); first = false; n++;
            }
            return n > 0 ? chiefs + "." : BandItPlus.Localization.Get("bp_rebelsiegehelper_003", "outlaw chiefs beyond the counting.");
        }

        private static string BuildClanBackstory() { return BandItPlus.Localization.Get("bp_kinglore_002", FACTION_LORE); }

        private const string CHIEF_LORE =
            "No chronicler agrees on where Vaykos was whelped, only that it was somewhere the gutters ran down into the gallows-shadow and froze there in winter, black and stinking. A thief's son, they say, born in the wet straw behind a tannery in some town that has since forgotten its own name, the air thick with lye and the green reek of soaking hides. His father swung from a crossroads gibbet before the boy could shape a word, and the rope that took the father is the only inheritance the son ever owned. He learned his letters from the wanted writs nailed to tavern doors, sounding out his own dead name in the candlelight, and he learned his trade from the very men who had driven those nails. By the time the first dark hair came in along his jaw he had killed for bread, for a pair of boots that fit, and once — men lower their voices to say it — for nothing but the slow pleasure of watching a richer man come to understand that all his coin could not buy back the breath in his throat.\n \n"
          + "The petty kings of the wilds never saw him coming, for there was nothing to see. He was one knife among ten thousand, a face in the smoke of a hundred campfires that all guttered and stank alike, indistinguishable as wet ash. He chose the strongest chieftain first, the one called Black Drennach who held the high passes and took toll of everything that crawled through them, and he did not meet that man in battle. He met him at supper. He came smiling, with empty hands and a gift of wine, and in the grey before dawn Drennach's own sworn men carried the body out across the threshold and knelt in the bloodied rushes to Vaykos before the corpse had cooled. So it went, year upon bloody year — a feast, a wedding, a parley sworn beneath the truce-oath and the open sky — and then another band swallowed whole, its banner fed to the fire, its hard sons bound and broken to a stranger's purpose. He did not defeat the outlaw chieftains of Calradia. He digested them, the way a winter swallows a column of marching men and gives back only the boots.\n \n"
          + "He rules now by three things, and the chroniclers set them down in the order a wise man learns to fear them. First the knife — the blade that reaches every rutted road, every granary, every river-gate, so that no caravan fords a stream without his leave and no headman in the marches sleeps the night through who has not paid his portion. Second the debt — for Vaykos lends before ever he takes, open-handed and patient, and a man who owes him is a man already conquered, owned down to the marrow of him before a single sword leaves its sheath. And third the fear, which is the cheapest of his weapons and the dearest, for it costs him nothing and buys him everything: a village that has done no more than hear his name spoken will leave its grain stacked at the treeline unasked, the way the old folk leave meat at the forest's edge for the wolf, so that the wolf, fed, will not come in the night for the children.\n \n"
          + "They have tried to take him. Margraves have ridden out with bright banners snapping and brighter intent, columns of mailed riders certain of their quarry, and they have found cold ash where his throne should stand and crows quarreling over the leavings, for his throne has no capital and never did. There are no walls to set an engine against, no high keep to ring and starve through a hungry spring, no painted court where a clever ambassador might unfold his bribes and his subtle poisons. To take Vaykos you must first take a thousand blades in a thousand shadows, and on the very night you think at last you have run him to ground, each of those thousand is whetting its edge by a different fire in a different forest, a hundred leagues apart. He is everywhere his men are, and his men are everywhere; cut down one and you have cut off a finger, and the hand does not so much as close.\n \n"
          + "These are the chieftains who have bent the knee and worn his collar of obligation, each a king of cutthroats in his own right, each now no more than a single tooth set in the wider jaw:\n \n"
          + "{CHIEFS}\n \n"
          + "Lords speak his name in council the way a man speaks of fever already in his own house — quietly, glancing at the door, as though the wrong volume might summon what it names. They say he never raises his voice. They say he carries a particular stillness about him, the stillness of a thing that has finished deciding, and that strong men have wept and confessed to crimes he had not even named, betraying themselves to deaf air, only to break the unbearable quiet of him. The beggars on the temple steps tell it differently, and worse: to them he is the one king in all Calradia who keeps every promise he makes, for his promise is always a threat, and the threat is always paid in full and on the day appointed. Some among them — the truly ruined, the ones the winter has hollowed out — fold their cracked hands and pray to him.\n \n"
          + "Burn his hideouts and he is the smoke that stings your eyes. Hang his men and he is the rope that does the hanging. He has forged all of Calradia's lawless dark into a single crown, and he wears it where no army on earth can strike it from his brow: in the cold, sure dread of every traveller on every road who, hearing a branch snap in the dark behind him, does not run and does not turn, but only walks on, and wonders whether the toll has already come due.";

        private const string FACTION_LORE =
            "There are kingdoms in Calradia, and there is the Holdout, and the chroniclers who keep the difference straight do so for the sake of their own necks. A kingdom is a thing of crowns and charters, of councils that quarrel over grain levies, of weddings that bind one bloodline to another with a wax seal and a feast. The Mountain Bandits Holdout is none of these. It is a fortress the Bandit King's host took at the point of the sword and never gave back — a robber-throne raised on stolen stone, its gates hung yet with the faded standards of the men who once held the walls, their threadbare colours left to rot in the wind as a jest no one dares to laugh at. Frost rimes those walls in the small hours, white along every joint and arrow-loop, and does not melt until the sun clears the ridge. Crows keep the battlements where sentries should. The masons who cut these blocks served some forgotten lord, dressed each stone true and set it square, and that lord's bones — if any soul yet living remembers the ditch they were flung into — lie outside the gate they no longer command, gnawed clean and nameless beneath the scree.\n \n"
          + "Within, there is one law, and it is the King's word — spoken once, in the low even voice of a man who has never needed to raise it, and binding until he speaks otherwise. No clerk sets it to parchment. No court weighs it against precedent. A man learns the law of the Holdout the way he learns the edge of a blade: by where it has already cut, by the stump where another man's hand used to be, by the silence that falls in the hall when a certain name is spoken too loud. There is one coin here, too, and it is not silver. It is fear — minted fresh each grey dawn, spent freely on the slow and the proud, hoarded by the wise against the day they will need to spend it themselves. Men obey because disobedience has a face, and the face watches from the high hall through the smoke of the long-fires, and in all the years of telling no man has ever sworn he saw it blink.\n \n"
          + "What is held within these walls would amount to nothing — a den, a sore, a single season's banditry burned out by the first hard winter — were it not for how far the writ runs beyond them. From this one stronghold the King's hand reaches down into every cave-mouth hideout in the hills, into every ambush-thicket crouched along the trade roads, into every drunken outlaw camp where ragged men carve up plunder by firelight and swear they answer to no master. They do not all know his name. Many have never climbed within a day's march of his gate. They obey it regardless, the way water obeys the slope of the land. A toll wrung from a mountain pass at knifepoint; a caravan bled white at a river ford and left for the kites; a village that pays in winter grain and a daughter or two not to be put to the torch — the dread that makes such things possible flows down from one source, gathers like meltwater, and runs back uphill again as tribute, that the source may sit behind his stolen stone and count what the country has yielded him.\n \n"
          + "The great kingdoms understand the Holdout the way a herdsman understands the wolf standing motionless at the treeline: as a thing that will not be reasoned with, only watched, and watched poorly. They cannot buy it, for the King already takes what he wants and owes thanks to no man for the taking. They cannot wed it into the fold, for it keeps no daughters to trade across a marriage-bed and holds no honour worth pledging there. And they cannot starve it into submission — for to lay siege to the Holdout is to drag a host up through the killing cold to the foot of a mountain, there to sit and shiver and dwindle, while every road at the army's back bleeds from a hundred small wounds the King need only nod to open: a bridge fired in the night, a supply train gutted in a defile, a forage party that rides out at dawn and is simply never seen again. Kings have marched on these gates. The chronicles that mention such marches grow strangely quiet about how they ended, and the men who could have told you do not return to tell.\n \n"
          + "So the Holdout endures, and the map endures it. Mothers in towns a week's hard ride distant name it in the dark to frighten squalling children into stillness, and the children grow and frighten their own the same way. Merchants weigh its unspoken tax into the price of every bolt of cloth and every cask of oil, and pay it, and never once name the levy aloud. Lords who swear over their cups that they will see it thrown down find always some nearer war, some closer rival, some surer reason the reckoning must wait one more year — and the years pass, and the waiting lengthens into the shape of a custom, and the shadow over the high passes grows no shorter for all the swearing. It is not a realm. It is a wound that learned to hold a sword, and it has not yet decided to stop bleeding the country that bore it.";

        // Deck the Bandit King in the best gear in the game — highest-tier armour in every slot, a top sword,
        // bow + arrows, a thrown weapon (or shield), and the finest warhorse + harness. Battle loadout gets the
        // weapons + mount; the civilian loadout mirrors the armour so he is armoured in the encyclopedia too.
        private static ItemObject BestItem(params ItemObject.ItemTypeEnum[] types)
        {
            ItemObject best = null; float bt = -1f;
            try
            {
                foreach (var it in MBObjectManager.Instance.GetObjectTypeList<ItemObject>())
                {
                    if (it == null) continue;
                    bool ok = false; foreach (var t in types) if (it.ItemType == t) { ok = true; break; }
                    if (!ok) continue;
                    if (it.Tierf > bt) { bt = it.Tierf; best = it; }
                }
            }
            catch { }
            return best;
        }

        private static void SetSlot(Equipment eq, EquipmentIndex idx, ItemObject item)
        {
            try { if (eq != null && item != null) eq[idx] = new EquipmentElement(item); } catch { }
        }

        private static void EquipChief(Hero hero)
        {
            try
            {
                if (hero == null) return;
                var head = BestItem(ItemObject.ItemTypeEnum.HeadArmor);
                var body = BestItem(ItemObject.ItemTypeEnum.BodyArmor);
                var leg = BestItem(ItemObject.ItemTypeEnum.LegArmor);
                var glove = BestItem(ItemObject.ItemTypeEnum.HandArmor);
                var cape = BestItem(ItemObject.ItemTypeEnum.Cape);
                var horse = BestItem(ItemObject.ItemTypeEnum.Horse);
                var harness = BestItem(ItemObject.ItemTypeEnum.HorseHarness);
                var sword = BestItem(ItemObject.ItemTypeEnum.OneHandedWeapon, ItemObject.ItemTypeEnum.TwoHandedWeapon);
                var bow = BestItem(ItemObject.ItemTypeEnum.Bow);
                var arrows = BestItem(ItemObject.ItemTypeEnum.Arrows);
                var thrown = BestItem(ItemObject.ItemTypeEnum.Thrown);
                var shield = BestItem(ItemObject.ItemTypeEnum.Shield);

                var b = hero.BattleEquipment;
                SetSlot(b, EquipmentIndex.Head, head); SetSlot(b, EquipmentIndex.Body, body);
                SetSlot(b, EquipmentIndex.Leg, leg); SetSlot(b, EquipmentIndex.Gloves, glove);
                SetSlot(b, EquipmentIndex.Cape, cape);
                SetSlot(b, EquipmentIndex.Horse, horse); SetSlot(b, EquipmentIndex.HorseHarness, harness);
                // Dedicate Weapon1 to the SHIELD so it's ALWAYS equipped (was Weapon3 = thrown ?? shield, so thrown
                // almost always won and the shield never got equipped). Loadout: sword + shield + bow + arrows/thrown.
                SetSlot(b, EquipmentIndex.Weapon0, sword); SetSlot(b, EquipmentIndex.Weapon1, shield);
                SetSlot(b, EquipmentIndex.Weapon2, bow); SetSlot(b, EquipmentIndex.Weapon3, arrows ?? thrown);

                var civ = hero.CivilianEquipment;
                SetSlot(civ, EquipmentIndex.Head, head); SetSlot(civ, EquipmentIndex.Body, body);
                SetSlot(civ, EquipmentIndex.Leg, leg); SetSlot(civ, EquipmentIndex.Gloves, glove);
                SetSlot(civ, EquipmentIndex.Cape, cape);
                Log("equipped bandit king: full-tier armour, weapons, and warhorse");
            }
            catch (Exception ex) { Log("EquipChief threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // A distinct, non-bandit town/village home per lieutenant (spread so the few hired are all different),
        // so each is "from" a different corner of Calradia rather than all sharing the King's seat.
        private static Settlement PickLieutenantHome(int index)
        {
            try
            {
                var pool = new System.Collections.Generic.List<Settlement>();
                foreach (var s in Settlement.All)
                    if (s != null && s.IsActive && (s.IsTown || s.IsVillage) && (s.MapFaction == null || !s.MapFaction.IsBanditFaction))
                        pool.Add(s);
                if (pool.Count == 0) return null;
                return pool[((index * 7 + 3) % pool.Count + pool.Count) % pool.Count];
            }
            catch (Exception ex) { Log("PickLieutenantHome threw " + ex.Message); return null; }
        }

        // Set Hero.HomeSettlement (no public setter — mirror BanditChiefRegistry: reflected UpdateHomeSettlement
        // method first, else the <HomeSettlement>k__BackingField / _homeSettlement field).
        private static void SetHomeSettlement(Hero hero, Settlement home)
        {
            if (hero == null || home == null) return;
            try
            {
                var m = typeof(Hero).GetMethod("UpdateHomeSettlement", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(Settlement) }, null);
                if (m != null) { m.Invoke(hero, new object[] { home }); return; }
                for (var t = typeof(Hero); t != null && t != typeof(object); t = t.BaseType)
                {
                    var f = t.GetField("<HomeSettlement>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? t.GetField("_homeSettlement", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (f != null) { f.SetValue(hero, home); return; }
                }
                Log("SetHomeSettlement: no mutation API found");
            }
            catch (Exception ex) { Log("SetHomeSettlement threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // === Siege buildup (muster) ===

        // Army size scaled to the live garrison of the target: ~1.75x, clamped [80, 2000] so it handles
        // 1000+ garrisons without ballooning, and never musters a token force.
        public static int ComputeArmyTarget(Settlement target)
        {
            try
            {
                int g = (target != null && target.Town != null && target.Town.GarrisonParty != null && target.Town.GarrisonParty.MemberRoster != null)
                    ? target.Town.GarrisonParty.MemberRoster.TotalManCount : 0;
                int t = (int)System.Math.Round(g * 1.75);
                if (t < 80) t = 80;
                if (t > 2000) t = 2000;
                return t;
            }
            catch (Exception ex) { Log("ComputeArmyTarget threw " + ex.Message); return 120; }
        }

        // The highest-Tier non-hero troop already in the party's roster (the spawned warband is full of the
        // culture's bandit troops). Reusing the roster avoids needing to know the culture's troop tree.
        public static CharacterObject MaxTierTroop(MobileParty party)
        {
            try
            {
                CharacterObject best = null; int bestTier = -1;
                var roster = party != null ? party.MemberRoster : null;
                if (roster != null)
                    for (int i = 0; i < roster.Count; i++)
                    {
                        var c = roster.GetCharacterAtIndex(i);
                        if (c == null || c.IsHero) continue;
                        if (c.Tier > bestTier) { bestTier = c.Tier; best = c; }
                    }
                if (best == null && party != null && party.ActualClan != null && party.ActualClan.Culture != null)
                    best = party.ActualClan.Culture.BanditBoss;
                return best;
            }
            catch (Exception ex) { Log("MaxTierTroop threw " + ex.Message); return null; }
        }

        // === Varied-army recruitment ("Warlord's mixed host") ===
        // Per-culture cache of one representative regular troop per combat class, so the King's army musters a
        // realistic MIX (bow / swords / horse archer / cavalry) from the REAL culture whose fief he holds — instead
        // of 316 identical bandit bosses. Built once per culture by BFS over the troop tree; nulls are allowed (a
        // culture without horse archers simply has a null in that slot, and the caller redistributes its share).
        private struct CultureTroopMix { public CharacterObject Infantry; public CharacterObject Ranged; public CharacterObject Cavalry; public CharacterObject HorseArcher; }
        private static readonly System.Collections.Generic.Dictionary<CultureObject, CultureTroopMix> _cultureMixCache
            = new System.Collections.Generic.Dictionary<CultureObject, CultureTroopMix>();

        // Classify a regular troop into the 4 default combat classes. DefaultFormationClass can be a variant
        // (Skirmisher/HeavyInfantry/LightCavalry/HeavyCavalry), so we normalise via IsMounted+IsRanged — the same
        // pair the engine itself derives the base class from. mounted+ranged=HorseArcher, mounted+melee=Cavalry,
        // foot+ranged=Ranged, foot+melee=Infantry.
        private static FormationClass ClassifyTroop(CharacterObject c)
        {
            try
            {
                var fc = c.DefaultFormationClass;
                if (fc == FormationClass.Infantry || fc == FormationClass.Ranged || fc == FormationClass.Cavalry || fc == FormationClass.HorseArcher)
                    return fc;
                // Variant/unset class — fall back to the mounted/ranged flags.
                bool mounted = c.IsMounted, ranged = c.IsRanged;
                if (mounted && ranged) return FormationClass.HorseArcher;
                if (mounted) return FormationClass.Cavalry;
                if (ranged) return FormationClass.Ranged;
                return FormationClass.Infantry;
            }
            catch { return FormationClass.Infantry; }
        }

        // Build (and cache) one solid representative troop per class for a REAL (non-bandit) culture.
        // Candidate pool = BFS from BasicTroop + EliteBasicTroop over UpgradeTargets (all reachable non-hero troops),
        // plus the four militia troops folded in as candidates/fallbacks. Representative per class = highest Tier.
        private static CultureTroopMix GetCultureMix(CultureObject culture)
        {
            CultureTroopMix mix;
            try
            {
                if (culture == null) return default(CultureTroopMix);
                if (_cultureMixCache.TryGetValue(culture, out mix)) return mix;

                var pool = new System.Collections.Generic.List<CharacterObject>();
                var seen = new System.Collections.Generic.HashSet<CharacterObject>();
                var queue = new System.Collections.Generic.Queue<CharacterObject>();

                Action<CharacterObject> enqueueRoot = (root) =>
                {
                    if (root != null && !root.IsHero && seen.Add(root)) queue.Enqueue(root);
                };
                enqueueRoot(culture.BasicTroop);
                enqueueRoot(culture.EliteBasicTroop);
                // Militia troops as candidates/fallbacks.
                enqueueRoot(culture.MeleeMilitiaTroop);
                enqueueRoot(culture.RangedMilitiaTroop);
                enqueueRoot(culture.MeleeEliteMilitiaTroop);
                enqueueRoot(culture.RangedEliteMilitiaTroop);

                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    if (c == null || c.IsHero) continue;
                    pool.Add(c);
                    var ups = c.UpgradeTargets;
                    if (ups != null)
                        for (int i = 0; i < ups.Length; i++)
                        {
                            var u = ups[i];
                            if (u != null && !u.IsHero && seen.Add(u)) queue.Enqueue(u);
                        }
                }

                mix = default(CultureTroopMix);
                int tInf = -1, tRng = -1, tCav = -1, tHA = -1;
                for (int i = 0; i < pool.Count; i++)
                {
                    var c = pool[i];
                    int tier;
                    try { tier = c.Tier; } catch { tier = 0; }
                    switch (ClassifyTroop(c))
                    {
                        case FormationClass.Ranged:      if (tier > tRng) { tRng = tier; mix.Ranged = c; } break;
                        case FormationClass.Cavalry:     if (tier > tCav) { tCav = tier; mix.Cavalry = c; } break;
                        case FormationClass.HorseArcher: if (tier > tHA)  { tHA  = tier; mix.HorseArcher = c; } break;
                        default:                         if (tier > tInf) { tInf = tier; mix.Infantry = c; } break;
                    }
                }

                _cultureMixCache[culture] = mix;
                Log("GetCultureMix " + culture.StringId + ": inf=" + (mix.Infantry != null ? mix.Infantry.StringId : "-")
                    + " rng=" + (mix.Ranged != null ? mix.Ranged.StringId : "-")
                    + " cav=" + (mix.Cavalry != null ? mix.Cavalry.StringId : "-")
                    + " ha=" + (mix.HorseArcher != null ? mix.HorseArcher.StringId : "-")
                    + " (pool=" + pool.Count + ")");
                return mix;
            }
            catch (Exception ex) { Log("GetCultureMix threw " + ex.GetType().Name + ": " + ex.Message); return default(CultureTroopMix); }
        }

        // Add up to `perDay` troops, not exceeding `targetSize`, as a "Warlord's mixed host":
        //   40% bandit veterans (roster's existing top-tier bandit troop) + 30% infantry / 15% archers /
        //   10% cavalry / 5% horse archers from the party's REAL (conquered-fief) culture.
        // Missing classes redistribute their share to Infantry (else to the bandit core). If the real culture is
        // null / bandit / yields no troops, falls back to the original mono-troop (all `add` to MaxTierTroop).
        // Returns the new total man count. Direct roster add (not recruitment) — deterministic, cannot stall.
        public static int MusterStep(MobileParty party, int targetSize, int perDay)
        {
            try
            {
                if (party == null || party.MemberRoster == null) return 0;
                int have = party.MemberRoster.TotalManCount;
                int need = targetSize - have;
                if (need <= 0) return have;
                int add = need < perDay ? need : perDay;
                if (add <= 0) return have;

                var banditCore = MaxTierTroop(party); // the outlaw core (existing top-tier bandit troop, e.g. sea_raider_boss)

                // Resolve the REAL culture the holdout clan adopted from its conquered fief. A bandit culture (or none)
                // has no varied regular tree, so we fall back to the mono-troop seed behaviour.
                CultureObject realCulture = null;
                try { realCulture = party.ActualClan != null ? party.ActualClan.Culture : null; } catch { realCulture = null; }
                bool cultureIsBandit = false;
                try { cultureIsBandit = realCulture != null && realCulture.IsBandit; } catch { cultureIsBandit = false; }

                CultureTroopMix mix = default(CultureTroopMix);
                if (realCulture != null && !cultureIsBandit) mix = GetCultureMix(realCulture);
                bool haveVaried = mix.Infantry != null || mix.Ranged != null || mix.Cavalry != null || mix.HorseArcher != null;

                if (!haveVaried)
                {
                    // FALLBACK: original single-troop behaviour.
                    if (banditCore != null) party.MemberRoster.AddToCounts(banditCore, add);
                    return party.MemberRoster.TotalManCount;
                }

                // Split `add` into 40/30/15/10/5. Remainder goes to the bandit core so the sum stays EXACTLY `add`.
                int nInf = (add * 30) / 100;
                int nRng = (add * 15) / 100;
                int nCav = (add * 10) / 100;
                int nHA  = (add *  5) / 100;

                // Redistribute any class with no representative troop: its share goes to Infantry if that troop exists,
                // otherwise back to the bandit core (via the remainder below).
                if (mix.Infantry == null)   { nInf = 0; }
                if (mix.Ranged == null)     { if (mix.Infantry != null) nInf += nRng; nRng = 0; }
                if (mix.Cavalry == null)    { if (mix.Infantry != null) nInf += nCav; nCav = 0; }
                if (mix.HorseArcher == null){ if (mix.Infantry != null) nInf += nHA;  nHA  = 0; }

                int nBandit = add - nInf - nRng - nCav - nHA; // 40% + remainder + any share with no Infantry sink
                if (nBandit < 0) nBandit = 0;

                // If there is no bandit core troop, fold its share into Infantry (or drop it if Infantry is also absent).
                if (banditCore == null && nBandit > 0)
                {
                    if (mix.Infantry != null) { nInf += nBandit; }
                    nBandit = 0;
                }

                if (banditCore != null && nBandit > 0) party.MemberRoster.AddToCounts(banditCore, nBandit);
                if (mix.Infantry != null && nInf > 0)  party.MemberRoster.AddToCounts(mix.Infantry, nInf);
                if (mix.Ranged != null && nRng > 0)    party.MemberRoster.AddToCounts(mix.Ranged, nRng);
                if (mix.Cavalry != null && nCav > 0)   party.MemberRoster.AddToCounts(mix.Cavalry, nCav);
                if (mix.HorseArcher != null && nHA > 0)party.MemberRoster.AddToCounts(mix.HorseArcher, nHA);

                return party.MemberRoster.TotalManCount;
            }
            catch (Exception ex) { Log("MusterStep threw " + ex.Message); return party != null && party.MemberRoster != null ? party.MemberRoster.TotalManCount : 0; }
        }

        // Give a party a NATIVE fleet so it becomes naval-capable (HasNavalNavigationCapability) and can cross
        // water like the engine's lord parties. Resolve real ship hulls from the party's culture's AvailableShipHulls
        // (fallback to known medium hulls), then build + attach ships via the production action (PartyBase.Ships is
        // read-only). Clamped to the engine's ideal ship number so it never over-stuffs. Returns the ship count.
        public static int GiveFleet(MobileParty party, int count)
        {
            try
            {
                if (party == null || party.Party == null || !party.IsActive) { Log("GiveFleet: party null/inactive"); return 0; }

                var hulls = new System.Collections.Generic.List<ShipHull>();
                try
                {
                    var avail = party.ActualClan?.Culture?.AvailableShipHulls;
                    if (avail != null)
                        foreach (var h in avail) if (h != null) hulls.Add(h);
                }
                catch { }
                if (hulls.Count == 0)
                {
                    foreach (var id in new[] { "northern_medium_ship", "empire_medium_ship", "western_medium_ship", "eastern_medium_ship" })
                    {
                        try { var h = MBObjectManager.Instance.GetObject<ShipHull>(id); if (h != null) hulls.Add(h); } catch { }
                    }
                }
                if (hulls.Count == 0) { Log("GiveFleet: no ship hull resolved for " + party.StringId); return 0; }

                int ideal = count;
                try { var m = Campaign.Current?.Models?.PartyShipLimitModel; if (m != null) ideal = System.Math.Max(count, m.GetIdealShipNumber(party)); } catch { }
                if (ideal < count) ideal = count;
                if (ideal > 6) ideal = 6;

                for (int i = 0; i < ideal; i++)
                {
                    try { var ship = new Ship(hulls[i % hulls.Count]); ChangeShipOwnerAction.ApplyByProduction(party.Party, ship); }
                    catch (Exception ex) { Log("GiveFleet ship threw " + ex.GetType().Name); }
                }

                int have = party.Party.Ships != null ? party.Party.Ships.Count : 0;
                Log("GiveFleet: " + party.StringId + " now owns " + have + " ships, naval=" + party.HasNavalNavigationCapability);
                return have;
            }
            catch (Exception ex) { Log("GiveFleet threw " + ex.GetType().Name + ": " + ex.Message); return 0; }
        }

        // Create `count` companion heroes in the King's clan and RETURN them (the caller decides whether they ride
        // in the King's roster or are later promoted to their own lord parties). Each gets the King's full polish.
        public static System.Collections.Generic.List<Hero> HireCompanions(Hero chief, int count)
        {
            var result = new System.Collections.Generic.List<Hero>();
            try
            {
                if (chief == null || chief.Clan == null) return result;
                var culture = chief.Clan.Culture;
                var template = ResolveChiefTemplate(culture);
                if (template == null) { Log("HireCompanions: no template"); return result; }
                Settlement near = chief.HomeSettlement;
                int made = 0;
                for (int i = 0; i < count; i++)
                {
                    var comp = CreateChiefHero(template, near, chief.Clan);
                    if (comp == null) continue;
                    // Distinct named lieutenant, each with the King's full polish AND his own backstory.
                    try { var lnm = new TextObject(BanditLieutenantLore.Name(made)); comp.SetName(lnm, lnm); } catch { }
                    MaxOutChief(comp);
                    EquipChief(comp);
                    SetHeroCulture(comp, chief.Culture);     // bandit culture, not the template's Empire
                    try { comp.SetHasMet(); } catch { }
                    try { comp.IsKnownToPlayer = true; } catch { }
                    try { comp.ChangeState(Hero.CharacterStates.Active); } catch { }
                    SeedBanditFriends(comp);                 // Friends = the bandit chiefs
                    SetHomeSettlement(comp, PickLieutenantHome(made) ?? chief.HomeSettlement); // each from a different ruined corner
                    try { comp.EncyclopediaText = new TextObject("{=*}" + BanditLieutenantLore.Backstory(made)); } catch { }
                    result.Add(comp);
                    made++;
                }
                Log("hired " + made + " companions for the Bandit King");
            }
            catch (Exception ex) { Log("HireCompanions threw " + ex.GetType().Name + ": " + ex.Message); }
            return result;
        }

        // Promote a hired lieutenant into his OWN native lord party (LordPartyComponent) so he leads troops, raids,
        // besieges, and crosses water just like the King. Spawns a warband at the hideout, adds the lieutenant to it,
        // converts it to a lord party led by him, musters it to `armySize`, optionally gives it a fleet, and releases
        // it to native AI. Returns the new party (null on any failure).
        public static MobileParty SpawnLieutenantParty(Hero lt, CultureObject culture, Settlement hideout, int armySize, bool giveFleet)
        {
            try
            {
                if (lt == null || culture == null || hideout == null) { Log("SpawnLieutenantParty: null arg"); return null; }
                var party = BanditWarbandFactory.SpawnWarband(culture, hideout);
                if (party == null) { Log("SpawnLieutenantParty: SpawnWarband returned null"); return null; }
                try { if (lt.CharacterObject != null) party.MemberRoster.AddToCounts(lt.CharacterObject, 1); } catch { }
                try { LordPartyComponent.ConvertPartyToLordParty(party, lt, lt); } catch (Exception ex) { Log("SpawnLieutenantParty convert threw " + ex.Message); }
                if (party.LeaderHero != lt) { Log("Lt party leader bind failed"); return null; }
                int tgt = armySize > 0 ? armySize : 120;
                MusterStep(party, tgt, tgt);
                if (giveFleet) GiveFleet(party, 2);
                try { party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(2f)); } catch { }
                try { if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(false); } catch { }
                Log("SpawnLieutenantParty: " + party.StringId + " leader=" + (party.LeaderHero != null && party.LeaderHero.Name != null ? party.LeaderHero.Name.ToString() : "?") + " size=" + (party.MemberRoster != null ? party.MemberRoster.TotalManCount : 0));
                return party;
            }
            catch (Exception ex) { Log("SpawnLieutenantParty threw " + ex.GetType().Name + ": " + ex.Message); return null; }
        }

        // === War / siege / capture / dissolve lifecycle ===
        // The Bandit King makes war on every realm in Calradia, but never on his own kind — bandit factions are
        // skipped, leaving him at war with all kingdoms (the Wars list) and allied (Friends) with the outlaw bands.
        // Has the PLAYER earned the outlaws' FULL trust — allied (BanditAllianceBehavior) with EVERY bandit-chief
        // culture? Used by the war loop below (spare the ally's realm) and by BanditSiegeBehavior's player stance.
        // A mere setting (PeacefulEncounters) or one neutral relation is NOT enough — those spared the player wrongly.
        public static bool PlayerHasEarnedBanditAlliance()
        {
            try
            {
                var ally = BandItPlus.Behaviors.BanditAllianceBehavior.Instance;
                if (ally == null) return false;
                var cultures = BandItPlus.Origins.BanditOriginBehavior.AllParleyCultures;
                if (cultures == null || cultures.Length == 0) return false;
                foreach (var cid in cultures)
                    if (string.IsNullOrEmpty(cid) || !ally.IsAllied(cid)) return false; // any chief not yet allied -> not earned
                return true; // allied with EVERY bandit-chief culture = completed the chiefs' trust
            }
            catch { return false; }
        }

        // LOOSE variant, for the bandit-BACKUP feature only (bandits fight for you). True if the player is bandit-friendly
        // AT ALL: allied with ANY one bandit-chief culture, OR the global PeacefulEncounters bandit-peace mode is on.
        // Deliberately looser than PlayerHasEarnedBanditAlliance so backup kicks in long before every chief is allied.
        public static bool PlayerIsBanditFriendly()
        {
            try
            {
                if (MCMSettings.Instance != null && MCMSettings.Instance.PeacefulEncounters) return true;
                var ally = BandItPlus.Behaviors.BanditAllianceBehavior.Instance;
                var cultures = BandItPlus.Origins.BanditOriginBehavior.AllParleyCultures;
                if (ally != null && cultures != null)
                    foreach (var cid in cultures)
                        if (!string.IsNullOrEmpty(cid) && ally.IsAllied(cid)) return true; // allied with ANY chief
                return false;
            }
            catch { return false; }
        }

        public static void DeclareWarOnAllKingdoms(IFaction holdout)
        {
            try
            {
                if (holdout == null) return;
                // STEP 3: when the player is bandit-allied, the King never wars the player's own realm. This static
                // helper can't reach the behavior's PlayerIsBanditAllied(), so we gate on the global bandit-peace
                // MCM setting PeacefulEncounters (the same proxy PlayerBanditNeutrality uses) as the "player allied" signal.
                bool sparePlayerKingdom = PlayerHasEarnedBanditAlliance(); // spare only a fully-allied player's realm (every call site)
                Kingdom playerKingdom = Clan.PlayerClan != null ? Clan.PlayerClan.Kingdom : null;
                foreach (var k in Kingdom.All)
                {
                    if (k == null || k.IsBanditFaction || k == holdout) continue;
                    if (sparePlayerKingdom && playerKingdom != null && k == playerKingdom) continue; // spare the bandit-allied player's kingdom
                    DeclareWarOn(holdout, k);
                }
            }
            catch (Exception ex) { Log("DeclareWarOnAllKingdoms threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        public static bool DeclareWarOn(IFaction attacker, IFaction victim)
        {
            try
            {
                if (attacker == null || victim == null || attacker == victim) return false;
                if (attacker.IsAtWarWith(victim)) return true;
                foreach (var name in new[] { "ApplyByDefault", "ApplyByRebellion" }) // ApplyByDefault works for ANY kingdom; ApplyByRebellion only the liege he left
                {
                    foreach (var m in typeof(DeclareWarAction).GetMethods(BindingFlags.Static | BindingFlags.Public))
                    {
                        if (m.Name != name) continue;
                        var ps = m.GetParameters();
                        if (ps.Length < 2 || !ps[0].ParameterType.IsInstanceOfType(attacker)) continue;
                        var args = new object[ps.Length];
                        args[0] = attacker; args[1] = victim;
                        for (int i = 2; i < ps.Length; i++)
                            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                        m.Invoke(null, args);
                        Log("declared war: " + attacker.Name + " vs " + victim.Name + " (" + name + ")");
                        return true;
                    }
                }
                Log("DeclareWarOn: no usable DeclareWarAction overload"); return false;
            }
            catch (Exception ex) { Log("DeclareWarOn threw " + ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return false; }
        }

        // Raise a REAL native siege (MobilePartyAi.SetMoveBesiegeSettlement) so the engine builds a visible siege
        // camp + runs the assault — the King has a strong mustered army and is a valid at-war map faction, so unlike
        // a pure bandit he can besiege properly. Protect him from relief armies (IgnoreByOtherPartiesTill) while he
        // does. If the besiege API is missing in this build, fall back to the old march-and-park (the behavior's
        // force-capture timer still flips ownership). The [BP-Siege] log shows which path fired.
        public static void BeginSiege(MobileParty party, Settlement target)
        {
            try
            {
                if (party == null || target == null) return;
                if (TryNativeBesiege(party, target)) Log("siege begun (NATIVE besiege) on " + target.Name);
                else { BandItPlus.Compat.PartyOrderCompat.GoToSettlement(party, target); Log("siege begun (march + park fallback) on " + target.Name); }
                try { party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(6f)); } catch { } // protect the besieger; MaintainSiege re-asserts each poll
            }
            catch (Exception ex) { Log("BeginSiege threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Keep the siege alive while the King is still MARCHING (re-issue + commit). Once a real SiegeEvent is
        // running, UNLOCK his AI so the engine presses the assault, then hand the whole siege off to the engine.
        public static void MaintainSiege(MobileParty party, Settlement target)
        {
            try
            {
                if (party == null || !party.IsActive || target == null) return;
                try { party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(6f)); } catch { } // protect the besieger from relief armies THROUGHOUT the siege (re-asserted each poll) — fixes the mid-siege 'party lost' wipe
                bool besieging = (party.SiegeEvent != null) || (party.BesiegedSettlement == target);
                if (besieging)
                {
                    try { if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(false); } catch { } // siege formed — let the engine press the assault
                    return;
                }
                if (party.MapEvent != null) return; // battle in progress
                TryNativeBesiege(party, target); // keep marching to + committing to the target
            }
            catch (Exception ex) { Log("MaintainSiege threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Native besiege order — confirmed from the 1.4.5 DLL: MobileParty.SetMoveBesiegeSettlement(Settlement,
        // NavigationType). Then LOCK the order with SetDoNotMakeNewDecisions(true) so the AI COMMITS to the siege
        // instead of immediately overriding it — exactly the proven village-raid pattern (BanditRaidBehavior:
        // SetMoveRaidSettlement + SetDoNotMakeNewDecisions(true)). MaintainSiege unlocks once the SiegeEvent forms.
        private static bool TryNativeBesiege(MobileParty party, Settlement target)
        {
            try
            {
                if (party == null || target == null) return false;
                if (!BandItPlus.Compat.PartyOrderCompat.BesiegeSettlement(party, target)) return false;
                try { if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(true); } catch { }
                return true;
            }
            catch (Exception ex) { Log("TryNativeBesiege threw " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return false; }
        }

        // The Bandit King must NEVER fight looters/bandits — he is their overlord. The holdout clan is non-bandit
        // (IsBanditFaction=false, to avoid the GetInfestedHideoutCount / naval crashes), and the engine starts
        // every bandit faction AT WAR with all non-bandit factions — so without this the garrison auto-aggros
        // bandits and bandits aggro the holdout. Set the FACTION STANCE to neutral (personal +100 relation does
        // not stop party/garrison aggro). Covers vanilla IsBanditFaction clans AND the mod's custom bandit clans
        // (whose clans are IsBanditFaction=FALSE — enumerate the registry, same gotcha SeedBanditFriends documents).
        public static void MakePeaceWithAllBandits(IFaction holdout)
        {
            try
            {
                if (holdout == null) return;
                foreach (var c in Clan.All)
                    if (c != null && c != holdout && c.IsBanditFaction) PeaceWith(holdout, c);
                var reg = BandItPlus.Heroes.BanditChiefRegistry.Instance;
                if (reg != null)
                    foreach (var chief in reg.GetAllChiefHeroes())
                        if (chief != null && chief.Clan != null && chief.Clan != holdout) PeaceWith(holdout, chief.Clan);
            }
            catch (Exception ex) { Log("MakePeaceWithAllBandits threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Set the raw faction stance to neutral (SILENT). We deliberately do NOT call MakePeaceAction — it pops a
        // "peace declared" notification, and because the engine re-declares bandit war on a non-bandit faction each
        // war-tick, calling it repeatedly SPAMS the player. Authoritative peace is enforced instead by
        // HoldoutBanditPeacePatch (IsAtWarAgainstFaction -> false for holdout-vs-bandit), which is silent.
        private static void PeaceWith(IFaction a, IFaction b)
        {
            try
            {
                if (a == null || b == null || a == b) return;
                try { FactionManager.SetNeutral(a, b); } catch { }
            }
            catch (Exception ex) { Log("PeaceWith threw " + ex.Message); }
        }

        // Send the holdout King's lord party to BESIEGE an enemy fief via the NATIVE siege path (he now owns a
        // fief and is a valid at-war map faction, so unlike a pure bandit he can raise a real SiegeEvent). Clears
        // the pin + re-enables AI decisions so native combat runs. Falls back to a plain go-to if the besiege API
        // is missing in this build (he at least marches on the enemy instead of idling inside his own settlement).
        public static void SendToBesiege(MobileParty party, Settlement target)
        {
            try
            {
                if (party == null || !party.IsActive || target == null) return;
                try { party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(2f)); } catch { } // light protection en route to the next fief
                if (TryNativeBesiege(party, target)) Log("SendToBesiege: native besiege -> " + target.Name);
                else { try { BandItPlus.Compat.PartyOrderCompat.GoToSettlement(party, target); Log("SendToBesiege: fallback goto -> " + target.Name); } catch { } }
            }
            catch (Exception ex) { Log("SendToBesiege threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Relieve an owned fief under siege: prefer defending the settlement (native SetMoveDefendSettlement — join
        // its garrison / break the siege), fall back to engaging the besieging army's leader party, then a plain
        // go-to. Mirrors SendToBesiege's commit pattern (IgnoreByOtherPartiesTill + Ai.SetDoNotMakeNewDecisions).
        // API signatures VERIFIED against TaleWorlds.CampaignSystem.dll (v1.4.5): SetMoveDefendSettlement takes
        // (Settlement, bool isTargetingPort, NavigationType); SetMoveEngageParty takes (MobileParty, NavigationType);
        // SiegeEvent.BesiegerCamp is a public field; BesiegerCamp.LeaderParty is a MobileParty (no PartyBase hop).
        public static void SendToRelieve(MobileParty party, Settlement target)
        {
            try
            {
                if (party == null || !party.IsActive || target == null) return;
                bool moved = false;
                try { moved = BandItPlus.Compat.PartyOrderCompat.DefendSettlement(party, target); } catch { }
                if (!moved)
                {
                    MobileParty besieger = null;
                    try { besieger = target.SiegeEvent != null && target.SiegeEvent.BesiegerCamp != null ? target.SiegeEvent.BesiegerCamp.LeaderParty : null; } catch { }
                    if (besieger != null) { try { moved = BandItPlus.Compat.PartyOrderCompat.EngageParty(party, besieger); } catch { } }
                }
                if (!moved) { try { BandItPlus.Compat.PartyOrderCompat.GoToSettlement(party, target); } catch { } }
                try { party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(4f)); } catch { }
                try { if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(true); } catch { }
                Log("SendToRelieve: " + (party.LeaderHero != null ? party.LeaderHero.Name.ToString() : party.StringId) + " -> relieve " + target.Name);
            }
            catch (Exception ex) { Log("SendToRelieve threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Send the King's party to RAID an enemy village (pillage it) via the PROVEN native raid intent —
        // MobileParty.SetMoveRaidSettlement(Settlement, NavigationType, bool) + SetDoNotMakeNewDecisions(true) to
        // commit (mirrors BanditRaidBehavior). A bandit overlord pillages the countryside between sieges.
        public static void RaidVillage(MobileParty party, Settlement village)
        {
            try
            {
                if (party == null || !party.IsActive || village == null) return;
                try { party.IgnoreByOtherPartiesTill(CampaignTime.DaysFromNow(2f)); } catch { }
                if (BandItPlus.Compat.PartyOrderCompat.RaidSettlement(party, village))
                {
                    try { if (party.Ai != null) party.Ai.SetDoNotMakeNewDecisions(true); } catch { } // lock the raid so the AI commits
                    Log("RaidVillage: raiding -> " + village.Name);
                }
                else
                {
                    // No raid-intent API on this build — march there instead.
                    Log("RaidVillage: raid intent unavailable, falling back to goto -> " + village.Name);
                    BandItPlus.Compat.PartyOrderCompat.GoToSettlement(party, village);
                }
            }
            catch (Exception ex) { Log("RaidVillage threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        // Native-style reinforcement: pull the SURPLUS of the King's own fief garrison (above a defensive floor)
        // into his field party. His fief feeds his army — the garrison regrows naturally from his villages /
        // prosperity, so his host is sustained by his holdings instead of spawned from nothing. Returns count pulled.
        public static int ReinforceFromGarrison(MobileParty king, IFaction holdout, int defensiveFloor)
        {
            return ReinforceFromGarrison(king, holdout, defensiveFloor, int.MaxValue);
        }

        // 4-arg overload: same as the 3-arg but caps the pull to `maxPull` so a lieutenant doesn't drain the
        // King's whole surplus garrison in one tick (the King + each lieutenant share one garrison).
        public static int ReinforceFromGarrison(MobileParty king, IFaction holdout, int defensiveFloor, int maxPull)
        {
            try
            {
                if (king == null || king.MemberRoster == null || holdout == null || holdout.Settlements == null) return 0;
                MobileParty garrison = null;
                foreach (var s in holdout.Settlements)
                    if (s != null && s.Town != null && s.Town.GarrisonParty != null && s.Town.GarrisonParty.MemberRoster != null) { garrison = s.Town.GarrisonParty; break; }
                if (garrison == null) return 0;

                int pullable = System.Math.Max(0, garrison.MemberRoster.TotalManCount - System.Math.Max(0, defensiveFloor));
                pullable = System.Math.Min(pullable, System.Math.Max(0, maxPull));
                if (pullable <= 0) return 0;

                var snapshot = new System.Collections.Generic.List<(CharacterObject ch, int num)>();
                for (int i = 0; i < garrison.MemberRoster.Count; i++)
                {
                    var el = garrison.MemberRoster.GetElementCopyAtIndex(i);
                    if (el.Character == null || el.Character.IsHero || el.Number <= 0) continue;
                    snapshot.Add((el.Character, el.Number));
                }
                int pulled = 0;
                foreach (var (ch, num) in snapshot)
                {
                    if (pullable <= 0) break;
                    int take = System.Math.Min(num, pullable);
                    garrison.MemberRoster.AddToCounts(ch, -take); // out of the garrison
                    king.MemberRoster.AddToCounts(ch, take);      // into the King's field army
                    pulled += take; pullable -= take;
                }
                return pulled;
            }
            catch (Exception ex) { Log("ReinforceFromGarrison threw " + ex.GetType().Name + ": " + ex.Message); return 0; }
        }

        // 2026-07-02 (hire design): "call of the wilds" fallback — used ONLY while the
        // clan owns no fief (native fief recruitment covers the rest). Pays the party
        // leader's REAL gold (50g/head, sunk via GiveGoldAction to null) to add a small
        // daily batch of culture-mix troops through the existing MusterStep. Capped at
        // max(120, armyTarget); returns the hired count for the drive log.
        public static int HireFromTheWilds(MobileParty party, Clan clan, int armyTarget)
        {
            try
            {
                if (party == null || party.MemberRoster == null) return 0;
                var lead = party.LeaderHero ?? (clan != null ? clan.Leader : null);
                if (lead == null) return 0;
                const int costPerMan = 50;
                int cap = System.Math.Max(120, armyTarget);
                int batch = System.Math.Min(12, lead.Gold / costPerMan);
                if (batch <= 0 || party.MemberRoster.TotalManCount >= cap) return 0;
                int added = MusterStep(party, cap, batch);
                if (added > 0)
                {
                    try { GiveGoldAction.ApplyBetweenCharacters(lead, null, added * costPerMan, true); } catch { }
                    Log("HireFromTheWilds: " + (party.StringId ?? "?") + " hired " + added + " for " + (added * costPerMan) + "g");
                }
                return added;
            }
            catch (Exception ex) { Log("HireFromTheWilds threw " + ex.GetType().Name + ": " + ex.Message); return 0; }
        }

        // 2026-07-02 WARLORD BRAIN (B): daily garrison trickle for a developed fief — adds up to
        // `perDay` FIEF-CULTURE troops (reuses the culture-mix pathway) while the garrison sits
        // below `target`. Returns the number actually added (0 on any miss) so the caller can
        // bill the King's gold per head.
        public static int GarrisonTrickle(Settlement s, int target, int perDay)
        {
            try
            {
                if (s == null || s.Town == null || target <= 0 || perDay <= 0) return 0;
                var gp = s.Town.GarrisonParty;
                if (gp == null || gp.MemberRoster == null) return 0;
                int before = gp.MemberRoster.TotalManCount;
                if (before >= target) return 0;
                int add = System.Math.Min(perDay, target - before);
                var mix = GetCultureMix(s.Culture);
                CharacterObject inf = mix.Infantry ?? mix.Ranged ?? mix.Cavalry ?? mix.HorseArcher;
                if (inf == null) { try { inf = s.Culture != null ? s.Culture.BasicTroop : null; } catch { inf = null; } }
                if (inf == null) return 0;
                int nRng = (mix.Ranged != null && mix.Ranged != inf) ? add / 3 : 0;
                int nInf = add - nRng;
                if (nInf > 0) gp.MemberRoster.AddToCounts(inf, nInf);
                if (nRng > 0) gp.MemberRoster.AddToCounts(mix.Ranged, nRng);
                return gp.MemberRoster.TotalManCount - before;
            }
            catch (Exception ex) { Log("GarrisonTrickle threw " + ex.GetType().Name + ": " + ex.Message); return 0; }
        }

        // 2026-07-02 WARLORD BRAIN (B): seat a governor via a reflection-tolerant
        // ChangeGovernorAction.Apply(Town, Hero) — the exact Capture/ApplyBySiege idiom, so an
        // API mismatch in another game build skips gracefully instead of throwing at JIT.
        public static bool SeatGovernor(Town town, Hero governor)
        {
            try
            {
                if (town == null || governor == null) return false;
                var t = typeof(Clan).Assembly.GetType("TaleWorlds.CampaignSystem.Actions.ChangeGovernorAction");
                if (t == null) { Log("SeatGovernor: ChangeGovernorAction type missing"); return false; }
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (m.Name != "Apply") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 2) continue;
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType == typeof(Town) ? (object)town
                                : ps[i].ParameterType == typeof(Hero) ? governor
                                : ps[i].HasDefaultValue ? ps[i].DefaultValue
                                : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    m.Invoke(null, args);
                    Log("SeatGovernor: " + (governor.Name != null ? governor.Name.ToString() : "?") + " governs " + (town.Name != null ? town.Name.ToString() : "?"));
                    return true;
                }
                Log("SeatGovernor: no usable Apply overload"); return false;
            }
            catch (Exception ex) { Log("SeatGovernor threw " + ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return false; }
        }

        // 2026-07-02 WARLORD BRAIN (C): make peace between two factions via a reflection-tolerant
        // MakePeaceAction.Apply(IFaction, IFaction, ...) — same idiom as Capture's ApplyBySiege.
        // NOTE this is for KINGDOM truces only; holdout-vs-bandit peace stays with the silent
        // HoldoutBanditPeacePatch (MakePeaceAction there would spam popups — see PeaceWith above).
        public static bool MakePeaceWith(IFaction a, IFaction b)
        {
            try
            {
                if (a == null || b == null || a == b) return false;
                if (!a.IsAtWarWith(b)) return true; // already at peace
                var t = typeof(Clan).Assembly.GetType("TaleWorlds.CampaignSystem.Actions.MakePeaceAction");
                if (t == null) { Log("MakePeaceWith: MakePeaceAction type missing"); return false; }
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (m.Name != "Apply") continue;
                    var ps = m.GetParameters();
                    if (ps.Length < 2 || !ps[0].ParameterType.IsInstanceOfType(a) || !ps[1].ParameterType.IsInstanceOfType(b)) continue;
                    var args = new object[ps.Length];
                    args[0] = a; args[1] = b;
                    for (int i = 2; i < ps.Length; i++)
                        args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                    m.Invoke(null, args);
                    Log("made peace: " + a.Name + " and " + b.Name);
                    return true;
                }
                Log("MakePeaceWith: no usable Apply overload"); return false;
            }
            catch (Exception ex) { Log("MakePeaceWith threw " + ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return false; }
        }

        public static bool Capture(Hero chief, Settlement target)
        {
            try
            {
                if (chief == null || target == null) return false;
                // 2026-07-02 crash fix (Makeb, 00:10:07): NEVER flip ownership while a battle
                // is active at the target. ApplyBySiege mid-MapEvent detaches the engaged
                // garrison (CurrentSettlement -> null) and the engine's next
                // SimulateBattleRound NREs in SettlementHelper.IsGarrisonStarving (v1.4.6 IL
                // derefs the parameter unchecked). Native only applies ownership AFTER the
                // battle ends — defer the same way; the siege poll retries hourly.
                if (target.Party != null && target.Party.MapEvent != null)
                {
                    Log("Capture DEFERRED: battle in progress at " + target.Name);
                    return false;
                }
                foreach (var m in typeof(ChangeOwnerOfSettlementAction).GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (m.Name != "ApplyBySiege") continue;
                    var ps = m.GetParameters();
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType == typeof(Hero) ? (object)chief
                                : ps[i].ParameterType == typeof(Settlement) ? target
                                : ps[i].HasDefaultValue ? ps[i].DefaultValue
                                : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    m.Invoke(null, args);
                    Log("ApplyBySiege invoked for " + target.Name + " -> " + (chief.Clan != null ? chief.Clan.Name.ToString() : "?"));
                    return true;
                }
                Log("Capture: ApplyBySiege not found"); return false;
            }
            catch (Exception ex) { Log("Capture threw " + ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return false; }
        }

        public static void DissolveOnFailure(Clan clan, MobileParty party)
        {
            try
            {
                if (party != null && party.IsActive) { try { DestroyPartyAction.Apply(null, party); } catch (Exception e) { Log("dissolve party: " + e.Message); } }
                if (clan != null && !clan.IsEliminated && (clan.Settlements == null || clan.Settlements.Count == 0))
                {
                    string sid = null; try { sid = clan.StringId; } catch { }
                    try { DestroyClanAction.Apply(clan); Log("dissolved clan " + sid); }
                    catch (Exception e)
                    {
                        Log("dissolve clan (DestroyClanAction NRE): " + e.Message + " — falling back to SafeDestroyClan");
                        try { SafeDestroyClan(clan); } catch (Exception e2) { Log("SafeDestroyClan also threw: " + e2.Message + " — leaving clan (harmless: no fief, at peace)"); }
                    }
                }
            }
            catch (Exception ex) { Log("DissolveOnFailure threw " + ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
