using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HyperBox.Export
{
    /// <summary>A JSON number kept exactly as written, so rewriting a glTF never changes its data.</summary>
    public sealed class JNum
    {
        public readonly string Raw;
        public JNum(string raw) { Raw = raw; }
        public double Value => double.Parse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        public override string ToString() => Raw;
    }

    /// <summary>
    /// Lossless JSON for patching glTF files: objects become JObj, arrays List&lt;object&gt;, numbers
    /// JNum (raw text), plus string, bool and null. Written compactly. Pure C# (no UnityEngine).
    /// </summary>
    public static class GltfJson
    {
        public static object Parse(string s)
        {
            var i = 0;
            var v = ParseValue(s, ref i);
            Skip(s, ref i);
            if (i != s.Length) throw new InvalidDataException("trailing data in JSON");
            return v;
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '\0')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new InvalidDataException("unexpected end of JSON");
            var c = s[i];
            if (c == '{')
            {
                var o = new JObj();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Skip(s, ref i);
                    var key = ParseString(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new InvalidDataException("expected ':' in JSON");
                    i++;
                    o[key] = ParseValue(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw new InvalidDataException("expected ',' or '}' in JSON");
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(ParseValue(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw new InvalidDataException("expected ',' or ']' in JSON");
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            var start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new InvalidDataException($"unexpected '{c}' in JSON");
            return new JNum(s.Substring(start, i - start));
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new InvalidDataException("expected a string in JSON");
            var sb = new StringBuilder();
            for (i++; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); continue; }
                i++;
                if (i >= s.Length) break;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(s[i]); break;   // \" \\ \/
                }
            }
            throw new InvalidDataException("unterminated string in JSON");
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case JNum n: sb.Append(n.Raw); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(Num(f)); return;
                case double d: sb.Append(Num(d)); return;
                case JObj o:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteValue(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                case IEnumerable e:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var x in e)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        WriteValue(sb, x);
                    }
                    sb.Append(']');
                    return;
                default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            if (d == 0) return "0";
            return d.ToString("R", CultureInfo.InvariantCulture);
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

        // ---- small helpers for reading parsed glTF

        public static double Number(object v, double fallback = 0) =>
            v is JNum n ? n.Value : v is double d ? d : v is float f ? f : v is int i ? i : v is long l ? l : fallback;

        public static List<object> Array(JObj o, string key, bool create = false)
        {
            if (o[key] is List<object> a) return a;
            if (!create) return null;
            a = new List<object>();
            o[key] = a;
            return a;
        }

        public static JObj Obj(JObj o, string key, bool create = false)
        {
            if (o[key] is JObj x) return x;
            if (!create) return null;
            x = new JObj();
            o[key] = x;
            return x;
        }
    }

    /// <summary>A GLB file split into its JSON (parsed) and binary chunk, written back together.</summary>
    public sealed class GlbFile
    {
        const uint Magic = 0x46546C67;      // "glTF"
        const uint ChunkJson = 0x4E4F534A;  // "JSON"
        const uint ChunkBin = 0x004E4942;   // "BIN\0"

        public JObj Json;
        public List<byte> Bin = new List<byte>();
        bool _hadBin;

        public static GlbFile Read(byte[] glb)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != Magic)
                throw new InvalidDataException("not a GLB file");
            var jsonLength = (int)BitConverter.ToUInt32(glb, 12);
            if (BitConverter.ToUInt32(glb, 16) != ChunkJson || 20 + jsonLength > glb.Length)
                throw new InvalidDataException("GLB has no JSON chunk");
            var f = new GlbFile();
            f.Json = GltfJson.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength)) as JObj
                     ?? throw new InvalidDataException("glTF JSON is not an object");
            var at = 20 + jsonLength;
            if (at + 8 <= glb.Length && BitConverter.ToUInt32(glb, at + 4) == ChunkBin)
            {
                var binLength = (int)BitConverter.ToUInt32(glb, at);
                var bin = new byte[binLength];
                Array.Copy(glb, at + 8, bin, 0, binLength);
                f.Bin.AddRange(bin);
                f._hadBin = true;
            }
            return f;
        }

        public byte[] Write()
        {
            if (_hadBin || Bin.Count > 0)
            {
                // The binary chunk's buffer (buffer 0, no uri) must state its real length.
                var buffers = GltfJson.Array(Json, "buffers", true);
                if (buffers.Count == 0) buffers.Add(new JObj());
                if (buffers[0] is JObj b0) b0["byteLength"] = Bin.Count;
            }
            var json = new List<byte>(Encoding.UTF8.GetBytes(GltfJson.Write(Json)));
            while (json.Count % 4 != 0) json.Add(0x20);
            var bin = new List<byte>(Bin);
            while (bin.Count % 4 != 0) bin.Add(0);
            var withBin = _hadBin || bin.Count > 0;
            var total = 12 + 8 + json.Count + (withBin ? 8 + bin.Count : 0);
            var output = new List<byte>(total);
            output.AddRange(BitConverter.GetBytes(Magic));
            output.AddRange(BitConverter.GetBytes(2u));
            output.AddRange(BitConverter.GetBytes((uint)total));
            output.AddRange(BitConverter.GetBytes((uint)json.Count));
            output.AddRange(BitConverter.GetBytes(ChunkJson));
            output.AddRange(json);
            if (withBin)
            {
                output.AddRange(BitConverter.GetBytes((uint)bin.Count));
                output.AddRange(BitConverter.GetBytes(ChunkBin));
                output.AddRange(bin);
            }
            return output.ToArray();
        }

        /// <summary>Embeds a PNG in the binary chunk as a new glTF image; returns its index.</summary>
        public int AddPng(byte[] png, string name)
        {
            while (Bin.Count % 4 != 0) Bin.Add(0);
            var offset = Bin.Count;
            Bin.AddRange(png);
            var views = GltfJson.Array(Json, "bufferViews", true);
            views.Add(new JObj { { "buffer", 0 }, { "byteOffset", offset }, { "byteLength", png.Length } });
            var images = GltfJson.Array(Json, "images", true);
            images.Add(new JObj { { "name", name }, { "mimeType", "image/png" }, { "bufferView", views.Count - 1 } });
            return images.Count - 1;
        }

        /// <summary>Sets extras.hyperbox[key] on the file's root, keeping anything else there.</summary>
        public void SetHyperBoxExtra(string key, object value)
        {
            var extras = GltfJson.Obj(Json, "extras", true);
            var hb = GltfJson.Obj(extras, "hyperbox", true);
            hb[key] = value;
        }
    }
}
