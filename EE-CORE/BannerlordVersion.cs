using System;
using System.Reflection;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Runtime detection of Bannerlord engine version + version-conditional quirks.
    ///
    /// 2026-05-28: Created to handle the 1.4.x-only LayoutMethod enum inversion bug.
    /// On 1.4.x: VerticalBottomToTop renders top-down, VerticalTopToBottom renders bottom-up.
    /// On 1.3.x and earlier: enum names work as named (top-down means top-down).
    /// See memory: reference_bannerlord_layoutmethod_enum_inverted.
    ///
    /// Detection is performed once, cached, and tolerant of reflection failures.
    /// </summary>
    public static class BannerlordVersion
    {
        private static bool _probed;
        private static int _major = -1;
        private static int _minor = -1;
        private static int _revision = -1;
        private static bool? _layoutEnumInverted;

        /// <summary>
        /// Whether the LayoutMethod enum names are inverted (VerticalBottomToTop renders top-down).
        ///
        /// 2026-05-28 (revised twice): empirical observation on the user's 1.3.x install is that
        /// the inversion DOES apply. The earlier "1.3.x is not inverted" assertion turned out
        /// to be incorrect — switching to "VerticalTopToBottom" reversed prose body on 1.3.x.
        /// Treating ALL supported versions as inverted matches the pre-refactor hardcoded value
        /// that was empirically working before this helper was introduced.
        ///
        /// The version-detection plumbing is kept (Probe() still runs + logs) so a future
        /// version that flips behavior again can be detected and accommodated, but the helper's
        /// answer is currently constant.
        /// </summary>
        public static bool IsLayoutEnumInverted()
        {
            if (_layoutEnumInverted.HasValue) return _layoutEnumInverted.Value;
            Probe(); // populates Major/Minor + logs diagnostics

            // GROUND TRUTH (in-game): on 1.3.x the enum is INVERTED — VerticalBottomToTop renders
            // TOP-DOWN, so the prefabs (authored with VerticalBottomToTop for top-down) look right.
            // On 1.4.x (War Sails) TaleWorlds fixed the engine: VerticalBottomToTop renders BOTTOM-UP
            // (literal), so that same authoring renders UPSIDE-DOWN. Hence inverted = (version <= 1.3).
            // Probe failure (version unknown) -> default to inverted=true, preserving the long-standing
            // pre-1.4 behavior that worked before this helper existed (safest fallback).
            bool inverted;
            if (_major < 0 || _minor < 0) inverted = true;          // unknown -> safe pre-1.4 default
            else if (_major > 1) inverted = false;                  // future majors render literally
            else if (_major == 1 && _minor >= 4) inverted = false;  // 1.4.x+ -> NOT inverted (fixed)
            else inverted = true;                                    // 1.3.x and earlier -> inverted

            _layoutEnumInverted = inverted;
            try
            {
                var lg = EECoreLogger.For("BannerlordVersion");
                if (lg != null) lg.Info("IsLayoutEnumInverted=" + inverted + " (Major=" + _major + " Minor=" + _minor + ")");
            }
            catch { }
            return inverted;
        }

        /// <summary>
        /// Returns the LayoutMethod enum name to use for top-down visual rendering (prose, headers-first lists).
        /// Caller should look up this enum value via reflection on the StackLayout.LayoutMethod property type.
        /// </summary>
        public static string TopDownLayoutEnumName()
        {
            return IsLayoutEnumInverted() ? "VerticalBottomToTop" : "VerticalTopToBottom";
        }

        /// <summary>
        /// Returns the LayoutMethod enum name to use for bottom-up visual rendering (rarely needed; symmetric helper).
        /// </summary>
        public static string BottomUpLayoutEnumName()
        {
            return IsLayoutEnumInverted() ? "VerticalTopToBottom" : "VerticalBottomToTop";
        }

        public static int Major { get { Probe(); return _major; } }
        public static int Minor { get { Probe(); return _minor; } }
        public static int Revision { get { Probe(); return _revision; } }

        /// <summary>
        /// Engine version as a printable string, e.g. "v1.4.7". Returns "unknown" if every
        /// probe failed. For logs and support requests, not for comparisons - use
        /// Major/Minor/Revision when you need to branch on version.
        /// </summary>
        public static string GameVersionString
        {
            get
            {
                Probe();
                if (_major < 0) return "unknown";
                return "v" + _major + "." + _minor + "." + _revision;
            }
        }

        /// <summary>
        /// Version of any installed module, by module id (the folder name / SubModule.xml Id),
        /// e.g. GetModuleVersion("EE-Core") -> "v2.6.1.1".
        ///
        /// Returns "not installed" when the module isn't present, and "unknown" if the
        /// ModuleManager API can't be reached. Never throws: this exists to make logs
        /// self-describing, and a diagnostic must not be able to break the thing it reports on.
        /// </summary>
        public static string GetModuleVersion(string moduleId)
        {
            if (string.IsNullOrEmpty(moduleId)) return "unknown";
            try
            {
                var mgrType = Type.GetType(
                    "TaleWorlds.ModuleManager.ModuleHelper, TaleWorlds.ModuleManager", false);
                if (mgrType == null) return "unknown";

                var getInfo = mgrType.GetMethod("GetModuleInfo",
                    BindingFlags.Public | BindingFlags.Static);
                if (getInfo == null) return "unknown";

                var info = getInfo.Invoke(null, new object[] { moduleId });
                if (info == null) return "not installed";

                var versionProp = info.GetType().GetProperty("Version",
                    BindingFlags.Public | BindingFlags.Instance);
                if (versionProp == null) return "unknown";

                var v = versionProp.GetValue(info);
                return v == null ? "unknown" : v.ToString();
            }
            catch { return "unknown"; }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            string trace = "";
            try
            {
                if (TryReadApplicationVersionCurrent()) { trace = "ApplicationVersion.Current"; }
                else if (TryReadModuleNativeVersion()) { trace = "ModuleHelper.GetModuleInfo(Native)"; }
                else { trace = "ALL_PROBES_FAILED"; }
            }
            catch (Exception ex)
            {
                trace = "EXCEPTION:" + ex.GetType().Name;
            }

            // One-time log via EECoreLogger so we can confirm in-game which path resolved.
            try
            {
                var lg = EECoreLogger.For("BannerlordVersion");
                if (lg != null)
                    lg.Info("Probe done via=" + trace
                        + " Major=" + _major + " Minor=" + _minor + " Revision=" + _revision);
            }
            catch { }
        }

        private static bool TryReadApplicationVersionCurrent()
        {
            try
            {
                var avType = typeof(ApplicationVersion);
                var currentField = avType.GetField("Current",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (currentField != null)
                {
                    var v = currentField.GetValue(null);
                    if (v is ApplicationVersion av)
                    {
                        AssignFromApplicationVersion(av);
                        return _major >= 0;
                    }
                }

                var currentProp = avType.GetProperty("Current",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (currentProp != null && currentProp.CanRead)
                {
                    var v = currentProp.GetValue(null);
                    if (v is ApplicationVersion av)
                    {
                        AssignFromApplicationVersion(av);
                        return _major >= 0;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool TryReadModuleNativeVersion()
        {
            try
            {
                var mgrType = Type.GetType("TaleWorlds.ModuleManager.ModuleHelper, TaleWorlds.ModuleManager", false);
                if (mgrType == null) return false;

                var getInfo = mgrType.GetMethod("GetModuleInfo",
                    BindingFlags.Public | BindingFlags.Static);
                if (getInfo == null) return false;

                var info = getInfo.Invoke(null, new object[] { "Native" });
                if (info == null) return false;

                var versionProp = info.GetType().GetProperty("Version",
                    BindingFlags.Public | BindingFlags.Instance);
                if (versionProp == null) return false;

                var v = versionProp.GetValue(info);
                if (v is ApplicationVersion av)
                {
                    AssignFromApplicationVersion(av);
                    return _major >= 0;
                }
            }
            catch { }
            return false;
        }

        private static void AssignFromApplicationVersion(ApplicationVersion av)
        {
            try
            {
                var t = av.GetType();
                _major = ReadIntMember(t, av, "Major");
                _minor = ReadIntMember(t, av, "Minor");
                _revision = ReadIntMember(t, av, "Revision");
                if (_revision < 0) _revision = ReadIntMember(t, av, "Patch");
            }
            catch { }
        }

        private static int ReadIntMember(Type t, object obj, string name)
        {
            try
            {
                var p = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (p != null && p.CanRead && p.PropertyType == typeof(int))
                    return (int)p.GetValue(obj);
                var f = t.GetField(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && f.FieldType == typeof(int))
                    return (int)f.GetValue(obj);
            }
            catch { }
            return -1;
        }
    }
}
