using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Replaces the native plain-text description widget with a formatted
    /// widget tree that supports **bold**, *italic*, [[CrossRef]], {#color},
    /// and --- dividers. Uses the same segment-based rendering as LoreSectionInjector.
    /// </summary>
    internal static class DescriptionFormatter
    {
        private const int MaxRetries = 8;
        private const int RetryDelayMs = 350;
        private const int WordWrapChars = 110;
        private const string FormattedContainerId = "EditableEncyclopedia_FormattedDesc";
        private const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // _pendingText/_pendingShowIndicator must be volatile so the main thread sees
        // the latest payload after observing _formatPending=true (Theme 1 fix).
        private static volatile string _pendingText;
        private static volatile bool _pendingShowIndicator;
        private static int _retryCount;
        private static volatile bool _formatPending;
        private static volatile bool _clearPending;
        private static System.Threading.Timer _retryTimer;

        /// <summary>
        /// Schedules a deferred description widget replacement.
        /// Called after the VM text is set, before the UI tree is fully built.
        /// </summary>
        /// <summary>
        /// Removes any existing formatted description container from the encyclopedia widget tree.
        /// Called before page refresh after a save to ensure the old formatted text doesn't persist
        /// when the new text has no formatting (plain text).
        /// </summary>
        public static void ClearFormattedContainer()
        {
            try
            {
                var topScreen = TaleWorlds.ScreenSystem.ScreenManager.TopScreen;
                if (topScreen == null) return;
                foreach (var layer in topScreen.Layers)
                {
                    if (!(layer is TaleWorlds.Engine.GauntletUI.GauntletLayer gl)) continue;
                    Widget root = LoreSectionInjector.GetLayerRootWidget(gl);
                    if (root == null) continue;
                    RemoveFormattedContainers(root, 0);
                }
            }
            catch (Exception ex) { MCMSettings.DebugLog("DescriptionFormatter.ClearFormattedContainer: " + ex.ToString()); }
        }

        private static void RemoveFormattedContainers(Widget parent, int depth)
        {
            if (parent == null || depth > 20) return;
            for (int i = parent.ChildCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (child?.Id == FormattedContainerId)
                {
                    parent.RemoveChild(child);
                    MCMSettings.DebugLog("DescriptionFormatter: cleared old container from tree");
                }
                else
                {
                    RemoveFormattedContainers(child, depth + 1);
                }
            }
        }

        /// <summary>
        /// Requests a deferred clear of any formatted description container.
        /// Safe to call from any thread — the actual clear runs on the next main-thread Tick().
        /// </summary>
        public static void RequestClear()
        {
            _clearPending = true;
        }

        public static void ScheduleFormat(string rawMarkdown, bool showIndicator)
        {
            _pendingText = rawMarkdown;
            _pendingShowIndicator = showIndicator;
            _retryCount = 0;
            ScheduleRetry();
        }

        private static void ScheduleRetry()
        {
            _retryTimer?.Dispose();
            _retryTimer = new System.Threading.Timer(_ =>
            {
                _formatPending = true; // Signal main thread to do the work
            }, null, RetryDelayMs, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// Must be called from the main game thread (e.g., OnApplicationTick).
        /// Processes deferred formatting when the flag is set.
        /// </summary>
        public static void Tick()
        {
            // Process deferred clear request (after re-edit save)
            if (_clearPending)
            {
                _clearPending = false;
                ClearFormattedContainer();
            }

            if (!_formatPending) return;
            _formatPending = false;

            if (string.IsNullOrEmpty(_pendingText)) return;
            if (!EncyclopediaPageTracker.IsEncyclopediaOpen())
            {
                _pendingText = null;
                return;
            }

            _retryCount++;

            try
            {
                // Search all screen layers for the description widget
                string strippedText = MarkdownFormatter.StripMarkers(_pendingText);
                string searchText = strippedText.Length > 30 ? strippedText.Substring(0, 30) : strippedText;

                Widget descWidget = null;
                var topScreen = ScreenManager.TopScreen;
                if (topScreen != null)
                {
                    foreach (var layer in topScreen.Layers)
                    {
                        if (!(layer is GauntletLayer gl)) continue;
                        Widget root = LoreSectionInjector.GetLayerRootWidget(gl);
                        if (root == null) continue;
                        descWidget = FindWidgetByTextContent(root, searchText);
                        if (descWidget != null) break;
                        // Try edited prefix
                        if (_pendingShowIndicator)
                        {
                            string pfx = Localization.L("edited_prefix")?.Trim() ?? "[Edited]";
                            descWidget = FindWidgetByTextContent(root, pfx);
                            if (descWidget != null) break;
                        }
                    }
                }

                if (descWidget == null)
                {
                    if (_retryCount < MaxRetries) ScheduleRetry();
                    else MCMSettings.DebugLog("DescriptionFormatter: widget not found after " + MaxRetries + " retries");
                    return;
                }

                Widget parent = descWidget.ParentWidget;
                if (parent == null) return;

                // Remove old formatted container if it exists (re-edit scenario)
                for (int c = parent.ChildCount - 1; c >= 0; c--)
                {
                    var child = parent.GetChild(c);
                    if (child?.Id == FormattedContainerId)
                    {
                        parent.RemoveChild(child);
                        MCMSettings.DebugLog("DescriptionFormatter: removed old formatted container for re-format");
                    }
                }

                int idx = FindChildIndex(parent, descWidget);
                UIContext uiContext = descWidget.Context;

                // Get the brush from the original widget
                Brush contentBrush = null;
                try
                {
                    var brushProp = descWidget.GetType().GetProperty("Brush", AllFlags);
                    if (brushProp != null)
                        contentBrush = brushProp.GetValue(descWidget) as Brush;
                }
                catch { }

                // Extract hint text from the native widget (e.g. "[E Edit | N Name | ...]")
                string nativeText = TryGetText(descWidget) ?? "";
                string hintText = null;
                int hintStart = nativeText.LastIndexOf("\n[");
                if (hintStart >= 0)
                    hintText = nativeText.Substring(hintStart + 1);

                // Build formatted container
                string fullText = _pendingShowIndicator
                    ? (Localization.L("edited_prefix") ?? "") + _pendingText
                    : _pendingText;

                Widget container = BuildFormattedDescription(uiContext, fullText, contentBrush);
                if (container == null) return;

                // Prepend timestamp if available
                string tsText = TimestampWidgetInjector.CurrentText;
                if (!string.IsNullOrEmpty(tsText))
                {
                    var tsW = new TextWidget(uiContext);
                    tsW.Text = tsText;
                    tsW.WidthSizePolicy = SizePolicy.StretchToParent;
                    tsW.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentBrush != null)
                    {
                        var tsBrush = contentBrush.Clone();
                        SetBrushFontColor(tsBrush, new Color(0.55f, 0.50f, 0.40f, 0.8f));
                        tsBrush.FontSize = Math.Max(14, tsBrush.FontSize - 2);
                        tsW.Brush = tsBrush;
                    }
                    LoreSectionInjector.ForceTextAlignLeft(tsW);
                    container.AddChildAtIndex(tsW, 0);
                    // Hide the native timestamp widget so we don't get duplicates
                    TimestampWidgetInjector.Clear();
                }

                // Append hotkey hints at the bottom — single line, smaller font
                if (!string.IsNullOrEmpty(hintText))
                {
                    var hintWidget = new TextWidget(uiContext);
                    hintWidget.Text = hintText;
                    hintWidget.WidthSizePolicy = SizePolicy.StretchToParent;
                    hintWidget.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (contentBrush != null)
                    {
                        var hintBrush = contentBrush.Clone();
                        SetBrushFontColor(hintBrush, new Color(0.5f, 0.48f, 0.42f, 0.6f));
                        // Scale font down to fit hints on one line
                        try
                        {
                            var fsProp = hintBrush.GetType().GetProperty("FontSize", AllFlags);
                            if (fsProp != null)
                            {
                                var fsVal = fsProp.GetValue(hintBrush);
                                if (fsVal != null)
                                {
                                    int fs = (int)fsVal;
                                    fsProp.SetValue(hintBrush, Math.Max(14, fs - 4));
                                }
                            }
                        }
                        catch { }
                        hintWidget.Brush = hintBrush;
                    }
                    LoreSectionInjector.ForceTextAlignLeft(hintWidget);
                    container.AddChild(hintWidget);
                }

                // Copy layout properties from the native widget to match positioning
                container.MarginLeft = descWidget.MarginLeft;
                container.MarginRight = descWidget.MarginRight;
                container.MarginTop = descWidget.MarginTop;
                container.MarginBottom = descWidget.MarginBottom;
                container.HorizontalAlignment = descWidget.HorizontalAlignment;

                // Hide the original widget but keep it in the tree for TimestampOverlay anchor
                descWidget.IsVisible = false;
                if (idx >= 0 && idx < parent.ChildCount)
                    parent.AddChildAtIndex(container, idx);
                else
                    parent.AddChild(container);

                MCMSettings.DebugLog("DescriptionFormatter: replaced description with formatted version (" + _retryCount + " attempts)");
                _pendingText = null;
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("DescriptionFormatter: error: " + ex.ToString());
                if (_retryCount < MaxRetries) ScheduleRetry();
            }
        }

        private static Widget BuildFormattedDescription(UIContext uiContext, string text, Brush baseBrush)
        {
            var container = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                          ?? new Widget(uiContext);
            container.Id = FormattedContainerId;
            container.WidthSizePolicy = SizePolicy.StretchToParent;
            container.HeightSizePolicy = SizePolicy.CoverChildren;
            // Bug 2026-05-25: must be TopToBottom — formatted description paragraphs
            // and wrapped lines are added in source order via forward loops and
            // must render top-down (prose reads top-to-bottom).
            LoreSectionInjector.SetVerticalLayoutTopToBottom(container);

            // Split on inline --- dividers first to separate paragraphs
            var paragraphs = new List<string>();
            foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                if (line.Contains("---"))
                {
                    // Split around --- markers
                    int idx = 0;
                    while (idx < line.Length)
                    {
                        int dashPos = line.IndexOf("---", idx, StringComparison.Ordinal);
                        if (dashPos < 0)
                        {
                            string remainder = line.Substring(idx);
                            if (!string.IsNullOrWhiteSpace(remainder))
                                paragraphs.Add(remainder);
                            break;
                        }
                        if (dashPos > idx)
                        {
                            string before = line.Substring(idx, dashPos - idx);
                            if (!string.IsNullOrWhiteSpace(before))
                                paragraphs.Add(before);
                        }
                        paragraphs.Add("---");
                        idx = dashPos + 3;
                    }
                }
                else
                {
                    paragraphs.Add(line);
                }
            }

            foreach (string para in paragraphs)
            {
                if (string.IsNullOrWhiteSpace(para))
                {
                    var spacer = new Widget(uiContext);
                    spacer.WidthSizePolicy = SizePolicy.StretchToParent;
                    spacer.HeightSizePolicy = SizePolicy.Fixed;
                    spacer.SuggestedHeight = 8f;
                    container.AddChild(spacer);
                    continue;
                }

                if (para.Trim() == "---")
                {
                    // Paragraph break — just a spacer, no visible line
                    var spacerDiv = new Widget(uiContext);
                    spacerDiv.WidthSizePolicy = SizePolicy.StretchToParent;
                    spacerDiv.HeightSizePolicy = SizePolicy.Fixed;
                    spacerDiv.SuggestedHeight = 10f;
                    container.AddChild(spacerDiv);
                    continue;
                }

                // PLAIN paragraph: hand the whole thing to ONE TextWidget and let Gauntlet
                // wrap it to the panel's real width.
                //
                // It used to be hard-wrapped here at a fixed CHARACTER count (WordWrapChars),
                // with each resulting line in its own widget. That count has no relationship
                // to how wide the panel actually is, so the text broke early and rendered as
                // ragged short lines right after an edit or a reset. Navigating away and back
                // appeared to "fix the layout" only because the formatted container was gone
                // by then and the NATIVE widget was wrapping the text properly.
                //
                // Manual wrapping is still required below for FORMATTED lines, because those
                // become horizontal ListPanel rows which cannot wrap themselves.
                if (!MarkdownFormatter.HasFormatting(para))
                {
                    var plain = new TextWidget(uiContext);
                    plain.Text = para;
                    plain.WidthSizePolicy = SizePolicy.StretchToParent;
                    plain.HeightSizePolicy = SizePolicy.CoverChildren;
                    if (baseBrush != null) plain.Brush = baseBrush.Clone();
                    LoreSectionInjector.ForceTextAlignLeft(plain);
                    container.AddChild(plain);
                    continue;
                }

                // Word-wrap the paragraph, then parse each line for formatting
                var wrappedLines = LoreSectionInjector.WordWrapText(para, WordWrapChars);
                foreach (string wrappedLine in wrappedLines)
                {
                    if (!MarkdownFormatter.HasFormatting(wrappedLine))
                    {
                        // Plain text line
                        var tw = new TextWidget(uiContext);
                        tw.Text = wrappedLine;
                        tw.WidthSizePolicy = SizePolicy.StretchToParent;
                        tw.HeightSizePolicy = SizePolicy.CoverChildren;
                        if (baseBrush != null) tw.Brush = baseBrush.Clone();
                        LoreSectionInjector.ForceTextAlignLeft(tw);
                        container.AddChild(tw);
                        continue;
                    }

                    // Formatted line — build horizontal segment row
                    var segments = MarkdownFormatter.ParseLine(wrappedLine);
                    var segRow = LoreSectionInjector.TryCreateWidgetByType(uiContext, "ListPanel")
                               ?? new Widget(uiContext);
                    segRow.WidthSizePolicy = SizePolicy.StretchToParent;
                    segRow.HeightSizePolicy = SizePolicy.CoverChildren;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, segRow);

                    foreach (var seg in segments)
                    {
                        if (string.IsNullOrEmpty(seg.Text)) continue;

                        var segW = new TextWidget(uiContext);
                        segW.Text = seg.Text;
                        segW.WidthSizePolicy = SizePolicy.CoverChildren;
                        segW.HeightSizePolicy = SizePolicy.CoverChildren;

                        if (baseBrush != null)
                        {
                            var brush = baseBrush.Clone();
                            switch (seg.Style)
                            {
                                case MarkdownFormatter.SegmentStyle.Bold:
                                    SetBrushFontColor(brush, new Color(0.92f, 0.88f, 0.78f, 1f));
                                    break;
                                case MarkdownFormatter.SegmentStyle.Italic:
                                    SetBrushFontColor(brush, new Color(0.75f, 0.70f, 0.60f, 0.9f));
                                    break;
                                case MarkdownFormatter.SegmentStyle.CrossRef:
                                    SetBrushFontColor(brush, new Color(0.85f, 0.75f, 0.45f, 1f));
                                    break;
                                case MarkdownFormatter.SegmentStyle.Colored:
                                    if (seg.CustomColor.HasValue)
                                        SetBrushFontColor(brush, seg.CustomColor.Value);
                                    break;
                                case MarkdownFormatter.SegmentStyle.Strikethrough:
                                    SetBrushFontColor(brush, new Color(0.50f, 0.48f, 0.42f, 0.5f));
                                    break;
                                case MarkdownFormatter.SegmentStyle.Underline:
                                    SetBrushFontColor(brush, new Color(0.90f, 0.85f, 0.70f, 1f));
                                    break;
                                case MarkdownFormatter.SegmentStyle.Small:
                                    SetBrushFontColor(brush, new Color(0.60f, 0.55f, 0.45f, 0.8f));
                                    brush.FontSize = Math.Max(12, brush.FontSize - 4);
                                    break;
                                case MarkdownFormatter.SegmentStyle.Heading:
                                    SetBrushFontColor(brush, new Color(0.92f, 0.88f, 0.75f, 1f));
                                    brush.FontSize = brush.FontSize + 4;
                                    segW.WidthSizePolicy = SizePolicy.StretchToParent;
                                    break;
                                case MarkdownFormatter.SegmentStyle.Quote:
                                    SetBrushFontColor(brush, new Color(0.70f, 0.65f, 0.55f, 0.9f));
                                    segW.MarginLeft = 20f;
                                    segW.WidthSizePolicy = SizePolicy.StretchToParent;
                                    break;
                            }
                            segW.Brush = brush;
                        }

                        LoreSectionInjector.ForceTextAlignLeft(segW);
                        segRow.AddChild(segW);
                    }
                    container.AddChild(segRow);
                }
            }

            return container;
        }

        private static void SetBrushFontColor(Brush brush, Color color)
        {
            try
            {
                var fcProp = brush.GetType().GetProperty("FontColor", AllFlags);
                if (fcProp != null) fcProp.SetValue(brush, color);
            }
            catch { }
        }

        private static Widget FindWidgetByTextContent(Widget root, string searchText)
        {
            if (root == null || string.IsNullOrEmpty(searchText)) return null;

            string widgetText = TryGetText(root);
            if (widgetText != null && widgetText.Contains(searchText))
                return root;

            for (int i = 0; i < root.ChildCount; i++)
            {
                var found = FindWidgetByTextContent(root.GetChild(i), searchText);
                if (found != null) return found;
            }
            return null;
        }

        private static string TryGetText(Widget widget)
        {
            if (widget == null) return null;
            try
            {
                var textProp = widget.GetType().GetProperty("Text", AllFlags);
                return textProp?.GetValue(widget) as string;
            }
            catch { return null; }
        }

        private static int FindChildIndex(Widget parent, Widget child)
        {
            if (parent == null || child == null) return -1;
            for (int i = 0; i < parent.ChildCount; i++)
                if (parent.GetChild(i) == child) return i;
            return -1;
        }
    }
}
