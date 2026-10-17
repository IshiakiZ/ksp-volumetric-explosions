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
        const float ForShow = -1e6f;                  // (in place of when it is to be gone by, for a puff of young smoke whose trail is laid straight: it is shown for as far as it goes, see Hand)
        const byte Kept = 128;                      // (a puff of young smoke that is to go on to the trail, with the smoke of those that are not; and, in the trail, one that came so: see YoungSmoke and Hand)

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
        Vector3 youngRim;                         // where the nozzles that put young smoke in here are, on the whole (in this patch's axes)
        bool youngShown;                          // there is young smoke here whose trail is laid straight into the air: it gives way to that trail along a stretch behind the nozzles (see Hand, and TuneCuts)

        /// <summary>How far behind its nozzle an engine's smoke is its ship's patch's to draw (see YoungSmoke: handed on from there, or giving way from there to the trail laid for it).</summary>
        public static float YoungFar(in Exhaust e) => Mathf.Clamp(1.7f * Mathf.Max(e.flame, 4f * e.nozzle), 22f, 90f);
        public float HandFar => handFar;

        /// <summary>
        /// Whether an engine's trail is laid straight into the air it is leaving it in, ahead of the nozzle (see Vents), its
        /// young smoke in its ship's patch being only what is seen of it near the ship: so, where it is going at any speed
        /// with its jet pointing back the way it has come. A patch's grid takes a few frames to make, and is a tenth of a
        /// second old or more by the time it is seen. Standing on the pad that is nothing, and the young smoke is handed on
        /// to the air as copies of itself (see Hand), each coming on as the puff it copies fades. At six hundred metres a
        /// second it is sixty metres of trail: the copies could not be seen until the puffs they were copies of had long
        /// gone, and the trail began after a gap. The trail laid straight is laid a little ahead of the nozzle, so that
        /// every grid has it in time, and where it begins is told to the shader every frame (see Wants); the young smoke
        /// gives way to it along the same stretch (see TuneCuts, and begun in volume.glsl).
        /// </summary>
        public static bool LaidStraight(in Exhaust e)
        {
            double speed = e.going.magnitude;
            return speed > LaidFrom && Vector3d.Dot(e.along, e.going) < -0.5 * speed;
        }
        const float LaidFrom = 25f;               // metres a second

        /// <summary>What this patch handed on in its last step, for Air to give to the patch that holds the ship's trail.</summary>
        public List<Handoff> Handing => handing;
        public float HandNozzle => handNozzle;
        /// <summary>The patch this one last gave its smoke to (see Air.Deliver).</summary>
        public Site TrailSite;
        /// <summary>How fast the ship this patch goes along with is going, in the scene's axes.</summary>
        public Vector3d ShipGoing => frame.east * ridesGoing.x + frame.up * ridesGoing.y + frame.north * ridesGoing.z;

        /// <summary>
        /// How much of an engine's smoke there is to see, for the air it comes out into (1 at the ground). What a rocket
        /// puts out is the same at any height, but what makes a trail of it is the air: it holds the smoke together and
        /// makes cloud of its steam, and as the air thins the same smoke is spread through more and more room and is a
        /// thin veil, then nothing. A solid motor's, which is grit and not steam, goes on being seen far higher than a
        /// liquid engine's: on Kerbin a liquid engine's is a quarter of itself by fifteen kilometres and gone by thirty, a
        /// solid's a fifth at twenty-five. (Made as thick at every height and only let go the sooner, as until 2026-10-10,
        /// it was a solid cone behind a rocket twenty kilometres up, and wider than the flame it came from.)
        /// </summary>
        public static float Seen(float air, float amount)
        {
            float left = 1.25f * Mathf.Pow(Mathf.Max(air, 0f), Mathf.Lerp(0.8f, 0.4f, Mathf.Clamp01(amount)));
            return left < 0.02f ? 0f : Mathf.Min(left, 1f);
        }

        /// <summary>(Metres a second: a rocket's trail is laid no wider for going faster than this. It is laid wider the faster the rocket goes, four and a half times as wide by here: left to go on so, it was eleven times as wide at orbital speed.)</summary>
        public const float WidestAt = 700f;

        /// <summary>
        /// How much smoke an engine makes, a second, in the units of a puff's mass (its mass is how much it hides over its
        /// width): as much as the trail it used to lay (see Vents: so many puffs a second of so much each).
        /// </summary>
        public static float SmokeMade(in Exhaust e, float speed, float q, out float laid, out float wide)
        {
            wide = e.nozzle * 2.1f * (1f + Mathf.Min(speed, WidestAt) / 200f);
            float standing = (26f + 34f * Mathf.Sqrt(e.amount)) * e.running;
            laid = Mathf.Max(speed / (0.6f * wide), standing) * q * (0.45f + 0.55f * Mathf.Sqrt(e.amount));
            // (each puff 0.7 to 1.4 times six times the amount times its width squared, and its width 0.75 to 1.25 times the jet's: on average 6.4 times)
            return laid * 6.4f * e.amount * wide * wide;
        }

        /// <summary>
        /// What an engine's young smoke comes to once its push is spent, going at this speed: how wide it is, and how far
        /// from the nozzle's own way through the air it lies, along the jet. Reckoned in one place for the two that must
        /// agree about it: the ship's patch, which makes the young smoke (see YoungSmoke), and the patch of air that is laid
        /// the trail the young smoke gives way to (see Vents), which is laid there, that wide, from the first. (Laid as an
        /// engine's trail used to be, as wide as the jet at the flame's end and thrown on from there to swell, it lay ten
        /// metres and more to one side of the young smoke behind a rocket flying aslant, and was half as wide again.)
        /// </summary>
        void YoungEnds(in Exhaust e, Vector3 along, Vector3 going, float wide, float far, out float widest, out float thrown)
        {
            float length = Mathf.Max(e.flame, 4f * e.nozzle);
            float jet = Mathf.Max(6.25f * length, 60f), thin = Thin;
            float lasts = length * Mathf.Lerp(1f, 0.45f, thin) / jet, slows = 0.375f / lasts;
            float travels = 0.85f * jet / (slows * Mathf.Max(grip, 0.55f));
            // (a little wider than the trail's puffs used to be laid, and wider again where the air is thin and there is less
            // to hold the jet in; nor ever less than a jet widens standing still, by six parts in a hundred of the way it goes)
            widest = Mathf.Max(1.4f * wide * (1f + 0.2f * thin), 0.75f * e.nozzle + 0.06f * travels);
            float shipSpeed = going.magnitude;
            // (as its puffs go on the whole: see YoungSmoke, where each has its own speed by where across the jet it is)
            float speed = 0.72f * jet;
            float faster = Mathf.Max(1f, (along * speed - going).magnitude / speed);
            thrown = travels / (faster * Sooner(travels, far, shipSpeed));
        }

        /// <summary>How many times sooner young smoke spends its push, and does its widening, for the ship's going at speed: so that it has done both by where its trail takes over from it (see YoungSmoke).</summary>
        static float Sooner(float travels, float far, float shipSpeed) =>
            Mathf.Lerp(1f, Mathf.Max(1f, 3f * travels / Mathf.Max(far, 1f)), Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LaidFrom, 120f, shipSpeed)));

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
            float seen = Seen(air, e.amount);
            if (seen <= 0f) return;
            float made = SmokeMade(e, shipSpeed, q, out float laid, out float wide) * seen;
            // How fast the puffs come apart where they come to show: they leave the nozzle at the jet's speed, and the air they
            // are put out into goes back past the ship at the ship's. As many a second as keep each within a fifth of its
            // width of the next there.
            float apart = 0.6f * jet + shipSpeed;
            // (Within a fifth of its width, not half: the grid of a ship's patch is fine enough to draw each puff as the ball it
            // is, and at half its width apart the column under the flame was a string of beads.)
            float rate = Mathf.Clamp(apart / (0.2f * wide), 0.8f * laid, 700f * Mathf.Max(q, 0.3f));
            if (rate <= 0f) return;
            // (One puff in so many goes on to the trail, with the smoke of them all in it: which, is settled as they are made,
            // in turn. Settled by chance as they were handed on, here two together went and there none, and the trail began in lumps.)
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
            // How wide it gets, and over how far. Its push takes it so far through still air (the mover takes the push away at
            // this rate, see RunChunk), and over that way it widens, as a jet does, to a width it then keeps but for what
            // mixing adds: a little wider than the trail's puffs used to be laid, and wider again where the air is thin and
            // there is less to hold the jet in; nor ever less than a jet widens standing still, by six parts in a hundred of
            // the way it goes. (Until 2026-10-10 it widened by so much a metre for as long as its push lasted, and in thin air
            // by a fifth of a metre more: behind a fast rocket high up, where the trail is laid wide and the push lasts
            // long, each puff swelled to four times the trail's width within a rocket's length of the flame.)
            float travels = 0.85f * jet / (slows * Mathf.Max(grip, 0.55f));
            YoungEnds(e, along, going, wide, handFar, out float widest, out _);
            float swell = (widest - r0) / travels - 0.02f;
            // Whether its trail is laid straight into the air (see LaidStraight). Then what is made here is for show: none of
            // it goes on to the trail; it is all this patch's until it gives way to the trail (see Hand). And so that it is
            // as wide by then as the trail it gives way to, it spends its push and does its widening sooner the faster the
            // ship goes: within the way to where the trail begins, at speed, not within three times that.
            bool straight = LaidStraight(e);
            float sooner = Sooner(travels, handFar, shipSpeed);
            // The air this is put out into, as this patch has it: going back past the ship at the ship's speed.
            Vector3 past = -going;
            Vector3 across = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(along, across);
            for (int k = 0; k < puffs; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                // (in turn from a list that fills the stretch and the nozzle's mouth evenly, as the flame's gas is: see Flame.
                // Placed by chance, the few puffs of a frame bunched, and the column began as a row of balls.)
                int nth = vent.puffed++ & 0xFFFFF;
                float since = (k + (float)(nth * 0.8191725133961645 % 1.0)) / puffs * span;           // how long ago within that stretch it left
                float turn = (float)(nth * 0.6710436067037893 % 1.0) * Mathf.PI * 2f, out_ = Mathf.Sqrt((float)(nth * 0.5497004779019703 % 1.0));
                Vector3 side = across * Mathf.Cos(turn) + over * Mathf.Sin(turn);
                // (It is made in the jet's outer layers, which are slower than its middle. How fast goes by where across the
                // jet it is and by next to nothing else: each given a speed of its own by chance, a tenth either way, the
                // puffs overtook one another, and within a few metres lay as chance would have laid them, here four in a
                // metre and there none: a column of lumps as wide as itself.)
                float speed = jet * 0.85f * (1f - 0.3f * out_ * out_) * Random.Range(0.98f, 1.02f);
                Vector3 c = rim + side * (out_ * 0.9f * e.nozzle);
                Vector3 push = along * speed + side * (out_ * speed * 0.08f);
                // It is the air's from the first: what a rocket puts out is brought to rest in the air it is put out into
                // within a flame's length or two, and what is seen of it from the ship goes back past the ship at the ship's
                // own speed, and its push besides. (Until 2026-10-10 it left as the ship's, going along with it, for the air to
                // take up as it would any smoke: which thin air does slowly. Behind a rocket twenty kilometres up the smoke
                // of the last second kept up with the rocket, ten times as much of it to a metre there as in the trail it
                // thinned out into, and swelling: a dome on the end of the flame.) So it covers the ground from the nozzle
                // this many times faster than its push alone would take it; and all that it does on the way, it does that
                // many times sooner: it has cooled, and its push is spent, and it is as wide, as far from the nozzle at any
                // speed as standing still. The smoke comes to show where the flame ends, however fast the rocket goes.
                float faster = Mathf.Max(1f, (past + push).magnitude / speed);
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.vx = past.x; o.vy = past.y; o.vz = past.z;
                o.kx = push.x; o.ky = push.y; o.kz = push.z;
                o.kloss = slows * faster * sooner; o.drag = 3.5f;
                o.r = r0 * Random.Range(0.85f, 1.15f);
                o.grow = 0.5f * wide;
                o.swell = (0.02f + swell) * faster * sooner - 0.02f;
                o.turb = (2f + 6f * out_ * out_) * (1f - 0.6f * thin);
                o.rise = Random.Range(0.5f, 1.8f);
                o.heat = Random.Range(0.95f, 1.05f);
                o.cool = cools * faster * Random.Range(0.85f, 1.15f);
                o.mass = each * Random.Range(0.7f, 1.3f);
                o.life = stays * Random.Range(0.6f, 1.3f);
                float tone = e.shade * Random.Range(0.82f, 1.22f);
                o.ar = tone; o.ag = tone * (1f - 0.05f * e.warm); o.ab = tone * (1f - 0.14f * e.warm);
                o.tint = (byte)Mathf.Clamp(e.tint, 0, FireKinds - 1);
                o.flags = Exhausted;
                if (straight)
                {
                    // (its place in the trail is taken already: marked as handed on from the first, with no time set for it to be gone by)
                    o.flags |= HandedOn;
                    o.fadeIn = ForShow;
                }
                else
                {
                    vent.kept += 1f / handThin;
                    if (vent.kept >= 1f) { vent.kept -= 1f; o.flags |= Kept; }
                }
                // (where, and how, it would have got to by now: see Since)
                Since(ref o, since, past);
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
        void Hand(float dt)
        {
            handing.Clear();
            if (!riding || !frame.Riding || handFar <= 0f) return;         // (once the ship has gone this patch stays where it is, and keeps what it has)
            float far2 = handFar * handFar;
            Vector3 by = ridesGoing;
            // (The game moves a ship fifty times a second whatever the frame rate: in one frame it has gone on by one such
            // step, in the next by two, or by none. The smoke in this patch goes with the ship, so here that shows in
            // nothing; but its copies stay in the air, and put down by where the ship stood at each frame they were laid in
            // lots that overlapped here and left a gap there, eight metres either way at six hundred metres a second. So the
            // ship is taken to be where it would be had it moved evenly: on from its last step by as long as this frame is past
            // it, less half a step, so that the copies are as often ahead of the smoke they are copies of as behind.)
            Vector3d even = ShipGoing * (Mathf.Clamp(Time.time - Time.fixedTime, 0f, Time.fixedDeltaTime) - 0.5f * Time.fixedDeltaTime);
            Vector3 back = by.sqrMagnitude > 1f ? -by.normalized : Vector3.zero;
            int forShow = 0;
            for (int i = 0; i < count; i++)
            {
                ref P q = ref p[i];
                if ((q.flags & HandedOn) != 0 && q.fadeIn < 0.5f * ForShow && q.life >= 0f)
                {
                    // A puff whose trail is laid straight into the air: it gives way to that trail by where it is, not by when
                    // (the shader is told where, every frame: see TuneCuts), and is let go once all of it is past where
                    // the trail shows whole: so far behind the nozzles, along the way the ship has come.
                    float behind = (q.x - youngRim.x) * back.x + (q.y - youngRim.y) * back.y + (q.z - youngRim.z) * back.z;
                    if (behind - 1.5f * q.r > 2f * handFar + 2f) { q.life = -1f; continue; }
                    forShow++;
                    continue;
                }
                if (q.mass <= 0f || q.life < 0f || (q.flags & HandedOn) != 0 || q.age < 0.02f) continue;
                if (q.x * q.x + q.y * q.y + q.z * q.z < far2 && q.age < 2.5f) continue;
                // (over as long as it takes to go as far again at the speed it is going past the ship: a stretch of the column as
                // long as the one before it, however fast that is)
                float sx = q.vx + q.kx, sy = q.vy + q.ky, sz = q.vz + q.kz;
                float cross = Mathf.Clamp(handFar / Mathf.Max(Mathf.Sqrt(sx * sx + sy * sy + sz * sz), 1f), 0.04f, 1.2f);
                float left = q.life - q.age;
                q.flags |= HandedOn;
                // (When it is gone from here, kept where a puff still coming on keeps how long that takes: the mover fades it out
                // by this, evenly. Given so much longer to live instead, as at first, it faded twice over at the end, as
                // everything does in the last fifth of its life, and the two together were thin half-way.)
                q.fadeIn = -(q.age + cross);
                if (left <= cross || (q.flags & Kept) == 0) continue;
                var l = new Handoff { p = q };
                // (Where it will be when its copy is first there to be moved, which is a frame from now: this frame's move on
                // from here. Begun where the puff is now, the copy was a frame behind it for good, tens of metres at speed and
                // not the same from frame to frame; and a frame behind it in coming on, so that the two together were less
                // than either all the way.)
                l.at = body.GetRelSurfacePosition(frame.ToWorld(new Vector3(q.x + sx * dt, q.y + sy * dt, q.z + sz * dt)) + even);
                // (Over the ground it is going as the air takes it here plus the ship's own way, which this patch goes along with.)
                l.going = body.GetRelSurfaceDirection(frame.east * (q.vx + by.x) + frame.up * (q.vy + by.y) + frame.north * (q.vz + by.z));
                l.push = body.GetRelSurfaceDirection(frame.east * q.kx + frame.up * q.ky + frame.north * q.kz);
                l.p.life = left; l.p.age = dt; l.p.fadeIn = cross;
                l.p.mass = q.mass * handThin;
                l.p.flags = Exhausted | Kept;
                handing.Add(l);
            }
            youngShown = forShow > 0;
        }

#if DEV
        /// <summary>(Every puff of smoke of this patch, a line each: how far behind a place it is along a line, how far off the line, how wide, how much, how hot, how old; for looking at how evenly a ship's smoke is laid. See Air.Column.)</summary>
        public void Column(System.Text.StringBuilder say, int nth, Vector3d from, Vector3d back)
        {
            frame.Refresh();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < count; i++)
            {
                if (p[i].mass <= 0f || p[i].life < 0f) continue;
                Vector3d off = frame.ToWorld(new Vector3(p[i].x, p[i].y, p[i].z)) - from;
                double behind = Vector3d.Dot(off, back);
                say.Append(string.Format(inv, "{0} {1} {2:F2} {3:F2} {4:F3} {5:F5} {6:F3} {7:F3} {8:F3} {9:F3} {10} {11:F1} {12:F2}\n", nth, riding ? 1 : 0, behind, (off - back * behind).magnitude, p[i].r, p[i].mass, p[i].heat, p[i].age, p[i].life, p[i].fadeIn, p[i].flags,
                                         Mathf.Sqrt((p[i].vx + p[i].kx) * (p[i].vx + p[i].kx) + (p[i].vy + p[i].ky) * (p[i].vy + p[i].ky) + (p[i].vz + p[i].kz) * (p[i].vz + p[i].kz)), time - shownTime));
            }
        }

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
                        at = AddLamp(glowAt.x, glowAt.y, glowAt.z, glows, strength, 0.6f, (byte)Mathf.Clamp(pad.tint, 0, FireKinds - 1), true, 0f, 0f);
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
                        o.tint = (byte)Mathf.Clamp(pad.tint, 0, FireKinds - 1);
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
