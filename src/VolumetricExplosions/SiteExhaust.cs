using UnityEngine;
using Random = UnityEngine.Random;

namespace VolumetricExplosions
{
    /// <summary>
    /// What a running rocket engine is putting into the air this frame. Whoever knows of the engine says it
    /// afresh every frame (see <see cref="Sources"/>); the air it is said to keeps no more of it than where
    /// it was the frame before.
    /// </summary>
    public struct Exhaust
    {
        public int id;                 // the same number every frame for the same nozzle
        public Vector3d rim;           // the middle of the nozzle's mouth, in the scene's axes
        public Vector3d along;         // which way the jet goes, one long
        public Vector3d going;         // how the nozzle is moving over the ground, metres a second, in the scene's axes (a vessel's srf_velocity)
        public float nozzle;           // the mouth's radius, metres
        public float flame;            // the length of its flame, metres: its smoke begins towards the end of that
        public float running;          // 0 to 1: how hard it is burning
        public float amount;           // of smoke: 1 is a solid booster's, 0 none
        public float shade;            // how light the smoke is: 0.03 soot, 0.85 steam
        public float warm;             // how brown the smoke is: 0 grey, 1 the brown of burnt kerosene
        public float lasts;            // seconds the smoke hangs, in thick still air
        public int tint;               // the flame's colours, as a blast's: 0 fuel, 1 solid propellant, 2 monopropellant, 4 hydrogen
        public float fire;             // how bright its flame is, drawn as burning gas: 0 none of it (the smoke alone), 1 a kerosene engine's at full thrust
        public Transform rides;        // what the nozzle is on (its ship): the flame is worked out in a patch of air that goes along with this. Without it there is no flame.
    }

    /// <summary>
    /// The way in for smoke that is not an explosion's: a mod that knows what is burning (rocket engines, for
    /// one) says so here, and the same air that carries an explosion's smoke carries it, lights it and draws it.
    /// </summary>
    public static class Sources
    {
        /// <summary>Raised once a frame in flight, at the moment the air can take new smoke: answer it by calling Burning for each thing that is.</summary>
        public static event System.Action Asked;

        /// <summary>Whether smoke said here will be drawn at all: the mod is running, and can draw smoke as a volume. (As flat sprites a rocket's smoke would be no better than the game's own.)</summary>
        public static bool Ready => Air.Instance != null && Assets.Volume != null && Settings.Volume;

        /// <summary>Whether it was taken: not where there is no air to hold smoke, nor when there is no room left.</summary>
        public static bool Burning(in Exhaust exhaust) => Air.Instance != null && Air.Instance.Take(exhaust);

