using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HyperBox.Export
{
    /// <summary>
    /// Unity Terrain → things a GLB can hold: the heightmap as a mesh, the painted layers, and
    /// the trees as copies of their prefabs. Built as temporary scene objects for the exporter
    /// to write; call Destroy afterwards.
    ///
    /// The painted layers go two ways: baked into one colour texture (the material every glTF
    /// viewer shows), and — embedded in the GLB with EmbedLayers — as the splat control maps
    /// plus each layer's own textures and tiling (extras.hyperbox.terrain), which HyperBox
    /// draws with a splat shader so the ground keeps Unity's full tiled detail up close.
    /// </summary>
    public static class TerrainExport
    {
        public class Built
        {
            public GameObject Ground;   // mesh with the baked texture, at the terrain's position
            public GameObject Trees;    // copies of the tree prefabs, in world placement (or null)
            public int TreeCount, TreesSkipped;
            /// <summary>The splat data for EmbedLayers (null: no painted layers).</summary>
            public Splat Layers;
            public int LayerCount => Layers != null ? Layers.Layers.Count : 0;

            /// <summary>Adds the painted layers to the ground's GLB (extras.hyperbox.terrain).</summary>
            public void EmbedLayers(string glbPath, List<string> warnings)
            {
                if (Layers == null || !File.Exists(glbPath)) return;
                try
                {
                    var glb = GlbFile.Read(File.ReadAllBytes(glbPath));
                    glb.SetHyperBoxExtra("terrain", Layers.Embed(glb));
                    File.WriteAllBytes(glbPath, glb.Write());
                }
                catch (Exception e)
                {
                    warnings.Add($"Terrain \"{Ground?.name}\": its layers couldn't be embedded ({e.Message}); it uses the baked texture.");
                }
            }

            public void Destroy()
            {
                if (Ground != null)
                {
                    var mr = Ground.GetComponent<MeshRenderer>();
                    if (mr != null && mr.sharedMaterial != null)
                    {
                        Object.DestroyImmediate(mr.sharedMaterial.mainTexture);
                        Object.DestroyImmediate(mr.sharedMaterial);
                    }
                    var mf = Ground.GetComponent<MeshFilter>();
                    if (mf != null) Object.DestroyImmediate(mf.sharedMesh);
                    Object.DestroyImmediate(Ground);
                }
                if (Trees != null) Object.DestroyImmediate(Trees);
            }
        }

        /// <param name="gridSize">vertices per side (the heightmap is resampled to this)</param>
        /// <param name="textureSize">pixels per side of the baked colour texture</param>
        /// <param name="maxTrees">trees beyond this are thinned out evenly</param>
        public static Built Build(Terrain terrain, int gridSize, int textureSize, int maxTrees, List<string> warnings)
        {
            var data = terrain.terrainData;
            var built = new Built();
            EditorUtility.DisplayProgressBar("Export to HyperBox", terrain.name + ": ground", 0.1f);
            built.Ground = BuildGround(terrain, data, Mathf.Clamp(gridSize, 2, Math.Max(2, data.heightmapResolution)), textureSize);
            EditorUtility.DisplayProgressBar("Export to HyperBox", terrain.name + ": layers", 0.55f);
            built.Layers = Splat.Build(data, warnings, terrain.name);
            if (data.detailPrototypes != null && data.detailPrototypes.Length > 0)
                warnings.Add($"Terrain \"{terrain.name}\": grass and detail meshes aren't exported yet.");
            if (terrain.drawTreesAndFoliage && data.treeInstanceCount > 0)
            {
                EditorUtility.DisplayProgressBar("Export to HyperBox", terrain.name + ": trees", 0.6f);
                built.Trees = BuildTrees(terrain, data, maxTrees, built, warnings);
            }
            return built;
        }

        static GameObject BuildGround(Terrain terrain, TerrainData data, int n, int textureSize)
        {
            var size = data.size;
            var vertices = new Vector3[n * n];
            var normals = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            for (var z = 0; z < n; z++)
            {
                var v = z / (float)(n - 1);
                for (var x = 0; x < n; x++)
                {
                    var u = x / (float)(n - 1);
                    var i = z * n + x;
                    vertices[i] = new Vector3(u * size.x, data.GetInterpolatedHeight(u, v), v * size.z);
                    normals[i] = data.GetInterpolatedNormal(u, v);
                    uvs[i] = new Vector2(u, v);
                }
            }
            var triangles = new int[(n - 1) * (n - 1) * 6];
            var t = 0;
            for (var z = 0; z < n - 1; z++)
            {
                for (var x = 0; x < n - 1; x++)
                {
                    int a = z * n + x, b = a + 1, c = a + n, d = c + 1;
                    // Unity's front faces wind clockwise seen from above.
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }
            var mesh = new Mesh { name = terrain.name };
            if (vertices.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            var go = new GameObject(terrain.name);
            go.transform.position = terrain.transform.position;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = NewLitMaterial();
            material.name = terrain.name + " ground";
            var texture = BakeLayers(data, textureSize);
            if (texture != null)
            {
                material.mainTexture = texture;
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            }
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        /// <summary>A material UnityGLTF knows how to export in the current render pipeline.</summary>
        static Material NewLitMaterial() => new Material(MaterialExport.NewLitShader());

        /// <summary>The painted terrain layers blended into one colour texture (like Unity's distant
        /// "base map", but at full resolution).</summary>
        static Texture2D BakeLayers(TerrainData data, int size)
        {
            var layers = data.terrainLayers;
            if (layers == null || layers.Length == 0) return null;
            int aw = data.alphamapWidth, ah = data.alphamapHeight;
            var alpha = data.GetAlphamaps(0, 0, aw, ah);  // [y, x, layer]
            var count = Math.Min(layers.Length, alpha.GetLength(2));
            var pixels = new Color32[count][];
            var widths = new int[count];
            var heights = new int[count];
            for (var l = 0; l < count; l++)
            {
                var tex = layers[l] != null ? layers[l].diffuseTexture : null;
                if (tex == null) continue;
                pixels[l] = ReadableCopy(tex, 1024, out widths[l], out heights[l]);
            }

            var terrainSize = data.size;
            var output = new Color32[size * size];
            for (var py = 0; py < size; py++)
            {
                if (py % 128 == 0)
                    EditorUtility.DisplayProgressBar("Export to HyperBox", "Painting terrain texture", 0.1f + 0.5f * py / size);
                var v = (py + 0.5f) / size;
                var ay = Math.Min(ah - 1, (int)(v * ah));
                var worldZ = v * terrainSize.z;
                for (var px = 0; px < size; px++)
                {
                    var u = (px + 0.5f) / size;
                    var ax = Math.Min(aw - 1, (int)(u * aw));
                    var worldX = u * terrainSize.x;
                    float r = 0, g = 0, b = 0, total = 0;
                    for (var l = 0; l < count; l++)
                    {
                        var w = alpha[ay, ax, l];
                        if (w < 0.003f || pixels[l] == null) continue;
                        var layer = layers[l];
                        var tile = layer.tileSize;
                        var tu = (worldX + layer.tileOffset.x) / Math.Max(0.01f, tile.x);
                        var tv = (worldZ + layer.tileOffset.y) / Math.Max(0.01f, tile.y);
                        var c = SampleWrap(pixels[l], widths[l], heights[l], tu, tv);
                        var tint = (Vector4)layer.diffuseRemapMax;
                        r += c.r * tint.x * w; g += c.g * tint.y * w; b += c.b * tint.z * w;
                        total += w;
                    }
                    if (total > 0) { r /= total; g /= total; b /= total; }
                    output[py * size + px] = new Color32((byte)Mathf.Min(255, r), (byte)Mathf.Min(255, g), (byte)Mathf.Min(255, b), 255);
                }
            }
            var result = new Texture2D(size, size, TextureFormat.RGB24, true) { name = "terrain_color" };
            result.SetPixels32(output);
            result.Apply(true);
            return result;
        }

        static Color32 SampleWrap(Color32[] px, int w, int h, float u, float v)
        {
            u -= Mathf.Floor(u);
            v -= Mathf.Floor(v);
            var x = Math.Min(w - 1, (int)(u * w));
            var y = Math.Min(h - 1, (int)(v * h));
            return px[y * w + x];
        }

        /// <summary>Pixels of any texture (readable or not, compressed or not), at most maxSize wide.</summary>
        public static Color32[] ReadableCopy(Texture tex, int maxSize, out int width, out int height, bool linear = false)
        {
            var scale = Math.Min(1f, maxSize / (float)Math.Max(tex.width, tex.height));
            width = Math.Max(1, (int)(tex.width * scale));
            height = Math.Max(1, (int)(tex.height * scale));
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(copy);
            }
        }

        static GameObject BuildTrees(Terrain terrain, TerrainData data, int maxTrees, Built built, List<string> warnings)
        {
            var prototypes = data.treePrototypes;
            var trees = data.treeInstances;
            var step = maxTrees > 0 && trees.Length > maxTrees ? trees.Length / (double)maxTrees : 1.0;
            var root = new GameObject(terrain.name + " trees");
            var origin = terrain.transform.position;
            var missing = 0;
            for (double i = 0; i < trees.Length; i += step)
            {
                var tree = trees[(int)i];
                if (tree.prototypeIndex < 0 || tree.prototypeIndex >= prototypes.Length || prototypes[tree.prototypeIndex].prefab == null)
                {
                    missing++;
                    continue;
                }
                var prefab = prototypes[tree.prototypeIndex].prefab;
                var position = origin + Vector3.Scale(tree.position, data.size);
                var rotation = Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0);
                var copy = Object.Instantiate(prefab, position, rotation, root.transform);
                copy.name = prefab.name;
                var s = prefab.transform.localScale;
                copy.transform.localScale = new Vector3(s.x * tree.widthScale, s.y * tree.heightScale, s.z * tree.widthScale);
                built.TreeCount++;
            }
            built.TreesSkipped = trees.Length - built.TreeCount - missing;
            if (built.TreesSkipped > 0)
                warnings.Add($"Terrain \"{terrain.name}\": {trees.Length} trees, so every {step:0.#}th of them was kept ({built.TreeCount}). Raise \"Max trees\" to keep more.");
            if (missing > 0)
                warnings.Add($"Terrain \"{terrain.name}\": {missing} tree(s) have no prefab and were skipped.");
            if (built.TreeCount == 0)
            {
                Object.DestroyImmediate(root);
                return null;
            }
            return root;
        }
            /// <summary>A terrain's painted layers as HyperBox draws them: up to 8 layers (the most
        /// painted ones), two RGBA control maps of their weights, and per-layer textures as PNG.</summary>
        public class Splat
        {
            public const int MaxLayers = 8;
            public Vector3 Size;
            public byte[][] Control = new byte[0][];
            public readonly List<Layer> Layers = new List<Layer>();

            public class Layer
            {
                public string Name;
                public byte[] Albedo, Normal;   // PNG (Normal may be null)
                public Vector2 Tile, Offset;
                public Color Tint = Color.white;
                public float Smoothness, Metallic, NormalScale = 1;
            }

            public static Splat Build(TerrainData data, List<string> warnings, string terrainName)
            {
                var layers = data.terrainLayers;
                if (layers == null || layers.Length == 0) return null;
                int aw = data.alphamapWidth, ah = data.alphamapHeight;
                var alpha = data.GetAlphamaps(0, 0, aw, ah);  // [y, x, layer]
                var count = Math.Min(layers.Length, alpha.GetLength(2));

                // The most painted layers that have a texture.
                var totals = new double[count];
                for (var y = 0; y < ah; y++)
                    for (var x = 0; x < aw; x++)
                        for (var l = 0; l < count; l++)
                            totals[l] += alpha[y, x, l];
                var keep = Enumerable.Range(0, count)
                    .Where(l => layers[l] != null && layers[l].diffuseTexture != null && totals[l] > 0)
                    .OrderByDescending(l => totals[l]).Take(MaxLayers).OrderBy(l => l).ToList();
                if (keep.Count == 0) return null;
                var used = Enumerable.Range(0, count).Count(l => totals[l] > 0 && layers[l] != null);
                if (used > keep.Count)
                    warnings.Add($"Terrain \"{terrainName}\": {used} painted layers; HyperBox draws the {keep.Count} most used (the rest blend into them).");

                var splat = new Splat { Size = data.size };
                var maps = (keep.Count + 3) / 4;
                splat.Control = new byte[maps][];
                for (var c = 0; c < maps; c++)
                {
                    var pixels = new Color32[aw * ah];
                    for (var y = 0; y < ah; y++)
                    {
                        for (var x = 0; x < aw; x++)
                        {
                            var px = new byte[4];
                            for (var ch = 0; ch < 4; ch++)
                            {
                                var k = c * 4 + ch;
                                px[ch] = k < keep.Count ? (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha[y, x, keep[k]]) * 255) : (byte)0;
                            }
                            pixels[y * aw + x] = new Color32(px[0], px[1], px[2], px[3]);
                        }
                    }
                    splat.Control[c] = Png(pixels, aw, ah, true);
                }

                foreach (var l in keep)
                {
                    var layer = layers[l];
                    var albedo = ReadableCopy(layer.diffuseTexture, 1024, out var w, out var h);
                    for (var i = 0; i < albedo.Length; i++) albedo[i].a = 255;   // alpha is smoothness in Unity
                    var entry = new Layer
                    {
                        Name = layer.name,
                        Albedo = Png(albedo, w, h, false),
                        Tile = layer.tileSize,
                        Offset = layer.tileOffset,
                        Tint = (Color)(Vector4)layer.diffuseRemapMax,
                        Smoothness = layer.smoothness,
                        Metallic = layer.metallic,
                        NormalScale = layer.normalScale,
                    };
                    if (layer.normalMapTexture != null)
                        entry.Normal = Png(DecodeNormals(ReadableCopy(layer.normalMapTexture, 1024, out var nw, out var nh, true)), nw, nh, false);
                    splat.Layers.Add(entry);
                }
                return splat;
            }

            /// <summary>Unity's packed normals (DXT5nm: x in alpha; BC5/plain: x in red) as an
            /// ordinary RGB normal map (+Y up, like Godot's).</summary>
            static Color32[] DecodeNormals(Color32[] px)
            {
                var output = new Color32[px.Length];
                for (var i = 0; i < px.Length; i++)
                {
                    var p = px[i];
                    var x = (p.r / 255f) * (p.a / 255f) * 2 - 1;   // Unity's UnpackNormal trick
                    var y = p.g / 255f * 2 - 1;
                    var z = Mathf.Sqrt(Mathf.Max(0, 1 - x * x - y * y));
                    output[i] = new Color32((byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255), (byte)Mathf.RoundToInt((y * 0.5f + 0.5f) * 255),
                        (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255), 255);
                }
                return output;
            }

            static byte[] Png(Color32[] pixels, int w, int h, bool withAlpha)
            {
                var tex = new Texture2D(w, h, withAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
                try
                {
                    tex.SetPixels32(pixels);
                    tex.Apply(false);
                    return tex.EncodeToPNG();
                }
                finally
                {
                    Object.DestroyImmediate(tex);
                }
            }

            /// <summary>Embeds the textures in the GLB and returns extras.hyperbox.terrain.</summary>
            public JObj Embed(GlbFile glb)
            {
                var control = new List<object>();
                for (var c = 0; c < Control.Length; c++)
                    control.Add(glb.AddPng(Control[c], $"terrain_control_{c}"));
                var layers = new List<object>();
                for (var i = 0; i < Layers.Count; i++)
                {
                    var l = Layers[i];
                    var o = new JObj
                    {
                        { "name", l.Name },
                        { "albedo", glb.AddPng(l.Albedo, $"terrain_layer_{i}_albedo") },
                        { "normal", l.Normal != null ? glb.AddPng(l.Normal, $"terrain_layer_{i}_normal") : -1 },
                        { "tile", new double[] { l.Tile.x, l.Tile.y } },
                        { "offset", new double[] { l.Offset.x, l.Offset.y } },
                        { "tint", new double[] { l.Tint.r, l.Tint.g, l.Tint.b } },
                        { "smoothness", (double)l.Smoothness },
                        { "metallic", (double)l.Metallic },
                        { "normal_scale", (double)l.NormalScale },
                    };
                    layers.Add(o);
                }
                return new JObj
                {
                    { "size", new double[] { Size.x, Size.y, Size.z } },
                    { "control", control },
                    { "layers", layers },
                };
            }
        }
    }
}
