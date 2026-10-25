using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace VolumetricExplosions
{
    /// <summary>The second half of a Site: what an explosion puts into the air, and the things that keep feeding it afterwards.</summary>
    public sealed partial class Site
    {
        // ================================================================== adding particles

        /// <summary>
        /// How much wider a puff gets for each metre a blast carries it (the metres added up the way the mover adds
        /// them), where the air is thin: so that by the time that air has stopped it, it is as much wider as it has
        /// been carried further than thick air would have let it go (see Wide). It holds no more smoke for that.
        /// </summary>
        float Swell(Vector3 way, float speed, float kloss, float r)
        {
            float wide = Wide;
            float sum = Mathf.Abs(way.x) + Mathf.Abs(way.y) + Mathf.Abs(way.z);
            return wide > 1.001f && speed > 0.01f && sum > 0.01f ? (wide - 1f) * r * kloss / (sum * speed * wide) : 0f;
        }

        /// <summary>
        /// Room made at once for so many more particles (a blast's, known before it lays them). Add grows the lists a doubling at
        /// a time, and a big tank's blast had them copied and made again up to three times over in the middle of its frame
        /// (4096 to 30000 particles; read on the PC, 2026-10-24): once here, to the size it will need.
        /// </summary>
        void Room(int more)
        {
            int want = Math.Min(Settings.MaxParticles, count + more);
            if (want <= p.Length) return;
            int size = p.Length;
            while (size < want) size *= 2;
            size = Math.Min(Settings.MaxParticles, size);
            Array.Resize(ref p, size);
            Array.Resize(ref draw, size);
        }

        int Add()
        {
            if (Air.Alive >= Settings.MaxParticles) return -1;
            if (count >= p.Length)
            {
                int size = Math.Min(Settings.MaxParticles, p.Length * 2);
                if (size <= p.Length) return -1;
                Array.Resize(ref p, size);
                Array.Resize(ref draw, size);
            }
            Air.Alive++;
            p[count] = default;
            p[count].lit = 1f;
            p[count].sky = 1f;
            p[count].seed = (LuckBits() & 0x7FFFFFFEu) | 1u;
            p[count].roll = Luck(0f, 360f);
            p[count].spin = Luck(-22f, 22f);
            return count++;
        }

        /// <summary>
        /// (Born, for gas laid this moment at a nozzle in the patch that goes along with its ship: nothing has carried it yet, so
        /// its billows begin where it is, and its round is the one the patch's new smoke takes. Looked up in the grid round it,
        /// as other new smoke's is, the billows cost more than all the rest of laying it, for what the gas streaming through
        /// replaces within the frame. `turn`, `pace` and `octave` as Fresh gives them, once for each nozzle.)
        /// </summary>
        void BornFresh(int i, float turn, float pace, int octave)
        {
            p[i].born = p[i].r;
            p[i].turn = turn; p[i].pace = pace; p[i].octave = octave; p[i].rate = Settings.TestTurns >= 1f ? 1f / UsualRound : PerRound[octave];
            float x = p[i].x, y = p[i].y, z = p[i].z;
            p[i].ax = x; p[i].ay = y; p[i].az = z;
            p[i].bx = x; p[i].by = y; p[i].bz = z;
            p[i].ex = x; p[i].ey = y; p[i].ez = z;
            p[i].cx = x; p[i].cy = y; p[i].cz = z;
            p[i].dx = x; p[i].dy = y; p[i].dz = z;
            p[i].fx = x; p[i].fy = y; p[i].fz = z;
            draw[i] = new ParticleSystem.Particle
            {
                position = new Vector3(x, y, z),
                startLifetime = 1000f,
                remainingLifetime = 1000f * (1f - (p[i].tile + 0.5f) / (Assets.Tiles * Assets.Tiles)),
                randomSeed = p[i].seed,
                startSize = 0f,
                startColor = new Color32(0, 0, 0, 0),
            };
        }

        /// <summary>(The round new gas takes where nothing has carried it yet: as RestAt has it before it looks into the grid.)</summary>
        void Fresh(out float turn, out float pace, out int octave)
        {
            bool oneRound = Settings.TestTurns >= 1f;
            pace = time - lastBlast < 1.5f && !oneRound ? 1f / QuickestRound : 0.4f;
            octave = OctaveFor(pace);
            turn = time * (oneRound ? 1f / UsualRound : PerRound[octave]);
            turn -= Mathf.Floor(turn);
        }

        /// <summary>Give the particle its place in the list that is drawn. The tile it shows is picked through its "age".</summary>
        void Born(int i)
        {
            p[i].born = p[i].r;
            // It shows the same billows as the smoke it is born into (see Afresh).
            float x = p[i].x, y = p[i].y, z = p[i].z;
            RestAt(x, y, z);
            p[i].turn = restTurn; p[i].pace = restPace; p[i].octave = restOctave; p[i].rate = Settings.TestTurns >= 1f ? 1f / UsualRound : PerRound[restOctave];
            Vector3[] there = restThere;
            p[i].ax = x + there[0].x; p[i].ay = y + there[0].y; p[i].az = z + there[0].z;
            p[i].bx = x + there[1].x; p[i].by = y + there[1].y; p[i].bz = z + there[1].z;
            p[i].ex = x + there[2].x; p[i].ey = y + there[2].y; p[i].ez = z + there[2].z;
            p[i].cx = x + there[3].x; p[i].cy = y + there[3].y; p[i].cz = z + there[3].z;
            p[i].dx = x + there[4].x; p[i].dy = y + there[4].y; p[i].dz = z + there[4].z;
            p[i].fx = x + there[5].x; p[i].fy = y + there[5].y; p[i].fz = z + there[5].z;
            var d = new ParticleSystem.Particle
            {
                position = new Vector3(p[i].x, p[i].y, p[i].z),
                startLifetime = 1000f,
                remainingLifetime = 1000f * (1f - (p[i].tile + 0.5f) / (Assets.Tiles * Assets.Tiles)),
                randomSeed = p[i].seed,
                startSize = 0f,
                startColor = new Color32(0, 0, 0, 0),
            };
            draw[i] = d;
        }

        // With the volume drawing the smoke, the few particles left as sprites (strays outside its box) are plain soft dots.
        static byte Lumpy() => (byte)(Assets.Volume != null ? 15 : Random.Range(0, 8));
        static byte Wispy() => (byte)(Assets.Volume != null ? 15 : Random.Range(8, 12));
        static byte Specks() => (byte)(Assets.Volume != null ? 15 : Random.Range(12, 15));

        // ================================================================== an explosion

        /// <summary>
        /// An explosion for this patch of air. Called at the end of the frame it happened in, while the
        /// scene's coordinates are still the ones the blast was reported in: everything is restated in this
        /// Site's own frame here. The particles are set off at the start of the next frame, between two
        /// rounds of moving them.
        /// </summary>
        public void Explode(Plan pl)
        {
            frame.Refresh();
            pl.at = frame.ToLocal(pl.world);
            // What the wreck was doing: over the ground for a cloud in air, against this patch's own flight in space.
            Vector3d ours = space ? frame.velocity - body.getRFrmVel(pl.world) : Vector3d.zero;
            pl.going = frame.DirToLocal(pl.velocity - ours);
            if (pl.surfaceBelow && !space && !hasGround) Ground(pl.surfacePoint, pl.surfaceNormal);
            // (the ground is to be known as far as this throws things, before they get there: see Survey)
            expect = Mathf.Max(time - lastBlast < 1f ? expect : 0f, Mathf.Max(Mathf.Abs(pl.at.x), Mathf.Abs(pl.at.z)) + 2.2f * Mathf.Max(pl.radius, 0.8f * pl.reach) + 4f);
            if (pl.wreck != null)
                foreach (Death d in pl.wreck)
                {
                    d.at = frame.ToLocal(d.world);
                    d.going = frame.DirToLocal(d.velocity - ours);
                    foreach (Shell shell in d.shells) shell.local = frame.ToLocal(shell.toWorld);
                }
            if (pl.ground && hasGround && !pl.water && (pl.radius > 1f || pl.impact > 25f)) Air.Instance.Mark(pl, Mathf.Max(1.5f, pl.radius * 1.25f + pl.reach * 0.2f));
            waiting.Add(pl);
            lastBlast = time; blasted = true;
        }

        void Burst(Plan pl)
        {
            Vector3 c = pl.at, v0 = pl.going;
            float q = Settings.Quality * Air.Room();
            float R = pl.radius;
            lastBlast = time; blasted = true;
            if (time - fireballAt > 3f || R > fireballR) { fireballR = R; fireballAt = time; }
            if (R > scale || count < 50)
            {
                scale = Mathf.Max(2f, Mathf.Max(R, pl.reach * 0.6f));
                // The size of the billows, and what counts as thick smoke, go with the size of the blast. They are set
                // when a cloud starts (or a much bigger blast takes it over) and then left alone.
                if (count < 50 || scale * 0.55f + 3.5f > detailRepeat * 1.5f)
                {
                    detailRepeat = Mathf.Clamp(scale * 0.55f + 3.5f, 4f, 26f);
                    fullLength = Mathf.Clamp(0.13f * scale, 0.8f, 4f);
                    seaBorn = pl.water && (pl.ground || pl.underwater);
                }
            }
            bool onGround = pl.ground && hasGround;
            if (onGround) c.y = Mathf.Max(c.y, GroundAt(c.x, c.z) + 0.3f);
            // On a slope, "up out of the ground" is not straight up: what a blast on the ground throws out goes out over
            // the ground as it lies there, up the slope and down it alike.
            blastUp = onGround ? GroundUp(c.x, c.z, Mathf.Max(2f, 0.5f * R)) : Vector3.up;
            Wreck(pl);
            if (R > 0.3f && pl.fire >= flameWeight) { flameKind = (byte)pl.tint; flameWeight = pl.fire; }      // a battery going off inside a fuel fire does not turn the fire blue
            float strongest = Mathf.Max(pl.dust.r, Mathf.Max(pl.dust.g, pl.dust.b));
            if (strongest > 0.01f) dustHue = new Vector3(pl.dust.r, pl.dust.g, pl.dust.b) / strongest;

#if DEV
            BurstLap(-1);
#endif
            if (count > 0 && !vacuum) Shove(c, Mathf.Max(R, pl.reach * 0.7f));
            StartKnock(pl, c, onGround);
#if DEV
            BurstLap(0);
#endif
            if (R > 0.3f) Fireball(pl, c, v0, R, q, onGround);
#if DEV
            BurstLap(1);
#endif
            if (pl.vapour > 0.5f) Vapour(pl, c, v0, q, onGround);
            if (pl.water && (pl.ground || pl.underwater)) { Splash(pl, c, v0, q); SeaBlow(pl, c); }
            else if (pl.dirt > 0f) Dust(pl, c, v0, q);
#if DEV
            BurstLap(2);
#endif
            Sparks(pl, c, v0, q, onGround);
#if DEV
            BurstLap(3);
#endif

            if (R > 0.3f)
            {
                float lift = !vacuum && grip > 0.2f && R > 1.2f ? 0.78f * Mathf.Sqrt(gravity * R) * Mathf.Min(1f, grip) : 0f;
                float up = onGround ? R * 0.45f : 0f;
                int lamp = AddLamp(c.x, c.y + up, c.z, R, vacuum ? 3.5f : 5f, vacuum ? 0.3f + 0.05f * R : 0.9f + 0.32f * R, (byte)pl.tint, false, R * 6.5f, lift * 0.8f);
                // The fire of something that blew up at speed flies on (see Fireball), and its light goes with it: left
                // where the blast was, it was a glowing ball hanging in the air behind a fire already far away.
                Vector3 thrown = vacuum ? v0 : v0 * Mathf.Lerp(1f, 0.75f * (0.4f + 0.6f * Mathf.Clamp01(v0.magnitude / 120f)), Mathf.Clamp01(grip));
                lamps[lamp].vx = thrown.x; lamps[lamp].vy = thrown.y; lamps[lamp].vz = thrown.z;
                if (lift > 0f) AddThermal(c.x + v0.x * 0.15f, c.y + up, c.z + v0.z * 0.15f, R * 0.72f, lift, c.y + R * Luck(6f, 10f));
#if DEV
                BurstLap(4);
#endif
                Chunks(pl, c, v0, q, onGround);
#if DEV
                BurstLap(5);
#endif
            }
            if (pl.burn > 0f && onGround) AddFire(pl, c, R, q);
#if DEV
            BurstLap(6);
#endif
            if (Settings.Shockwave && air > 0.12f && R > 2f && time - lastRing > 0.5f) Shock(c, R, onGround, pl.fire, onGround && !pl.water && !pl.paved ? pl.dirt : 0f, pl.dust);
#if DEV
            BurstLap(7);
            Bursts.Add("    burst R " + R.ToString("F1") + ", q " + q.ToString("F2") + ": shove " + BurstMs[0].ToString("F1") + ", fireball " + BurstMs[1].ToString("F1") + ", vapour/splash/dust " + BurstMs[2].ToString("F1")
                       + ", sparks " + BurstMs[3].ToString("F1") + ", lamp " + BurstMs[4].ToString("F1") + ", chunks " + BurstMs[5].ToString("F1") + ", fire " + BurstMs[6].ToString("F1") + ", shock " + BurstMs[7].ToString("F1") + " ms; " + count + " particles");
#endif
        }

#if DEV
        // (for the development build: where a burst's time goes, written beside the patch's step in Made)
        static readonly double[] BurstMs = new double[8];
        static long burstLap;
        internal static readonly List<string> Bursts = new List<string>();
        static void BurstLap(int k)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (k >= 0) BurstMs[k] = (now - burstLap) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            else for (int n = 0; n < BurstMs.Length; n++) BurstMs[n] = 0;
            burstLap = now;
        }
#endif

        /// <summary>A blast shoves aside the smoke that was already hanging there.</summary>
        void Shove(Vector3 c, float R)
        {
            float far2 = 49f * R * R, R2 = R * R;
            for (int i = 0; i < count; i++)
            {
                ref P o = ref p[i];
                if ((o.flags & Ballistic) != 0) continue;
                float dx = o.x - c.x, dy = o.y - c.y, dz = o.z - c.z, r2 = dx * dx + dy * dy + dz * dz;
                if (r2 > far2) continue;
                float r = Mathf.Sqrt(r2) + 0.05f;
                // Moved about as far as the blast is wide when right beside it, hardly at all a few widths away.
                float speed = 1.3f * R * R2 / (r2 + R2) * Mathf.Max(1f, o.kloss) / r;
                o.kx += dx * speed; o.ky += dy * speed * 0.6f; o.kz += dz * speed;
                o.turb = Mathf.Max(o.turb, Mathf.Min(8f, speed * r * 0.25f));
            }
        }

        void Fireball(Plan pl, Vector3 c, Vector3 v0, float R, float q, bool onGround)
        {
            int n = (int)(q * Mathf.Clamp(95f * Mathf.Pow(R, 1.45f), 260f, 9000f));
            // (its rind and its dust come to at most about half as many again: see Room)
            Room(n + n / 2);
            // Fewer particles are made bigger, so that the fireball stays filled in.
            float rp = Mathf.Clamp(0.105f * Mathf.Pow(R, 0.75f) + 0.1f, 0.18f, 1.6f) / Mathf.Pow(Mathf.Clamp(q, 0.08f, 3f), 0.33f);
            float burns = vacuum ? 0.25f + 0.045f * R : (0.8f + 0.30f * R) * Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(grip));      // seconds; shorter where the air is thin
            float spreads = 0.16f + 0.02f * R;                                               // seconds for the blast to spend itself in air
            float smokeLasts = (14f + 15f * Mathf.Sqrt(R)) * Settings.Smoke * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(air));
            float mixes = 0.08f + 0.012f * R;                                                // how fast a puff spreads into the air round it
            float smoky = vacuum || Settings.Smoke <= 0.01f ? 0f : Mathf.Lerp(0.25f, 0.8f, pl.soot);
            float shade = pl.smokeShade;

            // No explosion is a perfect ball: a few directions get more of it.
            var lobes = new Vector3[9];
            var more = new float[9];
            for (int m = 0; m < lobes.Length; m++)
            {
                Vector3 way = LuckSphere();
                if (onGround) way = OffGround(way, blastUp, 1f);
                lobes[m] = way;
                more[m] = Luck(0.1f, 1.0f) * Luck(0.4f, 1f);
            }
            // Something that hit at speed throws its fire on ahead of it.
            float carried = Mathf.Clamp01(v0.magnitude / 120f);
            // Where the soot shows first. A fuel fireball is luminous through and through to begin with: its soot glows (that is what
            // makes the flame yellow). The soot goes dark where the burning stops, which is where cold air gets in: all round the
            // outside, and soonest on the rising cap and in the rolls of its surface, so that a black skin closes round the fire
            // with the flame bursting through it, and the luminous core shrinks until the whole is a black cloud. So the dark comes
            // in sheets and patches, never dot by dot: how fast each bit cools follows a smooth pattern over the ball (a few broad
            // patches, fixed for the blast), not a throw of the dice for each bit. (Until 2026-10-09 it was the dice, and a tenth of
            // the smoky bits were dark from the first moment: the fireball came up speckled with black balls.)
            var patchWay = new Vector3[6];
            var patchOf = new float[6];
            for (int m = 0; m < patchWay.Length; m++) { patchWay[m] = LuckSphere(); patchOf[m] = Luck(-1f, 1f); }
            bool speckled = Settings.TestSoot >= 1f;                                   // (for testing: as it was)

            // (from 2026-10-24 a big one is laid over two of the patch's steps: see HalfLaid)
            var f = new HalfLaid { pl = pl, c = c, v0 = v0, up = blastUp, R = R, rp = rp, burns = burns, spreads = spreads, smokeLasts = smokeLasts, mixes = mixes,
                               smoky = smoky, shade = shade, carried = carried, onGround = onGround, speckled = speckled, lobes = lobes, more = more,
                               patchWay = patchWay, patchOf = patchOf, n = n, at = time };
            f.rind = smoky > 0f ? (int)(n * (speckled ? 0.3f : 0.42f) * pl.soot) : 0;
            bool split = n >= SplitFrom;
