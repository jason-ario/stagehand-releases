using System.Collections.Generic;

namespace HyperBox.Export
{
    /// <summary>What a material needs in the GLB that UnityGLTF may not have written (custom
    /// shaders: cutout foliage, two-sided leaves, tiled textures). Null fields leave the GLB as is.</summary>
    public class MaterialFix
    {
        /// <summary>"OPAQUE", "MASK" (cutout) or "BLEND" (transparent).</summary>
        public string AlphaMode;
        public double AlphaCutoff = 0.5;
        public bool? DoubleSided;
        /// <summary>Unity texture tiling (_MainTex_ST xy) and offset (zw); written as
        /// KHR_texture_transform on textures that don't have one yet.</summary>
        public double[] Tiling, Offset;

        public bool HasTransform =>
            Tiling != null && Offset != null &&
            (Tiling[0] != 1 || Tiling[1] != 1 || Offset[0] != 0 || Offset[1] != 0);
    }

    /// <summary>Applies MaterialFix entries (by material name) to a glTF. Pure C# (no UnityEngine).</summary>
    public static class MaterialFixes
    {
        public const string TextureTransform = "KHR_texture_transform";

        static readonly string[] PbrTextures = { "baseColorTexture", "metallicRoughnessTexture" };
        static readonly string[] MaterialTextures = { "normalTexture", "occlusionTexture", "emissiveTexture" };

        /// <summary>Unity's UV transform (uv * tiling + offset, v up) as glTF's (v down):
        /// glTF v = 1 - Unity v, so the v offset becomes 1 - tiling.y - offset.y.</summary>
        public static double[] GltfOffset(double[] tiling, double[] offset) =>
            new[] { offset[0], 1 - tiling[1] - offset[1] };

        /// <returns>how many materials were changed</returns>
        public static int Apply(JObj gltf, IDictionary<string, MaterialFix> fixes)
        {
            var materials = GltfJson.Array(gltf, "materials");
            if (materials == null || fixes == null || fixes.Count == 0) return 0;
            var changed = 0;
            var usedTransform = false;
            foreach (var item in materials)
            {
                if (!(item is JObj m) || !(m["name"] is string name) || !fixes.TryGetValue(name, out var fix)) continue;
                changed++;
                if (fix.AlphaMode != null)
                {
                    m["alphaMode"] = fix.AlphaMode;
                    if (fix.AlphaMode == "MASK") m["alphaCutoff"] = fix.AlphaCutoff;
                }
                if (fix.DoubleSided.HasValue) m["doubleSided"] = fix.DoubleSided.Value;
                if (fix.HasTransform)
                {
                    var pbr = GltfJson.Obj(m, "pbrMetallicRoughness");
                    if (pbr != null)
                        foreach (var key in PbrTextures)
                            usedTransform |= AddTransform(pbr[key] as JObj, fix);
                    foreach (var key in MaterialTextures)
                        usedTransform |= AddTransform(m[key] as JObj, fix);
                }
            }
            if (usedTransform)
            {
                var used = GltfJson.Array(gltf, "extensionsUsed", true);
                if (!used.Contains(TextureTransform)) used.Add(TextureTransform);
            }
            return changed;
        }

        static bool AddTransform(JObj textureInfo, MaterialFix fix)
        {
            if (textureInfo == null) return false;
            var ext = GltfJson.Obj(textureInfo, "extensions", true);
            if (ext.Has(TextureTransform)) return false;   // UnityGLTF already wrote one
            ext[TextureTransform] = new JObj
            {
                { "offset", GltfOffset(fix.Tiling, fix.Offset) },
                { "scale", new[] { fix.Tiling[0], fix.Tiling[1] } },
            };
            return true;
        }
    }
}
