using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
#if HYPERBOX_UNITYGLTF
using UnityGLTF;
#endif

namespace HyperBox.Export
{
    public class ExportOptions
    {
        public string OutputFolder;
        public string FilmName = "Unity Scene";
        /// <summary>Static, unanimated objects go into one "Scenery" model (much faster to load).</summary>
        public bool MergeStatic = true;
        public bool IncludeAnimations = true;
        public double ShotLength = 5;
        public int Fps = 30;
        public int Width = 1920, Height = 1080;
        /// <summary>Skybox, ambient light, fog, post-processing and baked-lighting hints.</summary>
        public bool IncludeLighting = true;
        public bool IncludeTerrain = true;
        public int TerrainGrid = 513;        // vertices per side of a terrain's mesh
        public int TerrainTexture = 2048;    // baked terrain colour texture size
        public int MaxTrees = 3000;          // per terrain
        /// <summary>Leave out geometry Unity doesn't draw: deactivated objects, disabled or
        /// shadows-only renderers, EditorOnly objects, layers no camera in the scene renders.</summary>
        public bool SkipHidden = true;
        /// <summary>Also leave out objects hidden with the Scene view's eye icon.</summary>
        public bool SkipSceneViewHidden = false;
    }

    public class ExportResult
    {
        public string FilmPath;
        public int Actors;
        public int Files;
        public List<string> Warnings = new List<string>();
        public List<string> Written = new List<string>();
        /// <summary>What was found about the scene's lighting and how it was carried over.</summary>
        public List<string> Report = new List<string>();
    }

    /// <summary>
    /// Exports Unity content for HyperBox:
    ///   - ExportFilm: a scene (or selection) → a ready-to-open .film.json plus one GLB per model
    ///   - ExportModels: objects → standalone GLB files to drag into HyperBox
    ///   - ExportClipPack: animation clips baked onto a Humanoid character → an animation set
    /// Models are written with UnityGLTF, which bakes Humanoid clips onto the skeleton.
    /// </summary>
    public static class HyperBoxExporter
    {
        public const string UnityGltfGitUrl = "https://github.com/KhronosGroup/UnityGLTF.git";

        public static bool UnityGltfInstalled
        {
            get
            {
#if HYPERBOX_UNITYGLTF
                return true;
#else
                return false;
#endif
            }
        }

        // ------------------------------------------------------------------ film

        class Unit
        {
            public string Path;
            public GameObject Go;
        }

        class Collected
        {
            public readonly List<Unit> Units = new List<Unit>();
            public readonly List<Unit> Scenery = new List<Unit>();
            public readonly List<(string path, Camera cam)> Cameras = new List<(string, Camera)>();
            public readonly List<(string path, Light light)> Lights = new List<(string, Light)>();
            public readonly List<(string path, Terrain terrain)> Terrains = new List<(string, Terrain)>();
            public readonly HashSet<string> Skipped = new HashSet<string>();
        }

