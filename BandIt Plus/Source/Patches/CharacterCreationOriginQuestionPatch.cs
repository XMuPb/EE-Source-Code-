using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace BandItPlus.Patches
{
    // 07-14 Native "Before the Road" origin question.
    //
    // Players read the old custom Gauntlet origin screen + cinematic as "AI slop", so
    // the origin choice now lives in the game's OWN character creation as a native
    // backstory question. 1.4.5 War Sails renamed the classes to NarrativeMenu /
    // NarrativeMenuOption; menus render only when linked into the chain via
    // OutputMenuId -> StringId, so we splice ours onto the live tail (it becomes the
    // last backstory question, right before the review — "before the road").
    //
    // Content handlers implement ICharacterCreationContentHandler.AfterInitializeContent
    // as PRIVATE explicit-interface methods; several may be active at once (base /
    // story mode / naval DLC), all sharing one CharacterCreationManager. We patch each
    // by name and add our menu idempotently (guarded by menu id) so it appears in both
    // story-mode and sandbox campaigns.
    //
    // The option records the pick in ChosenOrigin; BanditOriginBehavior applies the
    // origin's full effects on the first map tick (the behavior doesn't exist yet
    // during creation). The small themed skill reward here is native flavor; BP's
    // GrantOriginPerks still grants the full kit once on campaign start.
    [HarmonyPatch]
    public static class CharacterCreationOriginQuestionPatch
    {
        // 0 / -1 = unchosen; 1 = Outlaw Blood, 2 = Drifter, 3 = Lawkeeper
        // (matches BanditOriginBehavior.BanditOrigin). Consumed by BanditOriginBehavior.
        public static volatile int ChosenOrigin = -1;

        private const string MenuId = "bp_origin_question";
        private const string BookId = "bp_origin_book";

        private static readonly string[] HandlerTypeNames =
        {
            "TaleWorlds.CampaignSystem.CampaignBehaviors.CharacterCreationCampaignBehavior",
            "StoryMode.Behaviors.StoryModeCharacterCreationCampaignBehavior",
            "SandBox.CampaignBehaviors.NavalCharacterCreationCampaignBehavior",
        };

        private const string IfaceMethod =
            "TaleWorlds.CampaignSystem.CharacterCreationContent.ICharacterCreationContentHandler.AfterInitializeContent";

        private static MethodBase ResolveAfterInit(Type t)
        {
            var m = AccessTools.Method(t, IfaceMethod);
            if (m != null) return m;
            // Explicit-interface impls carry the fully-qualified name; match by suffix
            // and the single CharacterCreationManager parameter.
            foreach (var mi in t.GetMethods(BindingFlags.Instance | BindingFlags.Public
                     | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!mi.Name.EndsWith("AfterInitializeContent", StringComparison.Ordinal)) continue;
                var ps = mi.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.Name == "CharacterCreationManager") return mi;
            }
            return null;
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            int found = 0;
            foreach (var tn in HandlerTypeNames)
            {
                var t = AccessTools.TypeByName(tn);
                if (t == null) continue;
                var m = ResolveAfterInit(t);
                if (m != null) { found++; yield return m; }
            }
            Log("attached to " + found + " content handler(s)");
        }

        public static bool Prepare()
        {
            foreach (var tn in HandlerTypeNames)
            {
                var t = AccessTools.TypeByName(tn);
                if (t != null && ResolveAfterInit(t) != null) return true;
            }
            Log("no character-creation content handler found — origin falls back to the native inquiry");
            return false;
        }

        public static void Postfix(CharacterCreationManager characterCreationManager)
        {
            try
            {
                var mgr = characterCreationManager;
                if (mgr == null || mgr.NarrativeMenus == null) return;

                // Idempotent: add our question only once per creation session.
                try { if (mgr.GetNarrativeMenuWithId(MenuId) != null) return; } catch { }

                var menus = new List<NarrativeMenu>();
                foreach (var m in mgr.NarrativeMenus) if (m != null) menus.Add(m);
                if (menus.Count == 0) return; // nothing to chain onto — bail safely

                // Tail = the menu whose OutputMenuId leads nowhere (the chain terminal).
                // Fall back to the last menu in list order.
                NarrativeMenu tail = null;
                for (int i = menus.Count - 1; i >= 0; i--)
                {
                    var m = menus[i];
                    string outId = m.OutputMenuId;
                    bool leadsNowhere = string.IsNullOrEmpty(outId);
                    if (!leadsNowhere)
                    {
                        bool resolves = false;
                        foreach (var o in menus) if (o.StringId == outId) { resolves = true; break; }
                        leadsNowhere = !resolves;
                    }
                    if (leadsNowhere) { tail = m; break; }
                }
                if (tail == null) tail = menus[menus.Count - 1];

                string tailOut = tail.OutputMenuId; // preserve whatever the tail pointed to

                // Reuse the tail menu's scene characters + character-args delegate. A
                // NarrativeMenu built with an empty Characters list and a NULL
                // GetNarrativeMenuCharacterArgs delegate crashes the game inside
                // CharacterCreationManager.ModifyMenuCharacters() the instant the stage
                // switches to it (that method invokes the delegate). Cloning the tail's
                // data makes our menu behave exactly like a real backstory question.
                var chars = tail.Characters != null
                    ? new List<NarrativeMenuCharacter>(tail.Characters)
                    : new List<NarrativeMenuCharacter>();
                var charArgs = tail.GetNarrativeMenuCharacterArgs;
                var menu = new NarrativeMenu(
                    MenuId, tail.StringId, BookId,
                    new TextObject("{=bp_ccq_title}Before the Road"),
                    new TextObject("{=bp_ccq_desc}Every outlaw has a past. Who were you, before the road took you?"),
                    chars, charArgs);
                // OutputMenuId is a readonly field — repoint the tail via reflection so
                // OutputMenuId-based chain navigation reaches our menu. (Our menu's
                // InputMenuId is already the tail's id from the ctor, covering the
                // InputMenuId-based model too.)
                try
                {
                    var outField = AccessTools.Field(typeof(NarrativeMenu), "OutputMenuId");
                    outField?.SetValue(tail, MenuId);
                }
                catch (Exception rex) { Log("tail repoint failed: " + rex.GetType().Name + ": " + rex.Message); }

                AddOption(menu, 1, "{=bp_ccq_o1}Outlaw Blood",
                    "{=bp_ccq_o1d}Raised among the lawless — the bandit clans remember your name.\n\"I was weaned on woodsmoke and other men's coin.\"\n\nSchooled in Roguery and Scouting, quick on your feet and sharp in a skirmish. You set out with throwing blades, a hunting bow and about 1,200 denars.\n\nThe eight bandit clans start friendlier to you (+15) and their camps trade with you from day one. You begin at war like everyone — but a chief's price for peace costs you HALF.",
                    new[] { DefaultSkills.Roguery, DefaultSkills.Scouting });
                AddOption(menu, 2, "{=bp_ccq_o2}Drifter",
                    "{=bp_ccq_o2d}You owe nothing to anyone, and no past owns you.\n\nA jack of all trades — Scouting, Trade, Riding, a little blade-work and Roguery. You set out with a hunting bow, a pack horse, throwing knives and about 1,600 denars.\n\nAt war with every clan, but their chiefs hear an outsider's terms at the usual price, and camp traders warm to you quickly. A balanced start, no strings attached.",
                    new[] { DefaultSkills.Scouting, DefaultSkills.Trade });
                AddOption(menu, 3, "{=bp_ccq_o3}Lawkeeper",
                    "{=bp_ccq_o3d}You hunted outlaws once, and the road remembers.\n\nA hardened fighter and leader — One-Handed, Polearm, Leadership, Charm and Riding. You set out with a tempered tier-2 sword and about 2,200 denars.\n\nBounties on raiders pay you +25%. But the clans distrust you (-20), and any chief who deigns to parley will demand DOUBLE for peace.",
                    new[] { DefaultSkills.OneHanded, DefaultSkills.Leadership });

                // Chronicle "book" page — a native beat right after the origin choice,
                // leading into the review. Reuses the tail's scene data (same NRE-safety
                // as the origin menu) and carries the authored prologue, span-stripped so
                // the native UI never shows raw ink tags.
                var bookChars = tail.Characters != null
                    ? new List<NarrativeMenuCharacter>(tail.Characters)
                    : new List<NarrativeMenuCharacter>();
                var bookMenu = new NarrativeMenu(
                    BookId, MenuId, tailOut,
                    new TextObject("{=bp_ccbook_title}The Chronicle"),
                    new TextObject("{=bp_ccbook_body}You were born far from here, in a house of small people — the kind kings never meet and tax collectors never forget. Then came the year it all broke, and what you lost is your business; that you lost it is written all over you, plain as a brand.\n\nOn the road inland, a one-eyed peddler pressed this book into your hands and would take no coin for it. \"The kings will tell you what Calradia is. The road will tell you the truth.\"\n\nIt still smells of woodsmoke."),
                    bookChars, charArgs);
                var readOn = new NarrativeMenuOption(
                    BookId + "_go",
                    new TextObject("{=bp_ccbook_go}Read on"),
                    new TextObject(""),
                    a => { try { a.SetAffectedSkills(new SkillObject[0]); a.SetLevelToSkills(0); a.SetFocusToSkills(0); } catch { } },
                    null, null,
                    m => { });
                bookMenu.AddNarrativeMenuOption(readOn);

                mgr.AddNewMenu(menu);
                mgr.AddNewMenu(bookMenu);
                Log("origin + chronicle spliced after '" + tail.StringId + "' (tailOut='" + tailOut + "')");
            }
            catch (Exception ex) { Log("Postfix " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void AddOption(NarrativeMenu menu, int originId, string text, string desc, SkillObject[] skills)
        {
            var opt = new NarrativeMenuOption(
                MenuId + "_" + originId,
                new TextObject(text),
                new TextObject(desc),
                args =>
                {
                    // Small native flavor reward so the option reads like a real
                    // backstory question. The full themed kit is granted by BP's
                    // GrantOriginPerks on campaign start.
                    try
                    {
                        args.SetAffectedSkills(skills);
                        args.SetLevelToSkills(15);
                        args.SetFocusToSkills(1);
                    }
                    catch { }
                },
                null,                                    // onCondition
                null,                                    // onSelect
                mgr => { ChosenOrigin = originId; });     // onConsequence = the recorded choice
            menu.AddNarrativeMenuOption(opt);
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-Origin/CCQ] " + msg); } catch { }
            // The peace-debug log isn't wired up during character creation (no campaign
            // yet), so also append to a standalone file — our only visibility this early.
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Configs", "ModSettings", "Global", "BandItPlus");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "bp_charcreation.log"), "[CCQ] " + msg + "\n");
            }
            catch { }
        }
    }
}
