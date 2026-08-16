using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;

namespace BandItPlus.Patches
{
    // Wave 4.6.4: overrides CharacterObject.Name during dialog rendering so the speaker
    // label shows our authored chief/instance name (e.g. "Vargolf the Iron-eyed",
    // "Aldwin") instead of the troop class name ("Forest Bandit Boss").
    //
    // Side channel: static dictionary keyed by character -> override TextObject.
    // Set via OverrideName(character, name) from dialog conditions; cleared via
    // ClearAll() at mission end so overrides don't bleed across encounters.
    //
    // Side effect: any code that reads the boss's CharacterObject.Name during the
    // conversation window also sees the overridden name. Acceptable — the boss
    // CharacterObject is rarely accessed outside conversation context, and the
    // override is cleared on mission end.
    // v213-rollback-step-2 (2026-05-31): [HarmonyPatch] attribute COMMENTED OUT to test
    // whether THIS patch is the cause of SaveResultType.GeneralFailure on save. Audit
    // synthesis flagged it HIGH-confidence: Postfix substitutes unregistered TextObjects
    // during save serialization, ClearAll() not wired to OnBeforeSave so overrides leak.
    // CALLERS (OverrideName/ClearAll from BanditDialogManager + HideoutVendorDialog +
    // HideoutSlaverDialog) still compile and execute — they just populate a dict that
    // is no longer queried. Cosmetic: dialog speaker labels show vanilla troop names
    // instead of authored chief names for the duration of this test.
    // v218 (2026-05-31): RE-ENABLED. v213 bisect proved this patch was NOT the cause
    // of the save NRE — actual cause was a missing ConstructContainerDefinition(
    // typeof(Dictionary<string, double>)) — see BandItPlusSaveableTypeDefiner.cs +
    // memory note bannerlord-construct-container-definition.
    [HarmonyPatch(typeof(CharacterObject), "get_Name")]
    public static class ConversationNamePatch
    {
        private static readonly Dictionary<CharacterObject, TextObject> _overrides
            = new Dictionary<CharacterObject, TextObject>();

        public static void OverrideName(CharacterObject character, string name)
        {
            if (character == null || string.IsNullOrEmpty(name)) return;
            _overrides[character] = new TextObject(name);
        }

        public static void ClearOverride(CharacterObject character)
        {
            if (character == null) return;
            _overrides.Remove(character);
        }

        public static void ClearAll()
        {
            _overrides.Clear();
        }

        [HarmonyPostfix]
        public static void Postfix(CharacterObject __instance, ref TextObject __result)
        {
            if (__instance == null) return;
            if (_overrides.TryGetValue(__instance, out var overrideName))
                __result = overrideName;
        }
    }
}
