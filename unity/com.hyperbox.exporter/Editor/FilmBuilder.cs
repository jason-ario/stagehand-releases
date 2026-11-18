using System;
using System.Collections.Generic;

namespace HyperBox.Export
{
    /// <summary>A transform in Unity world space (left-handed, +Z forward).</summary>
    public class UnityXform
    {
        public double px, py, pz;
        public Quat rot = Quat.Identity;
        public double sx = 1, sy = 1, sz = 1;
    }

    public class ClipSpec
    {
        public string Name;      // as Godot will name it (Coords.GodotAnimationName)
        public double Length;
        public bool Loop;
    }

    /// <summary>One exported model placed in the film.</summary>
    public class ModelSpec
    {
        public string Name;              // display name (hierarchy path)
        public string Path;              // GLB path relative to the film
        /// <summary>Unity world transform of the object. Null = the GLB already holds world
        /// placement (merged scenery), so the actor sits at the origin.</summary>
        public UnityXform Xform;
        public bool Character;           // rigged (skinned) model
        public bool Humanoid;            // Unity Humanoid avatar
        /// <summary>Unity HumanBodyBones name → bone (transform) name in the model.</summary>
        public Dictionary<string, string> BoneMap = new Dictionary<string, string>();
        public List<ClipSpec> Clips = new List<ClipSpec>();
        public string DefaultClip;       // clip the character plays from the start (null = rest pose)
    }

    public class CameraSpec
    {
        public string Name;
        public UnityXform Xform;
        public double Fov = 50;          // vertical, degrees (Unity's Camera.fieldOfView)
        public bool Main;
        public double[] LookAt;          // optional Unity-space point to aim at (fallback cameras)
    }

    public class LightSpec
    {
        public string Name;
        public UnityXform Xform;
        public string Type = "point";    // point, spot, directional
        public double R = 1, G = 1, B = 1;
        public double Intensity = 1;
        public double Range = 10;
        public double SpotAngle = 30;    // Unity's full cone angle, degrees
        public bool Shadows;
    }

    public class FilmSpec
    {
        public string Title = "Unity scene";
        public string Notes = "";
        public int Fps = 30;
        public int Width = 1920, Height = 1080;
        public double Duration = 5;
        public double AmbientIntensity = 1;
        public double[] AmbientColor;    // flat ambient colour (rgb 0..1); null = ambient from the sky
        public string SkyTexture;        // panorama of the skybox, relative to the film; null = preset sky
        public double SkyEnergy = 1;
        public double FogDensity;        // exponential fog; 0 = none
        public double FogStart, FogEnd;  // Unity's linear fog (FogEnd > 0): converted to a density
        public double[] FogColor;        // rgb 0..1
        public double FogSkyAffect;      // Unity's fog leaves the skybox alone
        public bool GlobalIllumination;  // the scene had baked lighting: bounce light in HyperBox
        public string Tonemap;           // "aces" | "linear"; null = HyperBox's default
        public double Exposure = 1;
        public bool? Glow;
        public JObj Grade;               // film-look grade from post-processing (HyperBox grade fields)
        public List<ModelSpec> Models = new List<ModelSpec>();
        public List<CameraSpec> Cameras = new List<CameraSpec>();
        public List<LightSpec> Lights = new List<LightSpec>();
    }

