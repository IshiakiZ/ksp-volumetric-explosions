using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// (Since 2026-11-09, the way in's Level 7: Sources.Nuke.) A nuclear explosion, worked out from the physics of one: its flash and
    /// the light it throws on everything round it, its fireball, the cloud the fireball becomes as it rises and rolls over into a
    /// mushroom's cap, the cap levelling out where it stops, the dust its afterwinds draw up into a stem under it, the skirt of dust
    /// its blast lifts off the ground as it sweeps out, and the cloud its shock leaves for a moment in damp air: all scaled with its
    /// yield, and lasting minutes. Asked for by the owner on 2026-11-09 for BD Armory's nukes ("we have to engineer a nuke explosion
    /// too"); the sources and the sums are research/armory/nuke.md, and the rise was worked out first in tools/dev/scratch-2026-11-09-nuke/rise.py.
    ///
    /// It is drawn in patches of its own, all made from one Plan and so all in the same axes: its cap (and its fireball, its flash, its
    /// shock front and the cloud behind that); the dust drawn up under it, where it went off near enough the ground to raise any; and the
    /// skirt of dust its blast lifts, where that reaches the ground hard enough. Each patch's grid is fitted to its own part, as a trail's
    /// patches are: a cap three kilometres across over a skirt twelve kilometres across in one grid would be drawn in cells the size of
    /// streets. Its smoke is moved by the same mover as any (see Nuked): the cap by a "led" thermal, whose place, size and speed are
    /// the ones worked out here, once a frame, and not left to the thermal itself; the stem by a column of rising air under it.
    /// </summary>
    public sealed partial class Site
    {
        internal const int NukeCap = 1, NukeStem = 2, NukeSkirt = 3;
        // The cap holds what is in it (see Nuked): what strays out past so much of its size (in its own measure, stretched to its shape)
        // is drawn back in, at this share of how far out it has strayed each second; and its bits follow the air this closely, one over
        // the moment they take to (a fireball's gas and the cloud that condenses in it are the air itself, and carry no way of their own).
        const float CapSkin = 0.9f, CapHold = 1f, CapDrag = 24f;

        /// <summary>
        /// One nuclear explosion, shared by its patches: what it is (scaled from its yield as Glasstone and Dolan give it), and its cloud's
        /// rise, worked out once a frame by whichever of its patches steps first. The rise is a buoyant thermal in stably stratified air
        /// (Morton, Taylor and Turner 1956; Turner 1973), with the two corrections a very hot one needs (Morton 1965; Escudier and Maxworthy
        /// 1973), its two numbers set so that a 10-kiloton burst's cloud comes out where Glasstone and Dolan's 2.17 has it (see rise.py).
        /// </summary>
        internal sealed class Nuclear
        {
            // ---- the numbers of the rise (rise.py): how fast it takes in air, the share of the yield left as heat in the fireball, a sphere's added mass
            const double Alpha = 0.34, HeatShare = 0.564, Added = 0.5;

            // ---- what it is
            public float kt, tnt;               // kilotons; and kilograms of TNT its blast is worth (half its yield: G&D 1.24)
            public float Rmax;                  // its fireball's radius at its widest, metres (180 W^0.4 ft at sea level, see Make; wider in thinner air)
            public float tmax, tlum;            // when its heat comes out fastest (G&D 7.85); how long its fireball glows
            public float height;                // how high over the ground or the sea it went off
            public float air, g, cp, temp, density;
            public bool vacuum, space, water, groundKnown;
            public float couple;                // how far its fireball is a burst on the ground: 1 touching it, 0 from three of its radii up
            public float dirt;                  // how much dust its afterwinds draw up: 1 on the ground, 0 from five of its radii up
            public float groundReach;           // how far out along the ground its front lifts dust (where it still squeezes the air by some 4 kPa)
            public bool stem, skirt;            // whether it has a patch for each
            public Color ground;                // the colour of what it raises
            public Vector3d world, groundWorld, groundNormal, velocity;
            // ---- in the axes its patches share
            public bool placed;
            public Vector3 at, foot;            // where it went off; the ground under it
            public float x, y, z;               // its cap's middle now
            public float ah, av, eh, ev;        // its cap's half-width and half-height, and how fast each grows, of itself a second
            public float W;                     // how fast its cap's middle goes up
            public float driftX, driftZ;        // and how fast sideways, as its smoke goes with the wind
            // ---- the rise
            public double t, b, M, B, zc;       // since it went off; the thermal's radius, momentum, buoyancy; its middle above where it went off
            public float w, wPeak, tStop, lift;
            public bool stopped;
            public float zStop, bStop, tStopSeen;    // (as worked out ahead, at its start)
            float[] n2;                         // the air's stability (its buoyancy frequency, squared) every 500 m above where it went off
            int frameDone = -1;

            /// <summary>The thermal's radius as the cloud's mean size: its fireball's growth over its first moments, wider on the ground.</summary>
            public float Mean => Mathf.Pow(Mathf.Max(ah * ah * av, 1e-3f), 1f / 3f);
            public float Age => (float)t;

            public static Nuclear Make(Plan plan, float kilotons, Vector3d velocity)
            {
                var n = new Nuclear { kt = Mathf.Clamp(kilotons, 0.001f, 50000f), world = plan.world, velocity = velocity };
                CelestialBody body = plan.body;
                n.g = Mathf.Max(0.05f, plan.gravity);
                n.air = plan.air;
                n.vacuum = plan.vacuum;
                n.space = plan.space;
                n.tnt = 0.5f * n.kt * 1e6f;
                n.ground = plan.dust;
                // The ground under it: what the plan sounded (700 m down), else the planet's own ground (or sea) under the place.
                if (plan.surfaceBelow)
                {
                    n.height = plan.underwater ? 0f : plan.height;
                    n.groundWorld = plan.surfacePoint;
                    n.groundNormal = plan.surfaceNormal;
                    n.water = plan.water;
                    n.groundKnown = true;
                }
                else if (body != null && body.pqsController != null)
                {
                    double lat = body.GetLatitude(plan.world), lon = body.GetLongitude(plan.world);
                    double terrain = body.TerrainAltitude(lat, lon, true);
                    bool sea = body.ocean && terrain < 0.0;
                    double surface = sea ? 0.0 : terrain;
                    n.height = (float)Math.Max(0.0, body.GetAltitude(plan.world) - surface);
                    n.groundWorld = body.GetWorldSurfacePosition(lat, lon, surface);
                    n.groundNormal = plan.up;
                    n.water = sea;
                    n.groundKnown = true;
                }
                else { n.height = 1e6f; n.groundWorld = plan.world; n.groundNormal = plan.up; }
                if (n.water) n.ground = new Color(0.9f, 0.92f, 0.94f);
                try { n.temp = (float)FlightGlobals.getExternalTemperature(plan.world, body); } catch (Exception) { n.temp = 288f; }
                if (!(n.temp > 50f)) n.temp = 288f;
                n.density = 1.225f * Mathf.Max(n.air, 1e-4f);
                n.cp = body != null && body.atmosphereContainsOxygen ? 1005f : 850f;

                // Its fireball 180 W^0.4 ft in radius at its widest at sea level (G&D 2.05: a megaton's grows to about 5,700 ft across; a
                // fireball's radius goes with the yield as W^0.4, 2.127), wider in thinner air as the cube root of how much thinner (no more
                // than four times); and its heat at its fastest after 0.0417 W^0.44 s (G&D 7.85), sooner in thinner air. Its glow lasts about
                // a minute for a megaton (G&D 2.05), and as t_max does with the yield.
                float thin = Mathf.Clamp(Mathf.Pow(Mathf.Max(n.air, 1e-4f), -1f / 3f), 0.6f, 4f);
                n.Rmax = 54.86f * Mathf.Pow(n.kt, 0.4f) * thin;
                n.tmax = Mathf.Max(0.004f, 0.0417f * Mathf.Pow(n.kt, 0.44f) * Mathf.Pow(Mathf.Clamp(n.air, 0.02f, 1f), 0.36f));
                n.tlum = Mathf.Max(0.6f, 60f * Mathf.Pow(n.kt / 1000f, 0.44f));
                // How far it is a burst on the ground: its fireball touches the ground from less than its own radius up (G&D 1.32, 2.18), and
                // its afterwinds draw dust up into a stem from lower than about five radii (G&D 2.09 says only "depending on the height of
                // burst"; five is the choice here). Its front lifts dust out to where it still squeezes the air by some 4 kPa (Kinney and
                // Graham at 22 m for a kilogram, as RingDust's reach), the ground's reflection doubling the charge.
                n.couple = n.vacuum ? 0f : Mathf.Clamp01((3f * n.Rmax - n.height) / (2f * n.Rmax));
                n.dirt = n.vacuum || !n.groundKnown ? 0f : Mathf.Clamp01((5f * n.Rmax - n.height) / (4f * n.Rmax));
                float slant = 22f * Mathf.Pow(2f * n.tnt, 1f / 3f);
                n.groundReach = n.groundKnown && slant > n.height ? Mathf.Sqrt(slant * slant - n.height * n.height) : 0f;
                n.stem = !n.vacuum && !n.space && n.dirt > 0.05f;
                n.skirt = !n.vacuum && !n.space && n.groundKnown && n.air > 0.12f && n.groundReach > 2f * n.Rmax;

                // How stable the air is above it, from the game's own air: N^2 = (g / T) (dT/dz + g / c_p), every 500 m to 20 km. (Held
                // between a buoyancy frequency of 0.006 and 0.025 a second: Earth's troposphere is about 0.0113, its stratosphere 0.02.)
                n.n2 = new float[41];
                try
                {
                    float before = n.temp;
                    for (int k = 0; k < n.n2.Length; k++)
                    {
                        float next = (float)FlightGlobals.getExternalTemperature(plan.world + plan.up * (500.0 * (k + 1)), body);
                        float stable = n.g / Mathf.Max(before, 100f) * ((next - before) / 500f + n.g / n.cp);
                        n.n2[k] = Mathf.Clamp(stable, 0.006f * 0.006f, 0.025f * 0.025f);
                        before = next;
                    }
                }
                catch (Exception) { for (int k = 0; k < n.n2.Length; k++) n.n2[k] = 0.0113f * 0.0113f; }

                n.Start();
                // (the whole rise worked out ahead once, for what is laid by how far it has come: see NukeDust)
                var ahead = (Nuclear)n.MemberwiseClone();
                while (!ahead.stopped && ahead.t < 1500.0) ahead.Rise(0.1f);
                n.zStop = (float)ahead.zc; n.bStop = (float)ahead.b; n.tStopSeen = (float)ahead.t; n.wPeak = ahead.wPeak;
                return n;
            }

            void Start()
            {
                t = 0.0; b = Rmax; M = 0.0; zc = 0.0; w = 0f; stopped = false; lift = 0f;
                B = g * HeatShare * kt * 4.184e12 / (density * cp * temp);
            }

            float Stability(double above)
            {
                float u = (float)(above / 500.0);
                if (u <= 0f) return n2[0];
                int k = (int)u;
                if (k >= n2.Length - 1) return n2[n2.Length - 1];
                return Mathf.Lerp(n2[k], n2[k + 1], u - k);
            }

            /// <summary>The thermal's rise over so long (rise.py's sums, in steps of no more than a twentieth of a second).</summary>
            void Rise(float dt)
            {
                int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.05f), 1, 200);
                double h = dt / (double)steps;
                for (int k = 0; k < steps; k++)
                {
                    if (!stopped && !vacuum)
                    {
                        double V = 4.0 / 3.0 * Math.PI * b * b * b, gp = Math.Min(B / V, 0.9 * g), s = 1.0 - gp / g;
                        double vel = M / ((s + Added) * V);
                        if (t > 1.0 && vel <= 0.0) { stopped = true; tStop = (float)t; vel = 0.0; }
                        else
                        {
                            b += Alpha * Math.Sqrt(Math.Max(s, 0.0)) * Math.Abs(vel) * h;
                            M += gp * V * h;
                            B -= Stability(zc) * V * vel * h;
                            zc += vel * h;
                        }
                        w = (float)Math.Max(vel, 0.0);
                        if (w > wPeak) wPeak = w;
                    }
                    t += h;
                }
            }

            /// <summary>Where it went off, in its patches' axes (once, by the first of them).</summary>
            public void Place(Frame f)
            {
                if (placed) return;
                placed = true;
                at = f.ToLocal(world);
                foot = groundKnown ? f.ToLocal(groundWorld) : at - new Vector3(0f, height, 0f);
                // (on the ground: its middle no lower than the ground)
                if (at.y < foot.y) at.y = foot.y;
                x = at.x; y = at.y; z = at.z;
                Shape();
            }

            /// <summary>
            /// The cap's shape now. Its fireball comes to six tenths of its width at once and the rest over a few t_max (G&D 2.05: a
            /// megaton's is widest, 5,700 ft across, at ten seconds); on the ground it is a half-ball as wide as a burst of twice the yield
            /// in the air (G&D 2.127: the ground's reflection doubles the yield in W^0.4, and 2^0.4 = 1.32). It flattens as it
            /// rolls up into a ring (G&D 2.07), to a head near twice as wide as it is tall and of the same volume (until 2026-11-09 by a
            /// tenth: a ball on a stalk, and in the air a ball, where every photograph has a flattened, rolling head), and when it has stopped rising it levels out over four minutes, wider and flatter, its top
            /// staying where it got to (G&D 2.15).
            /// </summary>
            void Shape()
            {
                float tt = (float)t;
                float grows = 0.6f + 0.4f * (1f - Mathf.Exp(-tt / (3f * tmax)));
                float onGround = 1f + 0.32f * couple * Mathf.Exp(-tt / Mathf.Max(tlum, 0.5f));
                float a = (float)b * grows * onGround;
                float rolled = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(tt / Mathf.Max(tlum, 0.3f)));
                float level = stopped ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((tt - tStop) / 240f)) : 0f;
                float flat = 1f - 0.42f * rolled;
                ah = a / Mathf.Sqrt(flat) * (1f + 0.15f * level);
                av = a * flat * (1f - 0.1f * level);
                lift = 0.1f * a * level;
            }

            /// <summary>Once a frame, by the first of its patches to step.</summary>
            public void Advance(float dt)
            {
                if (Time.frameCount == frameDone || dt <= 0f) return;
                frameDone = Time.frameCount;
                float ahWas = ah, avWas = av, yWas = y;
                Rise(dt);
                Shape();
                eh = ahWas > 0f ? (ah - ahWas) / (ah * dt) : 0f;
                ev = avWas > 0f ? (av - avWas) / (av * dt) : 0f;
                x += driftX * dt; z += driftZ * dt;
                y = at.y + (float)zc + lift;
                W = (y - yWas) / dt;
            }

            /// <summary>
            /// The cap's colour as it cools: the reddish brown of the nitrous oxides at its skin (G&D 2.08), going over to white as the water
            /// in it condenses, from two of its glow's lengths to six; with the dirt drawn up into it where it went off near enough the
            /// ground (G&D 2.19: "much more heavily loaded with debris"). One that went off on the ground stays the colour of its dirt,
            /// darkened: thousands of tons of it are in the cloud, and the photographs of such show a brown-grey to black cauliflower with
            /// no white in it (Buster-Jangle Sugar, 1.2 kt on the ground; Small Boy). Until 2026-11-09 six tenths of the dirt's colour at
            /// the most, lightened with cloud: a cotton-white ball over the ground ("not realistic at all", the owner).
            /// </summary>
            public Color CapColour()
            {
                float tt = (float)t;
                float white = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2f * tlum, 6f * tlum, tt));
                var brown = new Color(0.58f, 0.36f, 0.21f);
                var cloud = water ? new Color(0.9f, 0.9f, 0.91f) : new Color(0.86f, 0.86f, 0.87f);
                Color c = Color.Lerp(brown, cloud, white);
                if (dirt > 0f && !water)
                {
                    Color dirty = Color.Lerp(ground, brown, 0.35f) * 0.85f;
                    dirty.a = 1f;
                    // (And later paler, the water in it condensing as it climbs and cools: from four of its glow's lengths to twelve
                    // the dirt's share falls by half, to a grey between the dirt's colour and a cloud's white, never the cotton white
                    // the owner called "not realistic at all". Small Boy, 1.65 kt three metres over the ground, and Tumbler-Snapper
                    // Dog, seen from some kilometres a minute on: a pale grey head over a darker stem. Since 2026-11-10; until then the
                    // head stayed the dirt's brown for good.)
                    float pales = 0.5f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f * tlum, 12f * tlum, tt));
                    c = Color.Lerp(c, dirty, dirt * Mathf.Lerp(0.4f, 0.92f, couple) * (1f - pales) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f * tlum, 3f * tlum, tt)));
                }
                return c;
            }

            /// <summary>
            /// How wide the column of the afterwinds is (a bell this wide; its dust spreads to about three times that across): a stem about
            /// a third as wide as the cloud up to twenty kilotons, a fifth to a tenth as wide for megatons (G&D 2.17; Small Boy, Tumbler-
            /// Snapper Dog, Upshot-Knothole Grable and Plumbbob Priscilla for the small ones). (Until 2026-11-10 a bell of 0.32 of the
            /// cap's half-width up to twenty kilotons, a stem half as wide as the cap: from some kilometres off, a fat brown trunk under a
            /// small head.)
            /// </summary>
            public float StemWide => Mathf.Max(Mathf.Lerp(0.21f, 0.06f, Mathf.InverseLerp(3f, 6.9f, Mathf.Log(Mathf.Max(kt, 1f)))) * ah, 0.3f * Rmax);

            /// <summary>
            /// How fast the afterwinds go up the column's middle: a little faster than the cap climbs, so that the dust catches its foot;
            /// dying away with the cap's own rise, which slows to nothing where it stops.
            /// </summary>
            public float StemSpeed => 1.15f * w + 2f * Mathf.Clamp01(w / 5f);

