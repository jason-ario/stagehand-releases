# HyperBox Exporter for Unity

Brings Unity scenes, characters and animations into HyperBox. It writes a film document that
HyperBox opens as it is, plus GLB model files. Humanoid animation clips are baked onto the
skeleton, so they play in HyperBox and can be retargeted onto any other character there.

## Install

Unity 2021.3 or newer, with the Built-in Render Pipeline or URP. HDRP scenes export too, but their materials and light intensities need more fixing up in HyperBox.

1. **UnityGLTF** writes the model files. Open **Window → Package Manager → + → Add package from git URL…**
   and paste `https://github.com/KhronosGroup/UnityGLTF.git`. The exporter's window also has a
   button that does this.
2. **This package:** add it the same way, from
   `https://github.com/jason-ario/stagehand-releases.git?path=/unity/com.hyperbox.exporter`.
   That is HyperBox's public downloads repo, so no GitHub sign-in is needed. To stay on one
   version, add its tag to the end of the URL, e.g. `#exporter-v0.4.0`.

## Use

Open **Tools → HyperBox → Export to HyperBox…** and choose what to export:

| Export | You get | In HyperBox |
|---|---|---|
| **Whole scene → film** | `<name>.film.json` + `<name>_models/*.glb` | **Open…** the film |
| **Selected objects → film** | the same, with only the selection | **Open…** the film |
| **Selected objects → model files** | one `.glb` per object | drag the files onto the window (or **Models → Import File…**) |
| **Animation clips → clip pack** | one `.glb` with the clips baked onto a character | **Animation → Import Animations…** |

To make a clip pack quickly, select clips in the Project window, right-click and choose
**HyperBox → Make Clip Pack From Selected Clips**.

## What comes across

- **Models:** each object with an Animator, each prefab instance and each object with its own
  mesh becomes one actor. Empty "organizer" objects are opened up and their children exported.
  Copies of the same prefab (without overrides) or the same mesh share one file.
- **Static scenery:** with **Merge static scenery** on (the default), objects marked Static and
  not animated become one "Scenery" model. That loads much faster, but its pieces can't be moved
  one at a time in HyperBox.
- **Characters:** skinned meshes become characters. A Humanoid avatar's exact bone mapping is
  written into the film (`rig.bone_map`) and into the file itself (`extras.hyperbox.bone_map`),
  so HyperBox never has to guess bone names. Every clip in the Animator Controller is baked in,
  the default state's clip plays from the start, and the clips are also listed as an animation
  set, so other characters in the film can use them.
- **Cameras:** every enabled camera is copied, with the same field of view. The camera tagged
  MainCamera frames the opening shot. With no camera in the scene, the Scene view's camera is used.
- **Lights:** the first directional light becomes HyperBox's sun. Point and spot lights become
  light actors. Lights inside models are pulled out as separate actors. In a Gamma colour space
  project, intensities are converted to Linear.
- **Sky:** the skybox (procedural, HDRI, 6-sided or a custom shader) is rendered in six
  directions and saved as a panorama image (`<name>_models/sky.png`). HyperBox shows it and
  lights the scene with it. It's a still image, so it won't follow later sun changes in
  HyperBox. Applying a look with lighting swaps it for the look's sky.
- **Ambient light:** from the skybox, a flat colour, or a gradient (averaged into one colour),
  with its intensity.
- **Fog:** Unity's fog from the Lighting window, in any mode. Linear fog becomes the closest
  exponential fog, and the sky stays clear of it as in Unity. Fog from volumes or fog assets
  can't come over yet. The export report says where the scene's fog comes from.
- **Post-processing:** global URP / HDRP volumes and Post Processing Stack v2 volumes:
  tonemapping (none stays untonemapped, like Unity), post-exposure, contrast, saturation,
  white balance, bloom, vignette and film grain.
- **Baked lighting:** HyperBox can't use Unity's lightmaps. If the scene has lightmaps or
  Baked/Mixed lights, the film turns on bounce light (real-time global illumination) instead.
