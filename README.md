# OSM Workflow for Unity URP

A Unity editor tool that packs three grayscale maps into **one texture**, and creates the shader and material to use it.

**OSM** means the three maps are stored in the color channels of a single image:

| Channel | Map |
|---|---|
| **R** | Ambient **O**cclusion |
| **G** | **S**moothness |
| **B** | **M**etallic |

Unity's URP Lit shader uses two textures for these (metallic-smoothness and occlusion). OSM uses one, so each material needs less graphics memory.

> Built with AI assistance (Claude) and tested by the author.

## Result from one test

2K model, same textures on both materials, Windows standalone build, measured with Unity's Memory Profiler:

| | URP Lit (separate maps) | OSM |
|---|---|---|
| Graphics memory used by the mask maps | 10.8 MB (2 textures) | 5.4 MB (1 texture) |
| Total graphics memory for the material | 14.8 MB | 9.4 MB |

That is about **5.4 MB saved per 2K material**. This is one model on one computer, and it measures memory only, not speed.

## Requirements

- Unity **6.5 (6000.5)** or newer
- Universal Render Pipeline (URP) **17.5** or newer

## Install

See the [Installation Guide](Documentation/Installation_Guide.md). In short:

1. Click **Code > Download ZIP** on this page and unzip it.
2. Rename the folder to `OSMWorkflow` and put it in your project's `Assets` folder.
3. In Unity, run **Tools > OSM Workflow > Run Self-Test**. Every line should say PASS.

## Use it

1. **Assets > Create > OSM Workflow > Shader Graph (Separate Maps template)**, then double-click it.
2. Add your textures and click **Generate Material**.
3. Click **Bake OSM…** to save one packed texture.
4. Use **Compare…** to check the result looks the same.

The [User Manual](Documentation/User_Manual.md) explains each step in detail.

## What it includes

- A node graph for textures, with checks that warn about common mistakes
- Roughness-to-Smoothness and normal map flip tools
- A baker that saves the packed OSM texture
- A generated URP Lit-style shader and material
- A Compare window (original vs packed)
- A built-in self-test

## Limitations

- Needs Unity 6.5+ and URP 17.5+.
- Uses Unity's experimental GraphView API, which may change in future Unity versions.
- Makes an opaque standard PBR shader only (alpha clipping works, alpha blending does not). No detail maps, clear coat or decals.
- Not tested on mobile or console. Check your own build.
- Does not combine meshes or make texture atlases.
- The 16-bit bake option is experimental.
- The memory saving depends on your texture settings. Compare with the same resolution and compression on both sides.

## Help

Found a problem? Open an **Issue** on this page and include your Unity version and the error message from the Console.
