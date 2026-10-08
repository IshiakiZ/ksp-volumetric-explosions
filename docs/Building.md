# Building

`./build.sh fx` compiles the plugin and its library and installs both. The library (`Native/vfxgrid.c`) is
built with the system's C compiler (on a Mac, the Xcode command line tools); without one the mod is built
without it and says so. The C follows the C# in `Site.cs` function for function, and the C# is still what
runs wherever the library is not: **whatever is changed in one has to be changed in the other**, and
`dev_fx_call {method: NativeCheck}` in the development build shows whether they still agree. The textures, and the cube of noise the billows are
cut with, are made by `tools/fxbake` (`dotnet run -c Release --project tools/fxbake --
src/VolumetricExplosions/Textures`). The four shader bundles are made by `python3
tools/shaderpack/make_bundle.py <KSP folder> src/VolumetricExplosions/Shaders/<name>.bundle <name>` for
`volume`, `enlarge`, `mark` and `shock`, which needs a copy of the game because it uses one of the game's
own shaders as its pattern. `tools/unityshaders` is for building shaders for Direct3D in the Unity editor;
it is behind (see [How it is drawn](How-it-is-drawn.md)).

The bundles hold this mod's own shader text and Unity's description of how a shader is laid out in a file
(which every bundle made by Unity carries); nothing of the game's own shader is left in them.

This repository is made from a workspace that holds this mod beside others; changes arrive here from there.
The development build spoken of above, and the commands whose names begin `dev_`, belong to that workspace
and are not in this repository. Here `./build.sh` builds and installs the mod (`./build.sh fx` is the same
thing), `./build.sh check` only compiles, `./build.sh shaders` packs the four shaders again and
`./build.sh package` makes the download. `OTHERS=1` in front of any of them also builds the library for
Windows and Linux, which nobody has run yet.
