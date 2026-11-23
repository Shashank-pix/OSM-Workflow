# User Manual

## 1. What this tool does

A material in Unity uses several textures. Three of them are grayscale maps:

| Map | What it controls |
|---|---|
| **AO** (Ambient Occlusion) | Darkens cracks and corners |
| **Smoothness** | How shiny the surface is |
| **Metallic** | Whether the surface behaves like metal |

Unity's URP Lit shader stores these in two texture files. This tool stores them in **one** texture, called an **OSM texture**:

- **R** (red) = Occlusion
- **G** (green) = Smoothness
- **B** (blue) = Metallic

It also creates the shader and material that read this texture. You don't write any code.

## 2. Do you need it?

It can help if you have many different 2K PBR models, or you target mobile, VR or other devices with limited memory. In one test it saved about **5.4 MB of graphics memory per material**.

It is probably not worth it if you only have a few models, you target powerful PCs, or you need URP Lit features that this tool doesn't have (see Limits at the end).

## 3. Before you start

Install the tool (see the Installation Guide) and run **Tools > OSM Workflow > Run Self-Test**. Every line should say PASS.

## 4. Make your first material

### Step 1: Create a graph
Right-click in the Project window: **Create > OSM Workflow > Shader Graph (Separate Maps template)**. Double-click the new file.

A graph is a diagram of boxes (nodes) joined by lines. Data flows from left to right. The final box on the right, **OSM Lit**, is your material.

### Step 2: Add your textures
Click each texture node and assign your image:

- **Base Color Texture**: the colored image
- **Normal Map**: the bump detail image
- **AO Map**, **Roughness Map**, **Metallic Map**: the three grayscale maps

If your map is already *smoothness* (not roughness), click **Roughness to Smoothness** and turn **Invert** off.

### Step 3: Generate the material
Click **Generate Material** in the top bar and choose where to save. Unity creates a shader and a material. Drag the material onto your model.

Now the material works, but it still uses three separate maps. The memory saving comes from the next step.

### Step 4: Bake the packed texture
1. Click **Bake OSM…**
2. Choose a size (for example 2048) and keep mipmaps on.
3. Click **Bake**. One new image appears in your project.
4. Create a new graph: **Create > OSM Workflow > Shader Graph (Packed OSM template)**.
5. Put the baked image in the **OSM Texture Input** node, connect your other textures, and click **Generate Material**.
6. Make sure nothing else uses the three old grayscale maps, so they stay out of your build.

If you already have a packed texture (R = AO, G = Smoothness, B = Metallic), skip Steps 1 to 4 and use the Packed OSM template directly.

## 5. The top bar of the graph window

| Button | What it does |
|---|---|
| **Save** | Saves the graph (Ctrl+S) |
| **Generate Material** | Creates the shader and material. Next time it updates the same one. |
| Small arrow beside it | Update linked material, Select linked material, Save as new material… |
| **Bake OSM…** | Saves the packed texture |
| **Compare…** | Checks the packed result against your original maps |

## 6. The Compare window

Compare checks that packing did not change how your material looks. It shows:

- **Original**: uses your three separate maps
- **Packed**: uses the one packed texture
- **Difference**: turn it on with the **Difference** button

The two pictures should look the same. The Difference picture works like a warning light:

| Color | Meaning |
|---|---|
| **Black** | Identical. Good. |
| **Yellow** | Small difference |
| **Red** | Big difference. Something is wrong. |

A few yellow dots on edges is normal. Large red areas are a problem.

Other controls:
- **Refresh** rebuilds both pictures after you change the graph.
- The dropdown (Lit, AO, Smoothness, Metallic, Normal, Base Color) shows one map at a time, so you can find which one causes a difference.
- **Tolerance** sets how much difference counts as "different".
- **Compute stats** shows numbers: *Max difference*, *mean* and *pixels above tolerance*. They should be close to 0. The background counts in these numbers, so they look slightly better than they would for the object alone.
- Drag to rotate the preview, scroll to zoom.

Compare needs a graph that has an **OSM Combiner** (the Separate Maps template). It does not measure memory, does not compare against URP Lit, and does not simulate texture compression.

## 7. Node reference

