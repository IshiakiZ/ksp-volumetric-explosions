# How it is drawn, and where

The mod's four shaders (the smoke, the one that puts it on the screen, the burn mark and the shock
front) are OpenGL text, packed into bundles without the Unity editor (see `tools/shaderpack`).

* **Mac and Linux:** the game runs on OpenGL: smoke is drawn as a volume, burn marks are thrown onto the
  ground, and shock fronts bend the picture. (Made and tested on a Mac only.)
* **Windows:** the game runs on Direct3D 11 by default, which needs the shaders in a compiled form this
  mod does not ship. There the same simulation is drawn as thousands of soft sprites instead, each lit
  from the same 3D grid, burn marks are flat sheets, and a shock front is a faint white ring. Starting the
  game with `-force-glcore` should give the OpenGL drawing on Windows too. A Unity project for building
  the shaders for Direct3D is in `tools/unityshaders`; it has not been built, and it is behind: it knows
  only the smoke and the burn mark as they were before the smoke was last reworked. **None of this has
  been run on Windows**: there was no Windows computer to try it on.

The smoke is drawn with half as many pixels each way as the screen and then enlarged (`half` in the
settings). Enlarged plainly it would smear over the outline of anything standing in it, so each small pixel
stops at the nearest or the farthest of the things behind it, turn about, and each pixel of the screen
takes its smoke from the small pixels that stopped where its own surface is. ("Where its own surface is"
allows for a surface that slopes away from the camera, as level ground does seen from low down.)

A ray is walked through the smoke in steps about as long as a pixel is wide out there, but never longer
than half a cell of the grid, so a cloud far away is walked as finely as its grid is; and where a ray
has to take longer steps to get through a great depth of smoke, the billows there are read more coarsely
to match.

Three things are left out of that walk because they cannot change the picture. Clear air is crossed in
leaps: the mod works out, for every cell of a coarser grid, how far it is to the nearest cell with any
smoke in it, and a ray in clear air goes that far at once without looking. A set of billows that counts for
nothing at the moment is not read. And the small billows are not read where the big ones have already cut
the smoke right away, which in the thin skirts of a cloud is most of the time. Of the two cameras the game
draws a flight with (one for what is within 400 m, one for what is beyond), a cloud is drawn only by those
it reaches into.

And one thing is left out that can change the picture, by less than can be seen: smoke too thin to show.
A cloud is wrapped in haze thinner than anything visible, and a ray used to be walked through all of it,
billows and all, to add up to nothing. Now each ray keeps count of the most that the smoke it has passed
over could have hidden, and until that comes to `thin` 255ths of what is behind (2, as installed) the smoke
is passed over; from there on everything counts as before, and flame always does. `thin = 0` walks all of it.

The game's log says which is in use: `[VolumetricExplosions] smoke is drawn as a volume`, or
`... as sprites` with the reason.

**The grids are made in C** where the mod's library can be used: on a Mac (see [Installing](Installing.md); the library is
built for Intel processors, which is what the game itself is built for, and Apple's own processors run
both the same way). The mod knows how to load one on Windows (`PluginData/vfxgrid.dll`) and on Linux
(`vfxgrid.so`) as well, and `OTHERS=1 ./build.sh` makes both where it finds a compiler that can (Zig, or
MinGW for Windows alone). **Neither has ever been run, so neither is in the download**: a library that goes
wrong takes the whole game down with it, which the mod's own code cannot do. Wherever the library is
missing the same sums are done in C#, as they always were, spread over the processor's cores; the picture
is the same either way, and only the frame rate differs.

While there is smoke or a burn mark in view, the mod asks the game's camera to keep a depth picture of
the scene (the game's own light cones at the space centre do the same at night). That is how the smoke
knows what is in front of it.
