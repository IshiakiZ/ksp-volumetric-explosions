using System;
using System.Threading;
using UnityEngine;
using Random = UnityEngine.Random;

namespace VolumetricExplosions
{
    /// <summary>
    /// The fourth part of a Site: what is left in the smoke where something has gone through it (since 0.5.0).
    ///
    /// The air already parted round the parts of a craft and round other mods' pieces of wreckage (see Wash and the mover), but only
    /// as finely as the smoke is made: puffs a metre or more across, drawn from a grid whose cells are as big. A piece of wreckage a
    /// metre wide going through a cloud cut no hole that could be seen, and a fast craft left no wake. Three things are done about
    /// that, each with a setting of its own:
    ///
    /// * Looking ahead ("predict"). Every frame the way each thing in or near the smoke is going is followed on for half a second
    ///   (falling as it goes), and the puffs it will pass through before then are split into smaller ones, the smoke in them kept
    ///   (see Refine), so that when it gets there it pushes aside smoke as finely as the grid can show it. They grow and mix again,
    ///   as all smoke does.
    /// * Wakes ("wakes"). Each thing's way through the smoke is kept as a few straight lengths with their ages, and the shader draws
    ///   along them, pixel by pixel whatever the grid (see carve in volume.glsl): a tunnel as wide as the thing, clean at first and
    ///   filling in from its walls over a second or two, the walls a little thicker for the smoke pushed into them, and the pattern
    ///   of the smoke round it wound round by the air left turning there.
    /// * Swirls ("vortices"). The air behind a moving thing is left turning, and carries the smoke round with it (see Swirled):
    ///   behind a blunt thing in eddies turning one way and then the other along its path (a vortex street, shed at about a fifth of
    ///   its speed over its width: one each way for every five widths of the way), and behind a craft's wings in two tubes of air
    ///   turning against each other at the tips, as strong as the lift the wings carry (circulation = lift / (air density x speed x
    ///   span)). Each tube turns the air round it out to well beyond the other (as 1 / distance outside its core), so between the two
    ///   the air goes down, and outside them up: the pair sinks, carrying the smoke between them down with it (a trench in a cloud a
    ///   craft has flown through), and the smoke at the edges of that is rolled up round each tube.
    /// </summary>
    public sealed partial class Site
    {
        /// <summary>Something moving through the air as a whole (a craft, a piece of wreckage): where it is, how it moves, how wide it is across its way, and its wings (see Body, in Wash).</summary>
        struct Body { public Vector3 at, going; public float r, speed, span, lift; public Vector3 tipL, tipR; }
        readonly Body[] bodies = new Body[16];
        int bodyCount;

        /// <summary>One straight length of a wake: from a to b (b the newer end), how long ago the thing was at each end, how wide, how hard the air in it turns (and which way, and how far along it the turning changes from one way to the other), and whether the thing is still making it.</summary>
        struct Wake { public Vector3 a, b, going; public float ageA, ageB, r, swirl, wave, sense, life, span, sink; public bool live, tip, clears; }
        const int MostWakes = 8;
        readonly Wake[] wakes = new Wake[MostWakes];
        readonly bool[] wakeSeen = new bool[MostWakes];
        int wakeCount;
        Vector3 wakeLo, wakeHi;                      // a box round every wake and the air they turn (see Swirled)

        /// <summary>The way a thing is going, followed on for a moment: from where, which way, how far, how wide it is; and how small the puffs it will pass through are made.</summary>
        struct Coming { public Vector3 from, way; public float length, r, small; }
        readonly Coming[] aheads = new Coming[16];
        int aheadCount;
        Vector3 aheadLo, aheadHi;
        const byte Split = 32;                       // (a puff the mover found in the way of something: it is split, see Refine)
        const byte Refined = 64;                     // (a puff made by splitting another: it is not split again, see InTheWay)
        readonly int[] splitting = new int[384];
        int splitCount;                              // (counted up by the mover's threads together: see InTheWay)
        const float LookAheadFor = 0.5f;             // seconds
        const float SwirlDies = 1.2f;                // seconds: how long the air left turning behind a thing goes on turning (and dragged after it)
        const float TipDies = 3f;                    // seconds: the same for the tubes of air behind a wing's tips, which last longer (fading to half in this time)
        const float TipReach = 1.2f;                 // spans: how far out from its tube the air a wing's tip leaves is turned
        const float TipWound = 4f, TipSpiral = 3.5f;  // (drawn: how far round, radians, a tip's vortex winds the smoke at its core, and out to how many of its core's widths: see carve)
        int refined;                                 // (for the development build: puffs split so far)

