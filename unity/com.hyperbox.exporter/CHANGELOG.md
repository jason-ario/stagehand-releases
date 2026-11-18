# Changelog

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
