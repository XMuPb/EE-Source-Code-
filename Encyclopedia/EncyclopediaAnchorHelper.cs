using System;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Shared defensive validation for the three settlement-page widget injectors
    /// (LoreSectionInjector, JournalSectionInjector, HeroTimelineSectionInjector).
    ///
    /// Each injector walks up from an anchor widget (typically a section-header
    /// near EncyclopediaDividerButtonWidget) to find a parent ListPanel, then
    /// AddChildAtIndex's its own widget there. On vanilla pages this works; on
    /// pages restructured by other mods (Realm of Thrones, etc.), the walked-up
    /// parent has a different shape and the injection lands in a column that
    /// renders broken layout.
    ///
    /// This helper centralises the "is it safe to inject here?" decision so
    /// each injector can call one method instead of duplicating logic. Today
    /// the gate is page-type + mod-detection driven; future shape checks can
    /// be added without touching every injector.
    /// </summary>
    public static class EncyclopediaAnchorHelper
    {
        /// <summary>
        /// True when the current insertion is safe to proceed.
        /// Returns false only when ALL of these hold:
        ///   - The current page is a Settlement page
        ///   - A known conflicting layout mod is detected
        ///   - The user has NOT overridden via MCM (OverrideLayoutModCompat = true)
        ///
        /// For Hero/Clan/Faction/Kingdom pages, always returns true.
        /// For Settlement pages with no detected conflict, always returns true.
        /// </summary>
        /// <param name="parent">The walked-up parent widget about to receive AddChildAtIndex. May be null.</param>
        /// <param name="injectorTag">Short tag for log lines (e.g. "Lore", "Journal", "Timeline").</param>
        public static bool IsSafeToInjectOnCurrentPage(Widget parent, string injectorTag)
        {
            try
            {
                string pageType = null;
                try { pageType = EncyclopediaPageTracker.CurrentPageType; } catch { }

                // Only apply the strict gate on Settlement pages. The reported
                // bug (2026-05-25, image S987QOQ) is settlement-specific; we
                // don't want to block Hero/Clan/Faction/Kingdom injection.
                if (pageType != "Settlement") return true;

                if (!ModCompatHelper.IsConflictingSettlementLayoutModLoaded) return true;

                // Conflict detected. Respect the user-facing override toggle.
                var s = MCMSettings.Instance;
                if (s != null && s.OverrideLayoutModCompat) return true;

                try
                {
                    MCMSettings.DebugLog("[" + injectorTag + "Inject] Skipping " + injectorTag + " injection on Settlement page - "
                        + "conflicting layout mod loaded (" + ModCompatHelper.DetectedModuleId
                        + "). Toggle 'Override Layout Mod Compat' in MCM to force on.");
                }
                catch { }

                _ = parent; // suppress unused-parameter warning; reserved for future shape checks
                return false;
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("[AnchorHelper] IsSafeToInjectOnCurrentPage failed: " + ex.ToString()); } catch { }
                // Fail-open: if our own check threw, don't block the injector.
                return true;
            }
        }
    }
}