        /// <summary>Note a thing that moves through the air as a whole (from Wash, and TakeMovers), for its wake.</summary>
        void Moves(Vector3 at, Vector3 going, float r, float span = 0f, float lift = 0f, Vector3 tipL = default(Vector3), Vector3 tipR = default(Vector3))
        {
            if (bodyCount >= bodies.Length) return;
            float speed = going.magnitude;
            if (speed < 2f) return;
            bodies[bodyCount++] = new Body { at = at, going = going, r = Mathf.Max(0.2f, r), speed = speed, span = span, lift = lift, tipL = tipL, tipR = tipR };
        }

        /// <summary>
        /// A craft as a whole, for its wake: how wide it is across the way it is going (its parts' farthest reach out from the line through
        /// its middle, wings left out), and its wings: the lift they carry and where their tips are.
        /// </summary>
        void Craft(Vessel v, Vector3 at, Vector3 going, float speed)
        {
            Vector3 way = going / speed;
            Vector3 side = Vector3.Cross(way, Vector3.up);
            if (side.sqrMagnitude < 0.01f) side = Vector3.Cross(way, Vector3.right);
            side.Normalize();
            float wide = 0.4f, lift = 0f, leftMost = float.MaxValue, rightMost = float.MinValue, leftHalf = 0f, rightHalf = 0f;
            Vector3 tipL = at, tipR = at;
            int every = Mathf.Max(1, v.parts.Count / 40);
            for (int k = 0; k < v.parts.Count; k += every)
            {
                Part part = v.parts[k];
                if (part == null) continue;
                Vector3 where = frame.ToLocal(part.transform.position), off = where - at;
                float half = part.prefabSize.sqrMagnitude < 1e-4f ? 0.5f : 0.5f * Mathf.Max(part.prefabSize.x, Mathf.Max(part.prefabSize.y, part.prefabSize.z));
                bool wing = false;
                for (int m = 0; m < part.Modules.Count; m++)
                    if (part.Modules[m] is ModuleLiftingSurface surface) { wing = true; lift += surface.liftForce.magnitude * every; }
                if (wing)
                {
                    float across = Vector3.Dot(off, side);
                    if (across < leftMost) { leftMost = across; tipL = where; leftHalf = half; }
                    if (across > rightMost) { rightMost = across; tipR = where; rightHalf = half; }
                    continue;
                }
                float along = Vector3.Dot(off, way), out2 = off.sqrMagnitude - along * along;
                float reach = Root(out2 > 0f ? out2 : 0f) + 0.6f * half;
                if (reach > wide) wide = reach;
            }
            float span = 0f;
            if (rightMost > leftMost)
            {
                // (a wing's tip is beyond the middle of its outermost part)
                tipL -= side * (0.6f * leftHalf); tipR += side * (0.6f * rightHalf);
                span = rightMost - leftMost + 0.6f * (leftHalf + rightHalf);
            }
            Moves(at, going, Mathf.Min(wide, 12f), span, lift, tipL, tipR);
        }