        public static ExportResult ExportFilm(IList<GameObject> roots, ExportOptions o, string title)
        {
            RequireUnityGltf();
            BeginHiddenRules(o.SkipHidden, o.SkipSceneViewHidden);
            var result = new ExportResult();
            var warnings = result.Warnings;
            Directory.CreateDirectory(o.OutputFolder);
            var modelsDirName = Coords.FileName(o.FilmName, new HashSet<string>()) + "_models";
            var modelsDir = Path.Combine(o.OutputFolder, modelsDirName);

            var c = new Collected();
            foreach (var r in roots)
                Walk(r, "", c, o);

            var spec = new FilmSpec
            {
                Title = title,
                Fps = o.Fps,
                Width = o.Width,
                Height = o.Height,
                Duration = o.ShotLength,
                Notes = $"Exported from the Unity scene \"{title}\" with the HyperBox Exporter. Materials are standard PBR " +
                        "(custom shaders fall back to their textures). Lightmaps themselves don't come over (bounce light is approximated).",
            };

            var files = new HashSet<string>();
            var exported = new Dictionary<string, string>();   // dedupe key -> relative GLB path
            _refs.Clear();
            var total = c.Units.Count + (c.Scenery.Count > 0 ? 1 : 0) + c.Terrains.Count;
            var done = 0;
            try
            {
                if (c.Scenery.Count > 0)
                {
                    EditorUtility.DisplayProgressBar("Export to HyperBox", "Scenery", 0);
                    var name = Coords.FileName("Scenery", files);
                    try
                    {
                        ExportGroup(c.Scenery.Select(u => u.Go).ToList(), modelsDir, name);
                        var rel = modelsDirName + "/" + name + ".glb";
                        result.Written.Add(rel);
                        spec.Models.Add(new ModelSpec { Name = "Scenery", Path = rel, Xform = null });
                    }
                    catch (Exception e)
                    {
                        warnings.Add("Scenery could not be exported: " + e.Message);
                        Debug.LogException(e);
                    }
                    done++;
                }

                foreach (var (path, terrain) in c.Terrains)
                {
                    done++;
                    ExportTerrain(path, terrain, o, modelsDir, modelsDirName, files, spec, result);
                }

                foreach (var u in c.Units)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Export to HyperBox", u.Path, done / (float)Math.Max(1, total)))
                    {
                        warnings.Add("Export cancelled; the film has what was exported so far.");
                        break;
                    }
                    done++;
                    var key = DedupeKey(u.Go);
                    if (!exported.TryGetValue(key, out var rel))
                    {
                        var name = Coords.FileName(u.Go.name, files);
                        try
                        {
                            ExportUnit(u.Go, modelsDir, name, o.IncludeAnimations, null);
                        }
                        catch (Exception e)
                        {
                            warnings.Add($"\"{u.Path}\" could not be exported: {e.Message}");
                            Debug.LogException(e);
                            continue;
                        }
                        rel = modelsDirName + "/" + name + ".glb";
                        exported[key] = rel;
                        result.Written.Add(rel);
                    }
                    spec.Models.Add(DescribeModel(u, rel, o.IncludeAnimations));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _refs.Clear();
            }

            CollectCameras(c, spec);
            CollectLights(c, spec, warnings);
            if (o.IncludeLighting)
            {
                // The whole scene's lighting, even when only a selection is exported.
                var sceneRoots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
                SceneLighting.Collect(spec, sceneRoots, modelsDir, modelsDirName + "/sky.png", result.Report, warnings);
                if (spec.SkyTexture != null) result.Written.Add(spec.SkyTexture);
                spec.Notes += " Lighting from Unity: " + string.Join(" ", result.Report);
            }
            else
            {
                CollectEnvironment(spec, warnings);
            }
            foreach (var s in c.Skipped) warnings.Add(s);
            ReportHidden(result);

            var doc = FilmBuilder.Build(spec, warnings);
            result.FilmPath = Path.Combine(o.OutputFolder, Coords.FileName(o.FilmName, new HashSet<string>()) + ".film.json");
            File.WriteAllText(result.FilmPath, FilmJson.Write(doc));
            result.Actors = spec.Models.Count + spec.Lights.Count;
            result.Files = result.Written.Count;
            return result;
        }

