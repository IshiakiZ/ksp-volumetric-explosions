# Installing

Copy `GameData/VolumetricExplosions` into your KSP `GameData` folder and start the game. To remove it,
delete that folder.

Made and tested on KSP 1.12.5 on a Mac. See [How it is drawn](How-it-is-drawn.md) before installing on Windows.

**On a Mac, one more step if you downloaded the mod with a browser.** The mod has a small library of its
own (`PluginData/vfxgrid.dylib`) that does its heaviest sums. macOS marks everything a browser downloads,
and will not let a game load a marked library without stopping to ask. So the mod does not try: it leaves
a marked library alone, says so once in the game's log, and does those sums the slow way. The picture is
the same; the frame rate while there is smoke about is lower (see [What it costs](What-it-costs.md)). To have the library
used, take the mark off that one file yourself, in Terminal:

```bash
xattr -d com.apple.quarantine "<your KSP folder>/GameData/VolumetricExplosions/PluginData/vfxgrid.dylib"
```

That mark is how macOS knows a file came from the internet, so only do this for a copy of the mod you
trust. A copy you build yourself (see [Building](Building.md)) is never marked. The log says which way it went:
`[VolumetricExplosions] grids are made by vfxgrid.dylib`, or `... by the mod's own code` with the reason.