#if DEV
            if (TestWhole) split = false;
#endif
            f.next = split ? n / 2 : n;
            f.rindNext = split ? f.rind / 2 : f.rind;
            Lay(f, 0, f.next, 0, f.rindNext, 0f);
            if (split) firesLeft.Add(f);
        }

        // ---- a big fireball laid over two of the patch's steps
        // (On the PC, 2026-10-24: a fuel fireball of some 9,000 bits took 8 to 10 ms of one step to lay, nearly all of a crash's blast in
        // its frame (Made's "burst ... fireball"). Now one of SplitFrom bits or more is laid half at its blast and half at the patch's
        // next step, the frame after, each bit of that half moved on, cooled and aged by the time between (Aged), as if it had been laid
        // with the rest: what the eye gets is the same fireball a frame on, the frame's work split in two.)
        const int SplitFrom = 2400;
#if DEV
        /// <summary>(Development build: every fireball laid whole at its blast, as until 2026-10-24, to film the two ways against each other.)</summary>
        public static bool TestWhole;
        public static string Whole(string v) { TestWhole = v != "off"; return TestWhole ? "fireballs laid whole" : "big fireballs laid over two steps"; }
#endif

        sealed class HalfLaid
        {
            public Plan pl;
            public Vector3 c, v0, up;
            public float R, rp, burns, spreads, smokeLasts, mixes, smoky, shade, carried, at;
            public bool onGround, speckled;
            public Vector3[] lobes, patchWay;
            public float[] more, patchOf;
            public int n, next, rind, rindNext;
        }

        readonly List<HalfLaid> firesLeft = new List<HalfLaid>();

        /// <summary>The rest of the fireballs begun at the patch's last step, laid as if with the rest (at the start of this one, before its own blasts).</summary>
        void FiresLeft()
        {
            foreach (HalfLaid f in firesLeft)
            {
                Room((f.n - f.next) + (f.rind - f.rindNext));
                Lay(f, f.next, f.n, f.rindNext, f.rind, time - f.at);
            }
            firesLeft.Clear();
        }

        /// <summary>A bit laid so long after its blast: where it would have got to, as hot as it would still be, as old.</summary>
        void Aged(int i, float late)
        {
            ref P o = ref p[i];
            o.x += (o.vx + o.kx) * late; o.y += (o.vy + o.ky) * late; o.z += (o.vz + o.kz) * late;
            o.heat *= Mathf.Exp(-o.cool * late);
            o.age += late;
            o.life -= late;
        }

        /// <summary>Some of a fireball's bits (from, to) and of its rind's (rindFrom, rindTo), laid so long after its blast (late).</summary>
        void Lay(HalfLaid f, int from, int to, int rindFrom, int rindTo, float late)
        {
            Plan pl = f.pl;
            Vector3 c = f.c, v0 = f.v0, up = f.up;
            float R = f.R, rp = f.rp, burns = f.burns, spreads = f.spreads, smokeLasts = f.smokeLasts, mixes = f.mixes, smoky = f.smoky, shade = f.shade, carried = f.carried;
            bool onGround = f.onGround, speckled = f.speckled;
            Vector3[] lobes = f.lobes, patchWay = f.patchWay;
            float[] more = f.more, patchOf = f.patchOf;
            for (int k = from; k < to; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = LuckSphere();
                if (onGround) way = OffGround(way, up, 0.5f).normalized;
                float gain = 1f;
                for (int m = 0; m < lobes.Length; m++)
                {
                    float along = Vector3.Dot(way, lobes[m]);
                    if (along > 0f) { float a2 = along * along; gain += more[m] * a2 * a2; }
                }
                float rho = Mathf.Pow(Luck(0f, 1f), 0.4f);                                  // most of it is toward the outside
                float reach = R * (0.2f + 0.8f * rho) * gain, start = R * 0.2f * Luck(0f, 1f);
                o.x = c.x + way.x * start; o.y = c.y + way.y * start; o.z = c.z + way.z * start;
                o.r = rp * Luck(0.7f, 1.35f);
                if (vacuum)
                {
                    // Nothing stops it: it flies out, thins and is gone.
                    float speed = reach / (burns * 0.55f);
                    o.vx = v0.x + way.x * speed; o.vy = v0.y + way.y * speed; o.vz = v0.z + way.z * speed;
                    o.flags = Ballistic;
                    o.grow = speed * 0.3f;
                }
                else
                {
                    // The air stops the blast within a fraction of a second, 'reach' from where it began.
                    float speed = reach / spreads;
                    o.kx = way.x * speed; o.ky = way.y * speed; o.kz = way.z * speed;
                    // (thick air takes much of the wreck's speed out of it at once; thin air only as it goes, in the mover)
                    float keeps = Mathf.Lerp(1f, Luck(0.5f, 1f) * (0.4f + 0.6f * carried), Mathf.Clamp01(grip));
                    o.vx = v0.x * keeps; o.vy = v0.y * keeps; o.vz = v0.z * keeps;
                    o.kloss = 1f / spreads;
                    o.swell = Swell(way, speed, o.kloss, o.r);
                    o.drag = 3.5f;
                    o.grow = mixes;
                    o.turb = 0.5f + 0.1f * speed;
                    // (Lifted by its own heat as a fuel fireball is: gas at some 1500 K is a fifth as dense as the air round it, and a body of
                    // it rises at about the root of g times its radius times that, 14 m/s for one 50 m across (the mover takes this times
                    // its heat, and it eases as it cools): within a second a fireball stands up off the ground and climbs into a rolling
                    // cap, its stem of cooler smoke under it. At 0.5 of the root of R, as until 2026-10-18 (TestFire 2 or TestRise 1), it rose at a
                    // third of that and lay on the ground as a dome for its first seconds.)
                    o.rise = (Settings.TestFire == 2f || Settings.TestRise == 1f ? 0.5f : 1.4f) * Mathf.Sqrt(R);
                }
                // The middle is hottest and stays hot longest; the outside cools first into smoke, the top and the patches of the
                // pattern above soonest (see patchWay).
                float patch = speckled ? 0f : Patch(way, patchWay, patchOf);
                float cap = speckled ? 0f : Mathf.Max(0f, Vector3.Dot(way, up));
                // (The outside not at once: filmed frame by frame, a large fuel fireball is luminous all through for its first second, a
                // bright yellow-white edge, and only in its second does soot build up, a tenth of its surface dark by the end of it, as
                // its colour goes over to red (Crawley 1982, an 80-tonne fireball). So here the outside goes dark from about 0.8 s on
                // where the pattern has it cool first, 1.2 s on the whole, and later where it stays hot. Until 2026-10-09 the outermost
                // bits were dark within a quarter of a second.)
                o.heat = speckled ? 1.12f - 0.42f * rho + 0.12f * (gain - 1f) + Luck(-0.08f, 0.08f)
                                  : 1.12f - 0.26f * rho + 0.12f * (gain - 1f) + 0.05f * patch + Luck(-0.02f, 0.02f);
                // In air the fire cools into smoke. With no air it stays bright and simply thins away as it flies apart.
                o.cool = vacuum ? 0.7f / burns : speckled ? (1.3f + 2.2f * rho * rho) / burns * Luck(0.8f, 1.25f)
                                                          : (1.3f + 1.6f * rho * rho) / burns * (1f - 0.2f * patch + 0.2f * cap * rho) * Luck(0.95f, 1.05f);
                o.flame = Luck(0.75f, 1.15f);
                o.tint = (byte)pl.tint;
                o.tile = Lumpy();
                if (Luck(0f, 1f) < smoky)
                {
                    float later = o.r * 2.2f;                                               // about the size it has grown to when the smoke shows
                    o.mass = Luck(0.6f, 2.2f) * Mathf.Pow(Mathf.Max(0.05f, pl.soot), 0.7f) * later * later;
                    o.life = smokeLasts * Luck(0.45f, 1.15f);
                    if (!speckled)
                    {
                        // (The same soot spread wider: half as wide again, so a third as thick, and each bit's smoke runs into its
                        // neighbours'. As wide as it was, each bit that went dark was a dense ball with clear air or flame between it
                        // and the next: a fireball spotted with dark balls, where real soot comes as a sheet. Its flame no brighter
                        // for being wider.)
                        o.r *= 1.5f;
                        o.flame *= 0.67f;
                    }
                    // (Until 2026-10-09 some of it was soot from the first moment, meant for the dark blotches that roll about in a
                    // fuel fire: each was a black ball on the fireball from its first frame. The blotches come from the pattern now.)
                    if (speckled && Luck(0f, 1f) < 0.08f * pl.soot) { o.heat *= 0.42f; o.cool *= 0.6f; }
                    float tone = shade * Luck(0.7f, 1.5f);
                    o.ar = tone; o.ag = tone * 0.97f; o.ab = tone * 0.93f;
                }
                else o.life = vacuum ? burns * Luck(1.6f, 3f) : 4.2f / o.cool;     // flame only: gone when it is out (or, with no air, when it has thinned to nothing)
                if (late > 0f) Aged(i, late);
                Born(i);
            }

            // The dark rind a fuel fire has: smoke round the outside. (It used to be there from the first moment, cool, coming into
            // view at once: a shell of separate dark balls, a tenth seen a quarter of a second in, all over the fire. Now it is hot gas
            // at first, its soot glowing unseen, which turns to smoke as it cools (see Deposit): from about a third of the way through
            // the burning, soonest on top and where the pattern has the fire cool first, and over as long again; so it closes round
            // the fire as a skin. More of it, and smaller, for that.)
            for (int k = rindFrom; k < rindTo; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = LuckSphere();
                if (onGround) way = OffGround(way, up, 0.5f).normalized;
                float reach = R * Luck(0.8f, 1.25f), speed = reach / spreads;
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.kx = way.x * speed; o.ky = way.y * speed; o.kz = way.z * speed;
                float kept = Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(grip));
                o.vx = v0.x * kept; o.vy = v0.y * kept; o.vz = v0.z * kept;
                o.kloss = 1f / spreads;
                o.drag = 3.5f;
                o.r = rp * (speckled ? Luck(1.2f, 2.2f) : Luck(0.9f, 1.5f));
                o.swell = Swell(way, speed, o.kloss, o.r);
                o.grow = mixes;
                o.turb = 0.5f + 0.1f * speed;
                o.rise = (Settings.TestFire == 2f || Settings.TestRise == 1f ? 0.3f : 0.8f) * Mathf.Sqrt(R);      // (the rind rises with the fireball it wraps: see above)
                o.mass = Luck(0.8f, 2.2f) * pl.soot * o.r * o.r * 2.5f;
                o.life = smokeLasts * Luck(0.4f, 1.1f);
                if (speckled) o.fadeIn = burns * Luck(0.45f, 0.9f);
                else
                {
                    // (from hot to the start of smoke, a third of the burning or so, and as long again to full smoke: cooling as it does,
                    // by a share of its heat a second, that is heat 1.46 and 3.6 / burns a second, whatever the moment)
                    float shows = burns * Mathf.Clamp(0.32f + 0.12f * Patch(way, patchWay, patchOf) - 0.1f * Mathf.Max(0f, Vector3.Dot(way, up)) + Luck(-0.03f, 0.03f), 0.12f, 0.55f);
                    o.cool = 3.6f / burns;
                    o.heat = 0.5f * Mathf.Exp(o.cool * shows);
                    o.fadeIn = 0.1f;
                }
                float tone = shade * Luck(0.8f, 1.5f);
                o.ar = tone; o.ag = tone * 0.97f; o.ab = tone * 0.93f;
                o.tile = Lumpy();
                if (late > 0f) Aged(i, late);
                Born(i);
            }
        }

        /// <summary>
        /// A smooth pattern over the fireball, -1 to 1 (about), by direction from its middle: a few broad patches, each a third of
        /// the way round or so. Neighbouring bits of the fire get much the same, so what follows from it (when the soot shows)
        /// comes in patches.
        /// </summary>
        static float Patch(Vector3 way, Vector3[] at, float[] of)
        {
            float sum = 0f, weight = 0f;
            for (int m = 0; m < at.Length; m++)
            {
                float near = Mathf.Exp(3.2f * (Vector3.Dot(way, at[m]) - 1f));
                sum += of[m] * near; weight += near;
            }
            return weight > 1e-4f ? Mathf.Clamp(1.6f * sum / weight, -1f, 1f) : 0f;
        }

        /// <summary>Something that does not burn here: fuel with no oxygen to burn in, spilled oxidiser, a burst gas tank. A cold white cloud.</summary>
        void Vapour(Plan pl, Vector3 c, Vector3 v0, float q, bool onGround)
        {
            float reach = Mathf.Min(30f, Settings.Size * 1.3f * Mathf.Pow(pl.vapour, 0.3f));
            int n = (int)(q * Mathf.Clamp(22f * Mathf.Sqrt(pl.vapour), 30f, 1500f));
            for (int k = 0; k < n; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = LuckSphere();
                if (onGround) way = OffGround(way, blastUp, 0.4f);
                float far = reach * Luck(0.2f, 1.1f);
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.r = (0.25f + 0.05f * reach) * Luck(0.7f, 1.4f);
                if (vacuum)
                {
                    // (In a vacuum what is let out boils at once where it is liquid, and the boiling freezes the rest: a cloud of fine
                    // drops and crystals flies out from the burst in straight lines, never billowing, thinning as it spreads, bright
                    // where the sun is on it. Vented propellant in orbit stays in sight for many seconds (a Shuttle's water dump was
                    // seen 2.5 km long, Pike 1990); until 0.8.0 this cloud was gone in a second or two.)
                    float speed = far / 0.35f;
                    o.vx = v0.x + way.x * speed; o.vy = v0.y + way.y * speed; o.vz = v0.z + way.z * speed;
                    o.flags = Ballistic;
                    o.grow = speed * 0.45f;
                }
                else
                {
                    float speed = far / 0.3f;
                    o.kx = way.x * speed; o.ky = way.y * speed; o.kz = way.z * speed;
                    float kept = Mathf.Lerp(1f, 0.6f, Mathf.Clamp01(grip));
                    o.vx = v0.x * kept; o.vy = v0.y * kept; o.vz = v0.z * kept;
                    o.kloss = 3.3f; o.drag = 3f;
                    o.swell = Swell(way, speed, o.kloss, o.r);
                    o.grow = 0.5f;
                    o.turb = 0.6f + 0.08f * speed;
                    o.rise = -0.3f;                                                        // cold and heavy: it sinks and spreads
                    o.life = Luck(2.5f, 7f) * Mathf.Max(0.3f, Settings.Smoke);
                }
                o.mass = Luck(0.4f, 1.0f) * o.r * o.r * 4f;
                if (vacuum)
                {
                    // (How thick the cloud is, and so how long it is seen: what flashes off a liquid in a vacuum freezes, some two
                    // thirds of it, into grains of the order of fifty micrometres, each hiding as much as its own cross-section:
                    // 3 / (4 x 800 kg/m3 x 25 um), 37.5 m2 for each kilogram, and through the middle of a cloud R across a column of
                    // 3A / (2 pi R2). The shading takes each puff as hiding its mass over its radius squared, and the puffs a line
                    // crosses add up to all of them over the cloud's radius squared: 18 for each kilogram, all told, shared out among
                    // the puffs. So a tonne let go is a white ball, solid for its first seconds, thinning as it spreads with the
                    // square of its size, and gone when its column is a fiftieth: some twenty seconds for a tonne, forty for ten.
                    // Until 2026-10-19 it was a sixth as thick and lived three to eight seconds: a haze gone almost at once.)
                    float all = 18f * 0.66f * pl.vapour;
                    o.mass = all / Mathf.Max(1, n) * Luck(0.6f, 1.4f);
                    float fastest = reach * 1.1f / 0.35f;
                    o.life = Mathf.Clamp(Mathf.Sqrt(all / 0.02f) / Mathf.Max(fastest, 1f) * Luck(0.8f, 1.2f), 3f, 90f);
                }
                // (ice crystals in sunlight are near white; a cold cloud in air a little greyer)
                float tone = vacuum ? Luck(0.88f, 1f) : Luck(0.7f, 0.9f);
                o.ar = tone; o.ag = tone; o.ab = tone * 1.04f;
                o.tile = Luck(0f, 1f) < 0.5f ? Wispy() : Lumpy();
                Born(i);
            }
        }

        /// <summary>Dirt thrown out along the ground and up. In air it billows and hangs; with no air it flies in clean arcs and drops.</summary>
        void Dust(Plan pl, Vector3 c, Vector3 v0, float q)
        {
            float reach = pl.reach;
            // (A fuel blast is a burning, not a detonation: it throws up less of the ground than the size of its fireball would say,
            // and what it does throw up is darkened by its soot. With all of it the ground's dust, as until 2026-10-18, a fuel crash
            // on sand was a dome of tan dust lit orange by the fire inside it, and hid the fire.)
            float fiery = Settings.TestFire == 2f ? 0f : Mathf.Clamp01(pl.radius / Mathf.Max(pl.reach, 0.1f));
            float sooted = 0.7f * fiery * pl.soot;
            int n = (int)(q * pl.dirt * Mathf.Lerp(1f, 0.4f, fiery) * Mathf.Clamp(35f * Mathf.Pow(reach, 1.5f), 40f, 2500f));
            // "Along the ground" and "up" are as the ground lies under the blast: on a slope the low skirt of dust goes out up the slope and down it.
            Quaternion lay = Quaternion.FromToRotation(Vector3.up, blastUp);
            Vector3 ahead = new Vector3(v0.x, 0f, v0.z);
            float thrown = Mathf.Clamp01(ahead.magnitude / 60f);
            ahead = ahead.sqrMagnitude > 1f ? lay * ahead.normalized : Vector3.zero;
            for (int k = 0; k < n; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float turn = Luck(0f, Mathf.PI * 2f), tilt = Luck(0f, 1f);
                tilt = tilt * tilt * 1.2f;                                                  // most of it low, a little straight up
                Vector3 way = lay * new Vector3(Mathf.Cos(turn) * Mathf.Cos(tilt), Mathf.Sin(tilt), Mathf.Sin(turn) * Mathf.Cos(tilt));
                way = (way + ahead * (0.7f * thrown)).normalized;                           // an object that came in at a slant throws it on ahead
                float far = reach * Luck(0.5f, 2.3f);
                o.x = c.x + way.x * 0.3f * reach * Luck(0f, 1f); o.z = c.z + way.z * 0.3f * reach * Luck(0f, 1f);
                o.y = GroundAt(o.x, o.z) + 0.2f;
                float tone = Luck(0.75f, 1.15f);
                o.ar = Mathf.Lerp(pl.dust.r, pl.smokeShade, sooted) * tone; o.ag = Mathf.Lerp(pl.dust.g, pl.smokeShade, sooted) * tone; o.ab = Mathf.Lerp(pl.dust.b, pl.smokeShade, sooted) * tone;
                if (vacuum)
                {
                    // Speed for a throw of that length at that angle.
                    float angle = Mathf.Clamp(tilt, 0.26f, 1.3f);
                    float speed = Mathf.Sqrt(Mathf.Max(0.5f, gravity) * far / Mathf.Sin(2f * angle));
                    way = lay * new Vector3(Mathf.Cos(turn) * Mathf.Cos(angle), Mathf.Sin(angle), Mathf.Sin(turn) * Mathf.Cos(angle));
                    o.vx = way.x * speed; o.vy = way.y * speed; o.vz = way.z * speed;
                    o.flags = Ballistic | DiesOnGround;
                    o.r = (0.2f + 0.04f * reach) * Luck(0.6f, 1.3f);
                    o.grow = 0.08f * speed;
                    o.mass = Luck(0.15f, 0.45f) * o.r * o.r * 6f;
                    o.life = 60f;
                    o.tile = Luck(0f, 1f) < 0.4f ? Specks() : Lumpy();
                }
                else
                {
                    float speed = far / 0.3f;
                    o.kx = way.x * speed; o.ky = way.y * speed; o.kz = way.z * speed;
                    o.kloss = 3.3f; o.drag = 3f;
                    o.r = (0.3f + 0.06f * reach) * Luck(0.6f, 1.4f);
                    o.grow = 0.15f;
                    o.turb = 0.5f + 0.08f * speed;
                    o.rise = -Luck(0.1f, 0.7f);                                     // it settles
                    o.mass = Luck(0.5f, 1.5f) * o.r * o.r * 4f;
                    if (Settings.TestSoot < 1f)
                    {
                        // (The same dust spread half as wide again, so a third as thick, and raised over a moment rather than there at
                        // once: each bit of it was a dense dark ball two metres across, and those thrown up flew out in front of the fire,
                        // with clear air between them: the rest of the "blobs" on a fireball in its first second. It runs together into
                        // a skirt of dust now.)
                        o.r *= 1.5f;
                        o.fadeIn = Luck(0.12f, 0.3f);
                    }
                    o.swell = Swell(way, speed, o.kloss, o.r);
                    o.life = Luck(6f, 18f) * Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(air));
                    o.tile = Luck(0f, 1f) < 0.25f ? Wispy() : Lumpy();
                }
                Born(i);
            }
        }

        // (since 0.8.0: the water itself, where Natural Ocean draws it: see SeaBlow)
        static System.Reflection.MethodInfo seaStruck;
        static bool seaLooked;

        /// <summary>
        /// (Since 0.8.0.) A blast on or in the sea dents the water itself where Natural Ocean draws it: the crater it digs, its walls
        /// falling back, the rings it sends out and the white water it leaves, as that mod does for a craft striking the sea
        /// (NaturalOcean.Spray.Struck, found by name: nothing of it is needed). The blow it is given: the blast's charge in TNT
        /// (4.2 MJ a kilogram), all of it for one in the water, as a blow straight down; for a fireball over the water a
        /// twentieth, the push of its front reflected off the water (the most of a burst in the air goes into the air). (Filmed
        /// 2026-10-18 from 160 m: a hump that seemed to rise over the fireball in its first half second was the shock front's
        /// bending of the view behind it, the same with the water given a fiftieth; the water's own dent not told apart there.)
        /// </summary>
        void SeaBlow(Plan pl, Vector3 c)
        {
            // (Never the warm-up's: its crash, set off out of sight 20 km and more ahead at every flight's start (Air.WarmUp), lands
            // out at sea from the pad, and the sea's dent of it cost the game's thread 25 ms a frame for the 50 s it lasts, the
            // first minute of every flight (found 2026-10-24: "a craft struck the sea: cavity 14.4 m ... from 1679.8 MJ" in the
            // game's log after every load). Marked on its plan as it is taken in: its burst, and this with it, comes a frame or
            // more later, when Air.WarmingNow no longer holds.)
            if (Settings.TestRing == 1f || pl.warm) return;
            if (!seaLooked)
            {
                seaLooked = true;
                foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                {
                    Type t = a.assembly != null ? a.assembly.GetType("NaturalOcean.Spray", false) : null;
                    if (t == null) continue;
                    seaStruck = t.GetMethod("Struck", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null,
                        new[] { typeof(CelestialBody), typeof(Vector3d), typeof(float), typeof(float), typeof(Vector3), typeof(float) }, null);
                    if (seaStruck != null) break;
                }
            }
            if (seaStruck == null || body == null) return;
            float tnt = Mathf.Max(0.1f * Mathf.Max(pl.charge, pl.fire), 0.5f);
            float energy = tnt * 4.2e6f * (pl.underwater || pl.drowned > 0.3f ? 1f : 0.05f);
            const float Speed = 60f;
            float tonnes = energy / (0.5f * 1000f * Speed * Speed);
            Vector3d world = frame.ToWorld(c);
            try { seaStruck.Invoke(null, new object[] { body, world, tonnes, Speed, -(Vector3)frame.up * Speed, 0f }); }
            catch (Exception) { seaStruck = null; }
        }

        /// <summary>On water: a column of spray straight up, a crown thrown outward, and mist left drifting.</summary>
        /// <remarks>(2026-10-17) Sized by what went off, not by the pieces: a blast under the water had its fire put out, and
        /// with it its size, and threw up a mound of a few metres where the same charge in the air was a fireball of
        /// twenty. As much water as the fireball would have been wide, and under the water the column is most of it: a
        /// shallow blast's gas comes up through the surface as a dome and bursts through it as a column of spray, tall and
        /// white (Cole, Underwater Explosions; footage of mines and depth charges), with no fire; at the surface the same
        /// column stands over the fireball.</remarks>
        void Splash(Plan pl, Vector3 c, Vector3 v0, float q)
        {
            float level = hasGround ? GroundAt(c.x, c.z) : c.y;
            float depth = Mathf.Max(0f, level - c.y);
            if (depth > 30f) return;                                                        // too deep to break the surface
            float charged = pl.charge > 0.5f ? Settings.Size * 1.75f * Mathf.Pow(pl.charge, 0.29f) : 0f;
            float reach = Mathf.Max(Mathf.Max(pl.reach, pl.radius), Mathf.Min(charged, 45f)) * (1f - depth / 40f);
            bool under = pl.underwater || pl.drowned > 0.3f;
            int n = (int)(q * Mathf.Clamp(45f * Mathf.Pow(reach, 1.5f), 60f, 3000f));
            float g = Mathf.Max(0.5f, gravity);
            for (int k = 0; k < n; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float turn = Luck(0f, Mathf.PI * 2f), kind = Luck(0f, 1f);
                float tone = Luck(0.8f, 0.95f);
                o.ar = tone; o.ag = tone; o.ab = tone;
                o.x = c.x + Mathf.Cos(turn) * reach * 0.25f * Luck(0f, 1f); o.z = c.z + Mathf.Sin(turn) * reach * 0.25f * Luck(0f, 1f);
                o.y = level + 0.2f;
                if (kind < 0.8f)
                {
                    // Water: thrown, and it falls back. (Under the water most of it goes up the column, and higher.)
                    bool column = kind < (under ? 0.6f : 0.35f);
                    float tilt = column ? Luck(1.25f, 1.55f) : Luck(0.7f, 1.2f);
                    float high = reach * (column ? Luck(1.5f, under ? 5.5f : 4.5f) : Luck(0.5f, 1.6f));
                    float speed = Mathf.Sqrt(2f * g * high) / Mathf.Sin(tilt);
                    o.vx = Mathf.Cos(turn) * Mathf.Cos(tilt) * speed + v0.x * 0.3f; o.vy = Mathf.Sin(tilt) * speed; o.vz = Mathf.Sin(turn) * Mathf.Cos(tilt) * speed + v0.z * 0.3f;
                    o.flags = Ballistic | DiesOnGround;
                    o.r = (0.25f + 0.05f * reach) * Luck(0.6f, 1.4f);
                    o.grow = 0.5f + 0.04f * speed;
                    o.mass = Luck(0.6f, 1.6f) * o.r * o.r * 5f;
                    o.life = Luck(2f, 7f);
                    o.tile = Luck(0f, 1f) < 0.6f ? Specks() : Lumpy();
                }
                else if (!vacuum)
                {
                    // Mist, which stays.
                    float speed = reach * Luck(1f, 4f);
                    o.kx = Mathf.Cos(turn) * speed; o.ky = Luck(0.5f, 2f) * speed; o.kz = Mathf.Sin(turn) * speed;
                    o.kloss = 3f; o.drag = 3f;
                    o.r = (0.4f + 0.08f * reach) * Luck(0.7f, 1.4f);
                    o.grow = 0.3f;
                    o.turb = 1f;
                    o.rise = Luck(0.1f, 0.6f);
                    o.mass = Luck(0.3f, 0.8f) * o.r * o.r * 4f;
                    o.life = Luck(5f, 14f) * Mathf.Max(0.3f, Settings.Smoke);
                    o.tile = Wispy();
                }
                else { o.life = 0.01f; }
                Born(i);
            }
        }

        void Sparks(Plan pl, Vector3 c, Vector3 v0, float q, bool onGround)
        {
            int n = Mathf.Min(600, (int)(pl.sparks * Mathf.Min(1.5f, q)));
            if (n > 0)
            {
                if (sparks == null) sparks = Recipe.Sparks(t, gravity, grip, hasGround);
                float fast = 9f + 4.5f * Mathf.Sqrt(pl.reach);
                Color32 colour = pl.tint == 3 ? new Color32(190, 225, 255, 255) : pl.tint == 1 ? new Color32(255, 255, 245, 255) : new Color32(255, 232, 195, 255);
                for (int k = 0; k < n; k++)
                {
                    Vector3 way = LuckSphere();
                    if (onGround) way = OffGround(way, blastUp, 1f);
                    var e = new ParticleSystem.EmitParams
                    {
                        position = c + way * 0.3f,
                        velocity = v0 * 0.6f + way * (fast * Luck(0.3f, 2.2f)),
                        startLifetime = vacuum ? Luck(1.5f, 3.2f) : Luck(0.4f, 1.9f),
                        startSize = Luck(0.05f, 0.13f) * (1f + 0.03f * pl.reach),
                        startColor = colour,
                    };
                    sparks.Emit(e, 1);
                }
            }
            if (!Settings.Debris) return;
            int bits = Mathf.Min(8, (int)((1f + pl.reach * 0.3f) * Mathf.Min(1f, q)));      // fine bits; the big pieces are real ones (see Wreckage)
            if (bits <= 0) return;
            if (shards == null) shards = Recipe.Shards(t, gravity, grip, hasGround);
            float thrown = 5f + 2.5f * Mathf.Sqrt(pl.reach);
            for (int k = 0; k < bits; k++)
            {
                Vector3 way = LuckSphere();
                if (onGround) way = OffGround(way, blastUp, 1f);
                var e = new ParticleSystem.EmitParams
                {
                    position = c + way * 0.3f,
                    velocity = v0 * 0.7f + way * (thrown * Luck(0.4f, 2.4f)),
                    startLifetime = Luck(1.5f, 3.6f),
                    startSize = Luck(0.06f, 0.2f),
                    rotation = Luck(0f, 360f),
                    startColor = new Color32(255, 255, 255, 255),
                };
                shards.Emit(e, 1);
            }
        }

        void Shock(Vector3 c, float R, bool onGround, float fire, float dirt, Color dust)
        {
            lastRing = time;
            if (Assets.Shock != null && sound > 0f)
            {
                // The shock front itself: a shell of squeezed air that bends the light coming through it. It leaves
                // the fireball faster than sound and slows to the speed of sound, weakening as it spreads.
                int at = waveCount < waves.Length ? waveCount++ : 0;
                if (onGround) c.y = GroundAt(c.x, c.z);
                var wave = new Wave { x = c.x, y = c.y, z = c.z, ground = onGround, start = 0.6f * R, radius = 0.6f * R, reach = Mathf.Clamp(R * 30f, 120f, 900f), power = Mathf.Min(0.12f, 0.05f + 0.004f * R) };
                try { Condense(ref wave, c, fire, R); }
                catch (Exception) { wave.cloud = 0f; }
                // (since 0.8.0: the ring of dust it lifts off bare ground as it sweeps over it, see RingDust)
                float size = Mathf.Pow(0.1f * Mathf.Max(fire, 0f), 1f / 3f);
                if (onGround && dirt > 0f && !vacuum && size >= 1.5f && Settings.TestRing != 1f)
                {
                    wave.dust = dirt; wave.dustSize = size; wave.dustFrom = 0.9f * R; wave.dustReach = Mathf.Max(22f * size, 1.5f * R); wave.dustColour = dust;
                }
                waves[at] = wave;
                return;
            }
            ParticleSystem ring;
            if (onGround)
            {
                if (ringFlat == null) ringFlat = Recipe.Ring(t, true);
                ring = ringFlat;
                c.y = GroundAt(c.x, c.z) + 0.3f;
            }
            else
            {
                if (ringAir == null) ringAir = Recipe.Ring(t, false);
                ring = ringAir;
            }
            // In the open air the wave is a faint shell seen edge-on; along the ground it shows more, in the dust it lifts.
            ring.Emit(new ParticleSystem.EmitParams { position = c, startSize = R * 11f, startLifetime = 0.26f + 0.012f * R, startColor = new Color32(255, 255, 255, (byte)(onGround ? 255 : 110)) }, 1);
        }

        /// <summary>
        /// Whether a front leaves a cloud behind it for a moment (a Wilson cloud), and how. Behind the squeezed air of a front
        /// comes air thinned below what it was, for about three times as long; air thinned that quickly cools, about 0.8 of a
        /// degree for each kilopascal; and where that takes it below its dew point the water in it turns to cloud, and back
        /// again as the air comes back. How far out that happens goes with the blast's size and how damp the air is: in air
        /// near its dew point (fog, under a low cloud's base, over the sea at night) a big blast throws a white dome round
        /// itself that is gone within a second; in dry air there is none. A blast is sized here by its charge in kilograms of
        /// TNT, taken as a tenth of the propellant that burns (as is reckoned for liquid propellants going off on a launch pad),
        /// and the thinning behind its front as Kinney and Graham give it, 35/Z kilopascals at the most (Z the distance over the
        /// cube root of the charge). The shader draws it, from what is worked out here (see tools/shaderpack/shock.glsl).
        /// </summary>
        void Condense(ref Wave w, Vector3 c, float fire, float R)
        {
            float size = Mathf.Pow(0.1f * fire, 1f / 3f);
#if DEV
            CondenseSaid = "no cloud: size " + size.ToString("F2") + " m";
#endif
            if (size < 1f || vacuum) return;
            Vector3d at = t.TransformPoint(c), up = t.up;
            double ut = Planetarium.GetUniversalTime();
            float higher = 8f * size;
            float rh = Damp(at, ut), rhAbove = Damp(at + up * higher, ut);
            float kelvin = (float)FlightGlobals.getExternalTemperature(at, body), celsius = kelvin - 273.15f;
#if DEV
            CondenseSaid += ", damp " + rh.ToString("F2") + " (" + rhAbove.ToString("F2") + " " + higher.ToString("F0") + " m up), " + celsius.ToString("F1") + " C";
#endif
            if (rh <= 0.05f && rhAbove <= 0.05f) return;
            // (too cold to hold water enough to show)
            if (celsius < -25f) return;
            float gap = DewGap(rh, celsius), gapAbove = DewGap(rhAbove, celsius - 0.0098f * higher);
            // (the cooling goes with the air's own temperature: put against the gap, as the shader reckons it at 15 degrees)
            float k = 288f / Mathf.Max(kelvin, 150f);
            w.dew = gap * k;
            w.lapse = (gap - gapAbove) / higher * k;
            // Where the cloud may still form at the furthest: where the thinning still cools the air as far as the dampest
            // air in reach is from its dew point. Close in the front heats the air more than its wake ever cools it: from
            // about three and a half of the blast's sizes out (the shader).
            float least = Mathf.Max(0f, Mathf.Min(w.dew, w.dew - w.lapse * 40f * size));
            float furthest = Mathf.Min(25f, 28.5f / Mathf.Max(least, 0.7f));
#if DEV
            CondenseSaid += ", dew gap " + w.dew.ToString("F2") + " K, out to " + furthest.ToString("F1") + " sizes";
#endif
            if (furthest < 3.6f) return;
            w.cloud = size;
            w.fire = 0.9f * R;
            w.gone = (furthest + 4.5f) * size;
#if DEV
            CondenseSaid = "size " + size.ToString("F2") + " m, damp " + rh.ToString("F2") + " (" + rhAbove.ToString("F2") + " " + higher.ToString("F0") + " m up), " + celsius.ToString("F1") +
                " C, dew gap " + w.dew.ToString("F2") + " K (" + (w.lapse * 100f).ToString("F2") + " K less each 100 m up), cloud out to " + furthest.ToString("F1") + " sizes, gone at " + w.gone.ToString("F0") + " m";
#endif
        }

