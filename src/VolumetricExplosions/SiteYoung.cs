using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace VolumetricExplosions
{
    /// <summary>
    /// The fourth part of a Site: an engine's smoke while it is young, worked out with the ship's flames in the patch of
    /// air that goes along with the ship, and handed on, once the ship has left it behind, to a patch that stays where
    /// it is; and the smoke that comes out at the ends of a launch pad's flame trench.
    ///
    /// Why young smoke rides with the ship. Laid straight into a patch that stays where it is, a rocket's smoke was a
    /// separate thing from its flame: it began where the flame was taken to end, as a column already as wide as it
    /// would be there, in a grid whose cells along the trail were metres long, drawn as a body of its own over or under
    /// the flame as the two boxes happened to be sorted. Seen close to, the top of the column came on in lumps as the
    /// rocket climbed through each cell, and did not join the flame. Here the smoke is made in the jet itself, at the
    /// nozzle, along with the flame's gas: hot, and showing nothing while it is hot, and coming to show as it cools,
    /// which is where the flame dies. It is drawn in the same grid as the flame (fine, because the patch is no longer
    /// than the flame and what follows it), so the one turns into the other. Once a puff is some way behind the ship it
    /// is the air's: a copy of it is begun in the patch that holds the ship's trail, coming on as the one here fades,
    /// over a third of a second or so, and the trail is drawn from there on as it was.
    /// </summary>
    public sealed partial class Site
    {
        const byte HandedOn = 8;                    // (a puff of young smoke whose copy has gone on to the trail: it fades here, see the mover)

        /// <summary>A puff of smoke on its way from the patch that goes along with a ship to one that stays where it is: where it is and how it is going kept fixed to the body (the scene's own coordinates may move between the two frames it takes).</summary>
        public struct Handoff
        {
            public P p;
            public Vector3d at, going, push;      // in the body's own axes (see CelestialBody.GetRelSurfacePosition)
        }

        readonly List<Handoff> handing = new List<Handoff>();
        readonly List<Handoff> adopting = new List<Handoff>();
        float handFar;                            // how far behind the ship young smoke goes before it is handed on (set by the engines feeding this patch)
        float handThin = 1f;                      // of so many puffs of young smoke, only one goes on, with the smoke of them all in it
        float handNozzle = 0.5f;                  // how wide the engines' nozzles are, for the billows of the trail that takes the smoke
        float youngFed = -100f;                   // when an engine last put young smoke in here

        /// <summary>What this patch handed on in its last step, for Air to give to the patch that holds the ship's trail.</summary>
        public List<Handoff> Handing => handing;
        public float HandNozzle => handNozzle;
        /// <summary>The patch this one last gave its smoke to (see Air.Deliver).</summary>
        public Site TrailSite;
        /// <summary>How fast the ship this patch goes along with is going, in the scene's axes.</summary>
        public Vector3d ShipGoing => frame.east * ridesGoing.x + frame.up * ridesGoing.y + frame.north * ridesGoing.z;

        /// <summary>
        /// How much smoke an engine makes, a second, in the units of a puff's mass (its mass is how much it hides over its
        /// width): as much as the trail it used to lay (see Vents: so many puffs a second of so much each).
        /// </summary>
        public static float SmokeMade(in Exhaust e, float speed, float q, out float laid, out float wide)
        {
            wide = e.nozzle * 2.1f * (1f + speed / 200f);
            float standing = (26f + 34f * Mathf.Sqrt(e.amount)) * e.running;
            laid = Mathf.Max(speed / (0.6f * wide), standing) * q * (0.45f + 0.55f * Mathf.Sqrt(e.amount));
            // (each puff 0.7 to 1.4 times six times the amount times its width squared, and its width 0.75 to 1.25 times the jet's: on average 6.4 times)
            return laid * 6.4f * e.amount * wide * wide;
        }

        /// <summary>
        /// An engine's smoke in the patch of air that goes along with its ship: made at the nozzle with the flame's gas,
        /// leaving it as fast, slowing more gently (it is what the flame becomes, and goes on past where the flame ends),
        /// widening as a jet does; hot, and so showing no smoke until it has cooled, over the last half of the flame. As
        /// many puffs as keep each within a fifth of its width of the next where it comes to show, so that the column is one
        /// thing from where it begins.
        /// </summary>
        void YoungSmoke(ref Vent vent, in Exhaust e, Vector3 rim, Vector3 along, Vector3 going, float dt, float q)
        {
            float shipSpeed = going.magnitude;
            float length = Mathf.Max(e.flame, 4f * e.nozzle);
            float jet = Mathf.Max(6.25f * length, 60f);                 // (as the flame's gas goes: see Flame)
            float thin = Thin;
            float reach = length * Mathf.Lerp(1f, 0.45f, thin), lasts = reach / jet;
            float made = SmokeMade(e, shipSpeed, q, out float laid, out float wide);
            // How fast the puffs come apart where they come to show: they leave the nozzle at the jet's speed, which the air
            // is taking away, and the air takes them back past the ship at its own. As many a second as keep each within a
            // fifth of its width of the next there.
            float apart = 0.6f * jet + 0.3f * shipSpeed;
            // (Within a fifth of its width, not half: the grid of a ship's patch is fine enough to draw each puff as the ball it
            // is, and at half its width apart the column under the flame was a string of beads.)
            float rate = Mathf.Clamp(apart / (0.2f * wide), 0.8f * laid, 700f * Mathf.Max(q, 0.3f));
            if (rate <= 0f) return;
            handThin = Mathf.Max(1f, rate / Mathf.Max(laid, 1f));
            handNozzle = e.nozzle;
            // (Handed on once it is well clear of the flame and of what the flame stirs up: far enough that the handing on is
            // not to be seen against the flame, near enough that this patch, and with it the flame's grid, stays small.)
            handFar = Mathf.Max(handFar, Mathf.Clamp(1.7f * length, 22f, 90f));
            youngFed = time;
            // (the stretch that last frame's puffs have left behind them, as for the flame: see Flame)
            float span = stepDt > 1e-4f ? stepDt : dt;
            vent.due += rate * span;
            int puffs = Mathf.Min((int)vent.due, 96);
            vent.due -= (int)vent.due;
            if (puffs <= 0) return;
            float each = made / rate;
            float slows = 0.375f / lasts;
            float cools = 1.68f / lasts * (1f - 0.75f * thin);
            float hangs = Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(air * 1.4f));
            float stays = e.lasts * hangs / (1f + shipSpeed / 250f);
            float r0 = 0.75f * e.nozzle;
            // (it widens by about a tenth of the way it goes, as a jet does, to be as wide as the trail's puffs were laid by where the flame ends)
            float swell = Mathf.Max(0.04f, (wide - r0) / Mathf.Max(reach, 1f) - 0.02f);
            Vector3 across = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(along, across);
            for (int k = 0; k < puffs; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float since = (k + Random.value) / puffs * span;           // how long ago within that stretch it left
                float turn = Random.Range(0f, Mathf.PI * 2f), out_ = Mathf.Sqrt(Random.value);
                Vector3 side = across * Mathf.Cos(turn) + over * Mathf.Sin(turn);
                // (it is made in the jet's outer layers, which are slower than its middle)
                float speed = jet * Random.Range(0.75f, 0.95f) * (1f - 0.3f * out_ * out_);
                Vector3 c = rim + side * (out_ * 0.9f * e.nozzle) + along * (speed * since);
                Vector3 push = along * speed + side * (out_ * speed * 0.08f);
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.kx = push.x; o.ky = push.y; o.kz = push.z;
                o.kloss = slows; o.drag = 3.5f;
                o.r = r0 * Random.Range(0.85f, 1.15f);
                o.grow = 0.5f * wide;
                // (Where the air is thin the jet opens out far more, as its flame does, and its smoke with it: spread through more
                // room it is that much fainter. Drawn as in thick air, it was a solid white cone behind a rocket twenty kilometres up.)
                o.swell = swell + Swell(along, speed, slows, o.r) + 0.3f * thin;
                o.turb = (2f + 6f * out_ * out_) * (1f - 0.6f * thin);
                o.rise = Random.Range(0.5f, 1.8f);
                o.heat = Random.Range(0.95f, 1.05f);
                o.cool = cools * Random.Range(0.85f, 1.15f);
                o.mass = each * Random.Range(0.7f, 1.3f);
                o.life = stays * Random.Range(0.6f, 1.3f);
                o.age = since;
                float tone = e.shade * Random.Range(0.82f, 1.22f);
                o.ar = tone; o.ag = tone * (1f - 0.05f * e.warm); o.ab = tone * (1f - 0.14f * e.warm);
                o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
                o.flags = Exhausted;
                o.tile = Random.value < 0.25f ? Wispy() : Lumpy();
                Born(i);
            }
        }

        /// <summary>
        /// (Each step, in a patch that goes along with a ship.) The young smoke that the ship has left far enough behind is
        /// the air's now: a copy of each such puff (or of one in so many, carrying the others' smoke: see YoungSmoke) is put
        /// in the list for the patch that holds the trail (see Air.Deliver), to come on there over as long as this one
        /// takes to fade here; this one fades meanwhile (see the mover) and is let go.
        /// </summary>
        void Hand()
        {
            handing.Clear();
            if (!riding || !frame.Riding || handFar <= 0f) return;         // (once the ship has gone this patch stays where it is, and keeps what it has)
            float far2 = handFar * handFar;
            Vector3 by = ridesGoing;
            for (int i = 0; i < count; i++)
            {
                ref P q = ref p[i];
                if (q.mass <= 0f || q.life < 0f || (q.flags & HandedOn) != 0 || q.age < 0.05f) continue;
                if (q.x * q.x + q.y * q.y + q.z * q.z < far2 && q.age < 2.5f) continue;
                // (over as long as it takes to go half as far again at the speed it is going past the ship)
                float sx = q.vx + q.kx, sy = q.vy + q.ky, sz = q.vz + q.kz;
                float cross = Mathf.Clamp(0.5f * handFar / Mathf.Max(Mathf.Sqrt(sx * sx + sy * sy + sz * sz), 1f), 0.15f, 0.6f);
                float left = q.life - q.age;
                q.flags |= HandedOn;
                q.life = q.age + cross;
                if (left <= cross || Random.value * handThin > 1f) continue;
                var l = new Handoff { p = q };
                l.at = body.GetRelSurfacePosition(frame.ToWorld(new Vector3(q.x, q.y, q.z)));
                // (Over the ground it is going as the air takes it here plus the ship's own way, which this patch goes along with.)
                l.going = body.GetRelSurfaceDirection(frame.east * (q.vx + by.x) + frame.up * (q.vy + by.y) + frame.north * (q.vz + by.z));
                l.push = body.GetRelSurfaceDirection(frame.east * q.kx + frame.up * q.ky + frame.north * q.kz);
                l.p.life = left; l.p.age = 0f; l.p.fadeIn = cross;
                l.p.mass = q.mass * handThin;
                l.p.flags = Exhausted;
                handing.Add(l);
            }
        }

#if DEV
        /// <summary>(What this patch holds of young smoke and flame, and what it hands on; or, for one that stays where it is, what it has taken.)</summary>
        public string YoungNow()
        {
            int smoke = 0, flame = 0, handed = 0, hot = 0;
            for (int i = 0; i < count; i++)
            {
                if (p[i].mass > 0f) { smoke++; if (p[i].heat > 0.5f) hot++; } else if (p[i].flame > 0f) flame++;
                if ((p[i].flags & HandedOn) != 0) handed++;
            }
            return (riding ? "ship's patch" : vented ? "trail" : "cloud") + ": " + count + " particles, " + smoke + " smoke (" + hot + " still hot, " + handed + " handed on and fading), " + flame + " flame; hands on at " + handFar.ToString("F0") +
                   " m, one in " + handThin.ToString("F1") + "; box " + (bx1 - bx0).ToString("F0") + " x " + (by1 - by0).ToString("F0") + " x " + (bz1 - bz0).ToString("F0") + ", cells " + cellNow.ToString("F2") + " m; grates " + grateCount + "; trenches " + trenches.Count;
        }
#endif

        /// <summary>
        /// Whether a puff of young smoke handed on at this place belongs in this patch: where an engine's smoke laid there
        /// would feed it (a trail's patch takes so far of the trail and no further, see Feeds); and the cloud of a launch,
        /// which spreads far over the pad, a little further.
        /// </summary>
        public bool Keeps(CelestialBody where, Vector3d world)
        {
            if (Feeds(where, world)) return true;
            if (!vented || where != body || space || vacuum || leaving || riding || count > Settings.MaxParticles * 0.45f) return false;
            bool low = hasGround && ventFrom.y - GroundAt(ventFrom.x, ventFrom.z) < 30f;
            return low && (frame.ToLocal(world) - ventFrom).sqrMagnitude < 75f * 75f;
        }

        /// <summary>
        /// Smoke handed on from the patch that goes along with a ship: kept until this patch's next step, when the
        /// particles can be put in (they are being moved on another thread meanwhile). The first such smoke makes this
        /// the patch of an engine's trail, as being fed an engine's smoke does (see Fed).
        /// </summary>
        public void Adopt(List<Handoff> list, float nozzle, Vector3d going)
        {
            if (list.Count == 0) return;
            frame.Refresh();
            float speed = (float)going.magnitude;
            if (!vented)
            {
                vented = true;
                ventedAt = time;
                Vector3d first = body.position + body.BodyFrame.LocalToWorld(list[0].at.xzy).xzy;
                ventFrom = frame.ToLocal(first);
                if (count >= 50 || litOnce) turnDecided = true;
                if (count < 50)
                {
                    scale = Mathf.Clamp(2f + 6f * nozzle, 2.5f, 12f);
                    detailRepeat = Mathf.Clamp(scale * 0.55f + 3.5f, 4f, 26f);
                    fullLength = Mathf.Clamp(0.13f * scale, 0.8f, 4f);
                }
                if (!turnDecided)
                {
                    // (a trail left by an engine going fast, with no ground here for it to lie on, lies along the way it went: see TurnAlong)
                    turnDecided = true;
                    if (speed > 60f && (!hasGround || ventFrom.y - GroundAt(ventFrom.x, ventFrom.z) > 120f)) TurnAlong(frame.DirToLocal(going) / speed);
                }
            }
            fedAt = time;
            fedSpeed = speed;
            adopting.AddRange(list);
        }

        /// <summary>(In the step, while nothing else is moving the particles.) The smoke handed on since the last step, put in: where it is, how it is going, and the billows of the smoke about it.</summary>
        void TakeAdopted()
        {
            if (adopting.Count == 0) return;
            frame.Refresh();
            Vector3d centre = body.position;
            foreach (Handoff l in adopting)
            {
                int i = Add();
                if (i < 0) break;
                P o = l.p;
                Vector3d world = centre + body.BodyFrame.LocalToWorld(l.at.xzy).xzy;
                Vector3 at = frame.ToLocal(world);
                Vector3 v = frame.DirToLocal(body.BodyFrame.LocalToWorld(l.going.xzy).xzy), k = frame.DirToLocal(body.BodyFrame.LocalToWorld(l.push.xzy).xzy);
                o.x = at.x; o.y = at.y; o.z = at.z;
                o.vx = v.x; o.vy = v.y; o.vz = v.z;
                o.ux = v.x; o.uy = v.y; o.uz = v.z;
                o.kx = k.x; o.ky = k.y; o.kz = k.z;
                o.seed = p[i].seed;
                p[i] = o;
                Born(i);
            }
            adopting.Clear();
        }

        // ================================================================== a launch pad's flame trench

        readonly List<Pad> trenches = new List<Pad>();

        /// <summary>This patch holds the smoke that comes out of this pad's trench (see Air.Trenches).</summary>
        public void Trench(Pad pad, float nozzle)
        {
            if (!trenches.Contains(pad)) trenches.Add(pad);
            if (!vented)
            {
                frame.Refresh();
                vented = true;
                ventedAt = time;
                ventFrom = frame.ToLocal((Vector3d)pad.middle);
                turnDecided = true;                                         // (the cloud of a launch lies on the ground, every way at once)
                if (count < 50)
                {
                    scale = Mathf.Clamp(2.5f + 8f * nozzle, 3f, 12f);
                    detailRepeat = Mathf.Clamp(scale * 0.55f + 3.5f, 4f, 26f);
                    fullLength = Mathf.Clamp(0.13f * scale, 0.8f, 4f);
                }
            }
            fedAt = time;
        }

        /// <summary>
        /// The smoke that comes out at the ends of each pad's flame trench that feeds this patch: all that the engines
        /// standing over its grate blow down through it, coming out low and fast at both ends, along the ground and a
        /// little up the ramp each end is, already cooled in the trench and stirred hard, and rolling up and out over the
        /// ground from there. (A trench takes a moment to fill when the engines light, and to empty when they stop: see Pad.fill.)
        /// </summary>
        void Ends(float dt, float q)
        {
            for (int t = trenches.Count - 1; t >= 0; t--)
            {
                Pad pad = trenches[t];
                if (pad == null || pad.site != this) { trenches.RemoveAt(t); continue; }
                float made = pad.made * pad.fill;
                if (made <= 0f || pad.ends.Length == 0) continue;
                fedAt = time;
                float hangs = Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(air * 1.4f));
                float rate = Mathf.Clamp(20f + 16f * Mathf.Sqrt(made), 20f, 80f) * Mathf.Clamp(q, 0.3f, 1.5f);      // puffs a second at each end
                float each = 1.3f * made / (rate * pad.ends.Length);                                                       // (more of it: the trench's gale raises what lies in it)
                float speed = (24f + 36f * pad.strongest) * Mathf.Sqrt(pad.fill);
                for (int n = 0; n < pad.ends.Length; n++)
                {
                    Transform end = pad.ends[n];
                    if (end == null) continue;
                    Vector3 dir = frame.DirToLocal((Vector3d)end.forward);
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-4f) continue;
                    dir = dir.normalized;
                    Vector3 across = new Vector3(dir.z, 0f, -dir.x);
                    Vector3 mouth = frame.ToLocal((Vector3d)(end.position + end.forward * 1.5f));
                    // (The ground in front of it, looked for: the end is a hole low in the side of the pad, metres under its deck,
                    // and until this patch has sounded the ground about it, it takes the ground to be as level as the deck.)
                    float ground = hasGround ? GroundAt(mouth.x, mouth.z) : mouth.y;
                    Vector3 upward = (Vector3)frame.up;
                    if (Physics.Raycast(end.position + end.forward * 1.5f + upward * 6f, -upward, out RaycastHit below, 20f, 1 << 15, QueryTriggerInteraction.Ignore))
                        ground = frame.ToLocal((Vector3d)below.point).y;
                    float w = pad.wide[n], h = pad.tall[n];
                    // (The fire down in the trench lights the smoke coming out of it: at night its ends glow, and the clouds
                    // rolling out of them are lit from inside and below.)
                    Vector3 glowAt = mouth - dir * 1.5f;
                    glowAt.y = ground + 0.7f * h;
                    int owner = 0x5eed + 31 * t + n;
                    float glows = Mathf.Max(3f, 1.2f * (w + h)), strength = 1.8f * pad.strongest * pad.fill;
                    int at = n < pad.lamp.Length ? pad.lamp[n] : -1;
                    if (at < 0 || at >= lampCount || !lamps[at].quiet || lamps[at].owner != owner)
                    {
                        at = AddLamp(glowAt.x, glowAt.y, glowAt.z, glows, strength, 0.6f, (byte)Mathf.Clamp(pad.tint, 0, 7), true, 0f, 0f);
                        lamps[at].quiet = true;
                        lamps[at].owner = owner;
                        if (n < pad.lamp.Length) pad.lamp[n] = at;
                    }
                    else
                    {
                        ref Lamp lamp = ref lamps[at];
                        lamp.x = glowAt.x; lamp.y = glowAt.y; lamp.z = glowAt.z; lamp.r = glows; lamp.age = 0f; lamp.peak = strength;
                    }
                    pad.due[n] += rate * dt;
                    int puffs = Mathf.Min((int)pad.due[n], 24);
                    pad.due[n] -= (int)pad.due[n];
                    for (int k = 0; k < puffs; k++)
                    {
                        int i = Add();
                        if (i < 0) break;
                        ref P o = ref p[i];
                        float lateral = Random.Range(-1f, 1f);
                        Vector3 c = mouth + across * (lateral * w * 0.8f) + dir * Random.Range(0f, 2f);
                        o.r = Mathf.Min(w, h) * Random.Range(0.55f, 0.85f);
                        c.y = ground + o.r * 0.6f + Random.Range(0f, 0.6f) * h;
                        // (out along the ground and a little up the ramp: and spreading, the sides of it out to the sides)
                        float fast = speed * Random.Range(0.7f, 1.1f);
                        Vector3 push = (dir + Vector3.up * Random.Range(0.08f, 0.26f) + across * (lateral * 0.35f)) * fast;
                        o.x = c.x; o.y = c.y; o.z = c.z;
                        o.kx = push.x; o.ky = push.y; o.kz = push.z;
                        o.kloss = 1.6f; o.drag = 3.5f;
                        o.grow = 0.6f * o.r;
                        o.swell = 0.12f + Swell(dir, fast, 1.6f, o.r);
                        o.turb = 6f;
                        o.rise = Random.Range(1.2f, 3.2f);
                        o.mass = each * Random.Range(0.7f, 1.3f);
                        o.life = pad.lasts * hangs * Random.Range(0.6f, 1.3f);
                        o.fadeIn = 0.12f;
                        // (the engines' smoke, paler for what of the trench it carries out with it)
                        float tone = Mathf.Lerp(pad.shade, 0.78f, 0.3f) * Random.Range(0.85f, 1.15f);
                        o.ar = tone; o.ag = tone * (1f - 0.04f * pad.warm); o.ab = tone * (1f - 0.1f * pad.warm);
                        o.tint = (byte)Mathf.Clamp(pad.tint, 0, 7);
                        o.flags = Exhausted;
                        o.tile = Lumpy();
                        Born(i);
                    }
                }
            }
        }

        // ---- the grates of pads under a patch that goes along with a ship (see Under, and the mover): what goes down through one goes into its trench
        const int MostGrates = 2;
        readonly float[] grateX = new float[MostGrates], grateZ = new float[MostGrates], grateAX = new float[MostGrates], grateAZ = new float[MostGrates],
                         grateBX = new float[MostGrates], grateBZ = new float[MostGrates], grateA = new float[MostGrates], grateB = new float[MostGrates];
        int grateCount;

        /// <summary>Where the grates of the pads near the ship are, in this patch's axes (for a patch that goes along with a ship: each frame).</summary>
        void Grates()
        {
            grateCount = 0;
            List<Pad> pads = Pads.All;
            for (int n = 0; n < pads.Count && grateCount < MostGrates; n++)
            {
                Pad pad = pads[n];
                if (pad.grate == null) continue;
                Vector3 at = frame.ToLocal((Vector3d)pad.middle);
                if (at.sqrMagnitude > 200f * 200f) continue;
                Vector3 a = frame.DirToLocal((Vector3d)pad.axisA), b = frame.DirToLocal((Vector3d)pad.axisB);
                a.y = 0f; b.y = 0f;
                if (a.sqrMagnitude < 0.5f || b.sqrMagnitude < 0.5f) continue;
                a.Normalize(); b.Normalize();
                int g = grateCount++;
                grateX[g] = at.x; grateZ[g] = at.z;
                grateAX[g] = a.x; grateAZ[g] = a.z; grateBX[g] = b.x; grateBZ[g] = b.z;
                grateA[g] = pad.halfA; grateB[g] = pad.halfB;
            }
        }

        /// <summary>Whether a place in this patch is over the grate of a pad (see Grates).</summary>
        bool OverGrate(float x, float z)
        {
            for (int g = 0; g < grateCount; g++)
            {
                float dx = x - grateX[g], dz = z - grateZ[g];
                float u = dx * grateAX[g] + dz * grateAZ[g], w = dx * grateBX[g] + dz * grateBZ[g];
                if (u > -grateA[g] && u < grateA[g] && w > -grateB[g] && w < grateB[g]) return true;
            }
            return false;
        }
    }
}
