# What it costs

Measured on an Apple M5 Pro at 1280 x 720 on 2026-10-07, with Scatterer, EVE, TUFX and Waterfall installed
(they alone took the game from about 115 frames a second to about 79), with a 4-tonne fuel fire (8,000 to
12,000 particles) 30 m from the ship and the camera 70 m from the ship, so that the smoke fills half the
view and the camera is in the edge of it. Frame times are means over 2.5 seconds, taken 2.5 seconds after
whatever was changed (see the last point for why):

* **With nothing going on: 12.6 to 13.1 ms a frame** (76 to 79 frames a second).
* **With the fire, as the mod is now** (that evening, two runs): **17.8 ms** from 2 to 7 seconds after
  the blast (56 frames a second), **15.7 to 16.4 ms** from 7 to 17 seconds, **14.0 to 14.6 ms** from 17 to
  27, and **13.4 to 14.3 ms** after that (70 to 75 frames a second, against 79 with nothing going on).
* **With the fire, as the mod was that afternoon** (the grids already made in C, the short cuts in the
  walk through the smoke in): 17.9 to 18.2 ms in its first twenty seconds and 16.1 to 17.1 ms after. Half a
  millisecond of what was saved since is the looking ahead (below); where the rest of the later seconds'
  saving comes from was not tracked down, and the ground as it really lies (see [What the smoke does](What-the-smoke-does.md)),
  switched against the one flat plane it used to be taken as in the same fire, makes no difference that
  can be measured (0.1 ms either way).
* **With the fire, as the mod was early that afternoon** (the three sets of billows already in, the
  grids made in C#, the walk through the smoke without its three short cuts), switched back and forth in
  the same fires: **21.6 to 22.6 ms** (44 to 46 frames a second).
* Where the difference was. **Making the grids in C#** is 75 to 115 ms of processor time for every grid,
  twenty-five to thirty grids a second, and to keep up it was spread over every core the processor has.
  The work is not on the game's own thread, but spread out like that it slowed the game's own threads by
  4.5 to 5.8 ms a frame: more than all the drawing. (Held to four threads it cost the same; to two, 2 ms
  less; to one, 3.5 ms less, but then each grid took twice as long to arrive.) **In C the same sums take
  11 to 16 ms on one thread**, and cost 1 to 2 ms a frame, some 0.5 ms of that for handing the grids to
  the graphics card. Much of the C# time, it turned out, was not the sums but where the numbers lay in memory
  (twenty-two grids side by side, read a cell at a time from each in turn, kept pushing one another out of
  the processor's nearest memory: the C keeps each cell's twenty-two numbers together).
* **Drawing the smoke** is 3.2 to 3.6 ms a frame in the first seconds of that fire and 2.1 to 2.5 ms later.
  Nearly all of it is the walk along each ray, and nearly all of that is reading the billows: in one such
  frame the rays of the half-size picture took 20 million steps between them, 12 million of them in smoke,
  at eleven looks into textures each for the billows alone. The three short cuts (see [How it is drawn](How-it-is-drawn.md)) save about 0.5 ms a frame of this. The picture they give is the same; see [What was tested](What-was-tested.md).
  (The third set of billows to each size, put in the same day to make a cloud change shape evenly, is what
  made it eleven looks and not seven: by a measurement of that morning, taken the quick way, drawing had
  been 1.8 to 2.3 ms a frame.)
* **Passing over smoke too thin to see** (`thin`, that evening) saves less than was hoped: 4 to 5% of the
  steps of the walk with `thin` at 1, 5 to 8% at 2 and 10 to 16% at 8, which in this fire is 0.1 to 0.3 ms
  a frame. Most of the walk's steps turned out to be in smoke thick enough to show. (At 1, against the full
  walk in the same frozen frame: the picture differs by 0.05 to 0.08 of a 255th on average, and 0.1 to
  0.7% of the pixels with smoke in them lose a trace of haze.)
* **Each bit of smoke keeping its billows for its own length of time** (that evening; see [What the smoke does](What-the-smoke-does.md)) costs nothing that could be measured. The walk reads one small texture more for each step in
  smoke (twelve looks for the billows, not eleven): switched off and on three times in one settled fire, the frame
  time differed by -0.12, -0.06 and +0.14 ms, which is what one such switch differs from the next by. The
  library's part of a grid that this adds to took 4.1 to 4.3 ms for 6,000 particles before and after
  (twenty-nine numbers a cell now, not twenty-two). Moving the particles, on other threads, takes 0.8 to
  0.9 ms for 8,000 to 12,000 of them.
* The game's own thread spends **0.35 to 0.9 ms a frame** on the mod (2,000 to 18,000 particles). It was
  1 to 2 ms: more than half of that was the looking ahead, the thousand or so collision queries a frame by
  which smoke finds what it is about to run into. Setting them up took 0.65 ms; it takes 0.14 ms now that
  each is worked out number by number with the mod's own quick square root (the runtime's own costs a
  tenth of a microsecond a call, as this game runs it) and the list is handed to the game whole instead of
  one at a time. The same queries are made as before.
* Sounding the ground costs the game's own thread about 0.1 ms each time (4,225 queries set going; they
  run on its worker threads, and the lattice is made of what comes back on another thread again, 0.3 to
  1 ms there), and happens when a cloud outgrows or drifts off its lattice and every ten seconds besides.
  Made on the game's own thread, as it was first written, the lattice took 2.5 ms: one frame held up each time.
* The first two seconds of a blast are dearer than the fire after it, and were not gone into.
* **Measuring this is easy to get wrong.** Switched off for less than a second, the making of the grids
  seemed to cost 0.3 ms a frame; given two seconds to settle, 5 ms. What costs is not the work but what the
  system does with the processor's cores while it goes on, which takes a second or two to change. Earlier
  figures in this file and in `TODO.md` that put the grids at "about 5 frames a second" were measured the
  quick way and were too low.
* Without the four visual mods none of this has been measured.
* Lower `quality` if it is too heavy: it thins the particles and shortens the walk through the smoke.