#if DEV
            public string Said() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F2} kt, {1:F1} s: fireball {2:F0} m, t_max {3:F3} s, glow {4:F1} s; {5:F0} m up (over the ground), ground coupling {6:F2}, dust {7:F2}, dust out to {8:F0} m; cap at {9:F0} m, {10:F0} m wide and {11:F0} m high, rising {12:F1} m/s{13}; top {14:F0} m over where it went off (ahead: stops at {15:F0} s, top {16:F0} m)",
                kt, t, Rmax, tmax, tlum, height, couple, dirt, groundReach, y - foot.y, 2f * ah, 2f * av, W, stopped ? " (stopped at " + tStop.ToString("F0") + " s)" : "", y + av - at.y, tStopSeen, zStop + 0.9f * bStop);
#endif
        }

        // ---- this patch's part in one
        Nuclear nuke;
        int nukeRole;
        float nukeBegan, nukeMean, nukeLamp = -1f;
        int nukeLaid, nukeTarget;
        internal bool IsNuke => nuke != null;
        internal float NukeAge => nuke != null ? nuke.Age : 0f;
        internal bool NukeOf(Nuclear n) => nuke == n;

        /// <summary>(Kept while it has still to lay what it is to: see Finished.)</summary>
        bool NukeKeeps => nuke != null && (time - nukeBegan < 5f || nukeRole == NukeStem && nukeLaid < nukeTarget && !nuke.stopped);

        /// <summary>
        /// This patch made one of a nuclear explosion's: its cap (role NukeCap: the first, at once), its stem of dust or its skirt.
        /// The flash, the light, the shock front and the cloud it leaves are the cap's; laid here, the fireball over the cap's first steps.
        /// </summary>
        internal void BeginNuke(Nuclear n, int role)
        {
            nuke = n; nukeRole = role; nukeBegan = time;
            frame.Refresh();
            n.Place(frame);
            // (the ground under it from the axes the patches share: a world position goes stale as the game moves its origin)
            if (!space && n.groundKnown && !hasGround) Ground(frame.ToWorld(n.foot), n.groundNormal);
            lastBlast = time; blasted = true;
            float q = Mathf.Clamp(Settings.Quality * Air.Room(), 0.3f, 1.5f);
            // (its fire's colours those of burning metal's, white-hot to deep red, whatever else burns in it)
            flameKind = 1; flameWeight = 1e9f;
            // Billows to the cloud's size. The cap's and the stem's grow with them (see NukeCapStep and NukeStemStep): a cloud that
            // grows tenfold with billows of a fixed size ends a smooth ball with a fine grain on it, and a real one is a cauliflower
            // from first to last, a few great lumps across at every size (the photographs in research/armory/nuke.md). The skirt's
            // are fixed, as every other patch's are. (Until 2026-11-09 all three fixed at the fireball's size.)
            scale = role == NukeCap ? Mathf.Max(4f, n.Rmax) : Mathf.Max(3f, 0.4f * n.Rmax);
            detailRepeat = role == NukeCap ? Mathf.Clamp(0.5f * n.Rmax, 20f, 450f) : role == NukeStem ? Mathf.Clamp(0.5f * n.StemWide, 12f, 200f) : Mathf.Clamp(0.4f * n.Rmax, 12f, 200f);
            fullLength = 4f;
            seaBorn = n.water && role != NukeCap;
            float strongest = Mathf.Max(n.ground.r, Mathf.Max(n.ground.g, n.ground.b));
            if (strongest > 0.01f) dustHue = new Vector3(n.ground.r, n.ground.g, n.ground.b) / strongest;
            expect = role == NukeSkirt ? Mathf.Min(Mathf.Min(n.groundReach, 12f * n.Rmax), 2000f) : 2f * n.Rmax;
            switch (role)
            {
                case NukeCap:
                {
                    nukeTarget = n.vacuum ? (int)Mathf.Clamp(600f * q, 200f, 900f) : (int)Mathf.Clamp(4500f * q, 1500f, 6000f);
                    fireballR = n.Rmax; fireballAt = time; fireballC = n.at;
                    // The flash and the light: its heat comes out as a pulse that peaks at t_max and has mostly come by ten times that
                    // (G&D 7.84 to 7.87); the fireball glows on until it is out. (See the pulse in Lamps.) Its flash is as wide as
                    // twenty-five fireballs and whites the picture out for whoever looks at it from near (Glows and SeeFlashes: a
                    // film of a real one is plain white for its first frames, the fireball coming out of the white as it dims);
                    // and its light lasts three of its glow's lengths, the fire showing through the cloud as long (NukeLay). (Until
                    // 2026-11-09 a flash six and a half fireballs wide, hidden by the fireball's own fire in front of it, and a
                    // light of one and a third lengths: no flash was seen at all.)
                    int lamp = AddLamp(n.at.x, n.at.y, n.at.z, n.Rmax, 8f, n.vacuum ? 1.2f : Mathf.Max(1.5f, 3f * n.tlum), 1, false, 25f * n.Rmax, 0f);
                    lamps[lamp].flashFor = Mathf.Clamp(20f * n.tmax, 0.6f, 3f);
                    // (its reach held to some kilometres: a light's cost goes with what it reaches)
                    lamps[lamp].reach = Mathf.Min(80f * n.Rmax, 12000f);
                    lamps[lamp].pulse = n.vacuum ? 0f : n.tmax;
                    nukeLamp = lamps[lamp].seed;
                    // Its front: as fast as it squeezes the air (see NukeWave), weakening as it spreads; and where the air is damp, the
                    // cloud behind it (G&D 2.47 to 2.50; Condense, sized by the cube root of its charge).
                    if (!n.vacuum && Settings.Shockwave && Assets.Shock != null && sound > 0f && air > 0.012f)
                    {
                        bool flat = n.couple >= 0.5f;
                        var wave = new Wave { x = n.at.x, y = flat ? n.foot.y : n.at.y, z = n.at.z, ground = flat, start = 0.6f * n.Rmax, radius = 0.6f * n.Rmax,
                                              reach = Mathf.Clamp(30f * Mathf.Pow(n.tnt, 1f / 3f), 300f, 15000f), power = 0.3f, charge = n.tnt };
                        try { Condense(ref wave, new Vector3(wave.x, wave.y, wave.z), 10f * n.tnt, n.Rmax); }
                        catch (Exception) { wave.cloud = 0f; }
                        AddWave(wave);
                    }
                    lastRing = time;
                    break;
                }
                case NukeStem:
                    nukeTarget = (int)Mathf.Clamp(1800f * q * n.dirt, 200f, 2400f);
                    NukeLights(n);
                    break;
                case NukeSkirt:
                {
                    // (its front again, unseen: only for the dust it lifts as it sweeps out along the ground)
                    var wave = new Wave { x = n.at.x, y = n.at.y, z = n.at.z, ground = true, start = 0.6f * n.Rmax, radius = 0.6f * n.Rmax, reach = 1f, power = 0f, charge = n.tnt,
                                          over = Mathf.Max(0f, n.at.y - n.foot.y), dust = 1f, dustSize = Mathf.Pow(2f * n.tnt, 1f / 3f), dustFrom = 0.8f * n.Rmax, dustReach = Mathf.Min(n.groundReach, 12f * n.Rmax),
                                          dustColour = n.ground };
                    AddWave(wave);
                    NukeLights(n);
                    break;
                }
            }
#if DEV
            Addon.Log("a nuclear explosion's " + (role == NukeCap ? "cap" : role == NukeStem ? "stem" : "skirt") + " begun (frame " + Time.frameCount + "): " + n.Said());
#endif
        }

        /// <summary>
        /// The fireball's light on the stem's dust and the skirt's. The smoke of a patch is lit by that patch's fires (SmokeLamps),
        /// and the fireball's light is the cap's: so each of the other two has a light of its own where the fireball is, as long
        /// as the cap's and going with it (NukeLampFollows), for its smoke alone: a quiet one, which throws no light on the land
        /// and has no glow and no flash (those are the cap's, once); three times the cap's own in strength, since the dust stands
        /// out beyond the fireball, where a fire's light on its own smoke has all but gone. (Until 2026-11-09 they had none: the dust round a
        /// fireball at night, and the stem's rising before it, stood black against it, the lower two thirds of the ball hidden
        /// behind unlit dust from two seconds after the flash.)
        /// </summary>
        void NukeLights(Nuclear n)
        {
            if (n.vacuum) return;
            int lamp = AddLamp(n.x, n.y, n.z, Mathf.Max(n.Rmax, n.Mean), 24f, Mathf.Max(1.5f, 3f * n.tlum), 1, false, 0f, 0f);
            lamps[lamp].quiet = true;
            lamps[lamp].pulse = n.tmax;
            lamps[lamp].age = (float)n.t;
            nukeLamp = lamps[lamp].seed;
        }

        void AddWave(Wave wave)
        {
            int at = waveCount < waves.Length ? waveCount++ : 0;
            waves[at] = wave;
        }

        /// <summary>(In the step, after the thermals: the mover is not working on the particles.)</summary>
        void NukeStep(float dt)
        {
            Nuclear n = nuke;
            n.Advance(dt);
            if (nukeRole == NukeCap) NukeCapStep(dt);
            else if (nukeRole == NukeStem) NukeStemStep(dt);
            if (nukeRole != NukeCap) NukeLampFollows();
        }

        // ================================================================== the cap

        void NukeCapStep(float dt)
        {
            Nuclear n = nuke;
            // (the fireball laid over the cap's first three steps: a frame's work of some thousands of bits, in thirds)
            if (nukeLaid < nukeTarget)
            {
                int now = Math.Min(nukeTarget - nukeLaid, (nukeTarget + 2) / 3);
                if (n.vacuum) NukeDebris(now); else NukeLay(now);
                nukeLaid += now;
            }
            NukeLampFollows();
            if (n.vacuum) return;
            // (its fire's place and size, while it glows: for the rectangle its flames are walked whole in, see FlameRect)
            if (n.t < 5f * n.tlum) { fireballAt = time; fireballC = new Vector3(n.x, n.y, n.z); fireballR = Mathf.Max(n.Rmax, Mathf.Max(n.ah, n.av)); }
            // Its thermal, led: where the rise has it.
            int k = NukeThermal(false);
            if (k >= 0)
            {
                ref Thermal h = ref thermals[k];
                h.x = n.x; h.y = n.y; h.z = n.z; h.a = Mathf.Max(n.ah, n.av); h.ah = n.ah; h.av = n.av; h.eh = n.eh; h.ev = n.ev; h.w = n.W; h.top = 1e9f; h.turn = 1f;
            }
            // Each bit of the cap as much wider as the cap has grown, and as much thicker, so as hard to see through as it was: the cloud
            // takes in air as it rises and its water condenses in it, so that it stays a cloud and does not thin to nothing as it grows
            // tenfold. Its colour as it cools. And the cap goes sideways as its own smoke does, with the wind at its height.
            float mean = n.Mean, ratio = nukeMean > 0f ? mean / nukeMean : 1f;
            nukeMean = mean;
            // (and its billows as much larger: see BeginNuke. The patch began where it went off, and its billows are counted from
            // there less how far its smoke has gone, so they grow about the cap's own middle.)
            detailRepeat = Mathf.Clamp(0.5f * mean, 20f, 450f);
            bool recolour = (tick & 3) == 0;
            Color c = recolour ? n.CapColour() : default;
            double sx = 0.0, sz = 0.0;
            int m = 0;
            for (int i = 0; i < count; i++)
            {
                ref P o = ref p[i];
                if (o.tint != 1 || o.life < 0f) continue;
                if (ratio != 1f) { o.r *= ratio; o.born *= ratio; o.mass *= ratio * ratio; }
                if (recolour)
                {
                    float tone = 0.9f + 0.16f * ((o.seed >> 12) & 255) * (1f / 255f);
                    o.ar = c.r * tone; o.ag = c.g * tone; o.ab = c.b * tone;
                }
                sx += o.vx; sz += o.vz; m++;
            }
            if (m > 0)
            {
                float ease = 1f - Mathf.Exp(-dt / 1.5f);
                n.driftX += ((float)(sx / m) - n.driftX) * ease;
                n.driftZ += ((float)(sz / m) - n.driftZ) * ease;
            }
        }

        /// <summary>
        /// Some of the fireball: bits all through the cap as it is now, most of them towards its skin, white-hot, cooling as its glow
        /// does (G&D 2.05); each smoke that shows as it cools (the cloud), whose colour the cap's step sets. The bits are carried by
        /// the cap's thermal, so they go as it goes: up, round its ring, and outwards as it grows. Its skin cools first, to smoke in
        /// about one and a half of its glow's lengths, and the core of the ring it turns about last, in about five: in the photographs taken some seconds on
        /// the cloud is brown and billowed with the fire glowing orange through the gaps between its lumps and in a ring under it
        /// (Upshot-Knothole Grable, Plumbbob Priscilla). (Until 2026-11-09 every bit alike, out in one length: half a second of dim
        /// fire with holes in it; until 2026-11-10 its middle the last to cool, see below.)
        /// </summary>
        void NukeLay(int howMany)
        {
            Nuclear n = nuke;
            Room(howMany);
            float mean = n.Mean, rp = 0.14f * mean * Mathf.Pow(4500f / Mathf.Max(nukeTarget, 500), 1f / 3f);
            float lasts = 720f * Mathf.Clamp(Settings.Smoke, 0.3f, 2f);
            Color c = n.CapColour();
            for (int k = 0; k < howMany; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = LuckSphere();
                float rho = Mathf.Pow(Luck(0f, 1f), 0.3f) * 0.95f;
                // (Slower to cool the nearer it lies to the ring the cap turns about, by four times and more: the red glow inside the
                // rolling head, seen through the gaps in its dark outside for some seconds after the fireball has gone, is what the
                // films of real ones are remembered for, and in the photographs it is a ring, under the cap and round its foot
                // (Upshot-Knothole Grable, Plumbbob Priscilla): the hot gas gathers in the core of the ring the fireball turns into
                // (G&D 2.07). How near: the cap's stream function (Hill's vortex, as it turns in Nuked), 0 on its axis and at its
                // skin and 1 at its ring, seven tenths of the way out and halfway up; what is laid near the ring goes round it and
                // stays near it. Since 2026-11-10: until then the slowest to cool were those laid in the middle, slower by three
                // times; but the middle lies on the axis, the turning carries what is there up it and out over the top and down the
                // outside, and the glow came out on the cap's top and sides and was gone in a few seconds, where the photographs
                // have a dark top over a glowing ring. The skin's own cooling, too, eased from 1.8 to 1.5 of a glow's length: so
                // the head went dull brown for a second between the fireball and its ring, and lit up again.)
                float s2 = rho * rho, core = Mathf.Clamp01(4f * s2 * (way.x * way.x + way.z * way.z) * (1f - s2));
                float cool = Mathf.Lerp(1.5f, 0.42f, core) / n.tlum;
                o.x = n.x + way.x * n.ah * rho; o.y = n.y + way.y * n.av * rho; o.z = n.z + way.z * n.ah * rho;
                o.r = rp * Luck(0.75f, 1.25f);
                if (hasGround)
                {
                    // (on the ground, a half-ball: what would be under the ground is above it)
                    float floor = GroundAt(o.x, o.z) + 0.5f * o.r;
                    if (o.y < floor) o.y = Mathf.Min(floor + (floor - o.y), n.y + n.av);
                }
                o.drag = CapDrag; o.kloss = 2f; o.grow = 0f; o.turb = 0.5f; o.rise = 0f;
                o.heat = Mathf.Exp(-cool * n.Age) * (Luck(1.08f, 1.18f) - 0.08f * rho);
                o.cool = cool * Luck(0.92f, 1.08f);
                o.flame = Luck(0.85f, 1.15f);
                o.tint = 1;
                o.tile = Lumpy();
                o.mass = Luck(1.0f, 2.0f) * o.r * o.r;
                o.ar = c.r; o.ag = c.g; o.ab = c.b;
                o.life = lasts * Luck(0.8f, 1.2f);
                Born(i);
            }
        }

        /// <summary>
        /// In a vacuum there is no air to make a fireball of: its flash, and the bomb's own stuff flying apart, glowing and gone within a
        /// second or two (its true speed, hundreds of kilometres a second, would be gone in a frame: drawn at a few kilometres a second).
        /// </summary>
        void NukeDebris(int howMany)
        {
            Nuclear n = nuke;
            Room(howMany);
            Vector3 v0 = frame.DirToLocal(n.velocity - (space ? frame.velocity - body.getRFrmVel(n.world) : Vector3d.zero));
            float fast = 8f * n.Rmax / 1.5f;
            for (int k = 0; k < howMany; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = LuckSphere();
                float speed = fast * Luck(0.35f, 1f);
                o.x = n.at.x; o.y = n.at.y; o.z = n.at.z;
                o.vx = v0.x + way.x * speed; o.vy = v0.y + way.y * speed; o.vz = v0.z + way.z * speed;
                o.flags = Ballistic;
                o.r = 0.06f * n.Rmax * Luck(0.7f, 1.3f);
                o.grow = speed * 0.12f;
                o.heat = Luck(1.05f, 1.2f); o.cool = Luck(1.6f, 2.6f);
                o.flame = Luck(1.0f, 1.4f);
                o.tint = 1; o.tile = Lumpy();
                o.life = Luck(1.2f, 2.2f);
                Born(i);
            }
        }

        /// <summary>The light goes with the fireball as it rises, and is as wide as it.</summary>
        void NukeLampFollows()
        {
            if (nukeLamp < 0f) return;
            for (int k = 0; k < lampCount; k++)
                if (lamps[k].seed == nukeLamp)
                {
                    lamps[k].x = nuke.x; lamps[k].y = nuke.y; lamps[k].z = nuke.z;
                    lamps[k].r = Mathf.Max(nuke.Rmax, nuke.Mean);
                    return;
                }
            nukeLamp = -1f;
        }

        /// <summary>
        /// (Lamps: a light with a pulse.) A nuclear explosion's heat as a share of its most: Glasstone and Dolan's thermal pulse (7.84 to
        /// 7.86, Fig. 7.84), rising to its most at t_max and falling away, here as 2 (t/t_max)^2 / (1 + (t/t_max)^4) does, nine tenths of
        /// it come by ten t_max (theirs: four fifths); then the fireball's own glow, dying as the light's life runs out.
        /// </summary>
        static float NukePulse(float age, float pulse, float left)
        {
            float tau = age / pulse, t2 = tau * tau;
            return Mathf.Max(2f * t2 / (1f + t2 * t2), 0.22f * left * left);
        }

        /// <summary>This patch's thermal for the cap (led) or the stem: found, or put in. (Nothing else is laid into a nuclear explosion's patches: see Takes.)</summary>
        int NukeThermal(bool stem)
        {
            for (int k = 0; k < thermalCount; k++) if (stem ? thermals[k].stem : thermals[k].led) return k;
            if (thermalCount >= thermals.Length) return -1;
            thermals[thermalCount] = new Thermal { led = !stem, stem = stem, turn = 1f };
            return thermalCount++;
        }

        // ================================================================== the stem

        void NukeStemStep(float dt)
        {
            Nuclear n = nuke;
            float wide = n.StemWide;
            // (its billows as wide as the column is now: see BeginNuke)
            detailRepeat = Mathf.Clamp(0.5f * wide, 12f, 200f);
            // The afterwinds (G&D 2.09): air drawn in along the ground towards the foot of the column under the rising cloud, and up it,
            // the column leaning from its foot to the cap as the wind carries the cap.
            int k = NukeThermal(true);
            if (k >= 0)
            {
                ref Thermal h = ref thermals[k];
                h.x = n.foot.x; h.y = n.foot.y; h.z = n.foot.z; h.top = n.y - 0.65f * n.av; h.lx = n.x - n.foot.x; h.lz = n.z - n.foot.z;
                h.a = wide; h.w = n.StemSpeed; h.ah = 4f * wide; h.eh = 0.3f * h.w; h.av = 0.5f * wide + 40f; h.turn = 1f;
                // (and the column widens as the cap does: what is in it goes outwards as much, see Nuked)
                h.ev = n.eh;
            }
            NukeDust(dt, wide);
            // What has got up into the cap's foot is taken into the cap: it fades out there, inside the cap's own cloud. (Once the cap has
            // stopped, the column stands, and drifts.)
            if (n.W > 1f)
            {
                float foot = n.y - 0.6f * n.av, wide2 = 0.81f * n.ah * n.ah;
                for (int i = 0; i < count; i++)
                {
                    ref P o = ref p[i];
                    if (o.life < 0f || (o.flags & HandedOn) != 0 || o.y < foot) continue;
                    float dx = o.x - n.x, dz = o.z - n.z;
                    if (dx * dx + dz * dz > wide2) continue;
                    o.flags |= HandedOn;
                    o.fadeIn = -(o.age + 2.5f);
                }
            }
        }

        /// <summary>
        /// Dust (spray, over the sea) raised round the column's foot and drawn up it: as much of it as the cap has climbed of the way it
        /// will, the most while it climbs fastest; from when the front has come back up off the ground.
        /// </summary>
        void NukeDust(float dt, float wide)
        {
            Nuclear n = nuke;
            if (nukeLaid >= nukeTarget || n.Age < 0.3f + n.height / 300f) return;
            float done = n.stopped ? 1f : Mathf.Clamp01((float)n.zc / Mathf.Max(1f, n.zStop));
            int want = Math.Min((int)(nukeTarget * Mathf.Sqrt(done)) - nukeLaid, Mathf.CeilToInt(240f * dt) + 2);
            if (want <= 0) return;
            Room(want);
            float lasts = Mathf.Clamp(Settings.Smoke, 0.3f, 2f), up = 0.5f * n.StemSpeed;
            for (int k = 0; k < want; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float ang = Luck(0f, 2f * Mathf.PI), off = 1.5f * wide * Mathf.Sqrt(Luck(0f, 1f));
                o.x = n.foot.x + Mathf.Cos(ang) * off; o.z = n.foot.z + Mathf.Sin(ang) * off;
                float floor = hasGround ? GroundAt(o.x, o.z) : n.foot.y;
                o.r = Mathf.Clamp(0.55f * wide * Luck(0.55f, 1.45f), 2f, 600f);
                o.y = floor + 0.4f * o.r + Luck(0f, 0.3f) * wide;
                o.vy = up * Luck(0.5f, 1f);
                o.drag = 4f; o.kloss = 1f; o.grow = 0.02f * o.r; o.turb = 1.5f; o.rise = 0f;
                o.mass = Luck(0.6f, 1.4f) * Mathf.Max(0.3f, n.dirt) * o.r * o.r;
                float tone = Luck(0.85f, 1.1f);
                o.ar = n.ground.r * tone; o.ag = n.ground.g * tone; o.ab = n.ground.b * tone;
                o.fadeIn = 1.5f;
                o.life = Luck(420f, 720f) * lasts;
                o.tile = Lumpy();
                Born(i);
                nukeLaid++;
            }
        }

        // ================================================================== its front, and the skirt of dust it lifts

        /// <summary>
        /// A nuclear blast's front (charge > 0): as fast as it squeezes the air (Rankine and Hugoniot: the speed of sound times the root of
        /// one and 6/7 of the squeeze, in the air's own pressures), squeezed as Kinney and Graham give it at the distance over the cube
        /// root of the charge (and over the cube root of how much thinner the air is: Sachs). A 20-kiloton's starts out at some 2,000 m/s,
        /// is at 440 m/s 650 m out and within a twentieth of sound's by two kilometres (tools/dev/scratch-2026-11-09-nuke/numbers.py). As
        /// it passes the camera the camera is jolted; along the ground it lifts its skirt of dust. True when it is over.
        /// </summary>
        bool NukeWave(ref Wave w, float dt, float before)
        {
            float per = Mathf.Pow(Mathf.Max(air, 0.01f), 1f / 3f) / Mathf.Pow(w.charge, 1f / 3f), r = w.radius, left = dt;
            while (left > 0f)
            {
                float h = Mathf.Min(left, 0.02f);
                r += sound * Mathf.Sqrt(1f + 0.857f * OverAt(r * per)) * h;
                left -= h;
            }
            w.radius = r;
            if (w.power > 0f)
            {
                float away = (camL - new Vector3(w.x, w.y, w.z)).magnitude;
                if (before < away && w.radius >= away && Air.Instance != null) Air.Instance.Jolt(Mathf.Min(1.5f, 60f * Strength(w)));
                // (the cloud behind it stands off the fireball as that grows)
                if (w.cloud > 0f && nuke != null) w.fire = 0.9f * Mathf.Max(nuke.ah, 0.6f * nuke.Rmax);
            }
            float along = w.over > 0f ? Mathf.Sqrt(Mathf.Max(0f, r * r - w.over * w.over)) : r;
            if (w.dust > 0f && nuke != null)
            {
                float was = w.over > 0f ? Mathf.Sqrt(Mathf.Max(0f, before * before - w.over * w.over)) : before;
                float from = Mathf.Max(was, w.dustFrom), to = Mathf.Min(along, w.dustReach);
                if (to > from) NukeRing(ref w, from, to);
            }
            return w.radius >= Mathf.Max(w.reach, w.gone) && (w.dust <= 0f || along >= w.dustReach);
        }

        /// <summary>
        /// The skirt: dust lifted off the ground as the front sweeps over it, between two distances out along the ground. Behind the front
        /// the air rushes outwards for a moment (its speed by Rankine and Hugoniot from how hard the front squeezes it there, the ground's
        /// reflection doubling the charge), and the loose ground goes with it: a low wall of dust racing out under the cloud, tens of
        /// metres high near in and more further out, that hangs for minutes (G&D 3.50 on the dust a blast picks up; the dirt cloud under a
        /// low air burst in their Fig. 2.07b). Spray over water where it went off over the sea; none where it went off over land and the front crosses water.
        /// </summary>
        void NukeRing(ref Wave w, float from, float to)
        {
            Nuclear n = nuke;
            float q = Mathf.Clamp(Settings.Quality * Air.Room(), 0.3f, 1.5f);
            // (fewer puffs and larger, each thick enough to be a lump of dust with an edge: until 2026-11-09 four times as many, half
            // as wide and thin, which drew as one even brown haze)
            float mid = 0.5f * (from + to), wide = Mathf.Max(4f, 0.09f * mid);
            float want = q * w.dust * 0.7f * (to * to - from * from) / (wide * wide) + w.dustCarry;
            int laid = Mathf.Min((int)want, 60);
            w.dustCarry = Mathf.Min(want - laid, 600f);
            float per = Mathf.Pow(Mathf.Max(air, 0.01f), 1f / 3f) / Mathf.Pow(2f * w.charge, 1f / 3f);
            float lasts = Mathf.Clamp(Settings.Smoke, 0.3f, 2f) * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(air));
            // (no puff smaller than its share of the skirt: a few hundred across the whole of it)
            float least = Mathf.Min(400f, Mathf.Max(3f, 0.015f * w.dustReach));
            for (int k = 0; k < laid; k++)
            {
                float rr = Luck(from, to), ang = Luck(0f, Mathf.PI * 2f);
                float cx = Mathf.Cos(ang), cz = Mathf.Sin(ang);
                float px = w.x + cx * rr, pz = w.z + cz * rr;
                // Near the foot, within some fireballs of it, the dust is thrown up in a ring of great dark lumps as tall as the
                // fireball (the base of the column in every photograph of one that went off on or near the ground: Plumbbob
                // Priscilla, Buster-Jangle Sugar), coming up over a second or so, after the fireball has been seen; further out a
                // low sheet of it in patches, thinner and sooner gone the further out, to twelve fireballs' radii. (Until
                // 2026-11-09 one sheet of the same puffs all the way out, to two and a half kilometres at 1.5 kt: a flat brown
                // disc on the ground, the widest thing in the picture.)
                // (The great lumps only where the fireball is near enough the ground to draw a column up from it: under a burst high in
                // the air, which has none, only the sheet is raised, by the blast.)
                // (How far out counts from the burst itself, not from the point under it: the sheet thins as the front weakens, and
                // under a burst high in the air the front is weak already where it first touches the ground. Counted along the ground
                // alone, as until 2026-11-09, 1.5 kt at 600 m laid as much dust under itself as a burst on the ground lays round its
                // foot: a round brown patch 500 m across with a dark rim, like a crater seen from the air (the rim was the cap's
                // shadow, falling on so much dust). Now a thin haze there, gone within a minute.)
                // (Since 2026-11-10 the great lumps are lower, smaller, and come up over the fireball's first glow rather than within
                // a second of the flash: in Plumbbob Priscilla's photograph they stand about a third as tall as the glowing ball over
                // them. Until then they were as tall as the fireball and full within one to two seconds, and from near by they hid
                // all but its top through its brightest seconds: a brown dome with a glowing cap on it, where every film of a real
                // one shows the whole ball blazing first ("make the nuke more epic", the owner, 2026-11-10).)
                float nearness = (1f - Mathf.Clamp01((rr - 2f * n.Rmax) / (3f * n.Rmax))) * Mathf.Clamp01(2f * n.dirt);
                float out8 = Mathf.Sqrt(rr * rr + w.over * w.over) / (4f * n.Rmax);
                // (the sheet in patches: a third of it left out, more of it the further out)
                if (nearness <= 0f && Luck(0f, 1f) < Mathf.Lerp(0.2f, 0.6f, Mathf.Clamp01(0.5f * out8 - 0.5f))) continue;
                float floor = GroundFar(px, pz, w.y - w.over, out bool wet);
                if (wet && !n.water) continue;
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float over = OverAt(Mathf.Sqrt(rr * rr + w.over * w.over) * per);
                float wind = Mathf.Min(240f, sound / 1.4f * over / Mathf.Sqrt(1f + 0.857f * over)) * Luck(0.6f, 1.1f);
                float tall = Mathf.Max(Mathf.Clamp(0.03f * rr, 6f, Mathf.Max(6f, 0.5f * n.Rmax)), 0.3f * n.Rmax * nearness);
                o.x = px; o.z = pz;
                o.y = floor + Luck(0.05f, 1f) * tall;
                // (The great lumps near the foot are thrown up more than blown out, and stay round the foot for the column to
                // draw from: blown out as the sheet is, they left bare ground for two fireballs' radii round the column, the
                // skirt a ring with a hole in it.)
                float push = 1f - 0.85f * nearness;
                o.kx = cx * wind * push; o.kz = cz * wind * push; o.ky = wind * Luck(0.05f, 0.2f) * push;
                o.kloss = 1.2f; o.drag = 2f;
                o.r = Mathf.Clamp(Mathf.Max(Mathf.Min(0.07f * rr, 0.4f * n.Rmax), 0.3f * n.Rmax * nearness), least, 400f) * Luck(0.8f, 1.3f);
                o.grow = Mathf.Lerp(0.06f, 0.2f, nearness) * o.r;
                o.turb = 0.5f + 0.03f * wind;
                o.rise = Luck(0.1f, 0.6f) + 0.6f * nearness;
                o.mass = Luck(1.0f, 2.2f) * o.r * o.r * (0.3f + 0.7f * Mathf.Clamp01(over * 8f)) * w.dust / (1f + out8 * out8 * out8);
                o.fadeIn = Luck(0.3f, 1.2f) + Mathf.Clamp(0.6f * n.tlum, 1f, 6f) * nearness * Luck(0.7f, 1.3f);
                o.life = Luck(90f, 200f) * lasts * Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(0.5f * out8));
                float tone = Luck(0.85f, 1.1f);
                Color c = wet || n.water ? new Color(0.9f, 0.92f, 0.94f) : w.dustColour;
                o.ar = c.r * tone; o.ag = c.g * tone; o.ab = c.b * tone;
                o.tile = Luck(0f, 1f) < 0.25f ? Wispy() : Lumpy();
                Born(i);
            }
        }

        // ================================================================== in the mover

        /// <summary>
        /// (The mover, on another thread.) The air of a nuclear explosion's cap (led) or of the column under it (stem). The cap: Hill's
        /// vortex, as any thermal's (see the mover), stretched to the cap's shape (a stretch the same everywhere leaves it a flow that neither
        /// piles the air up nor thins it), going up as fast as the rise has it, and growing as the cap does; what has strayed out of it
        /// carried along all the same, and drawn back in (since 2026-11-09: until then the air outside parted round it as round a ball,
        /// and took away below it what had strayed). The stem: air going up its middle, fastest on its axis (a bell across), dying away
        /// at the cap's foot; drawn in towards its foot along the ground, as far in as the column's own edge; and going outwards as the
        /// column widens. (Until 2026-11-09 the air at its foot went in all the way to its axis, slowing only at the axis itself: the
        /// dust was gathered to a line and went up as one, a thin stick under the cap where a real stem is a third as wide as the cap.)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Nuked(ref P q, in Thermal h, ref float ux, ref float uy, ref float uz, ref float stir)
        {
            if (h.led)
            {
                float dx = q.x - h.x, dy = q.y - h.y, dz = q.z - h.z;
                float ih = 1f / h.ah, iv = 1f / h.av, tw = h.w;
                float nx = dx * ih, ny = dy * iv, nz = dz * ih, n2 = nx * nx + ny * ny + nz * nz;
                float speed = tw < 0f ? -tw : tw;
                if (n2 < 1f)
                {
                    float om = 1.5f * tw * h.turn * iv;
                    ux += h.ah * om * ny * nx + h.eh * dx;
                    uy += h.av * om * (1f - 2f * (nx * nx + nz * nz) - ny * ny) + tw + h.ev * dy;
                    uz += h.ah * om * ny * nz + h.eh * dz;
                    float churn = 0.08f * speed + 0.4f;
                    if (stir < churn) stir = churn;
                }
                else
                {
                    // (out of it, as the air there is not: carried along as what is in it is, by its rise and its growth)
                    ux += h.eh * dx; uy += tw + h.ev * dy; uz += h.eh * dz;
                    float edge = 0.08f * speed / n2;
                    if (stir < edge) stir = edge;
                }
                // Everything in the cap's patch is the cap's own (nothing else is laid in it: see Takes), and the cap holds what is in
                // it: in Hill's vortex the gas goes round inside the ball, whose skin the flow does not cross. Worked a step at a time it
                // did: each bit a moment behind the air (its drag, then 6), the air looked at every fourth frame, and the ball turning
                // over in a second or two while it is small and fast, the bits were flung out past its skin, where the air going down
                // round the outside of it took them away below it. (Found 2026-11-09 with Air.NukeWhere, 1.5 kt on the grass west of the
                // pad: by 15 s 8 of its 4,500 bits were in the ball, the middle of them 114 m up with the cap's middle at 803 m; at 140 s
                // still 50 to 600 m up with the cap at 2,300: a white column standing on the ground under where the cap was, and no cap.)
                // So what strays out past nine tenths of its size is drawn back in, at so much of how far out it has strayed each
                // second: no faster than twice the cap's rise or 20 m/s, so that one that has gone far comes back over some seconds.
                if (n2 > CapSkin * CapSkin)
                {
                    float rn = Root(n2), back = CapHold * (rn - CapSkin) / rn;
                    float pull2 = back * back * (dx * dx + dy * dy + dz * dz), most = speed > 10f ? 2f * speed : 20f;
                    if (pull2 > most * most) back *= most / Root(pull2);
                    ux -= back * dx; uy -= back * dy; uz -= back * dz;
                }
                return;
            }
            float foot = h.y, span = h.top - foot;
            if (span < 1f) return;
            float up = (q.y - foot) / span, along = up < 0f ? 0f : up > 1f ? 1f : up;
            float sx = q.x - (h.x + h.lx * along), sz = q.z - (h.z + h.lz * along);
            float d2 = sx * sx + sz * sz, s2 = h.a * h.a;
            if (up < 1.15f && d2 < 9f * s2)
            {
                float e = d2 / s2, bell = 1f / (1f + e + 0.5f * e * e), ends = up > 0.95f ? (1.15f - up) * 5f : 1f;
                float lift = h.w * bell * ends;
                uy += lift;
                ux += h.ev * sx; uz += h.ev * sz;
                float churn = 0.12f * lift + 0.3f;
                if (stir < churn) stir = churn;
            }
            float height = q.y - foot;
            if (height < h.av && d2 < h.ah * h.ah && d2 > 0.25f)
            {
                float d = Root(d2), near = d >= h.a ? h.a / d : d > 0.5f * h.a ? 2f * d / h.a - 1f : 0f;
                float pull = h.eh * near * (height <= 0f ? 1f : 1f - height / h.av);
                ux -= sx / d * pull; uz -= sz / d * pull;
            }
        }

