using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HyperBox.Export
{
    /// <summary>A JSON object that keeps its keys in insertion order (so film files diff nicely).</summary>
    public class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();

        public void Add(string key, object value) => this[key] = value;

        public object this[string key]
        {
            get
            {
                foreach (var kv in _items)
                    if (kv.Key == key) return kv.Value;
                return null;
            }
            set
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Key != key) continue;
                    _items[i] = new KeyValuePair<string, object>(key, value);
                    return;
                }
                _items.Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public bool Has(string key)
        {
            foreach (var kv in _items)
                if (kv.Key == key) return true;
            return false;
        }

        public int Count => _items.Count;
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    /// <summary>Minimal JSON writer for film documents: JObj, lists/arrays, strings, numbers, bools, null.</summary>
    public static class FilmJson
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: sb.Append(Num(d)); return;
                case float f: sb.Append(Num(f)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case JObj o: WriteObject(sb, o, indent); return;
                case IEnumerable e: WriteArray(sb, e, indent); return;
                default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        static void WriteObject(StringBuilder sb, JObj o, int indent)
        {
            if (o.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            var first = true;
            foreach (var kv in o)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append(' ', (indent + 1) * 2);
                WriteString(sb, kv.Key);
                sb.Append(": ");
                WriteValue(sb, kv.Value, indent + 1);
            }
            sb.Append('\n').Append(' ', indent * 2).Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable e, int indent)
        {
            var items = new List<object>();
            foreach (var x in e) items.Add(x);
            if (items.Count == 0) { sb.Append("[]"); return; }
            // Short arrays of plain values (vectors, colors) stay on one line.
            var inline = items.Count <= 4 && items.TrueForAll(x => !(x is JObj) && (x is string || !(x is IEnumerable)));
            if (inline)
            {
                sb.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    WriteValue(sb, items[i], indent);
                }
                sb.Append(']');
                return;
            }
            sb.Append("[\n");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",\n");
                sb.Append(' ', (indent + 1) * 2);
                WriteValue(sb, items[i], indent + 1);
            }
            sb.Append('\n').Append(' ', indent * 2).Append(']');
        }

        static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            var r = Math.Round(d, 4);
            if (r == 0) return "0";
            return r.ToString("0.####", CultureInfo.InvariantCulture);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
