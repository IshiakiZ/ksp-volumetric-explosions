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
            p[count].seed = (uint)Random.Range(1, int.MaxValue);
            p[count].roll = Random.Range(0f, 360f);
            p[count].spin = Random.Range(-22f, 22f);
            return count++;
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
            if (R > scale || count < 50)
            {
                scale = Mathf.Max(2f, Mathf.Max(R, pl.reach * 0.6f));
                // The size of the billows, and what counts as thick smoke, go with the size of the blast. They are set
                // when a cloud starts (or a much bigger blast takes it over) and then left alone.
                if (count < 50 || scale * 0.55f + 3.5f > detailRepeat * 1.5f)
                {
                    detailRepeat = Mathf.Clamp(scale * 0.55f + 3.5f, 4f, 26f);
                    fullLength = Mathf.Clamp(0.13f * scale, 0.8f, 4f);
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

            if (count > 0 && !vacuum) Shove(c, Mathf.Max(R, pl.reach * 0.7f));
            if (R > 0.3f) Fireball(pl, c, v0, R, q, onGround);
            if (pl.vapour > 0.5f) Vapour(pl, c, v0, q, onGround);
            if (pl.water && (pl.ground || pl.underwater)) Splash(pl, c, v0, q);
            else if (pl.dirt > 0f) Dust(pl, c, v0, q);
            Sparks(pl, c, v0, q, onGround);

            if (R > 0.3f)
            {
                float lift = !vacuum && grip > 0.2f && R > 1.2f ? 0.78f * Mathf.Sqrt(gravity * R) * Mathf.Min(1f, grip) : 0f;
                float up = onGround ? R * 0.45f : 0f;
                int lamp = AddLamp(c.x, c.y + up, c.z, R, vacuum ? 3.5f : 5f, vacuum ? 0.3f + 0.05f * R : 0.9f + 0.32f * R, (byte)pl.tint, false, R * 6.5f, lift * 0.8f);
                // The fire of something that blew up at speed flies on (see Fireball), and its light goes with it: left
                // where the blast was, it was a glowing ball hanging in the air behind a fire already far away.
                Vector3 thrown = vacuum ? v0 : v0 * Mathf.Lerp(1f, 0.75f * (0.4f + 0.6f * Mathf.Clamp01(v0.magnitude / 120f)), Mathf.Clamp01(grip));
                lamps[lamp].vx = thrown.x; lamps[lamp].vy = thrown.y; lamps[lamp].vz = thrown.z;
                if (lift > 0f) AddThermal(c.x + v0.x * 0.15f, c.y + up, c.z + v0.z * 0.15f, R * 0.72f, lift, c.y + R * Random.Range(6f, 10f));
                Chunks(pl, c, v0, q, onGround);
            }
            if (pl.burn > 0f && onGround) AddFire(pl, c, R, q);
            if (Settings.Shockwave && air > 0.12f && R > 2f && time - lastRing > 0.5f) Shock(c, R, onGround);
        }

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
                Vector3 way = Random.onUnitSphere;
                if (onGround) way = OffGround(way, blastUp, 1f);
                lobes[m] = way;
                more[m] = Random.Range(0.1f, 1.0f) * Random.Range(0.4f, 1f);
            }
            // Something that hit at speed throws its fire on ahead of it.
            float carried = Mathf.Clamp01(v0.magnitude / 120f);

            for (int k = 0; k < n; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = Random.onUnitSphere;
                if (onGround) way = OffGround(way, blastUp, 0.5f).normalized;
                float gain = 1f;
                for (int m = 0; m < lobes.Length; m++)
                {
                    float along = Vector3.Dot(way, lobes[m]);
                    if (along > 0f) { float a2 = along * along; gain += more[m] * a2 * a2; }
                }
                float rho = Mathf.Pow(Random.value, 0.4f);                                  // most of it is toward the outside
                float reach = R * (0.2f + 0.8f * rho) * gain, start = R * 0.2f * Random.value;
                o.x = c.x + way.x * start; o.y = c.y + way.y * start; o.z = c.z + way.z * start;
                o.r = rp * Random.Range(0.7f, 1.35f);
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
                    float keeps = Mathf.Lerp(1f, Random.Range(0.5f, 1f) * (0.4f + 0.6f * carried), Mathf.Clamp01(grip));
                    o.vx = v0.x * keeps; o.vy = v0.y * keeps; o.vz = v0.z * keeps;
                    o.kloss = 1f / spreads;
                    o.swell = Swell(way, speed, o.kloss, o.r);
                    o.drag = 3.5f;
                    o.grow = mixes;
                    o.turb = 0.5f + 0.1f * speed;
                    o.rise = 0.5f * Mathf.Sqrt(R);
                }
                // The middle is hottest and stays hot longest; the outside cools at once into smoke.
                o.heat = 1.12f - 0.42f * rho + 0.12f * (gain - 1f) + Random.Range(-0.08f, 0.08f);
                // In air the fire cools into smoke. With no air it stays bright and simply thins away as it flies apart.
                o.cool = vacuum ? 0.7f / burns : (1.3f + 2.2f * rho * rho) / burns * Random.Range(0.8f, 1.25f);
                o.flame = Random.Range(0.75f, 1.15f);
                o.tint = (byte)pl.tint;
                o.tile = Lumpy();
                if (Random.value < smoky)
                {
                    float later = o.r * 2.2f;                                               // about the size it has grown to when the smoke shows
                    o.mass = Random.Range(0.6f, 2.2f) * Mathf.Pow(Mathf.Max(0.05f, pl.soot), 0.7f) * later * later;
                    o.life = smokeLasts * Random.Range(0.45f, 1.15f);
                    // Some of it is soot from the first moment: the dark blotches that roll about in a fuel fire.
                    if (Random.value < 0.08f * pl.soot) { o.heat *= 0.42f; o.cool *= 0.6f; }
                    float tone = shade * Random.Range(0.7f, 1.5f);
                    o.ar = tone; o.ag = tone * 0.97f; o.ab = tone * 0.93f;
                }
                else o.life = vacuum ? burns * Random.Range(1.6f, 3f) : 4.2f / o.cool;     // flame only: gone when it is out (or, with no air, when it has thinned to nothing)
                Born(i);
            }

            // Cooler smoke round the outside from the start: the dark rind a fuel fire has.
            int rind = smoky > 0f ? (int)(n * 0.3f * pl.soot) : 0;
            for (int k = 0; k < rind; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                Vector3 way = Random.onUnitSphere;
                if (onGround) way = OffGround(way, blastUp, 0.5f).normalized;
                float reach = R * Random.Range(0.8f, 1.25f), speed = reach / spreads;
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.kx = way.x * speed; o.ky = way.y * speed; o.kz = way.z * speed;
                float kept = Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(grip));
                o.vx = v0.x * kept; o.vy = v0.y * kept; o.vz = v0.z * kept;
                o.kloss = 1f / spreads;
                o.drag = 3.5f;
                o.r = rp * Random.Range(1.2f, 2.2f);
                o.swell = Swell(way, speed, o.kloss, o.r);
                o.grow = mixes;
                o.turb = 0.5f + 0.1f * speed;
                o.rise = 0.3f * Mathf.Sqrt(R);
                o.mass = Random.Range(0.8f, 2.2f) * pl.soot * o.r * o.r * 2.5f;
                o.life = smokeLasts * Random.Range(0.4f, 1.1f);
                o.fadeIn = burns * Random.Range(0.45f, 0.9f);
                float tone = shade * Random.Range(0.8f, 1.5f);
                o.ar = tone; o.ag = tone * 0.97f; o.ab = tone * 0.93f;
                o.tile = Lumpy();
                Born(i);
            }
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
                Vector3 way = Random.onUnitSphere;
                if (onGround) way = OffGround(way, blastUp, 0.4f);
                float far = reach * Random.Range(0.2f, 1.1f);
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.r = (0.25f + 0.05f * reach) * Random.Range(0.7f, 1.4f);
                if (vacuum)
                {
                    float speed = far / 0.35f;
                    o.vx = v0.x + way.x * speed; o.vy = v0.y + way.y * speed; o.vz = v0.z + way.z * speed;
                    o.flags = Ballistic;
                    o.grow = speed * 0.45f;
                    o.life = Random.Range(0.7f, 1.8f);
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
                    o.life = Random.Range(2.5f, 7f) * Mathf.Max(0.3f, Settings.Smoke);
                }
                o.mass = Random.Range(0.4f, 1.0f) * o.r * o.r * 4f;
                float tone = Random.Range(0.7f, 0.9f);
                o.ar = tone; o.ag = tone; o.ab = tone * 1.04f;
                o.tile = Random.value < 0.5f ? Wispy() : Lumpy();
                Born(i);
            }
        }

        /// <summary>Dirt thrown out along the ground and up. In air it billows and hangs; with no air it flies in clean arcs and drops.</summary>
        void Dust(Plan pl, Vector3 c, Vector3 v0, float q)
        {
            float reach = pl.reach;
            int n = (int)(q * pl.dirt * Mathf.Clamp(35f * Mathf.Pow(reach, 1.5f), 40f, 2500f));
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
                float turn = Random.Range(0f, Mathf.PI * 2f), tilt = Random.value;
                tilt = tilt * tilt * 1.2f;                                                  // most of it low, a little straight up
                Vector3 way = lay * new Vector3(Mathf.Cos(turn) * Mathf.Cos(tilt), Mathf.Sin(tilt), Mathf.Sin(turn) * Mathf.Cos(tilt));
                way = (way + ahead * (0.7f * thrown)).normalized;                           // an object that came in at a slant throws it on ahead
                float far = reach * Random.Range(0.5f, 2.3f);
                o.x = c.x + way.x * 0.3f * reach * Random.value; o.z = c.z + way.z * 0.3f * reach * Random.value;
                o.y = GroundAt(o.x, o.z) + 0.2f;
                float tone = Random.Range(0.75f, 1.15f);
                o.ar = pl.dust.r * tone; o.ag = pl.dust.g * tone; o.ab = pl.dust.b * tone;
                if (vacuum)
                {
                    // Speed for a throw of that length at that angle.
                    float angle = Mathf.Clamp(tilt, 0.26f, 1.3f);
                    float speed = Mathf.Sqrt(Mathf.Max(0.5f, gravity) * far / Mathf.Sin(2f * angle));
                    way = lay * new Vector3(Mathf.Cos(turn) * Mathf.Cos(angle), Mathf.Sin(angle), Mathf.Sin(turn) * Mathf.Cos(angle));
                    o.vx = way.x * speed; o.vy = way.y * speed; o.vz = way.z * speed;
                    o.flags = Ballistic | DiesOnGround;
                    o.r = (0.2f + 0.04f * reach) * Random.Range(0.6f, 1.3f);
                    o.grow = 0.08f * speed;
                    o.mass = Random.Range(0.15f, 0.45f) * o.r * o.r * 6f;
                    o.life = 60f;
                    o.tile = Random.value < 0.4f ? Specks() : Lumpy();
                }
                else
                {
                    float speed = far / 0.3f;
                    o.kx = way.x * speed; o.ky = way.y * speed; o.kz = way.z * speed;
                    o.kloss = 3.3f; o.drag = 3f;
                    o.r = (0.3f + 0.06f * reach) * Random.Range(0.6f, 1.4f);
                    o.grow = 0.15f;
                    o.turb = 0.5f + 0.08f * speed;
                    o.rise = -Random.Range(0.1f, 0.7f);                                     // it settles
                    o.mass = Random.Range(0.5f, 1.5f) * o.r * o.r * 4f;
                    o.swell = Swell(way, speed, o.kloss, o.r);
                    o.life = Random.Range(6f, 18f) * Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(air));
                    o.tile = Random.value < 0.25f ? Wispy() : Lumpy();
                }
                Born(i);
            }
        }

        /// <summary>On water: a column of spray straight up, a crown thrown outward, and mist left drifting.</summary>
        void Splash(Plan pl, Vector3 c, Vector3 v0, float q)
        {
            float level = hasGround ? GroundAt(c.x, c.z) : c.y;
            float depth = Mathf.Max(0f, level - c.y);
            if (depth > 30f) return;                                                        // too deep to break the surface
            float reach = Mathf.Max(pl.reach, pl.radius) * (1f - depth / 40f);
            int n = (int)(q * Mathf.Clamp(45f * Mathf.Pow(reach, 1.5f), 60f, 3000f));
            float g = Mathf.Max(0.5f, gravity);
            for (int k = 0; k < n; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float turn = Random.Range(0f, Mathf.PI * 2f), kind = Random.value;
                float tone = Random.Range(0.8f, 0.95f);
                o.ar = tone; o.ag = tone; o.ab = tone;
                o.x = c.x + Mathf.Cos(turn) * reach * 0.25f * Random.value; o.z = c.z + Mathf.Sin(turn) * reach * 0.25f * Random.value;
                o.y = level + 0.2f;
                if (kind < 0.8f)
                {
                    // Water: thrown, and it falls back.
                    bool column = kind < 0.35f;
                    float tilt = column ? Random.Range(1.25f, 1.55f) : Random.Range(0.7f, 1.2f);
                    float high = reach * (column ? Random.Range(1.5f, 4.5f) : Random.Range(0.5f, 1.6f));
                    float speed = Mathf.Sqrt(2f * g * high) / Mathf.Sin(tilt);
                    o.vx = Mathf.Cos(turn) * Mathf.Cos(tilt) * speed + v0.x * 0.3f; o.vy = Mathf.Sin(tilt) * speed; o.vz = Mathf.Sin(turn) * Mathf.Cos(tilt) * speed + v0.z * 0.3f;
                    o.flags = Ballistic | DiesOnGround;
                    o.r = (0.25f + 0.05f * reach) * Random.Range(0.6f, 1.4f);
                    o.grow = 0.5f + 0.04f * speed;
                    o.mass = Random.Range(0.6f, 1.6f) * o.r * o.r * 5f;
                    o.life = Random.Range(2f, 7f);
                    o.tile = Random.value < 0.6f ? Specks() : Lumpy();
                }
                else if (!vacuum)
                {
                    // Mist, which stays.
                    float speed = reach * Random.Range(1f, 4f);
                    o.kx = Mathf.Cos(turn) * speed; o.ky = Random.Range(0.5f, 2f) * speed; o.kz = Mathf.Sin(turn) * speed;
                    o.kloss = 3f; o.drag = 3f;
                    o.r = (0.4f + 0.08f * reach) * Random.Range(0.7f, 1.4f);
                    o.grow = 0.3f;
                    o.turb = 1f;
                    o.rise = Random.Range(0.1f, 0.6f);
                    o.mass = Random.Range(0.3f, 0.8f) * o.r * o.r * 4f;
                    o.life = Random.Range(5f, 14f) * Mathf.Max(0.3f, Settings.Smoke);
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
                    Vector3 way = Random.onUnitSphere;
                    if (onGround) way = OffGround(way, blastUp, 1f);
                    var e = new ParticleSystem.EmitParams
                    {
                        position = c + way * 0.3f,
                        velocity = v0 * 0.6f + way * (fast * Random.Range(0.3f, 2.2f)),
                        startLifetime = vacuum ? Random.Range(1.5f, 3.2f) : Random.Range(0.4f, 1.9f),
                        startSize = Random.Range(0.05f, 0.13f) * (1f + 0.03f * pl.reach),
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
                Vector3 way = Random.onUnitSphere;
                if (onGround) way = OffGround(way, blastUp, 1f);
                var e = new ParticleSystem.EmitParams
                {
                    position = c + way * 0.3f,
                    velocity = v0 * 0.7f + way * (thrown * Random.Range(0.4f, 2.4f)),
                    startLifetime = Random.Range(1.5f, 3.6f),
                    startSize = Random.Range(0.06f, 0.2f),
                    rotation = Random.Range(0f, 360f),
                    startColor = new Color32(255, 255, 255, 255),
                };
                shards.Emit(e, 1);
            }
        }

        void Shock(Vector3 c, float R, bool onGround)
        {
            lastRing = time;
            if (Assets.Shock != null && sound > 0f)
            {
                // The shock front itself: a shell of squeezed air that bends the light coming through it. It leaves
                // the fireball faster than sound and slows to the speed of sound, weakening as it spreads.
                int at = waveCount < waves.Length ? waveCount++ : 0;
                if (onGround) c.y = GroundAt(c.x, c.z);
                waves[at] = new Wave { x = c.x, y = c.y, z = c.z, ground = onGround, start = 0.6f * R, radius = 0.6f * R, reach = Mathf.Clamp(R * 30f, 120f, 900f), power = Mathf.Min(0.12f, 0.05f + 0.004f * R) };
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
                if (w.radius >= w.reach) waves[n] = waves[--waveCount];
            }
        }

        public int WaveCount => waveCount;

        /// <summary>How far a front pushes the picture aside where it is now, in heights of the screen: weaker the further it has spread, gone at the end of its reach.</summary>
        static float Strength(Wave w) => w.power * Mathf.Min(1f, 2.2f * w.start / w.radius + 0.25f) * Mathf.Pow(Mathf.Max(0f, 1f - w.radius / w.reach), 0.7f);

        /// <summary>The shock fronts there are just now, for drawing (see Air.Shocks).</summary>
        public void Fronts(List<Air.Front> into)
        {
            for (int n = 0; n < waveCount; n++)
            {
                Wave w = waves[n];
                float push = Strength(w) * Settings.Bend;
                if (push < 0.0005f) continue;
                into.Add(new Air.Front { middle = t.TransformPoint(new Vector3(w.x, w.y, w.z)), up = t.up, radius = w.radius, push = push, ground = w.ground });
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
            lamps[at] = new Lamp { x = x, y = y, z = z, r = r, peak = peak, power = peak, life = life, tint = tint, steady = steady, flash = flash, climb = climb };
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
                float left = 1f - l.age / l.life, flicker = Mathf.PerlinNoise(time * 11f, n * 3.7f);
                l.power = l.steady ? l.peak * Mathf.Min(1f, left * 2.8f) * (0.75f + 0.25f * flicker) : l.peak * left * left * (0.88f + 0.12f * flicker);
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
                light.intensity = Mathf.Min(8f, lamps[which].power * 1.4f);
                light.range = Mathf.Max(22f, lamps[which].r * 8f);
            }
        }

        /// <summary>Spilled propellant goes on burning on the ground, and that is where the column of smoke comes from.</summary>
        void AddFire(Plan pl, Vector3 c, float R, float q)
        {
            if (fireCount >= fires.Length) return;
            float floor = GroundAt(c.x, c.z);
            var f = new Fire
            {
                x = c.x, y = floor + 0.2f, z = c.z, r = Mathf.Max(0.6f, R * 0.4f), left = pl.burn, total = pl.burn,
                rate = q * Mathf.Clamp(18f + 9f * R, 20f, 220f), soot = pl.soot, shade = pl.smokeShade, scale = Mathf.Clamp(0.25f + 0.05f * R, 0.3f, 1f), tint = (byte)pl.tint,
            };
            f.lamp = AddLamp(f.x, f.y + f.r * 0.5f, f.z, f.r * 1.6f, 2.2f, pl.burn, f.tint, true, 0f, 0f);
            fires[fireCount++] = f;
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
                if (f.puff <= 0f && !vacuum && grip > 0.2f)
                {
                    f.puff = 0.667f * Mathf.Sqrt(Mathf.Max(0.5f, 2f * f.r)) * Random.Range(0.85f, 1.15f);
                    float lift = 0.34f * Mathf.Sqrt(gravity * 2f * f.r) * strength * Mathf.Min(1f, grip) * Random.Range(0.85f, 1.15f);
                    if (lift > 0.5f) AddThermal(f.x + Random.Range(-0.2f, 0.2f) * f.r, f.y + 0.8f * f.r, f.z + Random.Range(-0.2f, 0.2f) * f.r, 0.8f * f.r * Random.Range(0.85f, 1.15f), lift, f.y + f.r * Random.Range(9f, 16f), 0.5f);
                }
                while (f.due <= 0f)
                {
                    f.due += 1f;
                    int i = Add();
                    if (i < 0) break;
                    ref P o = ref p[i];
                    float turn = Random.Range(0f, Mathf.PI * 2f), out_ = f.r * Random.value * Random.value;
                    o.x = f.x + Mathf.Cos(turn) * out_; o.z = f.z + Mathf.Sin(turn) * out_;
                    o.y = hasGround ? GroundAt(o.x, o.z) + 0.2f : f.y;                       // (the fire lies on the ground, whichever way that slopes)
                    o.r = f.scale * Random.Range(0.6f, 1.2f) * (0.6f + 0.4f * strength);
                    o.ky = Random.Range(1f, 3f);
                    o.kloss = 2f; o.drag = 3f;
                    o.heat = Random.Range(0.8f, 1.02f);
                    o.cool = Random.Range(1.2f, 2.2f);
                    o.flame = 1f;
                    o.tint = f.tint;
                    // (it has some lift of its own, but it is mostly the bubbles of hot gas going up from the fire that carry it)
                    o.rise = Random.Range(2.5f, 5f) * Mathf.Sqrt(f.scale * 2f) * (0.7f + 0.45f * puff) * (vacuum || grip <= 0.2f ? 1f : 0.45f);
                    o.grow = 0.1f;
                    o.turb = 1.4f;
                    o.tile = Lumpy();
                    if (Random.value < 0.45f * Mathf.Lerp(0.5f, 1f, f.soot))
                    {
                        float later = o.r * 2.2f;
                        o.mass = Random.Range(1f, 2.4f) * Mathf.Max(0.15f, f.soot) * later * later;
                        o.life = Random.Range(10f, 26f) * lasts;
                        float tone = f.shade * Random.Range(0.8f, 1.4f);
                        o.ar = tone; o.ag = tone * 0.97f; o.ab = tone * 0.93f;
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
                Vector3 way = Random.onUnitSphere;
                // (away from the ground, as the ground lies; in the air, upward)
                float off = Vector3.Dot(way, blastUp);
                way += blastUp * (Mathf.Abs(off) * Random.Range(0.4f, 1f) + (onGround ? 0.25f : 0f) - off);
                way.Normalize();
                float speed = Mathf.Min(120f, Mathf.Sqrt(2f * g * R * Random.Range(0.8f, 3.2f)) + 3f);
                frags[fragCount++] = new Frag
                {
                    x = c.x, y = c.y + 0.3f, z = c.z, vx = v0.x * 0.7f + way.x * speed, vy = v0.y * 0.7f + way.y * speed, vz = v0.z * 0.7f + way.z * speed,
                    life = Mathf.Clamp(2f * speed / g + 1.5f, 3f, 14f), size = Mathf.Clamp(0.16f + 0.03f * R, 0.2f, 0.6f) * Random.Range(0.7f, 1.3f),
                    tint = (byte)pl.tint, white = pl.whiteTrails,
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
                        // It lands and burns out where it lies.
                        f.y = GroundAt(f.x, f.z) + 0.1f;
                        f.down = true;
                        f.vx = f.vy = f.vz = 0f;
                        f.life = Mathf.Min(f.life, f.age + Random.Range(1.5f, 5f));
                    }
                }
                f.due -= dt;
                while (f.due <= 0f)
                {
                    f.due += f.down ? every * 2.5f : every;
                    if (!Trail(f.x, f.y, f.z, f.vx, f.vy, f.vz, f.size, f.tint, f.white, lasts)) break;
                }
            }
        }

        /// <summary>One puff of the trail a burning thing leaves: flame that turns to smoke where it was.</summary>
        bool Trail(float x, float y, float z, float vx, float vy, float vz, float size, byte tint, bool white, float lasts)
        {
            int i = Add();
            if (i < 0) return false;
            ref P o = ref p[i];
            o.x = x + Random.Range(-0.1f, 0.1f); o.y = y + Random.Range(-0.1f, 0.1f); o.z = z + Random.Range(-0.1f, 0.1f);
            o.vx = vx * 0.12f + Random.Range(-0.5f, 0.5f); o.vy = vy * 0.12f + Random.Range(-0.5f, 0.5f); o.vz = vz * 0.12f + Random.Range(-0.5f, 0.5f);
            o.r = size * Random.Range(0.8f, 1.4f);
            o.heat = Random.Range(0.7f, 0.95f);
            o.cool = 3f;
            o.flame = 1f;
            o.tint = tint;
            o.tile = Random.value < 0.3f ? Wispy() : Lumpy();
            if (vacuum)
            {
                o.flags = Ballistic;
                o.grow = 1.5f;
                o.life = 0.8f;
            }
            else
            {
                o.drag = 5f; o.kloss = 4f;
                o.grow = 0.06f;
                o.turb = 0.8f;
                o.rise = 0.6f;
                o.mass = Random.Range(0.6f, 1.3f) * o.r * o.r * 4f;
                o.life = Random.Range(2.5f, 6f) * lasts;
                float tone = white ? Random.Range(0.45f, 0.7f) : Random.Range(0.03f, 0.07f);
                o.ar = tone; o.ag = tone; o.ab = tone;
            }
            Born(i);
            return true;
        }

        /// <summary>The glare of the fires, and the burning pieces themselves: a handful of soft lights drawn behind and in front of the cloud.</summary>
        void Glows()
        {
            int n = 0;
            for (int k = 0; k < lampCount && n < glows.Length; k++)
            {
                if (lamps[k].quiet) continue;
                int at = (lamps[k].tint * 6 + 3) * 3;
                float v = Mathf.Min(1f, lamps[k].power * 0.3f) * 255f;
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
            ballCount = jetCount = 0;
            if (!Settings.Push || count == 0 || vacuum || space) return;
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
        }
    }
}
