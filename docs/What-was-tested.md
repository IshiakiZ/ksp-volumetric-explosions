# What was tested

All on KSP 1.12.5 (Steam, macOS, OpenGL 4.1) on one machine, with Scatterer, EVE, TUFX and Waterfall
installed, by setting explosions off with development commands and recording frames. Everything below
was done on 2026-10-07 with the mod as it is now, and the game's log had no error from any of it:

* Kerbin, on the launch pad by day: 400 kg and 4 tonnes of fuel and oxidiser; solid propellant,
  monopropellant, a battery bank, a xenon tank and fuel alone; a slow crash; a part burning up in the air.
* Kerbin by night; at the sea's surface, and 3 m under it (2.3 km east of the pad).
* Duna (thin air without oxygen), the Mun (no air) and orbit round Duna.
* A 20-part, 22-tonne rocket and a 51-tonne one destroyed part by part through the game's own explosion
  call, which is the path a real crash takes; and two real crashes, a rocket flown into the ground under
  power at 763 m/s and at 480 m/s.
* Explosions beside a rocket in flight: at 230 m/s (1.5 km up), 330 m/s (5 km), 480 m/s (9 km) and
  800 m/s (26 km, where the game moves the world past the ship instead of the ship through the world),
  that last one with a crew aboard and the portraits showing; and the rocket itself destroyed at
  900 m/s, 31 km up.
* The camera drawn in from 110 m to 14 m and out again, and out from 60 m to 1.5 km and back, across
  the distance (400 m) at which the game changes from its near camera to its far one.
* A rocket launched up past a hanging cloud of smoke.
* That evening, with the ground taken as it really lies: 400 kg fires on the long ramp of the launch pad,
  in the middle of it and three metres from its side wall (with the ground taken the old way and the new
  in turn: the old way the smoke stops level with the top of the wall, the new way it comes over the edge
  and lies on the ground below); on a mountainside sloping at 18 degrees, a 400 kg fire, the same in
  still air for forty seconds, and a dry crash at 70 m/s (the low smoke left by it moved some twelve
  metres downhill in fifteen seconds); the pad scenes, the sea, Duna, the Mun, Laythe and the night again;
  the 22-tonne rocket destroyed part by part; a real crash at 769 m/s, which happened to come down on a
  hillside (its cloud lies along the slope); explosions in flight at 1.5 km and at 26 km. The lattice
  agreed with a careful sounding of each of its 4,225 points everywhere it was checked (but for the two
  points on the pad that a thin mast had been taken out of, on purpose), and the library and the C# made
  the same grids on flat ground, on the ramp and on the mountainside (at most 1 part in 65,535 apart, in
  10 to 310 cells of 262,144).

What those turned up, and what was done about it:

* **From further than about 400 m the smoke was speckled, with a ragged outline.** That far off it is
  the game's far camera that draws it, which has none of TUFX's smoothing between frames to hide two
  things: steps through the smoke that grew with distance without limit (6 m long from 1.5 km), each
  pixel starting somewhere else along its first, and an enlargement from half size that, over ground
  seen at a slant, was no enlargement at all but squares two pixels wide. Both mended.
* **From close by, windows clean through the smoke with hard edges.** A ray that began in the thin skirts
  of a cloud used up all its steps there and never reached the body of it. Mended; and where a ray must
  lengthen its steps, the billows there are now read more coarsely to match, instead of being caught or
  missed by chance (which showed as speckle, and as rows of dots along thin walls of smoke).
* **In thin air a blast was a scatter of separate balls**, not a cloud: the same number of puffs, thrown
  nearly twice as far. Each now spreads as much wider on the way, and no thicker for it.
* **The fire of something that blew up at speed** left its light behind as a glowing ball where the
  blast had been; the light now goes with the fire. And a cloud flying fast used to stand still between
  one grid and the next and then leap; its box now goes along with it.
* **In the first half second of a very hard crash** the top of the dust was cut off flat, the grid not
  reaching that far up. Mended.
* The development command that sets test explosions off took "east" to mean west. Mended. (So wherever
  an earlier note says something was tried at sea, it may not have been.)