        /// <summary>
        /// Once a frame, after Wash: the way each thing is going followed on (for the mover to split the puffs in its way, see InTheWay);
        /// each thing's wake made longer, or begun where it has come into the smoke, or ended where it has gone out of it or turned; every
        /// wake older, carried on the wind, and let go once it has filled in.
        /// </summary>
        void Wakes(float dt)
        {
            aheadCount = 0;
            aheadLo = Vector3.one * 1e9f; aheadHi = -aheadLo;
            // (Not in a patch that goes along with a ship: the ship stands still in it, and its young smoke and flame are its own.)
            if (riding) { wakeCount = 0; wakeLo = aheadLo; wakeHi = aheadHi; return; }
            for (int n = 0; n < MostWakes; n++) wakeSeen[n] = false;
            const float margin = 4f;
            for (int k = 0; k < bodyCount; k++)
            {
                Body b = bodies[k];
                // (A rocket does not clear a tunnel in the smoke its own engines are laying: in a patch an engine is feeding, what is
                // at one of its nozzles leaves a wake that turns the smoke round it and drags it after it, but clears nothing (its
                // exhaust fills its way), and nothing is split ahead of it.)
                bool feeds = vented && Feeding(b.at, b.r);
                bool inside = count > 0 && b.at.x > bx0 - b.r - margin && b.at.x < bx1 + b.r + margin && b.at.y > by0 - b.r - margin && b.at.y < by1 + b.r + margin
                              && b.at.z > bz0 - b.r - margin && b.at.z < bz1 + b.r + margin;
                // (A wake begun goes on a little way out of the smoke, for a third of a second's going: the air dragged along behind the
                // thing draws a tail of smoke out of the cloud after it, and the turning air rolls up the edge it came out of.)
                float beyond = margin + 0.3f * b.speed;
                bool near = count > 0 && b.at.x > bx0 - b.r - beyond && b.at.x < bx1 + b.r + beyond && b.at.y > by0 - b.r - beyond && b.at.y < by1 + b.r + beyond
                            && b.at.z > bz0 - b.r - beyond && b.at.z < bz1 + b.r + beyond;
                if (Settings.Predict && !feeds && count > 0 && aheadCount < aheads.Length)
                {
                    // (straight on, falling as it goes: over half a second a thrown piece's arc is within a metre of that)
                    Vector3 end = b.at + b.going * LookAheadFor + Vector3.down * (0.5f * gravity * LookAheadFor * LookAheadFor);
                    Vector3 way = end - b.at;
                    float length = way.magnitude;
                    if (length > 0.5f)
                    {
                        float small = Mathf.Max(0.3f, Mathf.Max(0.8f * b.r, 0.9f * cellNow));
                        // (as wide as the thing, or as its wings, whose tips leave the turning air that rolls the smoke up)
                        float wide = Mathf.Max(b.r, 0.5f * b.span + 0.5f);
                        aheads[aheadCount++] = new Coming { from = b.at, way = way / length, length = length, r = wide, small = small };
                        Vector3 reach = Vector3.one * (wide + 4f);
                        aheadLo = Vector3.Min(aheadLo, Vector3.Min(b.at, end) - reach);
                        aheadHi = Vector3.Max(aheadHi, Vector3.Max(b.at, end) + reach);
                    }
                }
                if (!Settings.Wakes && !Settings.Vortices) continue;
                // (Behind wings the craft's own wake is carried down between the tubes their tips leave, as fast as the two together
                // turn the air down half way between them (see Swirled), which is how fast the smoke there goes down.)
                float sink = 0f;
                if (b.span > 2f && b.lift > 1f)
                    sink = Mathf.Min(5f, 4f * b.lift * 1000f / (1.225f * Mathf.Max(air, 0.01f) * b.speed * b.span) / (6.2832f * b.span));
                Track(b.at, b.going, b.speed, b.r, false, 0f, inside, near, dt, !feeds, 0f, sink);
                // A craft's wings leave a tube of turning air at each tip, as strong as the lift they carry.
                if (b.span > 2f && b.lift > 1f)
                {
                    float density = 1.225f * Mathf.Max(air, 0.01f), circulation = b.lift * 1000f / (density * b.speed * b.span);
                    float core = Mathf.Max(0.3f, 0.07f * b.span);
                    float spin = Mathf.Min(circulation / (6.2832f * core), 0.8f * b.speed);      // metres a second at the edge of the core
                    Track(b.tipL, b.going, b.speed, core, true, spin, inside, near, dt, !feeds, b.span);
                    Track(b.tipR, b.going, b.speed, core, true, -spin, inside, near, dt, !feeds, b.span);
                }
            }
            // Older; carried along with the air; and those that have filled in let go.
            wakeLo = Vector3.one * 1e9f; wakeHi = -wakeLo;
            for (int n = wakeCount - 1; n >= 0; n--)
            {
                ref Wake w = ref wakes[n];
                if (w.live && !wakeSeen[n]) w.live = false;
                w.ageA += dt;
                if (!w.live) w.ageB += dt;
                if (w.ageB > w.life) { wakes[n] = wakes[--wakeCount]; continue; }
                float height = hasGround ? 0.5f * (w.a.y + w.b.y) - GroundAt(0.5f * (w.a.x + w.b.x), 0.5f * (w.a.z + w.b.z)) : 10f;
                float wind = wind10 * Aloft(Mathf.Max(0f, height));
                Vector3 drift = new Vector3(windX * wind, 0f, windZ * wind) * dt;
                // (the two tubes behind wings go down together, each carried down by the other: at circulation / (2 pi x their distance
                // apart, which is a little less than the span once the air behind the wing has rolled up), as long as they turn)
                // (each end as fast as the turning there, which fades with its age)
                float down = w.tip ? Mathf.Min(3f, Mathf.Abs(w.swirl) * w.r / (0.785f * Mathf.Max(w.span, 1f))) : w.sink;
                w.a += drift + Vector3.down * (down / (1f + w.ageA / TipDies) * dt);
                if (!w.live) w.b += drift + Vector3.down * (down / (1f + w.ageB / TipDies) * dt);
            }
            for (int n = 0; n < wakeCount; n++)
            {
                float reach = 2.6f * wakes[n].r * (1f + 0.3f * Mathf.Max(wakes[n].ageA, wakes[n].ageB)) + 1f;
                if (wakes[n].tip) reach = Mathf.Max(reach, TipReach * wakes[n].span + 1f);
                wakeLo = Vector3.Min(wakeLo, Vector3.Min(wakes[n].a, wakes[n].b) - Vector3.one * reach);
                wakeHi = Vector3.Max(wakeHi, Vector3.Max(wakes[n].a, wakes[n].b) + Vector3.one * reach);
            }
        }

