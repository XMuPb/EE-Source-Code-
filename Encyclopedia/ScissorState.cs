using System;
using System.Reflection;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Tracks the engine's current scissor rectangle so EE's clip patches can RESTORE it
    /// instead of wiping it.
    ///
    /// THE BUG THIS FIXES
    /// ------------------
    /// EE pushes a scissor rect before rendering the enlarged edit field and the wrapped
    /// preview, so their text cannot paint outside the popup. Afterwards it called
    /// TwoDimensionContext.ResetScissor() to undo that.
    ///
    /// ResetScissor() does not pop a stack - it CLEARS clipping outright. Gauntlet sets its
    /// own scissor for the regions it draws (the encyclopedia page's scrolling body, panel
    /// frames). Once EE reset it, every widget drawn later in that frame rendered with NO
    /// clip at all. That is why the encyclopedia page's description text painted straight
    /// through the popup's input field and across the Cancel / Done buttons: not the popup
    /// leaking out, but the PAGE losing the clip EE had removed.
    ///
    /// TwoDimensionContext exposes only SetScissor(ScissorTestInfo) and ResetScissor() -
    /// verified against both 1.3.15 and 1.4.7 assemblies, there is no getter, not even a
    /// private field. So the current rect cannot be read back; it has to be recorded as it
    /// goes past. A Harmony postfix on SetScissor stores the last rect, a postfix on
    /// ResetScissor clears it, and EE's finalizers restore whatever was in effect before.
    ///
    /// COST: SetScissor is a per-frame hot path (tens to low hundreds of calls). The postfix
    /// only assigns two fields, so the overhead is on the order of microseconds per frame.
    /// [ThreadStatic] keeps it correct if the UI ever renders off the main thread.
    /// </summary>
    internal static class ScissorState
    {
        [ThreadStatic] private static object _current;
        [ThreadStatic] private static bool _hasCurrent;

        private static bool _applied;
        private static MethodInfo _setScissor;
        private static MethodInfo _resetScissor;

        /// <summary>True if the engine had a scissor active at this moment.</summary>
        internal static bool HasCurrent { get { return _hasCurrent; } }

        /// <summary>The last ScissorTestInfo passed to SetScissor, boxed. Null if none.</summary>
        internal static object Current { get { return _current; } }

        /// <summary>
        /// Installs the tracking postfixes. Safe to call more than once.
        /// Returns false if the engine methods could not be resolved, in which case callers
        /// should keep using ResetScissor and accept the old behaviour rather than crash.
        /// </summary>
        internal static bool TryApply(HarmonyLib.Harmony harmony, object twoDimensionContext)
        {
            if (_applied) return true;
            if (harmony == null || twoDimensionContext == null) return false;

            try
            {
                var ctxType = twoDimensionContext.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _setScissor = ctxType.GetMethod("SetScissor", flags);
                _resetScissor = ctxType.GetMethod("ResetScissor", flags);
                if (_setScissor == null || _resetScissor == null)
                {
                    MCMSettings.DebugLog("ScissorState: SetScissor/ResetScissor not found on "
                        + ctxType.FullName + " - restore disabled");
                    return false;
                }

                var onSet = typeof(ScissorState).GetMethod(nameof(OnSetScissor),
                    BindingFlags.Static | BindingFlags.NonPublic);
                var onReset = typeof(ScissorState).GetMethod(nameof(OnResetScissor),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(_setScissor, postfix: new HarmonyLib.HarmonyMethod(onSet));
                harmony.Patch(_resetScissor, postfix: new HarmonyLib.HarmonyMethod(onReset));

                _applied = true;
                MCMSettings.DebugLog("ScissorState: tracking SetScissor/ResetScissor for restore");
                return true;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ScissorState: TryApply failed: " + ex.ToString());
                return false;
            }
        }

        // Harmony postfix - records whatever rect the engine (or we) just set.
        private static void OnSetScissor(object __0)
        {
            _current = __0;
            _hasCurrent = true;
        }

        // Harmony postfix - the engine cleared clipping.
        private static void OnResetScissor()
        {
            _current = null;
            _hasCurrent = false;
        }

        /// <summary>
        /// Restores a previously captured scissor state on the given context.
        /// Pass the values captured BEFORE the caller set its own rect.
        ///
        /// Use this instead of calling ResetScissor() directly: resetting removes the clip
        /// the engine still needs for everything drawn after us.
        /// </summary>
        internal static void Restore(object twoDimensionContext, bool hadPrevious, object previous)
        {
            if (twoDimensionContext == null) return;
            try
            {
                if (hadPrevious && previous != null && _setScissor != null)
                    _setScissor.Invoke(twoDimensionContext, new object[] { previous });
                else if (_resetScissor != null)
                    _resetScissor.Invoke(twoDimensionContext, null);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("ScissorState: Restore failed: " + ex.ToString());
            }
        }
    }
}
