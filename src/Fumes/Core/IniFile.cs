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

        // The writer below is LIFTED FROM HOODRICH, same as Json.cs -- same author, same box,
        // and a surgical ini editor is not worth writing twice. If a bug is found in one, fix
        // it there and copy it back; do not let the two drift.

        /// <summary>
        /// Changes one value in the file on disk, and changes NOTHING else.
        ///
        /// A surgical line edit rather than a re-serialise. This ini is eighty lines of
        /// hand-written comments explaining what every key does, grouped and spaced on purpose
        /// -- rewriting it from the parsed dictionary would hand the player back a bare list
        /// of key=value and throw all of that away the first time they changed a setting.
        ///
        /// So: find the section, find the key inside it, replace the text after the equals
        /// sign, put the file back exactly as it was otherwise. A key that is not there is
        /// appended at the end of its section; a section that is not there is appended at the
        /// end of the file. Both keep every comment above them.
        ///
        /// Returns false rather than throwing. A settings screen that cannot write is a
        /// setting that does not stick, which is worth reporting; it is not worth taking the
        /// mod down over.
        /// </summary>
        public static bool SetValue(string path, string section, string key, string value)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(key)) return false;

            try
            {
                if (!File.Exists(path)) return false;

                var lines = new List<string>(File.ReadAllLines(path));

                var inSection = string.IsNullOrEmpty(section);
                var sectionEnd = -1;

                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        var name = trimmed.Substring(1, trimmed.Length - 2).Trim();

                        // Leaving the section we wanted without having found the key: this is
                        // where it gets appended, before whatever comes next.
                        if (inSection && !string.IsNullOrEmpty(section))
                        {
                            sectionEnd = i;
                            break;
                        }

                        inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inSection) continue;
                    if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#') continue;

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    if (!string.Equals(line.Substring(0, eq).Trim(), key,
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Found it. Keep whatever indentation the line had.
                    var lead = line.Substring(0, line.Length - line.TrimStart().Length);
                    lines[i] = lead + key + "=" + value;

                    File.WriteAllLines(path, lines.ToArray());
                    return true;
                }

                // Not found. Put it where it belongs rather than at the bottom of the file.
                if (sectionEnd >= 0)
                {
                    while (sectionEnd > 0 && lines[sectionEnd - 1].Trim().Length == 0) sectionEnd--;
                    lines.Insert(sectionEnd, key + "=" + value);
                }
                else if (inSection)
                {
                    lines.Add(key + "=" + value);
                }
                else
                {
                    lines.Add("");
                    lines.Add("[" + section + "]");
                    lines.Add(key + "=" + value);
                }

                File.WriteAllLines(path, lines.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not write " + key + " to the ini: " + ex.Message);
                return false;
            }
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
