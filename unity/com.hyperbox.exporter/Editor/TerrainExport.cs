using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HyperBox.Export
{
    /// <summary>
    /// Unity Terrain → things a GLB can hold: the heightmap as a mesh, the painted layers baked
    /// into one colour texture, and the trees as copies of their prefabs. Built as temporary
    /// scene objects for the exporter to write; call Destroy afterwards.
    /// </summary>
    public static class TerrainExport
    {
        public class Built
        {
            public GameObject Ground;   // mesh with the baked texture, at the terrain's position
            public GameObject Trees;    // copies of the tree prefabs, in world placement (or null)
            public int TreeCount, TreesSkipped;
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
        static Material NewLitMaterial()
        {
            var pipeline = SceneLighting.CurrentPipeline();
            Shader shader = null;
            if (pipeline == SceneLighting.Pipeline.Urp) shader = Shader.Find("Universal Render Pipeline/Lit");
            else if (pipeline == SceneLighting.Pipeline.Hdrp) shader = Shader.Find("HDRP/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null && GraphicsSettings.currentRenderPipeline != null)
                shader = GraphicsSettings.currentRenderPipeline.defaultMaterial?.shader;
            return new Material(shader);
        }

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
                        r += c.r * w; g += c.g * w; b += c.b * w;
                        total += w;
                    }
                    if (total > 0) { r /= total; g /= total; b /= total; }
                    output[py * size + px] = new Color32((byte)r, (byte)g, (byte)b, 255);
                }
            }
            var result = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "terrain_color" };
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
        public static Color32[] ReadableCopy(Texture tex, int maxSize, out int width, out int height)
        {
            var scale = Math.Min(1f, maxSize / (float)Math.Max(tex.width, tex.height));
            width = Math.Max(1, (int)(tex.width * scale));
            height = Math.Max(1, (int)(tex.height * scale));
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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
    }
}