- **Terrain:** the heightmap becomes a mesh (513×513 vertices) and the trees become one merged
  model (up to 3000 per terrain, thinned evenly beyond that). The painted layers travel inside
  the ground's GLB (`extras.hyperbox.terrain`: the splat control maps plus each layer's colour
  and normal maps, tile size, offset, tint and smoothness), and HyperBox draws them tiling at
  full detail, as Unity does. Up to 8 layers (the most painted ones). The file's own material is
  the layers baked into one 2048² texture, which other glTF viewers show. Grass and detail
  meshes aren't exported yet.
- **LOD groups:** only the most detailed level is exported.
- **Hidden geometry:** with **Skip hidden objects** on (the default), what Unity doesn't draw is
  left out: deactivated objects, disabled and shadows-only renderers, EditorOnly objects, and
  layers no camera in the scene renders. **…and Scene view hidden** also leaves out objects
  hidden with the eye icon. A skinned mesh's bones always stay, even if hidden.
- **Materials:** standard PBR (base colour, normal, metallic/roughness, emission) through
  UnityGLTF, plus what UnityGLTF can't read from custom shaders:
  - **Cutout and transparency:** alpha-tested materials (foliage, leaves, grass cards, fences)
    become glTF cutout (`MASK`) with Unity's cutoff; transparent ones become blended. Found from
    the material's keywords, render type, surface/cutout settings and render queue, so it works
    for SpeedTree, Tree Creator, Shader Graph and asset-store shaders. Leaves and other
    two-sided materials (Cull Off) render from both sides.
  - **Tiling:** each material's texture tiling and offset (`_MainTex_ST` / `_BaseMap_ST`)
    become `KHR_texture_transform`, which HyperBox applies.
  - **World-space / triplanar texturing:** a GLB can't compute UVs from world position, so the
    exporter bakes them: each triangle gets UVs from the world plane it faces (as a triplanar
    shader does), at the material's tiling. World-tiled walls, floors and rocks then look as
    they do in Unity. Such objects each get their own GLB. Skinned meshes keep their own UVs.
  - **Custom shaders and Shader Graph** are converted to the render pipeline's Lit shader with
    their colour map, tint, normal map, metallic, smoothness and emission.

  The exporter guesses from shader and property names. When it guesses wrong, select the
  materials in the Project window and use **Assets → HyperBox → Material Export →** World-Space
  Tiled, Cutout, Transparent, Opaque or Two-Sided (or Automatic to clear). These are stored as
  asset labels (`HyperBox-WorldUV`, `HyperBox-Cutout`, …), so they stay with the project. For
  world-space materials the tiling comes from a property named like *tiling* or *scale* (repeats
  per metre) or *size* (metres per repeat), otherwise from the colour map's tiling.

After a film export, the window lists what it found about the scene's lighting and materials,
and how each part was carried over. The same report goes to the Console and into the film's notes, so
Claude knows it too.

**Not carried over yet:** fog from volumes or fog assets, terrain grass and details, area
lights, Timeline and Cinemachine, particles, HDRP physical light units.

## Coordinates

Unity is left-handed and HyperBox is right-handed. UnityGLTF mirrors X, and the exporter does
the same for positions and rotations. Models face +Z in both Unity and HyperBox, so Claude's
`face` and `walk_to` turn imported characters correctly. Cameras and lights point down −Z in
HyperBox, so they get an extra half turn. A character facing +X in Unity faces −X in
HyperBox: the same direction, seen from the mirrored side. `Editor/Coords.cs` has the math,
and `unity/tests` checks it against Godot.

## Development

`Coords.cs`, `FilmBuilder.cs`, `FilmJson.cs`, `GlbPatch.cs`, `GltfJson.cs`, `MaterialFixes.cs` and
`SkyResample.cs` don't use UnityEngine. `dotnet run --project unity/tests` checks them and compares `unity/tests/fixtures.json`
(run it with `-- --write` after a change). HyperBox's tests read that file:
`app/tests/unity_import_test.gd` and `server/unity-export.test.js`.
`app/tests/unity_materials_test.gd` loads GLBs written the way the exporter patches them
(cutout, transparency, tiling, terrain layers) and checks what HyperBox makes of them. `tools/check.sh` runs all of
them.
