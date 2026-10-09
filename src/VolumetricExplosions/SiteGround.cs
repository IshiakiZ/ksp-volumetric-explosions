using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>The part of a Site that knows the ground under it.</summary>
    public sealed partial class Site
    {
        /// <summary>
        /// The ground under a cloud as it really lies: its height at each point of a square lattice, sounded
        /// with the game's own collision queries (which run on its worker threads while everything else goes
        /// on). Everything that has to do with the ground reads it from here: where smoke comes to rest, the
        /// way the air near the ground follows it up a rise and down a fall, which way a blast on a slope
        /// throws things, and where the foot of the smoke is drawn.
        ///
        /// (It used to be one tilted plane, fitted through five soundings. Flat ground and an even slope were
        /// right by it; but where the ground fell away, off the edge of the launch pad or over the brow of a
        /// hill, the smoke went straight on with a flat underside in the air.)
        ///
        /// The lattice lies along the site's own axes and its points are whole steps from the site's origin,
        /// the steps being a half metre, or one, two, four and so on: so a lattice moved along after a
        /// drifting cloud, or a coarser one taken for a cloud that has grown, has its points where the last
        /// had points, and finds the same heights there.
        /// </summary>
        sealed class Land
        {
            public const int Side = 65;                    // soundings each way
            public const float Last = Side - 1.001f;
            public const float Steepest = 1.5f;            // (a wall is taken as a slope this steep: metres up for a metre along)
            public readonly float[] h;                     // the height at each point, in the site's axes (changed only on the game's own thread, between two rounds of moving)
            public readonly float x0, z0, step, inv;
            public float[] to;                             // where h is going, while it goes over from what it was to a new sounding
            public float left;                             // seconds of that still to go
            public float made;                             // when it was sounded, by the site's clock

            public Land(float[] h, float x0, float z0, float step)
            {
                this.h = h; this.x0 = x0; this.z0 = z0; this.step = step;
                inv = 1f / step;
            }

            public bool Covers(float x, float z) => x >= x0 && z >= z0 && x <= x0 + (Side - 1) * step && z <= z0 + (Side - 1) * step;
        }

        Land land;                                         // nothing until the first sounding is back: until then the ground is the tilted plane below
        float groundY, groundSX, groundSZ;                 // the ground as a tilted plane: height = groundY - x * groundSX - z * groundSZ (what the blast itself reported, for the first frame or two)
        float expect;                                      // how far from the site's middle the next few moments' smoke will reach (see Explode)
        Vector3 blastUp = Vector3.up;                      // straight out of the ground under the blast in hand (see Burst)

        /// <summary>The height of the ground at a place, between the four soundings round it. (Beyond the lattice: as at its edge.)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Height(float[] h, float x0, float z0, float inv, float x, float z)
        {
            float u = (x - x0) * inv, v = (z - z0) * inv;
            if (!(u > 0f)) u = 0f; else if (u > Land.Last) u = Land.Last;       // (written so that what is no number at all counts as nought)
            if (!(v > 0f)) v = 0f; else if (v > Land.Last) v = Land.Last;
            int i = (int)u, j = (int)v, at = i + j * Land.Side;
            float fu = u - i, fv = v - j;
            float low = h[at] + (h[at + 1] - h[at]) * fu, high = h[at + Land.Side] + (h[at + Land.Side + 1] - h[at + Land.Side]) * fu;
            return low + (high - low) * fv;
        }

        /// <summary>The same, and how steeply the ground rises there to the east and to the north (metres up for a metre along).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Height(float[] h, float x0, float z0, float inv, float x, float z, out float east, out float north)
        {
            float u = (x - x0) * inv, v = (z - z0) * inv;
            if (!(u > 0f)) u = 0f; else if (u > Land.Last) u = Land.Last;
            if (!(v > 0f)) v = 0f; else if (v > Land.Last) v = Land.Last;
            int i = (int)u, j = (int)v, at = i + j * Land.Side;
            float fu = u - i, fv = v - j;
            float a = h[at], b = h[at + 1] - a, c = h[at + Land.Side], d = h[at + Land.Side + 1] - c;
            float low = a + b * fu, high = c + d * fu;
            east = (b + (d - b) * fv) * inv;
            north = (high - low) * inv;
            if (east > Land.Steepest) east = Land.Steepest; else if (east < -Land.Steepest) east = -Land.Steepest;
            if (north > Land.Steepest) north = Land.Steepest; else if (north < -Land.Steepest) north = -Land.Steepest;
            return low + (high - low) * fv;
        }

        float GroundAt(float x, float z)
        {
            Land l = land;
            return l != null ? Height(l.h, l.x0, l.z0, l.inv, x, z) : groundY - x * groundSX - z * groundSZ;
        }

        /// <summary>
        /// Straight out of the ground at a place, taking the lie of the ground over 'span' metres each way
        /// (a blast is not turned aside by one small bump under it).
        /// </summary>
        Vector3 GroundUp(float x, float z, float span)
        {
            if (!hasGround) return Vector3.up;
            Land l = land;
            float east = -groundSX, north = -groundSZ;
            if (l != null)
            {
                float d = Mathf.Max(span, l.step);
                east = Mathf.Clamp((GroundAt(x + d, z) - GroundAt(x - d, z)) / (2f * d), -Land.Steepest, Land.Steepest);
                north = Mathf.Clamp((GroundAt(x, z + d) - GroundAt(x, z - d)) / (2f * d), -Land.Steepest, Land.Steepest);
            }
            return new Vector3(-east, 1f, -north).normalized;
        }

        /// <summary>A direction that points into the ground, turned to point out of it: all of what went in comes back out (keep 1), or a part of it.</summary>
        static Vector3 OffGround(Vector3 way, Vector3 up, float keep)
        {
            float into = Vector3.Dot(way, up);
            return into < 0f ? way - up * (into * (1f + keep)) : way;
        }

        /// <summary>The ground as the blast itself reported it: the place under it and which way that faces. Used until the first sounding is back.</summary>
        void Ground(Vector3d point, Vector3d normal)
        {
            if (hasGround) return;
            Vector3 at = frame.ToLocal(point), n = frame.DirToLocal(normal);
            if (n.y < 0.35f) n = Vector3.up;                 // a wall or a cliff: treat it as level
            groundSX = Mathf.Clamp(n.x / n.y, -0.5f, 0.5f);
            groundSZ = Mathf.Clamp(n.z / n.y, -0.5f, 0.5f);
            groundY = at.y + groundSX * at.x + groundSZ * at.z;
            hasGround = true;
        }

        // ---- sounding it
        NativeArray<RaycastCommand> soundRays;
        NativeArray<RaycastHit> soundHits;
        JobHandle soundJob;
        bool soundOut;
        System.Threading.Tasks.Task<Land> charting;       // making a lattice of soundings that are back
        float soundX0, soundZ0, soundStep, soundTop;       // the lattice being sounded, and the height its soundings are taken from
        float nextSurvey;
        const float NoGround = -1e30f;                     // (a sounding that found nothing)
        static readonly RaycastCommand[] soundList = new RaycastCommand[Land.Side * Land.Side];      // (used on the game's own thread only)
        readonly int[] speck = new int[64];
        readonly float[] speckTo = new float[64];
#if DEV
        float soundSetMs, soundReadMs, soundWaited;
        int soundMissed, soundings, soundSpecks;
#endif

        /// <summary>
        /// Keep the lattice under the cloud: sound the ground again when the cloud has outgrown the lattice or
        /// drifted off it, and now and then anyway (a building may have come down). Called every frame, between
        /// two rounds of moving the particles.
        ///
        /// The soundings themselves are the game's collision queries, which it runs on its worker threads; and
        /// making a lattice of what comes back is done on another thread too. (It is a few thousand numbers,
        /// and on the game's own thread it took two milliseconds and more: a frame held up, every time.)
        /// </summary>
        void Survey(float dt)
        {
            if (space || riding) return;
            if (soundOut && soundJob.IsCompleted)
            {
                soundOut = false;
                soundJob.Complete();
                Land old = land;
                NativeArray<RaycastHit> hits = soundHits;
                float x0 = soundX0, z0 = soundZ0, step = soundStep, top = soundTop, now = time;
                // Where there is sea, the sea is the ground wherever it lies over the land (and where no land was found under it).
                bool sea = body.ocean;
                Vector3d middle = body.position - frame.origin;
                double cx = Vector3d.Dot(middle, frame.east), cy = Vector3d.Dot(middle, frame.up) + body.Radius, cz = Vector3d.Dot(middle, frame.north), curve = 0.5 / body.Radius;
                charting = System.Threading.Tasks.Task.Run(() => Chart(hits, old, x0, z0, step, top, now, sea, cx, cy, cz, curve));
            }
            if (charting != null && charting.IsCompleted)
            {
                Land fresh = null;
                try { fresh = charting.Result; }
                catch (Exception ex) { Addon.Log("the ground could not be sounded: " + ex.GetType().Name + ": " + ex.Message); }
                charting = null;
                soundRays.Dispose();
                soundHits.Dispose();
#if DEV
                soundings++;
                soundWaited = time - soundWaited;
#endif
                if (fresh != null) { land = fresh; hasGround = true; }
                else nextSurvey = time + 3f;                 // (no ground within reach, or none that is solid yet: carry on as before, and look again in a while)
            }
            Land l = land;
            if (l != null && l.to != null)
            {
                // Going over to a new sounding: most of the way in the first half second, all of it in a second and a half.
                // (All at once, the ground would jump under whatever smoke lay where the two differ.)
                l.left -= dt;
                float[] h = l.h, to = l.to;
                if (l.left <= 0f) { Array.Copy(to, h, h.Length); l.to = null; }
                else
                {
                    float share = 1f - Mathf.Exp(-dt / 0.35f);
                    for (int n = 0; n < h.Length; n++) h[n] += (to[n] - h[n]) * share;
                }
            }
            if (Settings.TestGround >= 1f) { land = null; return; }
            if (soundOut || charting != null || time < nextSurvey) return;
            nextSurvey = time + (time - lastBlast < 3f ? 0.1f : 0.5f);
            if (count == 0 && waiting.Count == 0 && l != null) return;

            // The stretch to be covered: everything there is, with room to spread; and in a blast's first moments, as far as it will throw things.
            float bx = bx0, tx = bx1, bz = bz0, tz = bz1;
            if (time - lastBlast < 1f && expect > 0f)
            {
                if (bx > -expect) bx = -expect; if (tx < expect) tx = expect;
                if (bz > -expect) bz = -expect; if (tz < expect) tz = expect;
            }
            float need = Mathf.Max(tx - bx, tz - bz) * 1.3f + 8f, wanted = 0.5f;
            while ((Land.Side - 1) * wanted < need && wanted < 64f) wanted *= 2f;
            if (l != null && l.step >= wanted && l.step <= 4f * wanted)
            {
                // The lattice there is will do while the cloud lies well inside it (and is not far too coarse for it).
                float edge = 2f + 0.05f * (tx - bx > tz - bz ? tx - bx : tz - bz), far = (Land.Side - 1) * l.step;
                if (bx - edge >= l.x0 && tx + edge <= l.x0 + far && bz - edge >= l.z0 && tz + edge <= l.z0 + far)
                {
                    if (time - l.made < 10f) return;
                    Sound(l.x0, l.z0, l.step);
                    return;
                }
                if (far >= need) wanted = l.step;              // (only drifted off it: the same steps, further along)
            }
            float half = 0.5f * (Land.Side - 1) * wanted;
            Sound(Mathf.Round(((bx + tx) * 0.5f - half) / wanted) * wanted, Mathf.Round(((bz + tz) * 0.5f - half) / wanted) * wanted, wanted);
        }

        /// <summary>Set the soundings going: one straight down onto each point of the lattice, from well above anything that could stand there.</summary>
        void Sound(float x0, float z0, float step)
        {
#if DEV
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            const int side = Land.Side;
            soundRays = new NativeArray<RaycastCommand>(side * side, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            soundHits = new NativeArray<RaycastHit>(side * side, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            float reach = (side - 1) * step;
            soundTop = Mathf.Max(by1, 0f) + 150f + 0.75f * reach;
            float length = soundTop - Mathf.Min(by0, 0f) + 1500f;
            Vector3 east = (Vector3)frame.east, up = (Vector3)frame.up, north = (Vector3)frame.north, down = -up;
            Vector3 corner = (Vector3)frame.origin + east * x0 + up * soundTop + north * z0;
            // (number by number, into a list of the game's own kind that is then handed over whole: see LookAhead)
            float ex = east.x * step, ey = east.y * step, ez = east.z * step, nx = north.x * step, ny = north.y * step, nz = north.z * step;
            RaycastCommand[] list = soundList;
            for (int j = 0; j < side; j++)
            {
                float rx = corner.x + nx * j, ry = corner.y + ny * j, rz = corner.z + nz * j;
                for (int i = 0; i < side; i++) list[i + j * side] = new RaycastCommand(new Vector3(rx + ex * i, ry + ey * i, rz + ez * i), down, length, 1 << 15, 1);
            }
            NativeArray<RaycastCommand>.Copy(list, soundRays, side * side);
            soundJob = RaycastCommand.ScheduleBatch(soundRays, soundHits, 128);
            soundOut = true;
            soundX0 = x0; soundZ0 = z0; soundStep = step;
#if DEV
            soundSetMs = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000f / System.Diagnostics.Stopwatch.Frequency;
            soundWaited = time;
#endif
        }

        /// <summary>
        /// Make the lattice of soundings that are back. (On another thread: see Survey. Nothing here asks the
        /// game for anything.) Nothing, if no sounding found any ground.
        /// </summary>
        Land Chart(NativeArray<RaycastHit> hits, Land old, float x0, float z0, float step, float top, float now, bool sea, double cx, double cy, double cz, double curve)
        {
#if DEV
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            const int side = Land.Side;
            var h = new float[side * side];
            int found = 0;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    float far = hits[i + j * side].distance;
                    float y = far > 0f ? top - far : NoGround;
                    if (sea)
                    {
                        // (the sea's surface is a ball; over a stretch this small, near enough a bowl turned over)
                        double dx = x0 + i * step - cx, dz = z0 + j * step - cz;
                        float level = (float)(cy - (dx * dx + dz * dz) * curve);
                        if (y < level) y = level;
                    }
                    if (y > NoGround) found++;
                    h[i + j * side] = y;
                }
#if DEV
            soundMissed = side * side - found;
#endif
            if (found == 0) return null;
            if (found < side * side) Fill(h);
            Specks(h, step);

            var fresh = new Land(h, x0, z0, step) { made = now };
            if (old != null)
            {
                // It starts from the ground as it has been taken to be, and goes over to this (see Survey).
                var from = new float[side * side];
                bool differs = false;
                for (int j = 0; j < side; j++)
                    for (int i = 0; i < side; i++)
                    {
                        float x = x0 + i * step, z = z0 + j * step, here = h[i + j * side];
                        float was = old.Covers(x, z) ? Height(old.h, old.x0, old.z0, old.inv, x, z) : here;
                        if (was - here > 0.02f || here - was > 0.02f) differs = true;
                        from[i + j * side] = was;
                    }
                if (differs) fresh = new Land(from, x0, z0, step) { made = now, to = h, left = 1.5f };
            }
#if DEV
            soundReadMs = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000f / System.Diagnostics.Stopwatch.Frequency;
#endif
            return fresh;
        }

        /// <summary>Where a sounding found nothing, take the nearest in its row that did; and for a row with none at all, the nearest in each column.</summary>
        static void Fill(float[] h)
        {
            const int side = Land.Side;
            for (int pass = 0; pass < 2; pass++)
            {
                int along = pass == 0 ? 1 : side, across = pass == 0 ? side : 1;
                for (int j = 0; j < side; j++)
                {
                    int row = j * across;
                    float last = NoGround;
                    for (int i = 0; i < side; i++) { float v = h[row + i * along]; if (v > NoGround) last = v; else h[row + i * along] = last; }
                    last = NoGround;
                    for (int i = side - 1; i >= 0; i--) { float v = h[row + i * along]; if (v > NoGround) last = v; else h[row + i * along] = last; }
                }
            }
        }

        /// <summary>
        /// A sounding that came down on something thin standing far above everything round it (a flagpole, a
        /// mast, a lamp post) is brought down to its neighbours: as one point of the lattice it would stand
        /// for a hill as wide as the lattice's steps, and lift the smoke over it. The edge of a roof or the
        /// top of a wall is not thin: it has neighbours as high as itself.
        /// </summary>
        void Specks(float[] h, float step)
        {
            const int side = Land.Side;
            float stands = 1f + 1.5f * step;
            int n = 0;
            for (int j = 1; j < side - 1 && n < speck.Length; j++)
                for (int i = 1; i < side - 1 && n < speck.Length; i++)
                {
                    int at = i + j * side;
                    float here = h[at];
                    if (here - 0.25f * (h[at - 1] + h[at + 1] + h[at - side] + h[at + side]) < stands) continue;
                    // The third highest of the eight round it.
                    float first = float.MinValue, second = float.MinValue, third = float.MinValue;
                    for (int k = 0; k < 9; k++)
                    {
                        if (k == 4) continue;
                        float v = h[at + (k % 3 - 1) + (k / 3 - 1) * side];
                        if (v > first) { third = second; second = first; first = v; }
                        else if (v > second) { third = second; second = v; }
                        else if (v > third) third = v;
                    }
                    if (here > third + stands) { speck[n] = at; speckTo[n] = third; n++; }
                }
            for (int k = 0; k < n; k++) h[speck[k]] = speckTo[k];
#if DEV
            soundSpecks = n;
#endif
        }

        void EndSurvey()
        {
            if (soundOut) soundJob.Complete();
            if (charting != null) { try { charting.Wait(); } catch (Exception) { } }
            if (soundOut || charting != null) { soundRays.Dispose(); soundHits.Dispose(); }
            soundOut = false;
            charting = null;
        }

        // ---- the ground under the grid being made (see Deposit)
        static readonly int[] FloorsAt = { 0, G2, G2 + G2 / 4, G2 + G2 / 4 + G2 / 16 };       // where each coarser grid's begin in the list
        readonly float[] floors = new float[G2 + G2 / 4 + G2 / 16 + G2 / 64];
        Land lightLand;

        /// <summary>The height of the ground under the middle of each column of the grid in hand, and of each column of the coarser grids the widest puffs are put on.</summary>
        void Floors()
        {
            float[] f = floors;
            Land l = lightLand;
            if (l != null)
            {
                float[] h = l.h;
                float lx0 = l.x0, lz0 = l.z0, inv = l.inv;
                for (int k = 0; k < G; k++)
                {
                    float z = nz0 + (k + 0.5f) * dZ;
                    for (int i = 0; i < G; i++) f[i + k * G] = Height(h, lx0, lz0, inv, nx0 + (i + 0.5f) * dX, z);
                }
            }
            else
            {
                for (int k = 0; k < G; k++)
                {
                    float z = nz0 + (k + 0.5f) * dZ;
                    for (int i = 0; i < G; i++) f[i + k * G] = lightGroundY - (nx0 + (i + 0.5f) * dX) * lightGroundSX - z * lightGroundSZ;
                }
            }
            for (int level = 1; level < Levels; level++)
            {
                int side = G >> level, at = FloorsAt[level], under = FloorsAt[level - 1], twice = side * 2;
                for (int k = 0; k < side; k++)
                    for (int i = 0; i < side; i++)
                    {
                        int below = under + 2 * i + 2 * k * twice;
                        f[at + i + k * side] = 0.25f * (f[below] + f[below + 1] + f[below + twice] + f[below + twice + 1]);
                    }
            }
        }

#if DEV
        /// <summary>For the development build: the lattice in use, and what the last sounding took.</summary>
        string DescribeGround()
        {
            Land l = land;
            if (l == null) return hasGround ? "a plane at " + groundY.ToString("F1") + " (no sounding yet)" : "none";
            float low = float.MaxValue, high = float.MinValue;
            foreach (float v in l.h) { if (v < low) low = v; if (v > high) high = v; }
            return "sounded every " + l.step.ToString("F1") + " m from " + l.x0.ToString("F1") + ", " + l.z0.ToString("F1") + " (" + ((Land.Side - 1) * l.step).ToString("F0") + " m each way), heights " + low.ToString("F1") + " to " + high.ToString("F1") +
                   ", under the middle " + GroundAt(0f, 0f).ToString("F2") + (l.to != null ? ", going over to a new sounding" : "") + "; " + soundings + " soundings, the last " + soundMissed + " found nothing, " + soundSpecks + " specks, set up in " +
                   soundSetMs.ToString("F2") + " ms, made into a lattice (on another thread) in " + soundReadMs.ToString("F2") + " ms, in use after " + (soundWaited * 1000f).ToString("F0") + " ms; queries hit triggers: " + Physics.queriesHitTriggers + "; looking ahead, " + lookParts;
        }

        /// <summary>For the development build: how far the lattice in use is from what a careful sounding of each of its points gives (one that passes over triggers), and the heights along a line through the middle.</summary>
        public string CheckGround()
        {
            Land l = land;
            if (l == null) return "no lattice";
            const int side = Land.Side;
            Vector3 up = (Vector3)frame.up;
            int differ = 0, none = 0;
            float worst = 0f;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    float x = l.x0 + i * l.step, z = l.z0 + j * l.step;
                    Vector3 above = (Vector3)frame.ToWorld(new Vector3(x, soundTop, z));
                    if (!Physics.Raycast(above, -up, out RaycastHit hit, 5000f, 1 << 15, QueryTriggerInteraction.Ignore)) { none++; continue; }
                    float off = Mathf.Abs(soundTop - hit.distance - (l.to != null ? l.to[i + j * side] : l.h[i + j * side]));
                    if (off > 0.05f) differ++;
                    if (off > worst) worst = off;
                }
            var line = new System.Text.StringBuilder();
            for (int i = 0; i < side; i += 2) line.Append(l.h[i + (side / 2) * side].ToString("F1")).Append(' ');
            line.Append("| along the other axis: ");
            for (int j = 0; j < side; j += 2) line.Append(l.h[side / 2 + j * side].ToString("F1")).Append(' ');
            return DescribeGround() + " | against a careful sounding: " + differ + " of " + (side * side) + " points differ by more than 5 cm (the worst by " + worst.ToString("F2") + " m), " + none + " have no ground | along the first axis through the middle of the lattice: " + line;
        }
#endif
    }
}
