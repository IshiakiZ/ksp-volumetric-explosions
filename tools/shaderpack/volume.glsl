#ifdef VERTEX
#version 150
uniform vec4 hlslcc_mtx4x4unity_ObjectToWorld[4];
uniform vec4 hlslcc_mtx4x4unity_MatrixVP[4];
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
    gl_Position = clip;
    vs_TEXCOORD0 = world.xyz;
    vs_TEXCOORD1 = clip;
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
uniform vec4 _VolStep;       // x: length of the finest step in metres
uniform vec4 _VolGrid;       // x: fine steps to a step through clear air (a power of two, about a cell of the grid), y: how many of the screen's pixels wide a step may look, z: cells of the detail to a metre, times how many of the screen's pixels the smallest lump drawn should cover, w: (for testing) 1: the smoke on the grid alone, 2: with its puffs but unlit, 3: lit but without puffs
uniform vec4 _VolSize;       // xyz: size of the box in metres
uniform vec4 _VolOffset;     // xyz: the middle of the box, in metres from the site's origin
uniform vec4 _VolDetail;     // xyz: how far the billows' pattern had been carried along with the smoke as a whole when the grid in use was made, in metres, w: repeats of the pattern per metre
uniform vec4 _VolFlow;       // x: twice how far the fastest smoke on the grid has gone since the grid was made, in metres, yw: what turns a number read from the textures of where the smoke "was" into metres (times y, plus w), z: the farthest the smoke is carried on from where the grid has it, in metres
uniform vec4 _VolPeak;       // x: 1 if this is drawn at half size, to be enlarged, y: how smooth the flame is (0: licked into tongues by the billows, as a fire in the open is; 1: not at all, as an engine's jet in a vacuum), z: (for testing) steps moved along by this part of a step, w: metres of the thickest smoke that count as thick
uniform vec4 _VolThin;       // x: how much of what is behind it a ray's smoke may hide, at the very most, before any of it is drawn (nought: all of it is drawn), y: how long ago the grid in use was made, in seconds
uniform vec4 _VolSun;        // rgb: sunlight, w: how much thin smoke in front of the sun shines
uniform vec4 _VolSunDir;     // xyz: towards the sun
uniform vec4 _VolSunLocal;   // xyz: the same, in the site's own axes (east, up, north), w: how strongly the billows shade each other
uniform vec4 _VolAmb;        // rgb: light from the sky
uniform vec4 _VolGlow;       // rgb: light from the fire
uniform vec4 _VolLampA;      // xyz: where the strongest fire is, in metres from the site's origin, w: its size squared;
uniform vec4 _VolLampB;      // the second
uniform vec4 _VolLampC;      // and the third
uniform vec4 _VolLamps;      // xyz: how bright each of the three is
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
uniform vec4 _VolJet;        // A ship's patch (its engines' flames): xyz which way the jets go, in the grid's axes, one long; w how far the flame's pattern has been carried down them, metres
uniform vec4 _VolJetFrom;    // xyz: the mouth of the strongest nozzle, in the grid's axes; w: its radius, metres
uniform vec4 _VolJetLook;    // x: how far the flame's edge is torn into tongues (0 to 1); y: how sooty it is; z: how strong its shock diamonds are; w: the size of its pattern, metres (nought: not a ship's patch)
uniform vec4 _VolJetBody;    // x: how much flame makes the flame's body whole (a clear, faint flame is whole where there is any of it); y: how many shock diamonds there are
uniform vec4 _VolFire;       // A fire in the open (not an engine's): x how far its flames' pattern has risen through the burning gas, metres; y one over the size of its tongues, per metre; z how far thin flame is torn into tongues (0 to 1); w 1 to draw it so (0: as a glow, as until 2026-10-10)
uniform vec4 _VolTint;       // rgb: the hue of the dust here
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
uniform sampler3D _Around;   // r: how much smoke there is round about, over a couple of metres (square root), g: more than nothing if there is any smoke or flame near, b: how light that smoke is (square root), a: how much of it is dust
uniform sampler3D _Flow;     // rgb: which way the smoke is moving and how fast, as a share of the fastest (0.5: not at all), a: how much the pattern of billows is squeezed there (1: to a sixteenth)
uniform sampler3D _RestA;    // how far the smoke at a place is from where it "was", by each of six reckonings (three for the big billows, then three for the small), east, up and north:
uniform sampler3D _RestB;    // eighteen numbers of sixteen bits, four to a texture, one after another
uniform sampler3D _RestC;
uniform sampler3D _RestD;
uniform sampler3D _RestE;    // (its third number is something else: how fast the smoke at a place is going through its round of turns, in rounds a second)
uniform sampler3D _Turns;    // whose turn it is at a place: rg the point of its round the smoke there is at, as a point on a circle (0.5: the middle of it), for the three reckonings the big billows go by; ba the same for the small billows' three
uniform sampler3D _Clear;    // r: how many cells of the coarser grid it is from this one to the nearest with any smoke or flame in it, over 255 (nought: there is some in this one). Read cell by cell, not between cells.
uniform sampler3D _Detail;   // r: how much the smoke is gathered (lumps upon lumps), gba: the slope of that, each way
uniform sampler2D _CameraDepthTexture;
in vec3 vs_TEXCOORD0;
in vec4 vs_TEXCOORD1;
layout(location = 0) out vec4 SV_Target0;
// (and, where the picture drawn into has a second part: how far along the ray what the smoke hides lies, on the whole, and how
// widely that is spread along it (r and g), each times how much it hides, times _VolCamera.w. With them the patches' smoke is put
// in order pixel by pixel: see layers.glsl.)
layout(location = 1) out vec4 SV_Target1;