**Steadiness** (2026-10-07, a 400 kg fuel fire watched from 70 m by a still camera, the game running
normally and every frame it drew kept, 39 to 47 a second, for 22 seconds; four runs): from one frame to
the next the picture changed, on average, by 0.49 to 0.59 (of 255) from 2 to 6 seconds after the blast,
while the fire still burns and flickers, and by 0.45 to 0.49 after that. The worst single frame was 0.90,
in the first six seconds of one run; in the other three runs no frame changed by more than 0.65. The day
before, one run of the smoke as it then was gave 0.46 to 0.50 and a worst frame of 0.59: no steadier,
no less steady, within what one run differs from the next. All of this is with TUFX's own smoothing
between frames switched on, as it is by default.

**Without TUFX's smoothing between frames** (2026-10-07, the same measure, with the mod as it is now):
0.64 from 2 to 6 seconds, 0.59 to 0.60 from 6 to 12, 0.57 to 0.58 after, and the worst frame 0.79 to 0.81;
with the smoothing on, in two runs made the same afternoon, 0.53 to 0.54, 0.48 and 0.45, worst frame 0.64
to 0.71. The hundredth of the picture that flickers most does so by more than 2.3 to 2.8 without it and
1.2 to 1.4 with it. So the smoothing hides about a fifth of the flicker, and half of the worst of it.

**How evenly the shape of a cloud changes** (2026-10-07): with a cloud stopped where it was, so that
nothing changed but the billows taking turns, the picture's rate of change was followed for nine seconds
in tenths of a second. With two sets of billows to a size it rose and fell by a factor of 1.9, every 1.4
seconds. With three it stays between 9.8 and 10.8 (a factor of 1.1), with no beat in it.

**How long billows are kept** (the evening of 2026-10-07; a 1.5-tonne fuel fire on the launch pad, with
the wind made the same for every run: 3.5 m/s, or none). Two things were measured, each with all the
smoke changing its billows every 2.8 seconds (which is what the mod did from four seconds after a blast
on) and with each bit of smoke keeping them for its own length of time, in the same build:

* *How fast calm smoke changes shape.* The particles were stopped where they were, so that only the
  billows taking their turns changed the picture, and the picture compared with itself half a second
  later (the mean change of a pixel with smoke in it, in 255ths, less the grain of the recording, which
  by itself comes to about 0.4). 35 seconds after the blast in the wind: 3.8 before, 1.1 now. In
  still air: 3.6 before, 0.7 now; and at 60 seconds 3.6 before, 0.6 now. So smoke drifting on a light wind
  changes shape about three times more slowly than it did, and smoke hanging in still air five to six
  times. (At 40 seconds, in the wind, 56% of the puffs were keeping their big billows for 9.5 seconds and
  43% for 19; in still air 29% for 19 seconds and 70% for 38.)
* *How far billows are drawn out.* For every cell of the grid with smoke in it, how many times longer a
  lump of the big billows' pattern lay over the smoke than it should was worked out from where the smoke
  there "was". Before: more than twice as long for 14% of the smoke 4 seconds after the blast, and for 39%
  of the fastest tenth of it; at 6 seconds 5% and 17%; from 9 seconds on, next to none. Now: 2% and under
  1% at 4 seconds, 3% and under 1% at 6. **But it is the other way round later:** while the smoke is
  settling on to longer times, some of it shows billows drawn out more than twice that would not have
  before: 5% of it at 9 seconds, 10% at 14, 2% at 25, 1% at 40 (in still air, from 25 seconds on, under
  1%). That is the price of keeping them longer. Letting the air pull them only two thirds as far before
  they are replaced was tried: 6% at 14 seconds, but at 40 seconds in the wind nine tenths of the smoke
  was then keeping its billows for 9.5 seconds and almost none for 19.
* How evenly a cloud changes shape is as it was: the test above ("How evenly"), run again that evening,
  gave 6.1 to 7.9 with each bit of smoke keeping its own time and 5.9 to 7.9 with all of it keeping the
  same, and a faint beat in both (the rate of change repeats itself by 0.3 at most, where 1 would be a
  perfect beat).