        /// <summary>Finds what to export. A "unit" (one model) is an object with an Animator, a
        /// prefab instance, or an object with its own mesh; empty organizer objects are looked into.</summary>
        static void Walk(GameObject g, string parentPath, Collected c, ExportOptions o)
        {
            if (!g.activeInHierarchy || (g.hideFlags & HideFlags.HideInHierarchy) != 0 || g.CompareTag("EditorOnly") || SceneViewHidden(g))
                return;
            var path = parentPath.Length == 0 ? g.name : parentPath + "/" + g.name;

            var terrain = g.GetComponent<Terrain>();
            if (terrain != null && terrain.enabled && terrain.terrainData != null)
            {
                if (o.IncludeTerrain) c.Terrains.Add((path, terrain));
                else c.Skipped.Add($"Terrain \"{path}\" was left out (Include terrain is off).");
            }

            if (IsUnit(g))
            {
                // Lights and cameras inside a model become their own actors (models are exported without them).
                foreach (var l in g.GetComponentsInChildren<Light>(false))
                    if (l.enabled) c.Lights.Add((path + RelPath(g.transform, l.transform), l));
                foreach (var cam in g.GetComponentsInChildren<Camera>(false))
                    if (cam.enabled) c.Cameras.Add((path + RelPath(g.transform, cam.transform), cam));
                var unit = new Unit { Path = path, Go = g };
                if (o.MergeStatic && IsStaticScenery(g)) c.Scenery.Add(unit);
                else c.Units.Add(unit);
                return;
            }

            var camera = g.GetComponent<Camera>();
            if (camera != null && camera.enabled) c.Cameras.Add((path, camera));
            var light = g.GetComponent<Light>();
            if (light != null && light.enabled) c.Lights.Add((path, light));
            foreach (Transform child in g.transform)
                Walk(child.gameObject, path, c, o);
        }

        static string RelPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
            return parts.Count == 0 ? "" : "/" + string.Join("/", parts);
        }

        // ------------------------------------------------------------------ hidden geometry

        // Rules for the export in progress (set by each public entry point).
        static bool _skipHidden = true;
        static bool _skipSceneViewHidden;
        static int _cameraMask = -1;
        static int _hiddenRemoved;

        static void BeginHiddenRules(bool skipHidden, bool skipSceneViewHidden)
        {
            _skipHidden = skipHidden;
            _skipSceneViewHidden = skipSceneViewHidden;
            // Layers any enabled camera in the scene draws (camera stacks can draw extra layers).
            var cameras = Camera.allCameras.Where(c => c != null && c.gameObject.scene.IsValid()).ToList();
            _cameraMask = cameras.Count == 0 ? -1 : cameras.Aggregate(0, (m, c) => m | c.cullingMask);
            _hiddenRemoved = 0;
        }

        static void ReportHidden(ExportResult result)
        {
            if (_hiddenRemoved > 0)
                result.Report.Add($"Hidden geometry: left out {_hiddenRemoved} hidden object(s) or renderer(s) (deactivated, disabled, shadows-only, EditorOnly, or on a layer no camera draws" +
                                  (_skipSceneViewHidden ? ", or hidden in the Scene view" : "") + ").");
        }

        static bool SceneViewHidden(GameObject g) =>
            _skipSceneViewHidden && g.scene.IsValid() && SceneVisibilityManager.instance.IsHidden(g, false);

        /// <summary>An object Unity doesn't draw at all (it and everything under it).</summary>
        static bool HiddenObject(GameObject g) =>
            _skipHidden && (!g.activeSelf || g.CompareTag("EditorOnly") || SceneViewHidden(g));

        /// <summary>A renderer Unity doesn't draw: disabled, shadows only, forced off, or on a layer
        /// no camera in the scene draws.</summary>
        static bool HiddenRenderer(Renderer r) =>
            _skipHidden && (!r.enabled || r.forceRenderingOff || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                            || (_cameraMask & (1 << r.gameObject.layer)) == 0);

        static bool VisibleMesh(Renderer r) =>
            (r is MeshRenderer || r is SkinnedMeshRenderer) && !HiddenRenderer(r) && !SceneViewHidden(r.gameObject);

        /// <summary>Removes hidden objects and renderers from an export copy. `source` (the object
        /// the copy was made from, same hierarchy) is only needed for Scene-view hiding, which Unity
        /// tracks per scene object; null for copies of assets.</summary>
        static void PruneHidden(GameObject copy, GameObject source)
        {
            if (!_skipHidden) return;
            var bones = new HashSet<Transform>();
            foreach (var smr in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var b in smr.bones)
                    if (b != null) bones.Add(b);
            Prune(copy.transform, source != null ? source.transform : null, bones);
        }

