#ifdef VERTEX
#version 150
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
uniform vec4 _VolShift;      // xy: for a patch walked a quarter of its pixels a frame, how far its picture is moved (-1 to 1 each way, as the camera's projection has it; see below); nought otherwise
in vec3 in_POSITION0;
out vec3 vs_TEXCOORD0;
out vec4 vs_TEXCOORD1;
void main()
{
    vec4 world = hlslcc_mtx4x4unity_ObjectToWorld[0] * in_POSITION0.x + hlslcc_mtx4x4unity_ObjectToWorld[1] * in_POSITION0.y + hlslcc_mtx4x4unity_ObjectToWorld[2] * in_POSITION0.z + hlslcc_mtx4x4unity_ObjectToWorld[3];
    vec4 clip = hlslcc_mtx4x4unity_MatrixVP[0] * world.x + hlslcc_mtx4x4unity_MatrixVP[1] * world.y + hlslcc_mtx4x4unity_MatrixVP[2] * world.z + hlslcc_mtx4x4unity_MatrixVP[3] * world.w;
    // The far faces of the box are what is drawn, and they must not be cut off by the far end of the camera's
    // range (the game's near camera stops at 400 m): the smoke nearer than that still has to be drawn there.
    // Its depth is not used for anything, so it is simply held inside the range.
    clip.z = min(clip.z, clip.w * 0.99999);
    vs_TEXCOORD0 = world.xyz;
    vs_TEXCOORD1 = clip;
    // (A patch walked a quarter of its pixels a frame, the rest carried over from the frame before (Layers; carry.glsl), is drawn
    // into a picture half as wide and tall as its slot, each pixel of it the one of a two by two of the slot's walked this frame:
    // the picture moved by part of a pixel, so that each pixel's middle is on that one's. Where the game draws into its pictures
    // upside down (Direct3D), Layers turns the move up and down over.)
    gl_Position = vec4(clip.xy - _VolShift.xy * clip.w, clip.zw);
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_explicit_attrib_location : require
// Smoke and fire as a volume, the way Counter-Strike 2 draws its smoke grenades. The box this is drawn
// on holds grids made by the mod from its particles: how much smoke and flame there is at each place,
// how much light reaches it (from the sun, the open sky and the fire) and how hot the flame is, and (more
// coarsely) how much smoke there is round about, what colour it is, which way it is moving and where it
// has come from. For each pixel a ray is walked through the box from the camera, a step at a time. At each
// step the edge of the smoke is cut into billows by cellular noise, which is what turns a smooth blob into
// something like smoke; the smoke takes its colour from the light there and the flame from its heat; and
// the walk ends at whatever solid thing the game has already drawn.
//
// The billows belong to the smoke. They are not a pattern standing in the air for the smoke to drift
// through (it was once: every billow was then forever being cut afresh as the smoke moved on through the
// pattern, and the whole cloud seethed). Each bit of smoke knows where it "was" a few seconds ago, and it is
// that place the pattern is read at: so a billow rises with the smoke it is made of, is carried round by
// the eddies that carry the smoke, and is drawn out as the smoke spreads. Left at that it would in the end
// be drawn out of all recognition. So there are three such reckonings, begun afresh by turns, each counting
// for nothing when it begins, for most half-way through its life and for nothing again at its end: what is
// seen is always a blend of sets of billows none of them too old, and (the three being a third of a life
// apart, and rising and falling as a bell curve does) one that changes at an even rate. How long a turn
// lasts differs from place to place: under a second where the air is pulling the smoke about, so that no
// set is kept until it is drawn out into streaks, and up to half a minute where the smoke is calm, where
// the billows it has can be kept and it hardly changes at all. (The mod sets the pace, and tells the shader
// whose turn it is at each place: see _Turns.) And there are two such threes, one for the big billows and
// one, taking turns twice as fast, for the small: a small eddy is gone long before a big one.
//
// Most of the work is the walk itself, and most of that is reading the billows. Three things are left out
// that cannot change the picture: clear air is crossed in leaps (see _Clear), a set of billows that counts
// for nothing is not read, and the small billows are not read where the big ones have already cut the smoke
// right away. (Checked against the walk without them, both in one program so that the same sums are rounded
// the same way: over a dozen kinds of scene not one pixel differed.)
//
// And one thing that can, by less than can be seen: smoke too thin to see is passed over (see _VolThin).
// A cloud is wrapped in haze thinner than anything that shows, and a ray used to be walked through all of
// it in fine steps, billows and all, to add up to nothing. Now each ray keeps count of the most the smoke
// it has met so far could have hidden, and until that comes to a trace that could be seen (two steps of an
// eight-bit picture, as installed) the smoke is passed over at the pace of clear air. From there on
// everything counts as before. So a ray that only grazes the haze costs next to nothing, a ray into the
// body of a cloud loses at most the first two 255ths of what it would have hidden (in practice far less:
// the count is of the most the smoke could hide, were the billows to gather all of it), and no ray loses
// more. It is a small saving, not a large one: counted in a four-tonne fire, a twentieth of the walk's
// steps. Most of the walk is through smoke that shows.
//
// The picture must not depend on where exactly the steps fall, or it shimmers whenever they move: when
// the camera moves, or when the cloud outgrows its grid. So the steps come at fixed distances from the
// camera that have nothing to do with the grid, and the noise is only ever read as coarsely as the steps
// are long: detail finer than a step cannot be drawn, only guessed at differently each time.
uniform vec3 _WorldSpaceCameraPos;
uniform vec4 _ProjectionParams;
uniform vec4 _ZBufferParams;
uniform vec4 hlslcc_mtx4x4unity_WorldToObject[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixV[4];
uniform vec4 _VolParams;     // x: how much a metre of the thickest smoke blocks, y: how many steps may be spent on smoke, z: how deep the billows are cut into the edge of the smoke (0 to 1), w: how much a metre of the thickest flame blocks
uniform vec4 _VolCamera;     // x: 1 if the camera drawing this has a depth picture of the scene, y: the angle one of its pixels covers (both set for each camera)
uniform vec4 _VolStep;       // x: length of the finest step in metres, y: how hard the smoke's cut edge is (0: it comes on gently, as until 2026-11-16; 1: at once), z: how wide the band it comes on over then is, as a share of the smoke's whole thickness, w: how much paler dark smoke is where it is thin
uniform vec4 _VolGrid;       // x: fine steps to a step through clear air (a power of two, about a cell of the grid), y: how many of the screen's pixels wide a step may look, z: cells of the detail to a metre, times how many of the screen's pixels the smallest lump drawn should cover, w: (for testing) 1: the smoke on the grid alone, 2: with its puffs but unlit, 3: lit but without puffs
uniform vec4 _VolSize;       // xyz: size of the box in metres
uniform vec4 _VolOffset;     // xyz: the middle of the box, in metres from the site's origin
uniform vec4 _VolDetail;     // xyz: how far the billows' pattern had been carried along with the smoke as a whole when the grid in use was made, in metres, w: repeats of the pattern per metre
uniform vec4 _VolFlow;       // x: twice how far the fastest smoke on the grid has gone since the grid was made, in metres, yw: what turns a number read from the textures of where the smoke "was" into metres (times y, plus w), z: the farthest the smoke is carried on from where the grid has it, in metres
uniform vec4 _VolPeak;       // x: 1 if this is drawn at half size, to be enlarged, y: how smooth the flame is (0: licked into tongues by the billows, as a fire in the open is; 1: not at all, as an engine's jet in a vacuum), z: (for testing) steps moved along by this part of a step, w: metres of the thickest smoke that count as thick
uniform vec4 _VolThin;       // x: how much of what is behind it a ray's smoke may hide, at the very most, before any of it is drawn (nought: all of it is drawn), y: how long ago the grid in use was made, in seconds, w: a nuclear explosion's cap: how far the daylight on its cloud is put out where its gas still glows (nought: not one)
uniform vec4 _VolSun;        // rgb: sunlight, w: how much thin smoke in front of the sun shines
uniform vec4 _VolSunDir;     // xyz: towards the sun
uniform vec4 _VolSunLocal;   // xyz: the same, in the site's own axes (east, up, north), w: how strongly the billows shade each other
uniform vec4 _VolAmb;        // rgb: light from the sky
uniform vec4 _VolUpLocal;    // xyz: straight up, in the grid's own axes; w: how far the sky's light on the smoke goes by the billows and comes in from the side (0: not at all, as until 2026-11-16)
uniform vec4 _VolSurface;    // (since 2026-11-16) the smoke's surface, see the walk: x how far the smoke is drawn as a thing with a surface (0: not at all, as until then), y the least smoke there is any of (as "fill": 0 to 1), z how deep the billows cut into it, w over how much more smoke than that least the surface comes on
uniform vec4 _VolSurfaceIn;  // x: how thick the smoke is at the least just inside its surface (so much hidden over the length that counts as thick), y: how much smoke round about there must be for a surface at all (below three tenths of it: none, the smoke thins away as before), z: the big billows' size, as so many of them to what there were until 2026-11-16 (0.6: each 1.7 times as large), w: how much the small billows count for in the billows' shade (1 until then)
uniform vec4 _VolGlow;       // rgb: light from the fire; w: the camera's shoulder, how far light over the top goes over to white (0: none)
uniform vec4 _VolSteamTone;  // (since 2026-11-20, a launch's steam by night) x: how much of its flame's light the steam sends on inside itself to where the straight way does not reach (0: none), y: how far that goes, as a share of the straight way's stopping, z: how much of the top of the picture's range a camera's tone curve bends, colour by colour (0: none), w: how much whiter the flame's light the straight way is than what comes round (0: none)
uniform vec4 _VolLampA;      // xyz: where the strongest fire is, in metres from the site's origin, w: its size squared;
uniform vec4 _VolLampB;      // the second
uniform vec4 _VolLampC;      // and the third
uniform vec4 _VolLamps;      // xyz: how bright each of the three is; w: which of them are fires burning in one place (1, 2, 4 added: the first, second, third)
uniform vec4 _VolLampLineA;  // (since 2026-11-20) xyz: for a lamp that is a line, a rocket's flame, how far its light comes from all along beyond where the first is, in the grid's axes (nought: a point; see lampFrom)
uniform vec4 _VolLampLineB;  // the second's
uniform vec4 _VolLampLineC;  // and the third's
uniform vec4 _VolSceneA;     // The lamps of the scene round about (floodlights, the pad's own lights, buildings' lamps), four of them: xyz where, in the grid's axes, w its reach squared (nought: none)
uniform vec4 _VolSceneB;
uniform vec4 _VolSceneC;
uniform vec4 _VolSceneD;
uniform vec4 _VolSceneTintA; // rgb: its light, as an amount of light; a: the cosine of half a spotlight's cone (below -1.5: it shines every way)
uniform vec4 _VolSceneTintB;
uniform vec4 _VolSceneTintC;
uniform vec4 _VolSceneTintD;
uniform vec4 _VolSceneDirA;  // xyz: which way a spotlight points, in the grid's axes
uniform vec4 _VolSceneDirB;
uniform vec4 _VolSceneDirC;
uniform vec4 _VolSceneDirD;
uniform vec4 _VolSceneShade; // (since 2026-11-20) the lamps of the scene's light on the smoke shaded by its billows and stopped by the smoke between, as the sun's and the fires' are: x how much of what so much smoke would stop along a straight line (0: none, lit evenly through as until then), y how much of it goes round inside the smoke, as so much of the straight way's, z how far that goes, as a share of the straight way's stopping, w how much the billows shade it (0: not at all)
uniform vec4 _VolJet;        // A ship's patch (its engines' flames): xyz which way the jets go, in the grid's axes, one long; w how far the flame's pattern has been carried down them, metres
uniform vec4 _VolJetFrom;    // xyz: the mouth of the strongest nozzle, in the grid's axes; w: its radius, metres
uniform vec4 _VolJetLook;    // x: how far the flame's edge is torn into tongues (0 to 1); y: how sooty it is; z: how strong its shock diamonds are; w: the size of its pattern, metres (nought: not a ship's patch)
uniform vec4 _VolJetBody;    // x: how much flame makes the flame's body whole (a clear, faint flame is whole where there is any of it); y: how many shock diamonds there are
uniform vec4 _VolJetFlow;    // x: how far down the jet, metres, the pattern is carried half as fast as at the nozzle and is half as drawn out; y: how far the pattern's streaks differ in heat at the nozzle, z: and far down the jet; w: 0 if the flame ends at a sharp edge where it thins, as fire does, 1 if it thins away as a glow
uniform vec4 _VolJetEnd;     // x: the length of the strongest flame, metres; y: how far its far end is broken up into tongues, whatever is there (0 to 1); z: how much cooler its far end is drawn than the gas there is reckoned (0 to 1); w: how much wider the jet gets for each metre it goes
uniform vec4 _VolJetGlow;    // x: how much a shock diamond glows of itself, whatever flame is round it: so much a metre in the jet of a nozzle a metre wide, and by so much more as the nozzle is narrower; y: how far the flame's gas is gone from sight once it has cooled (1: a clear flame's, seen by its own light alone; 0: a sooty one's, dark); z: how much further apart the diamonds stand than at the ground, w: how much wider they are
uniform vec4 _VolJetSheath;  // (since 2026-11-20) a ship's patch, its flame's outer part cooler than its core: x by how much of its heat at the edge (nought: as until then), y and z between what thicknesses of flame (see f in the walk) it goes from that to none, w how much of that there is at the nozzle, where the air has not yet got into the gas
uniform vec4 _VolJetBoil;    // (since 2026-11-20) x: how far the flame's pattern has boiled, its lumps made afresh where they are (one turn to each whole number: each copy of the pattern is new once a turn); y: how much it boils (0: not at all, as until then); z, w: from how far down the flame, as a share of its length, it begins to boil, and boils fully
uniform vec4 _VolJetAt0;     // The mouths of up to eight of the ship's nozzles, for the shock diamonds in each one's jet. xyz: where, in the grid's axes; w: how wide, a radius in metres (nought: no such nozzle)
uniform vec4 _VolJetAt1;
uniform vec4 _VolJetAt2;
uniform vec4 _VolJetAt3;
uniform vec4 _VolJetAt4;
uniform vec4 _VolJetAt5;
uniform vec4 _VolJetAt6;
uniform vec4 _VolJetAt7;
uniform vec4 _VolFire;       // A fire in the open (not an engine's): x how far its flames' pattern has risen through the burning gas, metres; y one over the size of its tongues, per metre; z how far thin flame is torn into tongues (0 to 1); w 1 to draw it so (0: as a glow, as until 2026-10-10)
uniform vec4 _VolFireGone;   // xyz: what takes where the smoke now here was when the grid was made (the grid's "was", carried on as it is since) to where its fire's pattern is read: see firePattern; w: how far its flame is carried on with its gas between grids (1 as smoke is; 0 a fire that burns where it is, fed as fast as it rises: it stands still)
uniform vec4 _VolFireWasSize;    // xyz: the size of the grid before this one, w: how far from its flame to this one's a fire that stands is shown (1: this one's alone): see the walk
uniform vec4 _VolFireWasOffset;  // xyz: the middle of the grid before this one, in the grid's own reckoning; w: (since 2026-11-16) twice how far the fastest smoke of the grid before has gone since that grid was made, metres: its smoke, and flame that is carried on, go over from it to this one's as a fire that stands does (less than nought: not so)
uniform vec4 _VolTint;       // rgb: the hue of the dust here; w: for a launch's steam, how much of its rocket's flame's light it stops on the way through it, as a share of a straight line's (nought for anything else, and for steam until 2026-11-20: see Site.SteamStops)
uniform vec4 _VolHot1;       // rgb: the colour of flame at a quarter of full heat,
uniform vec4 _VolHot2;       // at half,
uniform vec4 _VolHot3;       // at three quarters,
uniform vec4 _VolHot4;       // and at full heat
uniform vec4 _VolCutA;      // Where an engine's smoke may begin (see begun, below). xyz: a place on the line its newest smoke lies along, in metres from the site's origin, w: the square of how far out from that line this holds (nought: there is no such engine)
uniform vec4 _VolCutB;      // xyz: which way along that line is back, the way the engine came, w: over how many metres from that place back the smoke comes on
uniform vec4 _VolCutC;      // x: how far ahead of that place this holds, y: how much of the smoke there is taken away (0 to 1)
uniform vec4 _VolCutD;      // and the same for a second engine, or cluster of them, in the same patch of air
uniform vec4 _VolCutE;
uniform vec4 _VolCutF;
uniform vec4 _VolSlot;       // xy: where in the picture it is drawn into this patch's own part of it begins, in pixels (see layers.glsl: the patches drawn at half size share one picture, each in a slot of its own)
uniform vec4 _VolQuarter;    // a patch walked a quarter of its pixels a frame (Layers; carry.glsl): xy which pixel of each two by two of its slot this frame's are; z 1 while it is (each pixel drawn is then that one of its two by two)
uniform vec4 _VolHole;       // (since 2026-10-25) a rectangle of the camera's picture (0 to 1 each way, xy to zw, as the game draws into its pictures) given to a burning patch's flames, walked whole by themselves (see Layers.ForCamera, Site.FlameRect)
uniform vec4 _VolHoleWay;    // x: 0 none; 1 this walk (at half size) leaves that rectangle out; 2 this walk (whole) walks only the rectangle
uniform vec4 _VolFireLook;   // (since 2026-10-26) a fire in the open's flames (see firePattern, and the flame in the walk): x how much of a third, finer size of lump is in their pattern (0: two sizes, as until then); y how far the pattern is drawn out upward (0.62 until then: a lump half as tall again as it is wide); z half the width of the step from no flame to flame where it is torn into tongues (0.05 until then); w how much the billows cut it into tongues, against the pattern (0.4 until then)
uniform vec4 _VolFireLight;  // x: how bright a fire in the open's folded flame is (0.45 until 2026-10-26), y: how dim the creases between its billows are, against their tops (0.25 until then); z: how steeply the fires' light on the smoke round them falls off (1: as until then, as one over the square of the distance, past the fire's own size; 2 as one over the fourth power), w: how strong that light is (8 until then)
uniform vec4 _VolFireShine;  // x: how hot a fire's smoke is when it begins to shine of itself (0.28 until 2026-10-26; at full 0.27 hotter), y: how much it shines then (0.45 until then); z: how hot a fire's flame is drawn, against the heat of its gas (1 until then: gas that comes off hotter, to stand as high as a real fire's does, is drawn as it would be cooler, its foot yellow and not white); w: how much flame it takes to burn rather than be torn away (0.35 until then)
uniform vec4 _VolSunMapFrom;  // what stands between the smoke and the sun (see SunShadow.cs): xyz where the sun's-eye picture is taken from, w: one over its width, metres
uniform vec4 _VolSunMapRight; // xyz: the picture's right, as the sun sees it
uniform vec4 _VolSunMapUp;    // xyz: its up
uniform vec4 _VolSunMapAlong; // xyz: the way the sun's light goes; w: 1 if the picture is of this patch (0: nothing is looked up)
uniform vec4 _VolWakes;      // what is left where something has gone through the smoke (see carve, and Wakes.cs): x how many wakes, y seconds for a wake to fill in, z seconds the air in one goes on turning, w how much of the smoke a fresh one has pushed out of its way (0 to 1)
uniform vec4 _VolWakeLo;     // xyz: a corner of a box round every wake, in the grid's axes (nothing outside it is looked at)
uniform vec4 _VolWakeHi;     // xyz: the opposite corner
uniform vec4 _VolWakeA0;     // a wake: xyz its older end, in the grid's axes, w how long ago the thing that made it was there, seconds
uniform vec4 _VolWakeB0;     // xyz its newer end, w how long ago that was (nought while the thing is still making it)
uniform vec4 _VolWakeC0;     // x how wide it is (the thing's radius), y how far the turning air winds the smoke round it at its edge once wound, radians, z how far along it the turning goes one way and back (nought: always one way), w which way, and how much (nought: no wake)
uniform vec4 _VolWakeA1;
uniform vec4 _VolWakeB1;
uniform vec4 _VolWakeC1;
uniform vec4 _VolWakeA2;
uniform vec4 _VolWakeB2;
uniform vec4 _VolWakeC2;
uniform vec4 _VolWakeA3;
uniform vec4 _VolWakeB3;
uniform vec4 _VolWakeC3;
uniform vec4 _VolWakeA4;
uniform vec4 _VolWakeB4;
uniform vec4 _VolWakeC4;
uniform vec4 _VolWakeA5;
uniform vec4 _VolWakeB5;
uniform vec4 _VolWakeC5;
uniform vec4 _VolWakeA6;
uniform vec4 _VolWakeB6;
uniform vec4 _VolWakeC6;
uniform vec4 _VolWakeA7;
uniform vec4 _VolWakeB7;
uniform vec4 _VolWakeC7;
uniform sampler3D _Volume;  // rg: how much smoke lies towards the sun (sixteen bits in two bytes), b: how much towards the open sky, a: how hot the flame is
uniform sampler3D _Amount;   // rg: how much smoke (sixteen bits in two bytes), ba: how much flame
uniform sampler3D _AmountWas;    // the same, of the grid before this one: a fire that stands goes over from its flame to this one's (see the walk)
uniform sampler3D _VolumeWas;    // (since 2026-11-16) the light and heat of the grid before this one, as _Volume: gone over from as the smoke is
uniform sampler3D _AroundWas;    // and what was round about in it, as _Around
uniform sampler3D _RestAWas;     // and where its smoke "was" by each of the six reckonings, as _RestA to _RestE (the pace in the fifth is not read)
uniform sampler3D _RestBWas;
uniform sampler3D _RestCWas;
uniform sampler3D _RestDWas;
uniform sampler3D _RestEWas;
uniform vec4 _VolDetailWas;      // xyz: how far the billows' pattern had been carried along with the smoke as a whole when the grid before was made, as _VolDetail; w: 1 where the billows' places go over from that grid's to this one's
uniform sampler3D _Around;   // r: how much smoke there is round about, over a couple of metres (square root), g: more than nothing if there is any smoke or flame near, b: how light that smoke is (square root), a: how much of it is dust
uniform sampler3D _Flow;     // rgb: which way the smoke is moving and how fast, as a share of the fastest (0.5: not at all), a: how much the pattern of billows is squeezed there (1: to a sixteenth)
uniform sampler3D _RestA;    // how far the smoke at a place is from where it "was", by each of six reckonings (three for the big billows, then three for the small), east, up and north:
uniform sampler3D _RestB;    // eighteen numbers of sixteen bits, four to a texture, one after another
uniform sampler3D _RestC;
uniform sampler3D _RestD;
uniform sampler3D _RestE;    // (its third number is something else: how fast the smoke at a place is going through its round of turns, in rounds a second)
uniform sampler3D _Turns;    // whose turn it is at a place: rg the point of its round the smoke there is at, as a point on a circle (0.5: the middle of it), for the three reckonings the big billows go by; ba the same for the small billows' three
uniform sampler3D _Clear;    // r: how many cells of the coarser grid it is from this one to the nearest with any smoke or flame in it, over 255 (nought: there is some in this one). Read cell by cell, not between cells.
uniform sampler2D _VolSunMap;    // how far along the sun's light, from that picture's plane, the nearest solid thing is at each dot of it
uniform sampler3D _Detail;   // r: how much the smoke is gathered (lumps upon lumps), gba: the slope of that, each way
uniform sampler2D _CameraDepthTexture;
in vec3 vs_TEXCOORD0;
in vec4 vs_TEXCOORD1;
layout(location = 0) out vec4 SV_Target0;
// (and, where the picture drawn into has a second part: how far along the ray what the smoke hides lies, on the whole, and how
// widely that is spread along it (r and g), each times how much it hides, times _VolCamera.w. With them the patches' smoke is put
// in order pixel by pixel: see layers.glsl.)
layout(location = 1) out vec4 SV_Target1;

// The billows at one place. The pattern is lumps upon lumps (cellular noise), read twice: once turned one way,
// for the small billows, and once much larger and turned another, for the big. No lump of the one lines up
// with a lump of the other, so although each repeats every few metres, the two together never show the same
// shape twice. How finely they are read depends on how far away the place is: the smallest lumps drawn are
// a pixel or two across. (Were it to follow the length of the steps, which changes in jumps, parts of a
// cloud would go blurred and sharp again as they moved; it does so only deep along a ray, where the steps
// have had to be lengthened and nothing finer than them can be read truly.) From so far off that even the biggest lumps are
// a pixel or two across they are left out: read that coarsely, the pattern is no longer lumps but the lattice
// it repeats on. Each adds how much more (or less) than usual the smoke is gathered there, and which way that slopes,
// times what its set counts for just now (seen: see the walk below, which leaves a set that counts for nothing unread).
// (The bigger lumps count for twice the smaller: a lump no deeper than a small one would not show as big.)
//
// Both are read turned, each its own way, and by turns that bring no line of the grid back on to the pattern's own lines
// before it has gone many repeats: along each of the grid's three axes the pattern is within a quarter of a repeat of
// where it began only after nearly four repeats, and within an eighth only after twelve. (Until 2026-10-10 the small
// billows were read straight, and the big turned by a 3-4-5 turn that came back on itself in thirteen. A column of
// smoke lying along the grid, which is how every engine's trail lies, showed the same small lump again at every
// repeat, a row of balls behind the rocket as far as it could be seen.)
void bigBillows(vec3 q, float level, float seen, inout float lump, inout vec3 slope)
{
    vec3 turned = vec3(dot(q, vec3(0.578659, -0.748473, -0.323945)), dot(q, vec3(0.328128, 0.577295, -0.747705)), dot(q, vec3(0.746649, 0.326371, 0.579653))) * (0.37 * _VolSurfaceIn.z) + vec3(0.31, 0.67, 0.13);
    vec4 broad = textureLod(_Detail, turned, max(level - 1.43 + log2(_VolSurfaceIn.z), 0.0));
    lump += 0.9 * seen * (broad.r - 0.45);
    vec3 way = (broad.gba - 0.5) * (2.0 * 13.0 * 0.37 * _VolSurfaceIn.z * 1.4 * seen);
    slope += vec3(dot(way, vec3(0.578659, 0.328128, 0.746649)), dot(way, vec3(-0.748473, 0.577295, 0.326371)), dot(way, vec3(-0.323945, -0.747705, 0.579653)));
}

void smallBillows(vec3 q, float level, float seen, inout float lump, inout vec3 slope)
{
    vec3 turned = vec3(dot(q, vec3(0.578659, 0.328128, 0.746649)), dot(q, vec3(-0.748473, 0.577295, 0.326371)), dot(q, vec3(-0.323945, -0.747705, 0.579653)));
    vec4 fine_ = textureLod(_Detail, turned, max(level, 0.0));
    lump += 0.45 * seen * (fine_.r - 0.45);
    vec3 way = (fine_.gba - 0.5) * (2.0 * 13.0 * 0.7 * seen);
    slope += vec3(dot(way, vec3(0.578659, -0.748473, -0.323945)), dot(way, vec3(0.328128, 0.577295, -0.747705)), dot(way, vec3(0.746649, 0.326371, 0.579653)));
}

// How much of the smoke at a place is left where an engine is leaving a trail.
// The grid is a moment old, and the engine that is laying the smoke may be doing hundreds of metres a second: the
// head of its trail, as any one grid has it, is where the engine was when that grid was begun, and stays there
// until the next grid, while the engine goes on. A trail drawn so began a rocket's length behind the rocket and
// caught it up in jumps. So the mod lays the smoke a little way ahead of the engine, where it is about to be, and
// says here every frame exactly where the engine now is: ahead of that, along the line it is flying, the smoke is
// not drawn yet, and for a little way behind it comes on by degrees, as the engine's own smoke does along its flame.
//
// And the other way about, for the patch of air that goes along with a ship (told by taking away less than nothing):
// there the engines' smoke is drawn while it is young, from the flame as far as that place, and gives way from there
// back, over the same stretch, to the trail laid in the air the ship is leaving, which comes on as this goes. What is
// here, and what is there, are two reckonings of the same smoke: so much of the one and the rest of the other, the
// same curve for both, make one trail of them, with no place where it can be told which is which.
float begun(vec3 here, vec4 a, vec4 b, vec4 c)
{
    vec3 d = here - a.xyz;
    float back = dot(d, b.xyz);
    if (c.y < 0.0) return 1.0 + c.y * smoothstep(0.0, b.w, back);
    float beside = 1.0 - smoothstep(a.w, 2.25 * a.w, dot(d, d) - back * back);
    float ahead = (1.0 - smoothstep(0.0, b.w, back)) * (1.0 - smoothstep(0.7 * c.x, c.x, -back));
    return 1.0 - c.y * beside * ahead;
}

// The light a lamp of the scene throws on smoke at a place: falling off much as the game's own lamps' light does,
// to nothing at the end of its reach; and a spotlight's only inside its cone. (Not shaded by the smoke between: nor
// is a fire's, above.)
vec3 sceneLamp(vec3 here, vec4 at, vec4 tint, vec4 dir)
{
    if (at.w <= 0.0) return vec3(0.0);
    vec3 d = here - at.xyz;
    float d2 = dot(d, d);
    float fall = clamp(1.0 - d2 / at.w, 0.0, 1.0);
    fall = fall * fall / (1.0 + 25.0 * d2 / at.w);
    if (tint.a > -1.5) fall *= smoothstep(tint.a, tint.a + 0.06, dot(d, dir.xyz) * inversesqrt(d2 + 1e-4));
    return tint.rgb * fall;
}

// Which of the ship's nozzles a place is nearest the line of: how far from that line (squared), how far down it from the
// nozzle's mouth, and how wide that nozzle is. (All of a ship's jets are taken to go the same way.)
void nearer(vec4 mouth, vec3 here, inout float aside2, inout float along, inout float wide)
{
    if (mouth.w <= 0.0) return;
    vec3 rel = here - mouth.xyz;
    float a = dot(rel, _VolJet.xyz), s2 = dot(rel, rel) - a * a;
    if (s2 < aside2) { aside2 = s2; along = a; wide = mouth.w; }
}

// An engine's flame, as its pattern is seen: in the nozzle's own frame, not the gas's. The gas goes through a flame at a
// hundred metres a second and more, and a pattern carried with it is smeared to nothing by the game's smoothing from one
// frame to the next: the flame was a blur of colour. What is seen of a real one holds its place on the nozzle and flows
// down it: tongues and streaks drawn out along the jet, coming on at the nozzle and carried down it far slower than the
// gas. So the pattern here is read in the jet's own measure (two ways across it, one along it drawn out three times) and
// carried down it by _VolJet.w, at three sizes. 0 to 1, about 0.45 on the whole.
// (Its three sizes of lump, read with the whole of it moved by off through the noise: see jetPattern.)
float jetLumps(vec3 q, float l, vec3 off)
{
    float n1 = textureLod(_Detail, q * 0.31 + vec3(0.13, 0.57, 0.91) + off, l).r;
    float n2 = textureLod(_Detail, q * 0.83 + vec3(0.71, 0.29, 0.43) + off.yzx, l + 0.6).r;
    float n3 = textureLod(_Detail, q * 2.2 + vec3(0.37, 0.83, 0.17) + off.zxy, l + 1.4).r;
    return 0.5 * n1 + 0.33 * n2 + 0.17 * n3;
}

float jetPattern(vec3 here, float level, out float along, out float aside)
{
    vec3 axis = _VolJet.xyz;
    vec3 rel = here - _VolJetFrom.xyz;
    along = dot(rel, axis);
    vec3 across = rel - axis * along;
    aside = length(across);
    vec3 u = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
    vec3 v = cross(axis, u);
    // (Fast at the nozzle, where the game's smoothing between frames draws it out into streaks, as the eye and a camera see
    // gas going that fast; slower and slower down the jet, and less and less drawn out, until at its far end it is the
    // slow turning lumps of a fire: the gas slows as it goes and what was streaks breaks up into eddies.)
    float down = max(along, 0.0);
    float gone = down + down * down / (2.0 * max(_VolJetFlow.x, 1e-3));
    // (and the jet opens out as it goes: its pattern opens out with it, as the gas's own paths do, so that what are fine
    // streaks at the nozzle are lumps as wide as the flame at its far end)
    float wider = 1.0 / (1.0 + _VolJetEnd.w * down / max(_VolJetFrom.w, 1e-3));
    vec3 q = vec3(dot(across, u) * wider, dot(across, v) * wider, (gone - _VolJet.w) * _VolJetBody.z) / _VolJetLook.w;
    float l = max(level - 0.5, 0.0);
    float n = jetLumps(q, l, vec3(0.0));
    // (Since 2026-11-20 its far part boils as well: its lumps are made afresh where they are, many times a second, as they go
    // down it. Carried down the jet and no more, as until then, every lump kept its shape the length of the flame, and slowed
    // to a crawl at its far end: the flame's end was a few slow, steady lumps. A real flame's end flickers: in NASA's film of
    // Demo-2's launch (its isolated views) the tongues at a Falcon 9's flame's end are new from one frame to the next, a
    // thirtieth of a second, where ours changed by a quarter as much from frame to frame; its body near the nozzle is steady,
    // as ours was. Two copies of the pattern, each read from somewhere else in the noise while it is not seen at all, one
    // fading in as the other fades out: by how much, down the jet, _VolJetBoil.y to .w; how far on, .x. _VolJetBoil.y
    // nought: as until then.)
    float boil = _VolJetBoil.y * smoothstep(_VolJetBoil.z, _VolJetBoil.w, down / max(_VolJetEnd.x, 1e-3));
    if (boil > 0.0)
    {
        float turn = _VolJetBoil.x, wa = 1.0 - abs(2.0 * fract(turn) - 1.0), wb = 1.0 - wa;
        // (the same sixty-four places in turn, so that the count can be taken back by a multiple of it without a jump)
        vec3 offA = fract(vec3(0.8191725, 0.6710436, 0.5497005) * mod(floor(turn), 64.0)) * 13.0;
        vec3 offB = fract(vec3(0.6710436, 0.5497005, 0.8191725) * mod(floor(turn + 0.5), 64.0)) * 13.0 + 6.5;
        float a = jetLumps(q, l, offA) - 0.45, b = jetLumps(q, l, offB) - 0.45;
        // (as torn on the whole while both are half seen as when one is: two lumps' worth evened out would be smoother)
        n = mix(n, 0.45 + (wa * a + wb * b) / sqrt(wa * wa + wb * wb), boil);
    }
    return n;
}

// A fire's flames in the open, as their pattern is seen (see the flame in the walk). Real flames are thin sheets where the fuel
// vapour meets the air, wrinkled at every size by the turmoil of the hot gas, so that a fire shows sharp-edged tongues and folds,
// not a glow: whole and brightest low down where it burns steadily, torn into tongues above that flicker as they rise and tear
// off at their tips (a pool fire's continuous and intermittent flame, McCaffrey 1979), a fireball's surface a mass of burning
// folds. The pattern is carried with the burning gas (read where the gas "was", as the billows are) and rises through it as the
// flames lick upward, stretched up a little as they are; at a size that goes with the fire (see TuneFire). Two sizes of lump,
// 0 to 1, about 0.45 on the whole.
// (Read where the smoke as a whole has gone by this frame, not where each bit of it was when the grid in use was made: that place
// is carried on with the gas between one grid and the next, and put back with each new grid, and a fire's gas rises fast. When
// the game was busy and its grids came four times a second, the flames rose with the gas and dropped back four times a second:
// "the fire levitates a bit, resets, levitates a bit". The mod says how far the smoke as a whole has gone since the grid was
// made, every frame: _VolFireGone.)
float firePattern(vec3 was, float level)
{
    vec3 q = vec3(was.x, was.y * _VolFireLook.y, was.z) * _VolFire.y;
    float rise = _VolFire.x * _VolFire.y;
    // (as finely as the billows are read where they are, for the size of these lumps against theirs)
    float l = max(level + log2(0.093 * _VolFire.y / max(_VolDetail.w, 1e-4)), 0.0);
    float n1 = textureLod(_Detail, (q - vec3(0.0, rise, 0.0)) * 0.093 + vec3(0.29, 0.61, 0.17), l).r;
    float n2 = textureLod(_Detail, (q - vec3(0.0, 1.7 * rise, 0.0)) * 0.25 + vec3(0.83, 0.07, 0.53), l + 1.4).r;
    float n = 0.62 * n1 + 0.38 * n2;
    // (and a third size, finer, licking up faster again: the small tongues and wrinkles along a flame's edge; added about nought,
    // so that the pattern is as bright and as torn on the whole as with two)
    if (_VolFireLook.x > 0.0) n += _VolFireLook.x * (textureLod(_Detail, (q - vec3(0.0, 2.6 * rise, 0.0)) * 0.62 + vec3(0.47, 0.91, 0.33), l + 2.7).r - 0.5);
    return n;
}

// (Since 2026-11-20.) Where a lamp's light comes from, seen from here: its place, or for a lamp that is a line, the nearest place on
// it. A rocket's flame over its pad lights the steam from all along it, from the nozzle down: in NASA's photographs by night
// (KSC-20261013-PH-SPX01_0004, a Falcon 9) the billows are cream beside the flame as high as it reaches. As one point at the
// flame's foot, the billows high up beside the flame were lit as dimly as those far off; and as the point climbed through the
// steam with the rocket, the way to it from every place came out of the steam at once, and the light on all of it swung together
// for a tenth of a second (the dip 4.5 s into every night launch film).
vec3 lampFrom(vec4 lamp, vec4 line, vec3 here)
{
    float ll = dot(line.xyz, line.xyz);
    return ll > 1e-4 ? lamp.xyz + line.xyz * clamp(dot(here - lamp.xyz, line.xyz) / ll, 0.0, 1.0) : lamp.xyz;
}

// How much the smoke at a place is a fire's own column at its foot, where the fire burns in one place (0 to 1): within about the
// fire's width of it and up through the first few of its widths, the column widening a little as it rises. Smoke rising off such a
// fire is fed from it as fast as it rises, and its picture there stands from one grid to the next as the flame's does (see
// _VolFireGone.w): carried on with its gas between grids, as drifting smoke is, the column's foot rose off its fire and dropped back
// on to it with each new grid. (The owner, 2026-10-21, of a crash: "whenever you crash and the fire is generating smoke, the smoke
// lifts and then goes back down to generate a little more smoke".) The three strongest fires, those that stand.
float standsAt(vec3 here)
{
    float s = 0.0;
    for (int n = 0; n < 3; n++)
    {
        if (mod(floor(_VolLamps.w / exp2(float(n))), 2.0) < 0.5) continue;
        vec4 lamp = n == 0 ? _VolLampA : n == 1 ? _VolLampB : _VolLampC;
        float r = sqrt(max(lamp.w, 1e-4));
        vec3 d = here - lamp.xyz;
        float up = max(d.y, 0.0), across = length(d.xz) - 0.25 * up;
        s = max(s, (1.0 - smoothstep(r, 2.2 * r, across)) * (1.0 - smoothstep(2.0 * r, 6.0 * r, up)));
    }
    return s;
}

// What is left in the smoke where something has gone through it (see Wakes.cs): a tunnel as wide as the thing, filling in again from its
// walls as it ages, the walls a little thicker for the smoke pushed into them; the smoke round it wound round the way the air left
// turning there carries it (behind a blunt thing a little, one way and then the other along its path, a vortex street; round a wing's tip
// as round a vortex, the smoke drawn out into a spiral round its core); and how churned it is there (see churn, after the wakes in the walk).
// Drawn here, at the pixel, whatever the size of the grid's cells: a piece of wreckage a metre wide cuts a hole a metre wide.
void carve(vec3 here, vec4 a, vec4 b, vec4 c, inout float hole, inout float wall, inout vec3 twist, inout vec4 churn, inout float churnWide)
{
    // (a width below nought: a wake that clears nothing, only turns the smoke round it: a rocket's in the smoke its own engines lay)
    if (c.x == 0.0) return;
    vec3 ab = b.xyz - a.xyz;
    float long2 = max(dot(ab, ab), 1e-4);
    float s = clamp(dot(here - a.xyz, ab) / long2, 0.0, 1.0);
    vec3 off = here - (a.xyz + ab * s);
    float age = mix(a.w, b.w, s);
    float r = abs(c.x) * (1.0 + 0.3 * age);                    // (it widens as the air in it mixes with the air round it)
    float d2 = dot(off, off);
    // (c.z below nought: a wing tip's, whose turning reaches out so many of its widths)
    float reach = c.z < 0.0 ? max(-c.z, 2.0) : 2.0;
    if (d2 > reach * reach * r * r) return;
    float d = sqrt(d2), x = d / r;
    // (Right behind the thing the smoke is pushed out of the way altogether, and the tunnel stays clear for a moment before the
    // turmoil in it brings the smoke back in: nearly empty for the first half of its filling time, mostly filled by the end of it.
    // Only so is it seen through dense smoke: a tunnel with a quarter of the smoke left in it is as dark as the smoke round it.)
    float filled = age / max(_VolWakes.y * (1.0 + 0.12 * abs(c.x)), 0.2);
    float fresh = exp(-filled * filled);
    if (c.x > 0.0) hole = max(hole, _VolWakes.w * fresh * (1.0 - smoothstep(0.6, 1.0, x)));
    float rim = (x - 1.0) / 0.35;
    if (c.x > 0.0) wall = max(wall, 0.3 * fresh * exp(-rim * rim));
    // (The air in a wake is turmoil: eddies about as big as the wake is wide, coming up within a quarter of a second of the thing's going
    // by and dying away over several. Of the most churned wake here: how churned it is, where in it this is (in its widths, from its
    // older end, which the wind carries along with it; drifting slowly through the pattern as it ages, so that the eddies turn over),
    // and how wide it is.)
    float churned = (1.0 - exp(-4.0 * age)) / (1.0 + 0.25 * age) * (1.0 - smoothstep(0.8, 1.8, x));
    if (churned > churn.w) { churn = vec4((here - a.xyz) / r + vec3(0.0, 0.07 * age, 0.0), churned); churnWide = r; }
    float turn;
    if (c.z < 0.0)
    {
        // A wing tip's vortex: the core turns whole, and outside it the air turns the slower the further out (as 1 / distance, so
        // round in a time as distance squared), so the smoke round it is drawn out into a spiral, a few arms round a clear core,
        // that winds up over the first half second and goes on turning slowly while the vortex lasts. (All along the tube the same
        // way, so seen down the tube it is one spiral, not a blur of them.)
        turn = (c.y * (1.0 - exp(-2.0 * age)) + 0.6 * min(age, 3.0)) * (x < 1.0 ? 1.0 : 1.0 / (x * x)) * (1.0 - smoothstep(0.7 * reach, reach, x)) * c.w;
    }
    else
    {
        // (it winds up as the air turns, and stays wound once the turning has died away)
        turn = c.y * (1.0 - exp(-age / max(_VolWakes.z, 0.05))) * (x < 1.0 ? x : max(2.0 - x, 0.0)) * c.w;
        if (c.z > 0.0) turn *= sin(6.2831853 * s * sqrt(long2) / c.z);
    }
    if (abs(turn) > 0.002)
    {
        vec3 axis = ab * inversesqrt(long2);
        float cs = cos(turn), sn = sin(turn);
        twist += off * (cs - 1.0) + cross(axis, off) * sn + axis * (dot(axis, off) * (1.0 - cs));
    }
}

void main()
{
    // (Since 2026-10-25: a burning patch's flames are walked whole, by themselves, into the ships' picture, and the walk at half size
    // leaves their rectangle to that one, but for a couple of its pixels inside the edge, which the picture passes over there (see
    // _VolHoleA in layers.glsl): so that neither leaves a gap nor a seam. Enlarged from half size, a fire's tongues were a blur of
    // colour. See Layers.ForCamera.)
    if (_VolHoleWay.x > 0.5)
    {
        vec2 at = vs_TEXCOORD1.xy / vs_TEXCOORD1.w * 0.5 + 0.5;
        bool inside = at.x >= _VolHole.x && at.x < _VolHole.z && at.y >= _VolHole.y && at.y < _VolHole.w;
        if (inside == (_VolHoleWay.x < 1.5)) discard;
    }
    vec3 eye = _WorldSpaceCameraPos;
    vec3 rd = normalize(vs_TEXCOORD0 - eye);
    // The same ray in the box's own coordinates (-0.5 to 0.5 each way). The distance along it stays in metres.
    vec3 ro = hlslcc_mtx4x4unity_WorldToObject[0].xyz * eye.x + hlslcc_mtx4x4unity_WorldToObject[1].xyz * eye.y + hlslcc_mtx4x4unity_WorldToObject[2].xyz * eye.z + hlslcc_mtx4x4unity_WorldToObject[3].xyz;
    vec3 rdo = hlslcc_mtx4x4unity_WorldToObject[0].xyz * rd.x + hlslcc_mtx4x4unity_WorldToObject[1].xyz * rd.y + hlslcc_mtx4x4unity_WorldToObject[2].xyz * rd.z;
    vec3 size = max(abs(rdo), vec3(1e-6));
    vec3 inv = vec3(rdo.x < 0.0 ? -1.0 : 1.0, rdo.y < 0.0 ? -1.0 : 1.0, rdo.z < 0.0 ? -1.0 : 1.0) / size;
    vec3 ta = (vec3(-0.5) - ro) * inv, tb = (vec3(0.5) - ro) * inv;
    vec3 lo = min(ta, tb), hi = max(ta, tb);
    float t0 = max(max(lo.x, lo.y), lo.z), t1 = min(min(hi.x, hi.y), hi.z);

    // Only the stretch of the ray that this camera draws (the game uses one camera for near things and one for far).
    float forward = -(hlslcc_mtx4x4unity_MatrixV[0].z * rd.x + hlslcc_mtx4x4unity_MatrixV[1].z * rd.y + hlslcc_mtx4x4unity_MatrixV[2].z * rd.z);
    forward = max(forward, 1e-4);
    t0 = max(t0, _ProjectionParams.y / forward);
    t1 = min(t1, _ProjectionParams.z / forward);
    if (_VolCamera.x > 0.5)
    {
        float ahead;
        if (_VolPeak.x > 0.5)
        {
            // Drawn at half size, to be enlarged afterwards (see enlarge.glsl): this pixel stands for four of the
            // screen's. It stops at the nearest of the four things behind them or at the farthest, turn and turn
            // about like the squares of a chessboard: so whatever a pixel of the screen shows, near thing or far,
            // one of the pixels round it here stopped at the same.
            ivec2 cell = ivec2(gl_FragCoord.xy - _VolSlot.xy), most = textureSize(_CameraDepthTexture, 0) - 1;
            if (_VolQuarter.z > 0.5) cell = cell * 2 + ivec2(_VolQuarter.xy + 0.5);
            vec4 four = vec4(texelFetch(_CameraDepthTexture, min(2 * cell, most), 0).x, texelFetch(_CameraDepthTexture, min(2 * cell + ivec2(1, 0), most), 0).x,
                             texelFetch(_CameraDepthTexture, min(2 * cell + ivec2(0, 1), most), 0).x, texelFetch(_CameraDepthTexture, min(2 * cell + ivec2(1, 1), most), 0).x);
            four = 1.0 / (_ZBufferParams.z * four + _ZBufferParams.w);
            ahead = ((cell.x + cell.y) & 1) == 0 ? min(min(four.x, four.y), min(four.z, four.w)) : max(max(four.x, four.y), max(four.z, four.w));
        }
        else
        {
            vec2 uv = vs_TEXCOORD1.xy / vs_TEXCOORD1.w * 0.5 + 0.5;
            ahead = 1.0 / (_ZBufferParams.z * texture(_CameraDepthTexture, uv).x + _ZBufferParams.w);
        }
        t1 = min(t1, ahead / forward);
    }
    if (t1 <= t0) discard;

    // Steps are counted in fine steps from the camera. In smoke they are as long as a pixel or two is wide
    // out there, a power of two fine steps, but never longer than half a cell of the grid; through clear air
    // near smoke, about a cell; where there is nothing near at all, twice that. Every pixel starts its own
    // fixed part of a step in.
    // (Half a cell: from far off a pixel is metres wide, and steps that long, each pixel starting somewhere
    // else along its first, made a distant cloud a speckle of light and dark pixels with a ragged outline.
    // A cloud that far off covers few pixels, so walking them finely costs next to nothing.)
    float finest = _VolStep.x;
    vec3 perSize = 1.0 / _VolSize.xyz;
    float perMetre = _VolCamera.y * _VolGrid.y / finest;
    // (never shorter than two fine steps, a cell of the billows' pattern: from close to, shorter ones only used up the steps there are)
    float fineMost = max(0.5 * _VolGrid.x, 2.0);
    // (fine steps to a cell of the grid, along whichever way across the grid this ray goes fastest: see the leaps below)
    float perCell = 1.0 / (max(max(size.x, size.y), size.z) * 64.0 * finest);
    float fineHere = clamp(exp2(floor(log2(max(t0 * perMetre, 1.0)) + 0.25)), 2.0, fineMost);
    float coarse = max(_VolGrid.x, fineHere);
    float grain = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))) + _VolPeak.z);
    float shift = grain * 8.0;
    float kFirst = t0 / finest - shift, kLast = t1 / finest - shift;
    float k = ceil(kFirst / coarse) * coarse;
    float stride = coarse;
    bool fine = false, ended = false;
    float left = _VolParams.y, clear = 0.0, last = -1.0e9, longer = 1.0, coarser = 0.0;
    float passed = 0.0;       // the most that the smoke passed over so far could have hidden (see _VolThin)
    float thinLonger = 1.0, thinRun = 0.0;     // how many times longer the steps are for thin smoke, and for how many steps it has been thin (see where they are made longer)

    // Thin smoke between the camera and the sun shines.
    float toward = max(dot(rd, _VolSunDir.xyz), 0.0);
    float shine = 1.0 + _VolSun.w * toward * toward * toward * toward;
    vec3 colour = vec3(0.0);
    float through = 1.0;
    float deep = 0.0, deep2 = 0.0;      // the depth of each step along the ray (and its square), weighed by how much it hid of what is behind (see SV_Target1)
    vec3 rounds = vec3(0.0);            // (testing, _VolThin.z: how many times round the loop, how many of them in smoke, how many leaps over clear air)
    // (the coarse grids held along the ray: see where they are read)
    float holdFor = 0.1 * max(max(_VolSize.x, _VolSize.y), _VolSize.z) / 32.0, heldAt = -1.0e9, billowsAt = -1.0e9;
    vec4 heldGoing = vec4(0.5), heldTone = vec4(0.0), heldTurn = vec4(0.0), heldR1 = vec4(0.0), heldR2 = vec4(0.0), heldR3 = vec4(0.0), heldR4 = vec4(0.0), heldR5 = vec4(0.0);
    vec4 heldW1 = vec4(0.0), heldW2 = vec4(0.0), heldW3 = vec4(0.0), heldW4 = vec4(0.0), heldW5 = vec4(0.0);     // (the same of the grid before: see _VolDetailWas)
    float heldPlacesWas = 0.0;
    for (int i = 0; i < 288; i++)
    {
        rounds.x += 1.0;
        if (through < 0.02) break;
        if (k >= kLast)
        {
            // At the end of the ray, one more look right in front of whatever stopped it: smoke lying thin against a surface is not to be stepped over.
            if (fine || ended) break;
            ended = true;
            k = floor((kLast - 0.01) / fineHere) * fineHere;
            if (k < kFirst || k <= last) break;
        }
        last = k;
        float t = (k + shift) * finest;
        vec3 p = ro + rdo * t;
        if (!fine)
        {
            // Clear air is crossed in leaps. The mod has worked out, for every cell of the coarser grid, how many
            // cells it is from there to the nearest that has any smoke or flame in it at all; so from a place in a
            // cell that is, say, six cells from any, the ray can go on for the best part of five cells and find
            // nothing, whichever way it is going, and need not look. (Less a cell and three quarters of the coarser
            // grid: one for where in its own cell the place is, a quarter for the finer grid's being read between
            // its cells, and the rest for the smoke's being carried on from where the grid has it, as below.)
            // A leap ends on a step that would have been taken anyway (in clear air every second coarse step is,
            // whatever else): so what is found after it, and so the picture, is the same as if every step up to
            // there had been taken.
            float cells = texelFetch(_Clear, clamp(ivec3((p + 0.5) * 32.0), ivec3(0), ivec3(31)), 0).r * 255.0;
            if (cells > 2.5)
            {
                float to = floor((k + (2.0 * cells - 3.5) * perCell) / (2.0 * coarse)) * 2.0 * coarse;
                if (to > k) { k = to; rounds.z += 1.0; continue; }
            }
        }
        // The grid is a moment old (it takes a few frames to make), and the smoke has moved on since. So the grid
        // is read not at this place but where the smoke now here was when the grid was made, going by how fast
        // the smoke about here was moving then: between one grid and the next the whole cloud is carried
        // smoothly on, instead of standing still and then jumping.
        // (How the smoke moves here, what is round about, and the billows' reckonings are on the coarse grids, 32 cells a side, read
        // between their cells by the card: along a ray they change little over a tenth of a cell. So they are read again only once the
        // ray has gone a tenth of a cell since they last were: at every step where the steps are longer, every few where they are
        // shorter, as from inside a cloud. Read at every step, as until 2026-10-18, they were a fifth of what the walk cost from inside
        // a booster's cloud on the pad (two hundred steps a ray, a tenth of a metre each); held so, the picture is the same within a
        // two-hundred-and-fiftieth.)
        bool reread = abs(t - heldAt) > holdFor;
        if (reread) { heldGoing = textureLod(_Flow, p + 0.5, 0.0); heldAt = t; }
        vec4 going = heldGoing;
        vec2 wakeShown = vec2(0.0);      // (testing: see TestView 17 below)
        vec3 gone = (going.xyz - 0.5) * _VolFlow.x;
        gone *= min(1.0, _VolFlow.z / max(length(gone), 1e-4));
        vec3 here = p * _VolSize.xyz + _VolOffset.xyz;
        // (and at the foot of a fire's column, where the fire burns in one place, carried no more than its flame is: see standsAt)
        if (_VolLamps.w > 0.5 && _VolFireGone.w < 0.999) gone *= 1.0 - standsAt(here) * (1.0 - _VolFireGone.w);
        vec3 was = here - gone - _VolDetail.xyz;
        vec3 at = p + 0.5 - gone * perSize;
        // (And everything of the grid goes over from the grid before's to this one's over the time between grids, the grid before
        // read where the smoke now here was when that one was made: so a cloud changes from frame to frame, not from grid to grid.
        // The carrying on between grids, above, is a quarter of what changes from one grid to the next; the rest came in a step
        // with each new grid, and where the game draws six or seven frames to a grid the whole cloud had a frame rate of its own,
        // seven or eight a second: tools/motion/rates.py, 2026-11-16. Where the smoke now here is in that grid, or nowhere:)
        float smokeFromWas = _VolFireWasOffset.w >= 0.0 ? 1.0 - _VolFireWasSize.w : 0.0;
        vec3 atWasSmoke = vec3(-1.0), placeWas = vec3(0.0);
        float placesFromWas = 0.0;
        if (smokeFromWas > 0.004)
        {
            vec3 goneWas = (going.xyz - 0.5) * _VolFireWasOffset.w;
            goneWas *= min(1.0, 3.0 * _VolFlow.z / max(length(goneWas), 1e-4));
            // (at the foot of a fire that stands, carried no more than this grid's is: see standsAt)
            if (_VolLamps.w > 0.5 && _VolFireGone.w < 0.999) goneWas *= 1.0 - standsAt(here) * (1.0 - _VolFireGone.w);
            atWasSmoke = (here - goneWas - _VolFireWasOffset.xyz) / _VolFireWasSize.xyz + 0.5;
            if (any(lessThan(atWasSmoke, vec3(0.0))) || any(greaterThan(atWasSmoke, vec3(1.0)))) atWasSmoke = vec3(-1.0);
            // (and by how much the place this smoke carries in the billows' pattern, as the grid before reckons places, is off this
            // grid's reckoning of them, before what either says of where the smoke "was": see the billows below)
            else if (_VolDetailWas.w > 0.5) { placesFromWas = smokeFromWas; placeWas = gone - goneWas + _VolDetail.xyz - _VolDetailWas.xyz; }
        }
        if (reread)
        {
            heldTone = textureLod(_Around, at, 0.0);
            // (what is round about, the same: how much smoke, which says how deep the billows are cut, and its colour)
            if (atWasSmoke.x >= 0.0) heldTone = mix(heldTone, textureLod(_AroundWas, atWasSmoke, 0.0), smokeFromWas);
        }
        vec4 tone = heldTone;
        if (tone.g <= 0.0 && !fine)
        {
            k = (floor(k / (2.0 * coarse)) + 1.0) * 2.0 * coarse;
            continue;
        }
        vec4 amount = textureLod(_Amount, at, 0.0);
        float smoke = amount.r * 0.99611 + amount.g * 0.0038911, flame = amount.b * 0.99611 + amount.a * 0.0038911;
        // (Smoke thins out towards the walls and the lid of the patch's box, over its outermost twentieth (2026-11-16). The box is
        // fitted round the smoke with room to spare, but smoke that grows fast gets to a wall before the box is fitted again, and
        // there it ended on the wall itself: a launch's smoke stood for a third of a second with a sheet of haze cut off straight
        // along two edges, six seconds after lift-off (the film of that day, night-launch_pad). Not at the floor, which lies
        // under the ground, where smoke lies thickest.)
        // (the smoke gone over from the grid before's, see above; and flame that is carried on with its gas, a fireball's: a fire
        // that stands has its own way, below. Outside that grid there was none.)
        if (smokeFromWas > 0.004)
        {
            vec4 before4 = atWasSmoke.x >= 0.0 ? textureLod(_AmountWas, atWasSmoke, 0.0) : vec4(0.0);
            smoke = mix(smoke, before4.r * 0.99611 + before4.g * 0.0038911, smokeFromWas);
            if (_VolFireGone.w >= 0.999) flame = mix(flame, before4.b * 0.99611 + before4.a * 0.0038911, smokeFromWas);
        }
        vec3 fromWall = min(at, 1.0 - at);
        smoke *= clamp(min(min(fromWall.x, fromWall.z), 1.0 - at.y) * 20.0, 0.0, 1.0);
        // (A fire that burns where it is, fed from its fuel as fast as its gas rises off it, is the same from one grid to the next:
        // carried on with its gas between them, as smoke is, it rose off its fuel and dropped back on to it with each new grid. So
        // its flame, and how hot that is, are read where it is carried on to by less, or not at all: see _VolFireGone.w.)
        vec3 atFire = at;
        if (_VolFireGone.w < 0.999)
        {
            atFire = p + 0.5 - gone * (perSize * _VolFireGone.w);
            vec2 still = textureLod(_Amount, atFire, 0.0).ba;
            flame = still.x * 0.99611 + still.y * 0.0038911;
            // And it goes over from the grid before's flame to this one's, over the time between grids, as it would burn on: so a fire
            // that stands changes as smoothly as its smoke moves, not in a step with each new grid. (Shown from each grid as it came,
            // a far patch's fire, whose grids come fifteen to twenty times a second in a crash with several, flickered at that rate over
            // smoke carried on smoothly: the owner, 2026-11-04, "That brighter flame has a frame rate of 15 or something, and the smoke
            // has a normal frame rate".) A flame carried on with its gas is shown from this grid alone, as before.
            float fromWas = (1.0 - _VolFireWasSize.w) * (1.0 - _VolFireGone.w);
            if (fromWas > 0.001)
            {
                vec3 atWas = ((atFire - 0.5) * _VolSize.xyz + _VolOffset.xyz - _VolFireWasOffset.xyz) / _VolFireWasSize.xyz + 0.5;
                float before = 0.0;
                if (all(greaterThanEqual(atWas, vec3(0.0))) && all(lessThanEqual(atWas, vec3(1.0))))
                {
                    vec2 was2 = textureLod(_AmountWas, atWas, 0.0).ba;
                    before = was2.x * 0.99611 + was2.y * 0.0038911;
                }
                flame = mix(flame, before, fromWas);
            }
        }
        // (How much of the smoke here is to be seen, where an engine's trail begins: see begun. It is what the smoke hides
        // that is cut down by it, further on, not how much smoke there is taken to be: thin smoke is cut into by the billows
        // far more than thick (see below), and smoke taken to be half as thick, in the midst of as much as there is, was
        // not half as much to see but a few scraps of it, or nothing. Where one patch's smoke gave way to another's, each
        // by halves, there was a gap between them.)
        float shows = 1.0;
        if (_VolCutA.w > 0.0) shows *= begun(here, _VolCutA, _VolCutB, _VolCutC);
        if (_VolCutD.w > 0.0) shows *= begun(here, _VolCutD, _VolCutE, _VolCutF);
        if (shows <= 0.0) smoke = 0.0;
        if (_VolWakes.x > 0.5 && smoke > 0.0 && all(greaterThan(here, _VolWakeLo.xyz)) && all(lessThan(here, _VolWakeHi.xyz)))
        {
            // (where something has gone through: see carve)
            float hole = 0.0, wall = 0.0;
            vec3 twist = vec3(0.0);
            vec4 churn = vec4(0.0);
            float churnWide = 1.0;
            carve(here, _VolWakeA0, _VolWakeB0, _VolWakeC0, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 1.5) carve(here, _VolWakeA1, _VolWakeB1, _VolWakeC1, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 2.5) carve(here, _VolWakeA2, _VolWakeB2, _VolWakeC2, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 3.5) carve(here, _VolWakeA3, _VolWakeB3, _VolWakeC3, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 4.5) carve(here, _VolWakeA4, _VolWakeB4, _VolWakeC4, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 5.5) carve(here, _VolWakeA5, _VolWakeB5, _VolWakeC5, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 6.5) carve(here, _VolWakeA6, _VolWakeB6, _VolWakeC6, hole, wall, twist, churn, churnWide);
            if (_VolWakes.x > 7.5) carve(here, _VolWakeA7, _VolWakeB7, _VolWakeC7, hole, wall, twist, churn, churnWide);
            if (churn.w > 0.02)
            {
                // The turmoil: the pattern of the smoke pushed this way and that by eddies about as big as the wake is wide (the
                // slope of the pattern itself, read large, gives each place a way to be pushed that changes smoothly from place to
                // place but has no order), and the edge of the tunnel ragged with them rather than round. (Wound round the wake's
                // line alone, the pattern was drawn out into rings, as of a record, seen down the wake.) The eddies go along with
                // the wake as the wind carries it, and turn over slowly as it ages.
                // (the pattern has about eleven lumps to its width: read at 0.065 a wake's width, a lump is some 1.4 widths across)
                vec4 eddy = textureLod(_Detail, churn.xyz * 0.065 + vec3(0.53, 0.19, 0.71), 0.0);
                twist += (eddy.gba - 0.5) * (2.4 * churn.w * churnWide);
                hole = clamp(hole * (1.0 + 1.8 * (eddy.r - 0.45) * churn.w), 0.0, 1.0);
            }
            // (testing: 17 leaves the smoke as it is and shows where the wakes are instead: red where they have cleared it, green where they wind it)
            if (_VolGrid.w > 16.5 && _VolGrid.w < 17.5) { wakeShown = vec2(max(hole, wall), length(twist)); }
            else
            {
                smoke *= (1.0 - hole) * (1.0 + wall);
                was += twist;
            }
        }
        bool some = smoke + flame > 0.00001;
        if (some && passed < _VolThin.x)
        {
            // Smoke that may be too thin to see. The most this step could hide is all its smoke gathered as thick as
            // the billows ever gather it (1.6 times) over the whole step. While that and all before it come to less
            // than could be seen, it is passed over as clear air is; the first step that takes it past, and every
            // one after, counts in full. (Flame is never passed over: a spark shows however small.)
            float most = smoke * _VolParams.x * 1.6 * stride * finest;
            if (flame <= 0.0 && passed + most < _VolThin.x) { passed += most; some = false; }
            else passed = _VolThin.x;
        }
        if (some && !fine)
        {
            float fineNow = clamp(exp2(floor(log2(max(t * perMetre, 1.0)) + 0.25)), fineHere, fineMost) * longer;
            if (fineNow < stride)
            {
                // Smoke. Its edge is somewhere since the last step, which was clear: back to just after that one, and on in fine steps.
                k = max(k - stride + fineNow, ceil(kFirst / fineNow) * fineNow);
                stride = fineNow;
                fine = true;
                clear = 0.0;
                thinLonger = 1.0; thinRun = 0.0;
                continue;
            }
        }
        if (some)
        {
            clear = 0.0;
            rounds.y += 1.0;

            // Billows: read where this smoke "was", by each reckoning that counts for anything just now (see the top),
            // and blended. (Where the pattern is squeezed, it is read that much more coarsely.)
            // And where the steps have had to be made longer than they should be (see the end of the loop: deep along
            // a ray, behind most of what there is to see), more coarsely by as much. Read finely there, each pixel
            // caught the small lumps or missed them by chance: smoke seen through much other smoke from close by was
            // speckled, and a thin wall of it far in showed as a row of dots. The change comes in over a few steps.
            coarser += (log2(longer) - coarser) * 0.5;
            float level = (coarser > 0.01 ? max(log2(t * _VolCamera.y * _VolGrid.z), 0.0) + coarser : log2(t * _VolCamera.y * _VolGrid.z)) + going.a * 4.0;
            float lump = 0.45;
            vec3 slope = vec3(0.0), testFrom = vec3(0.0);
            // Reading the billows is most of the work of the whole walk: six looks into the pattern, and five into the
            // grids that say where to look. So what each set counts for here is worked out first (nothing from so far
            // off that its lumps are too small to see, see above the functions; nothing for a set just begun afresh),
            // and a set that counts for nothing is not read: it would have been multiplied by nought.
            float bigNear = 1.0 - smoothstep(2.6, 4.0, level - 1.43 + log2(_VolSurfaceIn.z)), smallNear = 1.0 - smoothstep(2.6, 4.0, level);
            vec3 bigSeen = vec3(0.0), smallSeen = vec3(0.0);
            // Whose turn it is differs from place to place (see the top). So what each set counts for is read from the
            // grid, like where the smoke "was": the mean over the smoke that is there. (Each three so scaled that
            // together they are as deep as one set of billows whatever their shares: the three have nothing to do
            // with one another, so their depths add as squares do.)
            vec4 r5 = vec4(0.0);
            if (bigNear > 0.0)
            {
                // (As a point on a circle, for each three: the three reckonings count for as much as it is from three
                // places on the rim, a third of the way round apart. Turned on by as far as the smoke has gone round
                // since the grid was made, so that what they count for changes from frame to frame, not from grid to grid.)
                // (and the reckonings' grids, held as the rest are; where they are, all of them at once)
                if (abs(t - billowsAt) > holdFor)
                {
                    heldTurn = textureLod(_Turns, at, 0.0); heldR5 = textureLod(_RestE, at, 0.0);
                    heldR1 = textureLod(_RestA, at, 0.0); heldR2 = textureLod(_RestB, at, 0.0); heldR3 = textureLod(_RestC, at, 0.0); heldR4 = textureLod(_RestD, at, 0.0);
                    // (The billows' places gone over from the grid before's to this one's, as the smoke is: each new grid says afresh
                    // where the smoke in each coarse cell "was", and what the carrying on between grids had missed of that came all
                    // at once, a quarter to a third of a lump's width: the billows were what stepped at the grids' rate when the
                    // smoke itself no longer did.)
                    heldPlacesWas = placesFromWas > 0.0 ? 1.0 : 0.0;
                    if (heldPlacesWas > 0.5)
                    {
                        heldW1 = textureLod(_RestAWas, atWasSmoke, 0.0); heldW2 = textureLod(_RestBWas, atWasSmoke, 0.0); heldW3 = textureLod(_RestCWas, atWasSmoke, 0.0);
                        heldW4 = textureLod(_RestDWas, atWasSmoke, 0.0); heldW5 = textureLod(_RestEWas, atWasSmoke, 0.0);
                    }
                    billowsAt = t;
                }
                vec4 turn = heldTurn * 2.0 - 1.0;
                r5 = heldR5 * _VolFlow.y + _VolFlow.w;
                if (heldPlacesWas > 0.5) r5.rg = mix(r5.rg, heldW5.rg * _VolFlow.y + _VolFlow.w + placeWas.yz, placesFromWas);
                float on = 6.2831853 * r5.b * _VolThin.y, c = cos(on), s = sin(on);
                vec2 big2 = vec2(turn.r * c - turn.g * s, turn.r * s + turn.g * c);
                float c2 = c * c - s * s, s2 = 2.0 * s * c;
                vec2 small2 = vec2(turn.b * c2 - turn.a * s2, turn.b * s2 + turn.a * c2);
                // (Each counts for nothing over the third of its round about where it begins afresh, and comes in and goes out
                // smoothly either side: so at any place and moment two of the three count and the third is not read. Until
                // 2026-10-17 each went to nothing only at the moment it began afresh, and all three were read nearly always:
                // a tenth of what drawing the smoke costs, for a pattern no different to look at, and two blended instead of
                // three are the crisper; the end of a reckoning's round, where its pattern is drawn out furthest, is not seen.)
                vec3 share = max(0.5 - (big2.x * vec3(1.0, -0.5, -0.5) - big2.y * vec3(0.0, 0.8660254, -0.8660254)), 0.0);
                // (One that has come to the end of its turn since the grid was made, and begun afresh, counts for nothing
                // until the next grid comes: the grid still has where the smoke "was" by it as it was before. It is the
                // one that was in the second half of its turn then and is in the first half now. Where the smoke is not
                // all at the same point of its round, that goes for the part of it that is at this one: the rest counts on.)
                vec3 was2 = turn.r * vec3(0.0, 0.8660254, -0.8660254) + turn.g * vec3(1.0, -0.5, -0.5), now2 = big2.x * vec3(0.0, 0.8660254, -0.8660254) + big2.y * vec3(1.0, -0.5, -0.5);
                share = mix(share, vec3(0.5 * max(1.0 - length(turn.rg), 0.0)), step(was2, vec3(-1e-4)) * step(vec3(0.0), now2));
                bigSeen = bigNear * share * inversesqrt(max(dot(share, share), 1e-6));
                share = max(0.5 - (small2.x * vec3(1.0, -0.5, -0.5) - small2.y * vec3(0.0, 0.8660254, -0.8660254)), 0.0);
                was2 = turn.b * vec3(0.0, 0.8660254, -0.8660254) + turn.a * vec3(1.0, -0.5, -0.5); now2 = small2.x * vec3(0.0, 0.8660254, -0.8660254) + small2.y * vec3(1.0, -0.5, -0.5);
                share = mix(share, vec3(0.5 * max(1.0 - length(turn.ba), 0.0)), step(was2, vec3(-1e-4)) * step(vec3(0.0), now2));
                smallSeen = smallNear * share * inversesqrt(max(dot(share, share), 1e-6));
            }
            bvec3 big = greaterThan(bigSeen, vec3(0.0)), small = greaterThan(smallSeen, vec3(0.0));
            // (Each set is read at its own place in the pattern as well, so that three sets begun at the same spot are still three different sets.)
            vec4 r1 = vec4(0.0), r2 = vec4(0.0), r3 = vec4(0.0), r4 = vec4(0.0);
            if (big.x || big.y) r1 = heldR1 * _VolFlow.y + _VolFlow.w;
            if (big.y || big.z) r2 = heldR2 * _VolFlow.y + _VolFlow.w;
            if (big.z || small.x) r3 = heldR3 * _VolFlow.y + _VolFlow.w;
            // (each number of the eighteen is one way's, east, up or north, by turns: the grid before's, less how far its reckoning of
            // places is off this one's that way, mixed with this one's)
            if (heldPlacesWas > 0.5 && placesFromWas > 0.0)
            {
                if (big.x || big.y) r1 = mix(r1, heldW1 * _VolFlow.y + _VolFlow.w + placeWas.xyzx, placesFromWas);
                if (big.y || big.z) r2 = mix(r2, heldW2 * _VolFlow.y + _VolFlow.w + placeWas.yzxy, placesFromWas);
                if (big.z || small.x) r3 = mix(r3, heldW3 * _VolFlow.y + _VolFlow.w + placeWas.zxyz, placesFromWas);
            }
            testFrom = r1.rgb;
            if (big.x) bigBillows((was + r1.rgb) * _VolDetail.w, level, bigSeen.x, lump, slope);
            if (big.y) bigBillows((was + vec3(r1.a, r2.rg)) * _VolDetail.w + vec3(0.37, 0.11, 0.71), level, bigSeen.y, lump, slope);
            if (big.z) bigBillows((was + vec3(r2.ba, r3.r)) * _VolDetail.w + vec3(0.71, 0.59, 0.23), level, bigSeen.z, lump, slope);
            float lumpBig = lump;
            vec3 slopeBig = slope;

            // The body of the smoke is left whole: thick, and solid to look at. It is only towards its edge, where
            // there is less smoke at a place than round about it, that the billows are cut in: deepest between the
            // lumps of the pattern, hardly at all where a lump stands. So the smoke ends in a surface of rounded
            // bulges with creases between them, as real smoke does, rather than fading away or being full of holes.
            float fill = 1.0 - exp(-smoke * _VolParams.x * _VolPeak.w);
            float about = 1.0 - exp(-tone.r * tone.r * _VolParams.x * _VolPeak.w);
            // Where there is little smoke round about (the thin skirts of a cloud, and above all a puff that has
            // strayed off on its own) the billows count for far more: between the lumps the smoke is cut right
            // through, and on them it is gathered thicker, so that a lone puff is not a soft ball, which is not a
            // shape smoke takes, but a ragged scrap of it. Only the billows themselves do this: where they
            // cannot be seen (from far off, or where the pattern is in a tangle) the smoke is left as it is.
            float usual = _VolParams.z * 0.53, thinly = 1.0 - about, edge = max(about, 0.6 * fill);
            // (Well inside a thick cloud nothing is cut right through. A pocket of thinner smoke there stays thinner
            // smoke: cut clean away, it was a hole with a hard edge, for thick smoke is either there or not.)
            edge = mix(edge, min(edge, 1.25 * fill), smoothstep(0.45, 0.8, about));
            // (It is the big billows that do it: cut through by the small ones too, thin smoke was peppered with holes.)
            float eatenBig = _VolParams.z * clamp(1.25 - 1.6 * lumpBig, 0.0, 1.0);
            // (Tried on 2026-11-16 and taken out: this cutting two thirds as strong and the gathering on the lumps a fifth, for thin
            // fire and smoke seen from within a dispersing fireball, hundreds of separate ragged scraps. Filmed beside the old
            // (recordings/2026-11-16-pc/thin1) the smoke round a fuel fireball went grey and see-through and lost its black body.)
            float cut = (eatenBig - usual) * 4.5 * about * thinly * thinly;

            // In the skirts of a cloud the big billows have very often cut the smoke away altogether before the small
            // ones are asked: there is nothing left for them to shape. Whether that is so can be told for certain
            // without reading them: at their most (every one of them a full lump) they add so much to the lump and
            // no more, which leaves at least so much eaten away; if even then nothing is left, and no flame either
            // (none here, or the lump too slight for any to show), this step comes to nothing whatever they are.
            // (Told with a little to spare, so that a rounding error cannot make it a different answer from the full sum.)
            float lumpMost = lumpBig + 0.2475 * (smallSeen.x + smallSeen.y + smallSeen.z);
            float eatenLeast = _VolParams.z * clamp(1.25 - 1.6 * lumpMost, 0.0, 1.0);
            if (_VolGrid.w < 0.5 && fill - eatenLeast * edge - cut < -1e-5 && (_VolSurface.x <= 0.0 || fill - eatenLeast * edge * _VolSurface.z - cut < _VolSurface.y) && (flame <= 0.0 || (lumpMost < 0.1799 && _VolJetLook.w <= 0.0 && _VolFire.w < 0.5)))
            {
                // (what follows is all that such a step does: see where the flame is worked out, below)
                left -= 1.0;
                thinRun = flame > 0.0 ? 0.0 : thinRun + 1.0;
                k += stride;
                if (fine && left <= 0.0 && longer < 7.5 && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                else if (fine && thinRun >= 3.0 && thinLonger < 4.0 && (left < 0.6 * _VolParams.y || longer > 1.5) && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; thinLonger *= 2.0; thinRun = 0.0; }
                continue;
            }
            if (small.y || small.z) r4 = heldR4 * _VolFlow.y + _VolFlow.w;
            if ((small.y || small.z) && heldPlacesWas > 0.5 && placesFromWas > 0.0) r4 = mix(r4, heldW4 * _VolFlow.y + _VolFlow.w + placeWas.xyzx, placesFromWas);
            if (small.x) smallBillows((was + r3.gba) * _VolDetail.w, level, smallSeen.x, lump, slope);
            if (small.y) smallBillows((was + r4.rgb) * _VolDetail.w + vec3(0.53, 0.29, 0.17), level, smallSeen.y, lump, slope);
            if (small.z) smallBillows((was + vec3(r4.a, r5.rg)) * _VolDetail.w + vec3(0.19, 0.83, 0.61), level, smallSeen.z, lump, slope);

            float eaten = _VolParams.z * clamp(1.25 - 1.6 * lump, 0.0, 1.0);
            // (The billows' shade goes mostly by the big ones, since 2026-11-16, and they are larger: a real cloud of smoke is
            // domes tens of metres across with bumps on them, lit and shaded dome by dome, the bumps a texture on that. Shaded
            // by every bump as much as by the dome it stands on, and the domes little larger than the bumps, a launch's cloud
            // was a skin of flat grey flakes: set beside NASA's sts065-s-049.)
            slope = slopeBig + _VolSurfaceIn.w * (slope - slopeBig);
            float kept = max(fill - eaten * edge - cut, 0.0) / (1.0 - 0.6 * eaten);
            // (Just inside where it is cut away the smoke comes on gently: thick smoke that began at once at its full thickness had an edge like card.)
            // (Since 2026-11-16 the edge is cut hard where there is much smoke round about, the body of a young cloud, whose billows
            // then stand out as a cauliflower's do: coming on gently over the width of a lump, as it did everywhere until then (and a
            // hard edge on the three sizes of lump there were then was cut card), a blast's smoke from 220 m was cotton wool. Where
            // there is little round about, a stray puff or old smoke thinning away, it still comes on gently, and is no longer
            // gathered thicker on the lumps: cut hard and gathered, each stray puff was a dark ball, a ring of them round a fuel
            // blast's foot and hundreds across the sky from inside a fireball dispersing.)
            // (Pale smoke, an engine's on its pad, since 2026-11-16: its light and shade deeper, the paler it is. As much of the sun's
            // light strayed on through it as through a summer cloud, and its billows, white on white, had next to no shade between
            // them: "super smooth and kind of soft", the owner. A rocket's cloud is thick with water and soot, bright where the sun
            // is on it and grey to brown in its folds. So: half as much light strays through it, its clefts are twice as dark to
            // the sun, its billows shade each other by 1.7 times as much and down to a quarter, and its edge is cut hard from half
            // as much smoke round about. Soot's light is as it was: none of this where the smoke is dark.)
            // (A third of all that, since later the same day: at its full strength the pad's cloud was mottled grey, "The smoking
            // looks worse now", the owner. What had made that cloud smooth was not its light: its billows were drawn out into
            // fibres along the stream (see Site.Streamed), and deeper shade on fibres is dirt. With the billows whole, a little
            // of it gives them their grey undersides.)
            float pale = _VolUpLocal.w * 0.35 * min(tone.b * tone.b * 1.4, 1.0);
            float dense = _VolStep.y * smoothstep(0.15 * (1.0 - 0.5 * pale), 0.5 * (1.0 - 0.5 * pale), about);
            float whole = fill > 1e-4 ? min(kept / fill, _VolStep.y > 0.0 ? mix(1.0, 1.6, dense) : 1.6) : 0.0;
            float comes = whole * min(1.25 * whole, 1.0);
            if (dense > 0.0) comes = mix(comes, whole * smoothstep(0.0, max(_VolStep.z, 0.02), whole), dense);
            float sigma = smoke * _VolParams.x * comes * shows;
            // The smoke's surface (since 2026-11-16). Young, thick smoke is a thing with a surface: a launch's cloud and a fuel
            // fire's black smoke end, in every photograph of one, at a hard edge against the sky, lobe upon rounded lobe, as a
            // cumulus does; nothing of them fades out. (The owner, of what was drawn until then: "super smooth and kind of soft.
            // It doesn't look like or feel like real smoke"; set beside NASA's photographs of the Shuttle's and a Falcon 9's
            // launch clouds and of a jet-fuel fireball, it was a mound of wool with a fringe metres deep that one saw into.)
            // What made the fringe: the smoke on the grid thins away over several cells at its edge, and all of that was drawn,
            // as thin as it was, with the billows cut into it. So: where there is less smoke than a least, once the billows have
            // had their cut of it, there is none; over a little more than that least it comes on; and inside it is thick at
            // once, whatever the grid has. The billows then stand on the surface as lobes, and it is they, not the grid's soft
            // falling off, that the eye meets. Only where there is smoke enough round about: a thin trail, a stray puff and
            // old smoke gone thin are drawn as before, thinning away (cut hard, a stray puff was a dark ball: see above). And a
            // puff by itself that has smoke enough for a surface is cut ragged by the big billows, as before (`cut`): without
            // that, round black balls stood in the sky beside a crash's column.
            if (_VolSurface.x > 0.0)
            {
                // (As the smoke thins, by how much there is round about, the surface goes over to the old drawing by degrees: the
                // least smoke there is any of less, the surface coming on over more, the inside less thick. With the surface
                // whole until it was gone, a booster's column as it spread broke up into hard-edged tufts hanging in a haze,
                // each the top of a lump still over the least: filmed, `srb2`.)
                float surface = _VolSurface.x * smoothstep(0.3 * _VolSurfaceIn.y, _VolSurfaceIn.y, about);
                float solid = smoothstep(_VolSurface.y * surface, _VolSurface.y * surface + mix(0.35, max(_VolSurface.w, 0.01), surface), fill - eaten * edge * _VolSurface.z - cut);
                sigma = mix(sigma, max(smoke * _VolParams.x, surface * _VolSurfaceIn.x / _VolPeak.w) * solid * shows, surface);
            }
            if (_VolGrid.w > 0.5 && (_VolGrid.w < 1.5 || _VolGrid.w > 2.5)) sigma = smoke * _VolParams.x * shows;      // (testing: the smoke as it is on the grid, not cut into)
            float thin = exp(-sigma * _VolPeak.w);

            // Flame: licked into tongues, flickering, and coloured by how hot it is.
            // (folded: a fire in the open drawn as since 2026-10-18, its flame folded, its light stopped by its smoke, its hot smoke
            // shining; the mod's TestFire 2 draws it as before, for setting one against the other, and its TestDusk 1 (w 3) folded but
            // lit as on the morning of 2026-10-18, before the fire at dusk was seen)
            bool folded = _VolFire.w > 1.5 && _VolJetLook.w <= 0.0;
            float burning, fireN = -1.0, fireHot = 0.0;
            if (_VolFire.w > 0.5 && _VolJetLook.w <= 0.0 && flame > 0.0)
            {
                // A fire in the open (see firePattern): where the flame is thick it burns whole; where it thins (its edge, the tops
                // of its tongues) the pattern cuts it into tongues with sharp edges (the steep step from nothing to flame is what
                // makes a fire read as one: a soft ramp is a glow), brighter in the lumps of the pattern; and its cooler, thinner
                // parts are dark with the soot it makes, pockets in the flame and smoke rolling off its tips.
                fireN = firePattern(was + gone + _VolFireGone.xyz, level);
                // (and as it cools on its way up, it thins: so low down, where it is hot, it burns whole and steady, higher up it
                // breaks into tongues, and above them it is gone: a pool fire's continuous flame, its tongues, and its plume)
                float hotHere = textureLod(_Volume, atFire, 0.0).a;
                // (its heat gone over from the grid before's as its flame is: where the fire stands, at the same place of that grid)
                if (smokeFromWas > 0.004)
                {
                    vec3 atWasHot = _VolFireGone.w < 0.999 ? ((atFire - 0.5) * _VolSize.xyz + _VolOffset.xyz - _VolFireWasOffset.xyz) / _VolFireWasSize.xyz + 0.5 : atWasSmoke;
                    if (all(greaterThanEqual(atWasHot, vec3(0.0))) && all(lessThanEqual(atWasHot, vec3(1.0)))) hotHere = mix(hotHere, textureLod(_VolumeWas, atWasHot, 0.0).a, smokeFromWas);
                }
                fireHot = hotHere;
                // (Most of a fire's flame burns whole: only where it is thin is it torn. Cut more keenly, as first tried, a young
                // fireball, which should be luminous all through, was smoke with a few bright patches in it.)
                float body = (1.0 - exp(-flame * _VolParams.w * 2.0)) * smoothstep(0.05, 0.35, hotHere);
                float tongue = smoothstep(_VolFireShine.w - _VolFireLook.z, _VolFireShine.w + _VolFireLook.z, body * 1.2 + (fireN - 0.5) * 1.2 * _VolFire.z + (lump - 0.45) * _VolFireLook.w);
                burning = flame * _VolParams.w * tongue * (0.7 + 0.9 * fireN);
                // (A fuel fire's thick flame is folded at the size of its billows, as photographs of fuel fireballs show it: bright
                // where a billow's top faces out, and in the creases between billows, where cold air is drawn in, cooler, dim and dark
                // with soot. Burnt whole, as until 2026-10-18, a young fireball was one smooth glowing body: "a glowing volumetric
                // orb", the owner, 2026-10-17. And the flame as a whole at 0.45 of what it was: brighter, it rolled over into white in
                // the film's curve and threw a halo round itself through the film's glow, by day as well.)
                for (int once = folded ? 0 : 1; once < 1; once++)
                {
                    float top = smoothstep(0.40, 0.52, lump);
                    burning *= mix(_VolFireLight.y, 1.0, top) * _VolFireLight.x;
                    sigma += flame * _VolParams.w * (1.0 - top) * tongue;
                    fireHot *= mix(0.6, 1.0, top);
                }
                // (soot only where it has cooled: a fuel fireball is luminous all through for its first second. And in all of what the
                // tongues tear away, whichever lump of the pattern it lies in, the darker lumps the darker: until 2026-10-25 only the
                // darker half was, and the rest showed nothing at all, holes between a fire's tongues and a band above them that one saw
                // through before its smoke began, "where the fire connects to the smoke, there's a gap", the owner)
                sigma += flame * _VolParams.w * _VolFire.z * 0.45 * mix(0.55, 1.0, smoothstep(0.5, 0.25, fireN)) * (1.0 - tongue) * smoothstep(0.75, 0.35, hotHere);
            }
            else burning = flame * _VolParams.w * mix(clamp(2.5 * lump - 0.45, 0.0, 1.8), 0.7, _VolPeak.y);
            // An engine's flame (see jetPattern): where it is thin, at its edge and its far end, the pattern tears it into
            // tongues with sharp edges, and where it is thick it burns whole; brighter and dimmer along the pattern's
            // streaks; in thick air with shock diamonds in its core, a few nozzles' widths long; and a sooty fuel's
            // flame darkened by its own soot in the cooler streaks of its outer part.
            float jetN = -1.0, diamonds = 0.0, jetFar = 0.0, jetCooled = 1.0, jetSheath = 0.0;
            if (_VolJetLook.w > 0.0 && flame > 0.0)
            {
                float along, aside;
                jetN = jetPattern(here, level, along, aside);
                float f = 1.0 - exp(-flame * _VolParams.w * _VolJetBody.x);
                // (Towards its far end the gas has taken in air all the way through, and burns in pockets: the flame ends in
                // tongues with gaps between them, not in a rounded tip.)
                float ending = smoothstep(0.5, 1.1, along / max(_VolJetEnd.x, 1e-3));
                f *= 1.0 - _VolJetEnd.y * ending;
                // (and cools out through orange there)
                jetCooled = 1.0 - _VolJetEnd.z * ending;
                // (the air has had no time to tear it where it leaves the nozzle: its edge is straight there, and more and more torn the further it has gone)
                jetFar = clamp(along / max(_VolJetBody.w, 1e-3), 0.0, 1.0);
                // (Its outer part cooler than its core, since 2026-11-20: where the gas meets the air it mixes with it and burns on,
                // cooler and sooty, a yellow-orange sheath round the white-hot core, thin at the nozzle and thicker the further the air
                // has got in. In NASA's film of Demo-2's launch, against a pale sky as bright as ours, the Falcon 9's flame is white for
                // half its width and orange round that, its red over its blue some 65 levels of 255 over the sky's at 1.2 to 1.5 of the
                // white's half width, and still 40 at twice that. The bits the flame is laid in carry a cooler rim (see Flame in
                // SiteExhaust), but each is nearly as wide as the jet, and the grid evened their heat out: the whole of it came out
                // at its hottest colour, white to its edge, and its red over blue there fell to 30 by 1.5 half widths. Here the heat
                // falls towards the edge by how thin the flame is: the lower of this and the far end's own cooling, not the two
                // together, which made the far end a dull brown, where the Falcon 9's fades out orange.)
                jetSheath = _VolJetSheath.x * mix(_VolJetSheath.w, 1.0, jetFar) * (1.0 - smoothstep(_VolJetSheath.y, _VolJetSheath.z, f));
                float torn = _VolJetLook.x * max(jetFar, 0.12);
                float shape = smoothstep(0.32, 0.52, f * 1.25 + (jetN - 0.45) * 2.6 * torn);
                // (a clear flame, hydrogen's, has no such edge: it is a glow that thins away to nothing)
                shape = mix(shape, 1.0, _VolJetFlow.w * _VolJetFlow.w);
                // (A shock diamond: the gas is stopped short and heated where the shock stands across the jet, a bright disc
                // three quarters of a spacing out and then at every spacing; behind each the hot gas narrows to a point as
                // it speeds up and cools again, before the next. Each fainter than the last. In the jet of every nozzle,
                // each by its own width.)
                float dAside2 = 1e9, dAlong = along, dWide = _VolJetFrom.w;
                nearer(_VolJetAt0, here, dAside2, dAlong, dWide); nearer(_VolJetAt1, here, dAside2, dAlong, dWide);
                nearer(_VolJetAt2, here, dAside2, dAlong, dWide); nearer(_VolJetAt3, here, dAside2, dAlong, dWide);
                nearer(_VolJetAt4, here, dAside2, dAlong, dWide); nearer(_VolJetAt5, here, dAside2, dAlong, dWide);
                nearer(_VolJetAt6, here, dAside2, dAlong, dWide); nearer(_VolJetAt7, here, dAside2, dAlong, dWide);
                if (dAside2 > 1e8) dAside2 = aside * aside;
                float spacing = 2.6 * dWide * max(_VolJetGlow.z, 0.5);
                float cell = dAlong / spacing - 0.75, nth = floor(cell), into = cell - nth;
                float span = dWide * mix(0.6, 0.18, into) * max(_VolJetGlow.w, 0.5);
                float diamond = step(0.0, cell) * step(nth, _VolJetBody.y - 0.5) * exp(-3.2 * into) * smoothstep(0.0, 0.16, into) * exp(-0.65 * nth)
                              * exp(-dAside2 / (span * span));
                diamonds = _VolJetLook.z * diamond;
                // (a diamond is a thin disc of gas made hotter by the shock in it: it glows of itself, however faint the flame
                // round it is, as in a hydrogen engine's clear flame, and in the hot colours)
                // (a clear flame is even: the pattern that makes streaks and tongues of a thick one would make cloud of it)
                // (and a clear flame's gas is seen only by its own light: cooled, it is not there to be seen at all, where a
                // sooty flame's is dark and hides what is behind it)
                float seen = mix(1.0, smoothstep(0.35, 0.9, textureLod(_Volume, at, 0.0).a * 1.2 * jetCooled), _VolJetGlow.y);
                burning = flame * _VolParams.w * (shape * mix(0.45 + 1.2 * jetN, 1.0, _VolJetFlow.w) + 2.2 * diamonds) * seen + _VolJetGlow.x / max(dWide, 0.05) * diamonds * smoothstep(0.0, 0.02, flame);
                // (soot is made where the flame has had the time to cool, not at the nozzle)
                sigma += flame * _VolParams.w * _VolJetLook.y * smoothstep(0.5, 0.25, jetN) * (1.0 - f) * 0.6 * clamp(along / max(_VolJetBody.w, 1e-3) - 0.3, 0.0, 1.0) * (1.0 - _VolJetGlow.y);
            }
            // (Flame is a glow, and can be stepped through twice as fast as smoke. A step where the billows have cut
            // everything away counts in full, though there was nothing to light: there are only so many steps to a
            // ray, and counted for less, as they once were, a ray that began in the thin skirts of a cloud spent them
            // all there and never reached the body of it: from close by, windows clean through the smoke with hard edges.)
            left -= burning > sigma ? 2.0 : 1.0;
            // (Where the steps were made longer for thin smoke (see below), thicker smoke or flame has them as short as before again; and at
            // flame the ray goes back to just after the last step, which was in thin smoke, so that the flame begins where it does: begun a
            // long step late, flame inside thin smoke was speckled. Thick smoke's edge is not gone back for: inside a cloud, where the steps
            // are made longer most, going back for it cost more time than it saved and made the picture no better.)
            if (thinLonger > 1.5 && (burning > 0.0 || (sigma + burning) * stride * finest > 0.030)) { float strideWas = stride; stride /= thinLonger; thinLonger = 1.0; thinRun = 0.0; if (burning > 0.0) { k += stride - strideWas; continue; } }
            if (_VolGrid.w < 0.5 && sigma + burning <= 0.0)
            {
                thinRun = flame > 0.0 ? 0.0 : thinRun + 1.0;
                k += stride;
                if (fine && left <= 0.0 && longer < 7.5 && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                else if (fine && thinRun >= 3.0 && thinLonger < 4.0 && (left < 0.6 * _VolParams.y || longer > 1.5) && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; thinLonger *= 2.0; thinRun = 0.0; }
                continue;
            }
            vec4 air = textureLod(_Volume, at, 0.0);
            // (The light that reaches the smoke, and its heat, gone over from the grid before's as the smoke itself is, above: made
            // anew with each grid, the cloud's light and shade stepped at the grids' rate when its smoke no longer did.)
            if (atWasSmoke.x >= 0.0) air = mix(air, textureLod(_VolumeWas, atWasSmoke, 0.0), smokeFromWas);
            float heat = jetN >= 0.0 ? air.a * 1.2 * clamp(1.0 + (jetN - 0.45) * mix(_VolJetFlow.y, _VolJetFlow.z, jetFar), 0.15, 1.2) * min(jetCooled, 1.0 - jetSheath) + 0.9 * diamonds : fireN >= 0.0 ? fireHot * 1.2 * (0.5 + 0.85 * fireN) * _VolFireShine.z : air.a * 1.2 * (0.72 + 0.56 * mix(lump, 0.5, _VolPeak.y));
            vec3 hot = heat < 0.25 ? _VolHot1.rgb * (heat * 4.0) : heat < 0.5 ? mix(_VolHot1.rgb, _VolHot2.rgb, heat * 4.0 - 1.0) : heat < 0.75 ? mix(_VolHot2.rgb, _VolHot3.rgb, heat * 4.0 - 2.0) : mix(_VolHot3.rgb, _VolHot4.rgb, min(heat * 4.0 - 3.0, 1.0));
            // (A fire's light goes with its heat far more steeply than its colour does (a black body's light in what the eye sees
            // grows many times over between a dull red and a yellow heat): its hottest parts, low in it and in the lumps of the
            // pattern, blaze yellow-white, its edges a dim deep red. Spread evenly, as before, it was a warm haze.)
            if (fireN >= 0.0) { float h = min(heat, 1.1); hot *= 0.55 + 2.2 * h * h * h; }

            // Smoke: its own colour, in the light that reaches it. Thinned out, dark smoke looks paler. And each puff
            // has a lit side and a shaded one: where the smoke gets thicker towards the sun, this place is darker.
            vec3 own = tone.b * tone.b * mix(vec3(1.0), _VolTint.rgb, tone.a);
            // (Soot with a surface is darker by half, since 2026-11-16. What is seen of smoke with a surface is its outermost skin,
            // where the sun and the sky reach it unhindered; without one the eye went metres in, to smoke in the shade of the
            // smoke before it. A crash's black smoke came out mid grey by day beside NASA's EC84-31809, where it is black with
            // grey on its sunlit tops, and at dusk, lit square on by the low sun, a pale pink. Pale smoke as it was.)
            // (Soot: not dust, which is the ground's own colour, as a nuclear explosion's cloud is mostly.)
            own *= 1.0 - 0.5 * _VolSurface.x * (1.0 - min(tone.b * tone.b * 1.4, 1.0)) * (1.0 - tone.a) * smoothstep(0.3 * _VolSurfaceIn.y, _VolSurfaceIn.y, about);
            own += vec3(1.0, 0.96, 0.9) * (_VolStep.w * thin * step(own.r, 0.3));
            // (pale smoke with a surface: its domes shaded twice as much, a white cloud's grey side)
            float relief = clamp(1.0 - _VolSunLocal.w * 0.055 * (1.0 + _VolSurface.x * min(tone.b * tone.b * 1.4, 1.0)) * (1.0 + 0.7 * pale) * dot(slope, _VolSunLocal.xyz), mix(0.45, 0.24, pale), 1.6);
            // The sun's light, after the smoke that lies towards the sun (a part of it gets much further than the rest: light
            // finds its way round inside a cloud. Much of it in pale smoke, which throws nearly all the light it meets on to
            // the next bit; little in soot, which swallows it), the light of the open sky, and that of the fires near by.
            float before = (air.r * 0.99611 + air.g * 0.0038911) * 16.0;
            float strays = 0.12 + 0.5 * min(tone.b * tone.b * 1.4, 1.0) - 0.25 * pale;
            // Down in the clefts between billows less light of any kind gets in.
            float cleft = eaten * about;
            float sun = ((1.0 - strays) * exp(-before) + strays * exp(-0.25 * before)) * (1.0 - 0.3 * (1.0 + pale) * cleft);
            // What stands between this place and the sun (the craft, the tower, a building, the ground): its shadow in the smoke.
            // Not black: light that has found its way round inside the cloud still reaches it, more in pale smoke than in soot.
            float solidLit = 1.0;
            vec3 mapSeen = vec3(0.0);       // (testing: 24)
            if (_VolSunMapAlong.w > 0.5)
            {
                vec3 rel = eye + rd * t - _VolSunMapFrom.xyz;
                vec2 onMap = vec2(dot(rel, _VolSunMapRight.xyz), dot(rel, _VolSunMapUp.xyz)) * _VolSunMapFrom.w + 0.5;
                mapSeen = vec3(fract(onMap * 4.0), 0.0);
                if (onMap.x > 0.0 && onMap.y > 0.0 && onMap.x < 1.0 && onMap.y < 1.0)
                {
                    float far = dot(rel, _VolSunMapAlong.xyz), nearest = textureLod(_VolSunMap, onMap, 0.0).r;
                    mapSeen = vec3(clamp((far - nearest) / 20.0, 0.0, 1.0), clamp((nearest - far) / 20.0, 0.0, 1.0), fract(far / 100.0));
                    solidLit = smoothstep(far - 2.0, far - 0.6, nearest);
                    sun *= mix(0.12 + 0.4 * strays, 1.0, solidLit);
                }
            }
            float sky = exp(-air.b * 8.0) * (1.0 - 0.55 * cleft);
            // (The sky's light on the shaded side of a thick cloud, since 2026-11-16. The grid has the sky's light as what comes
            // straight down through the smoke above, and under a tall cloud that is none: with the sun stopped as well, by all the
            // cloud between, its near side when the sun is behind it was left the floor's three tenths of the sky's light, the same
            // at every place, on smoke as dark as soot: a flat black shape with a lumpy outline, where the shaded side of real smoke
            // is lit by all the sky beside it and shows every billow, its top paler than its underside and its clefts dark. So:
            // where there is little smoke round about, the open side of a cloud, the sky's light comes in whatever lies overhead;
            // and all of the sky's light goes by which way the billow faces, up or down, and is less in the clefts.)
            float skyAll = max(sky, _VolUpLocal.w * 0.75 * thinly * (1.0 - 0.55 * cleft));
            float skyRelief = clamp(1.0 - _VolUpLocal.w * _VolSunLocal.w * 0.045 * dot(slope, _VolUpLocal.xyz), 0.5, 1.6);
            vec3 atA = lampFrom(_VolLampA, _VolLampLineA, here), atB = lampFrom(_VolLampB, _VolLampLineB, here), atC = lampFrom(_VolLampC, _VolLampLineC, here);
            vec3 toA = here - atA, toB = here - atB, toC = here - atC;
            vec3 nearLamp = vec3(_VolLampA.w / (dot(toA, toA) + _VolLampA.w + 1e-4), _VolLampB.w / (dot(toB, toB) + _VolLampB.w + 1e-4), _VolLampC.w / (dot(toC, toC) + _VolLampC.w + 1e-4));
            // (round a fire in the open, falling off as steeply as its look says: see _VolFireLight)
            if (folded && abs(_VolFireLight.z - 1.0) > 0.001) nearLamp = pow(nearLamp, vec3(_VolFireLight.z));
            vec3 fromLamp = _VolLamps.xyz * nearLamp;
            float allLamps = fromLamp.x + fromLamp.y + fromLamp.z;
            float firelight = min(allLamps, 1.0);
            // (And the fires' light goes by the billows as the sun's does, since 2026-11-16: where the smoke gets thicker towards the
            // fire, this place is in the shade of it. The smoke round a big fire is lit by the fire far more than by the day, and
            // lit evenly it was a smooth orange-brown fog whatever billows it had.)
            if (_VolUpLocal.w > 0.0 && allLamps > 0.01)
            {
                vec3 toFire = -(toA * (fromLamp.x * inversesqrt(dot(toA, toA) + 1e-4)) + toB * (fromLamp.y * inversesqrt(dot(toB, toB) + 1e-4)) + toC * (fromLamp.z * inversesqrt(dot(toC, toC) + 1e-4))) / allLamps;
                firelight *= clamp(1.0 - _VolUpLocal.w * _VolSunLocal.w * 0.055 * dot(slope, toFire), 0.4, 1.6) * (1.0 - 0.4 * cleft);
            }
            // (The fires' light reaches a place only through the smoke between: in a thick cloud with the fire inside, its far side
            // is not lit by it. Taken from the coarse grid of how much smoke lies round about, at a third and two thirds of the way
            // to each lamp, and a twenty-fifth of what that would stop along a straight line: light finds its way round inside a
            // cloud. Unstopped, as until 2026-10-18, the fire lit a whole cloud as evenly as a lamp lights a paper lantern; stopped by
            // a tenth, as for that morning, the smoke round a fire at dusk lost the orange glow from within it. Each lamp's light by
            // its own way, as since 2026-10-20: stopped along the way to the nearest lamp alone, the light changed in a step wherever
            // the nearest lamp did, on the flat halfway between two lamps; between a blast's lamps one above another, as they climb,
            // a level line straight across the cloud, dark smoke over smoke lit orange (the owner: "a hard line edge with the
            // fire's rendering"). A lamp that is out gives nothing and is passed by.)
            // (A launch's steam the same since 2026-11-20, by as much as Site.SteamStops says: lit by its flame through none of it, the
            // cloud by the flame was one even cream, where in NASA's photograph of a Falcon 9 by night a quarter of the steam is in
            // brown shade. And the eye is dazzled by the flame as near as it is, whatever reaches this place, so the steam it does not
            // reach goes dark amber, not the grey of the floodlights.)
            float seenFire = firelight, stopsShare = folded ? (_VolFire.w > 2.5 ? 0.1 : 0.04) : _VolTint.w, straight = 1.0;
            for (int once = stopsShare > 0.0 && firelight > 0.01 ? 0 : 1; once < 1; once++)
            {
                float stops = 0.5 * _VolParams.x * stopsShare, lampLit = 0.0, lampStraight = 0.0;
                for (int n = 0; n < 3; n++)
                {
                    float gives = n == 0 ? fromLamp.x : n == 1 ? fromLamp.y : fromLamp.z;
                    if (gives <= 0.0) continue;
                    vec3 way = (n == 0 ? atA : n == 1 ? atB : atC) - here;
                    vec2 onWay = vec2(textureLod(_Around, (here + way * 0.333 - _VolOffset.xyz) / _VolSize.xyz + 0.5, 0.0).r, textureLod(_Around, (here + way * 0.667 - _VolOffset.xyz) / _VolSize.xyz + 0.5, 0.0).r);
                    // (A launch's steam by night, since 2026-11-20: the flame's light sent on inside the steam, a second way that
                    // stops less, as cloud-makers add for light scattered more than once. Stopped by the straight way alone, the shade
                    // the stopping made was filled by the floodlights and the night sky, and went grey, where NASA's photograph of a
                    // Falcon 9 by night has its shade brown: the flame's own light, come round through the steam.)
                    float tau = dot(onWay, onWay) * stops * length(way);
                    float byStraight = gives * exp(-tau) / (1.0 + _VolSteamTone.x);
                    lampLit += byStraight + gives * _VolSteamTone.x * exp(-tau * _VolSteamTone.y) / (1.0 + _VolSteamTone.x);
                    lampStraight += byStraight;
                }
                straight = lampLit > 1e-6 ? lampStraight / lampLit : 1.0;
                firelight *= lampLit / allLamps;
            }
            // (A launch's steam by night, since 2026-11-20: the flame's light the straight way whiter than what comes round through the
            // steam. The flame itself is white-hot; what the steam sends on has been through it, and is the amber of the steam it
            // lit. In NASA's photograph of a Falcon 9 by night the billows' lit tops are cream, blue half of red, and their shade
            // brown, blue a third of red; ours were the one amber all through, blue a quarter of red lit or not. _VolSteamTone.w how
            // much whiter, nought as until then.)
            vec3 glowHere = _VolGlow.rgb;
            if (_VolSteamTone.w > 0.0) glowHere = mix(glowHere, vec3(dot(glowHere, vec3(0.2126, 0.7152, 0.0722))), _VolSteamTone.w * straight);
            // Beside a fire the eye is taken up by the fire: the daylight on the smoke there counts for little, and it shows by the firelight.
            // (Not round a fire in the open since 2026-10-18: the film's eye goes by the whole picture, and with the daylight taken off
            // the smoke round a fire its billows lost their light and shade, and the cloud was all one orange.)
            float dazzle = folded ? 1.0 : 1.0 - 0.85 * min(seenFire * 1.6, 1.0);
            // (A big fire's light on the smoke beside it is many times the sun's (a fireball's face at 1300 K gives off some fifty times
            // the sun's light for every bit of sky it fills): twice as strong round a fire in the open since its light has been stopped
            // by the smoke on its way and the daylight left on the smoke, so that what it reaches glows orange, as at dusk it did before.)
            vec3 light = (_VolSun.rgb * (sun * shine * relief) + _VolAmb.rgb * ((0.3 * (1.0 - _VolUpLocal.w * 0.5 * cleft) + 0.7 * skyAll) * skyRelief)) * dazzle + glowHere * (firelight * (folded && _VolFire.w < 2.5 ? _VolFireLight.w : 4.0));
            // (and the lamps of the scene: at night a launch's smoke in the floodlights is not left black where its own fire does not reach)
            if (_VolSceneA.w > 0.0)
            {
                vec3 sceneA = sceneLamp(here, _VolSceneA, _VolSceneTintA, _VolSceneDirA), sceneB = sceneLamp(here, _VolSceneB, _VolSceneTintB, _VolSceneDirB);
                vec3 sceneC = sceneLamp(here, _VolSceneC, _VolSceneTintC, _VolSceneDirC), sceneD = sceneLamp(here, _VolSceneD, _VolSceneTintD, _VolSceneDirD);
                vec3 scene = sceneA + sceneB + sceneC + sceneD;
                // (Since 2026-11-20 shaded by the billows and stopped by the smoke between, as the sun's light and the fires' are. Lit
                // evenly through, all the smoke the floodlights reached was one flat grey, brighter or darker by how far it was from
                // them: billow stood against billow as flat shapes with hard edges, and smoke they hardly reached, beside it, black. By
                // night after a launch black smoke showed through the light grey (the owner, with a picture, that evening). Taken
                // towards where the lamps' light comes from on the whole (each lamp's place, weighed by how much of its light is
                // here), from the coarse grid of how much smoke lies round about, at a third and two thirds of the way across the box
                // that way (the lamps stand outside it); part of it going round inside the smoke, which stops less: steam throws on
                // nearly all the light it meets. _VolSceneShade nought: as until then.)
                vec3 lum3 = vec3(0.2126, 0.7152, 0.0722);
                float sceneAll = dot(scene, lum3);
                if (_VolSceneShade.x + _VolSceneShade.w > 0.0 && sceneAll > 1e-5)
                {
                    vec3 lampsAt = (_VolSceneA.xyz * dot(sceneA, lum3) + _VolSceneB.xyz * dot(sceneB, lum3) + _VolSceneC.xyz * dot(sceneC, lum3) + _VolSceneD.xyz * dot(sceneD, lum3)) / sceneAll;
                    vec3 toLamps = lampsAt - here;
                    float lampsFar = length(toLamps);
                    toLamps /= max(lampsFar, 1e-3);
                    float sceneShade = clamp(1.0 - _VolSceneShade.w * _VolUpLocal.w * _VolSunLocal.w * 0.055 * dot(slope, toLamps), 0.4, 1.6) * (1.0 - 0.4 * _VolSceneShade.w * cleft);
                    if (_VolSceneShade.x > 0.0)
                    {
                        float across = min(lampsFar, 0.5 * max(_VolSize.x, max(_VolSize.y, _VolSize.z)));
                        vec3 at1 = (here + toLamps * (0.333 * across) - _VolOffset.xyz) / _VolSize.xyz + 0.5, at2 = (here + toLamps * (0.667 * across) - _VolOffset.xyz) / _VolSize.xyz + 0.5;
                        // (nothing outside the box: the grid's edge would be read again there)
                        float in1 = step(0.0, min(at1.x, min(at1.y, at1.z))) * step(max(at1.x, max(at1.y, at1.z)), 1.0), in2 = step(0.0, min(at2.x, min(at2.y, at2.z))) * step(max(at2.x, max(at2.y, at2.z)), 1.0);
                        vec2 onWay = vec2(textureLod(_Around, at1, 0.0).r * in1, textureLod(_Around, at2, 0.0).r * in2);
                        float tau = dot(onWay, onWay) * 0.5 * _VolParams.x * _VolSceneShade.x * across;
                        sceneShade *= (exp(-tau) + _VolSceneShade.y * exp(-tau * _VolSceneShade.z)) / (1.0 + _VolSceneShade.y);
                    }
                    scene *= sceneShade;
                }
                light += clamp(scene, 0.0, 8.0) * dazzle;
            }
            vec3 lit = sqrt(own * light);
            // (A camera's shoulder, since 2026-11-20: where the light on the smoke is more than the picture can hold in its strongest
            // colour, the others go up towards it, as a film's or a sensor's do past their top, so that light too strong to show goes
            // over to white, not to the colour of what is left. By night the pad's steam nearest the flame came out yellow,
            // 254,247,165, where NASA's photograph of a Falcon 9 by night has it cream, 253,243,205, and both the same amber below
            // that: the red had reached the top and the light's blue stayed where it was. Only what is over the top is changed;
            // _VolGlow.w how far, nought as until then.)
            float brightest = max(lit.r, max(lit.g, lit.b));
            if (brightest > 1.0) lit = mix(lit, vec3(brightest), min((brightest - 1.0) * _VolGlow.w, 0.8));
            // (A launch's steam by night, since 2026-11-20: a camera's tone curve, colour by colour, over the top of its range. A film's
            // or a sensor's curve bends each colour as it nears the top, so the strongest of a bright light's colours is held back
            // most and what is bright goes pale: in NASA's photograph of a Falcon 9 by night the lit tops of the billows are cream,
            // blue half of red, where ours were a deep gold, blue a quarter of red, the shoulder above changing only what is over the
            // top. _VolSteamTone.z how much of the range is bent, nought as until then.)
            if (_VolSteamTone.z > 0.001)
            {
                float knee = 1.0 - _VolSteamTone.z;
                lit = min(lit, vec3(knee)) + _VolSteamTone.z * (1.0 - exp(-max(lit - knee, 0.0) / _VolSteamTone.z));
            }
            // (A nuclear explosion's cap, since 2026-11-16: its cloud dark where its gas still glows. A camera set for a fireball sees
            // the daylight on the dust in it as next to nothing: the films show a dark crust with the fire glowing through its gaps,
            // and the daylit cloud only where it has cooled. Lit by the day all through, as until then, the dust in the fireball
            // stood pale over its fire for all the ten seconds a 20-kiloton's glows: a ball of peach-coloured wool.)
            if (_VolThin.w > 0.0) lit *= 1.0 - _VolThin.w * smoothstep(0.06, 0.4, air.a);
            // (Hot smoke shines: in a fuel fire the soot is what gives the light, and where the gas round the flame is still hot, so is
            // its smoke. Cooler in the creases between billows, where cold air is drawn in, and dim there, steeply, as a black body's
            // light goes with its heat. For a fire in the open, since 2026-10-18. No hotter than an orange heat: smoke is soot cooling
            // out of the flame, and at the flame's own yellow-white heat it came out pale grey-white round a young fireball at dusk.)
            for (int once = folded ? 0 : 1; once < 1; once++)
            {
                float heatS = min(air.a * 1.2 * mix(0.5, 1.0, smoothstep(0.40, 0.52, lump)), _VolFire.w > 2.5 ? 9.0 : 0.62);
                vec3 hotS = heatS < 0.25 ? _VolHot1.rgb * (heatS * 4.0) : heatS < 0.5 ? mix(_VolHot1.rgb, _VolHot2.rgb, heatS * 4.0 - 1.0) : heatS < 0.75 ? mix(_VolHot2.rgb, _VolHot3.rgb, heatS * 4.0 - 2.0) : mix(_VolHot3.rgb, _VolHot4.rgb, min(heatS * 4.0 - 3.0, 1.0));
                float h = min(heatS, 1.1);
                lit += hotS * ((0.55 + 2.2 * h * h * h) * smoothstep(_VolFireShine.x, _VolFireShine.x + 0.27, heatS) * _VolFireShine.y);
            }
            if (_VolGrid.w > 0.5 && _VolGrid.w < 2.5) { lit = vec3(0.45); burning = 0.0; }                      // (testing: no light and no flame)
            if (_VolGrid.w > 23.5 && _VolGrid.w < 24.5) { lit = mapSeen; burning = 0.0; }      // (testing: 24 the sun's-eye picture as looked up: off it, where on it in stripes; on it, red as far behind the nearest solid thing, green as far in front, blue the distance in hundreds of metres)
            else if (_VolGrid.w > 22.5 && _VolGrid.w < 23.5) { lit = vec3(1.0 - solidLit, solidLit, 0.0) * (_VolSunMapAlong.w > 0.5 ? 1.0 : 0.3); burning = 0.0; }      // (testing: 23 what stands in the sun's way: red in its shadow, green not, dim where this patch has no sun's-eye picture)
            else if (_VolGrid.w > 16.5) { lit = mix(lit, vec3(1.0, 0.12, 0.08), min(wakeShown.x * 1.5, 1.0)) + vec3(0.0, min(wakeShown.y * 0.3, 0.8), 0.0); burning = 0.0; }      // (testing: 17 where the wakes are)
            else if (_VolGrid.w > 14.5) burning = 0.0;                 // (testing: 15 everything but the flame,
            else if (_VolGrid.w > 13.5) lit = vec3(0.0);                // 14 the flame alone, on black smoke)
            else if (_VolGrid.w > 9.5) { burning = 0.0; lit = _VolGrid.w < 10.5 ? vec3(sun * shine * relief) : _VolGrid.w < 11.5 ? vec3(sky) : _VolGrid.w < 12.5 ? own * 3.0 : vec3(firelight); }      // (testing: 10 the sun's light alone, 11 the sky's, 12 the smoke's own colour, 13 the firelight)
            else if (_VolGrid.w > 4.5) { burning = 0.0; lit = _VolGrid.w < 5.5 ? vec3(lump) : _VolGrid.w < 6.5 ? fract((was + testFrom) * _VolDetail.w) : _VolGrid.w < 7.5 ? abs(testFrom) / 30.0 : _VolGrid.w < 8.5 ? abs(gone) * 2.0 : vec3(going.a, 1.0 - going.a, 0.0); }      // (testing: 5 the billows, 6 where in their pattern, 7 how far from where it "was", 8 how far carried on, 9 how squeezed the pattern is: green not at all, red sixteen times)

            float both = sigma + burning;
            float stepLength = min(stride * finest, t1 - t);
            float a = 1.0 - exp(-both * stepLength);        // the last step stops at whatever solid thing is behind
            colour += through * a * (lit * sigma + hot * (burning * 1.8)) / max(both, 1e-5);
            float hidHere = through * a, tHere = t + 0.5 * stepLength;
            deep += hidHere * tHere;
            deep2 += hidHere * tHere * tHere;
            through *= 1.0 - a;
            thinRun = flame <= 0.0 && both * stepLength < 0.0020 ? thinRun + 1.0 : 0.0;
        }
        else if (fine)
        {
            clear += stride;
        }
        k += stride;
        if (fine)
        {
            // Once most of what can be seen along this ray has been passed the steps are four times as long. And each time
            // the steps allowed for are used up they are doubled, and half as many allowed again: so a ray is walked finely
            // for as long as it can be, and more and more coarsely after. (All at once to four times as long, as it once
            // was, thick smoke seen through thin from close by was speckled: from there the steps are short and soon used up.)
            if (through < 0.3 && longer < 3.5) { if (mod(k, stride * 4.0 / longer) < 0.5) { stride *= 4.0 / longer; longer = 4.0; } }
            else if (left <= 0.0 && longer < 7.5 && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
            // And in thin smoke longer still: after three steps in a row of smoke too thin to hide a five-hundredth each, with no flame on
            // the grid there, and once two fifths of the steps allowed for are used (or the steps are longer already), they are doubled,
            // up to four times as long, and short again at thicker smoke or flame (see above). From inside a booster's cloud on the pad a ray
            // spent its steps a tenth of a metre long in the thin smoke round the camera (two hundred of them, and a tenth of the rays at the
            // loop's end) and had them doubled and doubled again further on, where the smoke that can be seen is; walked so, it took a quarter
            // less time and came out nearer to one walked with no limit on its steps (from a mean of 0.9 of 255 apart to 0.4).
            else if (thinRun >= 3.0 && thinLonger < 4.0 && (left < 0.6 * _VolParams.y || longer > 1.5) && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; thinLonger *= 2.0; thinRun = 0.0; }
            // And out of the smoke, as long as the grid's cells again.
            if (clear >= coarse && mod(k, coarse) < 0.5) { fine = false; stride = coarse; thinLonger = 1.0; thinRun = 0.0; }
        }
    }
    // The walk stops when no more than a fiftieth of what is behind the smoke would still get through. That fiftieth
    // is not let through: smoke thick enough to have stopped the walk hides everything, and all smoke hides a
    // fiftieth more than the walk made it, so that there is no step where the one becomes the other. (Let through,
    // it was enough for a bright sky, or the line of the horizon, to show faintly through the thickest smoke.)
    float hides = 1.0 - through, all = min(hides / 0.98, 1.0);
    SV_Target0 = vec4(colour * (all / max(hides, 1e-6)), all);
    float meanDepth = deep / max(hides, 1e-6), spread = sqrt(max(deep2 / max(hides, 1e-6) - meanDepth * meanDepth, 0.0));
    SV_Target1 = vec4(all * meanDepth * _VolCamera.w, all * spread * _VolCamera.w, 0.0, all);
    if (_VolThin.z > 0.5) SV_Target0 = vec4(rounds / vec3(_VolThin.z), 1.0);       // (testing: the rounds, over so many: red all, green in smoke, blue leaps)
}
#endif
