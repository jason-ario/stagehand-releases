using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HyperBox.Export
{
    /// <summary>Tools → HyperBox → Export to HyperBox…</summary>
    public class HyperBoxExportWindow : EditorWindow
    {
        enum Mode { WholeScene, SelectionAsFilm, SelectionAsModels, AnimationClips }

        static readonly string[] ModeLabels =
        {
            "Whole scene → film",
            "Selected objects → film",
            "Selected objects → model files",
            "Animation clips → clip pack",
        };

        [SerializeField] Mode mode = Mode.WholeScene;
        [SerializeField] string outputFolder = "";
        [SerializeField] string filmName = "";
        [SerializeField] bool mergeStatic = true;
        [SerializeField] bool includeAnimations = true;
        [SerializeField] float shotLength = 5f;
        [SerializeField] int fps = 30;
        [SerializeField] bool includeLighting = true;
        [SerializeField] bool includeTerrain = true;
        [SerializeField] int maxTrees = 3000;
        [SerializeField] bool skipHidden = true;
        [SerializeField] bool skipSceneViewHidden = false;
        [SerializeField] GameObject character;
        [SerializeField] List<AnimationClip> clips = new List<AnimationClip>();
        [SerializeField] string packName = "Unity clips";

        string _lastMessage = "";
        MessageType _lastType = MessageType.None;
        string _lastFolder = "";
        Vector2 _scroll;
        SerializedObject _so;

        const string FolderPref = "HyperBox.Export.Folder";

        [MenuItem("Tools/HyperBox/Export to HyperBox…", priority = 0)]
        public static void Open()
        {
            var w = GetWindow<HyperBoxExportWindow>("HyperBox Export");
            w.minSize = new Vector2(380, 360);
            w.Show();
        }

        [MenuItem("Assets/HyperBox/Make Clip Pack From Selected Clips", priority = 1000)]
        static void ClipPackFromSelection()
        {
            var w = GetWindow<HyperBoxExportWindow>("HyperBox Export");
            w.mode = Mode.AnimationClips;
            w.clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).ToList();
            w.Show();
        }

        [MenuItem("Assets/HyperBox/Make Clip Pack From Selected Clips", true)]
        static bool ClipPackFromSelectionValid() => Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).Length > 0;

        void OnEnable()
        {
            if (string.IsNullOrEmpty(outputFolder))
                outputFolder = EditorPrefs.GetString(FolderPref, Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "HyperBoxExport"));
            _so = new SerializedObject(this);
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (!HyperBoxExporter.UnityGltfInstalled)
            {
                EditorGUILayout.HelpBox("The HyperBox Exporter writes models with UnityGLTF, which isn't installed in this project yet.", MessageType.Warning);
                if (GUILayout.Button("Install UnityGLTF (from GitHub)"))
                    UnityEditor.PackageManager.Client.Add(HyperBoxExporter.UnityGltfGitUrl);
                EditorGUILayout.EndScrollView();
                return;
            }

            mode = (Mode)EditorGUILayout.Popup("Export", (int)mode, ModeLabels);
            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                outputFolder = EditorGUILayout.TextField("Folder", outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var picked = EditorUtility.OpenFolderPanel("Export to", outputFolder, "");
                    if (!string.IsNullOrEmpty(picked)) outputFolder = picked;
                }
            }

            switch (mode)
            {
                case Mode.WholeScene:
                case Mode.SelectionAsFilm:
                    DrawFilmOptions();
                    break;
                case Mode.SelectionAsModels:
                    includeAnimations = EditorGUILayout.Toggle(new GUIContent("Include animations", "Bake each object's Animator clips into its file."), includeAnimations);
                    EditorGUILayout.HelpBox($"{Selection.gameObjects.Length} object(s) selected. Each becomes a .glb you can drag onto HyperBox's window.", MessageType.None);
                    break;
                case Mode.AnimationClips:
                    DrawClipPackOptions();
                    break;
            }

            if (mode != Mode.AnimationClips)
            {
                skipHidden = EditorGUILayout.Toggle(new GUIContent("Skip hidden objects", "Leave out what Unity doesn't draw: deactivated objects, disabled or shadows-only renderers, EditorOnly objects, and layers no camera in the scene renders."), skipHidden);
                using (new EditorGUI.DisabledScope(!skipHidden))
                    skipSceneViewHidden = EditorGUILayout.Toggle(new GUIContent("…and Scene view hidden", "Also leave out objects hidden with the eye icon in the Hierarchy / Scene view."), skipSceneViewHidden);
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!CanExport()))
            {
                if (GUILayout.Button("Export", GUILayout.Height(30)))
                    RunExport();
            }

            if (!string.IsNullOrEmpty(_lastMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_lastMessage, _lastType);
                if (!string.IsNullOrEmpty(_lastFolder) && GUILayout.Button("Show Files"))
                    EditorUtility.RevealInFinder(_lastFolder);
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawFilmOptions()
        {
            var sceneName = EditorSceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(filmName)) filmName = string.IsNullOrEmpty(sceneName) ? "Unity Scene" : sceneName;
            filmName = EditorGUILayout.TextField("Film name", filmName);
            mergeStatic = EditorGUILayout.Toggle(new GUIContent("Merge static scenery", "Objects marked Static become one 'Scenery' model: faster to load, but they can't be moved one by one in HyperBox."), mergeStatic);
            includeAnimations = EditorGUILayout.Toggle(new GUIContent("Include animations", "Bake each character's Animator clips (Humanoid clips included) into its model."), includeAnimations);
            shotLength = Mathf.Max(0.5f, EditorGUILayout.FloatField(new GUIContent("Shot length (s)", "Length of the opening shot."), shotLength));
            fps = Mathf.Clamp(EditorGUILayout.IntField("Frames per second", fps), 1, 240);
            includeLighting = EditorGUILayout.Toggle(new GUIContent("Sky, fog and lighting", "Capture the skybox as an image, and carry over ambient light, fog, post-processing (tonemapping, exposure, bloom, colour) and bounce light for baked scenes."), includeLighting);
            includeTerrain = EditorGUILayout.Toggle(new GUIContent("Terrain", "Export terrains as meshes with their painted layers baked into a texture, plus their trees."), includeTerrain);
            if (includeTerrain)
                maxTrees = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Max trees per terrain", "Trees beyond this are thinned out evenly (0 = no trees)."), maxTrees));
            if (mode == Mode.SelectionAsFilm)
                EditorGUILayout.HelpBox($"{Selection.gameObjects.Length} object(s) selected (their children come too).", MessageType.None);
        }

        void DrawClipPackOptions()
        {
            character = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Character", "A Humanoid character (scene object or model asset) to bake the clips onto."), character, typeof(GameObject), true);
            packName = EditorGUILayout.TextField("File name", packName);
            _so.Update();
            EditorGUILayout.PropertyField(_so.FindProperty("clips"), new GUIContent("Clips"), true);
            _so.ApplyModifiedProperties();
            if (GUILayout.Button("Use Clips Selected in Project"))
                clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets).ToList();
            EditorGUILayout.HelpBox("In HyperBox: Animation tab → Import Animations…, then pick this file. Humanoid clips play on any humanoid character.", MessageType.None);
        }

        bool CanExport()
        {
            if (string.IsNullOrWhiteSpace(outputFolder)) return false;
            switch (mode)
            {
                case Mode.SelectionAsFilm:
                case Mode.SelectionAsModels:
                    return Selection.gameObjects.Length > 0;
                case Mode.AnimationClips:
                    return character != null && clips.Any(c => c != null);
                default:
                    return true;
            }
        }

        void RunExport()
        {
            EditorPrefs.SetString(FolderPref, outputFolder);
            try
            {
                ExportResult result;
                switch (mode)
                {
                    case Mode.WholeScene:
                    case Mode.SelectionAsFilm:
                    {
                        var roots = mode == Mode.WholeScene
                            ? EditorSceneManager.GetActiveScene().GetRootGameObjects().ToList()
                            : TopLevel(Selection.gameObjects);
                        var options = new ExportOptions
                        {
                            OutputFolder = outputFolder,
                            FilmName = filmName,
                            MergeStatic = mergeStatic,
                            IncludeAnimations = includeAnimations,
                            ShotLength = shotLength,
                            Fps = fps,
                            IncludeLighting = includeLighting,
                            IncludeTerrain = includeTerrain,
                            MaxTrees = maxTrees,
                            SkipHidden = skipHidden,
                            SkipSceneViewHidden = skipSceneViewHidden,
                        };
                        result = HyperBoxExporter.ExportFilm(roots, options, filmName);
                        Report(result, $"Wrote {Path.GetFileName(result.FilmPath)} with {result.Actors} actor(s) and {result.Files} model file(s). Open it in HyperBox with Open….");
                        break;
                    }
                    case Mode.SelectionAsModels:
                        result = HyperBoxExporter.ExportModels(TopLevel(Selection.gameObjects), outputFolder, includeAnimations, skipHidden, skipSceneViewHidden);
                        Report(result, $"Wrote {result.Files} model file(s). Drag them onto HyperBox's window (or use Import File… in the Models tab).");
                        break;
                    default:
                        var source = character;
                        GameObject temp = null;
                        if (!source.scene.IsValid())  // a model asset: use a temporary instance
                        {
                            temp = (GameObject)PrefabUtility.InstantiatePrefab(source);
                            source = temp;
                        }
                        try
                        {
                            result = HyperBoxExporter.ExportClipPack(source, clips, outputFolder, packName);
                        }
                        finally
                        {
                            if (temp != null) DestroyImmediate(temp);
                        }
                        Report(result, $"Wrote {result.Written.FirstOrDefault()} with {clips.Count(c => c != null)} clip(s). In HyperBox: Animation tab → Import Animations….");
                        break;
                }
                _lastFolder = outputFolder;
            }
            catch (Exception e)
            {
                _lastMessage = e.Message;
                _lastType = MessageType.Error;
                Debug.LogException(e);
            }
        }

        void Report(ExportResult result, string summary)
        {
            _lastMessage = summary;
            if (result.Report.Count > 0)
                _lastMessage += "\n\n" + string.Join("\n", result.Report.Select(r => "• " + r));
            if (result.Warnings.Count > 0)
                _lastMessage += "\n\n" + string.Join("\n", result.Warnings.Select(w => "• " + w));
            _lastType = result.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
            foreach (var r in result.Report) Debug.Log("[HyperBox] " + r);
            foreach (var w in result.Warnings) Debug.LogWarning("[HyperBox] " + w);
            Debug.Log("[HyperBox] " + summary);
        }

        /// <summary>Selected objects without their selected descendants (they come along anyway).</summary>
        static List<GameObject> TopLevel(GameObject[] selection)
        {
            var set = new HashSet<Transform>(selection.Select(g => g.transform));
            return selection.Where(g =>
            {
                for (var p = g.transform.parent; p != null; p = p.parent)
                    if (set.Contains(p)) return false;
                return true;
            }).ToList();
        }
    }
}