#if DEV
        public static string CondenseSaid = "";
        public static string Condensed() => CondenseSaid;
#endif

        /// <summary>How damp the air is at a place, by the weather mod if there is one (Sources.Damp), else about as it mostly is.</summary>
        float Damp(Vector3d world, double ut)
        {
            if (Sources.Damp != null)
            {
                try { return Mathf.Clamp01(Sources.Damp(world, ut)); }
                catch (Exception) { }
            }
            // (no weather mod: only air with water in it, damper over the sea and at night, drier higher up)
            if (!body.atmosphereContainsOxygen) return 0f;
            double alt = body.GetAltitude(world), lat = body.GetLatitude(world), lon = body.GetLongitude(world);
            bool sea = body.ocean && body.TerrainAltitude(lat, lon, true) <= 0.0;
            float rh = sea ? 0.78f : 0.62f;
            if (!sunUp) rh += 0.08f;
            if (alt > 2500.0) rh *= Mathf.Clamp01(1f - (float)(alt - 2500.0) / 9000f);
            return rh;
        }

        /// <summary>How far above its dew point air is, in kelvin, at a relative humidity and a temperature in degrees (Magnus's formula).</summary>
        static float DewGap(float rh, float celsius)
        {
            const float a = 17.62f, b = 243.12f;
            float g = Mathf.Log(Mathf.Clamp(rh, 0.01f, 1f)) + a * celsius / (b + celsius);
            return Mathf.Max(0f, celsius - b * g / (a - g));
        }

        // ================================================================== things that go on after the blast

        void Waves(float dt)
        {
            for (int n = waveCount - 1; n >= 0; n--)
            {
                ref Wave w = ref waves[n];
                float before = w.radius;
                w.age += dt;
                w.radius = w.start + sound * w.age * (1f + 0.3f * Mathf.Exp(-3f * w.age));
                // As it passes the camera, the camera is jolted.
                float away = (camL - new Vector3(w.x, w.y, w.z)).magnitude;
                if (before < away && w.radius >= away && Air.Instance != null) Air.Instance.Jolt(Mathf.Min(1.5f, 60f * Strength(w)));
                if (w.dust > 0f && before < w.dustReach) RingDust(ref w, Mathf.Max(before, w.dustFrom), Mathf.Min(w.radius, w.dustReach));
                if (w.radius >= Mathf.Max(Mathf.Max(w.reach, w.gone), w.dust > 0f ? w.dustReach : 0f)) waves[n] = waves[--waveCount];
            }
        }

        public int WaveCount => waveCount;

        /// <summary>
        /// (Since 0.8.0.) The dust a front lifts off bare ground as it sweeps over it, between where it was a step ago and where it is
        /// now. Behind a blast's front the air rushes outwards for a moment (the blast wind: some 15 m/s where the front squeezes the
        /// air by a hundredth of an atmosphere, more nearer in), and that lifts the loose dust it passes over: films of big blasts on
        /// open ground show a low ring of dust racing out from under the fireball at the front's speed, far beyond the crater's own
        /// skirt (see Dust), and lying as a haze on the ground after. Lifted out to where the front still squeezes the air by some 3
        /// kPa (about 22 times the cube root of the charge in kilograms of TNT: 130 m for 200 kg, 220 m for a tonne), thinner the
        /// further out, as the push falls; the ground's own colour, low, carried out a little way by that wind and settling over
        /// several seconds. Not off paving or water, nor where the air is too thin to carry a front (see Burst).
        /// </summary>
        /// <summary>
        /// (Since 0.8.0.) The ground's height at a place in the site's axes as far out as a blast's ring of dust goes: the lattice's
        /// where it reaches (see Land), else the planet's own terrain there (no buildings), which beyond the lattice's edge is better
        /// than the edge's height held on, on a slope. Whether that place is under water.
        /// </summary>
        float GroundFar(float x, float z, float fallback, out bool wet)
        {
            wet = false;
            if (land != null && land.Covers(x, z)) return GroundAt(x, z);
            if (body == null || body.pqsController == null) return hasGround ? GroundAt(x, z) : fallback;
            Vector3d world = frame.ToWorld(new Vector3(x, 0f, z));
            double lat = body.GetLatitude(world), lon = body.GetLongitude(world);
            double alt = body.TerrainAltitude(lat, lon, true);
            if (body.ocean && alt < 0.0) { wet = true; alt = 0.0; }
            return frame.ToLocal(body.GetWorldSurfacePosition(lat, lon, alt)).y;
        }

        void RingDust(ref Wave w, float from, float to)
        {
            if (to <= from) return;
            float q = Mathf.Clamp(Settings.Quality * Air.Room(), 0.3f, 1.5f);
            // (puffs as wide as the dust has spread where they lie, wider further out, and as many as cover the ground the front
            // swept over this step, eight tenths of it: as many as the same number for every metre out had left the outer ring
            // one puff to every forty metres round it, and nothing of it was seen beyond eighty metres of a tonne's blast)
            float mid = 0.5f * (from + to), wide = 1.2f + 0.05f * mid;
            float want = q * w.dust * 0.8f * (to * to - from * from) / (wide * wide) + w.dustCarry;
            int n = Mathf.Min((int)want, 400);
            w.dustCarry = Mathf.Min(want - n, 400f);
            for (int k = 0; k < n; k++)
            {
                float rr = Luck(from, to), turn = Luck(0f, Mathf.PI * 2f);
                float cx = Mathf.Cos(turn), cz = Mathf.Sin(turn);
                float px = w.x + cx * rr, pz = w.z + cz * rr;
                float floor = GroundFar(px, pz, w.y, out bool wet);
                if (wet) continue;                                                                    // (over water: no dust)
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                // (how hard the blast wind blows there: full within eight of the blast's sizes, a third of that at the ring's end)
                float strength = Mathf.Clamp01(Mathf.Pow(8f * w.dustSize / Mathf.Max(rr, 1f), 1.2f));
                o.x = px; o.z = pz;
                o.y = floor + Luck(0.3f, 1.2f) * (0.6f + 0.15f * w.dustSize);
                float wind = Mathf.Clamp(40f * strength, 4f, 40f) * Luck(0.6f, 1.2f);
                o.kx = cx * wind; o.kz = cz * wind; o.ky = wind * Luck(0.08f, 0.3f);
                o.kloss = 1.6f; o.drag = 2f;
                o.r = (1.2f + 0.05f * rr) * Luck(0.8f, 1.3f);
                o.grow = 0.25f + 0.004f * rr;
                o.turb = 0.4f + 0.04f * wind;
                o.rise = -Luck(0.02f, 0.15f);
                o.mass = Luck(0.3f, 0.8f) * o.r * o.r * 1.2f * (0.35f + 0.65f * strength);
                o.fadeIn = Luck(0.05f, 0.15f);
                o.life = Luck(5f, 12f) * Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(air));
                float tone = Luck(0.8f, 1.1f);
                o.ar = w.dustColour.r * tone; o.ag = w.dustColour.g * tone; o.ab = w.dustColour.b * tone;
                o.swell = Swell(new Vector3(cx, 0.2f, cz), wind, o.kloss, o.r);
                o.tile = Luck(0f, 1f) < 0.5f ? Wispy() : Lumpy();
                Born(i);
            }
        }

        /// <summary>How far a front pushes the picture aside where it is now, in heights of the screen: weaker the further it has spread, gone at the end of its reach.</summary>
        static float Strength(Wave w) => w.power * Mathf.Min(1f, 2.2f * w.start / w.radius + 0.25f) * Mathf.Pow(Mathf.Max(0f, 1f - w.radius / w.reach), 0.7f);

        /// <summary>The shock fronts there are just now, for drawing (see Air.Shocks).</summary>
        public void Fronts(List<Air.Front> into)
        {
            for (int n = 0; n < waveCount; n++)
            {
                Wave w = waves[n];
                float push = Strength(w) * Settings.Bend;
                bool cloud = w.cloud > 0f && w.radius < w.gone;
                if (push < 0.0005f && !cloud) continue;
                var front = new Air.Front { middle = t.TransformPoint(new Vector3(w.x, w.y, w.z)), up = t.up, radius = w.radius, push = push, ground = w.ground };
                if (cloud)
                {
                    // (and the light on it, as on the smoke: see TuneVolume)
                    front.cloud = new Vector4(w.cloud, w.dew, w.lapse, w.fire);
                    front.sun = (Vector3)(frame.east * sunL.x + frame.up * sunL.y + frame.north * sunL.z);
                    front.sunLight = new Vector3(sunR, sunG, sunB);
                    front.sky = new Vector3(Mathf.Max(ambR, 0.16f * sunR), Mathf.Max(ambG, 0.17f * sunG), Mathf.Max(ambB, 0.2f * sunB));
                    // (and the fire's light from inside it: its strongest lamp near the middle, as the smoke takes it)
                    float most = 0f;
                    for (int k = 0; k < lampCount; k++)
                    {
                        float dx = lamps[k].x - w.x, dy = lamps[k].y - w.y, dz = lamps[k].z - w.z;
                        if (dx * dx + dy * dy + dz * dz < 9f * w.fire * w.fire + 100f) most = Mathf.Max(most, lamps[k].power);
                    }
                    front.fire = new Vector3(fireR, fireG, fireB) * Mathf.Min(0.25f * most, 1f);
                }
                into.Add(front);
            }
        }

        void AddThermal(float x, float y, float z, float a, float w, float top, float turn = 1f)
        {
            int at = thermalCount < thermals.Length ? thermalCount++ : 0;
            if (at == 0 && thermalCount == thermals.Length)
                for (int n = 1; n < thermalCount; n++) if (thermals[n].w < thermals[at].w) at = n;      // full: the weakest gives way
            thermals[at] = new Thermal { x = x, y = y, z = z, a = a, w = w, top = top, turn = turn };
        }

        void Thermals(float dt)
        {
            for (int n = thermalCount - 1; n >= 0; n--)
            {
                ref Thermal h = ref thermals[n];
                float height = hasGround ? h.y - GroundAt(h.x, h.z) : 10f;
                float w = wind10 * Aloft(height);
                h.x += windX * w * dt; h.z += windZ * w * dt;
                h.y += h.w * dt;
                h.a += 0.2f * h.w * dt;                                                     // it draws air in and widens as it climbs
                float loss = 0.1f + 0.085f * h.w / h.a + (h.y > h.top ? 1.2f : 0f);
                h.w *= 1f / (1f + loss * dt);
                if (h.w < 0.12f) thermals[n] = thermals[--thermalCount];
            }
        }

        int AddLamp(float x, float y, float z, float r, float peak, float life, byte tint, bool steady, float flash, float climb)
        {
            int at = lampCount < lamps.Length ? lampCount++ : 0;
            if (at == 0 && lampCount == lamps.Length)
                for (int n = 1; n < lampCount; n++) if (lamps[n].power < lamps[at].power) at = n;
            lampSeed = (lampSeed + 7.31f) % 997f;
            lamps[at] = new Lamp { x = x, y = y, z = z, r = r, peak = peak, power = peak, life = life, tint = tint, steady = steady, flash = flash, climb = climb, seed = lampSeed };
            return at;
        }

        void Lamps(float dt)
        {
            int brightest = -1;
            for (int n = lampCount - 1; n >= 0; n--)
            {
                ref Lamp l = ref lamps[n];
                l.age += dt;
                if (l.age >= l.life) { lamps[n] = lamps[--lampCount]; continue; }
                // (each lamp its own pattern of flicker: by its place in the list, as it was, it jumped to another's whenever a lamp went out)
                float left = 1f - l.age / l.life, flicker = Mathf.PerlinNoise(time * 11f, l.seed);
                if (l.steady && !l.quiet && Settings.TestFireLight != 1f)
                {
                    // A fire burning on the ground puffs: its flame swells and gathers itself again, and its light with it, the more
                    // slowly the wider it is (about 1.5 / √D times a second, D its width in metres: once in two and a half seconds
                    // for fifteen metres, one and a half times a second for one); over that, the quick flicker of its tongues.
                    // (Until 0.8.0 only the quick flicker, the same for a candle as for a burning tank farm.)
                    float wide = Mathf.Max(0.5f, 1.25f * l.r), puff = Mathf.PerlinNoise(time * 2.5f / Mathf.Sqrt(wide), l.seed + 31.7f);
                    l.power = l.peak * Mathf.Min(1f, left * 2.8f) * (0.6f + 0.36f * puff + 0.14f * flicker);
                }
                else l.power = l.steady ? l.peak * Mathf.Min(1f, left * 2.8f) * (0.75f + 0.25f * flicker) : l.peak * left * left * (0.88f + 0.12f * flicker);
                l.y += l.climb * dt;
                l.climb *= 1f / (1f + 0.25f * dt);
                if (l.vx != 0f || l.vy != 0f || l.vz != 0f)
                {
                    l.x += l.vx * dt; l.y += l.vy * dt; l.z += l.vz * dt;
                    float keeps = vacuum ? 1f : 1f / (1f + 3.5f * grip * dt);
                    l.vx *= keeps; l.vy *= keeps; l.vz *= keeps;
                    if (l.vx * l.vx + l.vy * l.vy + l.vz * l.vz < 0.01f) { l.vx = 0f; l.vy = 0f; l.vz = 0f; }
                    if (hasGround) { float floor = GroundAt(l.x, l.z) + 0.3f; if (l.y < floor) { l.y = floor; if (l.vy < 0f) l.vy = 0f; } }
                }
            }
            for (int n = 0; n < lampCount; n++) if (brightest < 0 || lamps[n].power > lamps[brightest].power) brightest = n;
            if (brightest >= 0)
            {
                // The colour of the firelight on the smoke: that of the strongest fire, as an amount of light.
                int at = (lamps[brightest].tint * 6 + 3) * 3;
                fireR = Flames[at] * Flames[at]; fireG = Flames[at + 1] * Flames[at + 1]; fireB = Flames[at + 2] * Flames[at + 2];
            }
            // Real light on the ground and the wreck from the two strongest, and from no more than four in the whole scene.
            int first = brightest, second = -1;
            // (An engine's fire lights the smoke round it, here; the light it throws on the pad and the craft is not ours to
            // make: it has one of its own from whoever draws its flame, or none.)
            if (first >= 0 && lamps[first].quiet)
            {
                first = -1;
                for (int n = 0; n < lampCount; n++) if (!lamps[n].quiet && (first < 0 || lamps[n].power > lamps[first].power)) first = n;
            }
            for (int n = 0; n < lampCount; n++) if (n != first && !lamps[n].quiet && (second < 0 || lamps[n].power > lamps[second].power)) second = n;
            for (int slot = 0; slot < lights.Length; slot++)
            {
                int which = slot == 0 ? first : second;
                bool wanted = Settings.Light && which >= 0 && lamps[which].power > 0.08f;
                Light light = lights[slot];
                if (!wanted)
                {
                    if (light != null && light.enabled) { light.enabled = false; lightsOn = Mathf.Max(0, lightsOn - 1); }
                    continue;
                }
                if (light == null)
                {
                    var holder = new GameObject("Light") { layer = Recipe.Layer };
                    holder.transform.SetParent(t, false);
                    light = lights[slot] = holder.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.shadows = LightShadows.None;
                    light.enabled = false;
                }
                if (!light.enabled)
                {
                    if (lightsOn >= 4) continue;
                    light.enabled = true;
                    lightsOn++;
                }
                int at = (lamps[which].tint * 6 + 3) * 3;
                light.transform.localPosition = new Vector3(lamps[which].x, lamps[which].y, lamps[which].z);
                light.color = new Color(Flames[at], Flames[at + 1], Flames[at + 2]);
                // (As bright beside what else lights the scene as a fire is: in the open by day what it adds near it is little beside
                // the sun's light; by night beside the space centre's floodlights it is many of them. The game's lights are not
                // reckoned in real amounts, and the eye (Natural Light's) opens to what it sees: so it is made stronger as the sun
                // goes, by its light here (after the weather's clouds). Until 0.8.0 it was as strong by night as by day, and a
                // burning pool lit next to nothing round it at night.)
                float daylight = Mathf.Sqrt(Mathf.Max(0f, 0.2126f * sunR + 0.7152f * sunG + 0.0722f * sunB));
                float gain = Settings.TestFireLight == 1f ? 1f : Mathf.Lerp(2.5f, 1f, Mathf.Clamp01(daylight * 1.6f));
                light.intensity = Mathf.Min(8f, lamps[which].power * 1.4f * gain);
                light.range = Mathf.Max(22f, lamps[which].r * 8f);
                // (the strongest fire's light drawn for every dot it reaches, always: left to the game, at night beside the
                // space centre's floodlights it was made one of the cheap kind, worked out at the corners of what it lights,
                // and on the great flat pieces of the ground and the paving that is nothing to be seen)
                light.renderMode = slot == 0 && Settings.TestFireLight != 1f ? LightRenderMode.ForcePixel : LightRenderMode.Auto;
            }
        }