        static void Prune(Transform copy, Transform source, HashSet<Transform> bones)
        {
            var doomed = new List<GameObject>();
            for (var i = 0; i < copy.childCount; i++)
            {
                var child = copy.GetChild(i);
                var sourceChild = source != null && i < source.childCount ? source.GetChild(i) : null;
                var sv = sourceChild != null && SceneViewHidden(sourceChild.gameObject);
                var hidden = !child.gameObject.activeSelf || child.gameObject.CompareTag("EditorOnly") || sv;
                // A skinned mesh's bones stay (removing one would break the mesh).
                if (hidden && !child.GetComponentsInChildren<Transform>(true).Any(bones.Contains))
                {
                    doomed.Add(child.gameObject);
                    continue;
                }
                Prune(child, sourceChild, bones);
            }
            foreach (var g in doomed)
            {
                _hiddenRemoved++;
                Object.DestroyImmediate(g);
            }
            var r = copy.GetComponent<Renderer>();
            if (r != null && (r is MeshRenderer || r is SkinnedMeshRenderer) &&
                (HiddenRenderer(r) || (source != null && SceneViewHidden(source.gameObject))))
            {
                var filter = copy.GetComponent<MeshFilter>();
                Object.DestroyImmediate(r);
                if (filter != null) Object.DestroyImmediate(filter);
                _hiddenRemoved++;
            }
        }

        static bool HasMeshes(GameObject g) =>
            g.GetComponentsInChildren<Renderer>(false).Any(r => VisibleMesh(r) && !UnderSceneViewHidden(r.transform, g.transform));

        static bool UnderSceneViewHidden(Transform t, Transform stop)
        {
            if (!_skipSceneViewHidden) return false;
            for (var x = t; x != null && x != stop.parent; x = x.parent)
                if (SceneViewHidden(x.gameObject)) return true;
            return false;
        }

        static bool IsUnit(GameObject g)
        {
            if (!HasMeshes(g)) return false;
            return g.GetComponent<Animator>() != null
                || g.GetComponent<Animation>() != null
                || g.GetComponent<MeshRenderer>() != null
                || g.GetComponent<SkinnedMeshRenderer>() != null
                || PrefabUtility.IsOutermostPrefabInstanceRoot(g);
        }

        static bool IsStaticScenery(GameObject g) =>
            g.isStatic
            && g.GetComponentInChildren<Animator>(true) == null
            && g.GetComponentInChildren<Animation>(true) == null
            && g.GetComponentInChildren<SkinnedMeshRenderer>(true) == null;

        /// <summary>Numbers for Unity objects within one export. (Not GetInstanceID: newer Unity 6
        /// versions reject it, and its replacement GetEntityId doesn't exist in older versions.)</summary>
        static readonly Dictionary<Object, int> _refs = new Dictionary<Object, int>();

        static int Ref(Object o)
        {
            if (o == null) return 0;
            if (!_refs.TryGetValue(o, out var n))
            {
                n = _refs.Count + 1;
                _refs[o] = n;
            }
            return n;
        }

