using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Shows Bannerlord's native TextInquiry dialog for editing encyclopedia text.
    /// Handles deferred show (must run on main thread) and post-show hooks for
    /// portrait injection, character limit override, and preview constraining.
    /// </summary>
    internal static class EditPopupInjector
    {
        // State
        private static bool _isOpen;

        // Deferred show — must run on main thread.
        // All _pending* fields are volatile so the main thread sees the latest
        // payload after observing _showPending=true (write-release / read-acquire).
        private static volatile bool _showPending;
        private static volatile string _pendingTitle;
        private static volatile string _pendingText;
        private static volatile string _pendingDescription;
        private static volatile string _pendingConfirmText;
        private static volatile string _pendingTipText;
        private static volatile int _pendingMaxLength;
        private static volatile Action<string> _pendingOnConfirm;
        private static volatile Action _pendingOnCancel;
        private static volatile Action _pendingOnFailed;

        public static bool IsOpen => _isOpen;

        /// <summary>
        /// Schedules the editor to show on the next main-thread tick.
        /// Safe to call from any thread / callback context.
        /// </summary>
        /// <param name="description">Optional separate description text shown as preview above the input.
        /// When null, currentText is used as both preview and input text.</param>
        /// <param name="confirmText">Optional custom text for the confirm button (default: "Done").</param>
        /// <param name="tipText">Optional custom tip text shown above the input field.
        /// When null, the default edit_tips localization is used.</param>
        public static void ScheduleShow(string title, string currentText, int maxLength,
            Action<string> onConfirm, Action onCancel, Action onFailed = null,
            string description = null, string confirmText = null, string tipText = null)
        {
            _pendingTitle = title;
            _pendingText = currentText;
            _pendingDescription = description;
            _pendingConfirmText = confirmText;
            _pendingTipText = tipText;
            _pendingMaxLength = maxLength;
            _pendingOnConfirm = onConfirm;
            _pendingOnCancel = onCancel;
            _pendingOnFailed = onFailed;
            _showPending = true;
        }

        /// <summary>
        /// Must be called from OnApplicationTick on the main game thread.
        /// </summary>
        public static void TickMainThread()
        {
            if (!_showPending) return;
            _showPending = false;

            bool ok = Show(_pendingTitle, _pendingText, _pendingMaxLength,
                _pendingOnConfirm, _pendingOnCancel, _pendingDescription,
                _pendingConfirmText, _pendingTipText);
            if (!ok)
            {
                MCMSettings.DebugLog("EditPopupInjector: Show failed, invoking onFailed callback");
                _pendingOnFailed?.Invoke();
            }

            _pendingTitle = null;
            _pendingText = null;
            _pendingDescription = null;
            _pendingConfirmText = null;
            _pendingTipText = null;
            _pendingOnConfirm = null;
            _pendingOnCancel = null;
            _pendingOnFailed = null;
        }

        public static bool Show(string title, string currentText, int maxLength,
            Action<string> onConfirm, Action onCancel, string description = null,
            string confirmText = null, string tipText = null)
        {
            try
            {
                string inputText = currentText ?? "";
                string doneText = confirmText ?? Localization.L("edit_done");

                // Pass inputText as the 2nd param — this is the game's initial editable text.
                // The game's onConfirm callback returns whatever text is in this field when
                // Done is pressed. Previously we passed previewText here which caused the
                // callback to always return the old text, ignoring user edits.
                var inquiryData = new TextInquiryData(
                    title, inputText, true, true,
                    doneText, Localization.L("edit_cancel"),
                    delegate (string newText)
                    {
                        _isOpen = false;
                        // The game's VM may return stale text (the initial value).
                        // Read the actual widget text as the authoritative source.
                        string widgetText = EncyclopediaEditPopup.ReadEditableWidgetText();
                        string finalText = !string.IsNullOrEmpty(widgetText) ? widgetText : newText;
                        MCMSettings.DebugLog("EditPopupInjector: onConfirm vmText=" + newText?.Length
                            + " widgetText=" + widgetText?.Length + " using=" + finalText?.Length);
                        onConfirm?.Invoke(finalText);
                    },
                    delegate { _isOpen = false; onCancel?.Invoke(); },
                    false, null);

                InformationManager.ShowTextInquiry(inquiryData, false, false);
                _isOpen = true;

                // Store custom tip text for the deferred tip injection
                EncyclopediaEditPopup.PendingCustomTipText = tipText;

                // Post-show hooks: override character limit, constrain preview, inject portrait.
                // Do NOT pass inputText to ScheduleCharLimitOverride — the TextInquiryData
                // constructor already has it as the initial text. Setting it again via reflection
                // desyncs the widget from the VM, causing the VM to return stale text on confirm.
                EncyclopediaEditPopup.ScheduleCharLimitOverride(maxLength > 0 ? maxLength : 10000);
                EncyclopediaEditPopup.SchedulePreviewConstrain();
                EncyclopediaEditPopup.SchedulePortraitInjection();

                // Re-navigate back since ShowTextInquiry navigates to Home
                EncyclopediaNavigationGuard.ScheduleNavigateBack();

                MCMSettings.DebugLog("EditPopupInjector: showed native TextInquiry: " + title);
                return true;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EditPopupInjector: Show error: " + ex.ToString());
                return false;
            }
        }

        public static void Close()
        {
            _isOpen = false;
        }
    }
}
