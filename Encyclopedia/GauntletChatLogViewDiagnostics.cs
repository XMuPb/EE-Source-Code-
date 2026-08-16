using System;
using HarmonyLib;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Bug 2026-05-25 NavalDLC clan crash: user reports game crashes with NRE in
    /// TaleWorlds.MountAndBlade.GauntletUI.GauntletChatLogView.HandleInput. The
    /// finalizers below DO NOT suppress the exception — they only log full details
    /// to MCMSettings.DebugLog so we have a callstack written to disk BEFORE the
    /// game terminates (otherwise the crash buffer-flushes before the log records
    /// what threw).
    ///
    /// Pure instrumentation. No behavior change. Game crash still happens.
    /// </summary>
    public static class GauntletChatLogViewDiagnostics
    {
        private static bool _applied;

        public static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;
            try
            {
                // Use AccessTools.TypeByName so we don't need to add a new csproj
                // reference to TaleWorlds.MountAndBlade.GauntletUI.dll.
                var chatLogType = AccessTools.TypeByName("TaleWorlds.MountAndBlade.GauntletUI.GauntletChatLogView");
                if (chatLogType == null)
                {
                    try { MCMSettings.DebugLog("[ChatLogDiag] GauntletChatLogView type not found by AccessTools.TypeByName - patches NOT installed"); } catch { }
                    return;
                }

                var onLateTick = AccessTools.Method(chatLogType, "OnLateTick");
                if (onLateTick != null)
                {
                    var finalizer = new HarmonyMethod(typeof(GauntletChatLogViewDiagnostics), nameof(OnLateTickFinalizer));
                    harmony.Patch(onLateTick, finalizer: finalizer);
                    try { MCMSettings.DebugLog("[ChatLogDiag] patched GauntletChatLogView.OnLateTick with diagnostic finalizer"); } catch { }
                }
                else
                {
                    try { MCMSettings.DebugLog("[ChatLogDiag] GauntletChatLogView.OnLateTick method not found"); } catch { }
                }

                var handleInput = AccessTools.Method(chatLogType, "HandleInput");
                if (handleInput != null)
                {
                    var finalizer = new HarmonyMethod(typeof(GauntletChatLogViewDiagnostics), nameof(HandleInputFinalizer));
                    harmony.Patch(handleInput, finalizer: finalizer);
                    try { MCMSettings.DebugLog("[ChatLogDiag] patched GauntletChatLogView.HandleInput with diagnostic finalizer"); } catch { }
                }
                else
                {
                    try { MCMSettings.DebugLog("[ChatLogDiag] GauntletChatLogView.HandleInput method not found"); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { MCMSettings.DebugLog("[ChatLogDiag] TryApply error (suppressed): " + ex.ToString()); } catch { }
            }
        }

        public static Exception OnLateTickFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                try
                {
                    MCMSettings.DebugLog("[ChatLogDiag][CRITICAL] GauntletChatLogView.OnLateTick THREW: "
                        + __exception.GetType().FullName + ": " + __exception.Message
                        + "\n  InnerException: " + (__exception.InnerException == null ? "(none)" : __exception.InnerException.ToString())
                        + "\n  StackTrace:\n" + __exception.StackTrace);
                }
                catch { }
            }
            return __exception; // RE-THROW - pure instrumentation, no suppression
        }

        public static Exception HandleInputFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                try
                {
                    MCMSettings.DebugLog("[ChatLogDiag][CRITICAL] GauntletChatLogView.HandleInput THREW: "
                        + __exception.GetType().FullName + ": " + __exception.Message
                        + "\n  InnerException: " + (__exception.InnerException == null ? "(none)" : __exception.InnerException.ToString())
                        + "\n  StackTrace:\n" + __exception.StackTrace);
                }
                catch { }
            }
            return __exception; // RE-THROW
        }
    }
}
