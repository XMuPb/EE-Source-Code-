using System;
using System.Collections;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Compatibility shim for the Bannerlord v1.4.5 encyclopedia subsystem refactor.
    ///
    /// In Bannerlord 1.3.x and earlier:
    ///   - Encyclopedia data lived on <c>GauntletMapEncyclopediaView._encyclopediaData</c>
    ///     (an <c>EncyclopediaData</c> instance in <c>TaleWorlds.CampaignSystem</c>).
    ///   - <c>EncyclopediaData</c> contained <c>_pages</c>, <c>_lists</c>, and the method
    ///     <c>SetEncyclopediaPage</c> that we Harmony-patched to fix NavalDLC's missing
    ///     "Clan" key bug.
    ///
    /// In Bannerlord 1.4.5+ (War Sails update, 2026-05-21):
    ///   - <c>EncyclopediaData</c>, <c>GauntletMapEncyclopediaView</c>, and
    ///     <c>EncyclopediaScreenManager</c> are REMOVED.
    ///   - All functionality merged into
    ///     <c>TaleWorlds.CampaignSystem.Encyclopedia.EncyclopediaManager</c>
    ///     (accessible via <c>Campaign.Current.EncyclopediaManager</c>).
    ///   - <c>EncyclopediaManager</c> directly owns the <c>_pages</c> dictionary
    ///     (Dictionary&lt;Type, EncyclopediaPage&gt;) plus <c>_executeLink</c> and methods
    ///     <c>GoToLink</c>, <c>GetPageOf</c>, <c>GetEncyclopediaPages</c>.
    ///   - <c>SetEncyclopediaPage</c> method no longer exists — navigation is now driven
    ///     by <c>GoToLink</c> + the <c>_executeLink</c> callback.
    ///
    /// This class detects which engine version we're on at module load and provides a
    /// uniform API for the rest of the mod. All access stays reflection-based so the
    /// build doesn't break against either Bannerlord version.
    /// </summary>
    public static class EncyclopediaCompat
    {
        public enum ApiMode
        {
            Unknown = 0,
            Legacy_1_3 = 1,   // EncyclopediaData on GauntletMapEncyclopediaView (1.3.x and earlier)
            Modern_1_4 = 2    // EncyclopediaManager directly on Campaign (1.4.5+, War Sails)
        }

        private static ApiMode _mode = ApiMode.Unknown;
        private static bool _detectionRan;
        private static readonly object _detectionLock = new object();

        private static Type _pagesOwnerType;
        private static FieldInfo _pagesField;
        private static FieldInfo _listsField;

        /// <summary>
        /// Active API mode (Legacy_1_3, Modern_1_4, or Unknown). Triggers detection if not yet run.
        /// </summary>
        public static ApiMode Mode
        {
            get { DetectIfNeeded(); return _mode; }
        }

        /// <summary>
        /// The type that owns the _pages dictionary. Useful for Harmony patch targeting.
        /// </summary>
        public static Type PagesOwnerType
        {
            get { DetectIfNeeded(); return _pagesOwnerType; }
        }

        public static FieldInfo PagesField
        {
            get { DetectIfNeeded(); return _pagesField; }
        }

        public static FieldInfo ListsField
        {
            get { DetectIfNeeded(); return _listsField; }
        }

        /// <summary>
        /// Writes a line to debug-compat.log (sibling to debug.log) AND attempts the normal
        /// EECoreLogger.For("EE-Core").Debug channel. The file-write path is independent of MCMSettings
        /// (no DebugMode gate, no Instance dependency) so the message is GUARANTEED to reach
        /// disk even if called during OnSubModuleLoad before MCMv5 settings deserialize.
        /// </summary>
        private static void CompatLog(string msg)
        {
            // Direct-to-file path (always works)
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Configs", "ModSettings", "Global",
                    "EditableEncyclopedia", "Logs");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, "debug-compat.log");
                var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                System.IO.File.AppendAllText(file, "[" + ts + "] " + msg + Environment.NewLine);
            }
            catch { /* if disk-write fails there's no recovery path */ }

            // Best-effort EECoreLogger.For("EE-Core").Debug path (will silently no-op if MCM not ready yet)
            try { EECoreLogger.For("EE-Core").Debug(msg); } catch { }
        }

        /// <summary>
        /// Runs detection once and caches the result. Idempotent — safe to call repeatedly.
        /// Logs the detected mode via <see cref="CompatLog"/> (always reaches debug-compat.log).
        /// </summary>
        public static void DetectIfNeeded()
        {
            if (_detectionRan) return;
            lock (_detectionLock)
            {
                if (_detectionRan) return;
                _detectionRan = true;

                try
                {
                    var instanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                    // 1. Try legacy: EncyclopediaData (deleted in v1.4.5).
                    var legacyType = ResolveType("TaleWorlds.CampaignSystem.Encyclopedia.EncyclopediaData")
                                  ?? ResolveType("TaleWorlds.CampaignSystem.EncyclopediaData");
                    if (legacyType != null)
                    {
                        var legacyPages = legacyType.GetField("_pages", instanceNonPublic);
                        if (legacyPages != null)
                        {
                            _mode = ApiMode.Legacy_1_3;
                            _pagesOwnerType = legacyType;
                            _pagesField = legacyPages;
                            _listsField = legacyType.GetField("_lists", instanceNonPublic);
                            CompatLog("[EncyclopediaCompat] Detected LEGACY 1.3.x API: "
                                + legacyType.FullName + "._pages + _lists. SetEncyclopediaPage patches should work.");
                            return;
                        }
                    }

                    // 2. Try modern: EncyclopediaManager (v1.4.5+).
                    var modernType = ResolveType("TaleWorlds.CampaignSystem.Encyclopedia.EncyclopediaManager");
                    if (modernType != null)
                    {
                        var modernPages = modernType.GetField("_pages", instanceNonPublic);
                        if (modernPages != null)
                        {
                            _mode = ApiMode.Modern_1_4;
                            _pagesOwnerType = modernType;
                            _pagesField = modernPages;
                            _listsField = null; // no _lists in modern API; use GetEncyclopediaPages() instead
                            CompatLog("[EncyclopediaCompat] Detected MODERN 1.4.5+ API: "
                                + modernType.FullName + "._pages (via Campaign.EncyclopediaManager). "
                                + "Legacy SetEncyclopediaPage patches are NO-OPS in this mode; "
                                + "GoToLink-based recovery required (see step 2 of v1.4.5 compat work).");
                            return;
                        }
                        CompatLog("[EncyclopediaCompat] Found EncyclopediaManager type but no _pages field — "
                            + "API may have changed further. Available fields: " + DumpFieldNames(modernType, instanceNonPublic));
                    }

                    CompatLog("[EncyclopediaCompat] WARNING: neither legacy (EncyclopediaData) nor modern "
                        + "(EncyclopediaManager) encyclopedia API detected. Mod may misbehave on this Bannerlord version. "
                        + "Encyclopedia* types found: " + ListEncyclopediaTypes());
                }
                catch (Exception ex)
                {
                    CompatLog("[EncyclopediaCompat] DetectIfNeeded error: " + ex.ToString());
                }
            }
        }

        /// <summary>
        /// Returns the live instance of the type that owns _pages.
        /// In Legacy mode this returns null — the caller must walk from the top screen
        /// (via the existing FindEncyclopediaScreenManager logic) because EncyclopediaData
        /// lives on the screen, not Campaign.
        /// In Modern mode this returns <c>Campaign.Current.EncyclopediaManager</c>.
        /// </summary>
        public static object GetPagesOwnerInstance()
        {
            DetectIfNeeded();
            try
            {
                if (_mode == ApiMode.Modern_1_4)
                {
                    var campaign = Campaign.Current;
                    return campaign?.EncyclopediaManager;
                }
                // Legacy: caller's responsibility (screen walk required).
                return null;
            }
            catch (Exception ex)
            {
                EECoreLogger.For("EE-Core").Debug("[EncyclopediaCompat] GetPagesOwnerInstance error: " + ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Reads the _pages dictionary from a given owner instance. Returns null on any failure.
        /// </summary>
        public static IDictionary GetPagesDict(object owner)
        {
            DetectIfNeeded();
            if (owner == null || _pagesField == null) return null;
            try { return _pagesField.GetValue(owner) as IDictionary; }
            catch (Exception ex)
            {
                CompatLog("[EncyclopediaCompat] GetPagesDict error: " + ex.ToString());
                return null;
            }
        }

        // Cached so EnsurePagesPopulated does the heavy iteration once per Campaign instance.
        private static object _lastPopulatedOwner;

        /// <summary>
        /// v1.4.5 fix for the "given key not present in the dictionary" crash on Clan navigation:
        /// iterate every page returned by EncyclopediaManager.GetEncyclopediaPages(), read its
        /// internal _identifierTypes (Type[] of entity types the page handles), and ensure the
        /// _pages dict has a mapping for each type. Guarantees ExecuteLink/GoToLink for any
        /// registered entity type (Hero, Clan, Kingdom, Settlement, Concept, Faction, Unit, Ship,
        /// plus types added by DLC modules) can resolve its page.
        ///
        /// Equivalent to the legacy "_lists -> _pages" recovery from v1.3.x, retargeted to the
        /// v1.4.5 API. Safe no-op in Legacy mode. Idempotent — caches per Campaign instance.
        /// </summary>
        public static int EnsurePagesPopulated()
        {
            DetectIfNeeded();
            if (_mode != ApiMode.Modern_1_4) return 0;

            try
            {
                var owner = GetPagesOwnerInstance();
                if (owner == null)
                {
                    CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: no EncyclopediaManager instance (Campaign not started?)");
                    return 0;
                }

                if (object.ReferenceEquals(owner, _lastPopulatedOwner)) return 0;

                var pages = GetPagesDict(owner);
                if (pages == null)
                {
                    CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: _pages dict is null");
                    return 0;
                }

                var getPagesMethod = _pagesOwnerType.GetMethod("GetEncyclopediaPages",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getPagesMethod == null)
                {
                    CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: GetEncyclopediaPages method not found");
                    return 0;
                }

                var enumerable = getPagesMethod.Invoke(owner, null) as IEnumerable;
                if (enumerable == null)
                {
                    CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: GetEncyclopediaPages returned non-IEnumerable");
                    return 0;
                }

                int before = pages.Count;
                int added = 0;
                FieldInfo identifierTypesField = null;

                foreach (var page in enumerable)
                {
                    if (page == null) continue;

                    if (identifierTypesField == null)
                    {
                        var t = page.GetType();
                        while (identifierTypesField == null && t != null && t != typeof(object))
                        {
                            identifierTypesField = t.GetField("_identifierTypes",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            t = t.BaseType;
                        }
                        if (identifierTypesField == null)
                        {
                            CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: _identifierTypes field not found on " + page.GetType().FullName);
                            return 0;
                        }
                    }

                    var idTypes = identifierTypesField.GetValue(page) as Type[];
                    if (idTypes == null) continue;

                    foreach (var entityType in idTypes)
                    {
                        if (entityType == null) continue;
                        if (!pages.Contains(entityType))
                        {
                            pages[entityType] = page;
                            added++;
                            CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: added missing entry "
                                + entityType.FullName + " -> " + page.GetType().FullName);
                        }
                    }
                }

                _lastPopulatedOwner = owner;
                CompatLog("[EncyclopediaCompat] EnsurePagesPopulated: started with " + before
                    + " entries, added " + added + ", total " + pages.Count);
                return added;
            }
            catch (Exception ex)
            {
                CompatLog("[EncyclopediaCompat] EnsurePagesPopulated error: " + ex.ToString());
                return 0;
            }
        }

        private static Type ResolveType(string fqn)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(fqn, false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string DumpFieldNames(Type t, BindingFlags flags)
        {
            try
            {
                var fields = t.GetFields(flags);
                var sb = new System.Text.StringBuilder();
                foreach (var f in fields)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(f.Name);
                }
                return sb.ToString();
            }
            catch { return "(failed to dump)"; }
        }

        private static string ListEncyclopediaTypes()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                int count = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("TaleWorlds")) continue;
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name.Contains("Encyclopedia") && !t.Name.Contains("<") && count < 12)
                            {
                                if (sb.Length > 0) sb.Append(", ");
                                sb.Append(t.FullName);
                                count++;
                            }
                        }
                    }
                    catch { }
                }
                if (count >= 12) sb.Append(", ...");
                return sb.ToString();
            }
            catch { return "(enumeration failed)"; }
        }
    }
}
