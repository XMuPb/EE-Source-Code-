using System;
using System.IO;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace BandItPlus.Diagnostics
{
    // v215 (2026-05-31) — captures the actual exception the engine swallows during
    // save serialization. SaveResultType.GeneralFailure is generic; the real exception
    // is caught inside SaveManager.Save's try/catch and never surfaces.
    //
    // Architecture (zero-Heisenberg by design):
    //   - AppDomain.FirstChanceException is a CLR primitive (no IL rewriting, no Harmony,
    //     no by-ref/__args boxing — the trap that bit v206-v210's SaveDiagnosticPatch).
    //   - IsRecording is gated by SaveHandler.OnSaveStarted/OnSaveEnded — the two
    //     entry points the memory note bannerlord-harmony-args-serializer-heisenberg
    //     explicitly marks SAFE for Harmony postfixing (no __args, no __result).
    //   - During recording, every first-chance exception (caught OR uncaught) gets
    //     logged to bp_save_trap.log with type, message, full stack trace.
    //   - [ThreadStatic] re-entry guard prevents infinite recursion if the logging
    //     itself throws (File.AppendAllText can throw IOException → fires
    //     FirstChanceException → would loop without the guard).
    //
    // Expected flow on next save attempt:
    //   1. OnSaveStarted fires → IsRecording=true → header line in log
    //   2. Engine runs CollectObjects* methods → if any throws, handler logs full stack
    //   3. OnSaveEnded(false, "save036") fires → IsRecording=false → footer line
    //   4. Log file contains the actual exception type/site → root cause identified
    public static class SaveExceptionTrap
    {
        private static readonly string LogPath = BandItPlus.Diagnostics.BpDiag.P("bp_save_trap.log");

        public static volatile bool IsRecording;

        [ThreadStatic]
        private static bool _inHandler;

        private static readonly object _writeLock = new object();
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
                Log("=== SaveExceptionTrap v215 initialized ===");
            }
            catch (Exception ex)
            {
                BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log(
                    "SaveExceptionTrap init fail: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            if (!IsRecording) return;
            if (_inHandler) return;
            _inHandler = true;
            try
            {
                var ex = e.Exception;
                if (ex == null) return;
                // v216 upgrade: Exception.StackTrace only contains frames between throw
                // and the active catch (which Bannerlord puts ONE FRAME UP in SaveManager
                // / per-behavior CollectObjects wrappers — hiding the caller). Capture
                // Environment.StackTrace too — that gives the FULL current thread chain
                // including the BP/vanilla behavior whose CollectObjectsFor* method is
                // currently iterating. The behavior name in the env-stack tells us
                // WHICH [SaveableField] container holds the null.
                string envStack;
                try { envStack = Environment.StackTrace; }
                catch { envStack = "<env-stack capture failed>"; }
                Log(string.Format(
                    "[FIRST-CHANCE] {0}: {1}\n  Throw-to-catch stack:\n{2}\n  Full thread stack:\n{3}",
                    ex.GetType().FullName ?? "<unknown>",
                    ex.Message ?? "<no message>",
                    ex.StackTrace ?? "<no stack>",
                    envStack));
            }
            catch { }
            finally { _inHandler = false; }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (_writeLock)
                {
                    File.AppendAllText(LogPath,
                        "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + "\n");
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SaveHandler), "OnSaveStarted")]
    public static class SaveStartedTrap
    {
        public static void Postfix()
        {
            SaveExceptionTrap.IsRecording = true;
            SaveExceptionTrap.Log("=== OnSaveStarted fired — recording window OPEN ===");
        }
    }

    // NOTE: Postfix parameter NAMES must match the original method's parameter names
    // exactly — Harmony binds by name, not position. Real signature in v1.4.1:
    //   void OnSaveEnded(bool isSaveSuccessful, string newSaveGameName)
    // Using wrong names ("successful", "saveName") crashes PatchAll() at startup
    // with: 'Parameter "successful" not found in method ...OnSaveEnded(...)'.
    [HarmonyPatch(typeof(SaveHandler), "OnSaveEnded")]
    public static class SaveEndedTrap
    {
        public static void Postfix(bool isSaveSuccessful, string newSaveGameName)
        {
            SaveExceptionTrap.Log(string.Format(
                "=== OnSaveEnded successful={0} save={1} — recording window CLOSED ===",
                isSaveSuccessful, newSaveGameName ?? "<null>"));
            SaveExceptionTrap.IsRecording = false;
        }
    }
}