* How jerkily the picture changes from one frame to the next is as it was too (how far each frame is from
  half-way between its two neighbours, in a recording of every frame of the fire from 2.5 to 9 seconds:
  0.53 and 0.54 now, 0.52 before, of which 0.31 is the grain of a picture in which nothing moves).

Not judged by eye by anyone but the mod's author, in still frames: whether the quicker changing of the
fastest smoke in the first seconds looks right in motion is for whoever plays it to say.

**That the short cuts in the walk change nothing** (2026-10-07). The smoke was drawn twice in the same
frame, into two pictures, with the short cuts and without, and the two compared pixel by pixel: 33 frames
from twelve kinds of scene (a 4-tonne fire from 70 m and from inside the cloud, a 400 kg fire from 40 m,
fires seen from 380 m, 900 m and 2.6 km, at night, solid propellant, a crash thrown along at 220 m/s,
Duna, the Mun, orbit). With both ways in one shader program, so that the graphics card rounds the same sums the same
way, **not one pixel differed in any of them**. Between the shader as it ships and the one before it,
which are two programs, up to 1 pixel in 100 of the smoke differs, by at most 0.015 where white is 1:
which is also what the old shader differed from itself by when it was built into two programs (up to 1
in 170, by at most 0.008). That much is the graphics card's own rounding.

**That the library makes the same grids as the C#** (2026-10-07, and the development build can repeat it
at any time: `dev_fx_call {method: NativeCheck}`). One grid made both ways from the same particles, at
four moments of a 4-tonne fire: the smoke, the flame and the smoke towards the sun differ by at most 1 in
65,535, in at most 425 of the 262,144 cells; the light of the sky, the heat and the smoke round about
differ nowhere, or by 1 in 255 in one or two cells; which way the smoke is going and the map of clear air
differ nowhere; where the smoke "was" differs by at most the last place of the number handed over. (The same
sums, but the game's runtime carries some of them at higher precision on the way, so the last place can
round differently.) The library was also run on made-up clouds with deliberately wild particles (no
number at all, infinities, ones a million kilometres off) under the compiler's checks for stepping
outside its memory: none.

**Eve, Laythe and Jool** (2026-10-07; a lander set down there with a development command). On Eve by day
(air 4.9 times Kerbin's at sea level): a 400 kg and a 4-tonne fuel fire; the blast is stopped short and
the fireball goes up as a tight mushroom. On Laythe (0.6): a 400 kg fire on a hillside. Nothing wrong
was seen in either and the log was clean. In Jool's upper air, 130 km up (0.024): the fireball and the
burning pieces look right, but see [Limits](Limits.md) for what the old smoke does.

A fault found on 2026-10-06: every few frames the game draws the kerbals' portraits in the middle of the
frame, and the mod took that for the frame's real drawing. With a crew aboard, clouds were then put where
the world had been a frame earlier, every few frames. Fixed. At 800 m/s with a crew aboard, a fire
followed frame by frame kept its place to within what the game's own physics steps allow (the ship
moves one or two of them to a frame, and everything that stands still in the world is seen to move
unevenly by as much).

**Solid things** (2026-10-07): with a fire set right beside a rocket on the pad, 1 or 2 of about 5,600
sampled smoke particles lay inside something solid with `collide` on, against 3 to 8 with it off. The
obstacles there are thin; it has not been tried in or against a building.

Not tested: Linux; Windows in either mode; other graphics cards; Parallax; a Mac with an Intel processor;
very long flights; anything without the four visual mods since the smoke was reworked. After the changes
of the afternoon of 2026-10-07 (three sets of billows, the short cuts, the library) these were gone
through again with the mod as it then stood, and the log was clean each time: the pad by day (400 kg and
4 tonnes, from 70 m, 28 m and 140 m) and by night, the kinds of propellant, the sea, Duna, the Mun, orbit,
the zoom out to 1.5 km and back, the 22-tonne rocket destroyed part by part, a rocket flown into the
ground at 758 m/s, and explosions beside a rocket in flight at 1.5 km and at 26 km (800 m/s), with the
rocket itself destroyed at 31 km (904 m/s). Not again: the 51-tonne rocket, the zoom in to 14 m, the
rocket launched past hanging smoke.