**Output**
- **OSM Lit**: the material. Inputs: Base Color, Normal, OSM, Emission, Height (parallax) and Alpha (for clipping). Options: Two Sided, Alpha Clip, Cutoff.

**Inputs**
- **Base Color Texture**: color and alpha, with tint, tiling and offset
- **Normal Map**: strength, tiling, offset, and *Encoding* (Auto, Unity Normal Map, Raw RGB). Leave it on Auto if the texture is imported as a Normal Map.
- **AO Map**, **Roughness Map**, **Metallic Map**: grayscale maps
- **Height Map**: for parallax
- **Emission Map**: glowing areas
- **OSM Texture Input**: a texture that is already packed (R = AO, G = Smoothness, B = Metallic)

**Processing**
- **Roughness to Smoothness**: turns roughness into smoothness (1 minus roughness). The **Invert** switch turns this off if your map is already smoothness.
- **Normal Convention Converter**: flips the X (red) or Y (green) of a normal map. Flipping Y fixes normal maps made for a different program.

**Packing**
- **OSM Combiner**: combines AO, Smoothness and Metallic into one OSM value. Missing inputs use defaults (AO 1, Smoothness 0.5, Metallic 0).

**Utility**
- **Split Channels**, **Combine Channels**, **One Minus**, **Multiply**, **Add**, **Lerp**, **Constant**

Each node has a help text, and nodes show warnings about common mistakes.

## 8. Bake window settings

| Setting | Meaning |
|---|---|
| **File name / Folder** | Where the image is saved (inside `Assets`) |
| **Size policy** | Fixed size, match the largest input, or match the first input |
| **Bit depth** | 8-bit is normal. 16-bit is experimental. |
| **Generate mipmaps** | Keep on for 3D objects |
| **Compression** | Uncompressed, Compressed (High Quality) or Compressed |
| **Filter / Wrap mode** | Texture sampling options |
| **Add 'OSM Texture Input' node afterwards** | Adds the baked image to your graph |

The window shows the output size and any warnings before you bake. If the file already exists, it asks before overwriting.

## 9. Checking the memory saving

1. Give both versions the **same** resolution, compression and mipmap settings. Otherwise you are measuring compression, not packing.
2. Make a **development build** and open the Profiler.
3. Install the **Memory Profiler** package and take a snapshot of each version.
4. Open **Unity Objects > Material**, select each material, and compare the graphics memory it uses on its own.

In one test with a 2K model, the OSM material used about 5.4 MB less graphics memory.

## 10. Problems and fixes

| What you see | Likely cause | Fix |
|---|---|---|
| Material is **pink** | The shader did not build | Click **Generate Material** again. Check Unity and URP versions. |
| Bumps look **inside out** | Normal map made for a different program | In **Normal Convention Converter**, tick **Flip Y** |
| Too **shiny** or too **dull** | Roughness inverted twice, or not at all | Check the **Invert** switch in **Roughness to Smoothness** |
| Too **dark or washed out** | A grayscale map imported as a color image | Select the texture and untick **sRGB (Color Texture)** |
| Compare shows a lot of **red** | One of the problems above | Use the Compare dropdown (AO, Smoothness, Metallic) to find the map |
| Works in the Editor, not in a build | Shader variants or build settings | Check the player log for shader errors, and your URP and Graphics settings |
| Bake fails | No **OSM Combiner** in the graph, or a node has errors | Fix the errors shown in the graph |

## 11. Limits

- Needs Unity 6.5+ and URP 17.5+.
- Uses Unity's experimental GraphView API, which may change.
- Makes an opaque standard PBR shader only. Alpha clipping works, alpha blending does not. No detail maps, clear coat or decals.
- Tested on one model and one Windows build. Not tested on mobile or console.
- No mesh combining or atlas support.
- The 16-bit bake option is experimental.

## 12. Glossary

- **Texture**: an image used on a 3D model.
- **PBR**: realistic materials made with maps like AO, smoothness and metallic.
- **Channel**: the red, green or blue part of an image.
- **Packing**: storing several maps in the channels of one image.
- **Baking**: saving the graph's result as an image file.
- **Mipmaps**: smaller copies of a texture used when the object is far away.
- **sRGB**: a color setting. On for color images, off for data maps (AO, smoothness, metallic, normal).
- **URP**: Unity's Universal Render Pipeline.
