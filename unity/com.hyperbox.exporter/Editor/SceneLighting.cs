using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HyperBox.Export
{
    /// <summary>
    /// Reads a scene's world lighting for HyperBox: the skybox (captured as a panorama image),
    /// ambient light, fog, post-processing (URP / HDRP volumes and the Post Processing Stack v2,
    /// read by reflection so the package needs none of them) and whether the scene was lit with
    /// baked lighting. Writes into FilmSpec and adds plain-language lines to `report`.
    /// </summary>
    public static class SceneLighting
    {
        public enum Pipeline { BuiltIn, Urp, Hdrp, Other }

        public static Pipeline CurrentPipeline()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return Pipeline.BuiltIn;
            var name = rp.GetType().Name;
            if (name.Contains("Universal")) return Pipeline.Urp;
            if (name.Contains("HDRenderPipeline")) return Pipeline.Hdrp;
            return Pipeline.Other;
        }

        public static bool GammaColorSpace => QualitySettings.activeColorSpace == ColorSpace.Gamma;

        /// <summary>A Unity light's intensity as HyperBox energy (HyperBox lights in linear space).</summary>
        public static double LightEnergy(double intensity) =>
            GammaColorSpace ? Math.Pow(Math.Max(0, intensity), 2.2) : intensity;

        public static void Collect(FilmSpec spec, IList<GameObject> roots, string skyFolder, string skyRelPath, List<string> report, List<string> warnings)
        {
            var pipeline = CurrentPipeline();
            report.Add($"Render pipeline: {PipelineName(pipeline)}, {(GammaColorSpace ? "Gamma" : "Linear")} colour space.");
            if (GammaColorSpace)
                report.Add("Light intensities were converted from Gamma to Linear space (HyperBox lights in Linear).");

            CollectSky(spec, skyFolder, skyRelPath, report, warnings);
            CollectAmbient(spec, report);
            CollectFog(spec, roots, report);
            CollectPostProcessing(spec, roots, pipeline, report);
            CollectBakedLighting(spec, roots, report);
        }

        static string PipelineName(Pipeline p) =>
            p == Pipeline.Urp ? "URP" : p == Pipeline.Hdrp ? "HDRP" : p == Pipeline.BuiltIn ? "Built-in" : "custom";

        // ------------------------------------------------------------------ sky

        static void CollectSky(FilmSpec spec, string folder, string relPath, List<string> report, List<string> warnings)
        {
            var skybox = RenderSettings.skybox;
            if (skybox == null)
            {
                report.Add("Sky: the scene has no skybox, so HyperBox picks a preset sky from the sun's height.");
                return;
            }
            try
            {
                var path = Path.Combine(folder, Path.GetFileName(relPath));
                if (CaptureSky(path))
                {
                    spec.SkyTexture = relPath;
                    report.Add($"Sky: captured the skybox (\"{skybox.shader.name}\") as a panorama image.");
                    return;
                }
                warnings.Add("The skybox rendered as plain black (this pipeline may not support capturing it), so HyperBox uses a preset sky instead.");
            }
            catch (Exception e)
            {
                warnings.Add("The skybox couldn't be captured (" + e.Message + "), so HyperBox uses a preset sky instead.");
                Debug.LogException(e);
            }
        }

        /// <summary>Renders the skybox in six directions and writes a 2048×1024 panorama PNG. False if
        /// nothing was drawn.</summary>
        public static bool CaptureSky(string pngPath, int faceSize = 768, int width = 2048)
        {
            var go = new GameObject("HyperBoxSkyCapture") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(faceSize, faceSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var read = new Texture2D(faceSize, faceSize, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 0;  // nothing but the sky
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.fieldOfView = 90;
                cam.aspect = 1;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 1000;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.targetTexture = rt;

                var faces = new List<SkyFace>();
                var lit = false;
                foreach (var view in SkyResample.Views)
                {
                    var fwd = new Vector3((float)view[0][0], (float)view[0][1], (float)view[0][2]);
                    var up = new Vector3((float)view[1][0], (float)view[1][1], (float)view[1][2]);
                    go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(fwd, up));
                    Render(cam, rt);
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, faceSize, faceSize), 0, 0);
                    read.Apply();
                    var px = read.GetPixels32();
                    var rgb = new float[faceSize * faceSize * 3];
                    for (var i = 0; i < px.Length; i++)
                    {
                        rgb[i * 3] = px[i].r / 255f;
                        rgb[i * 3 + 1] = px[i].g / 255f;
                        rgb[i * 3 + 2] = px[i].b / 255f;
                        if (!lit && (px[i].r > 2 || px[i].g > 2 || px[i].b > 2)) lit = true;
                    }
                    var t = go.transform;
                    faces.Add(new SkyFace
                    {
                        Forward = V(t.forward), Right = V(t.right), Up = V(t.up),
                        TanHalfFov = Math.Tan(cam.fieldOfView * Math.PI / 360), Size = faceSize, Rgb = rgb,
                    });
                }
                if (!lit) return false;

                var height = width / 2;
                var bytes = SkyResample.ToEquirect(faces, width, height);
                var pano = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    pano.LoadRawTextureData(bytes);
                    pano.Apply();
                    Directory.CreateDirectory(Path.GetDirectoryName(pngPath) ?? ".");
                    File.WriteAllBytes(pngPath, pano.EncodeToPNG());
                }
                finally
                {
                    Object.DestroyImmediate(pano);
                }
                return true;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(read);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        /// <summary>Renders one camera into `rt`: through the render pipeline's request API where
        /// there is one (Unity 2023.1+, looked up by reflection), otherwise Camera.Render.</summary>
        static void Render(Camera cam, RenderTexture rt)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                try
                {
                    var requestType = typeof(RenderPipeline).GetNestedType("StandardRequest");
                    if (requestType != null)
                    {
                        var request = Activator.CreateInstance(requestType);
                        requestType.GetField("destination")?.SetValue(request, rt);
                        var supports = typeof(RenderPipeline).GetMethod("SupportsRenderRequest", BindingFlags.Public | BindingFlags.Static);
                        var submit = typeof(RenderPipeline).GetMethod("SubmitRenderRequest", BindingFlags.Public | BindingFlags.Static);
                        if (supports != null && submit != null)
                        {
                            var ok = (bool)supports.MakeGenericMethod(requestType).Invoke(null, new[] { cam, request });
                            if (ok)
                            {
                                submit.MakeGenericMethod(requestType).Invoke(null, new[] { cam, request });
                                return;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[HyperBox] Render request failed, using Camera.Render: " + e.Message);
                }
            }
            cam.Render();
        }

        static double[] V(Vector3 v) => new double[] { v.x, v.y, v.z };

        // ------------------------------------------------------------------ ambient

        static void CollectAmbient(FilmSpec spec, List<string> report)
        {
            spec.AmbientIntensity = RenderSettings.ambientIntensity;
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Flat:
                    SetAmbientColor(spec, RenderSettings.ambientLight);
                    report.Add($"Ambient light: flat colour {Coords.Hex(spec.AmbientColor[0], spec.AmbientColor[1], spec.AmbientColor[2])}.");
                    break;
                case AmbientMode.Trilight:
                    // HyperBox has one ambient colour: weight the gradient like a room's average.
                    var c = RenderSettings.ambientSkyColor * 0.35f + RenderSettings.ambientEquatorColor * 0.45f + RenderSettings.ambientGroundColor * 0.2f;
                    SetAmbientColor(spec, c);
                    report.Add("Ambient light: gradient (sky / equator / ground), approximated by one colour.");
                    break;
                default:
                    spec.AmbientIntensity = RenderSettings.ambientIntensity;
                    report.Add($"Ambient light: from the skybox, ×{RenderSettings.ambientIntensity:0.##}.");
                    break;
            }
        }

        /// <summary>HDR colours keep their hue in the colour and their strength in ambient_energy.</summary>
        static void SetAmbientColor(FilmSpec spec, Color c)
        {
            var max = Math.Max(c.r, Math.Max(c.g, c.b));
            var scale = max > 1 ? max : 1f;
            spec.AmbientColor = new double[] { c.r / scale, c.g / scale, c.b / scale };
            spec.AmbientIntensity = scale;
        }

        // ------------------------------------------------------------------ fog

        static void CollectFog(FilmSpec spec, IList<GameObject> roots, List<string> report)
        {
            if (RenderSettings.fog)
            {
                var fc = RenderSettings.fogColor;
                spec.FogColor = new double[] { fc.r, fc.g, fc.b };
                spec.FogSkyAffect = 0;
                switch (RenderSettings.fogMode)
                {
                    case FogMode.Linear:
                        spec.FogStart = RenderSettings.fogStartDistance;
                        spec.FogEnd = Math.Max(RenderSettings.fogStartDistance + 0.1, RenderSettings.fogEndDistance);
                        report.Add($"Fog: linear, {spec.FogStart:0.#}–{spec.FogEnd:0.#} m (HyperBox's fog is exponential, so it thickens a little more gradually).");
                        break;
                    case FogMode.ExponentialSquared:
                        spec.FogDensity = RenderSettings.fogDensity * 1.2;
                        report.Add($"Fog: exponential squared, density {RenderSettings.fogDensity:0.####} (approximated as exponential).");
                        break;
                    default:
                        spec.FogDensity = RenderSettings.fogDensity;
                        report.Add($"Fog: exponential, density {RenderSettings.fogDensity:0.####}.");
                        break;
                }
                return;
            }
            var fogComponents = AllComponents(roots)
                .Select(c => c.GetType().Name)
                .Where(n => n.IndexOf("fog", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct().ToList();
            report.Add(fogComponents.Count > 0
                ? $"Fog: Unity's fog (Lighting window) is off; the scene's fog comes from {string.Join(", ", fogComponents)}, which can't be carried over yet."
                : "Fog: off.");
        }

        // ------------------------------------------------------------------ post-processing

        class Volume
        {
            public float Priority;
            public float Weight;
            public List<object> Components = new List<object>();
        }

        static void CollectPostProcessing(FilmSpec spec, IList<GameObject> roots, Pipeline pipeline, List<string> report)
        {
            // Global volumes, lowest priority first, so later ones override.
            var volumes = new List<Volume>();
            foreach (var mb in AllComponents(roots))
            {
                var typeName = mb.GetType().FullName;
                if (typeName == "UnityEngine.Rendering.Volume" || typeName == "UnityEngine.Rendering.PostProcessing.PostProcessVolume")
                {
                    if (mb is Behaviour b && !b.enabled) continue;
                    if (!(Get(mb, "isGlobal") is bool global) || !global) continue;
                    var weight = Convert.ToSingle(Get(mb, "weight") ?? 1f);
                    if (weight <= 0) continue;
                    var profile = Get(mb, "sharedProfile") ?? Get(mb, "profile");
                    var list = (Get(profile, "components") ?? Get(profile, "settings")) as System.Collections.IEnumerable;
                    if (list == null) continue;
                    var v = new Volume { Priority = Convert.ToSingle(Get(mb, "priority") ?? 0f), Weight = weight };
                    foreach (var comp in list)
                        if (comp != null && Active(comp)) v.Components.Add(comp);
                    volumes.Add(v);
                }
            }
            volumes.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            // Unity's defaults when nothing overrides them: HDRP's default profile uses ACES,
            // URP and Built-in don't tonemap at all.
            string tonemap = pipeline == Pipeline.Hdrp ? "aces" : "linear";
            double exposureEv = 0;
            double? contrast = null, saturation = null, temperature = null, tint = null, vignette = null, grain = null, bloom = null;

            foreach (var v in volumes)
            {
                foreach (var comp in v.Components)
                {
                    switch (comp.GetType().Name)
                    {
                        case "Tonemapping":
                            if (Param(comp, "mode", out var mode)) tonemap = ToneName(mode);
                            break;
                        case "ColorGrading":  // Post Processing Stack v2
                            if (Param(comp, "tonemapper", out var tm)) tonemap = ToneName(tm);
                            ReadColorAdjust(comp, ref exposureEv, ref contrast, ref saturation);
                            if (Param(comp, "temperature", out var t2)) temperature = Num(t2) / 100;
                            if (Param(comp, "tint", out var ti2)) tint = Num(ti2) / 100;
                            break;
                        case "ColorAdjustments":
                            ReadColorAdjust(comp, ref exposureEv, ref contrast, ref saturation);
                            break;
                        case "WhiteBalance":
                            if (Param(comp, "temperature", out var t)) temperature = Num(t) / 100;
                            if (Param(comp, "tint", out var ti)) tint = Num(ti) / 100;
                            break;
                        case "Bloom":
                            if (Param(comp, "intensity", out var bi))
                                bloom = comp.GetType().Namespace == "UnityEngine.Rendering.PostProcessing" ? Num(bi) / 3 : Num(bi);
                            break;
                        case "Vignette":
                            if (Param(comp, "intensity", out var vi)) vignette = Num(vi);
                            break;
                        case "FilmGrain":
                        case "Grain":
                            if (Param(comp, "intensity", out var gi)) grain = Num(gi);
                            break;
                    }
                }
            }

            spec.Tonemap = tonemap;
            spec.Exposure = Math.Pow(2, exposureEv);
            spec.Glow = bloom.HasValue && bloom.Value > 0.01;
            var grade = new JObj();
            if (contrast.HasValue) grade["contrast"] = Coords.R(Clamp(1 + contrast.Value / 100, 0.5, 2));
            if (saturation.HasValue) grade["saturation"] = Coords.R(Clamp(1 + saturation.Value / 100, 0, 2));
            if (temperature.HasValue) grade["temperature"] = Coords.R(Clamp(temperature.Value, -1, 1));
            if (tint.HasValue) grade["tint"] = Coords.R(Clamp(tint.Value, -1, 1));
            if (vignette.HasValue) grade["vignette"] = Coords.R(Clamp(vignette.Value, 0, 1));
            if (grain.HasValue) grade["grain"] = Coords.R(Clamp(grain.Value, 0, 1));
            if (bloom.HasValue && bloom.Value > 0.01) grade["bloom"] = Coords.R(Clamp(bloom.Value, 0, 3));
            spec.Grade = grade;

            var parts = new List<string> { tonemap == "linear" ? "no tonemapping" : "ACES-style tonemapping" };
            if (Math.Abs(exposureEv) > 0.01) parts.Add($"exposure {exposureEv:+0.##;-0.##} EV");
            if (bloom.HasValue) parts.Add($"bloom {bloom.Value:0.##}");
            foreach (var kv in grade)
                if (kv.Key != "bloom") parts.Add($"{kv.Key} {kv.Value}");
            report.Add(volumes.Count == 0
                ? $"Post-processing: none found, so like Unity there's {parts[0]}."
                : "Post-processing: " + string.Join(", ", parts) + ".");
        }

        static void ReadColorAdjust(object comp, ref double exposureEv, ref double? contrast, ref double? saturation)
        {
            if (Param(comp, "postExposure", out var e)) exposureEv = Num(e);
            if (Param(comp, "contrast", out var c)) contrast = Num(c);
            if (Param(comp, "saturation", out var s)) saturation = Num(s);
        }

        static string ToneName(object mode)
        {
            var name = mode?.ToString() ?? "None";
            return name == "None" ? "linear" : "aces";
        }

        /// <summary>A volume component's switch: SRP's "active", PPv2's "enabled" parameter.</summary>
        static bool Active(object comp)
        {
            var active = Get(comp, "active");
            if (active is bool a && !a) return false;
            var enabled = Get(comp, "enabled");
            if (enabled != null && !(enabled is bool))
            {
                var v = Get(enabled, "value");
                if (v is bool e && !e) return false;
            }
            return true;
        }

        /// <summary>An overridden volume parameter's value (SRP VolumeParameter or PPv2 ParameterOverride).</summary>
        static bool Param(object comp, string name, out object value)
        {
            value = null;
            var p = Get(comp, name);
            if (p == null) return false;
            if (!(Get(p, "overrideState") is bool overridden) || !overridden) return false;
            value = Get(p, "value");
            return value != null;
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var f = t.GetField(name, flags);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, flags);
            if (p != null && p.GetIndexParameters().Length == 0)
            {
                try { return p.GetValue(o); }
                catch { return null; }
            }
            return null;
        }

        static double Num(object v)
        {
            try { return Convert.ToDouble(v); }
            catch { return 0; }
        }

        static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        // ------------------------------------------------------------------ baked lighting

        static void CollectBakedLighting(FilmSpec spec, IList<GameObject> roots, List<string> report)
        {
            var lightmaps = LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0;
            var bakedLights = roots.SelectMany(r => r.GetComponentsInChildren<Light>(false))
                .Count(l => l.enabled && l.lightmapBakeType != LightmapBakeType.Realtime);
            if (lightmaps || bakedLights > 0)
            {
                spec.GlobalIllumination = true;
                report.Add("Baked lighting: " + (lightmaps ? "the scene has lightmaps" : $"{bakedLights} light(s) are Baked or Mixed") +
                           ", so bounce light (global illumination) is on in HyperBox.");
            }
        }

        static IEnumerable<MonoBehaviour> AllComponents(IList<GameObject> roots) =>
            roots.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(false)).Where(c => c != null);
    }
}