        /// <summary>Objects that would export to identical files share one GLB.</summary>
        static string DedupeKey(GameObject g)
        {
            var animator = MainAnimator(g);
            var controller = animator != null && animator.runtimeAnimatorController != null ? Ref(animator.runtimeAnimatorController) : 0;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(g) && !PrefabUtility.HasPrefabInstanceAnyOverrides(g, false))
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(g);
                var path = source != null ? AssetDatabase.GetAssetPath(source) : "";
                if (path.Length > 0) return $"prefab:{path}:{controller}";
            }
            if (g.transform.childCount == 0 && animator == null)
            {
                var mf = g.GetComponent<MeshFilter>();
                var mr = g.GetComponent<MeshRenderer>();
                if (mf != null && mr != null && mf.sharedMesh != null)
                    return "mesh:" + Ref(mf.sharedMesh) + ":" +
                           string.Join(",", mr.sharedMaterials.Select(Ref));
            }
            return "object:" + Ref(g);
        }

        static void ExportTerrain(string path, Terrain terrain, ExportOptions o, string modelsDir, string modelsDirName,
            HashSet<string> files, FilmSpec spec, ExportResult result)
        {
            TerrainExport.Built built = null;
            try
            {
                built = TerrainExport.Build(terrain, o.TerrainGrid, o.TerrainTexture, o.MaxTrees, result.Warnings);
                var name = Coords.FileName(terrain.name, files);
                ExportUnit(built.Ground, modelsDir, name, false, null);
                var rel = modelsDirName + "/" + name + ".glb";
                result.Written.Add(rel);
                spec.Models.Add(new ModelSpec { Name = path, Path = rel, Xform = Xform(built.Ground.transform, false) });
                if (built.Trees != null)
                {
                    EditorUtility.DisplayProgressBar("Export to HyperBox", terrain.name + ": writing trees", 0.8f);
                    foreach (Transform tree in built.Trees.transform)
                    {
                        PruneHidden(tree.gameObject, null);
                        Strip(tree.gameObject);
                    }
                    var treesName = Coords.FileName(terrain.name + "_trees", files);
                    WriteGlb(built.Trees.transform, modelsDir, treesName, false);
                    var treesRel = modelsDirName + "/" + treesName + ".glb";
                    result.Written.Add(treesRel);
                    spec.Models.Add(new ModelSpec { Name = path + " trees", Path = treesRel, Xform = null });
                }
                result.Report.Add($"Terrain \"{path}\": exported as a mesh with its painted layers baked into one texture" +
                                  (built.TreeCount > 0 ? $", plus {built.TreeCount} trees." : "."));
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Terrain \"{path}\" could not be exported: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                built?.Destroy();
            }
        }

        static ModelSpec DescribeModel(Unit u, string relPath, bool includeAnimations)
        {
            var g = u.Go;
            var m = new ModelSpec
            {
                Name = u.Path,
                Path = relPath,
                Xform = Xform(g.transform, true),
                Character = g.GetComponentsInChildren<SkinnedMeshRenderer>(false).Any(VisibleMesh),
            };
            // The Animator is often on a child ("Player" > "Model"); UnityGLTF exports it wherever it is.
            var animator = MainAnimator(g);
            if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
            {
                m.Humanoid = true;
                m.BoneMap = HumanBoneMap(animator);
            }
            var animated = animator != null ? animator.gameObject : g.GetComponentInChildren<Animation>(false)?.gameObject;
            if (includeAnimations && animated != null)
            {
                var clips = AnimationUtility.GetAnimationClips(animated);
                foreach (var clip in clips.Where(x => x != null).Distinct())
                    m.Clips.Add(new ClipSpec { Name = Coords.GodotAnimationName(clip.name), Length = clip.length, Loop = clip.isLooping });
                var first = DefaultClip(animated);
                if (first != null) m.DefaultClip = Coords.GodotAnimationName(first.name);
                else if (m.Clips.Count > 0) m.DefaultClip = m.Clips[0].Name;
            }
            return m;
        }

        /// <summary>The object's Animator, or the first one below it.</summary>
        static Animator MainAnimator(GameObject g)
        {
            var a = g.GetComponent<Animator>();
            return a != null ? a : g.GetComponentInChildren<Animator>(false);
        }

        /// <summary>The clip an Animator starts in (its default state), or the Animation component's clip.</summary>
        static AnimationClip DefaultClip(GameObject g)
        {
            var animator = g.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController is AnimatorController ac && ac.layers.Length > 0)
            {
                var sm = ac.layers[0].stateMachine;
                if (sm != null && sm.defaultState != null && sm.defaultState.motion is AnimationClip clip) return clip;
            }
            var anim = g.GetComponent<Animation>();
            return anim != null ? anim.clip : null;
        }

        /// <summary>Unity's Humanoid mapping: HumanBodyBones name → the model's bone name.</summary>
        public static Dictionary<string, string> HumanBoneMap(Animator animator)
        {
            var map = new Dictionary<string, string>();
            var avatar = animator.avatar;
            if (avatar == null || !avatar.isHuman) return map;
            foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (b == HumanBodyBones.LastBone) continue;
                var t = animator.GetBoneTransform(b);
                if (t != null) map[b.ToString()] = t.name;
            }
            if (map.Count > 0) return map;
            // Animator not initialized: read the avatar's own description (HumanTrait.BoneName is
            // indexed like HumanBodyBones).
            var byTrait = new Dictionary<string, string>();
            for (var i = 0; i < HumanTrait.BoneCount && i < (int)HumanBodyBones.LastBone; i++)
                byTrait[HumanTrait.BoneName[i]] = ((HumanBodyBones)i).ToString();
            foreach (var hb in avatar.humanDescription.human)
                if (byTrait.TryGetValue(hb.humanName, out var bone) && !string.IsNullOrEmpty(hb.boneName))
                    map[bone] = hb.boneName;
            return map;
        }

        static void CollectCameras(Collected c, FilmSpec spec)
        {
            foreach (var (path, cam) in c.Cameras)
            {
                spec.Cameras.Add(new CameraSpec
                {
                    Name = path,
                    Xform = Xform(cam.transform, false),
                    Fov = cam.orthographic ? 50 : cam.fieldOfView,
                    Main = cam.CompareTag("MainCamera"),
                });
            }
            if (spec.Cameras.Count > 0) return;
            // No camera in the scene: start from where the Scene view is looking.
            var view = SceneView.lastActiveSceneView;
            if (view != null && view.camera != null)
                spec.Cameras.Add(new CameraSpec
                {
                    Name = "Scene view camera",
                    Xform = Xform(view.camera.transform, false),
                    Fov = view.camera.fieldOfView,
                    Main = true,
                });
        }

        static void CollectLights(Collected c, FilmSpec spec, List<string> warnings)
        {
            var physical = false;
            foreach (var (path, l) in c.Lights)
            {
                string type;
                switch (l.type)
                {
                    case LightType.Directional: type = "directional"; break;
                    case LightType.Spot: type = "spot"; break;
                    case LightType.Point: type = "point"; break;
                    default:
                        warnings.Add($"Light \"{path}\" is an area light (baked only in Unity), so it was skipped.");
                        continue;
                }
                var color = l.color;
                if (l.useColorTemperature && UnityEngine.Rendering.GraphicsSettings.lightsUseColorTemperature)
                    color *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature);
                if (l.intensity > 100) physical = true;
                spec.Lights.Add(new LightSpec
                {
                    Name = path,
                    Xform = Xform(l.transform, false),
                    Type = type,
                    R = color.r, G = color.g, B = color.b,
                    Intensity = SceneLighting.LightEnergy(l.intensity),
                    Range = l.range,
                    SpotAngle = l.spotAngle,
                    Shadows = l.shadows != LightShadows.None,
                });
            }
            if (physical)
                warnings.Add("Some lights use physical intensities (HDRP?), so they'll look far too bright. Lower their energy in HyperBox.");
        }

        static void CollectEnvironment(FilmSpec spec, List<string> warnings)
        {
            spec.AmbientIntensity = RenderSettings.ambientIntensity;
            if (RenderSettings.fog)
            {
                switch (RenderSettings.fogMode)
                {
                    case FogMode.Linear:
                        spec.FogDensity = 3.0 / Math.Max(1.0, RenderSettings.fogEndDistance);
                        break;
                    case FogMode.ExponentialSquared:
                        spec.FogDensity = RenderSettings.fogDensity * 1.2;
                        break;
                    default:
                        spec.FogDensity = RenderSettings.fogDensity;
                        break;
                }
                var fc = RenderSettings.fogColor;
                spec.FogColor = new double[] { fc.r, fc.g, fc.b };
            }
            if (RenderSettings.skybox != null)
                warnings.Add("The skybox isn't carried over; HyperBox uses a procedural sky (day, sunset, night or overcast).");
        }

        static UnityXform Xform(Transform t, bool withScale)
        {
            var p = t.position;
            var q = t.rotation;
            var x = new UnityXform { px = p.x, py = p.y, pz = p.z, rot = new Quat(q.x, q.y, q.z, q.w) };
            if (withScale)
            {
                var s = t.lossyScale;
                x.sx = s.x; x.sy = s.y; x.sz = s.z;
            }
            return x;
        }

        // ------------------------------------------------------------------ models and clip packs

        /// <summary>Each object → its own GLB in `folder`, at the origin, facing +Z (glTF's and HyperBox's forward).</summary>
        public static ExportResult ExportModels(IList<GameObject> objects, string folder, bool includeAnimations, bool skipHidden = true, bool skipSceneViewHidden = false)
        {
            RequireUnityGltf();
            BeginHiddenRules(skipHidden, skipSceneViewHidden);
            var result = new ExportResult();
            var files = new HashSet<string>();
            try
            {
                for (var i = 0; i < objects.Count; i++)
                {
                    var g = objects[i];
                    EditorUtility.DisplayProgressBar("Export to HyperBox", g.name, i / (float)objects.Count);
                    var name = Coords.FileName(g.name, files);
                    try
                    {
                        ExportUnit(g, folder, name, includeAnimations, null);
                        result.Written.Add(name + ".glb");
                    }
                    catch (Exception e)
                    {
                        result.Warnings.Add($"\"{g.name}\" could not be exported: {e.Message}");
                        Debug.LogException(e);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            result.Files = result.Written.Count;
            ReportHidden(result);
            return result;
        }

        /// <summary>Bakes `clips` onto a Humanoid character and writes them as one animation file.
        /// In HyperBox: Animation tab → Import Animations… (the clips then play on any humanoid).</summary>
        public static ExportResult ExportClipPack(GameObject character, IList<AnimationClip> clips, string folder, string fileName, bool skipHidden = true)
        {
            RequireUnityGltf();
            BeginHiddenRules(skipHidden, false);
            var result = new ExportResult();
            var animator = character != null ? MainAnimator(character) : null;
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                throw new InvalidOperationException("Pick a character with an Animator and a Humanoid avatar.");
            var list = clips.Where(x => x != null).Distinct().ToList();
            if (list.Count == 0)
                throw new InvalidOperationException("Add at least one animation clip.");
            foreach (var clip in list.Where(x => !x.isHumanMotion))
                result.Warnings.Add($"\"{clip.name}\" isn't a Humanoid clip, so it only plays on this exact rig.");

            var controllerPath = AssetDatabase.GenerateUniqueAssetPath("Assets/HyperBoxClipPack.controller");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            try
            {
                foreach (var clip in list)
                    controller.AddMotion(clip);
                var name = Coords.FileName(fileName, new HashSet<string>());
                ExportUnit(character, folder, name, true, controller);
                result.Written.Add(name + ".glb");
                result.Files = 1;
            }
            finally
            {
                AssetDatabase.DeleteAsset(controllerPath);
            }
            return result;
        }

        /// <summary>Exports one object: a copy at the origin with no rotation (so it faces +Z, as
        /// HyperBox expects of models). Lights and cameras are left out (they become actors).</summary>
        static void ExportUnit(GameObject source, string folder, string fileName, bool includeAnimations, RuntimeAnimatorController controller)
        {
            var wrapper = new GameObject("HyperBoxExport");
            try
            {
                wrapper.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var copy = Object.Instantiate(source, wrapper.transform, false);
                copy.name = source.name;
                copy.SetActive(true);
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one;
                if (controller != null)
                    MainAnimator(copy).runtimeAnimatorController = controller;
                PruneHidden(copy, source);
                Strip(copy);
                WriteGlb(wrapper.transform, folder, fileName, includeAnimations);
            }
            finally
            {
                Object.DestroyImmediate(wrapper);
            }
            // Humanoids carry Unity's exact bone mapping inside the file (extras.hyperbox.bone_map),
            // so HyperBox maps the rig correctly however the file is brought in.
            var animator = MainAnimator(source);
            if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
            {
                var map = GlbPatch.HyperBoxBoneMap(HumanBoneMap(animator));
                if (map.Count > 0)
                    GlbPatch.AddHyperBoxExtras(Path.Combine(folder, fileName + ".glb"),
                        new JObj { { "exporter", "HyperBox Exporter for Unity" }, { "bone_map", map } });
            }
        }

        /// <summary>Exports many objects as one model, keeping their world placement (merged scenery).</summary>
        static void ExportGroup(IList<GameObject> sources, string folder, string fileName)
        {
            var root = new GameObject("Scenery");
            try
            {
                foreach (var s in sources)
                {
                    var copy = Object.Instantiate(s, s.transform.position, s.transform.rotation, root.transform);
                    copy.name = s.name;
                    copy.SetActive(true);
                    copy.transform.localScale = s.transform.lossyScale;
                    PruneHidden(copy, s);
                    Strip(copy);
                }
                WriteGlb(root.transform, folder, fileName, false);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Removes cameras, lights and listeners from an export copy (with the components
        /// that depend on them, like URP's additional camera/light data).</summary>
        static void Strip(GameObject copy)
        {
            // LOD groups: keep the most detailed level only (otherwise every level is exported
            // and drawn on top of each other).
            foreach (var group in copy.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                if (lods.Length > 1)
                {
                    var keep = new HashSet<Renderer>(lods[0].renderers.Where(r => r != null));
                    foreach (var lod in lods.Skip(1))
                    {
                        foreach (var r in lod.renderers)
                        {
                            if (r == null || keep.Contains(r)) continue;
                            var filter = r.GetComponent<MeshFilter>();
                            Object.DestroyImmediate(r);
                            if (filter != null) Object.DestroyImmediate(filter);
                        }
                    }
                }
                Object.DestroyImmediate(group);
            }

            var hosts = new HashSet<GameObject>();
            foreach (var c in copy.GetComponentsInChildren<Camera>(true)) hosts.Add(c.gameObject);
            foreach (var l in copy.GetComponentsInChildren<Light>(true)) hosts.Add(l.gameObject);
            foreach (var a in copy.GetComponentsInChildren<AudioListener>(true)) hosts.Add(a.gameObject);
            foreach (var h in hosts)
            {
                foreach (var mb in h.GetComponents<MonoBehaviour>().Reverse())
                    if (mb != null) Object.DestroyImmediate(mb);
                foreach (var comp in h.GetComponents<Component>())
                    if (comp is Camera || comp is Light || comp is AudioListener || comp is FlareLayer)
                        Object.DestroyImmediate(comp);
            }
        }

        static void WriteGlb(Transform root, string folder, string fileName, bool includeAnimations)
        {
#if HYPERBOX_UNITYGLTF
            Directory.CreateDirectory(folder);
            var settings = ScriptableObject.CreateInstance<GLTFSettings>();
            try
            {
                settings.ExportNames = true;
                settings.UseMainCameraVisibility = false;
                settings.ExportDisabledGameObjects = false;
                settings.ExportAnimations = includeAnimations;
                settings.BakeAnimationSpeed = true;
                var context = new ExportContext(settings);
                var exporter = new GLTFSceneExporter(new[] { root }, context);
                exporter.SaveGLB(folder, fileName);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
#else
            throw new InvalidOperationException("UnityGLTF isn't installed.");
#endif
        }

        static void RequireUnityGltf()
        {
            if (!UnityGltfInstalled)
                throw new InvalidOperationException("The HyperBox Exporter needs UnityGLTF. Install it from " + UnityGltfGitUrl);
        }
    }
}
