# Installation Guide

**You need:** Unity 6.5 (6000.5) or newer, with the Universal Render Pipeline (URP) 17.5 or newer.

## Option 1: Download ZIP (easiest)

1. Open this repository on GitHub.
2. Click **Code > Download ZIP**, then unzip the file.
3. Rename the unzipped folder (it looks like `OSM-Workflow-main`) to **`OSMWorkflow`**.
4. Move it into your Unity project's **`Assets`** folder.
   - Inside it you should see `Editor`, `Tests`, `package.json` and `README.md` directly.
5. Switch to Unity and wait for it to finish compiling (progress bar at the bottom right).

## Option 2: Unity Package Manager

1. Install **Git** from https://git-scm.com, then restart Unity and Unity Hub.
2. In Unity, go to **Window > Package Manager**.
3. Click **+** (top left) > **Install package from git URL…**
4. Paste this and click **Install**:
   ```
   https://github.com/Shashank-pix/OSM-Workflow.git
   ```
5. Wait for Unity to download and compile.

## Check that it works

1. In Unity, go to **Tools > OSM Workflow > Run Self-Test**.
2. Open the Console (**Window > General > Console**). Every line should say **PASS**.

## If something goes wrong

| Problem | What to try |
|---|---|
| Red errors about `GraphView` or `EntityId` | Your Unity is older than 6.5. This tool does not support older versions. |
| No **Tools > OSM Workflow** menu | Unity hit compile errors. Fix them in the Console, or try **Assets > Reimport All**. |
| Self-test fails on shader compile | Make sure your project uses URP (Project Settings > Graphics has a URP asset) and URP is 17.5 or newer. |
| Package Manager says `git not found` | Install Git, restart Unity and Unity Hub, or use Option 1. |
| Errors about duplicate classes | The tool is installed twice. Remove one copy. |

## Uninstall

- **Option 1:** delete the `OSMWorkflow` folder from `Assets`.
- **Option 2:** in Package Manager, select **OSM Workflow** and click **Remove**.

Materials, shaders and textures you already made keep working.

## Next

Read the [User Manual](User_Manual.md).