        /// <summary>Whether something here is what is feeding this patch: one of its engines' nozzles, seen in the last half second, is within its reach.</summary>
        bool Feeding(Vector3 at, float r)
        {
            float near = r + 12f;
            for (int n = 0; n < ventCount; n++)
                if (time - vents[n].seen < 0.5f && (vents[n].last - at).sqrMagnitude < near * near) return true;
            return false;
        }

        /// <summary>The wake of one thing (or of one wing tip): the one it has been making, made longer; or a new one, where it has turned too far for the last to go straight on, or there is none.</summary>
        void Track(Vector3 at, Vector3 going, float speed, float r, bool tip, float spin, bool inside, bool near, float dt, bool clears, float span = 0f, float sink = 0f)
        {
            int best = -1;
            float bestFar = float.MaxValue;
            for (int n = 0; n < wakeCount; n++)
            {
                if (!wakes[n].live || wakes[n].tip != tip || wakeSeen[n]) continue;
                Vector3 expected = wakes[n].b + wakes[n].going * dt;
                float far = (expected - at).sqrMagnitude;
                if (far < bestFar) { bestFar = far; best = n; }
            }
            float match = r + speed * dt * 1.5f + 1f;
            if (best >= 0 && bestFar < match * match)
            {
                ref Wake w = ref wakes[best];
                wakeSeen[best] = true;
                Vector3 axis = at - w.a;
                float length = axis.magnitude;
                // (turned more than eight degrees from the way it was going: that length ends where it was, and a new one goes on from there)
                Vector3 before = w.b - w.a;
                if (before.magnitude > Mathf.Max(2f * r, 3f) && length > 1e-3f && Vector3.Dot(axis / length, before.normalized) < 0.99f)
                {
                    w.live = false;
                    if (inside) Begin(w.b, at, going, speed, r, tip, spin, dt, clears, span, sink);
                    return;
                }
                if (!near) { w.live = false; return; }
                w.b = at; w.ageB = 0f; w.going = going; w.clears = clears; w.sink = sink;
                return;
            }
            if (inside) Begin(at - going * dt, at, going, speed, r, tip, spin, dt, clears, span, sink);
        }

        void Begin(Vector3 from, Vector3 at, Vector3 going, float speed, float r, bool tip, float spin, float ageFrom, bool clears, float span, float sink)
        {
            int n;
            if (wakeCount < MostWakes) n = wakeCount++;
            else
            {
                // (full: the oldest of those no longer being made gives way; or else the oldest)
                n = 0;
                for (int m = 1; m < MostWakes; m++)
                    if ((!wakes[m].live && wakes[n].live) || (wakes[m].live == wakes[n].live && wakes[m].ageA > wakes[n].ageA)) n = m;
            }
            // How hard the air behind it turns: behind a blunt thing about a quarter of its speed at the edge of the wake, one way and
            // then the other every two and a half widths (a vortex street, shed at a fifth of the speed over the width: so a whole turn
            // of one way and the other in five widths).
            wakes[n] = new Wake
            {
                a = from, b = at, going = going, ageA = ageFrom, ageB = 0f, r = r, live = true, tip = tip, clears = clears, span = span, sink = sink,
                swirl = tip ? spin : Mathf.Min(0.25f * speed, 30f), sense = tip ? Mathf.Sign(spin) : (Random.value < 0.5f ? 1f : -1f), wave = tip ? 0f : 10f * r,
                life = tip ? 6f + 0.5f * r : 2.5f + 0.25f * r + 0.01f * speed,
            };
            wakeSeen[n] = true;
        }

