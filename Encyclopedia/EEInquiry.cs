using System;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Thin wrapper around <c>InformationManager.ShowTextInquiry</c> that also arms the
    /// deferred preview-constrain pass.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// The native TextInquiry renders its description/preview text with NO height limit,
    /// and both RichTextWidget and TextWidget bypass ClipRect during the engine's render
    /// pass. A long preview therefore paints across the whole screen, over and outside the
    /// popup frame. The cure is <see cref="EncyclopediaEditPopup.SchedulePreviewConstrain"/>,
    /// which finds the preview once the dialog is built, wraps it in a fixed-height
    /// ScrollablePanel and tags it so the scissor patch clips its rendering.
    ///
    /// That call was only being made from three of EE's ~14 inquiry sites. Every other
    /// popup carried the same latent leak - most visibly the LoreStory template dialog,
    /// but also the ones that build long StringBuilder lists (culture list, numbered list,
    /// occupation editor). Routing every EE inquiry through here means a new popup gets
    /// the protection by default instead of by remembering.
    ///
    /// DELIBERATELY NOT a Harmony patch on InformationManager.ShowTextInquiry: that would
    /// also reach into vanilla and other mods' dialogs and restyle their previews, which is
    /// not EE's business.
    ///
    /// This type must NOT live in EditableEncyclopediaPatches.cs. The call sites there were
    /// migrated to this wrapper by a whole-file substitution; had the wrapper lived in the
    /// same file, its own inner call would have been rewritten to call itself.
    /// </summary>
    internal static class EEInquiry
    {
        /// <summary>
        /// Shows a text inquiry and schedules the preview-constrain pass.
        /// Parameter list mirrors <c>InformationManager.ShowTextInquiry</c> so call sites
        /// needed only the receiver renamed.
        /// </summary>
        internal static void Show(TextInquiryData data,
                                  bool pauseGameActiveState = false,
                                  bool shouldClearPreviousOnes = false)
        {
            InformationManager.ShowTextInquiry(data, pauseGameActiveState, shouldClearPreviousOnes);

            // Safe to call even when the caller also schedules it: SchedulePreviewConstrain
            // just resets the retry counter, and the wrap helpers skip widgets already
            // tagged with the preview marker ids, so nothing gets wrapped twice.
            try
            {
                EncyclopediaEditPopup.SchedulePreviewConstrain();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("EEInquiry: SchedulePreviewConstrain failed: " + ex.ToString());
            }
        }
    }
}