    /// <summary>Turns what was collected from a Unity scene into a HyperBox film document
    /// (schema/film.schema.json, format film-studio/0.1). Pure C#: no UnityEngine.</summary>
    public static class FilmBuilder
    {
        public static JObj Build(FilmSpec spec, List<string> warnings)
        {
            var used = new HashSet<string>();
            var actors = new List<object>();
            var cameras = new List<object>();
            var tracks = new List<object>();
            var sets = new List<object>();
            var setPaths = new HashSet<string>();

            foreach (var m in spec.Models)
            {
                var id = Coords.Id(LastPart(m.Name), used, m.Character ? "character" : "prop");
                var actor = new JObj
                {
                    { "id", id },
                    { "name", m.Name },
                    { "kind", m.Character ? "character" : "prop" },
                    { "transform", m.Xform == null ? new JObj { { "position", new[] { 0.0, 0, 0 } } } : Transform(m.Xform, true) },
                    { "asset", new JObj { { "type", "gltf" }, { "path", m.Path }, { "name", m.Name } } },
                };
                if (m.Humanoid && m.BoneMap.Count > 0)
                {
                    var map = GlbPatch.HyperBoxBoneMap(m.BoneMap);
                    if (map.Count > 0) actor["rig"] = new JObj { { "bone_map", map } };
                }
                actors.Add(actor);

                if (!string.IsNullOrEmpty(m.DefaultClip))
                {
                    var key = new JObj { { "t", 0.0 }, { "v", m.DefaultClip }, { "blend", 0.0 } };
                    var clip = m.Clips.Find(c => c.Name == m.DefaultClip);
                    if (clip != null && clip.Loop) key["loop"] = true;
                    tracks.Add(new JObj { { "target", id }, { "property", "animation" }, { "keys", new List<object> { key } } });
                }
                // A Humanoid character's clips also work on every other character in HyperBox.
                if (m.Humanoid && m.Clips.Count > 0 && setPaths.Add(m.Path))
                {
                    var clips = new List<object>();
                    foreach (var c in m.Clips)
                        clips.Add(new JObj { { "name", c.Name }, { "length", c.Length }, { "loop", c.Loop } });
                    sets.Add(new JObj
                    {
                        { "id", Coords.Id(id + "_clips", used) },
                        { "name", LastPart(m.Name) + " clips (from Unity)" },
                        { "path", m.Path },
                        { "clips", clips },
                    });
                }
            }

            // Lights: the first directional light becomes the sun.
            LightSpec sun = null;
            foreach (var l in spec.Lights)
            {
                if (l.Type == "directional")
                {
                    if (sun == null) sun = l;
                    else warnings.Add($"Light \"{l.Name}\": HyperBox has one sun, so only \"{sun.Name}\" was kept as the sun.");
                    continue;
                }
                var light = new JObj
                {
                    { "type", l.Type == "spot" ? "spot" : "omni" },
                    { "color", Coords.Hex(l.R, l.G, l.B) },
                    { "energy", Coords.R(Math.Max(0, l.Intensity)) },
                    { "range", Coords.R(Math.Max(0.01, l.Range)) },
                    { "shadows", l.Shadows },
                };
                if (l.Type == "spot")
                    light["angle_deg"] = Coords.R(Math.Min(89, Math.Max(1, l.SpotAngle * 0.5)));
                actors.Add(new JObj
                {
                    { "id", Coords.Id(LastPart(l.Name), used, "light") },
                    { "name", l.Name },
                    { "kind", "light" },
                    { "transform", Transform(l.Xform) },
                    { "light", light },
                });
            }

            // Cameras: the main one (or the first) frames the opening shot.
            string mainId = null, mainName = null;
            foreach (var c in spec.Cameras)
            {
                var id = Coords.Id(LastPart(c.Name), used, "camera");
                var cam = new JObj
                {
                    { "id", id },
                    { "name", c.Name },
                    { "fov", Coords.R(Math.Max(1, Math.Min(170, c.Fov))) },
                    { "transform", Transform(c.Xform) },
                };
                if (c.LookAt != null)
                    cam["look_at"] = Coords.Position(c.LookAt[0], c.LookAt[1], c.LookAt[2]);
                cameras.Add(cam);
                if (mainId == null || c.Main) { mainId = id; mainName = c.Name; }
            }
            if (mainId == null)
            {
                mainId = Coords.Id("camera_main", used);
                mainName = "Main camera";
                cameras.Add(new JObj
                {
                    { "id", mainId }, { "name", mainName }, { "fov", 50.0 },
                    { "transform", new JObj { { "position", new[] { 0.0, 1.6, 6 } } } },
                    { "look_at", new[] { 0.0, 1, 0 } },
                });
                warnings.Add("No camera found, so a default camera was added.");
            }

            var env = new JObj();
            if (sun != null)
            {
                var f = Forward(sun.Xform.rot);
                var angles = Coords.SunAngles(f[0], f[1], f[2]);
                env["sky"] = angles[0] < -2 ? "night" : angles[0] < 12 ? "sunset" : "day";
                env["sun"] = new JObj
                {
                    { "elevation_deg", Math.Max(angles[0], -10) },
                    { "azimuth_deg", angles[1] },
                    { "energy", Coords.R(Math.Max(0, sun.Intensity)) },
                    { "color", Coords.Hex(sun.R, sun.G, sun.B) },
                };
            }
            else
            {
                env["sky"] = "day";
                env["sun"] = new JObj { { "elevation_deg", 45.0 }, { "azimuth_deg", 30.0 }, { "energy", 0.0 } };
            }
            if (!string.IsNullOrEmpty(spec.SkyTexture))
            {
                env["sky_texture"] = spec.SkyTexture;
                if (Math.Abs(spec.SkyEnergy - 1) > 1e-4) env["sky_energy"] = Coords.R(Math.Max(0, spec.SkyEnergy));
            }
            env["ambient_energy"] = Coords.R(Math.Max(0, spec.AmbientIntensity));
            if (spec.AmbientColor != null)
                env["ambient_color"] = Coords.Hex(spec.AmbientColor[0], spec.AmbientColor[1], spec.AmbientColor[2]);
            var density = spec.FogEnd > spec.FogStart && spec.FogEnd > 0 ? LinearFogDensity(spec.FogStart, spec.FogEnd) : spec.FogDensity;
            if (density > 0)
            {
                env["fog_density"] = Coords.R(density);
                if (spec.FogColor != null) env["fog_color"] = Coords.Hex(spec.FogColor[0], spec.FogColor[1], spec.FogColor[2]);
                env["fog_sky_affect"] = Coords.R(Math.Max(0, Math.Min(1, spec.FogSkyAffect)));
            }
            if (spec.GlobalIllumination) env["gi"] = true;
            if (!string.IsNullOrEmpty(spec.Tonemap)) env["tonemap"] = spec.Tonemap;
            if (Math.Abs(spec.Exposure - 1) > 1e-4) env["exposure"] = Coords.R(Math.Max(0, spec.Exposure));
            if (spec.Glow.HasValue) env["glow"] = spec.Glow.Value;
            if (spec.Grade != null && spec.Grade.Count > 0) env["grade"] = spec.Grade;

            var duration = Math.Max(0.1, spec.Duration);
            var doc = new JObj
            {
                { "format", "film-studio/0.1" },
                { "meta", new JObj
                    {
                        { "title", spec.Title },
                        { "fps", spec.Fps },
                        { "resolution", new[] { spec.Width, spec.Height } },
                        { "notes", spec.Notes },
                    }
                },
                { "environment", env },
                { "actors", actors },
                { "cameras", cameras },
                { "tracks", tracks },
            };
            if (sets.Count > 0) doc["animation_sets"] = sets;
            doc["shots"] = new List<object>
            {
                new JObj { { "id", "shot_1" }, { "name", mainName }, { "camera", mainId }, { "start", 0.0 }, { "end", Coords.R(duration) } },
            };
            return doc;
        }

