using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Fumes.Core
{
    /// <summary>
    /// Small INI reader. Tolerates ';', '#' and '//' comments, trailing inline comments, and
    /// keys outside any section. Reading a missing file yields an empty instance rather than
    /// throwing, so a deleted ini falls back to the code defaults instead of killing the mod.
    /// </summary>
    internal sealed class IniFile
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static IniFile Load(string path)
        {
            var ini = new IniFile();
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn("No ini at " + path + " - using built-in defaults.");
                    return ini;
                }

                var current = ini.SectionFor("");
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == ';' || line[0] == '#') continue;
                    if (line.StartsWith("//", StringComparison.Ordinal)) continue;

                    if (line[0] == '[')
                    {
                        var close = line.IndexOf(']');
                        if (close > 1)
                        {
                            current = ini.SectionFor(line.Substring(1, close - 1).Trim());
                            continue;
                        }
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = line.Substring(0, eq).Trim();
                    var value = StripInlineComment(line.Substring(eq + 1)).Trim();
                    current[key] = value;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed reading ini " + path, ex);
            }

            return ini;
        }

        /// <summary>
        /// Strips a trailing '//' or ';' comment, but only when whitespace precedes it, so a
        /// value that legitimately contains those characters survives.
        /// </summary>
        private static string StripInlineComment(string value)
        {
            for (var i = 1; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i - 1])) continue;
                if (value[i] == ';') return value.Substring(0, i);
                if (value[i] == '/' && i + 1 < value.Length && value[i + 1] == '/') return value.Substring(0, i);
            }
            return value;
        }

        private Dictionary<string, string> SectionFor(string name)
        {
            if (!_sections.TryGetValue(name, out var s))
            {
                s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _sections[name] = s;
            }
            return s;
        }

        private bool TryGet(string section, string key, out string value)
        {
            value = null;
            return _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out value);
        }

        public string GetString(string section, string key, string fallback)
        {
            return TryGet(section, key, out var v) && v.Length > 0 ? v : fallback;
        }

        public bool GetBool(string section, string key, bool fallback)
        {
            if (!TryGet(section, key, out var v)) return fallback;

            switch (v.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": return true;
                case "0": case "false": case "no": case "off": return false;
                default:
                    Log.Warn("[" + section + "] " + key + " = '" + v + "' is not a yes/no - using " + fallback + ".");
                    return fallback;
            }
        }

        public int GetInt(string section, string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            if (!TryGet(section, key, out var v)) return fallback;

            if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                Log.Warn("[" + section + "] " + key + " = '" + v + "' is not a whole number - using " + fallback + ".");
                return fallback;
            }

            return Clamp(n, min, max, section, key);
        }

        public float GetFloat(string section, string key, float fallback, float min = float.MinValue, float max = float.MaxValue)
        {
            if (!TryGet(section, key, out var v)) return fallback;

            if (!float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                Log.Warn("[" + section + "] " + key + " = '" + v + "' is not a number - using " + fallback + ".");
                return fallback;
            }

            if (n < min || n > max)
            {
                Log.Warn("[" + section + "] " + key + " = " + n + " is outside " + min + ".." + max + " - clamped.");
                return n < min ? min : max;
            }

            return n;
        }

        private static int Clamp(int n, int min, int max, string section, string key)
        {
            if (n >= min && n <= max) return n;

            Log.Warn("[" + section + "] " + key + " = " + n + " is outside " + min + ".." + max + " - clamped.");
            return n < min ? min : max;
        }

        /// <summary>
        /// A key.
        ///
        /// TAKES BOTH SPELLINGS ON PURPOSE. Fuel mods on this machine have historically stored
        /// keys as hex (MarkPumpKey=0x51), which is invisible to anybody reading their own ini
        /// and invisible to a hotkey audit as well. A name is what a person types; the hex is
        /// accepted so that a config copied from one of those mods still works.
        /// </summary>
        public Keys GetKey(string section, string key, Keys fallback)
        {
            if (!TryGet(section, key, out var v)) return fallback;

            v = v.Trim();
            if (v.Length == 0) return fallback;

            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) &&
                Enum.IsDefined(typeof(Keys), hex))
            {
                return (Keys)hex;
            }

            // A bare letter is the common case and Enum.TryParse handles it, but only in the
            // right case -- "e" is not a Keys name, "E" is.
            if (v.Length == 1) v = v.ToUpperInvariant();

            if (Enum.TryParse(v, true, out Keys parsed)) return parsed;

            Log.Warn("[" + section + "] " + key + " = '" + v + "' is not a key name - using " + fallback + ".");
            return fallback;
        }
    }
}
