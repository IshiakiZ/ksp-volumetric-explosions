# Shock fronts debris and burn marks

## The shock front

A blast in air sends out a shock front: a shell of squeezed air that leaves the fireball faster than
sound and slows to the speed of sound of the air it is in. Nothing is drawn of the shell itself. It bends
the light that passes through it, so what lies behind it ripples as it goes over: a ring round the blast
that sweeps outward across the view, and a second ring racing along the ground. Whatever stands in front
of the shell is left alone. When the front reaches the camera the camera is jolted and the whole picture
swells for a moment and springs back. Small blasts make none, and there is none in a vacuum.

It travels at the real speed, so from 70 m it has passed in a fifth of a second; from further off you
see it cross the ground towards you. `bend` in the settings makes it stronger or weaker, and `shake`
turns off what it does to the camera.

**The bang comes with it** (since 0.3.2). The game starts an explosion's sound on the instant, however far
off it is. The mod holds it back for as long as sound takes to reach the camera in the air of the place:
the flash at once, the bang a second later for every 340 metres at sea level on Kerbin, and later than
that in thin air high up. Nearer than 20 metres it is left as the game has it, and so it is where there is
no air: the game plays its bang in a vacuum, and that is not this mod's to take away. `sound_travels` in
the settings turns it off. Measured on 2026-10-08: blasts 25, 311, 600 and 1727 metres from the camera had
their sound started 0.08, 0.93, 1.77 and 5.12 seconds after the flash. What it sounds like has not been
listened to: the Mac it was made on had its sound off.

## Pieces of the ship

When a part is destroyed, pieces of that part may be thrown out: patches of its own skin, torn off
where its triangles meet, tumbling, falling and bouncing. How many, how big and how fast depends on
what happened. A fuel tank that burst is torn into small panels and flung, some of them burning and
trailing smoke, all of them scorched and glowing hot for a few seconds. A part that crumpled in a slow
crash leaves a few large pieces where it lay; one that hit at speed sprays small ones ahead of it. A
part that burned up mostly leaves nothing. Small parts often leave nothing either.

The pieces are drawn but are not solid. Nothing in the game can be hit or damaged by one.

## Burn marks

A fire on the ground leaves a mark: charred black in the middle, soot fading out around it, thin
streaks thrown much further, and embers that glow for the first few seconds. It is drawn out along the
direction of a crash and fades over five minutes.

The mark is not a sheet laid on the ground. It is thrown onto whatever is there, pixel by pixel, so it
follows ramps, steps and roofs exactly and cannot float over a slope or sink into one. (That is on OpenGL;
see [How it is drawn](How-it-is-drawn.md). Elsewhere it is a sheet dropped onto the ground from above, which cuts corners.)
