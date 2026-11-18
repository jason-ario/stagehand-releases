using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HyperBox.Export
{
    /// <summary>
    /// Writes HyperBox's data into a GLB's top-level "extras" (extras.hyperbox), e.g. the
    /// Humanoid bone map, so the file describes its own rig wherever it goes: placed in a film,
    /// dragged onto HyperBox's window, or imported as an animation set. Pure C# (no UnityEngine).
    /// </summary>
    public static class GlbPatch
    {
        const uint Magic = 0x46546C67;      // "glTF"
        const uint ChunkJson = 0x4E4F534A;  // "JSON"

        /// <summary>The bone map as HyperBox stores it: standard bone → bone name after Godot's import.</summary>
        public static JObj HyperBoxBoneMap(IEnumerable<KeyValuePair<string, string>> unityMap)
        {
            var map = new JObj();
            foreach (var kv in unityMap)
            {
                var bone = Coords.HyperBoxBone(kv.Key);
                if (bone != null && !string.IsNullOrEmpty(kv.Value))
                    map[bone] = Coords.GodotBoneName(kv.Value);
            }
            return map;
        }

        public static void AddHyperBoxExtras(string glbPath, JObj hyperbox)
        {
            File.WriteAllBytes(glbPath, AddHyperBoxExtras(File.ReadAllBytes(glbPath), hyperbox));
        }

        public static byte[] AddHyperBoxExtras(byte[] glb, JObj hyperbox)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != Magic)
                throw new InvalidDataException("not a GLB file");
            var jsonLength = (int)BitConverter.ToUInt32(glb, 12);
            if (BitConverter.ToUInt32(glb, 16) != ChunkJson || 20 + jsonLength > glb.Length)
                throw new InvalidDataException("GLB has no JSON chunk");
            var json = Encoding.UTF8.GetString(glb, 20, jsonLength).TrimEnd(' ', '\0');
            var patched = AddToJson(json, FilmJson.Write(hyperbox).Trim());

            var jsonBytes = new List<byte>(Encoding.UTF8.GetBytes(patched));
            while (jsonBytes.Count % 4 != 0) jsonBytes.Add(0x20);
            var rest = glb.Length - (20 + jsonLength);  // the BIN chunk (and anything after it), unchanged
            var output = new byte[20 + jsonBytes.Count + rest];
            Array.Copy(glb, 0, output, 0, 8);  // magic + version
            BitConverter.GetBytes((uint)output.Length).CopyTo(output, 8);
            BitConverter.GetBytes((uint)jsonBytes.Count).CopyTo(output, 12);
            BitConverter.GetBytes(ChunkJson).CopyTo(output, 16);
            jsonBytes.CopyTo(output, 20);
            Array.Copy(glb, 20 + jsonLength, output, 20 + jsonBytes.Count, rest);
            return output;
        }

        /// <summary>Puts "hyperbox": value into the root object's "extras" (creating it, or
        /// replacing an earlier "hyperbox" entry).</summary>
        public static string AddToJson(string json, string hyperboxValue)
        {
            var root = json.IndexOf('{');
            if (root < 0) throw new InvalidDataException("glTF JSON has no root object");
            var extras = FindKey(json, root, "extras");
            if (extras.valueStart < 0)
                return json.Substring(0, root + 1) + "\"extras\":{\"hyperbox\":" + hyperboxValue + "}" +
                       (IsEmptyObject(json, root) ? "" : ",") + json.Substring(root + 1);
            if (json[extras.valueStart] != '{')
                throw new InvalidDataException("glTF extras is not an object");
            var old = FindKey(json, extras.valueStart, "hyperbox");
            if (old.valueStart >= 0)
                return json.Substring(0, old.valueStart) + hyperboxValue + json.Substring(old.valueEnd);
            return json.Substring(0, extras.valueStart + 1) + "\"hyperbox\":" + hyperboxValue +
                   (IsEmptyObject(json, extras.valueStart) ? "" : ",") + json.Substring(extras.valueStart + 1);
        }

        static bool IsEmptyObject(string json, int open)
        {
            var i = SkipSpace(json, open + 1);
            return i < json.Length && json[i] == '}';
        }

        /// <summary>Finds `key` among the direct members of the object starting at `open`.
        /// Returns where its value starts and ends (-1 if absent).</summary>
        static (int valueStart, int valueEnd) FindKey(string json, int open, string key)
        {
            var i = SkipSpace(json, open + 1);
            while (i < json.Length && json[i] != '}')
            {
                if (json[i] == ',') { i = SkipSpace(json, i + 1); continue; }
                var nameEnd = SkipString(json, i);
                var name = json.Substring(i + 1, nameEnd - i - 2);
                i = SkipSpace(json, nameEnd);
                if (i >= json.Length || json[i] != ':') throw new InvalidDataException("bad glTF JSON");
                var valueStart = SkipSpace(json, i + 1);
                var valueEnd = SkipValue(json, valueStart);
                if (name == key) return (valueStart, valueEnd);
                i = SkipSpace(json, valueEnd);
            }
            return (-1, -1);
        }

        static int SkipSpace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        static int SkipString(string s, int i)
        {
            if (s[i] != '"') throw new InvalidDataException("bad glTF JSON");
            for (i++; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == '"') return i + 1;
            }
            throw new InvalidDataException("unterminated string in glTF JSON");
        }

        static int SkipValue(string s, int i)
        {
            if (s[i] == '"') return SkipString(s, i);
            if (s[i] == '{' || s[i] == '[')
            {
                var depth = 0;
                for (; i < s.Length; i++)
                {
                    var c = s[i];
                    if (c == '"') { i = SkipString(s, i) - 1; continue; }
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }
                throw new InvalidDataException("unbalanced glTF JSON");
            }
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            return i;
        }
    }
}
