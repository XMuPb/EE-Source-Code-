using System;
using System.IO;

namespace BandItPlus.Diagnostics
{
    // build channel. `dotnet build` = DEV (full diagnostics), `dotnet build -c Release`
    // = RELEASE for the Workshop (quiet logs, errors only). one code base, one flag.
    public static class BpBuild
    {
#if DEBUG
        public const bool IsDev = true;
#else
        public const bool IsDev = false;
#endif
    }

    // Wave 4.34 (2026-06-28): central debug-log directory for ALL BandIt Plus logs.
    // Logs now write to the canonical Bannerlord ModSettings folder instead of the old hardcoded
    // dev-machine path (H:\WORK STUFF FOR MODDING\bp_*.log). "Documents" resolves to the user's real
    // Documents folder (OneDrive-redirected if applicable), so this lands at:
    //   Documents\Mount and Blade II Bannerlord\Configs\ModSettings\Global\BandItPlus\
    // Use BpDiag.P("bp_xxx.log") to build a full log-file path.
    public static class BpDiag
    {
        private static string _dir;

        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                try
                {
                    string d = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "Mount and Blade II Bannerlord", "Configs", "ModSettings", "Global", "BandItPlus");
                    Directory.CreateDirectory(d); // File.AppendAllText does NOT create dirs — ensure it exists
                    _dir = d;
                }
                catch
                {
                    // Last-resort fallbacks so logging never throws on path/permission issues.
                    try { _dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
                    catch { _dir = "."; }
                }
                return _dir;
            }
        }

        public static string P(string fileName)
        {
            return Path.Combine(Dir, fileName);
        }

        // release builds keep the log files but only accept error-ish lines; dev
        // builds log everything. the save trap bypasses this on purpose — crash
        // forensics stay on for players too.
        public static bool ShouldWrite(string msg)
        {
            // CS0162 is expected in BOTH configurations, and the suppression must span
            // the whole body to cover both cases:
            //   Release (IsDev=false) — the early return below is unreachable.
            //   Debug   (IsDev=true)  — everything AFTER the early return is unreachable.
            // An earlier fix wrapped only the `if` line, which silenced Release but left
            // Debug reporting a warning at the filter. Intentional build switch —
            // suppressed rather than removed, so the dev/release logging split survives.
#pragma warning disable CS0162
            if (BpBuild.IsDev) return true;

            if (string.IsNullOrEmpty(msg)) return false;
            return msg.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("crash", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("averted", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("NRE", StringComparison.Ordinal) >= 0;
#pragma warning restore CS0162
        }
    }
}