        // ---- told to the shader (see carve in volume.glsl)
        static readonly int[] wakeA = Ids("_VolWakeA"), wakeB = Ids("_VolWakeB"), wakeC = Ids("_VolWakeC");
        static readonly int wakesNote = Shader.PropertyToID("_VolWakes"), wakeLoNote = Shader.PropertyToID("_VolWakeLo"), wakeHiNote = Shader.PropertyToID("_VolWakeHi");

        static int[] Ids(string name)
        {
            var ids = new int[MostWakes];
            for (int n = 0; n < MostWakes; n++) ids[n] = Shader.PropertyToID(name + n);
            return ids;
        }

        /// <summary>Every frame (from TuneVolume): the wakes in the grid's axes, in the box as it stood when its grid was made, and a box round all of them.</summary>
        void TuneWakes()
        {
            int shown = Settings.Wakes ? wakeCount : 0;
            Vector3 lo = Vector3.one * 1e9f, hi = -lo;
            for (int n = 0; n < MostWakes; n++)
            {
                if (n >= shown) { boxMaterial.SetVector(wakeC[n], Vector4.zero); continue; }
                Wake w = wakes[n];
                Vector3 a = ToGrid(w.a) - slid, b = ToGrid(w.b) - slid;
                // (how far round the pattern at the edge of the wake is wound, once wound: as far as the air's turning carries it in
                // the time that turning lasts, and never more than a fifth of a turn: wound further, the pattern was drawn out into
                // rings round the wake's line, as of a record, plain to see down the wake; the churning eddies do the rest, see carve)
                float wound = Settings.Vortices ? Mathf.Clamp(Mathf.Abs(w.swirl) * SwirlDies / Mathf.Max(w.r, 0.2f), 0f, 1.2f) : 0f;
                // (a wing tip's: wound as round a vortex, into a spiral, told with how far out it reaches as a wave length below nought)
                if (w.tip) wound = Settings.Vortices ? TipWound : 0f;
                boxMaterial.SetVector(wakeA[n], new Vector4(a.x, a.y, a.z, w.ageA));
                boxMaterial.SetVector(wakeB[n], new Vector4(b.x, b.y, b.z, w.ageB));
                // (a wake that clears nothing is told with its width below nought: see carve)
                boxMaterial.SetVector(wakeC[n], new Vector4(w.clears ? w.r : -w.r, wound, w.tip ? -TipSpiral : w.wave, w.tip ? Mathf.Sign(w.swirl) : w.sense));
                float reach = (w.tip ? TipSpiral : 2.2f) * w.r * (1f + 0.3f * Mathf.Max(w.ageA, w.ageB)) + 1f;
                lo = Vector3.Min(lo, Vector3.Min(a, b) - Vector3.one * reach);
                hi = Vector3.Max(hi, Vector3.Max(a, b) + Vector3.one * reach);
            }
            boxMaterial.SetVector(wakesNote, new Vector4(shown, 1.6f, SwirlDies, 1f));
            boxMaterial.SetVector(wakeLoNote, lo);
            boxMaterial.SetVector(wakeHiNote, hi);
        }

