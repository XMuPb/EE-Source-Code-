using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Lightweight markdown parser for encyclopedia text fields.
    /// Supports: **bold**, *italic*, [[CrossRef]], {#RRGGBB}colored{/}, ---
    /// Converts to either typed text segments (for widget rendering) or HTML (for RichTextWidget).
    /// </summary>
    public static class MarkdownFormatter
    {
        /// <summary>
        /// A styled text segment for widget-based rendering.
        /// </summary>
        public class TextSegment
        {
            public string Text;
            public SegmentStyle Style;
            public Color? CustomColor;
        }

        public enum SegmentStyle
        {
            Normal,
            Bold,
            Italic,
            CrossRef,       // [[Name]] golden text
            Colored,        // {#RRGGBB}text{/} or named colors
            Divider,        // --- horizontal rule
            Strikethrough,  // ~~text~~
            Underline,      // __text__
            Heading,        // # text
            Quote,          // > text
            Small           // {small}text{/}
        }

        // Named color presets
        private static readonly Dictionary<string, Color> NamedColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "red",    new Color(0.80f, 0.20f, 0.20f, 1f) },
            { "blood",  new Color(0.55f, 0.10f, 0.10f, 1f) },
            { "gold",   new Color(0.83f, 0.69f, 0.22f, 1f) },
            { "amber",  new Color(0.85f, 0.65f, 0.13f, 1f) },
            { "blue",   new Color(0.29f, 0.56f, 0.85f, 1f) },
            { "green",  new Color(0.18f, 0.80f, 0.44f, 1f) },
            { "purple", new Color(0.56f, 0.27f, 0.68f, 1f) },
            { "silver", new Color(0.75f, 0.75f, 0.75f, 1f) },
            { "white",  new Color(0.95f, 0.95f, 0.95f, 1f) },
            { "orange", new Color(0.90f, 0.50f, 0.15f, 1f) },
            { "teal",   new Color(0.20f, 0.60f, 0.60f, 1f) },
            { "brown",  new Color(0.55f, 0.35f, 0.20f, 1f) },
        };

        // ── Markdown → Segments (for Lore section widget rendering) ──

        /// <summary>
        /// Parses a single line of text into styled segments.
        /// Recognizes: **bold**, *italic*, [[CrossRef]], {#RRGGBB}colored{/}
        /// </summary>
        public static List<TextSegment> ParseLine(string line)
        {
            var segments = new List<TextSegment>();
            if (string.IsNullOrEmpty(line)) return segments;

            // Check for divider line
            string trimmed = line.Trim();
            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                segments.Add(new TextSegment { Text = "", Style = SegmentStyle.Divider });
                return segments;
            }

            // Check for heading (# text)
            if (trimmed.StartsWith("# "))
            {
                segments.Add(new TextSegment { Text = trimmed.Substring(2), Style = SegmentStyle.Heading });
                return segments;
            }

            // Check for quote (> text)
            if (trimmed.StartsWith("> "))
            {
                segments.Add(new TextSegment { Text = trimmed.Substring(2), Style = SegmentStyle.Quote });
                return segments;
            }

            int i = 0;
            var plainBuf = new StringBuilder();

            while (i < line.Length)
            {
                // --- inline divider (must appear as exactly three dashes)
                if (line[i] == '-' && i + 2 < line.Length && line[i + 1] == '-' && line[i + 2] == '-'
                    && (i + 3 >= line.Length || line[i + 3] != '-'))
                {
                    FlushPlain(plainBuf, segments);
                    segments.Add(new TextSegment { Text = "", Style = SegmentStyle.Divider });
                    i += 3;
                    continue;
                }

                // **bold**
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                {
                    int end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        FlushPlain(plainBuf, segments);
                        segments.Add(new TextSegment
                        {
                            Text = line.Substring(i + 2, end - i - 2),
                            Style = SegmentStyle.Bold
                        });
                        i = end + 2;
                        continue;
                    }
                }

                // *italic* (but not **)
                if (line[i] == '*' && (i + 1 >= line.Length || line[i + 1] != '*'))
                {
                    int end = line.IndexOf('*', i + 1);
                    if (end > i + 1)
                    {
                        FlushPlain(plainBuf, segments);
                        segments.Add(new TextSegment
                        {
                            Text = line.Substring(i + 1, end - i - 1),
                            Style = SegmentStyle.Italic
                        });
                        i = end + 1;
                        continue;
                    }
                }

                // [[CrossRef]]
                if (i + 1 < line.Length && line[i] == '[' && line[i + 1] == '[')
                {
                    int end = line.IndexOf("]]", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        FlushPlain(plainBuf, segments);
                        segments.Add(new TextSegment
                        {
                            Text = line.Substring(i + 2, end - i - 2),
                            Style = SegmentStyle.CrossRef
                        });
                        i = end + 2;
                        continue;
                    }
                }

                // {#RRGGBB}text{/}
                if (line[i] == '{' && i + 8 < line.Length && line[i + 1] == '#')
                {
                    int closeBrace = line.IndexOf('}', i + 2);
                    if (closeBrace > i + 2 && closeBrace - i - 2 == 6)
                    {
                        string hexColor = line.Substring(i + 2, 6);
                        int endTag = line.IndexOf("{/}", closeBrace + 1, StringComparison.Ordinal);
                        if (endTag > closeBrace && TryParseHexColor(hexColor, out Color color))
                        {
                            FlushPlain(plainBuf, segments);
                            segments.Add(new TextSegment
                            {
                                Text = line.Substring(closeBrace + 1, endTag - closeBrace - 1),
                                Style = SegmentStyle.Colored,
                                CustomColor = color
                            });
                            i = endTag + 3;
                            continue;
                        }
                    }
                }

                // ~~strikethrough~~
                if (line[i] == '~' && i + 1 < line.Length && line[i + 1] == '~')
                {
                    int end = line.IndexOf("~~", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        FlushPlain(plainBuf, segments);
                        segments.Add(new TextSegment
                        {
                            Text = line.Substring(i + 2, end - i - 2),
                            Style = SegmentStyle.Strikethrough
                        });
                        i = end + 2;
                        continue;
                    }
                }

                // __underline__
                if (line[i] == '_' && i + 1 < line.Length && line[i + 1] == '_')
                {
                    int end = line.IndexOf("__", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        FlushPlain(plainBuf, segments);
                        segments.Add(new TextSegment
                        {
                            Text = line.Substring(i + 2, end - i - 2),
                            Style = SegmentStyle.Underline
                        });
                        i = end + 2;
                        continue;
                    }
                }

                // {small}text{/} and {namedColor}text{/}
                if (line[i] == '{' && i + 1 < line.Length && line[i + 1] != '#' && line[i + 1] != '/')
                {
                    int closeBrace = line.IndexOf('}', i + 1);
                    if (closeBrace > i + 1)
                    {
                        string tag = line.Substring(i + 1, closeBrace - i - 1).Trim();
                        int endTag = line.IndexOf("{/}", closeBrace + 1, StringComparison.Ordinal);
                        if (endTag > closeBrace)
                        {
                            string content = line.Substring(closeBrace + 1, endTag - closeBrace - 1);

                            if (tag.Equals("small", StringComparison.OrdinalIgnoreCase))
                            {
                                FlushPlain(plainBuf, segments);
                                segments.Add(new TextSegment { Text = content, Style = SegmentStyle.Small });
                                i = endTag + 3;
                                continue;
                            }

                            Color namedColor;
                            if (NamedColors.TryGetValue(tag, out namedColor))
                            {
                                FlushPlain(plainBuf, segments);
                                segments.Add(new TextSegment
                                {
                                    Text = content,
                                    Style = SegmentStyle.Colored,
                                    CustomColor = namedColor
                                });
                                i = endTag + 3;
                                continue;
                            }
                        }
                    }
                }

                plainBuf.Append(line[i]);
                i++;
            }

            FlushPlain(plainBuf, segments);
            return segments;
        }

        /// <summary>
        /// Returns true if the line contains any markdown formatting markers.
        /// Used to skip parsing for plain text lines (performance optimization).
        /// </summary>
        public static bool HasFormatting(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return text.Contains("**") || text.Contains("[[")
                || text.Contains("{#") || text.Contains("---")
                || text.Contains("***") || text.Contains("___")
                || text.Contains("~~") || text.Contains("__")
                || text.Contains("{small}") || text.Contains("{red}")
                || text.Contains("{gold}") || text.Contains("{blue}")
                || text.Contains("{green}") || text.Contains("{purple}")
                || text.Contains("{blood}") || text.Contains("{amber}")
                || text.Contains("{silver}") || text.Contains("{orange}")
                || text.Contains("{teal}") || text.Contains("{brown}")
                || text.Contains("{white}")
                || text.StartsWith("# ") || text.StartsWith("> ")
                || ContainsSingleAsteriskItalic(text);
        }

        private static bool ContainsSingleAsteriskItalic(string text)
        {
            // Check for *text* pattern (single asterisk, not double)
            int first = text.IndexOf('*');
            if (first < 0) return false;
            if (first + 1 < text.Length && text[first + 1] == '*') return false; // ** is bold
            int second = text.IndexOf('*', first + 1);
            return second > first + 1;
        }

        // ── Markdown → HTML (for description RichTextWidget) ──

        /// <summary>
        /// Converts markdown text to basic HTML for RichTextWidget display.
        /// Handles: **bold**, *italic*, [[CrossRef]], {#RRGGBB}colored{/}, ---
        /// Newlines are converted to &lt;br/&gt; tags.
        /// </summary>
        public static string ToHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder();
            string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.None);

            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) sb.Append("<br/>");

                string line = lines[li];
                string trimmed = line.Trim();

                // Divider line
                if (trimmed == "---" || trimmed == "***" || trimmed == "___")
                {
                    sb.Append("<br/>────────────────────<br/>");
                    continue;
                }

                // Process inline formatting
                int i = 0;
                while (i < line.Length)
                {
                    // Escape existing HTML
                    if (line[i] == '<')
                    {
                        sb.Append("&lt;");
                        i++;
                        continue;
                    }
                    if (line[i] == '>')
                    {
                        sb.Append("&gt;");
                        i++;
                        continue;
                    }

                    // **bold**
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                    {
                        int end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                        if (end > i + 2)
                        {
                            sb.Append("<b>");
                            sb.Append(EscapeHtml(line.Substring(i + 2, end - i - 2)));
                            sb.Append("</b>");
                            i = end + 2;
                            continue;
                        }
                    }

                    // *italic*
                    if (line[i] == '*' && (i + 1 >= line.Length || line[i + 1] != '*'))
                    {
                        int end = line.IndexOf('*', i + 1);
                        if (end > i + 1)
                        {
                            sb.Append("<i>");
                            sb.Append(EscapeHtml(line.Substring(i + 1, end - i - 1)));
                            sb.Append("</i>");
                            i = end + 1;
                            continue;
                        }
                    }

                    // [[CrossRef]] — golden color
                    if (i + 1 < line.Length && line[i] == '[' && line[i + 1] == '[')
                    {
                        int end = line.IndexOf("]]", i + 2, StringComparison.Ordinal);
                        if (end > i + 2)
                        {
                            string name = line.Substring(i + 2, end - i - 2);
                            sb.Append("<a style=\"Link.Hero\">");
                            sb.Append(EscapeHtml(name));
                            sb.Append("</a>");
                            i = end + 2;
                            continue;
                        }
                    }

                    // {#RRGGBB}text{/}
                    if (line[i] == '{' && i + 8 < line.Length && line[i + 1] == '#')
                    {
                        int closeBrace = line.IndexOf('}', i + 2);
                        if (closeBrace > i + 2 && closeBrace - i - 2 == 6)
                        {
                            string hexColor = line.Substring(i + 2, 6);
                            int endTag = line.IndexOf("{/}", closeBrace + 1, StringComparison.Ordinal);
                            if (endTag > closeBrace && IsValidHex(hexColor))
                            {
                                sb.Append("<a style=\"Link.Hero\">");
                                sb.Append(EscapeHtml(line.Substring(closeBrace + 1, endTag - closeBrace - 1)));
                                sb.Append("</a>");
                                i = endTag + 3;
                                continue;
                            }
                        }
                    }

                    sb.Append(line[i]);
                    i++;
                }
            }

            return sb.ToString();
        }

        // ── Markdown → Plain text (strip markers, keep content) ──

        /// <summary>
        /// Strips markdown markers from text, keeping the content readable.
        /// **bold** → bold, *italic* → italic, [[Name]] → Name,
        /// {#RRGGBB}text{/} → text, --- → ——
        /// </summary>
        public static string StripMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder(text.Length);
            string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.None);

            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) sb.Append('\n');
                string line = lines[li];
                string trimmed = line.Trim();

                if (trimmed == "---" || trimmed == "***" || trimmed == "___")
                {
                    sb.Append("——————————");
                    continue;
                }
                // # Heading → Heading
                if (trimmed.StartsWith("# ")) { sb.Append(trimmed.Substring(2)); continue; }
                // > Quote → Quote
                if (trimmed.StartsWith("> ")) { sb.Append("  " + trimmed.Substring(2)); continue; }

                int i = 0;
                while (i < line.Length)
                {
                    // --- inline divider
                    if (line[i] == '-' && i + 2 < line.Length && line[i + 1] == '-' && line[i + 2] == '-'
                        && (i + 3 >= line.Length || line[i + 3] != '-'))
                    {
                        sb.Append('\n');
                        sb.Append("——————————");
                        sb.Append('\n');
                        i += 3;
                        continue;
                    }
                    // **bold** → bold
                    if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
                    {
                        int end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                        if (end > i + 2) { sb.Append(line, i + 2, end - i - 2); i = end + 2; continue; }
                    }
                    // *italic* → italic
                    if (line[i] == '*' && (i + 1 >= line.Length || line[i + 1] != '*'))
                    {
                        int end = line.IndexOf('*', i + 1);
                        if (end > i + 1) { sb.Append(line, i + 1, end - i - 1); i = end + 1; continue; }
                    }
                    // [[Name]] → Name
                    if (i + 1 < line.Length && line[i] == '[' && line[i + 1] == '[')
                    {
                        int end = line.IndexOf("]]", i + 2, StringComparison.Ordinal);
                        if (end > i + 2) { sb.Append(line, i + 2, end - i - 2); i = end + 2; continue; }
                    }
                    // {#RRGGBB}text{/} → text
                    if (line[i] == '{' && i + 8 < line.Length && line[i + 1] == '#')
                    {
                        int closeBrace = line.IndexOf('}', i + 2);
                        if (closeBrace > i + 2 && closeBrace - i - 2 == 6)
                        {
                            string hexColor = line.Substring(i + 2, 6);
                            int endTag = line.IndexOf("{/}", closeBrace + 1, StringComparison.Ordinal);
                            if (endTag > closeBrace && IsValidHex(hexColor))
                            {
                                sb.Append(line, closeBrace + 1, endTag - closeBrace - 1);
                                i = endTag + 3;
                                continue;
                            }
                        }
                    }
                    // ~~strikethrough~~ → strikethrough
                    if (line[i] == '~' && i + 1 < line.Length && line[i + 1] == '~')
                    {
                        int end = line.IndexOf("~~", i + 2, StringComparison.Ordinal);
                        if (end > i + 2) { sb.Append(line, i + 2, end - i - 2); i = end + 2; continue; }
                    }
                    // __underline__ → underline
                    if (line[i] == '_' && i + 1 < line.Length && line[i + 1] == '_')
                    {
                        int end = line.IndexOf("__", i + 2, StringComparison.Ordinal);
                        if (end > i + 2) { sb.Append(line, i + 2, end - i - 2); i = end + 2; continue; }
                    }
                    // {small}text{/} and {namedColor}text{/} → text
                    if (line[i] == '{' && i + 1 < line.Length && line[i + 1] != '#' && line[i + 1] != '/')
                    {
                        int closeBrace = line.IndexOf('}', i + 1);
                        if (closeBrace > i + 1)
                        {
                            string tag = line.Substring(i + 1, closeBrace - i - 1).Trim();
                            int endTag = line.IndexOf("{/}", closeBrace + 1, StringComparison.Ordinal);
                            if (endTag > closeBrace && (tag.Equals("small", StringComparison.OrdinalIgnoreCase) || NamedColors.ContainsKey(tag)))
                            {
                                sb.Append(line, closeBrace + 1, endTag - closeBrace - 1);
                                i = endTag + 3;
                                continue;
                            }
                        }
                    }
                    sb.Append(line[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        // ── Helpers ──

        private static void FlushPlain(StringBuilder buf, List<TextSegment> segments)
        {
            if (buf.Length > 0)
            {
                segments.Add(new TextSegment { Text = buf.ToString(), Style = SegmentStyle.Normal });
                buf.Clear();
            }
        }

        private static bool TryParseHexColor(string hex, out Color color)
        {
            color = default;
            if (hex.Length != 6 || !IsValidHex(hex)) return false;
            try
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                color = new Color(r / 255f, g / 255f, b / 255f, 1f);
                return true;
            }
            catch { return false; }
        }

        private static bool IsValidHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        private static string EscapeHtml(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
