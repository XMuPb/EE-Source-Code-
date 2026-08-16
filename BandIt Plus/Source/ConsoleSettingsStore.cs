using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BandItPlus
{
    // 07-13 Console settings persistence.
    //
    // MCM's AttributeGlobalSettings only serializes properties that carry a
    // [SettingProperty*] attribute. BP intentionally strips those attributes from
    // its console-tuned properties so they don't render on the MCM options page
    // (they're edited via the in-game Royal Codex Console) — but that ALSO removes
    // them from MCM's serialized JSON, so they reverted to C# defaults on every
    // launch ("settings reset every time", and disabled clans re-enabled after a
    // restart because the toggles default to true).
    //
    // Fix: give the console-only properties their own companion JSON next to MCM's
    // own file (Global/BandItPlus/BandItPlus_console.json). Save() runs from
    // MCMSettings.PersistNow() (console close); Load() runs once at startup from
    // OnBeforeInitialModuleScreenSetAsRoot after MCM has loaded its 5 attributed
    // values. Global scope — same for every save.
    //
    // Selection is by reflection ("public scalar get/set property with no
    // [SettingProperty*] attribute") so new console settings persist automatically
    // and the 5 MCM-owned properties are always excluded — MCM stays their single
    // source of truth, no double-write.
    public static class ConsoleSettingsStore
    {
        private const string FileName = "BandItPlus_console.json";

        private static string SettingsFilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord", "Configs", "ModSettings", "Global", "BandItPlus");
            return Path.Combine(dir, FileName);
        }

        private static List<PropertyInfo> ConsoleProps()
        {
            var result = new List<PropertyInfo>();
            var props = typeof(MCMSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var p in props)
            {
                if (!p.CanRead || !p.CanWrite) continue;
                var t = p.PropertyType;
                if (t != typeof(bool) && t != typeof(int) && t != typeof(float) && t != typeof(string)) continue;
                bool isMcm = false;
                foreach (var a in p.GetCustomAttributes(false))
                {
                    if (a.GetType().Name.StartsWith("SettingProperty", StringComparison.Ordinal)) { isMcm = true; break; }
                }
                if (isMcm) continue;
                result.Add(p);
            }
            // Stable, diff-friendly ordering.
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }

        public static void Save()
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s == null) return;
                var props = ConsoleProps();
                var sb = new StringBuilder();
                sb.Append("{\n");
                for (int i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    object v = null;
                    try { v = p.GetValue(s); } catch { }
                    sb.Append("  \"").Append(p.Name).Append("\": ").Append(FormatValue(p.PropertyType, v));
                    sb.Append(i < props.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("}\n");

                string path = SettingsFilePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log("saved " + props.Count + " console setting(s) to " + FileName);
            }
            catch (Exception ex)
            {
                Log("Save " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Load()
        {
            try
            {
                var s = MCMSettings.Instance;
                if (s == null) return;
                string path = SettingsFilePath();
                if (!File.Exists(path)) { Log("no console settings file yet (" + FileName + ") — using defaults"); return; }
                string text = File.ReadAllText(path, Encoding.UTF8);
                int applied = 0;
                foreach (var p in ConsoleProps())
                {
                    if (TryReadRaw(text, p.Name, p.PropertyType, out object val))
                    {
                        try { p.SetValue(s, val); applied++; }
                        catch (Exception setEx) { Log("apply '" + p.Name + "' failed: " + setEx.Message); }
                    }
                }
                Log("loaded " + applied + " console setting(s) from " + FileName);
            }
            catch (Exception ex)
            {
                Log("Load " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string FormatValue(Type t, object v)
        {
            if (t == typeof(bool)) return (v != null && (bool)v) ? "true" : "false";
            if (t == typeof(int)) return (v == null ? 0 : (int)v).ToString(CultureInfo.InvariantCulture);
            if (t == typeof(float)) return (v == null ? 0f : (float)v).ToString("R", CultureInfo.InvariantCulture);
            // string
            if (v == null) return "null";
            return "\"" + EscapeJson((string)v) + "\"";
        }

        private static bool TryReadRaw(string text, string name, Type t, out object val)
        {
            val = null;
            var m = Regex.Match(text,
                "\"" + Regex.Escape(name) + "\"\\s*:\\s*(\"(?:\\\\.|[^\"\\\\])*\"|true|false|null|-?[0-9][0-9.eE+\\-]*)");
            if (!m.Success) return false;
            string raw = m.Groups[1].Value;
            try
            {
                if (t == typeof(bool))
                {
                    if (raw == "true") { val = true; return true; }
                    if (raw == "false") { val = false; return true; }
                    return false;
                }
                if (t == typeof(int))
                {
                    if (raw == "null") return false;
                    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) { val = iv; return true; }
                    if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv2)) { val = (int)fv2; return true; }
                    return false;
                }
                if (t == typeof(float))
                {
                    if (raw == "null") return false;
                    if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv)) { val = fv; return true; }
                    return false;
                }
                // string
                if (raw == "null") { val = null; return true; }
                if (raw.Length >= 2 && raw[0] == '"') { val = UnescapeJson(raw.Substring(1, raw.Length - 2)); return true; }
                return false;
            }
            catch { return false; }
        }

        private static string EscapeJson(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string UnescapeJson(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                string hex = s.Substring(i + 1, 4);
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                { sb.Append((char)code); i += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Log(string msg)
        {
            try { BandItPlus.HideoutVisit.HideoutPeacefulVisitState.Log("[BP-ConsoleCfg] " + msg); } catch { }
        }
    }
}