        /// <summary>
        /// (In the mover, on a worker thread.) The air the wakes leave turning, where this puff is: round each wake's line, fastest at the
        /// edge of its core, fading as it ages; behind a blunt thing out to two and a half widths, one way and then the other along its
        /// wake; round a wing's tip as a vortex does, as 1 / distance out to beyond the other tip (so that between the two the air goes
        /// down).
        /// </summary>
        static void Swirled(ref P q, Wake[] wk, int n, ref float airX, ref float airY, ref float airZ, float[] sin)
        {
            for (int k = 0; k < n; k++)
            {
                float ax = wk[k].a.x, ay = wk[k].a.y, az = wk[k].a.z;
                float abx = wk[k].b.x - ax, aby = wk[k].b.y - ay, abz = wk[k].b.z - az;
                float long2 = abx * abx + aby * aby + abz * abz;
                if (long2 < 1e-4f) continue;
                float s = ((q.x - ax) * abx + (q.y - ay) * aby + (q.z - az) * abz) / long2;
                // (ahead of a wing's tip that is still being drawn on, the turning it leaves is not there yet: half of it at the tip
                // itself, as at the end of a line of turning air, and none a little way ahead)
                float ahead = 1f;
                if (s > 1f && wk[k].tip && wk[k].live)
                {
                    float beyond = (s - 1f) * Root(long2), within = 0.25f * wk[k].span + 0.5f;
                    if (beyond > within) continue;
                    ahead = 0.5f * (1f - beyond / within);
                }
                s = s < 0f ? 0f : s > 1f ? 1f : s;
                float ox = q.x - (ax + abx * s), oy = q.y - (ay + aby * s), oz = q.z - (az + abz * s);
                float age = wk[k].ageA + (wk[k].ageB - wk[k].ageA) * s;
                float r = wk[k].r * (1f + 0.3f * age), d2 = ox * ox + oy * oy + oz * oz;
                if (wk[k].tip)
                {
                    // A wing's tip: the air turns round the tube as round a vortex, as fast as the tip left it at the edge of its core,
                    // and as 1 / distance beyond (the circulation, the same all the way out), so that each tube moves the air round
                    // the other: between them down, outside them up. The core widens as it ages; the circulation fades slowly.
                    float reach = TipReach * wk[k].span + r;
                    if (d2 > reach * reach || d2 < 1e-6f) continue;
                    float far = Root(d2), circ = wk[k].swirl * wk[k].r / (1f + age / TipDies) * ahead;
                    float v = far < r ? circ * far / (r * r) : circ / far;
                    float edge = far > 0.75f * reach ? (reach - far) / (0.25f * reach) : 1f;
                    v *= edge;
                    float invT = 1f / (Root(long2) * far);
                    airX += (aby * oz - abz * oy) * invT * v; airY += (abz * ox - abx * oz) * invT * v; airZ += (abx * oy - aby * ox) * invT * v;
                    continue;
                }
                if (d2 > 6.25f * r * r || d2 < 1e-6f) continue;
                float d = Root(d2), x = d / r;
                float fade = 1f / (1f + age / SwirlDies + 0.5f * (age / SwirlDies) * (age / SwirlDies));
                // The air in the wake goes after the thing that made it, fastest on its line and close behind it (a body drags a
                // wake of air along: a fifth of its speed or so just behind it, less as the wake widens and slows): smoke the thing
                // has gone through is drawn out of the cloud after it in a wisp, which the turning air then curls.
                // (not an engine's young smoke, which is still going its own way: see the hulls in the mover)
                if (!wk[k].tip && x < 1f && ((q.flags & Exhausted) == 0 || q.age >= 1.5f))
                {
                    float gx = wk[k].going.x, gy = wk[k].going.y, gz = wk[k].going.z;
                    float drag = 0.2f * fade * fade * (1f - x * x);
                    airX += gx * drag; airY += gy * drag; airZ += gz * drag;
                }
                float speed = wk[k].swirl * fade * (x < 1f ? x : x < 2.5f ? (2.5f - x) / 1.5f : 0f);
                if (!wk[k].tip)
                {
                    // (which way it turns here: once each way in every so many metres along the wake)
                    float along = s * Root(long2) / (wk[k].wave > 0.1f ? wk[k].wave : 1f);
                    speed *= wk[k].sense * sin[(int)(along * 4096f) & 4095];
                }
                // round the line: (the line's way) x (out from it), one long
                float inv = 1f / (Root(long2) * d);
                float tx = (aby * oz - abz * oy) * inv, ty = (abz * ox - abx * oz) * inv, tz = (abx * oy - aby * ox) * inv;
                airX += tx * speed; airY += ty * speed; airZ += tz * speed;
            }
        }

        /// <summary>
        /// (In the mover, on a worker thread.) Whether this puff is in the way of something coming, and big enough to split before it gets
        /// there (see Refine): noted in a list the threads share.
        /// </summary>
        void InTheWay(ref P q, int i, Coming[] ah, int n)
        {
            // (Once only. Split again and again while still in the way, a puff two metres across went into sixty-four, and a ball
            // sent through the middle of a cloud every two seconds took a patch from four and a half thousand puffs to twenty-seven
            // thousand in fifteen seconds.)
            if ((q.flags & Refined) != 0) return;
            for (int k = 0; k < n; k++)
            {
                if (q.r <= 1.25f * ah[k].small) continue;
                float rx = q.x - ah[k].from.x, ry = q.y - ah[k].from.y, rz = q.z - ah[k].from.z;
                float along = rx * ah[k].way.x + ry * ah[k].way.y + rz * ah[k].way.z;
                if (along < -ah[k].r - q.r || along > ah[k].length + q.r) continue;
                float side2 = rx * rx + ry * ry + rz * rz - along * along, reach = ah[k].r + q.r;
                if (side2 > reach * reach) continue;
                int at = Interlocked.Increment(ref splitCount) - 1;
                if (at < splitting.Length) { splitting[at] = i; q.flags |= Split; }
                return;
            }
        }

