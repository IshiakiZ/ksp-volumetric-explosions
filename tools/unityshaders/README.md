# Building the shaders in the Unity editor

Volumetric Explosions has two shaders of its own: the smoke (a ray walked through a volume) and the burn
mark (thrown onto the ground). On the Mac and Linux versions of the game, which run on OpenGL, they are
packed by hand from GLSL text (`tools/shaderpack`), and that is what the mod ships. The Windows version
runs on Direct3D 11, which wants compiled bytecode that only the Unity editor can make. This folder is a
Unity project that makes it.

**State: not yet built or run.** The shaders here were written without a Unity editor to hand, and a
Direct3D build cannot be tested on a Mac at all. Expect to fix compile errors the first time.

## What is needed

* Unity **2019.4.18f1** exactly (the game's version; bundles from other versions do not load), with
  "Windows Build Support (Mono)" added. On an Apple-silicon Mac the editor runs under Rosetta.
* A Unity licence activated on the machine (the free Personal one will do). Activating it means signing
  in to a Unity account.

## Building

    python3 tools/unityshaders/port.py        # only after changing tools/shaderpack/volume.glsl
    "/Applications/Unity/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -quit \
        -projectPath tools/unityshaders -executeMethod BuildShaderBundles.All -logFile -

That writes `tools/unityshaders/Bundles/shaders-windows.bundle` (and `-linux`, `-mac` where the editor
has those build supports). Copy `shaders-windows.bundle` to
`GameData/VolumetricExplosions/PluginData/`: on Direct3D the mod looks for it there and, finding it,
draws smoke as a volume and throws burn marks onto the ground, as on the Mac. Without it, it falls back
to sprites and flat sheets as before.

`Volume.shader` is generated from the GLSL by `port.py`; `Mark.shader` is short and kept in step by hand.

## Behind

Since 2026-10-06 the mod has four shaders, not two, and the smoke's has changed: it is drawn at half
size (reading single pixels of the camera's depth picture), carried on between grids, and reads six more
textures of where the smoke "was". This project still has the smoke and the burn mark as they were before
that, and nothing for the enlarging shader (`tools/shaderpack/enlarge.glsl`) or the shock front
(`shock.glsl`). `port.py` needs teaching the new things before it can make `Volume.shader` again, and the
two new shaders need writing here. The mod does not look for either of them in a built bundle yet. The changes of 2026-10-07
(steps no longer than half a cell, billows read more coarsely where steps are lengthened, the enlargement's
allowance for sloping surfaces) are not here either; nor are that afternoon's: three sets of billows to a
size read from five textures of where the smoke "was" (`_RestA` to `_RestE`, with `_VolRest` and
`_VolRest2` for what each set counts for), the map of clear air the walk leaps by (`_Clear`, read cell by
cell with no smoothing between cells), and `_VolDrawn` in the enlarging shader, by which a cloud is left
out for a camera it does not reach into.
