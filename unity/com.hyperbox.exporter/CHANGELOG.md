# Changelog

## 0.4.0
- Cutout and transparent materials: foliage, leaves, grass cards, fences and glass keep their
  alpha whatever the shader (SpeedTree, Tree Creator, Shader Graph, asset-store shaders):
  alpha-tested materials arrive as cutout with Unity's cutoff, transparent ones as blended,
  and leaves and other two-sided materials render from both sides.
- Tiling: texture tiling and offset carry over on every material, custom shaders included.
- World-space / triplanar materials: their texturing is baked into the mesh's UVs at export,
  so world-tiled walls, floors and rocks look as they do in Unity. Objects using them each get
  their own model file (their UVs depend on where they stand).
- Custom shaders are converted to standard PBR with their colour map, tint, normal map,
  metallic, smoothness and emission (before, only some of their textures came through).
- Terrain: the painted layers now tile at full detail in HyperBox (up to 8 layers, with their
  normal maps, tint and smoothness), instead of one texture stretched over the whole terrain.
  The baked texture stays in the file for other glTF viewers.
- Assets > HyperBox > Material Export overrides the guesses for selected materials
  (world-space tiled, cutout, transparent, opaque, two-sided), stored as asset labels.
- The export report lists cutout, transparent, two-sided, tiled and converted materials.

## 0.3.1
- Hidden geometry is left out (on by default, "Skip hidden objects"): deactivated objects,
  disabled and shadows-only renderers, EditorOnly objects, and layers no camera in the scene
  draws. Objects hidden with the Scene view's eye icon can be left out too (off by default).
  The export report says how many were left out.

## 0.3.0
- Sky: the skybox is captured as a panorama image and used as HyperBox's sky and ambient light.
- Lighting: ambient light (skybox, flat colour, gradient); post-processing from global URP/HDRP
  volumes and Post Processing Stack v2 (tonemapping, exposure, contrast, saturation, white
  balance, bloom, vignette, grain); Gamma-space intensities; bounce light for baked scenes.
- Fog: every Unity fog mode; the sky stays clear of fog, as in Unity.
- Terrain: exported as a mesh with its painted layers baked into a texture, plus its trees.
- LOD groups export only their most detailed level.
- The export window shows a lighting report.

## 0.2.0
- Models face +Z, as HyperBox expects, so Claude's face and walk_to turn imported characters
  the right way.
- Works with newer Unity 6 versions (no GetInstanceID).

## 0.1.0
- First version: scenes, characters, cameras, lights, animation clips and clip packs.