// The billows at one place. The pattern is lumps upon lumps (cellular noise), read twice: once as it is,
// for the small billows, and once much larger and turned another way, for the big. No lump of the one lines up
// with a lump of the other, so although each repeats every few metres, the two together never show the same
// shape twice. How finely they are read depends on how far away the place is: the smallest lumps drawn are
// a pixel or two across. (Were it to follow the length of the steps, which changes in jumps, parts of a
// cloud would go blurred and sharp again as they moved; it does so only deep along a ray, where the steps
// have had to be lengthened and nothing finer than them can be read truly.) From so far off that even the biggest lumps are
// a pixel or two across they are left out: read that coarsely, the pattern is no longer lumps but the lattice
// it repeats on. Each adds how much more (or less) than usual the smoke is gathered there, and which way that slopes,
// times what its set counts for just now (seen: see the walk below, which leaves a set that counts for nothing unread).
// (The bigger lumps count for twice the smaller: a lump no deeper than a small one would not show as big.)
void bigBillows(vec3 q, float level, float seen, inout float lump, inout vec3 slope)
{
    vec3 turned = vec3(dot(q, vec3(0.80, -0.48, 0.36)), dot(q, vec3(0.60, 0.64, -0.48)), dot(q, vec3(0.0, 0.60, 0.80))) * 0.37 + vec3(0.31, 0.67, 0.13);
    vec4 broad = textureLod(_Detail, turned, max(level - 1.43, 0.0));
    lump += 0.9 * seen * (broad.r - 0.45);
    vec3 way = (broad.gba - 0.5) * (2.0 * 10.72 * 0.37 * 1.4 * seen);
    slope += vec3(dot(way, vec3(0.80, 0.60, 0.0)), dot(way, vec3(-0.48, 0.64, 0.60)), dot(way, vec3(0.36, -0.48, 0.80)));
}

void smallBillows(vec3 q, float level, float seen, inout float lump, inout vec3 slope)
{
    vec4 fine_ = textureLod(_Detail, q, max(level, 0.0));
    lump += 0.45 * seen * (fine_.r - 0.45);
    slope += (fine_.gba - 0.5) * (2.0 * 10.72 * 0.7 * seen);
}

