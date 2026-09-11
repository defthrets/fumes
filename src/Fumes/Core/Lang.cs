using System;
using System.Collections.Generic;
using System.IO;

namespace Fumes.Core
{
    /// <summary>The languages the menu can offer. English is the code's own; the rest are files.</summary>
    internal enum Language
    {
        English,
        PortugueseBR
    }

    /// <summary>
    /// What the player reads, in the language they asked for.
    ///
    /// THE ENGLISH IS THE KEY. Every string in the code stays exactly as written and is
    /// looked up, at the moment it is drawn, in a table loaded from
    /// scripts\Fumes\lang\&lt;code&gt;.json. A string the table does not have comes back as
    /// itself, so a translation that covers half the mod shows English for the other half
    /// rather than blanks or keys -- and a new setting added in English is simply English
    /// until somebody translates it. Nothing is ever missing; some of it is just not yet
    /// translated.
    ///
    /// That is also why the table is a data file and not code: a translator edits a json
    /// with the English on the left and their language on the right, and never needs the
    /// source, the compiler or a new dll. The first one came exactly that way -- a PT-BR
    /// build of 0.1.1 somebody made and sent back -- and this is what lets it be folded in
    /// without forking.
    ///
    /// LOOKED UP AT DRAW TIME, not when the menu is built, so switching the language in the
    /// menu changes the menu you are looking at, including the row you switched it on.
    ///
    /// A LOT OF THE MOD'S TEXT IS BUILT BY CONCATENATION -- "Could not read " + name + ": "
    /// -- so an exact match on the whole string would miss most of the log and half the
    /// notices. When the whole string is not in the table, the longest table key the string
    /// STARTS with is swapped out and the rest is kept. The fragments are keys in their own
    /// right, with their trailing spaces and colons intact, which is why the file looks the
    /// way it does.
    /// </summary>
    internal static class Lang
    {
        private static Language _language = Language.English;
        private static Dictionary<string, string> _table;

        /// <summary>Every key of three characters or more, longest first, for the scan in Resolve.</summary>
        private static List<string> _prefixes;

        /// <summary>Whole strings already resolved once, so the help text drawn every frame costs a lookup, not a scan.</summary>
        private static readonly Dictionary<string, string> _memo = new Dictionary<string, string>();

        private const int MemoCap = 600;

        public static Language Current => _language;

        /// <summary>The file a language reads from, or null for English, which needs none.</summary>
        public static string FileFor(Language language)
        {
            switch (language)
            {
                case Language.PortugueseBR: return "pt-BR.json";
                default: return null;
            }
        }

        /// <summary>How the language names itself on the menu row.</summary>
        public static string NameOf(Language language)
        {
            switch (language)
            {
                case Language.PortugueseBR: return "PORTUGUÊS (BR)";
                default: return "ENGLISH";
            }
        }

        /// <summary>
        /// Switches language. English drops the table; anything else loads its file, and
        /// a file that is missing or malformed leaves the mod in English and says so once.
        /// </summary>
        public static void Use(Language language)
        {
            _language = language;
            _table = null;
            _prefixes = null;
            _memo.Clear();

            var file = FileFor(language);
            if (file == null) return;

            try
            {
                var path = Path.Combine(Paths.Lang, file);
                var doc = JsonFile.Read(path);

                if (doc == null)
                {
                    Log.Warn("No usable " + file + " in " + Paths.Lang + " - staying in English.");
                    _language = Language.English;
                    return;
                }

                var strings = doc["strings"];
                var table = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var key in strings.Keys)
                {
                    var value = strings[key].AsString(null);
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value) || key == value) continue;
                    table[key] = value;
                }

                // Every key, longest first, for the scan in Resolve. Nothing under three
                // characters: a two-letter key is a coincidence waiting to happen.
                var prefixes = new List<string>();
                foreach (var key in table.Keys)
                {
                    if (key.Length >= 3) prefixes.Add(key);
                }
                prefixes.Sort((a, b) => b.Length.CompareTo(a.Length));

                _table = table;
                _prefixes = prefixes;

                Log.Info(doc["language"].AsString(language.ToString()) + " loaded from " + file + ": " +
                         table.Count + " string(s)" +
                         (doc.Has("by") ? ", by " + doc["by"].AsString("") : "") + ".");
            }
            catch (Exception ex)
            {
                Log.Error("Could not load " + file + " - staying in English.", ex);
                _table = null;
                _prefixes = null;
                _language = Language.English;
            }
        }

        /// <summary>The string in the current language, or itself when there is no translation.</summary>
        public static string T(string english)
        {
            if (_table == null || string.IsNullOrEmpty(english)) return english;

            string hit;
            if (_memo.TryGetValue(english, out hit)) return hit;

            hit = Resolve(english);

            if (_memo.Count >= MemoCap) _memo.Clear();
            _memo[english] = hit;
            return hit;
        }

        private static string Resolve(string english)
        {
            string whole;
            if (_table.TryGetValue(english, out whole)) return whole;

            // NOT A WHOLE STRING, SO IT WAS GLUED TOGETHER: "Struck off " + title + ": stood
            // on its coordinate for " + n + "s and there is no pump within " + m + "m. It is
            // not a real station." has four fixed parts round three that vary. Walk the
            // string; at each position take the LONGEST key that starts there, on a word
            // boundary, and copy anything nothing matches -- the names, the numbers, the
            // units -- through untouched. What comes out is every fixed part translated
            // and every variable part where it was.
            var sb = new System.Text.StringBuilder(english.Length + 32);
            var i = 0;
            var any = false;

            while (i < english.Length)
            {
                string hit = null;

                // A key may not begin in the middle of a word, so "Stop" never fires inside
                // "Stopped" and "ON" never fires inside "STATION".
                var atBoundary = i == 0 || !char.IsLetterOrDigit(english[i - 1]) || !char.IsLetterOrDigit(english[i]);

                if (atBoundary)
                {
                    foreach (var key in _prefixes)          // longest first
                    {
                        if (key.Length > english.Length - i) continue;
                        if (string.CompareOrdinal(english, i, key, 0, key.Length) != 0) continue;

                        // ...nor end in the middle of one.
                        var end = i + key.Length;
                        if (end < english.Length && char.IsLetterOrDigit(english[end]) &&
                            char.IsLetterOrDigit(key[key.Length - 1])) continue;

                        hit = key;
                        break;
                    }
                }

                if (hit != null)
                {
                    sb.Append(_table[hit]);
                    i += hit.Length;
                    any = true;
                }
                else
                {
                    sb.Append(english[i]);
                    i++;
                }
            }

            return any ? sb.ToString() : english;
        }
    }
}