#if DEV
        /// <summary>(What this patch's lights on the scene are doing: on or off, how strong, how far they reach, where, of which kind; and its lamps.)</summary>
        public string LightsSaid()
        {
            var say = new System.Text.StringBuilder();
            for (int slot = 0; slot < lights.Length; slot++)
            {
                Light l = lights[slot];
                if (l == null) { say.Append("light ").Append(slot).Append(": none; "); continue; }
                say.Append("light ").Append(slot).Append(l.enabled ? " on" : " off").Append(", intensity ").Append(l.intensity.ToString("F2")).Append(", range ").Append(l.range.ToString("F0"))
                   .Append(", ").Append(l.renderMode).Append(", at ").Append(l.transform.localPosition.ToString("F1")).Append(", mask ").Append(l.cullingMask).Append("; ");
            }
            say.Append(lampCount).Append(" lamps:");
            for (int n = 0; n < lampCount; n++) say.Append(" [").Append(lamps[n].power.ToString("F2")).Append(" r ").Append(lamps[n].r.ToString("F1")).Append(lamps[n].quiet ? " quiet" : "").Append(lamps[n].steady ? " steady" : "").Append("]");
            return say.ToString();
        }
#endif

        /// <summary>Spilled propellant goes on burning on the ground, and that is where the column of smoke comes from.</summary>
        void AddFire(Plan pl, Vector3 c, float R, float q)
        {
            bool asWas = Settings.TestBlaze == 1f;                                          // (for testing: as until 0.6.2)
            float wide = Mathf.Max(0.6f, R * 0.4f);
            int into = -1;
            if (!asWas)
            {
                // What is spilt where a pool is burning already runs into it: the two are one pool, as wide as both together,
                // that burns for as long as there is fuel in it for its width. (A rocket that comes down on its pad blows up
                // tank by tank, each within a few metres of the last. Each used to leave a pool of its own, up to eight of them
                // in the same place, each sending up as many bits of flame and smoke as if it were the only one: which was
                // bearable while none of them burned for more than half a minute.)
                for (int n = 0; n < fireCount && into < 0; n++)
                {
                    if (fires[n].patch) continue;
                    float dx = fires[n].x - c.x, dz = fires[n].z - c.z, reach = 0.8f * (fires[n].r + wide);
                    if (dx * dx + dz * dz < reach * reach) into = n;
                }
                // (and where there is no room for one more fire, a pool takes the place of the patch with the least left to burn)
                if (into < 0 && fireCount >= fires.Length)
                {
                    int least = -1;
                    for (int n = 0; n < fireCount; n++) if (fires[n].patch && (least < 0 || fires[n].left < fires[least].left)) least = n;
                    if (least >= 0) fires[least] = fires[--fireCount];
                }
            }
            if (into < 0 && fireCount >= fires.Length) return;
            Fire f;
            int lit = -1;                                                                   // (the light of the pool this runs into, if it still has one)
            if (into >= 0)
            {
                f = fires[into];
                for (int n = 0; n < lampCount; n++)
                    if (lamps[n].steady && !lamps[n].quiet && Mathf.Abs(lamps[n].x - f.x) < 0.01f && Mathf.Abs(lamps[n].z - f.z) < 0.01f) lit = n;
                float a = f.r * f.r, b = wide * wide, both = a + b;
                f.x = (f.x * a + c.x * b) / both; f.z = (f.z * a + c.z * b) / both;
                f.left = (f.left * a + pl.burn * b) / both;
                f.total = Mathf.Max(f.total, f.left);
                f.soot = (f.soot * a + pl.soot * b) / both; f.shade = (f.shade * a + pl.smokeShade * b) / both;
                if (b > a) f.tint = (byte)pl.tint;
                f.r = Mathf.Min(Mathf.Sqrt(both), 18f);
            }
            else f = new Fire { x = c.x, z = c.z, r = wide, left = pl.burn, total = pl.burn, soot = pl.soot, shade = pl.smokeShade, tint = (byte)pl.tint, smoky = 0.45f };
            f.y = GroundAt(f.x, f.z) + 0.2f;
            // (as many bits a second, and as big, as go with a blast whose pool is this wide: a pool is four tenths as wide as its blast)
            float blast = into >= 0 ? f.r / 0.4f : R;
            f.rate = q * Mathf.Clamp(18f + 9f * blast, 20f, 220f);
            f.scale = Mathf.Clamp(0.25f + 0.05f * blast, 0.3f, 1f);
            Stands(ref f);
            if (lit >= 0)
            {
                // (one pool, one light: it goes where the pool now is, and lasts as long as the pool now will, as a new pool's would)
                ref Lamp l = ref lamps[lit];
                l.x = f.x; l.y = f.y + Mathf.Max(f.r * 0.5f, 0.3f * f.tall); l.z = f.z; l.r = f.r * 1.6f; l.age = 0f; l.life = f.left; l.tint = f.tint;
                f.lamp = lit;
            }
            else f.lamp = AddLamp(f.x, f.y + Mathf.Max(f.r * 0.5f, 0.3f * f.tall), f.z, f.r * 1.6f, 2.2f, f.left, f.tint, true, 0f, 0f);
            if (into >= 0) fires[into] = f; else fires[fireCount++] = f;
            // What a blast spills does not all lie in one pool under it. Some of it is thrown out and burns where it falls: patches
            // of fire round about the place, most of them the way the wreck was going. They are what is seen of the fire at
            // first, and for most of the time it burns: the pool is in the middle of the blast's own smoke, whose foot stands on
            // it for a minute, and what shows of the pool from outside is the lowest few metres of its flames, when anything
            // does (until 0.6.2 there was the pool and nothing else: the fire a blast left was a glow in the foot of its smoke).
            // The patches stand at the smoke's edge, from just inside the fireball's rim to half as far out again, clear of it
            // within a few seconds, each with its own flames standing a few times its width. Each is a fire of its own, a
            // quarter to a half as wide as the pool, with less to burn and so out sooner; it makes little smoke of its own
            // (most of a fire's smoke is the pool's), sends up no bubbles of hot gas to stir the air as the pool does, and
            // lights the smoke round it but not the ground. One for a blast a few metres wide, three for a big one; and always
            // room left for the pool of the next blast here.
            //
            // (What they cost: a small fire under a tall column is drawn no finer than the grid, some six metres, however narrow
            // it is, so a patch fed in proportion to its width would be a faint smudge. Each is fed some three tenths of the
            // pool's bits instead, as big as the pool's: the three of them are half as many particles again as the pool has, a
            // thousand for a 4-tonne blast, a tenth more than the blast and its pool had without them. TestBlaze 2: without.)
            if (asWas || Settings.TestBlaze == 2f) return;
            // By day only, for now. A patch throws no light on the ground (a patch of air has two real lamps, and they are the
            // blast's and the pool's). By day that does not show. By night it does: seen on 2026-10-10, the patches were bright
            // fires standing on ground that stayed black beside them, glowing shapes and not fire, where the pool alone, its
            // light on the ground and on its smoke, looked right. So a blast in the dusk or the dark leaves its pool and no
            // patches, until the patches can light the ground round them. (Seen with the sun 16 degrees up and three hours
            // after it had set; then, the same night, either side of sunset: with the sun 2.9 and 1.1 degrees up the scene
            // is as good as dark already and the patches were the same glowing shapes, and 1.1 and 4 degrees down the pool
            // alone looked right. So the rule is not the horizon but ten degrees of sun, below which the day's light no
            // longer hides what the patches lack. TestBlaze 3: patches whatever the hour, for working on that.)
            if (sunL.y < 0.174f && Settings.TestBlaze != 3f) return;
            int patches = R < 3.5f ? 0 : R < 7f ? 1 : R < 13f ? 2 : 3;
            var flung = new Vector3(pl.going.x, 0f, pl.going.z);
            float carried = Mathf.Clamp01(flung.magnitude / 120f), first = Luck(0f, Mathf.PI * 2f);
            if (carried > 0.05f) flung.Normalize();
            for (int k = 0; k < patches && fireCount < fires.Length - 2; k++)
            {
                // (each in its own part of the round, so that they do not fall in a heap)
                float turn = first + (k + Luck(-0.3f, 0.3f)) * (Mathf.PI * 2f / patches);
                var way = new Vector3(Mathf.Cos(turn), 0f, Mathf.Sin(turn));
                if (carried > 0.05f) way = (way + flung * (1.6f * carried)).normalized;
                float far = R * Luck(0.9f, 1.6f) * (1f + 0.8f * carried);
                Fire g = f;
                g.x = c.x + way.x * far; g.z = c.z + way.z * far;
                g.y = GroundAt(g.x, g.z) + 0.2f;
                g.r = Mathf.Max(0.5f, wide * Luck(0.25f, 0.45f));
                g.left = g.total = pl.burn * Luck(0.45f, 0.85f);
                // (no two quite alike: some burn harder than others)
                g.rate = q * Mathf.Clamp(0.3f * (18f + 9f * R), 14f, 66f) * Luck(0.7f, 1.15f);
                g.scale = Mathf.Clamp(0.25f + 0.05f * R, 0.3f, 1f);
                g.smoky = 0.15f;
                g.patch = true;
                g.due = Luck(0f, 1f); g.puff = 0f;
                Stands(ref g);
                // (its light is on the smoke round it and no more: the light thrown on the ground and the wreck is the pool's)
                g.lamp = AddLamp(g.x, g.y + Mathf.Max(g.r * 0.5f, 0.3f * g.tall), g.z, g.r * 1.6f, 1.2f, g.total, g.tint, true, 0f, 0f);
                lamps[g.lamp].quiet = true;
                fires[fireCount++] = g;
            }
        }

        /// <summary>
        /// How tall a fire's flames stand and how fast they rise, from how wide it is: a pool of kerosene burns some 1.5 MW to the
        /// square metre, its visible flame reaches 0.235 Q^(2/5) - 1.02 D on the whole (Q in kW, D its width in metres: Heskestad
        /// 1983), a few times its width for a small pool and one and a half times for a big one, and the gas in its flames goes up
        /// at about 1.9 Q^(1/5) metres a second (McCaffrey 1979), of which the flames' puffs here take a third, the rest being the
        /// turmoil the shader draws. (Until 2026-10-10 its flames rose at a couple of metres a second and cooled within half a
        /// second: a fire some eight metres across stood a metre or two high, a low warm glow on the ground.)
        /// </summary>
        static void Stands(ref Fire f)
        {
            float power = 1500f * Mathf.PI * f.r * f.r;
            f.tall = Mathf.Max(0.235f * Mathf.Pow(power, 0.4f) - 2.04f * f.r, 1.2f * f.r);
            f.lick = Mathf.Clamp(0.33f * 1.93f * Mathf.Pow(power, 0.2f), 1.5f, 9f);
        }

        void Fires(float dt)
        {
            float lasts = Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(air));
            for (int n = fireCount - 1; n >= 0; n--)
            {
                ref Fire f = ref fires[n];
                f.left -= dt;
                if (f.left <= 0f) { fires[n] = fires[--fireCount]; continue; }
                float strength = Mathf.Min(1f, f.left / (0.4f * f.total));                  // it dies down at the end
                // A fire on the ground does not burn evenly: it breathes, sending its smoke up in puffs, faster for a small fire than a large one.
                float breath = Mathf.Sin(time * Mathf.PI * 1.5f / Mathf.Sqrt(Mathf.Max(0.5f, 2f * f.r)) + n * 1.7f);
                float puff = 0.55f + 0.9f * breath * breath;
                f.due -= dt * f.rate * strength * puff;
                // With each breath a bubble of hot gas goes up from it, turning itself inside out as it climbs and drawing the
                // air in behind it: these, one after another, are what carry the smoke up as a column, roll it into
                // billows on the way, and pull the smoke near by in towards the fire.
                f.puff -= dt;
                if (f.puff <= 0f && !vacuum && grip > 0.2f && !f.patch)
                {
                    f.puff = 0.667f * Mathf.Sqrt(Mathf.Max(0.5f, 2f * f.r)) * Luck(0.85f, 1.15f);
                    float lift = 0.34f * Mathf.Sqrt(gravity * 2f * f.r) * strength * Mathf.Min(1f, grip) * Luck(0.85f, 1.15f);
                    if (lift > 0.5f) AddThermal(f.x + Luck(-0.2f, 0.2f) * f.r, f.y + 0.8f * f.r, f.z + Luck(-0.2f, 0.2f) * f.r, 0.8f * f.r * Luck(0.85f, 1.15f), lift, f.y + f.r * Luck(9f, 16f), 0.5f);
                }
                while (f.due <= 0f)
                {
                    f.due += 1f;
                    int i = Add();
                    if (i < 0) break;
                    ref P o = ref p[i];
                    float turn = Luck(0f, Mathf.PI * 2f), out_ = f.r * Luck(0f, 1f) * Luck(0f, 1f);
                    o.x = f.x + Mathf.Cos(turn) * out_; o.z = f.z + Mathf.Sin(turn) * out_;
                    o.y = hasGround ? GroundAt(o.x, o.z) + 0.2f : f.y;                       // (the fire lies on the ground, whichever way that slopes)
                    o.r = f.scale * Luck(0.6f, 1.2f) * (0.6f + 0.4f * strength);
                    o.ky = Luck(1f, 3f);
                    o.kloss = 2f; o.drag = 3f;
                    o.heat = Luck(0.8f, 1.02f);
                    o.cool = Luck(1.2f, 2.2f);
                    o.flame = 1f;
                    o.tint = f.tint;
                    // (it has some lift of its own, but it is mostly the bubbles of hot gas going up from the fire that carry it)
                    o.rise = Luck(2.5f, 5f) * Mathf.Sqrt(f.scale * 2f) * (0.7f + 0.45f * puff) * (vacuum || grip <= 0.2f ? 1f : 0.45f);
                    o.grow = 0.1f;
                    o.turb = 1.4f;
                    o.tile = Lumpy();
                    // Up at the speed of the fire's own gas, hot until it has risen as high as the fire's flames stand (see AddFire):
                    // the flame, and the smoke that comes of it where the flames end, at their tips, not at their foot.
                    bool tall = Settings.TestFire < 1f && !vacuum;
                    if (tall)
                    {
                        float lick = f.lick * Luck(0.7f, 1.1f) * (0.6f + 0.4f * strength);
                        o.ky = lick; o.kloss = 1.2f;
                        o.rise = 0.8f * lick;
                        o.cool = 1.2f * lick / Mathf.Max(f.tall * (0.6f + 0.4f * strength), 0.5f) * Luck(0.85f, 1.2f);
                    }
                    if (Luck(0f, 1f) < f.smoky * Mathf.Lerp(0.5f, 1f, f.soot))
                    {
                        float later = o.r * 2.2f;
                        o.mass = Luck(1f, 2.4f) * Mathf.Max(0.15f, f.soot) * later * later;
                        o.life = Luck(10f, 26f) * lasts;
                        float tone = f.shade * Luck(0.8f, 1.4f);
                        o.ar = tone; o.ag = tone * 0.97f; o.ab = tone * 0.93f;
                    }
                    else if (tall)
                    {
                        // (flame only: bigger and thicker than the bits that become smoke, so that, spread up the height of the
                        // flames, it still burns as one fire: spread up it as thinly as before, the flames were patchy, and the
                        // fire's own smoke hid them)
                        o.r *= 1.4f;
                        o.flame = 1.8f;
                        o.life = 2.6f / o.cool;
                    }
                    else o.life = 4.2f / o.cool;
                    Born(i);
                }
            }
        }

        /// <summary>Burning pieces thrown clear, each leaving a trail behind it.</summary>
        void Chunks(Plan pl, Vector3 c, Vector3 v0, float q, bool onGround)
        {
            int n = (int)(pl.chunks * Mathf.Min(1f, q));
            if (vacuum && pl.tint != 1) n = 0;                                              // only what carries its own oxidiser burns out here
            float g = Mathf.Max(0.5f, gravity), R = pl.radius;
            for (int k = 0; k < n && fragCount < frags.Length; k++)
            {
                Vector3 way = LuckSphere();
                // (away from the ground, as the ground lies; in the air, upward)
                float off = Vector3.Dot(way, blastUp);
                way += blastUp * (Mathf.Abs(off) * Luck(0.4f, 1f) + (onGround ? 0.25f : 0f) - off);
                way.Normalize();
                // (Thrown out of the fireball and well clear of it, at three to seven of its radii a second (up to 120 m/s), each
                // trailing a streak as thick as the fire's own lumps: the "octopus" of a big explosion in footage, burning pieces
                // arcing out with their smoke behind them. Thrown as high as one to three radii, as they first were, at about
                // twenty-five metres a second, and trailing half a metre, they never left the fireball, and were not seen at all.)
                float speed = Mathf.Min(120f, R * Luck(3f, 7f) + 6f);
                frags[fragCount++] = new Frag
                {
                    x = c.x, y = c.y + 0.3f, z = c.z, vx = v0.x * 0.7f + way.x * speed, vy = v0.y * 0.7f + way.y * speed, vz = v0.z * 0.7f + way.z * speed,
                    life = Mathf.Clamp(2f * speed / g + 1.5f, 3f, 14f), size = Mathf.Clamp(0.25f + 0.05f * R, 0.35f, 1.5f) * Luck(0.7f, 1.3f),
                    tint = (byte)pl.tint, white = pl.whiteTrails,
                    // (its trail begun only once it is out of the fireball: laid inside it, the trail's first puffs, cooling to
                    // smoke at once, were dark spots on the fire's face)
                    due = Mathf.Min(1.5f, 1.05f * R / Mathf.Max(speed, 1f)),
                };
            }
        }

        void Frags(float dt)
        {
            float every = 1f / (38f * Mathf.Clamp(Settings.Quality * Air.Room(), 0.15f, 1.5f));
            float lasts = Mathf.Max(0.3f, Settings.Smoke);
            for (int n = fragCount - 1; n >= 0; n--)
            {
                ref Frag f = ref frags[n];
                f.age += dt;
                if (f.age >= f.life) { frags[n] = frags[--fragCount]; continue; }
                if (!f.down)
                {
                    f.vy -= gravity * dt;
                    float speed = Mathf.Sqrt(f.vx * f.vx + f.vy * f.vy + f.vz * f.vz);
                    float keep = 1f / (1f + 0.012f * grip * speed * dt);
                    f.vx *= keep; f.vy *= keep; f.vz *= keep;
                    f.x += f.vx * dt; f.y += f.vy * dt; f.z += f.vz * dt;
                    if (hasGround && f.y < GroundAt(f.x, f.z) + 0.1f)
                    {
                        // It lands and burns out where it lies. (Since 0.8.0: throwing up a puff of what it strikes, as hard as it came
                        // down; burning for three to ten seconds, its smoke thinning as its fire dies, where it had burnt out in two to five;
                        // and out at once in water.)
                        float struck = Mathf.Sqrt(f.vx * f.vx + f.vy * f.vy + f.vz * f.vz);
                        f.y = GroundAt(f.x, f.z) + 0.1f;
                        if (Settings.TestRing != 1f) Strike(f.x, f.y, f.z, f.vx, f.vz, struck, f.size);
                        f.down = true; f.downAt = f.age;
                        f.vx = f.vy = f.vz = 0f;
                        f.life = groundWet ? f.age : Settings.TestRing == 1f ? Mathf.Min(f.life, f.age + Luck(1.5f, 5f)) : f.age + Luck(3f, 10f);
                    }
                }
                f.due -= dt;
                // (A piece in flight leaves its puffs by the way it has come, close enough together to run into a streak: at 38 a second,
                // as it was, one going thirty metres a second left a string of beads a metre apart. No more than 55 a second.)
                float laid = every;
                if (!f.down && Settings.TestSoot < 1f)
                {
                    float speed = Mathf.Sqrt(f.vx * f.vx + f.vy * f.vy + f.vz * f.vz);
                    laid = 1f / (Mathf.Clamp(speed / (0.55f * f.size), 30f, 55f) * Mathf.Clamp(Settings.Quality * Air.Room(), 0.15f, 1.5f));
                }
                while (f.due <= 0f)
                {
                    f.due += f.down ? every * (2.5f + 0.6f * (f.age - f.downAt)) : laid;
                    if (!Trail(f.x, f.y, f.z, f.vx, f.vy, f.vz, f.size, f.tint, f.white, lasts)) break;
                }
            }
            TakeTrails(dt);                 // (and the trails other mods say are in it: see Trails)
        }

        /// <summary>
        /// (Since 0.8.0.) A piece coming down throws up what it strikes: a puff of the ground's dust off bare ground, a splash of spray off
        /// water, grit off paving, more and higher the harder it lands; off airless ground the dust flies out on its own paths and
        /// falls back, as the rovers' and landers' films show of anything striking the Moon.
        /// </summary>
        void Strike(float x, float y, float z, float vx, float vz, float struck, float size)
        {
            if (struck < 4f) return;
            int n = Mathf.Clamp((int)(struck * size * 0.25f * Mathf.Clamp(Settings.Quality * Air.Room(), 0.3f, 1.5f)), 1, 8);
            Color what = groundWet ? new Color(0.86f, 0.88f, 0.9f) : groundBare ? groundDust : new Color(0.5f, 0.49f, 0.47f);
            for (int k = 0; k < n; k++)
            {
                int i = Add();
                if (i < 0) return;
                ref P o = ref p[i];
                float turn = Luck(0f, Mathf.PI * 2f), outward = struck * Luck(0.05f, 0.15f);
                o.x = x; o.y = y + 0.15f; o.z = z;
                float tone = Luck(0.85f, 1.1f);
                o.ar = what.r * tone; o.ag = what.g * tone; o.ab = what.b * tone;
                if (vacuum)
                {
                    float up = struck * Luck(0.1f, 0.35f);
                    o.vx = Mathf.Cos(turn) * outward * 2f; o.vy = up; o.vz = Mathf.Sin(turn) * outward * 2f;
                    o.flags = Ballistic | DiesOnGround;
                    o.r = 0.15f + 0.3f * size * Luck(0.6f, 1.2f);
                    o.grow = 0.1f * outward;
                    o.mass = Luck(0.15f, 0.45f) * o.r * o.r * 6f;
                    o.life = 20f;
                    o.tile = Specks();
                }
                else
                {
                    o.kx = Mathf.Cos(turn) * outward + vx * 0.1f; o.kz = Mathf.Sin(turn) * outward + vz * 0.1f; o.ky = struck * Luck(0.08f, 0.2f);
                    o.kloss = 3f; o.drag = 3f;
                    o.r = 0.2f + size * Luck(0.6f, 1.2f);
                    o.grow = groundWet ? 0.5f : 0.3f;
                    o.turb = 0.5f;
                    o.rise = groundWet ? -0.6f : -0.1f;
                    o.mass = Luck(0.4f, 0.9f) * o.r * o.r * 3f;
                    o.life = groundWet ? Luck(1f, 2.2f) : Luck(2f, 5f) * Mathf.Max(0.3f, Settings.Smoke);
                    o.fadeIn = 0.05f;
                    o.tile = groundWet ? Specks() : Luck(0f, 1f) < 0.5f ? Wispy() : Lumpy();
                }
                Born(i);
            }
        }

        /// <summary>One puff of the trail a burning thing leaves: flame that turns to smoke where it was.</summary>
        bool Trail(float x, float y, float z, float vx, float vy, float vz, float size, byte tint, bool white, float lasts)
        {
            int i = Add();
            if (i < 0) return false;
            ref P o = ref p[i];
            o.x = x + Luck(-0.1f, 0.1f); o.y = y + Luck(-0.1f, 0.1f); o.z = z + Luck(-0.1f, 0.1f);
            o.vx = vx * 0.12f + Luck(-0.5f, 0.5f); o.vy = vy * 0.12f + Luck(-0.5f, 0.5f); o.vz = vz * 0.12f + Luck(-0.5f, 0.5f);
            o.r = size * Luck(0.8f, 1.4f);
            o.heat = Luck(0.7f, 0.95f);
            o.cool = 3f;
            o.flame = 1f;
            o.tint = tint;
            o.tile = Luck(0f, 1f) < 0.3f ? Wispy() : Lumpy();
            if (vacuum)
            {
                o.flags = Ballistic;
                o.grow = 1.5f;
                o.life = 0.8f;
            }
            else if (Settings.TestSoot < 1f && vx * vx + vy * vy + vz * vz < 0.01f)
            {
                // A piece that has come down and burns where it lies: its smoke goes up as a plume, a thin wisp leaning on the wind,
                // narrow at the fire and widening as it climbs (see Smoulder). (It was a puff rising at 0.6 m/s every sixth of a
                // second: they gathered into a black ball sitting over the piece.)
                o.flame = 0.6f;
                o.drag = 4f; o.kloss = 2f;
                o.ky = Luck(0.8f, 1.6f);
                o.r = size * Luck(0.55f, 0.9f);
                o.grow = 0.15f;
                o.turb = Luck(0.8f, 1.4f);
                o.rise = Luck(2.2f, 3.2f);
                o.heat = Luck(0.42f, 0.55f);
                o.cool = Luck(1.4f, 2f);
                o.mass = Luck(0.6f, 1.3f) * o.r * o.r * 2.2f;
                o.life = Luck(3f, 6f) * lasts;
                o.fadeIn = 0.15f;
                float tone = white ? Luck(0.45f, 0.7f) : Luck(0.03f, 0.07f);
                o.ar = tone; o.ag = tone; o.ab = tone;
            }
            else
            {
                o.drag = 5f; o.kloss = 4f;
                o.grow = 0.06f;
                o.turb = 0.8f;
                o.rise = 0.6f;
                o.mass = Luck(0.6f, 1.3f) * o.r * o.r * 4f;
                o.life = Luck(2.5f, 6f) * lasts;
                float tone = white ? Luck(0.45f, 0.7f) : Luck(0.03f, 0.07f);
                o.ar = tone; o.ag = tone; o.ab = tone;
                if (Settings.TestSoot < 1f)
                {
                    // A burning piece trails flame first and smoke further back: its trail stays fire for the first half second
                    // or so, by which time the piece is out of the fireball it was thrown from. (It went dark within a fifth of a
                    // second, still inside the fire: each piece's trail was a string of black beads on the fireball, which was the
                    // most of the "blobs" on a fireball in its first second.) Thinner, as so many more puffs make it.
                    o.r = size * Luck(0.7f, 1.1f);
                    o.heat = Luck(0.85f, 1f);
                    o.cool = Luck(1.3f, 1.8f);
                    o.mass = Luck(0.6f, 1.3f) * o.r * o.r * 2.2f;
                }
            }
            Born(i);
            return true;
        }

        /// <summary>The glare of the fires, and the burning pieces themselves: a handful of soft lights drawn behind and in front of the cloud.</summary>
        void Glows()
        {
            int n = 0;
            // (The glare is the light a fire throws into the air round it, and what the eye's own lens spreads round a light far
            // brighter than what is round it: at night and at dusk, a fire's. By day a fire is hardly brighter than the sunlit
            // ground and smoke round it, and no glare is seen. At its full strength by day, as it was, every fire stood in a soft
            // yellow ball of its own light, and where that ball met the ground each burning pool was a smooth glowing pillow:
            // "a glowing volumetric orb ... doesn't really look like fire" (the owner, 2026-10-17).)
            float glare = Settings.TestGlare == 1f ? 1f : Mathf.Lerp(1f, 0.12f, daylight);
            for (int k = 0; k < lampCount && n < glows.Length; k++)
            {
                if (lamps[k].quiet) continue;
                int at = (lamps[k].tint * 6 + 3) * 3;
                float v = Mathf.Min(1f, lamps[k].power * 0.3f) * 255f * glare;
                glows[n++] = Glow(lamps[k].x, lamps[k].y, lamps[k].z, lamps[k].r * 3.6f, Flames[at] * v, Flames[at + 1] * v, Flames[at + 2] * v);
            }
            glowBack.SetParticles(glows, n);

            n = 0;
            for (int k = 0; k < lampCount && n < glows.Length; k++)
            {
                if (lamps[k].flash <= 0f || lamps[k].age > 0.16f) continue;
                float s = lamps[k].age / 0.16f, v = (1f - s) * (1f - s) * 255f;
                glows[n++] = Glow(lamps[k].x, lamps[k].y, lamps[k].z, lamps[k].flash * (0.55f + 0.6f * s), v, v * 0.95f, v * 0.85f);
            }
            glowFront.SetParticles(glows, n);
            // (kept as they are, to be dimmed by the smoke in front of each just before the frame is drawn: see SeeFlashes)
            Array.Copy(glows, flashes, n);
            flashCount = n;
        }

        /// <summary>
        /// A blast's flash is drawn after all the smoke (it is the first thing seen of a blast, before there is any smoke),
        /// so nothing used to hide it: in a crash, where one thing goes up after another, each later flash glared through
        /// the black cloud of the earlier ones standing in front of it (the user saw "flashes showing above the smoke").
        /// Now each flash is dimmed by as much smoke and flame as lies on the straight line from the camera to it, in
        /// every patch of air: walked through the grids the shader draws from (see Optical). A flash in the open, or in
        /// front of the smoke, is as bright as ever; one behind a thick cloud is gone. Once a frame, when every patch has
        /// been put where it is drawn (Air.BeforeDrawing).
        /// </summary>
        internal void SeeFlashes(Vector3 eye, List<Site> all)
        {
            if (flashCount == 0 || glowFront == null || Settings.TestFlash == 1f) return;
            float least = 1f;
            for (int i = 0; i < flashCount; i++)
            {
                ParticleSystem.Particle p = flashes[i];
                Vector3 at = t.TransformPoint(p.position);
                float deep = 0f;
                for (int k = 0; k < all.Count; k++) deep += all[k].Optical(eye, at);
                float seen = Mathf.Exp(-deep);
                least = Mathf.Min(least, seen);
                Color32 c = p.startColor;
                p.startColor = new Color32((byte)(c.r * seen + 0.5f), (byte)(c.g * seen + 0.5f), (byte)(c.b * seen + 0.5f), c.a);
                flashesSeen[i] = p;
            }
            glowFront.SetParticles(flashesSeen, flashCount);
            if (Settings.TestFlash == 2f && Time.time - flashSaid > 1f)
            {
                flashSaid = Time.time;
                Addon.Log("flashes: " + flashCount + ", the most hidden let through " + least.ToString("F3"));
            }
        }

        float flashSaid = -10f;

        /// <summary>
        /// How much this patch's smoke and flame block on the straight line between two places in the scene, as an optical
        /// depth (what each metre of it blocks, added up over the metres): read from the grid last handed to the shader
        /// (what it hides per metre, the same numbers the volume is drawn from, without the shader's fine detail), where
        /// its box is now. Nothing if the line misses the box.
        /// </summary>
        internal float Optical(Vector3 from, Vector3 to)
        {
            if (box == null || boxRenderer == null || !boxRenderer.enabled) return 0f;
            Transform b = box.transform;
            Vector3 a = b.InverseTransformPoint(from), d = b.InverseTransformPoint(to) - a;
            // (where the line is inside the box, which is -0.5 to 0.5 each way in its own units)
            float t0 = 0f, t1 = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = a[axis], dd = d[axis];
                if (Mathf.Abs(dd) < 1e-7f) { if (o < -0.5f || o > 0.5f) return 0f; continue; }
                float ta = (-0.5f - o) / dd, tb = (0.5f - o) / dd;
                if (ta > tb) { float swap = ta; ta = tb; tb = swap; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 >= t1) return 0f;
            }
            const int Steps = 48;
            float metres = Vector3.Distance(from, to) * (t1 - t0) / Steps, sum = 0f;
            const float SmokeUnit = Thickest / 65535f, FlameUnit = ThickestFlame / 65535f;
            for (int n = 0; n < Steps; n++)
            {
                Vector3 q = a + d * (t0 + (t1 - t0) * (n + 0.5f) / Steps);
                int i = Mathf.Clamp((int)((q.x + 0.5f) * G), 0, G - 1), j = Mathf.Clamp((int)((q.y + 0.5f) * G), 0, G - 1), k = Mathf.Clamp((int)((q.z + 0.5f) * G), 0, G - 1);
                Color32 cell = cellsB[i + G * (j + G * k)];
                sum += ((cell.r << 8) | cell.g) * SmokeUnit + ((cell.b << 8) | cell.a) * FlameUnit;
            }
            return sum * metres;
        }

        static ParticleSystem.Particle Glow(float x, float y, float z, float size, float r, float g, float b) => new ParticleSystem.Particle
        {
            position = new Vector3(x, y, z), startSize = size, startLifetime = 1000f, remainingLifetime = 999f, randomSeed = 1,
            startColor = new Color32((byte)Mathf.Clamp(r, 0f, 255f), (byte)Mathf.Clamp(g, 0f, 255f), (byte)Mathf.Clamp(b, 0f, 255f), 255),
        };

        // ================================================================== ships in the smoke

        /// <summary>
        /// Find the ships (and kerbals, and wreckage) in or near the cloud. Their hulls are taken as spheres
        /// that the air has to go round, and their running engines as jets that blow it away.
        /// </summary>
        void Wash()
        {
            ballCount = jetCount = bodyCount = 0;
            if (!Settings.Push || count == 0 || vacuum || space) return;
            bool wakes = Settings.Predict || Settings.Wakes || Settings.Vortices;
            float mx = (bx0 + bx1) * 0.5f, my = (by0 + by1) * 0.5f, mz = (bz0 + bz1) * 0.5f;
            float reach = 0.5f * Mathf.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0) + (bz1 - bz0) * (bz1 - bz0)) + 12f;
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            for (int n = 0; n < loaded.Count; n++)
            {
                Vessel v = loaded[n];
                if (v == null || !v.loaded || v.packed || v.parts == null) continue;
                Vector3 at = frame.ToLocal(v.CoMD);
                float size = 4f + 2.2f * Mathf.Pow(v.parts.Count, 0.34f);
                float dx = at.x - mx, dy = at.y - my, dz = at.z - mz, far = reach + size + 60f;
                if (dx * dx + dy * dy + dz * dz > far * far) continue;
                Vector3 going = frame.DirToLocal(v.srf_velocity);
                float speed = going.magnitude;
                bool moving = speed > 0.4f && dx * dx + dy * dy + dz * dz < (reach + size) * (reach + size);
                // (and the craft as a whole, for its wake, from as far off as it will come in the time it is looked ahead for: see Wakes.cs)
                float coming = reach + size + speed * LookAheadFor;
                if (wakes && speed > 2f && dx * dx + dy * dy + dz * dz < coming * coming) Craft(v, at, going, speed);
                int every = Mathf.Max(1, v.parts.Count / 24);
                float wider = Mathf.Pow(every, 0.33f);
                for (int k = 0; k < v.parts.Count; k++)
                {
                    Part part = v.parts[k];
                    if (part == null) continue;
                    if (jetCount < jets.Length)
                        for (int m = 0; m < part.Modules.Count; m++)
                        {
                            if (!(part.Modules[m] is ModuleEngines engine) || !engine.EngineIgnited || engine.finalThrust < 1f || engine.thrustTransforms == null) continue;
                            int nozzles = Mathf.Max(1, engine.thrustTransforms.Count);
                            float each = engine.finalThrust / nozzles;
                            for (int z = 0; z < engine.thrustTransforms.Count && jetCount < jets.Length; z++)
                            {
                                Transform nozzle = engine.thrustTransforms[z];
                                if (nozzle == null) continue;
                                Vector3 from = frame.ToLocal(nozzle.position), along = frame.DirToLocal(nozzle.forward);
                                float r = Mathf.Clamp(0.25f + 0.035f * Mathf.Sqrt(each), 0.3f, 2.5f);
                                jets[jetCount++] = new Jet { x = from.x, y = from.y, z = from.z, dx = along.x, dy = along.y, dz = along.z, r = r, speed = Mathf.Clamp(18f + 6f * Mathf.Sqrt(each), 20f, 140f), length = 25f + 60f * r };
                            }
                        }
                    if (!moving || k % every != 0 || ballCount >= balls.Length) continue;
                    Vector3 where = frame.ToLocal(part.transform.position), box = part.prefabSize;
                    float radius = box.sqrMagnitude < 1e-4f ? 0.6f : Mathf.Clamp(Mathf.Max(box.x, Mathf.Max(box.y, box.z)) * 0.5f, 0.3f, 3.5f);
                    balls[ballCount++] = new Ball { x = where.x, y = where.y, z = where.z, vx = going.x, vy = going.y, vz = going.z, r = radius * wider, speed = speed };
                }
            }
            TakeMovers();                   // (and what other mods say is moving through the air: see Movers)
        }
    }
}
