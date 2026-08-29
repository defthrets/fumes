using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fumes.Core
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// A tiny self-contained JSON DOM: parser, writer and typed accessors.
    ///
    /// Fumes deliberately does not reference Newtonsoft. A GTA scripts\ folder is a
    /// single shared assembly-resolution namespace, and pulling in a versioned third-party
    /// json library is a well-known way to break (or be broken by) an unrelated mod.
    ///
    /// LIFTED FROM HOODRICH, file for file, with only the namespace changed -- same author,
    /// same box, and a json parser is not worth writing twice. If a bug is found in one,
    /// fix it in Hoodrich and copy it back over; do not let the two drift.
    ///
    /// Accessors never throw: indexing a missing key or a wrong-typed node returns a Null
    /// node, and the As* helpers return the supplied fallback. Config data is untrusted
    /// text on the player's disk, so total tolerance beats strictness here.
    /// </summary>
    internal sealed class Json
    {
        public JsonKind Kind { get; private set; }

        private bool _bool;
        private double _number;
        private string _string;
        private List<Json> _array;
        private Dictionary<string, Json> _object;

        private static readonly Json NullNode = new Json { Kind = JsonKind.Null };

        // ---- construction ------------------------------------------------------

        private Json() { }

        public static Json Null() => NullNode;
        public static Json Bool(bool v) => new Json { Kind = JsonKind.Bool, _bool = v };
        public static Json Number(double v) => new Json { Kind = JsonKind.Number, _number = v };
        public static Json Str(string v) =>
            v == null ? NullNode : new Json { Kind = JsonKind.String, _string = v };

        public static Json Array()
        {
            return new Json { Kind = JsonKind.Array, _array = new List<Json>() };
        }

        public static Json Object()
        {
            return new Json { Kind = JsonKind.Object, _object = new Dictionary<string, Json>(StringComparer.Ordinal) };
        }

        // ---- mutation ----------------------------------------------------------

        public Json Add(Json value)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Add() on a " + Kind + " node.");
            _array.Add(value ?? NullNode);
            return this;
        }

        public Json Set(string key, Json value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Set() on a " + Kind + " node.");
            _object[key] = value ?? NullNode;
            return this;
        }

        public Json Set(string key, string value) => Set(key, Str(value));
        public Json Set(string key, double value) => Set(key, Number(value));
        public Json Set(string key, int value) => Set(key, Number(value));
        public Json Set(string key, bool value) => Set(key, Bool(value));

        // ---- access ------------------------------------------------------------

        public Json this[string key]
        {
            get
            {
                if (Kind == JsonKind.Object && _object.TryGetValue(key, out var v)) return v;
                return NullNode;
            }
        }

        public Json this[int index]
        {
            get
            {
                if (Kind == JsonKind.Array && index >= 0 && index < _array.Count) return _array[index];
                return NullNode;
            }
        }

        public int Count => Kind == JsonKind.Array ? _array.Count : Kind == JsonKind.Object ? _object.Count : 0;

        public bool Has(string key) => Kind == JsonKind.Object && _object.ContainsKey(key);

        public IEnumerable<string> Keys =>
            Kind == JsonKind.Object ? (IEnumerable<string>)_object.Keys : new string[0];

        public IEnumerable<Json> Items =>
            Kind == JsonKind.Array ? (IEnumerable<Json>)_array : new Json[0];

        public bool IsNull => Kind == JsonKind.Null;

        public string AsString(string fallback = "")
        {
            switch (Kind)
            {
                case JsonKind.String: return _string;
                case JsonKind.Number: return _number.ToString(CultureInfo.InvariantCulture);
                case JsonKind.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsDouble(double fallback = 0)
        {
            if (Kind == JsonKind.Number) return _number;
            if (Kind == JsonKind.String &&
                double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return fallback;
        }

        public float AsFloat(float fallback = 0f) => (float)AsDouble(fallback);

        public int AsInt(int fallback = 0)
        {
            var d = AsDouble(fallback);
            if (double.IsNaN(d) || double.IsInfinity(d)) return fallback;
            if (d > int.MaxValue) return int.MaxValue;
            if (d < int.MinValue) return int.MinValue;
            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public long AsLong(long fallback = 0)
        {
            var d = AsDouble(fallback);
            if (double.IsNaN(d) || double.IsInfinity(d)) return fallback;
            if (d > long.MaxValue) return long.MaxValue;
            if (d < long.MinValue) return long.MinValue;
            return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JsonKind.Bool) return _bool;
            if (Kind == JsonKind.Number) return Math.Abs(_number) > double.Epsilon;
            if (Kind == JsonKind.String)
            {
                if (_string.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (_string.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return fallback;
        }

        public List<string> AsStringList()
        {
            var list = new List<string>();
            foreach (var item in Items)
            {
                var s = item.AsString(null);
                if (s != null) list.Add(s);
            }
            return list;
        }

        // ---- parsing -----------------------------------------------------------

        public static bool TryParse(string text, out Json result)
        {
            result = NullNode;
            if (string.IsNullOrEmpty(text)) return false;

            try
            {
                var p = new Parser(text);
                var value = p.ParseValue();
                p.SkipWhitespace();
                if (!p.AtEnd) throw new FormatException("Trailing content at offset " + p.Position + ".");
                result = value;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("JSON parse failed: " + ex.Message);
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) { _s = s; _i = 0; }

            public int Position => _i;
            public bool AtEnd => _i >= _s.Length;

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private char Peek()
            {
                if (_i >= _s.Length) throw new FormatException("Unexpected end of input.");
                return _s[_i];
            }

            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException("Expected '" + c + "' at offset " + _i + ".");
                _i++;
            }

            public Json ParseValue()
            {
                SkipWhitespace();
                var c = Peek();
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return Str(ParseString());
                    case 't': ExpectLiteral("true"); return Bool(true);
                    case 'f': ExpectLiteral("false"); return Bool(false);
                    case 'n': ExpectLiteral("null"); return NullNode;
                    default: return ParseNumber();
                }
            }

            private void ExpectLiteral(string literal)
            {
                if (_i + literal.Length > _s.Length ||
                    string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("Expected '" + literal + "' at offset " + _i + ".");
                }
                _i += literal.Length;
            }

            private Json ParseObject()
            {
                Expect('{');
                var obj = Object();
                SkipWhitespace();
                if (Peek() == '}') { _i++; return obj; }

                while (true)
                {
                    SkipWhitespace();
                    var key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    obj.Set(key, ParseValue());
                    SkipWhitespace();

                    var c = Peek();
                    if (c == ',') { _i++; continue; }
                    if (c == '}') { _i++; return obj; }
                    throw new FormatException("Expected ',' or '}' at offset " + _i + ".");
                }
            }

            private Json ParseArray()
            {
                Expect('[');
                var arr = Array();
                SkipWhitespace();
                if (Peek() == ']') { _i++; return arr; }

                while (true)
                {
                    arr.Add(ParseValue());
                    SkipWhitespace();

                    var c = Peek();
                    if (c == ',') { _i++; continue; }
                    if (c == ']') { _i++; return arr; }
                    throw new FormatException("Expected ',' or ']' at offset " + _i + ".");
                }
            }

            private string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("Unterminated string.");
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();

                    if (c != '\\') { sb.Append(c); continue; }

                    if (_i >= _s.Length) throw new FormatException("Unterminated escape.");
                    var e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw new FormatException("Truncated \\u escape.");
                            sb.Append((char)ushort.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber,
                                                        CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default:
                            throw new FormatException("Unknown escape '\\" + e + "' at offset " + (_i - 1) + ".");
                    }
                }
            }

            private Json ParseNumber()
            {
                var start = _i;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                while (_i < _s.Length)
                {
                    var c = _s[_i];
                    if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }

                var text = _s.Substring(start, _i - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    throw new FormatException("Bad number '" + text + "' at offset " + start + ".");
                }
                return Number(d);
            }
        }

        // ---- writing -----------------------------------------------------------

        public string ToJsonString(bool indented = true)
        {
            var sb = new StringBuilder();
            WriteTo(sb, indented, 0);
            return sb.ToString();
        }

        private void WriteTo(StringBuilder sb, bool indented, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;

                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;

                case JsonKind.Number:
                    sb.Append(FormatNumber(_number));
                    break;

                case JsonKind.String:
                    WriteEscaped(sb, _string);
                    break;

                case JsonKind.Array:
                    if (_array.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (var i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        _array[i].WriteTo(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append(']');
                    break;

                case JsonKind.Object:
                    if (_object.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in _object)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, indented, depth + 1);
                        WriteEscaped(sb, kv.Key);
                        sb.Append(':');
                        if (indented) sb.Append(' ');
                        kv.Value.WriteTo(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static string FormatNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15)
            {
                return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            }
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteEscaped(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