        /// <summary>Document transform: models keep facing +Z; cameras and lights are turned to point down -Z.</summary>
        public static JObj Transform(UnityXform x, bool model = false)
        {
            var t = new JObj
            {
                { "position", Coords.Position(x.px, x.py, x.pz) },
                { "rotation_deg", Coords.EulerYXZDegrees(model ? Coords.ModelRotation(x.rot) : Coords.FacingRotation(x.rot)) },
            };
            if (Math.Abs(x.sx - 1) > 1e-4 || Math.Abs(x.sy - 1) > 1e-4 || Math.Abs(x.sz - 1) > 1e-4)
                t["scale"] = new[] { Coords.R(x.sx), Coords.R(x.sy), Coords.R(x.sz) };
            return t;
        }

        /// <summary>HyperBox's fog is exponential (Godot's depth fog doesn't follow a start/end
        /// ramp), so Unity's linear fog becomes the density that is 75% fogged where Unity's is.</summary>
        public static double LinearFogDensity(double start, double end)
        {
            var d75 = Math.Max(0.5, start + 0.75 * (end - start));
            return Math.Log(4) / d75;
        }

        /// <summary>Unity forward (+Z) of a rotation.</summary>
        public static double[] Forward(Quat q)
        {
            q = q.Normalized();
            return new[] { 2 * (q.x * q.z + q.w * q.y), 2 * (q.y * q.z - q.w * q.x), 1 - 2 * (q.x * q.x + q.y * q.y) };
        }

        static string LastPart(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var i = path.LastIndexOf('/');
            return i >= 0 ? path.Substring(i + 1) : path;
        }
    }
}
