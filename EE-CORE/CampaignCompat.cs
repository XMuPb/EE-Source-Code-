using System;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Compatibility shim for the Bannerlord campaign party-AI surface
    /// (<c>TaleWorlds.CampaignSystem.Actions.SetPartyAiAction</c>).
    ///
    /// Verified by diffing the full public surface of both engine builds
    /// (changeset 110062 vs 117484). Across every type consuming mods touch here,
    /// exactly ONE method the AI layer calls changed shape:
    ///
    ///   1.3.15  GetActionForRaidingSettlement(MobileParty owner, Settlement settlement,
    ///                                         NavigationType navigationType, bool isFromPort)
    ///   1.4.7   GetActionForRaidingSettlement(MobileParty owner, Settlement settlement,
    ///                                         NavigationType navigationType, bool isFromPort,
    ///                                         bool isTargetingPort)
    ///
    /// 1.4.7 APPENDED <c>isTargetingPort</c>. Every sibling method
    /// (GetActionForEngagingParty, GetActionForVisitingSettlement,
    /// GetActionForBesiegingSettlement, GetActionForPatrollingAroundPoint,
    /// GetActionForDefendingSettlement, GetActionForEscortingParty) is byte-identical
    /// between the two versions, parameter names included — they need no shim.
    ///
    /// WHY THIS MATTERS MORE THAN ONE PARAMETER SUGGESTS:
    /// the .NET JIT resolves every method token in a function body when that function is
    /// FIRST ENTERED, not when the offending line executes. A single unresolvable call
    /// therefore kills the entire enclosing method before any of it runs. In practice one
    /// missing overload buried in a long AI routine reads to the player as "the mod does
    /// nothing at all". Routing the call through reflection removes the unresolvable token,
    /// so the surrounding code JITs and runs normally on both engines.
    ///
    /// NOTE: no <c>[MethodImpl(NoInlining)]</c> guard is required here, because the missing
    /// item is a method OVERLOAD, not a TYPE. <c>MobileParty.NavigationType</c> resolves on
    /// both versions (identical values: None, Default, Naval, All), so it is safe to name in
    /// this class's public signature. Contrast <see cref="EncyclopediaCompat"/>, which shims a
    /// genuine type removal and does need isolation.
    ///
    /// All resolution is reflection-based and happens once, so EE-Core itself loads cleanly on
    /// any engine version — including future ones where these methods may change again.
    /// </summary>
    public static class CampaignCompat
    {
        /// <summary>Which shape of the raid API the running engine exposes.</summary>
        public enum RaidApiMode
        {
            Unknown = 0,
            /// <summary>1.3.x — four parameters, no isTargetingPort.</summary>
            FourArg_1_3 = 1,
            /// <summary>1.4.x — five parameters, trailing isTargetingPort.</summary>
            FiveArg_1_4 = 2,
            /// <summary>Method not found at all; calls become no-ops.</summary>
            Missing = 3
        }

        private const string SetPartyAiActionTypeName =
            "TaleWorlds.CampaignSystem.Actions.SetPartyAiAction";
        private const string RaidMethodName = "GetActionForRaidingSettlement";

        private static readonly object _lock = new object();
        private static bool _detectionRan;

        private static RaidApiMode _raidMode = RaidApiMode.Unknown;
        private static MethodInfo _raidMethod;
        private static bool _loggedRaidFailure;

        /// <summary>
        /// Active raid-API mode. Triggers detection on first access.
        /// </summary>
        public static RaidApiMode RaidMode
        {
            get { DetectIfNeeded(); return _raidMode; }
        }

        /// <summary>
        /// True when the engine accepts the <c>isTargetingPort</c> argument (1.4.x).
        /// When false, callers' port-targeting intent is silently dropped — a raid on a
        /// coastal settlement still happens, it just does not specifically target the port.
        /// </summary>
        public static bool SupportsPortTargeting
        {
            get { DetectIfNeeded(); return _raidMode == RaidApiMode.FiveArg_1_4; }
        }

        /// <summary>
        /// Runs detection once and caches the result. Idempotent and thread-safe.
        /// Mirrors <see cref="EncyclopediaCompat.DetectIfNeeded"/>'s contract.
        /// </summary>
        public static void DetectIfNeeded()
        {
            if (_detectionRan) return;
            lock (_lock)
            {
                if (_detectionRan) return;
                _detectionRan = true;

                try
                {
                    var type = ResolveType(SetPartyAiActionTypeName);
                    if (type == null)
                    {
                        _raidMode = RaidApiMode.Missing;
                        CompatLog("[CampaignCompat] WARNING: " + SetPartyAiActionTypeName
                            + " not found. Party-AI calls routed through this shim will no-op.");
                        return;
                    }

                    // Pick the richest overload present; the engine has only ever shipped one.
                    MethodInfo best = null;
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        if (methods[i].Name != RaidMethodName) continue;
                        if (best == null ||
                            methods[i].GetParameters().Length > best.GetParameters().Length)
                        {
                            best = methods[i];
                        }
                    }

                    if (best == null)
                    {
                        _raidMode = RaidApiMode.Missing;
                        CompatLog("[CampaignCompat] WARNING: " + RaidMethodName
                            + " not found on " + type.FullName + ". Raid orders will no-op. "
                            + "Available GetActionFor* methods: " + ListActionMethods(type));
                        return;
                    }

                    _raidMethod = best;
                    int count = best.GetParameters().Length;

                    if (count >= 5)
                    {
                        _raidMode = RaidApiMode.FiveArg_1_4;
                        CompatLog("[CampaignCompat] Detected 5-arg " + RaidMethodName
                            + " (Bannerlord 1.4.x). Port targeting supported.");
                    }
                    else if (count == 4)
                    {
                        _raidMode = RaidApiMode.FourArg_1_3;
                        CompatLog("[CampaignCompat] Detected 4-arg " + RaidMethodName
                            + " (Bannerlord 1.3.x). isTargetingPort will be dropped; "
                            + "coastal raids proceed without explicit port targeting.");
                    }
                    else
                    {
                        _raidMode = RaidApiMode.Missing;
                        _raidMethod = null;
                        CompatLog("[CampaignCompat] WARNING: " + RaidMethodName + " has an "
                            + "unexpected parameter count (" + count + "). Raid orders will no-op.");
                    }
                }
                catch (Exception ex)
                {
                    _raidMode = RaidApiMode.Missing;
                    _raidMethod = null;
                    CompatLog("[CampaignCompat] DetectIfNeeded error: " + ex);
                }
            }
        }

        /// <summary>
        /// Issues a raid order that works on both Bannerlord 1.3.x and 1.4.x.
        ///
        /// On 1.4.x this forwards all five arguments unchanged. On 1.3.x the trailing
        /// <paramref name="isTargetingPort"/> is dropped, because the parameter does not exist.
        /// </summary>
        /// <returns>
        /// True if an order was dispatched; false if the engine method could not be resolved.
        /// Never throws — a failure here must not take down a campaign tick.
        /// </returns>
        public static bool RaidSettlement(
            MobileParty owner,
            Settlement settlement,
            MobileParty.NavigationType navigationType,
            bool isFromPort,
            bool isTargetingPort)
        {
            if (owner == null || settlement == null) return false;

            DetectIfNeeded();
            if (_raidMethod == null) return false;

            try
            {
                object[] args;
                if (_raidMode == RaidApiMode.FiveArg_1_4)
                {
                    args = new object[] { owner, settlement, navigationType, isFromPort, isTargetingPort };
                }
                else if (_raidMode == RaidApiMode.FourArg_1_3)
                {
                    args = new object[] { owner, settlement, navigationType, isFromPort };
                }
                else
                {
                    return false;
                }

                _raidMethod.Invoke(null, args);
                return true;
            }
            catch (Exception ex)
            {
                // Log once. A raid order failing every tick must not spam the log to gigabytes.
                if (!_loggedRaidFailure)
                {
                    _loggedRaidFailure = true;
                    CompatLog("[CampaignCompat] RaidSettlement invoke failed (logged once): " + ex);
                }
                return false;
            }
        }

        /// <summary>
        /// Stamps a version banner into debug-compat.log for a module that depends on EE-Core.
        /// Call this once from the dependent mod's OnSubModuleLoad.
        ///
        /// The point is support: when someone posts a log, the very first lines should say
        /// which build of everything was actually running. Guessing at versions from symptoms
        /// wastes more time than any other single thing in mod support.
        ///
        /// Produces a line like:
        ///   [Versions] CompanionLeadArmy v2.1.0 | EE-Core v2.6.1.1 | Bannerlord v1.4.7
        ///
        /// Never throws.
        /// </summary>
        /// <param name="moduleId">Module id as it appears in SubModule.xml, e.g. "CompanionLeadArmy".</param>
        public static void LogModuleBanner(string moduleId)
        {
            try
            {
                string mod = BannerlordVersion.GetModuleVersion(moduleId);
                string core = BannerlordVersion.GetModuleVersion("EE-Core");
                string game = BannerlordVersion.GameVersionString;

                CompatLog("[Versions] " + moduleId + " " + mod
                    + " | EE-Core " + core
                    + " | Bannerlord " + game);
            }
            catch { /* a diagnostic must never break its caller */ }
        }

        /// <summary>
        /// Human-readable state of every binding, for debug.log / support requests.
        /// Call after a campaign loads to confirm the shim bound correctly.
        /// </summary>
        public static string Describe()
        {
            DetectIfNeeded();
            var sb = new StringBuilder();
            sb.AppendLine("=== CampaignCompat ===");
            sb.AppendLine("  RaidApiMode          : " + _raidMode);
            sb.AppendLine("  SupportsPortTargeting: " + SupportsPortTargeting);
            sb.AppendLine("  Bound method         : " +
                (_raidMethod == null
                    ? "<none>"
                    : _raidMethod.DeclaringType.FullName + "." + _raidMethod.Name
                      + "(" + _raidMethod.GetParameters().Length + " params)"));
            return sb.ToString();
        }

        // ── helpers ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a type by full name across every loaded assembly. Mirrors the approach in
        /// <see cref="EncyclopediaCompat"/> so both shims behave identically when an engine
        /// type moves between assemblies (which TaleWorlds does periodically).
        /// </summary>
        private static Type ResolveType(string fullName)
        {
            var direct = Type.GetType(fullName, false);
            if (direct != null) return direct;

            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    var t = asms[i].GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { /* dynamic or unloadable assembly — skip */ }
            }
            return null;
        }

        private static string ListActionMethods(Type type)
        {
            try
            {
                var sb = new StringBuilder();
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (!methods[i].Name.StartsWith("GetActionFor", StringComparison.Ordinal)) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(methods[i].Name).Append('/').Append(methods[i].GetParameters().Length);
                }
                return sb.Length == 0 ? "<none>" : sb.ToString();
            }
            catch { return "<enumeration failed>"; }
        }

        /// <summary>
        /// Writes to debug-compat.log independently of MCM settings, then best-effort to the
        /// normal EECoreLogger channel. Matches <see cref="EncyclopediaCompat"/>'s CompatLog so
        /// both shims land in the same file in load order.
        /// </summary>
        private static void CompatLog(string msg)
        {
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

            try { EECoreLogger.For("EE-Core").Debug(msg); } catch { }
        }
    }
}