        internal static void Ask()
        {
            if (Asked == null) return;
            // (each by itself: one mod's fault is not to leave another's engines without their smoke)
            foreach (System.Delegate one in Asked.GetInvocationList())
            {
                try { ((System.Action)one)(); }
                catch (System.Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] a source of smoke failed: " + ex);
                    complained = true;
                }
            }
        }
        static bool complained;
    }

    /// <summary>The third part of a Site: smoke that is fed into it as its source goes by.</summary>
    public sealed partial class Site
    {
#if DEV
        GameObject marker;
        /// <summary>(Where this patch last took each of its vents' nozzles to be, in its own axes.)</summary>
        public string VentsNow()
        {
            var say = new System.Text.StringBuilder();
            for (int n = 0; n < ventCount; n++) say.Append(vents[n].id).Append(" at ").Append(vents[n].last.ToString("F1")).Append(" (seen ").Append((time - vents[n].seen).ToString("F2")).Append(" s ago); ");
            return say.ToString();
        }
#endif
        struct Vent { public int id, lamp; public Vector3 last, laid; public float due, fire, seen, fed; public bool begun; }
        // A grid shows the smoke as it was when the grid was begun, and is in use from a frame or two after that until the
        // next is ready: on average this long after. A rocket doing three hundred metres a second goes twenty metres in
        // that time, and its smoke used to begin that far behind it, and further. So an engine's smoke is laid ahead of
        // it, along the way it is about to go, each puff with a time still to wait before the nozzle gets to it (an age
        // below nothing); and a grid takes each new puff to be as old as it will be when the grid is seen (see Relight).
        //
        // That is right on average and wrong at every moment: the head of the trail stood still for the life of a grid
        // and then jumped, while the rocket went smoothly on, and at speed it was now beside the rocket and now a
        // length behind. So where an engine is going fast a grid is given all the smoke laid ahead of it, the whole
        // of LaidAhead, and what is drawn of that is cut off where the engine is, which the shader is told every
        // frame (see Cuts, and "begun" in volume.glsl).
        const float GridLate = 0.045f, LaidAhead = 0.13f;
        const int MostVents = 64;        // nozzles that one patch can be fed by at once (a ship of clusters has dozens)
        readonly Vent[] vents = new Vent[MostVents];
        readonly Exhaust[] fed = new Exhaust[MostVents];
        readonly bool[] passing = new bool[MostVents];
        int ventCount, fedCount;
        /// <summary>Where the smoke of an engine (or of several side by side) may be drawn from: behind this place on the line the engine is flying along.</summary>
        struct Cut { public Vector3 from, back; public float wide, soft, far, much; }
        readonly Cut[] wants = new Cut[MostVents], cuts = new Cut[2];
        int wantCount, cutCount;
        float laidEarly = GridLate;      // how much older than it is a grid takes an engine's newest smoke to be
        Vector3 ventFrom;                // where exhaust was first fed into this patch of air
        float fedSpeed;                  // how fast the fastest of the engines feeding it was going, the last time any was
        bool vented, leaving;
        float fedAt = -100f;

#if DEV
        /// <summary>(For the development build: where the newest puffs are against the nozzle that laid them, and where this patch's box is drawn.)</summary>
        public string Trail(Vector3d nozzleNow)
        {
            frame.Refresh();
            Vector3 rim = frame.ToLocal(nozzleNow);
            var say = new System.Text.StringBuilder();
            say.Append("the nozzle is at ").Append(rim.ToString("F1")).Append(" in this patch; began to be fed at ").Append(ventFrom.ToString("F1")).Append("; ").Append(count).Append(" puffs; the box is drawn at ")
               .Append(box != null ? box.transform.localPosition.ToString("F1") : "nowhere").Append(" and is ").Append(box != null ? box.transform.localScale.ToString("F1") : "").Append(", gone along ").Append(slid.ToString("F1"))
               .Append(", drift ").Append(shownDrift.ToString("F1")).Append(", the grid is ").Append((time - shownTime).ToString("F3")).Append(" s old; the root is at ").Append(((Vector3d)t.position - frame.origin).magnitude.ToString("F2")).Append(" m from where the frame says");
            int shown = 0;
            for (int i = count - 1; i >= 0 && shown < 10; i -= Mathf.Max(1, count / 10), shown++)
                say.Append("\n  puff ").Append(i).Append(": ").Append((new Vector3(p[i].x, p[i].y, p[i].z) - rim).ToString("F1")).Append(" from the nozzle, age ").Append(p[i].age.ToString("F2")).Append(", going ")
                   .Append(new Vector3(p[i].vx + p[i].kx, p[i].vy + p[i].ky, p[i].vz + p[i].kz).ToString("F1")).Append(", r ").Append(p[i].r.ToString("F1"));
            return say.ToString();
        }
#endif

        /// <summary>Whether this patch of air has ever been fed an engine's smoke, and how long ago it last was.</summary>
        public bool Vented => vented;
        public float Unfed => time - fedAt;

        /// <summary>
        /// Whether an engine's smoke at this place belongs in this patch of air. A patch goes on being fed for so
        /// far from where it began to be, and no further: its grid has the same number of cells however long the
        /// trail in it, and a trail many kilometres long in one grid would be drawn in cells the size of houses. So
        /// a rocket leaves its smoke in one patch after another: each takes what the engine leaves in three and
        /// a half seconds at the speed it is going, so that however fast it goes there are only so many of them
        /// before the smoke of the first has thinned away. (On the ground a patch is fed for a shorter way: the cloud
        /// a launch leaves on the pad is wide, and the foot of the column going up from it wants a grid of its own.)
        /// </summary>
        public bool Feeds(CelestialBody where, Vector3d world)
        {
            if (where != body || space || vacuum || leaving || riding) return false;
            if (count > Settings.MaxParticles * 0.45f) return false;
            frame.Refresh();
            Vector3 at = frame.ToLocal(world);
            if (!vented)
            {
                // (An explosion's cloud takes in the smoke of an engine that is burning inside it.)
                return count > 0 && at.x > bx0 - 10f && at.x < bx1 + 10f && at.y > by0 - 10f && at.y < by1 + 10f && at.z > bz0 - 10f && at.z < bz1 + 10f;
            }
            bool low = hasGround && ventFrom.y - GroundAt(ventFrom.x, ventFrom.z) < 30f;
            float far = low ? 45f : Mathf.Clamp(3.5f * fedSpeed, 90f, 2500f);
            return (at - ventFrom).sqrMagnitude < far * far;
        }

        /// <summary>Whether this engine has been feeding this patch of air, or was until a moment ago.</summary>
        public bool Has(int id)
        {
            for (int n = 0; n < ventCount; n++) if (vents[n].id == id) return true;
            return false;
        }

#if DEV
        public string WhyNot(CelestialBody where, Vector3d world)
        {
            frame.Refresh();
            Vector3 at = frame.ToLocal(world);
            return (where != body ? "another body; " : "") + (space ? "space; " : "") + (vacuum ? "vacuum; " : "") + (leaving ? "leaving; " : "") + count + " puffs; " + (vented ? "" : "not an engine's; ") +
                   (at - ventFrom).magnitude.ToString("F0") + " m from where it began, fed at " + fedSpeed.ToString("F0") + " m/s, ground " + hasGround + ", " + time.ToString("F2") + " s old, last fed " + Unfed.ToString("F2") + " s ago";
        }
#endif

        /// <summary>Take what an engine is putting out this frame; the particles themselves are made in Step, between two rounds of moving them. (Not if there is no room for one more nozzle here.)</summary>
        public bool Fed(in Exhaust exhaust)
        {
            if (fedCount >= fed.Length) return false;
            if (ventCount >= vents.Length && !Has(exhaust.id)) return false;
            if (!vented)
            {
                frame.Refresh();
                vented = true;
                ventedAt = time;
                // (a cloud that is here already keeps its grid as it lies)
                if (count >= 50 || litOnce) turnDecided = true;
                ventFrom = frame.ToLocal(exhaust.rim);
                fedSpeed = (float)exhaust.going.magnitude;
                if (riding)
                {
                    // (A flame is a finer thing than the smoke it leaves: its billows are a fraction of the nozzle across.)
                    turnDecided = true;
                    FlameFamily = Family(exhaust.tint);
                    flameKind = (byte)Mathf.Clamp(exhaust.tint, 0, 7);
                    scale = Mathf.Clamp(1f + 3f * exhaust.nozzle, 1.2f, 6f);
                    detailRepeat = Mathf.Clamp(0.5f + 3.5f * exhaust.nozzle, 1.5f, 10f);
                    fullLength = Mathf.Clamp(0.5f * exhaust.nozzle, 0.3f, 2f);
                }
                else if (count < 50)
                {
                    // The size of the billows goes with the size of the jet, as a blast's does with the blast (see Burst).
                    scale = Mathf.Clamp(2f + 6f * exhaust.nozzle, 2.5f, 12f);
                    detailRepeat = Mathf.Clamp(scale * 0.55f + 3.5f, 4f, 26f);
                    fullLength = Mathf.Clamp(0.13f * scale, 0.8f, 4f);
                }
            }
            passing[fedCount] = false;
            fed[fedCount++] = exhaust;
            return true;
        }

        /// <summary>
        /// An engine that fed this patch of air a moment ago is now feeding another. The last of what it laid here
        /// was laid ahead of it, and for a few frames more it is this patch that has the head of its trail: so it
        /// has to know where the engine is for those few frames, to draw none of that before the engine gets to it.
        /// </summary>
        public void Passed(in Exhaust exhaust)
        {
            if (!vented || riding || fedCount >= fed.Length) return;
            for (int n = 0; n < ventCount; n++)
                if (vents[n].id == exhaust.id && time - vents[n].fed < 0.4f) { passing[fedCount] = true; fed[fedCount++] = exhaust; return; }
        }

        /// <summary>
        /// The smoke of each engine that is feeding this patch: a puff for every so much of the way the nozzle has
        /// come since the last frame (or so many a second, where it stands still), laid along that way, so that a
        /// rocket going at any speed leaves a trail and not a row of balls.
        ///
        /// The smoke begins where the flame ends. There it is thrown on down the jet with what push the jet has
        /// left, which the air takes away within a few lengths of the flame; after that it is the air's. Where the
        /// ground is in the way the mover turns that push along the ground (see RunChunk), and the smoke of a rocket
        /// standing on its pad goes out over the pad on every side and rolls up.
        /// </summary>
        void Vents(float dt)
        {
            for (int n = ventCount - 1; n >= 0; n--)
                if (time - vents[n].seen > 1f) vents[n] = vents[--ventCount];
            wantCount = 0;
            cutCount = 0;
            if (fedCount == 0) return;
            float fastest = -1f;
            float q = Mathf.Clamp(Settings.Quality * Air.Room(), 0.15f, 1.5f);
            Vector3 allGoing = Vector3.zero, allAlong = Vector3.zero;
            int flames = 0;
            // (thin air holds smoke together for less long, and there is less in it for the smoke to be seen against)
            float hangs = Mathf.Max(0.3f, Settings.Smoke) * Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(air * 1.4f));
            for (int f = 0; f < fedCount; f++)
            {
                Exhaust e = fed[f];
                bool passes = passing[f];
                // (How fast the nozzle is going is what its owner says, not how far it has come since the last frame over
                // how long that was. The game moves a ship fifty times a second, whatever the frame rate: at sixty frames
                // a second it does not move at all in one frame in six, and in a slow frame it moves twice. Reckoned
                // from that, a rocket doing two hundred metres a second was every few frames doing nothing, or four hundred.)
                Vector3 rim = frame.ToLocal(e.rim), along = frame.DirToLocal(e.along), going = frame.DirToLocal(e.going);
                int v = -1;
                for (int n = 0; n < ventCount; n++) if (vents[n].id == e.id) v = n;
                if (v < 0)
                {
                    if (passes || ventCount >= vents.Length) continue;
                    v = ventCount++;
                    vents[v] = new Vent { id = e.id, last = rim, lamp = -1 };
                }
                ref Vent vent = ref vents[v];
                Vector3 before = vent.begun ? vent.last : rim;
                float moved = (rim - before).magnitude;
                if (moved > 80f) { before = rim; moved = 0f; vent.begun = false; }       // (it has jumped: the game has put the craft somewhere else)
                // Which way the nozzle is going and how fast, and so where it will be when the smoke laid now is first seen.
                float speed = going.magnitude;
                if (riding)
                {
                    // A patch that goes along with the ship holds its engines' flames and nothing else: the nozzle stands
                    // still in it, and the air goes by (see the mover).
                    vent.last = rim; vent.begun = true; vent.seen = time; vent.fed = time;
                    fedAt = time;
                    allGoing += going; allAlong += along; flames++;
                    if (e.fire > 0.01f && e.running > 0.02f) Flame(ref vent, e, rim, along, e.fire, dt, q);
                    continue;
                }
                float fast = speed / 200f;
                float wide = e.nozzle * 2.1f * (1f + fast);                    // the jet's radius where the flame ends
                if (!turnDecided && !passes)
                {
                    // Which way this patch's grid is to lie: along the trail, where the engine that begins it is going fast
                    // enough to leave one, and there is no ground here for the smoke to lie on (see TurnAlong).
                    turnDecided = true;
                    if (speed > 60f && !litOnce && (!hasGround || ventFrom.y - GroundAt(ventFrom.x, ventFrom.z) > 120f)) TurnAlong(going / speed);
                }
                if (passes)
                {
                    vent.last = rim; vent.seen = time;
                    Wants(rim, along, going, speed, wide, e.flame);
                    continue;
                }
                fedAt = time;
                if (speed > fastest) fastest = speed;
                Vector3 ahead = rim + going * LaidAhead;
                Vector3 from = vent.begun ? vent.laid : ahead;
                if ((ahead - from).sqrMagnitude > 4f * (moved + 1f) * (moved + 1f) + 400f) from = ahead;      // (it has turned too sharply for what was laid to be gone on from)
                float stretch = (ahead - from).magnitude;
                vent.last = rim; vent.laid = ahead; vent.begun = true; vent.seen = time; vent.fed = time;
#if DEV
                if (Settings.TestView == 7f)
                {
                    // (for finding a fault: a red ball drawn where this patch of air takes the nozzle to be)
                    if (marker == null)
                    {
                        marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        UnityEngine.Object.Destroy(marker.GetComponent<Collider>());
                        marker.layer = Recipe.Layer;
                        marker.transform.SetParent(t, false);
                        marker.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
                    }
                    marker.transform.localPosition = rim;
                    marker.transform.localScale = Vector3.one * 3f;
                }
#endif
                if (e.amount <= 0.001f || e.running <= 0.02f) continue;

                // ---- its light on the smoke: a steady fire a third of the way down the flame. (Never under the ground: on a
                // pad the flame is turned along the pad a metre or two below the nozzle, and what lights the smoke there
                // is the sheet of fire lying on it. Put where the flame would have reached had nothing stopped it, the
                // light was in the pad, and the smoke of a launch at night came out of the ground black.)
                Vector3 fire = rim + along * (0.35f * e.flame);
                float glows = Mathf.Max(2.5f, e.nozzle * 5f);
                if (hasGround)
                {
                    float floor = GroundAt(fire.x, fire.z) + 1.5f;
                    if (fire.y < floor) { fire.y = floor; glows *= 1.6f; }
                }
                if (vent.lamp < 0 || vent.lamp >= lampCount || !lamps[vent.lamp].quiet || lamps[vent.lamp].owner != e.id)
                {
                    vent.lamp = AddLamp(fire.x, fire.y, fire.z, glows, 2.4f * e.running, 0.6f, (byte)e.tint, true, 0f, 0f);
                    lamps[vent.lamp].quiet = true;
                    lamps[vent.lamp].owner = e.id;
                }
                else
                {
                    ref Lamp lamp = ref lamps[vent.lamp];
                    lamp.x = fire.x; lamp.y = fire.y; lamp.z = fire.z; lamp.r = glows; lamp.age = 0f; lamp.peak = 2.4f * e.running;
                }
                if (e.amount >= flameWeight * 0.02f && flameWeight < 400f) { flameKind = (byte)Mathf.Clamp(e.tint, 0, 7); }

                // ---- its smoke
                // (A fast rocket's smoke is laid in wider puffs further apart: it is that much wider by the time anyone is
                // near enough to see it, and a trail laid as finely at five hundred metres a second as at fifty would be ten
                // times the puffs for the same smoke. Nor does it hang as long: it is torn apart by the speed it was left at.)
                Wants(rim, along, going, speed, wide, e.flame);
                float standing = (26f + 34f * Mathf.Sqrt(e.amount)) * e.running;
                vent.due += Mathf.Max(stretch / (0.6f * wide), dt * standing) * q * (0.45f + 0.55f * Mathf.Sqrt(e.amount));
                int puffs = Mathf.Min((int)vent.due, 48);
                vent.due -= (int)vent.due;
                float thrown = Mathf.Clamp(32f + 42f * e.nozzle, 35f, 110f) * (0.4f + 0.6f * e.running);
                float stays = e.lasts * hangs / (1f + speed / 250f);
                // (two directions across the jet)
                Vector3 across = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(along, across);
                for (int k = 0; k < puffs; k++)
                {
                    int i = Add();
                    if (i < 0) break;
                    ref P o = ref p[i];
                    float way = (k + Random.value) / puffs;                    // how far along this frame's stretch of the trail it belongs
                    float turn = Random.Range(0f, Mathf.PI * 2f), out_ = Mathf.Sqrt(Random.value);
                    Vector3 side = across * Mathf.Cos(turn) + over * Mathf.Sin(turn);
                    Vector3 on = Vector3.Lerp(from, ahead, way);
                    Vector3 c = on + along * (e.flame * Random.Range(0.45f, 0.9f)) + side * (out_ * wide * 0.8f);
                    float push_ = thrown * Random.Range(0.45f, 1.1f);
                    Vector3 push = along * push_ + side * (out_ * push_ * 0.22f);
                    bool landed = false;
                    if (hasGround)
                    {
                        // (the jet does not reach under the ground: what would begin there begins on it, still being driven down, and is turned along it)
                        float floor = GroundAt(c.x, c.z) + 0.25f * wide;
                        if (c.y < floor) { c.y = floor; landed = true; }
                    }
                    o.x = c.x; o.y = c.y; o.z = c.z;
                    o.kx = push.x; o.ky = push.y; o.kz = push.z;
                    o.kloss = 2.6f; o.drag = 3.5f;
                    // (A jet that is stopped by the ground is stirred into the air about it all at once: its smoke begins half as
                    // wide again, and there is the more of it for what the jet raises: see below.)
                    o.r = wide * Random.Range(0.75f, 1.25f) * (landed ? 1.5f : 1f);
                    o.grow = 0.5f * wide;
                    // (A jet widens as it goes, by a tenth or so of how far it has gone: it takes in the air beside it. So does
                    // each puff of it, for as long as it is still being thrown along: it is three times as wide by the time the
                    // air has stopped it.)
                    o.swell = 0.09f + Swell(along, push_, o.kloss, o.r);
                    o.turb = Mathf.Min(8f, 1.5f + 0.09f * push_ + (landed ? 2.5f : 0f));
                    // (hot, it goes up: slowly out of a trail, which the air has cooled; faster off the ground, where there is a great deal of it together)
                    o.rise = landed ? Random.Range(1.2f, 3.6f) : Random.Range(0.5f, 1.8f);
                    o.mass = Random.Range(0.7f, 1.4f) * 6f * e.amount * o.r * o.r * (landed ? 1.25f : 1f);
                    o.life = stays * Random.Range(0.6f, 1.3f);
                    o.fadeIn = 0.07f;
                    // (how long until the nozzle is where this was laid: until then it is not there to be seen)
                    o.age = speed > 1f ? -Vector3.Dot(on - rim, going) / (speed * speed) : -LaidAhead * Random.value;
                    if (o.age < -LaidAhead) o.age = -LaidAhead; else if (o.age > 0f) o.age = 0f;
                    float tone = e.shade * Random.Range(0.82f, 1.22f);
                    o.ar = tone; o.ag = tone * (1f - 0.05f * e.warm); o.ab = tone * (1f - 0.14f * e.warm);
                    if (landed && (groundBare || groundWet))
                    {
                        // Off bare ground the jet raises dust, of the ground's own colour, and off water spray: a part of every puff, and so much more in it.
                        float raised = Random.Range(0.25f, 0.6f), light = Random.Range(0.8f, 1.15f);
                        Color takes = groundWet ? new Color(0.9f, 0.92f, 0.94f) : groundDust;
                        o.ar = Mathf.Lerp(o.ar, takes.r * light, raised); o.ag = Mathf.Lerp(o.ag, takes.g * light, raised); o.ab = Mathf.Lerp(o.ab, takes.b * light, raised);
                        o.mass *= 1f + raised;
                    }
                    o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
                    o.flags = Exhausted;
                    o.tile = Random.value < 0.25f ? Wispy() : Lumpy();
                    Born(i);
                }
            }
            fedCount = 0;
            if (fastest >= 0f) fedSpeed = fastest;
            if (flames > 0)
            {
                // (the grid of the flames lies along the jets, as the ship points now: see TurnAlong)
                ridesGoing = allGoing / flames;
                if (allAlong.sqrMagnitude > 0.01f) TurnAlong(allAlong.normalized);
            }
            Cuts();
        }

        /// <summary>Whether this patch is on its way out (see Leave): it takes nothing more and no longer counts as one of the patches there is room for.</summary>
        public bool Leaving => leaving;

        /// <summary>
        /// Let what is here thin away now, within a couple of seconds, instead of when it would have. (The air holds
        /// only so many patches; when a rocket's trail needs one more than that, the far end of the trail goes, and it
        /// goes as smoke does, not all at once.)
        /// </summary>
        public void Leave()
        {
            if (leaving) return;
            leaving = true;
            for (int i = 0; i < count; i++)
            {
                if (p[i].life < 0f) continue;
                // (a puff is thinning for the last 0.22 of its life: so that it goes on from as thick as it is now, that much of its new life is still to come)
                float left = Mathf.Clamp(0.282f * p[i].age, 0.5f, 2.5f) * Random.Range(0.7f, 1f);
                if (p[i].age + left < p[i].life) p[i].life = p[i].age + left;
            }
        }

        /// <summary>
        /// An engine's flame: burning gas that leaves the nozzle fast, keeps most of its speed, widens as it goes and
        /// cools as it goes, and is spent where it has cooled. Enough of it every frame that the jet is one body of
        /// fire and not a string of beads: it goes metres in a frame, so each bit is put where it would have got to
        /// had it left at its own moment within the frame.
        ///
        /// Where the air is thin there is less to hold the jet together: it opens out into a wide pale bell and is
        /// spent in half the length, and in a vacuum nothing slows it or stirs it at all.
        /// </summary>
        void Flame(ref Vent vent, in Exhaust e, Vector3 rim, Vector3 along, float fire, float dt, float q)
        {
            float length = Mathf.Max(e.flame, 4f * e.nozzle);
            // How fast it looks to go, not how fast it goes, which is ten times this: the length of the flame in a sixth
            // of a second, and never slower than sixty metres a second, however short the flame (a small engine's flame
            // is as quick as a big one's, and shorter because it is over sooner). It keeps most of that speed to the
            // end, and the end of the flame is where it has cooled, not where it has stopped: gas that slowed to a stop
            // would spend most of its time at the far end, and the flame would be thin at the nozzle and thick at its tip.
            float jet = Mathf.Max(6.25f * length, 60f);
            float thin = Thin;                                          // 0 at the ground, 1 in a vacuum
            // (With no air to hold it in, the jet opens out at once and is spent in half the length: a wide pale bell.)
            float reach = length * Mathf.Lerp(1f, 0.45f, thin), lasts = reach / jet, slows = 0.5f / lasts;
            float opens = Mathf.Lerp(0.035f, 0.36f, thin);              // how much wider it gets for each metre it goes
            float r0 = 0.7f * e.nozzle;
            // The gas put out now fills the stretch that the gas put out last time has left behind it: as far as that was
            // moved since, which is by the length of the frame before this one, not of this one (the mover runs after
            // this, see Step). Filled by this frame's own length, every frame longer than the last overlapped it and
            // every shorter one left a gap: and one frame in three is longer, the one a grid is handed to the graphics
            // card in. A flame seen from the side was a row of three lumps.
            float span = stepDt > 1e-4f ? stepDt : dt;
            vent.fire += Mathf.Min(jet / (0.25f * r0), 1500f) * span * Mathf.Clamp(q, 0.4f, 1.2f);
            int bits = Mathf.Min((int)vent.fire, 200);
            vent.fire -= (int)vent.fire;
            if (bits <= 0) return;
            Vector3 across = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(along, across);
            if (fire >= flameWeight) { flameKind = (byte)Mathf.Clamp(e.tint, 0, 7); flameWeight = fire; }
            for (int k = 0; k < bits; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float since = (k + Random.value) / bits * span;         // how long ago within that stretch it left
                float turn = Random.Range(0f, Mathf.PI * 2f), out_ = Mathf.Sqrt(Random.value);
                Vector3 side = across * Mathf.Cos(turn) + over * Mathf.Sin(turn);
                // (the middle of a jet is fastest, and hottest)
                float speed = jet * Random.Range(0.8f, 1.1f) * (1f - 0.35f * out_ * out_);
                Vector3 c = rim + side * (out_ * 0.8f * e.nozzle) + along * (speed * since);
                Vector3 push = along * speed + side * (out_ * speed * opens * 0.6f);
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.kx = push.x; o.ky = push.y; o.kz = push.z;
                o.kloss = slows; o.drag = 2f;
                o.r = r0 * Random.Range(0.8f, 1.2f);
                // (its edge, where it meets the air, is torn by it; its middle hardly at all)
                o.turb = (3f + 9f * out_ * out_) * (1f - thin);
                o.heat = (1.02f - 0.26f * out_ * out_) * Random.Range(0.92f, 1.08f);
                o.flame = 0.17f * fire * Random.Range(0.8f, 1.2f) * (1f - 0.86f * thin);
                // (White at the nozzle, yellow down most of its length, and out through orange in the last third: in thick
                // air. Where there is little or none it hardly cools at all in the time it takes to spread to nothing.)
                o.cool = 2.1f / lasts * Random.Range(0.85f, 1.2f) * (1f - 0.75f * thin);
                o.life = Mathf.Lerp(2f, 1.25f, thin) * lasts * Random.Range(0.9f, 1.15f);
                if (vacuum)
                {
                    // (nothing slows it, and nothing stirs it: it only spreads)
                    o.flags = Ballistic | Exhausted;
                    o.grow = opens * speed;
                }
                else
                {
                    o.flags = Exhausted;
                    o.grow = 0.02f;
                    o.swell = opens - 0.02f;
                }
                o.age = since;
                o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
                o.tile = Lumpy();
                Born(i);
                // (Flame that has spread out is drawn thinner, by how much wider it is than it began: see Deposit. A jet
                // in thin air is meant to spread, and would be gone from sight a nozzle's width out: it is taken to have
                // begun as wide as the air lets it get before it pales.)
                p[i].born = o.r * Mathf.Lerp(1f, 1.9f, thin);
            }
        }

        /// <summary>
        /// Where one engine's smoke may be drawn from, this frame. What it has laid ahead of itself lies along the line
        /// it is flying, set back from that line by the length of its flame (the smoke begins between 0.45 and 0.9 of
        /// the way down the flame, see Vents); so the smoke that is the engine's by now is whatever lies further back
        /// along that line than the engine itself has come, and it comes on over as far as those beginnings are spread.
        ///
        /// Only where the engine is going fast, and away from its own smoke. A rocket standing on its pad, or coming
        /// down on its engine, is in its smoke by rights; and at a walking pace a twentieth of a second is no distance.
        /// </summary>
        void Wants(Vector3 rim, Vector3 along, Vector3 going, float speed, float wide, float flame)
        {
            if (speed < 12f || wantCount >= wants.Length) return;
            Vector3 back = going * (-1f / speed);
            float away = Vector3.Dot(along, back);                      // 1: the jet points straight back along the way it has come
            float much = Mathf.SmoothStep(0f, 1f, (speed - 12f) / 28f) * Mathf.SmoothStep(0f, 1f, (away - 0.2f) / 0.4f);
            if (much <= 0.01f) return;
            Vector3 line = rim + along * (0.675f * flame);              // a place on the line that what was laid ahead lies along
            float first = 0.45f * flame * away;                         // how far back from the nozzle the first of its smoke is
            // (how far the cells of the grid in use smear the smoke: along the trail they may be metres long)
            float cellLong = gridReady ? 1f / Mathf.Min(gix, Mathf.Min(giy, giz)) : 2f, cellWide = gridReady ? 1f / Mathf.Max(gix, Mathf.Max(giy, giz)) : 1f;
            wants[wantCount++] = new Cut
            {
                from = line + back * (first - Vector3.Dot(line - rim, back)), back = back, much = much,
                soft = Mathf.Max(first, 2f),
                wide = 3f * wide + 2f + 2f * cellWide + 0.3f * flame * Mathf.Sqrt(Mathf.Max(0f, 1f - away * away)),
                far = 1.3f * LaidAhead * speed + flame + 2.5f * cellLong,
            };
        }

        /// <summary>The engines that are side by side and going the same way, taken together: the shader is told of two places, not of every nozzle.</summary>
        void Cuts()
        {
            float early = GridLate;
            for (int a = 0; a < wantCount && cutCount < cuts.Length; a++)
            {
                if (wants[a].much <= 0f) continue;                      // (already one of a cluster)
                Cut c = wants[a];
                Vector3 from = c.from, back = c.back;
                float n = 1f, apart = 0f, least = 0f, most = 0f;
                for (int b = a + 1; b < wantCount; b++)
                {
                    if (wants[b].much <= 0f || Vector3.Dot(wants[b].back, c.back) < 0.94f) continue;
                    Vector3 d = wants[b].from - c.from;
                    float t = Vector3.Dot(d, c.back), beside = (d - c.back * t).magnitude;
                    if (beside > 14f || Mathf.Abs(t) > 30f) continue;
                    from += wants[b].from; back += wants[b].back; n++;
                    apart = Mathf.Max(apart, beside); least = Mathf.Min(least, t); most = Mathf.Max(most, t);
                    c.wide = Mathf.Max(c.wide, wants[b].wide); c.soft = Mathf.Max(c.soft, wants[b].soft);
                    c.far = Mathf.Max(c.far, wants[b].far); c.much = Mathf.Max(c.much, wants[b].much);
                    wants[b].much = 0f;
                }
                c.from = from / n; c.back = back.normalized;
                c.wide += 1.5f * apart; c.soft += most - least;
                cuts[cutCount++] = c;
                early = Mathf.Max(early, Mathf.Lerp(GridLate, LaidAhead, c.much));
            }
            laidEarly = early;
        }

        /// <summary>Tell the shader (see TuneVolume: every frame).</summary>
        void TuneCuts()
        {
            for (int n = 0; n < cuts.Length; n++)
            {
                bool any = n < cutCount;
                Cut c = any ? cuts[n] : default(Cut);
                // (The shader reckons places in the box as it stood when its grid was made: see the fires, in TuneVolume.)
                Vector3 from = ToGrid(c.from) - slid, back = ToGrid(c.back);
                boxMaterial.SetVector(n == 0 ? "_VolCutA" : "_VolCutD", any ? new Vector4(from.x, from.y, from.z, c.wide * c.wide) : Vector4.zero);
                boxMaterial.SetVector(n == 0 ? "_VolCutB" : "_VolCutE", any ? new Vector4(back.x, back.y, back.z, c.soft) : Vector4.zero);
                boxMaterial.SetVector(n == 0 ? "_VolCutC" : "_VolCutF", any ? new Vector4(c.far, c.much, 0f, 0f) : Vector4.zero);
            }
        }
    }
}
