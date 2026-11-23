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
    /// Makes materials survive the trip to glTF. UnityGLTF only understands the standard Lit
    /// shaders, so on an export copy this:
    ///   - finds cutout (alpha-tested), transparent and two-sided materials, whatever the shader
    ///     (foliage, SpeedTree, Tree Creator leaves, Shader Graphs, asset-store shaders);
    ///   - swaps custom shaders for a Lit material with the same textures and settings;
    ///   - bakes world-space / triplanar texturing into the mesh's UVs (a GLB can't compute UVs
    ///     from world position), so world-tiled walls and floors keep their tiling;
    ///   - records each material's alpha mode, cutoff, sidedness and tiling, which PatchGlb then
    ///     writes into the GLB (KHR_texture_transform for tiling).
    /// Asset labels override the guesses: see MaterialExportMenu.
    /// </summary>
    public sealed class MaterialExport : IDisposable
    {
        // Asset labels a user can put on a material (Assets > HyperBox > Material Export).
        public const string LabelWorldUv = "HyperBox-WorldUV";
        public const string LabelCutout = "HyperBox-Cutout";
        public const string LabelTransparent = "HyperBox-Transparent";
        public const string LabelOpaque = "HyperBox-Opaque";
        public const string LabelDoubleSided = "HyperBox-DoubleSided";
        public static readonly string[] Labels = { LabelWorldUv, LabelCutout, LabelTransparent, LabelOpaque, LabelDoubleSided };

        public readonly Dictionary<string, MaterialFix> Fixes = new Dictionary<string, MaterialFix>();
        readonly List<Object> _temps = new List<Object>();
        readonly HashSet<Mesh> _ownMeshes = new HashSet<Mesh>();
        readonly Dictionary<Material, Material> _replaced = new Dictionary<Material, Material>();
        readonly Dictionary<string, Material> _names = new Dictionary<string, Material>();
        readonly Dictionary<Material, Analysis> _analysis = new Dictionary<Material, Analysis>();
        readonly List<string> _warnings;

        // One export run (several GLBs): what the report says, each material counted once.
        static readonly HashSet<Material> _counted = new HashSet<Material>();
        static readonly List<string> _sessionWarnings = new List<string>();
        static int _cutout, _transparent, _doubleSided, _worldUv, _converted, _tiled;

        public static void BeginSession()
        {
            _counted.Clear();
            _sessionWarnings.Clear();
            _cutout = _transparent = _doubleSided = _worldUv = _converted = _tiled = 0;
        }

        /// <summary>Adds the run's material summary to the report and its warnings to `warnings`.</summary>
        public static void EndSession(List<string> report, List<string> warnings)
        {
            var parts = new List<string>();
            if (_cutout > 0) parts.Add($"{_cutout} cutout (alpha-tested)");
            if (_transparent > 0) parts.Add($"{_transparent} transparent");
            if (_doubleSided > 0) parts.Add($"{_doubleSided} two-sided");
            if (_tiled > 0) parts.Add($"{_tiled} with tiled textures");
            if (_worldUv > 0) parts.Add($"{_worldUv} textured in world space (baked into UVs)");
            if (_converted > 0) parts.Add($"{_converted} custom shader(s) converted to standard PBR");
            if (parts.Count > 0) report.Add("Materials: " + string.Join(", ", parts) + ".");
            foreach (var w in _sessionWarnings)
                if (!warnings.Contains(w)) warnings.Add(w);
            _counted.Clear();
            _sessionWarnings.Clear();
        }

        public MaterialExport() { _warnings = _sessionWarnings; }

        public void Dispose()
        {
            foreach (var t in _temps)
                if (t != null) Object.DestroyImmediate(t);
            _temps.Clear();
        }

        /// <summary>Prepares an export copy's materials. `source` is the object it was copied from
        /// (same hierarchy, used for world positions); null when the copy is already in world
        /// placement (merged scenery, terrain trees). Call before pruning or stripping the copy.</summary>
        public void Prepare(GameObject copy, GameObject source)
        {
            var copies = copy.GetComponentsInChildren<Renderer>(true);
            var sources = source != null ? source.GetComponentsInChildren<Renderer>(true) : null;
            if (sources != null && sources.Length != copies.Length) sources = null;
            for (var r = 0; r < copies.Length; r++)
            {
                var renderer = copies[r];
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                var mats = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    var a = Analyze(m);
                    if (a.WorldUv)
                    {
                        var world = sources != null ? sources[r].localToWorldMatrix : renderer.localToWorldMatrix;
                        if (!BakeWorldUvs(renderer, i, a, world))
                        {
                            Warn($"Material \"{m.name}\" is textured in world space, which can't be baked into the skinned mesh \"{renderer.name}\"; it keeps the mesh's own UVs.");
                            a = a.WithoutWorldUv();
                        }
                    }
                    var exported = Exported(m, a);
                    if (exported != m) { mats[i] = exported; changed = true; }
                }
                if (changed) renderer.sharedMaterials = mats;
            }
        }

        /// <summary>Whether any visible material under `g` is textured in world space.</summary>
        public static bool UsesWorldSpaceUvs(GameObject g)
        {
            var probe = new MaterialExport();
            foreach (var r in g.GetComponentsInChildren<MeshRenderer>(false))
                foreach (var m in r.sharedMaterials)
                    if (m != null && probe.Analyze(m).WorldUv) return true;
            return false;
        }

        /// <summary>Writes the recorded alpha modes, sidedness and tiling into a written GLB.</summary>
        public void PatchGlb(string glbPath)
        {
            if (Fixes.Count == 0 || !File.Exists(glbPath)) return;
            try
            {
                var glb = GlbFile.Read(File.ReadAllBytes(glbPath));
                if (MaterialFixes.Apply(glb.Json, Fixes) > 0)
                    File.WriteAllBytes(glbPath, glb.Write());
            }
            catch (Exception e)
            {
                Warn($"Couldn't fix materials in {Path.GetFileName(glbPath)}: {e.Message}");
            }
        }

        void Warn(string w)
        {
            if (_warnings != null && !_warnings.Contains(w)) _warnings.Add(w);
        }

        // ------------------------------------------------------------------ analysis

        sealed class Analysis
        {
            public string Alpha = "OPAQUE";
            public float Cutoff = 0.5f;
            public bool DoubleSided, WorldUv, Replace, Foliage, Known;
            public Texture BaseMap;
            public Vector2 Tiling = Vector2.one, Offset = Vector2.zero;   // base map _ST
            public Vector2 WorldTiling = Vector2.one, WorldOffset = Vector2.zero;

            public Analysis WithoutWorldUv()
            {
                var c = (Analysis)MemberwiseClone();
                c.WorldUv = false;
                c.Replace = !c.Known;
                return c;
            }
        }

        static readonly string[] BaseMapNames =
        {
            "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture", "_Albedo", "_AlbedoMap", "_AlbedoTex",
            "_AlbedoTexture", "_MainTexture", "_BaseTexture", "_Diffuse", "_DiffuseMap", "_DiffuseTex",
            "_DiffuseTexture", "_ColorMap", "_ColorTexture", "_LeafTex", "_LeavesTex", "_FoliageTex", "_Texture", "_Tex",
        };
        static readonly string[] ColorNames = { "_BaseColor", "_Color", "_MainColor", "_AlbedoColor", "_Tint", "_TintColor", "_ColorTint", "_DiffuseColor" };
        static readonly string[] NormalNames = { "_BumpMap", "_NormalMap", "_Normal", "_NormalTex", "_NormalTexture", "_NormalMapTex" };
        static readonly string[] NormalScaleNames = { "_BumpScale", "_NormalScale", "_NormalStrength", "_NormalIntensity" };
        static readonly string[] CutoffNames = { "_Cutoff", "_AlphaCutoff", "_AlphaClipThreshold", "_AlphaClip_Threshold", "_AlphaThreshold", "_ClipThreshold", "_CutOff", "_Clip", "_AlphaCutout" };
        static readonly string[] CutoutToggles = { "_AlphaClip", "_AlphaCutoffEnable", "_UseAlphaClip", "_AlphaClipping", "_AlphaTest", "_AlphaTestEnable", "_Cutout", "_UseCutout" };
        static readonly string[] FoliageWords = { "leaf", "leaves", "foliage", "grass", "bush", "fern", "plant", "vegetation", "speedtree", "nature/", "flower", "ivy", "branch", "shrub", "hedge", "weed", "reed", "frond", "palm", "canopy", "billboard" };
        static readonly string[] WorldWords = { "triplanar", "tri-planar", "tri planar", "worldspace", "world space", "world_space", "worlduv", "world uv", "world_uv", "worldaligned", "world aligned", "world-aligned", "worldprojection", "world projection", "worldmapping", "world mapping", "worldcoord" };
        static readonly string[] WorldToggleWords = { "worldspace", "world_space", "worlduv", "world_uv", "useworld", "triplanar", "worldmapping", "worldaligned", "worldprojection" };

        Analysis Analyze(Material m)
        {
            if (_analysis.TryGetValue(m, out var cached)) return cached;
            var a = new Analysis();
            var shaderName = m.shader != null ? m.shader.name : "";
            var lowerShader = shaderName.ToLowerInvariant();
            var labels = new HashSet<string>(AssetDatabase.GetLabels(m));
            var keywords = m.shaderKeywords.Select(k => k.ToUpperInvariant()).ToArray();
            var renderType = m.GetTag("RenderType", true, "");

            a.Known = KnownShader(shaderName);
            a.Replace = !a.Known;
            var baseProp = FirstTexture(m, BaseMapNames) ?? GuessBaseMap(m);
            if (baseProp != null)
            {
                a.BaseMap = m.GetTexture(baseProp);
                a.Tiling = m.GetTextureScale(baseProp);
                a.Offset = m.GetTextureOffset(baseProp);
            }

            // Alpha: labels, then what the material says outright, then the render queue, then foliage.
            var names = (lowerShader + " " + m.name.ToLowerInvariant());
            a.Foliage = FoliageWords.Any(names.Contains);
            var standard = shaderName.StartsWith("Standard") || shaderName == "Autodesk Interactive";
            var transparent =
                keywords.Any(k => k == "_SURFACE_TYPE_TRANSPARENT" || k == "_ALPHABLEND_ON" || k == "_ALPHAPREMULTIPLY_ON" || k == "_BLENDMODE_ALPHA")
                || renderType == "Transparent" || renderType == "Fade"
                || Float(m, "_Surface") > 0.5f || Float(m, "_SurfaceType") > 0.5f
                || (standard && Float(m, "_Mode") >= 1.5f);
            var cutout =
                keywords.Any(k => k.Contains("ALPHATEST") || k.Contains("ALPHACLIP") || k.Contains("ALPHA_CLIP") || k.Contains("CUTOUT"))
                || renderType == "TransparentCutout" || renderType == "TreeLeaf" || renderType == "TreeTransparentCutout"
                || renderType == "Grass" || renderType == "GrassBillboard"
                || CutoutToggles.Any(t => Float(m, t) > 0.5f)
                || (standard && Mathf.Approximately(Float(m, "_Mode"), 1f))
                || lowerShader.Contains("cutout");
            var queue = m.renderQueue;
            if (labels.Contains(LabelOpaque)) a.Alpha = "OPAQUE";
            else if (labels.Contains(LabelTransparent)) a.Alpha = "BLEND";
            else if (labels.Contains(LabelCutout)) a.Alpha = "MASK";
            else if (transparent) a.Alpha = "BLEND";
            else if (cutout) a.Alpha = "MASK";
            // Guesses only for custom shaders: the standard ones say outright when they're cutout
            // or transparent, and often keep smoothness in the colour map's alpha.
            else if (!a.Known && queue >= 2450 && queue < 2500) a.Alpha = "MASK";
            else if (!a.Known && queue > 2500 && queue < 4000 && HasAlpha(a.BaseMap)) a.Alpha = "BLEND";
            else if (!a.Known && a.Foliage && HasAlpha(a.BaseMap) && Float(m, "_SmoothnessTextureChannel") < 0.5f) a.Alpha = "MASK";
            var cutoffProp = CutoffNames.FirstOrDefault(n => m.HasProperty(n) && IsFloat(m, n));
            if (cutoffProp != null) a.Cutoff = Mathf.Clamp(m.GetFloat(cutoffProp), 0.01f, 0.99f);

            // Sides: labels, cull properties (CullMode.Off = 0), then foliage cards.
            var cull = new[] { "_Cull", "_CullMode", "_Culling", "_CullingMode", "_CullModeForward" }.FirstOrDefault(n => m.HasProperty(n) && IsFloat(m, n));
            if (labels.Contains(LabelDoubleSided)) a.DoubleSided = true;
            else if (cull != null) a.DoubleSided = Mathf.RoundToInt(m.GetFloat(cull)) == 0;
            else if (new[] { "_DoubleSidedEnable", "_DoubleSided", "_TwoSided" }.Any(n => Float(m, n) > 0.5f)) a.DoubleSided = true;
            else if (m.HasProperty("_RenderFace") && IsFloat(m, "_RenderFace")) a.DoubleSided = Mathf.RoundToInt(m.GetFloat("_RenderFace")) == 0;
            if (!a.DoubleSided && a.Alpha == "MASK" && (a.Foliage || renderType == "TreeLeaf" || renderType == "Grass" || renderType == "GrassBillboard")
                && cull == null)
                a.DoubleSided = true;

            // World-space texturing.
            a.WorldUv = labels.Contains(LabelWorldUv)
                        || WorldWords.Any(lowerShader.Contains)
                        || keywords.Any(k => k.Contains("TRIPLANAR") || k.Contains("WORLDSPACE") || k.Contains("WORLD_SPACE") || k.Contains("WORLDUV") || k.Contains("WORLD_UV"))
                        || WorldToggles(m);
            if (a.WorldUv) (a.WorldTiling, a.WorldOffset) = WorldTiling(m, a);
            if (a.WorldUv) a.Replace = true;   // its UVs are baked for the standard shader

            _analysis[m] = a;
            return a;
        }

        /// <summary>The material to export in place of `m` (itself, a converted copy, or a renamed
        /// copy when another material already uses its name), and its fix for PatchGlb.</summary>
        Material Exported(Material m, Analysis a)
        {
            if (_replaced.TryGetValue(m, out var done)) return done;
            var result = m;
            if (a.Replace)
            {
                result = ConvertToLit(m, a);
            }
            // glTF materials are found by name, so two different materials can't share one.
            var name = result.name;
            if (_names.TryGetValue(name, out var owner) && owner != m)
            {
                for (var n = 2; _names.ContainsKey(name); n++) name = $"{result.name} ({n})";
                if (result == m)
                {
                    result = new Material(m);
                    _temps.Add(result);
                }
                result.name = name;
            }
            _names[name] = m;
            _replaced[m] = result;

            var fix = new MaterialFix { AlphaMode = a.Alpha, AlphaCutoff = Math.Round(a.Cutoff, 4), DoubleSided = a.DoubleSided };
            if (!a.WorldUv)
            {
                fix.Tiling = new double[] { a.Tiling.x, a.Tiling.y };
                fix.Offset = new double[] { a.Offset.x, a.Offset.y };
            }
            Fixes[name] = fix;
            if (_counted.Add(m))
            {
                if (a.Alpha == "MASK") _cutout++;
                if (a.Alpha == "BLEND") _transparent++;
                if (a.DoubleSided) _doubleSided++;
                if (a.WorldUv) _worldUv++;
                if (fix.HasTransform) _tiled++;
                if (a.Replace) _converted++;
            }
            return result;
        }

        // ------------------------------------------------------------------ conversion

        /// <summary>Shaders UnityGLTF exports properly.</summary>
        static bool KnownShader(string name) =>
            name == "Standard" || name == "Standard (Specular setup)" || name == "Autodesk Interactive"
            || name.StartsWith("Universal Render Pipeline/") && !name.Contains("Terrain") && !name.Contains("Particles") && !name.Contains("Nature")
            || name == "HDRP/Lit" || name == "HDRP/Unlit" || name == "HDRP/LitTessellation"
            || name.StartsWith("UnityGLTF/") || name.StartsWith("Unlit/");

        Material ConvertToLit(Material m, Analysis a)
        {
            var lit = new Material(NewLitShader()) { name = m.name };
            _temps.Add(lit);
            var urp = lit.HasProperty("_BaseMap");
            var hdrp = lit.HasProperty("_BaseColorMap");

            if (a.BaseMap != null)
            {
                lit.mainTexture = a.BaseMap;
                SetTex(lit, "_BaseMap", a.BaseMap);
                SetTex(lit, "_MainTex", a.BaseMap);
                SetTex(lit, "_BaseColorMap", a.BaseMap);
            }
            var colorProp = ColorNames.FirstOrDefault(n => m.HasProperty(n) && IsColor(m, n));
            var color = colorProp != null ? m.GetColor(colorProp) : Color.white;
            SetColor(lit, "_BaseColor", color);
            SetColor(lit, "_Color", color);

            var normalProp = FirstTexture(m, NormalNames);
            if (normalProp != null)
            {
                var normal = m.GetTexture(normalProp);
                var scaleProp = NormalScaleNames.FirstOrDefault(n => m.HasProperty(n) && IsFloat(m, n));
                var scale = scaleProp != null ? m.GetFloat(scaleProp) : 1f;
                SetTex(lit, hdrp ? "_NormalMap" : "_BumpMap", normal);
                SetFloat(lit, hdrp ? "_NormalScale" : "_BumpScale", scale);
                lit.EnableKeyword(hdrp ? "_NORMALMAP_TANGENT_SPACE" : "_NORMALMAP");
                if (hdrp) lit.EnableKeyword("_NORMALMAP");
            }

            SetFloat(lit, "_Metallic", FirstFloat(m, 0f, "_Metallic", "_Metalness", "_Metal"));
            var smoothness = FirstFloat(m, -1f, "_Smoothness", "_Glossiness", "_Gloss");
            if (smoothness < 0)
            {
                var rough = FirstFloat(m, -1f, "_Roughness");
                smoothness = rough >= 0 ? 1 - rough : (a.Foliage ? 0.2f : 0.3f);
            }
            SetFloat(lit, "_Smoothness", smoothness);
            SetFloat(lit, "_Glossiness", smoothness);

            var emissionMap = FirstTexture(m, "_EmissionMap", "_EmissiveColorMap", "_EmissiveMap", "_EmissionTex");
            var emissionColorProp = new[] { "_EmissionColor", "_EmissiveColor", "_Emission" }.FirstOrDefault(n => m.HasProperty(n) && IsColor(m, n));
            var emissionColor = emissionColorProp != null ? m.GetColor(emissionColorProp) : Color.black;
            if (emissionMap != null || emissionColor.maxColorComponent > 0.001f)
            {
                if (emissionMap != null)
                {
                    SetTex(lit, "_EmissionMap", m.GetTexture(emissionMap));
                    SetTex(lit, "_EmissiveColorMap", m.GetTexture(emissionMap));
                    if (emissionColorProp == null) emissionColor = Color.white;
                }
                SetColor(lit, "_EmissionColor", emissionColor);
                SetColor(lit, "_EmissiveColor", emissionColor);
                lit.EnableKeyword("_EMISSION");
                lit.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            // Tiling travels in the GLB patch (or is baked into world UVs), not on the copy.
            ApplyAlpha(lit, a.Alpha, a.Cutoff, urp, hdrp);
            if (a.DoubleSided)
            {
                SetFloat(lit, "_Cull", 0);
                SetFloat(lit, "_DoubleSidedEnable", 1);
                lit.doubleSidedGI = true;
            }
            return lit;
        }

        static void ApplyAlpha(Material lit, string alpha, float cutoff, bool urp, bool hdrp)
        {
            if (alpha == "MASK")
            {
                SetFloat(lit, "_Cutoff", cutoff);
                SetFloat(lit, "_AlphaCutoff", cutoff);
                SetFloat(lit, "_AlphaClip", 1);
                SetFloat(lit, "_AlphaCutoffEnable", 1);
                SetFloat(lit, "_Mode", 1);
                lit.EnableKeyword("_ALPHATEST_ON");
                lit.SetOverrideTag("RenderType", "TransparentCutout");
                lit.renderQueue = (int)RenderQueue.AlphaTest;
            }
            else if (alpha == "BLEND")
            {
                SetFloat(lit, "_Surface", 1);
                SetFloat(lit, "_SurfaceType", 1);
                SetFloat(lit, "_Mode", 2);
                SetFloat(lit, "_Blend", 0);
                SetFloat(lit, "_SrcBlend", (float)BlendMode.SrcAlpha);
                SetFloat(lit, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloat(lit, "_ZWrite", 0);
                lit.EnableKeyword(urp || hdrp ? "_SURFACE_TYPE_TRANSPARENT" : "_ALPHABLEND_ON");
                lit.SetOverrideTag("RenderType", "Transparent");
                lit.renderQueue = (int)RenderQueue.Transparent;
            }
        }

        /// <summary>The Lit shader of the current render pipeline (one UnityGLTF exports).</summary>
        public static Shader NewLitShader()
        {
            var pipeline = SceneLighting.CurrentPipeline();
            Shader shader = null;
            if (pipeline == SceneLighting.Pipeline.Urp) shader = Shader.Find("Universal Render Pipeline/Lit");
            else if (pipeline == SceneLighting.Pipeline.Hdrp) shader = Shader.Find("HDRP/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null && GraphicsSettings.currentRenderPipeline != null)
                shader = GraphicsSettings.currentRenderPipeline.defaultMaterial?.shader;
            return shader;
        }

        // ------------------------------------------------------------------ world-space UVs

        static bool WorldToggles(Material m)
        {
            var shader = m.shader;
            if (shader == null) return false;
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var type = shader.GetPropertyType(i);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range) continue;
                var n = shader.GetPropertyName(i).ToLowerInvariant();
                if (!WorldToggleWords.Any(n.Contains)) continue;
                if (n.Contains("blend") || n.Contains("sharp") || n.Contains("scale") || n.Contains("tiling") || n.Contains("falloff") || n.Contains("contrast")) continue;
                if (m.GetFloat(shader.GetPropertyName(i)) > 0.5f) return true;
            }
            return false;
        }

        /// <summary>Texture repeats per world unit for a world-space material: a tiling/scale
        /// property if it has one ("size" properties are units per tile), else its base map tiling.</summary>
        static (Vector2, Vector2) WorldTiling(Material m, Analysis a)
        {
            var shader = m.shader;
            for (var i = 0; shader != null && i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                var n = name.ToLowerInvariant();
                var type = shader.GetPropertyType(i);
                if (n.Contains("normal") || n.Contains("bump") || n.Contains("height") || n.Contains("detail") || n.Contains("noise")
                    || n.Contains("gloss") || n.Contains("occlusion") || n.Contains("blend") || n.Contains("sharp") || n.Contains("wind")
                    || n.Contains("emission") || n.Contains("parallax"))
                    continue;
                var isSize = n.Contains("size");
                if (!(n.Contains("tiling") || n.Contains("tile") || n.Contains("scale") || isSize)) continue;
                if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
                {
                    var v = m.GetFloat(name);
                    if (v <= 0.0001f) continue;
                    if (isSize) v = 1 / v;
                    return (new Vector2(v, v), a.Offset);
                }
                if (type == ShaderPropertyType.Vector)
                {
                    var v = (Vector2)m.GetVector(name);
                    if (v.x <= 0.0001f || v.y <= 0.0001f) continue;
                    if (isSize) v = new Vector2(1 / v.x, 1 / v.y);
                    return (v, a.Offset);
                }
            }
            return (a.Tiling, a.Offset);
        }

        /// <summary>Gives submesh `sub` of a MeshRenderer box-projected UVs from world position
        /// (each triangle takes the plane facing its normal, like a triplanar shader), on a
        /// copy of the mesh. False for skinned meshes.</summary>
        bool BakeWorldUvs(Renderer renderer, int sub, Analysis a, Matrix4x4 world)
        {
            if (!(renderer is MeshRenderer)) return false;
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return true;
            var mesh = filter.sharedMesh;
            if (sub >= mesh.subMeshCount) return true;
            if (!_ownMeshes.Contains(mesh))
            {
                mesh = Object.Instantiate(mesh);
                mesh.name = filter.sharedMesh.name;
                _temps.Add(mesh);
                _ownMeshes.Add(mesh);
                filter.sharedMesh = mesh;
            }
            if (mesh.GetTopology(sub) != MeshTopology.Triangles) return true;

            var verts = new List<Vector3>(); mesh.GetVertices(verts);
            var normals = new List<Vector3>(); mesh.GetNormals(normals);
            var tangents = new List<Vector4>(); mesh.GetTangents(tangents);
            var colors = new List<Color>(); mesh.GetColors(colors);
            var uvs = new List<Vector2>[4];
            for (var c = 0; c < 4; c++) { uvs[c] = new List<Vector2>(); mesh.GetUVs(c, uvs[c]); }
            if (uvs[0].Count != verts.Count) { uvs[0].Clear(); uvs[0].AddRange(new Vector2[verts.Count]); }

            var subs = new List<int[]>();
            for (var s = 0; s < mesh.subMeshCount; s++) subs.Add(mesh.GetTriangles(s));
            var tris = subs[sub];
            var normalMatrix = world.inverse.transpose;
            for (var t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 p0 = world.MultiplyPoint3x4(verts[tris[t]]), p1 = world.MultiplyPoint3x4(verts[tris[t + 1]]), p2 = world.MultiplyPoint3x4(verts[tris[t + 2]]);
                var face = Vector3.Cross(p1 - p0, p2 - p0);
                if (face.sqrMagnitude < 1e-12f && normals.Count == verts.Count)
                    face = normalMatrix.MultiplyVector(normals[tris[t]]);
                var ax = Mathf.Abs(face.x); var ay = Mathf.Abs(face.y); var az = Mathf.Abs(face.z);
                var axis = ay >= ax && ay >= az ? 1 : ax >= az ? 0 : 2;
                for (var k = 0; k < 3; k++)
                {
                    var old = tris[t + k];
                    var p = k == 0 ? p0 : k == 1 ? p1 : p2;
                    var uv = axis == 1 ? new Vector2(p.x, p.z) : axis == 0 ? new Vector2(p.z, p.y) : new Vector2(p.x, p.y);
                    uv = Vector2.Scale(uv, a.WorldTiling) + a.WorldOffset;
                    // A new vertex per corner: faces meeting at an edge project onto different planes.
                    verts.Add(verts[old]);
                    if (normals.Count > old) normals.Add(normals[old]);
                    if (tangents.Count > old) tangents.Add(tangents[old]);
                    if (colors.Count > old) colors.Add(colors[old]);
                    uvs[0].Add(uv);
                    for (var c = 1; c < 4; c++) if (uvs[c].Count > old) uvs[c].Add(uvs[c][old]);
                    tris[t + k] = verts.Count - 1;
                }
            }

            // Blend shapes are per vertex and do nothing on a MeshRenderer, so they go.
            if (mesh.blendShapeCount > 0) mesh.ClearBlendShapes();
            mesh.indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : mesh.indexFormat;
            mesh.SetVertices(verts);
            if (normals.Count == verts.Count) mesh.SetNormals(normals);
            if (colors.Count == verts.Count) mesh.SetColors(colors);
            for (var c = 0; c < 4; c++) if (uvs[c].Count == verts.Count) mesh.SetUVs(c, uvs[c]);
            for (var s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s, false);
            // Tangents follow the new UVs (normal maps line up with the projected texture).
            if (tangents.Count == verts.Count || FirstTexture(renderer.sharedMaterials[sub], NormalNames) != null) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return true;
        }

        // ------------------------------------------------------------------ property helpers

        static string FirstTexture(Material m, params string[] names)
        {
            if (m == null) return null;
            foreach (var n in names)
                if (m.HasProperty(n) && IsTexture(m, n) && m.GetTexture(n) != null) return n;
            return null;
        }

        /// <summary>A texture property that looks like the colour map, for shaders with unusual names.</summary>
        static string GuessBaseMap(Material m)
        {
            string fallback = null;
            foreach (var n in m.GetTexturePropertyNames())
            {
                if (!m.HasProperty(n) || m.GetTexture(n) == null || !(m.GetTexture(n) is Texture2D)) continue;
                var l = n.ToLowerInvariant();
                if (new[] { "normal", "bump", "mask", "emissi", "detail", "noise", "wind", "height", "occlusion", "metal", "rough", "spec", "gloss", "smooth", "lightmap", "ramp", "lut", "cube", "flow" }.Any(l.Contains))
                    continue;
                if (new[] { "albedo", "diffuse", "base", "main", "color", "colour", "leaf", "foliage" }.Any(l.Contains)) return n;
                if (fallback == null) fallback = n;
            }
            return fallback;
        }

        static bool HasAlpha(Texture t)
        {
            if (t == null) return false;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
            if (importer != null)
                return importer.alphaSource == TextureImporterAlphaSource.FromGrayScale
                       || (importer.alphaSource != TextureImporterAlphaSource.None && importer.DoesSourceTextureHaveAlpha());
            return t is Texture2D t2 && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(t2.graphicsFormat);
        }

        static ShaderPropertyType? Type(Material m, string name)
        {
            var shader = m.shader;
            if (shader == null) return null;
            var i = shader.FindPropertyIndex(name);
            return i < 0 ? (ShaderPropertyType?)null : shader.GetPropertyType(i);
        }

        static bool IsFloat(Material m, string n) { var t = Type(m, n); return t == ShaderPropertyType.Float || t == ShaderPropertyType.Range; }
        static bool IsColor(Material m, string n) => Type(m, n) == ShaderPropertyType.Color;
        static bool IsTexture(Material m, string n) => Type(m, n) == ShaderPropertyType.Texture;
        static float Float(Material m, string n) => m.HasProperty(n) && IsFloat(m, n) ? m.GetFloat(n) : 0f;

        static float FirstFloat(Material m, float fallback, params string[] names)
        {
            foreach (var n in names)
                if (m.HasProperty(n) && IsFloat(m, n)) return m.GetFloat(n);
            return fallback;
        }

        static void SetTex(Material m, string n, Texture t) { if (m.HasProperty(n)) m.SetTexture(n, t); }
        static void SetColor(Material m, string n, Color c) { if (m.HasProperty(n)) m.SetColor(n, c); }
        static void SetFloat(Material m, string n, float v) { if (m.HasProperty(n)) m.SetFloat(n, v); }
    }

    /// <summary>Assets &gt; HyperBox &gt; Material Export: override how the exporter treats the
    /// selected materials (stored as asset labels, so they travel with the project).</summary>
    static class MaterialExportMenu
    {
        const string Root = "Assets/HyperBox/Material Export/";

        [MenuItem(Root + "World-Space Tiled (bake UVs)")] static void WorldUv() => Set(MaterialExport.LabelWorldUv);
        [MenuItem(Root + "Cutout (alpha test)")] static void Cutout() => Set(MaterialExport.LabelCutout);
        [MenuItem(Root + "Transparent")] static void Transparent() => Set(MaterialExport.LabelTransparent);
        [MenuItem(Root + "Opaque")] static void Opaque() => Set(MaterialExport.LabelOpaque);
        [MenuItem(Root + "Two-Sided")] static void TwoSided() => Set(MaterialExport.LabelDoubleSided);
        [MenuItem(Root + "Automatic (clear overrides)")] static void Clear() => Set(null);

        [MenuItem(Root + "World-Space Tiled (bake UVs)", true)]
        [MenuItem(Root + "Cutout (alpha test)", true)]
        [MenuItem(Root + "Transparent", true)]
        [MenuItem(Root + "Opaque", true)]
        [MenuItem(Root + "Two-Sided", true)]
        [MenuItem(Root + "Automatic (clear overrides)", true)]
        static bool Valid() => Selection.GetFiltered<Material>(SelectionMode.Assets).Length > 0;

        static void Set(string label)
        {
            var alpha = new[] { MaterialExport.LabelCutout, MaterialExport.LabelTransparent, MaterialExport.LabelOpaque };
            foreach (var m in Selection.GetFiltered<Material>(SelectionMode.Assets))
            {
                var labels = new List<string>(AssetDatabase.GetLabels(m));
                if (label == null) labels.RemoveAll(l => MaterialExport.Labels.Contains(l));
                else
                {
                    if (alpha.Contains(label)) labels.RemoveAll(l => alpha.Contains(l));
                    if (!labels.Contains(label)) labels.Add(label);
                }
                AssetDatabase.SetLabels(m, labels.ToArray());
            }
        }
    }
}