        /// <summary>
        /// (On the game's own thread, at the start of a frame, once the mover has finished and before the dead are taken out, while the list
        /// it made is good.) Each puff found in the way of something is split in four, a tetrahedron's corners apart within it, each with a
        /// quarter of its smoke and as wide as a quarter of its room: the smoke is as it was, but in pieces the thing coming can push apart.
        /// At most so many a frame, and none when the air is full.
        /// </summary>
        void Refine()
        {
            int n = Math.Min(splitCount, splitting.Length);
            splitCount = 0;
            if (n == 0) return;
            int made = 0;
            for (int k = 0; k < n; k++)
            {
                int i = splitting[k];
                if (i >= count || (p[i].flags & Split) == 0) continue;
                p[i].flags = (byte)(p[i].flags & ~Split);
                if (!Settings.Predict || made >= 150 || p[i].life < 0f || Air.Alive + 3 > Settings.MaxParticles * 0.9f) continue;
                P q = p[i];
                float r = q.r * 0.63f, spread = q.r * 0.42f;
                Quaternion turn = Random.rotationUniform;
                for (int c = 0; c < 4; c++)
                {
                    Vector3 off = turn * Tetra[c] * spread;
                    int j = c == 0 ? i : Add();
                    if (j < 0) break;
                    if (c > 0) { uint seed = p[j].seed; p[j] = q; p[j].seed = seed; draw[j] = draw[i]; }
                    ref P o = ref p[j];
                    o.x = q.x + off.x; o.y = q.y + off.y; o.z = q.z + off.z;
                    o.ax = q.ax + off.x; o.ay = q.ay + off.y; o.az = q.az + off.z; o.bx = q.bx + off.x; o.by = q.by + off.y; o.bz = q.bz + off.z;
                    o.ex = q.ex + off.x; o.ey = q.ey + off.y; o.ez = q.ez + off.z; o.cx = q.cx + off.x; o.cy = q.cy + off.y; o.cz = q.cz + off.z;
                    o.dx = q.dx + off.x; o.dy = q.dy + off.y; o.dz = q.dz + off.z; o.fx = q.fx + off.x; o.fy = q.fy + off.y; o.fz = q.fz + off.z;
                    o.r = r; o.born = q.born * 0.63f; o.mass = q.mass * 0.25f;
                    o.flags = (byte)((q.flags & ~Split) | Refined);
                }
                made++;
            }
            refined += made;
        }

        static readonly Vector3[] Tetra = { new Vector3(0.57735f, 0.57735f, 0.57735f), new Vector3(0.57735f, -0.57735f, -0.57735f), new Vector3(-0.57735f, 0.57735f, -0.57735f), new Vector3(-0.57735f, -0.57735f, 0.57735f) };

#if DEV
        /// <summary>(For the development build: the wakes and what has been split.)</summary>
        public string WakesNow()
        {
            var say = new System.Text.StringBuilder();
            say.Append(bodyCount).Append(" moving things, ").Append(aheadCount).Append(" followed ahead, ").Append(refined).Append(" puffs split so far; ").Append(wakeCount).Append(" wakes");
            for (int n = 0; n < wakeCount; n++)
                say.Append("\n  ").Append(wakes[n].tip ? "wing tip" : "wake").Append(wakes[n].live ? " (being made)" : "").Append(": ").Append((wakes[n].b - wakes[n].a).magnitude.ToString("F1")).Append(" m long, ")
                   .Append(wakes[n].r.ToString("F1")).Append(" m wide, ages ").Append(wakes[n].ageA.ToString("F1")).Append(" to ").Append(wakes[n].ageB.ToString("F1")).Append(" s, swirl ").Append(wakes[n].swirl.ToString("F1")).Append(" m/s");
            return say.ToString();
        }
#endif
    }
}