#if DEV
        /// <summary>(Development build: this patch's part in a nuclear explosion, and how it stands.)</summary>
        internal string NukeSaid() => nuke == null ? "" : (nukeRole == NukeCap ? "cap" : nukeRole == NukeStem ? "stem" : "skirt") + ": " + count + " bits (" + nukeLaid + " of " + nukeTarget + " laid), " + thermalCount + " thermals, " + waveCount + " fronts; " + nuke.Said();
        /// <summary>(Development build, 2026-11-09: where this patch's bits are, against its thermal and the box its grid is drawn in:
        /// for the cap how many are inside its stretched ball, near it (within twice) and out of it; for all, their heights over the
        /// ground under the burst and their distance from the cap's axis, at a twentieth, a quarter, the half, three quarters and
        /// nineteen twentieths of them, and their mean size.)</summary>
        internal string NukeWhere()
        {
            if (nuke == null) return "";
            Nuclear n = nuke;
            int k = -1;
            for (int j = 0; j < thermalCount; j++) if (nukeRole == NukeCap ? thermals[j].led : thermals[j].stem) { k = j; break; }
            var ys = new System.Collections.Generic.List<float>(count);
            var ds = new System.Collections.Generic.List<float>(count);
            int inside = 0, near = 0, outside = 0;
            double rs = 0.0;
            for (int i = 0; i < count; i++)
            {
                if (p[i].life < 0f) continue;
                float dx = p[i].x - n.x, dz = p[i].z - n.z;
                ys.Add(p[i].y - n.foot.y); ds.Add(Mathf.Sqrt(dx * dx + dz * dz)); rs += p[i].r;
                if (nukeRole == NukeCap)
                {
                    float nx = dx / Mathf.Max(n.ah, 1f), ny = (p[i].y - n.y) / Mathf.Max(n.av, 1f), nz = dz / Mathf.Max(n.ah, 1f), n2 = nx * nx + ny * ny + nz * nz;
                    if (n2 < 1f) inside++; else if (n2 < 4f) near++; else outside++;
                }
            }
            if (ys.Count == 0) return (nukeRole == NukeCap ? "cap" : nukeRole == NukeStem ? "stem" : "skirt") + ": no bits";
            ys.Sort(); ds.Sort();
            System.Func<System.Collections.Generic.List<float>, string> fifths = l =>
                l[(int)(0.05f * (l.Count - 1))].ToString("F0") + "/" + l[(int)(0.25f * (l.Count - 1))].ToString("F0") + "/" + l[(l.Count - 1) / 2].ToString("F0") + "/" +
                l[(int)(0.75f * (l.Count - 1))].ToString("F0") + "/" + l[(int)(0.95f * (l.Count - 1))].ToString("F0");
            var said = new System.Text.StringBuilder();
            said.Append(nukeRole == NukeCap ? "cap" : nukeRole == NukeStem ? "stem" : "skirt").Append(" at ").Append(n.Age.ToString("F1")).Append(" s: ").Append(ys.Count).Append(" bits");
            if (nukeRole == NukeCap) said.Append(" (").Append(inside).Append(" in the ball, ").Append(near).Append(" near, ").Append(outside).Append(" out)");
            said.Append("; heights ").Append(fifths(ys)).Append(", from the axis ").Append(fifths(ds)).Append(", size ").Append((rs / ys.Count).ToString("F0"));
            said.Append("; the cap's middle ").Append((n.y - n.foot.y).ToString("F0")).Append(" up, ").Append(n.ah.ToString("F0")).Append(" by ").Append(n.av.ToString("F0"));
            if (k >= 0) said.Append(", its thermal at ").Append((thermals[k].y - n.foot.y).ToString("F0")).Append(" going up ").Append(thermals[k].w.ToString("F1"));
            said.Append("; the grid ").Append(lightTurned ? "turned, " : "").Append("up ").Append((gy0 - n.foot.y).ToString("F0")).Append(" to ").Append((gy0 + G / Mathf.Max(giy, 1e-6f) - n.foot.y).ToString("F0"))
                .Append(", east ").Append((gx0 - n.x).ToString("F0")).Append(" to ").Append((gx0 + G / Mathf.Max(gix, 1e-6f) - n.x).ToString("F0"))
                .Append(", kept up ").Append((keepY0 - n.foot.y).ToString("F0")).Append(" to ").Append((keepY1 - n.foot.y).ToString("F0"));
            return said.ToString();
        }
#endif
    }
}