// How much of the smoke at a place is left where an engine is leaving a trail.
// The grid is a moment old, and the engine that is laying the smoke may be doing hundreds of metres a second: the
// head of its trail, as any one grid has it, is where the engine was when that grid was begun, and stays there
// until the next grid, while the engine goes on. A trail drawn so began a rocket's length behind the rocket and
// caught it up in jumps. So the mod lays the smoke a little way ahead of the engine, where it is about to be, and
// says here every frame exactly where the engine now is: ahead of that, along the line it is flying, the smoke is
// not drawn yet, and for a little way behind it comes on by degrees, as the engine's own smoke does along its flame.
float begun(vec3 here, vec4 a, vec4 b, vec4 c)
{
    vec3 d = here - a.xyz;
    float back = dot(d, b.xyz);
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

// An engine's flame, as its pattern is seen: in the nozzle's own frame, not the gas's. The gas goes through a flame at a
// hundred metres a second and more, and a pattern carried with it is smeared to nothing by the game's smoothing from one
// frame to the next: the flame was a blur of colour. What is seen of a real one holds its place on the nozzle and flows
// down it: tongues and streaks drawn out along the jet, coming on at the nozzle and carried down it far slower than the
// gas. So the pattern here is read in the jet's own measure (two ways across it, one along it drawn out three times) and
// carried down it by _VolJet.w, at three sizes. 0 to 1, about 0.45 on the whole.
float jetPattern(vec3 here, float level, out float along, out float aside)
{
    vec3 axis = _VolJet.xyz;
    vec3 rel = here - _VolJetFrom.xyz;
    along = dot(rel, axis);
    vec3 across = rel - axis * along;
    aside = length(across);
    vec3 u = normalize(cross(axis, abs(axis.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
    vec3 v = cross(axis, u);
    vec3 q = vec3(dot(across, u), dot(across, v), (along - _VolJet.w) * 0.33) / _VolJetLook.w;
    float l = max(level - 0.5, 0.0);
    float n1 = textureLod(_Detail, q * 0.31 + vec3(0.13, 0.57, 0.91), l).r;
    float n2 = textureLod(_Detail, q * 0.83 + vec3(0.71, 0.29, 0.43), l + 0.6).r;
    float n3 = textureLod(_Detail, q * 2.2 + vec3(0.37, 0.83, 0.17), l + 1.4).r;
    return 0.5 * n1 + 0.33 * n2 + 0.17 * n3;
}

// A fire's flames in the open, as their pattern is seen (see the flame in the walk). Real flames are thin sheets where the fuel
// vapour meets the air, wrinkled at every size by the turmoil of the hot gas, so that a fire shows sharp-edged tongues and folds,
// not a glow: whole and brightest low down where it burns steadily, torn into tongues above that flicker as they rise and tear
// off at their tips (a pool fire's continuous and intermittent flame, McCaffrey 1979), a fireball's surface a mass of burning
// folds. The pattern is carried with the burning gas (read where the gas "was", as the billows are) and rises through it as the
// flames lick upward, stretched up a little as they are; at a size that goes with the fire (see TuneFire). Two sizes of lump,
// 0 to 1, about 0.45 on the whole.
float firePattern(vec3 was, float level)
{
    vec3 q = vec3(was.x, was.y * 0.62, was.z) * _VolFire.y;
    float rise = _VolFire.x * _VolFire.y;
    // (as finely as the billows are read where they are, for the size of these lumps against theirs)
    float l = max(level + log2(0.093 * _VolFire.y / max(_VolDetail.w, 1e-4)), 0.0);
    float n1 = textureLod(_Detail, (q - vec3(0.0, rise, 0.0)) * 0.093 + vec3(0.29, 0.61, 0.17), l).r;
    float n2 = textureLod(_Detail, (q - vec3(0.0, 1.7 * rise, 0.0)) * 0.25 + vec3(0.83, 0.07, 0.53), l + 1.4).r;
    return 0.62 * n1 + 0.38 * n2;
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

    // Thin smoke between the camera and the sun shines.
    float toward = max(dot(rd, _VolSunDir.xyz), 0.0);
    float shine = 1.0 + _VolSun.w * toward * toward * toward * toward;
    vec3 colour = vec3(0.0);
    float through = 1.0;
    float deep = 0.0, deep2 = 0.0;      // the depth of each step along the ray (and its square), weighed by how much it hid of what is behind (see SV_Target1)
    for (int i = 0; i < 288; i++)
    {
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
                if (to > k) { k = to; continue; }
            }
        }
        // The grid is a moment old (it takes a few frames to make), and the smoke has moved on since. So the grid
        // is read not at this place but where the smoke now here was when the grid was made, going by how fast
        // the smoke about here was moving then: between one grid and the next the whole cloud is carried
        // smoothly on, instead of standing still and then jumping.
        vec4 going = textureLod(_Flow, p + 0.5, 0.0);
        vec2 wakeShown = vec2(0.0);      // (testing: see TestView 17 below)
        vec3 gone = (going.xyz - 0.5) * _VolFlow.x;
        gone *= min(1.0, _VolFlow.z / max(length(gone), 1e-4));
        vec3 here = p * _VolSize.xyz + _VolOffset.xyz;
        vec3 was = here - gone - _VolDetail.xyz;
        vec3 at = p + 0.5 - gone * perSize;
        vec4 tone = textureLod(_Around, at, 0.0);
        if (tone.g <= 0.0 && !fine)
        {
            k = (floor(k / (2.0 * coarse)) + 1.0) * 2.0 * coarse;
            continue;
        }
        vec4 amount = textureLod(_Amount, at, 0.0);
        float smoke = amount.r * 0.99611 + amount.g * 0.0038911, flame = amount.b * 0.99611 + amount.a * 0.0038911;
        if (_VolCutA.w > 0.0) smoke *= begun(here, _VolCutA, _VolCutB, _VolCutC);
        if (_VolCutD.w > 0.0) smoke *= begun(here, _VolCutD, _VolCutE, _VolCutF);
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
                continue;
            }
        }
        if (some)
        {
            clear = 0.0;

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
            float bigNear = 1.0 - smoothstep(2.6, 4.0, level - 1.43), smallNear = 1.0 - smoothstep(2.6, 4.0, level);
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
                vec4 turn = textureLod(_Turns, at, 0.0) * 2.0 - 1.0;
                r5 = textureLod(_RestE, at, 0.0) * _VolFlow.y + _VolFlow.w;
                float on = 6.2831853 * r5.b * _VolThin.y, c = cos(on), s = sin(on);
                vec2 big2 = vec2(turn.r * c - turn.g * s, turn.r * s + turn.g * c);
                float c2 = c * c - s * s, s2 = 2.0 * s * c;
                vec2 small2 = vec2(turn.b * c2 - turn.a * s2, turn.b * s2 + turn.a * c2);
                vec3 share = max(0.5 - 0.5 * (big2.x * vec3(1.0, -0.5, -0.5) - big2.y * vec3(0.0, 0.8660254, -0.8660254)), 0.0);
                // (One that has come to the end of its turn since the grid was made, and begun afresh, counts for nothing
                // until the next grid comes: the grid still has where the smoke "was" by it as it was before. It is the
                // one that was in the second half of its turn then and is in the first half now. Where the smoke is not
                // all at the same point of its round, that goes for the part of it that is at this one: the rest counts on.)
                vec3 was2 = turn.r * vec3(0.0, 0.8660254, -0.8660254) + turn.g * vec3(1.0, -0.5, -0.5), now2 = big2.x * vec3(0.0, 0.8660254, -0.8660254) + big2.y * vec3(1.0, -0.5, -0.5);
                share = mix(share, vec3(0.5 * max(1.0 - length(turn.rg), 0.0)), step(was2, vec3(-1e-4)) * step(vec3(0.0), now2));
                bigSeen = bigNear * share * inversesqrt(max(dot(share, share), 1e-6));
                share = max(0.5 - 0.5 * (small2.x * vec3(1.0, -0.5, -0.5) - small2.y * vec3(0.0, 0.8660254, -0.8660254)), 0.0);
                was2 = turn.b * vec3(0.0, 0.8660254, -0.8660254) + turn.a * vec3(1.0, -0.5, -0.5); now2 = small2.x * vec3(0.0, 0.8660254, -0.8660254) + small2.y * vec3(1.0, -0.5, -0.5);
                share = mix(share, vec3(0.5 * max(1.0 - length(turn.ba), 0.0)), step(was2, vec3(-1e-4)) * step(vec3(0.0), now2));
                smallSeen = smallNear * share * inversesqrt(max(dot(share, share), 1e-6));
            }
            bvec3 big = greaterThan(bigSeen, vec3(0.0)), small = greaterThan(smallSeen, vec3(0.0));
            // (Each set is read at its own place in the pattern as well, so that three sets begun at the same spot are still three different sets.)
            vec4 r1 = vec4(0.0), r2 = vec4(0.0), r3 = vec4(0.0), r4 = vec4(0.0);
            if (big.x || big.y) r1 = textureLod(_RestA, at, 0.0) * _VolFlow.y + _VolFlow.w;
            if (big.y || big.z) r2 = textureLod(_RestB, at, 0.0) * _VolFlow.y + _VolFlow.w;
            if (big.z || small.x) r3 = textureLod(_RestC, at, 0.0) * _VolFlow.y + _VolFlow.w;
            testFrom = r1.rgb;
            if (big.x) bigBillows((was + r1.rgb) * _VolDetail.w, level, bigSeen.x, lump, slope);
            if (big.y) bigBillows((was + vec3(r1.a, r2.rg)) * _VolDetail.w + vec3(0.37, 0.11, 0.71), level, bigSeen.y, lump, slope);
            if (big.z) bigBillows((was + vec3(r2.ba, r3.r)) * _VolDetail.w + vec3(0.71, 0.59, 0.23), level, bigSeen.z, lump, slope);
            float lumpBig = lump;

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
            float cut = (eatenBig - usual) * 4.5 * about * thinly * thinly;

            // In the skirts of a cloud the big billows have very often cut the smoke away altogether before the small
            // ones are asked: there is nothing left for them to shape. Whether that is so can be told for certain
            // without reading them: at their most (every one of them a full lump) they add so much to the lump and
            // no more, which leaves at least so much eaten away; if even then nothing is left, and no flame either
            // (none here, or the lump too slight for any to show), this step comes to nothing whatever they are.
            // (Told with a little to spare, so that a rounding error cannot make it a different answer from the full sum.)
            float lumpMost = lumpBig + 0.2475 * (smallSeen.x + smallSeen.y + smallSeen.z);
            float eatenLeast = _VolParams.z * clamp(1.25 - 1.6 * lumpMost, 0.0, 1.0);
            if (_VolGrid.w < 0.5 && fill - eatenLeast * edge - cut < -1e-5 && (flame <= 0.0 || (lumpMost < 0.1799 && _VolJetLook.w <= 0.0 && _VolFire.w < 0.5)))
            {
                // (what follows is all that such a step does: see where the flame is worked out, below)
                left -= 1.0;
                k += stride;
                if (fine && left <= 0.0 && longer < 7.5 && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                continue;
            }
            if (small.y || small.z) r4 = textureLod(_RestD, at, 0.0) * _VolFlow.y + _VolFlow.w;
            if (small.x) smallBillows((was + r3.gba) * _VolDetail.w, level, smallSeen.x, lump, slope);
            if (small.y) smallBillows((was + r4.rgb) * _VolDetail.w + vec3(0.53, 0.29, 0.17), level, smallSeen.y, lump, slope);
            if (small.z) smallBillows((was + vec3(r4.a, r5.rg)) * _VolDetail.w + vec3(0.19, 0.83, 0.61), level, smallSeen.z, lump, slope);

            float eaten = _VolParams.z * clamp(1.25 - 1.6 * lump, 0.0, 1.0);
            float kept = max(fill - eaten * edge - cut, 0.0) / (1.0 - 0.6 * eaten);
            // (Just inside where it is cut away the smoke comes on gently: thick smoke that began at once at its full thickness had an edge like card.)
            float whole = fill > 1e-4 ? min(kept / fill, 1.6) : 0.0;
            float sigma = smoke * _VolParams.x * whole * min(1.25 * whole, 1.0);
            if (_VolGrid.w > 0.5 && (_VolGrid.w < 1.5 || _VolGrid.w > 2.5)) sigma = smoke * _VolParams.x;      // (testing: the smoke as it is on the grid, not cut into)
            float thin = exp(-sigma * _VolPeak.w);

            // Flame: licked into tongues, flickering, and coloured by how hot it is.
            float burning, fireN = -1.0;
            if (_VolFire.w > 0.5 && _VolJetLook.w <= 0.0 && flame > 0.0)
            {
                // A fire in the open (see firePattern): where the flame is thick it burns whole; where it thins (its edge, the tops
                // of its tongues) the pattern cuts it into tongues with sharp edges (the steep step from nothing to flame is what
                // makes a fire read as one: a soft ramp is a glow), brighter in the lumps of the pattern; and its cooler, thinner
                // parts are dark with the soot it makes, pockets in the flame and smoke rolling off its tips.
                fireN = firePattern(was, level);
                // (and as it cools on its way up, it thins: so low down, where it is hot, it burns whole and steady, higher up it
                // breaks into tongues, and above them it is gone: a pool fire's continuous flame, its tongues, and its plume)
                float hotHere = textureLod(_Volume, at, 0.0).a;
                // (Most of a fire's flame burns whole: only where it is thin is it torn. Cut more keenly, as first tried, a young
                // fireball, which should be luminous all through, was smoke with a few bright patches in it.)
                float body = (1.0 - exp(-flame * _VolParams.w * 2.0)) * smoothstep(0.05, 0.35, hotHere);
                float tongue = smoothstep(0.30, 0.40, body * 1.2 + (fireN - 0.5) * 1.2 * _VolFire.z + (lump - 0.45) * 0.4);
                burning = flame * _VolParams.w * tongue * (0.7 + 0.9 * fireN);
                // (soot only where it has cooled: a fuel fireball is luminous all through for its first second)
                sigma += flame * _VolParams.w * _VolFire.z * 0.45 * smoothstep(0.5, 0.25, fireN) * (1.0 - tongue) * smoothstep(0.75, 0.35, hotHere);
            }
            else burning = flame * _VolParams.w * mix(clamp(2.5 * lump - 0.45, 0.0, 1.8), 0.7, _VolPeak.y);
            // An engine's flame (see jetPattern): where it is thin, at its edge and its far end, the pattern tears it into
            // tongues with sharp edges, and where it is thick it burns whole; brighter and dimmer along the pattern's
            // streaks; in thick air with shock diamonds in its core, a few nozzles' widths long; and a sooty fuel's
            // flame darkened by its own soot in the cooler streaks of its outer part.
            float jetN = -1.0, diamonds = 0.0;
            if (_VolJetLook.w > 0.0 && flame > 0.0)
            {
                float along, aside;
                jetN = jetPattern(here, level, along, aside);
                float f = 1.0 - exp(-flame * _VolParams.w * _VolJetBody.x);
                float shape = smoothstep(0.32, 0.52, f * 1.25 + (jetN - 0.45) * 2.6 * _VolJetLook.x);
                float spacing = 2.6 * _VolJetFrom.w;
                float diamond = pow(max(cos(6.2831853 * along / spacing), 0.0), 10.0) * exp(-along / (2.5 * spacing)) * step(0.3 * spacing, along) * step(along, (_VolJetBody.y + 0.3) * spacing)
                              * exp(-aside * aside / (0.5 * _VolJetFrom.w * _VolJetFrom.w));
                diamonds = _VolJetLook.z * diamond;
                // (a diamond is a thin disc of gas made hotter by the shock in it: it glows of itself, however faint the flame
                // round it is, as in a hydrogen engine's clear flame, and in the hot colours)
                burning = flame * _VolParams.w * (shape * (0.45 + 1.2 * jetN) + 2.2 * diamonds);
                sigma += flame * _VolParams.w * _VolJetLook.y * smoothstep(0.5, 0.25, jetN) * (1.0 - f) * 0.6;
            }
            // (Flame is a glow, and can be stepped through twice as fast as smoke. A step where the billows have cut
            // everything away counts in full, though there was nothing to light: there are only so many steps to a
            // ray, and counted for less, as they once were, a ray that began in the thin skirts of a cloud spent them
            // all there and never reached the body of it: from close by, windows clean through the smoke with hard edges.)
            left -= burning > sigma ? 2.0 : 1.0;
            if (_VolGrid.w < 0.5 && sigma + burning <= 0.0)
            {
                k += stride;
                if (fine && left <= 0.0 && longer < 7.5 && mod(k, 2.0 * stride) < 0.5) { stride *= 2.0; longer *= 2.0; left = 0.5 * _VolParams.y; }
                continue;
            }
            vec4 air = textureLod(_Volume, at, 0.0);
            float heat = jetN >= 0.0 ? air.a * 1.2 * (0.38 + 1.25 * jetN) + 0.9 * diamonds : fireN >= 0.0 ? air.a * 1.2 * (0.5 + 0.85 * fireN) : air.a * 1.2 * (0.72 + 0.56 * mix(lump, 0.5, _VolPeak.y));
            vec3 hot = heat < 0.25 ? _VolHot1.rgb * (heat * 4.0) : heat < 0.5 ? mix(_VolHot1.rgb, _VolHot2.rgb, heat * 4.0 - 1.0) : heat < 0.75 ? mix(_VolHot2.rgb, _VolHot3.rgb, heat * 4.0 - 2.0) : mix(_VolHot3.rgb, _VolHot4.rgb, min(heat * 4.0 - 3.0, 1.0));
            // (A fire's light goes with its heat far more steeply than its colour does (a black body's light in what the eye sees
            // grows many times over between a dull red and a yellow heat): its hottest parts, low in it and in the lumps of the
            // pattern, blaze yellow-white, its edges a dim deep red. Spread evenly, as before, it was a warm haze.)
            if (fireN >= 0.0) { float h = min(heat, 1.1); hot *= 0.55 + 2.2 * h * h * h; }

            // Smoke: its own colour, in the light that reaches it. Thinned out, dark smoke looks paler. And each puff
            // has a lit side and a shaded one: where the smoke gets thicker towards the sun, this place is darker.
            vec3 own = tone.b * tone.b * mix(vec3(1.0), _VolTint.rgb, tone.a);
            own += vec3(0.05, 0.048, 0.045) * thin * step(own.r, 0.3);
            float relief = clamp(1.0 - _VolSunLocal.w * 0.055 * dot(slope, _VolSunLocal.xyz), 0.45, 1.6);
            // The sun's light, after the smoke that lies towards the sun (a part of it gets much further than the rest: light
            // finds its way round inside a cloud. Much of it in pale smoke, which throws nearly all the light it meets on to
            // the next bit; little in soot, which swallows it), the light of the open sky, and that of the fires near by.
            float before = (air.r * 0.99611 + air.g * 0.0038911) * 16.0;
            float strays = 0.12 + 0.5 * min(tone.b * tone.b * 1.4, 1.0);
            // Down in the clefts between billows less light of any kind gets in.
            float cleft = eaten * about;
            float sun = ((1.0 - strays) * exp(-before) + strays * exp(-0.25 * before)) * (1.0 - 0.3 * cleft);
            float sky = exp(-air.b * 8.0) * (1.0 - 0.55 * cleft);
            vec3 toA = here - _VolLampA.xyz, toB = here - _VolLampB.xyz, toC = here - _VolLampC.xyz;
            float firelight = min(_VolLamps.x * _VolLampA.w / (dot(toA, toA) + _VolLampA.w + 1e-4) + _VolLamps.y * _VolLampB.w / (dot(toB, toB) + _VolLampB.w + 1e-4) + _VolLamps.z * _VolLampC.w / (dot(toC, toC) + _VolLampC.w + 1e-4), 1.0);
            // Beside a fire the eye is taken up by the fire: the daylight on the smoke there counts for little, and it shows by the firelight.
            float dazzle = 1.0 - 0.85 * min(firelight * 1.6, 1.0);
            vec3 light = (_VolSun.rgb * (sun * shine * relief) + _VolAmb.rgb * (0.3 + 0.7 * sky)) * dazzle + _VolGlow.rgb * (firelight * 4.0);
            // (and the lamps of the scene: at night a launch's smoke in the floodlights is not left black where its own fire does not reach)
            if (_VolSceneA.w > 0.0) light += clamp(sceneLamp(here, _VolSceneA, _VolSceneTintA, _VolSceneDirA) + sceneLamp(here, _VolSceneB, _VolSceneTintB, _VolSceneDirB)
                                                 + sceneLamp(here, _VolSceneC, _VolSceneTintC, _VolSceneDirC) + sceneLamp(here, _VolSceneD, _VolSceneTintD, _VolSceneDirD), 0.0, 8.0) * dazzle;
            vec3 lit = sqrt(own * light);
            if (_VolGrid.w > 0.5 && _VolGrid.w < 2.5) { lit = vec3(0.45); burning = 0.0; }                      // (testing: no light and no flame)
            if (_VolGrid.w > 16.5) { lit = mix(lit, vec3(1.0, 0.12, 0.08), min(wakeShown.x * 1.5, 1.0)) + vec3(0.0, min(wakeShown.y * 0.3, 0.8), 0.0); burning = 0.0; }      // (testing: 17 where the wakes are)
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
            // And out of the smoke, as long as the grid's cells again.
            if (clear >= coarse && mod(k, coarse) < 0.5) { fine = false; stride = coarse; }
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
}
#endif
