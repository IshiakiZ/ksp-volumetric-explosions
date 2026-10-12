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
        public float fire;             // how bright its flame is, drawn as burning gas: 0 none of it (the smoke alone), 1 an engine's of its kind at full thrust (since 0.6.0: before, 1 was a kerosene engine's, whatever the kind)
        public Transform rides;        // what the nozzle is on (its ship): the flame is worked out in a patch of air that goes along with this, and so is the smoke while it is young (since 0.5.0). Without it there is no flame, and the smoke is laid straight into the air the ship leaves it in.
        public int kind;               // (since 0.5.0) what it is, one of ExhaustKind: an engine's jet, something burning with no jet, dust thrown up, a spray. Nought (unset): worked out from the rest (see ExhaustKind)
        public float reach;            // (since 0.6.3, a thermal's only) how far from where its patch of air began to be fed that patch goes on taking it, metres: the thermals of one cloud say the same, and so share one patch and one grid. Nought: 1.6 times its radius.
        internal float young;          // (set by the mod itself, see Air.Take) more than nothing: this engine's smoke is its ship's patch's to draw as far as this many metres behind the nozzle, and the trail laid for it here is to be seen only from there on
        internal bool begins;          // (the same) no patch of air has been laid this engine's trail until now: it begins at the nozzle (see Vents)
    }

    /// <summary>
    /// What a source of smoke is (Exhaust.kind), which decides how its smoke comes out (since 0.5.0). Left at Unset, as by a mod
    /// written before there was such a field, it is worked out from the rest: something that says what ship it is on, or has a
    /// flame, is an engine; something on no ship and with no flame is something burning (running 0.08 or less, its "jet" pointing
    /// up or level), dust (the same, its jet aimed into the ground), or a spray (running more).
    /// </summary>
    public static class ExhaustKind
    {
        /// <summary>Not said: worked out as above.</summary>
        public const int Unset = 0;
        /// <summary>A rocket engine's jet (or a burning leak, which is one): smoke thrown out down the jet at tens of metres a second, laid ahead of a fast nozzle and cut where it is.</summary>
        public const int Engine = 1;
        /// <summary>Something burning with no jet: a piece of wreckage on fire, a wreck burning. Its smoke goes up as a plume: hot and unseen as it leaves, coming into view as it cools, widening as it climbs. "along" is up (or up and back from the way it moves); "nozzle" is how big the fire is.</summary>
        public const int Burning = 2;
        /// <summary>Dust thrown up by something scraping along the ground: cold, low, mostly the ground's own colour, rolling out behind it. "going" is how it is moving; lay "rim" at the ground.</summary>
        public const int Dust = 3;
        /// <summary>A spray from a split tank or a burst line that is not alight: a mist driven out of the split at a few metres a second that thins away within a second or two and lies low. "along" is the way the split faces.</summary>
        public const int Spray = 4;
        /// <summary>(Since 0.6.3, for clouds made as smoke is made: Natural Weather's rework.) A thermal: air rising from a base and condensing. Its
        /// puffs are born over a disc ("nozzle" is its radius) at "rim", climb at a few metres a second (more with "running"), widen as
        /// they take in the air beside them, and stall having climbed about "flame" metres (the cloud's depth), the middle highest;
        /// they keep their thickness as they widen (water goes on condensing in rising air) and are gone after "lasts" seconds.</summary>
        public const int Thermal = 5;
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
        /// <summary>What this way in understands, for mods built against an older one to ask (not there before 0.6.0). 2: it knows for itself how thick and bright each kind of engine's flame is, and Exhaust.fire is against that kind's own usual (before, against kerosene's).</summary>
        public static int Level => 2;
        public static bool Ready => Air.Instance != null && Assets.Volume != null && Settings.Volume;

        /// <summary>Whether it was taken: not where there is no air to hold smoke, nor when there is no room left.</summary>
        public static bool Burning(in Exhaust exhaust) => Air.Instance != null && Air.Instance.Take(exhaust);

        /// <summary>What a weather mod says the wind is at a place: ten metres above the ground there (world space, metres a second), and how gusty (the typical size of a gust there, metres a second).</summary>
        public struct Breeze { public Vector3d wind; public float gusts; }

        /// <summary>
        /// A weather mod's wind (since 0.5.0): asked by each patch of air when it is made and twice a second after, on the
        /// game's thread, with the patch's place (world space) and the game's time; never for each particle. Null, as
        /// installed: the air makes up a wind of its own for each place and half hour, as it always has. (The test knob
        /// TestWind still wins over both.)
        /// </summary>
        public static System.Func<Vector3d, double, Breeze> Wind;

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
        struct Vent { public int id, lamp; public Vector3 last, laid, going, along; public float due, fire, seen, fed, kept, wide, flame, young; public bool begun; public int shot, puffed; }
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
        // (And further ahead for an engine whose trail is only seen from some way behind it, the first of its smoke being its
        // ship's patch's to draw: laid further ahead costs nothing that is seen, and a patch's first grid, or a slow one, has it in time
        // at any speed.)
        const float LaidAheadYoung = 0.26f;
        const int MostVents = 64;        // nozzles that one patch can be fed by at once (a ship of clusters has dozens)
        readonly Vent[] vents = new Vent[MostVents];
        readonly Exhaust[] fed = new Exhaust[MostVents];
        readonly bool[] passing = new bool[MostVents];
        int ventCount, fedCount;
        /// <summary>Where the smoke of an engine (or of several side by side) may be drawn from: behind this place on the line the engine is flying along.</summary>
        struct Cut { public Vector3 from, back; public float wide, soft, far, much, early; }
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
            for (int n = 0; n < cutCount; n++)
                say.Append("\n  cut ").Append(n).Append(": from ").Append(Vector3.Dot(cuts[n].from - rim, cuts[n].back).ToString("F1")).Append(" m behind the nozzle and ").Append((cuts[n].from - rim - cuts[n].back * Vector3.Dot(cuts[n].from - rim, cuts[n].back)).magnitude.ToString("F1"))
                   .Append(" m off its line, soft ").Append(cuts[n].soft.ToString("F1")).Append(", far ").Append(cuts[n].far.ToString("F0")).Append(", much ").Append(cuts[n].much.ToString("F2")).Append(", wide ").Append(cuts[n].wide.ToString("F1")).Append(", early ").Append(laidEarly.ToString("F2"));
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
        public bool Feeds(CelestialBody where, in Exhaust exhaust)
        {
            // (a cloud's patch takes thermals and nothing else, and a thermal goes into no other: an engine flown through a cloud
            // leaves its trail in a grid of its own, as fine as a trail wants)
            if ((exhaust.kind == ExhaustKind.Thermal) != cloudy) return false;
            return Feeds(where, exhaust.rim);
        }

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
            float far = cloudy ? cloudFar : low ? 45f : Mathf.Clamp(3.5f * fedSpeed, 90f, 2500f);
            return (at - ventFrom).sqrMagnitude < far * far;
        }

        /// <summary>Whether this engine has been feeding this patch of air, or was until a moment ago.</summary>
        public bool Has(int id)
        {
            for (int n = 0; n < ventCount; n++) if (vents[n].id == id) return true;
            return false;
        }

        /// <summary>Whether this patch has been laid this nozzle's trail within the last quarter of a second.</summary>
        public bool Laying(int id)
        {
            for (int n = 0; n < ventCount; n++) if (vents[n].id == id && time - vents[n].fed < 0.25f) return true;
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
            if (exhaust.kind == ExhaustKind.Thermal)
            {
                cloudy = true;
                cloudFar = Mathf.Max(cloudFar, exhaust.reach > 0f ? exhaust.reach : 1.6f * exhaust.nozzle);
            }
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
                    Flamed(exhaust);
                }
                else if (count < 50)
                {
                    // The size of the billows goes with the size of the jet, as a blast's does with the blast (see Burst).
                    scale = Mathf.Clamp(2f + 6f * exhaust.nozzle, 2.5f, 12f);
                    detailRepeat = Mathf.Clamp(scale * 0.55f + 3.5f, 4f, 26f);
                    fullLength = Mathf.Clamp(0.13f * scale, 0.8f, 4f);
                }
            }
            // (A ship's patch begun for its young smoke takes on the look of the first flame it is given: see Air.TakeFlame.)
            else if (riding && exhaust.fire > 0.01f && !hasFlame) Flamed(exhaust);
            if (riding && exhaust.fire > 0.01f) hasFlame = true;
            passing[fedCount] = false;
            fed[fedCount++] = exhaust;
            return true;
        }

        /// <summary>A ship's patch: the colours of its flames, and billows as fine as a flame's.</summary>
        void Flamed(in Exhaust exhaust)
        {
            FlameFamily = Family(exhaust.tint);
            flameKind = (byte)Mathf.Clamp(exhaust.tint, 0, 7);
            scale = Mathf.Clamp(1f + 3f * exhaust.nozzle, 1.2f, 6f);
            detailRepeat = Mathf.Clamp(0.5f + 3.5f * exhaust.nozzle, 1.5f, 10f);
            // (how much smoke counts as thick, as in the trail the ship's young smoke is handed on to: see Adopt. Thinner than
            // that here, the same smoke was wispier beside the ship than behind it, and the place it was handed on showed.)
            fullLength = Mathf.Clamp(0.13f * Mathf.Clamp(2f + 6f * exhaust.nozzle, 2.5f, 12f), 0.8f, 4f);
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
            if (fedCount == 0) { if (Lingering()) Cuts(); return; }
            float fastest = -1f;
            float q = Mathf.Clamp(Settings.Quality * Air.Room(), 0.15f, 1.5f);
            Vector3 allGoing = Vector3.zero, allAlong = Vector3.zero, youngRims = Vector3.zero;
            int flames = 0, youngCount = 0;
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
                    // (where each nozzle's mouth is, for the shock diamonds that stand in its jet: see TuneJet)
                    if (e.fire > 0.01f && e.running > 0.02f && jetMouths < jetMouth.Length) jetMouth[jetMouths++] = new Vector4(rim.x, rim.y, rim.z, e.nozzle);
                    // (something burning on the ship, its owner says, not an engine's jet: a fire on it, see Blaze; its smoke goes
                    // straight into a patch that stays where it is, see Air.Take)
                    bool burns = e.kind == ExhaustKind.Burning;
                    if (e.fire > 0.01f && e.running > 0.02f)
                    {
                        if (burns) Blaze(ref vent, e, rim, along, e.fire, dt, q);
                        else
                        {
                            Flame(ref vent, e, rim, along, -going, e.fire, dt, q);
                            // (the strongest of the ship's flames gives the pattern its place and its size: see TuneJet)
                            float strength = e.fire * e.nozzle;
                            if (strength > jetStrongest) { jetStrongest = strength; jetFromL = rim; jetNozzle = e.nozzle; jetLong = Mathf.Max(e.flame, 4f * e.nozzle); jetSpeed = Mathf.Max(6.25f * jetLong, 60f); jetKind = e.tint; }
                        }
                    }
                    // (and the smoke that the flame turns into, while it is young, lit by the flame: see SiteYoung)
                    if (e.amount > 0.001f && e.running > 0.02f && !vacuum && !burns) { YoungSmoke(ref vent, e, rim, along, going, dt, q); VentLamp(ref vent, e, rim, along); youngRims += rim; youngCount++; }
                    else if (burns && !vacuum) VentLamp(ref vent, e, rim, along);
                    continue;
                }
                float fast = Mathf.Min(speed, WidestAt) / 200f;
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
                    Wants(rim, along, going, speed, wide, e.flame, e.young);
                    continue;
                }
                fedAt = time;
                if (speed > fastest) fastest = speed;
                float laidAhead = e.young > 0f ? LaidAheadYoung : LaidAhead;
                Vector3 ahead = rim + going * laidAhead;
                // (An engine whose ship's patch has drawn its smoke until now, and made copies of it for the air (see Hand): what is
                // laid here begins at the nozzle, where those leave off, not ahead of it.)
                Vector3 from = vent.begun ? vent.laid : e.begins ? rim : ahead;
                if ((ahead - from).sqrMagnitude > 4f * (moved + 1f) * (moved + 1f) + 400f) from = ahead;      // (it has turned too sharply for what was laid to be gone on from)
                float stretch = (ahead - from).magnitude;
                vent.last = rim; vent.laid = ahead; vent.begun = true; vent.seen = time; vent.fed = time;
                vent.going = going; vent.along = along; vent.wide = wide; vent.flame = e.flame; vent.young = e.young;
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

                // (a thermal is no fire: with a fire's lamp inside it, as every other source has, a heap of cloud had an orange underside)
                if (e.kind != ExhaustKind.Thermal) VentLamp(ref vent, e, rim, along);
                if (e.amount >= flameWeight * 0.02f && flameWeight < 400f) { flameKind = (byte)Mathf.Clamp(e.tint, 0, 7); }

                // ---- its smoke
                // (A fast rocket's smoke is laid in wider puffs further apart: it is that much wider by the time anyone is
                // near enough to see it, and a trail laid as finely at five hundred metres a second as at fifty would be ten
                // times the puffs for the same smoke. Nor does it hang as long: it is torn apart by the speed it was left at.)
                Wants(rim, along, going, speed, wide, e.flame, e.young);
                // Not every source is an engine. Something burning with no jet to speak of (a piece of wreckage on fire: its owner
                // says it is hardly "running", and has no flame for us to draw) sends its smoke up as a plume: hot gas rising off
                // it, narrow where it leaves the fire and widening as it climbs and draws the air in (by about an eighth of the
                // height it has risen), leaning on the wind, in many small puffs close together. And what such a source aims
                // into the ground is dust thrown up by a wreck scraping along: cold, low, rolling out behind it. (Until
                // 2026-10-09 both were thrown out as an engine's smoke is, at twenty metres a second, a puff every half second:
                // each puff hung over the wreck as a dark ball.)
                // (what its owner says it is (Exhaust.kind); or, where it says nothing, worked out: an engine says what ship it is on,
                // however far it is throttled down, so only what says none is taken for such a thing)
                bool smoulders, scraped, sprays, thermal = e.kind == ExhaustKind.Thermal;
                if (thermal || Settings.TestSoot >= 1f || e.kind == ExhaustKind.Engine) smoulders = scraped = sprays = false;
                else if (e.kind == ExhaustKind.Burning) { smoulders = true; scraped = sprays = false; }
                else if (e.kind == ExhaustKind.Dust) { smoulders = scraped = true; sprays = false; }
                else if (e.kind == ExhaustKind.Spray) { sprays = true; smoulders = scraped = false; }
                else
                {
                    smoulders = e.running <= 0.08f && e.fire <= 0.01f && e.rides == null;
                    scraped = smoulders && along.y < -0.2f;
                    // And what says no ship and no flame but does run is a tank split open and spraying: a mist, not an engine's smoke
                    // (see Smoulder). (Thrown as an engine's smoke, in dense puffs at twenty metres a second, it hung behind a sliding
                    // wreck as a row of white balls.)
                    sprays = !smoulders && e.fire <= 0.01f && e.rides == null;
                }
                if (smoulders) wide = Mathf.Max(0.12f, e.nozzle * (scraped ? 1.6f : 1.1f));
                if (sprays) wide = Mathf.Max(0.12f, e.nozzle * 1.3f);
                if (thermal) wide = Mathf.Max(1f, e.nozzle);
                float standing = smoulders || sprays ? 9f + 25f * Mathf.Sqrt(e.amount) : (26f + 34f * Mathf.Sqrt(e.amount)) * e.running;
                // (a thermal: a few large puffs a second, however wide it is: the shader's billows carry the finer scales)
                if (thermal)
                {
                    vent.due += dt * (3f + 9f * e.running) * Mathf.Max(0.5f, q); cloudy = true;
                    // (a cloud's billows are as large as its puffs, tens of metres: at a flame's or a trail's few metres they are
                    // finer than a pixel from where a cloud is looked at, and each puff shows as a ball)
                    detailRepeat = Settings.TestCloudRepeat > 0f ? Mathf.Clamp(Settings.TestCloudRepeat * e.nozzle, 4f, 400f) : Mathf.Max(detailRepeat, Mathf.Clamp(0.9f * e.nozzle, 20f, 160f));
                }
                else vent.due += Mathf.Max(stretch / (0.6f * wide), dt * standing) * q * (0.45f + 0.55f * Mathf.Sqrt(e.amount));
                int puffs = Mathf.Min((int)vent.due, 48);
                vent.due -= (int)vent.due;
                float thrown = Mathf.Clamp(32f + 42f * e.nozzle, 35f, 110f) * (0.4f + 0.6f * e.running);
                float stays = e.lasts * hangs / (1f + speed / 250f);
                // (two directions across the jet)
                Vector3 across = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(along, across);
                float widest = wide, settles = 0f;
                if (e.young > 0f) YoungEnds(e, along, going, wide, e.young, out widest, out settles);
                for (int k = 0; k < puffs; k++)
                {
                    int i = Add();
                    if (i < 0) break;
                    ref P o = ref p[i];
                    float way = (k + Random.value) / puffs;                    // how far along this frame's stretch of the trail it belongs
                    float turn = Random.Range(0f, Mathf.PI * 2f), out_ = Mathf.Sqrt(Random.value);
                    // (Where the first of an engine's smoke is its ship's patch's to draw, and this is what that gives way to: laid as
                    // evenly as that is, in turn from the same kind of list (see Flame), close about its line, each puff much like the
                    // next. Laid by chance, a puff here and two there, each as far out to one side as it happened, it took over from
                    // an even column as a string of knots.)
                    bool even = e.young > 0f && !smoulders && !sprays;
                    if (even)
                    {
                        int nth = vent.puffed++ & 0xFFFFF;
                        way = (k + (float)(nth * 0.8191725133961645 % 1.0)) / puffs;
                        turn = (float)(nth * 0.6710436067037893 % 1.0) * Mathf.PI * 2f;
                        out_ = Mathf.Sqrt((float)(nth * 0.5497004779019703 % 1.0));
                    }
                    Vector3 side = across * Mathf.Cos(turn) + over * Mathf.Sin(turn);
                    Vector3 on = Vector3.Lerp(from, ahead, way);
                    if (smoulders || sprays || thermal)
                    {
                        if (thermal) ThermalPuff(ref o, e, on, side * out_, wide, stays);
                        else if (sprays) Spray(ref o, e, on, along, going, side * out_, wide, stays);
                        else Smoulder(ref o, e, on, rim, going, speed, side * out_, wide, stays, scraped);
                        Born(i);
                        continue;
                    }
                    // (That smoke is laid where the young smoke it takes over from comes to rest, and as wide: see YoungEnds. It has
                    // no push left, and is done with widening but for what mixing adds.)
                    Vector3 c = even ? on + along * settles + side * (out_ * 0.9f * e.nozzle) : on + along * (e.flame * Random.Range(0.45f, 0.9f)) + side * (out_ * wide * 0.8f);
                    float push_ = even ? 0f : thrown * Random.Range(0.45f, 1.1f);
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
                    o.r = (even ? widest * Random.Range(0.94f, 1.06f) : wide * Random.Range(0.75f, 1.25f)) * (landed ? 1.5f : 1f);
                    o.grow = 0.5f * wide;
                    // (A jet widens as it goes, by a tenth or so of how far it has gone: it takes in the air beside it. So does
                    // each puff of it, for as long as it is still being thrown along: it is three times as wide by the time the
                    // air has stopped it.)
                    o.swell = even ? 0f : 0.09f + Swell(along, push_, o.kloss, o.r);
                    o.turb = Mathf.Min(8f, 1.5f + 0.09f * push_ + (landed ? 2.5f : 0f));
                    // (hot, it goes up: slowly out of a trail, which the air has cooled; faster off the ground, where there is a great deal of it together)
                    o.rise = landed ? Random.Range(1.2f, 3.6f) : Random.Range(0.5f, 1.8f);
                    o.mass = (even ? Random.Range(0.95f, 1.15f) * 6f * e.amount * wide * wide : Random.Range(0.7f, 1.4f) * 6f * e.amount * o.r * o.r) * (landed ? 1.25f : 1f) * Seen(air, e.amount);
                    o.life = stays * Random.Range(0.6f, 1.3f);
                    o.fadeIn = 0.07f;
                    // (how long until the nozzle is where this was laid: until then it is not there to be seen)
                    o.age = speed > 1f ? -Vector3.Dot(on - rim, going) / (speed * speed) : -laidAhead * Random.value;
                    if (o.age < -laidAhead) o.age = -laidAhead; else if (o.age > 0f) o.age = 0f;
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
                if (allAlong.sqrMagnitude > 0.01f) { TurnAlong(allAlong.normalized); jetAxisL = allAlong.normalized; }
                if (jetStrongest > 0f) jetSeen = time;
            }
            if (youngCount > 0) youngRim = youngRims / youngCount;
            Lingering();
            Cuts();
        }

        /// <summary>
        /// (In a patch that stays where it is.) An engine that has stopped being laid a trail here, a moment ago, and whose
        /// smoke its ship's patch draws at first: what was laid ahead of it is still ahead of it, and is to be hidden until
        /// the ship has left it as far behind as it would have. Where its nozzle is, is no longer told: it is taken to have
        /// gone on as it was going. (Left unhidden, a quarter of a second's worth of trail came to be all at once, ahead of
        /// the rocket, at the moment its engine was shut down.)
        /// </summary>
        bool Lingering()
        {
            if (riding) return false;
            bool any = false;
            for (int n = 0; n < ventCount; n++)
            {
                ref Vent vent = ref vents[n];
                float since = time - vent.seen;
                if (vent.young <= 0f || !vent.begun || since < 1e-4f || since > 0.4f) continue;
                float speed = vent.going.magnitude;
                if (speed < 12f) continue;
                Wants(vent.last + vent.going * since, vent.along, vent.going, speed, vent.wide, vent.flame, vent.young);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// One puff of what something burning with no jet gives off (see Vents): the plume of a piece of wreckage on fire, or the dust
        /// a wreck throws up scraping along (scraped).
        /// </summary>
        /// <summary>One puff of a thermal (ExhaustKind.Thermal): see there. aside: where over the base's disc it is born, the disc's radius one.
        /// (The first trial made a heap of a few hundred puffs each tens of metres across: a smooth blob, of which the user said "that
        /// cloud looks so terrible". What makes the explosions' smoke read as real is thousands of small puffs lying over one another;
        /// so a thermal is that too: small puffs, stalled at every height up to the dome, and a third of them left at the base, which
        /// is flat.)</summary>
        void ThermalPuff(ref P o, in Exhaust e, Vector3 on, Vector3 aside, float wide, float stays)
        {
            float depth = Mathf.Max(e.flame, 2f * wide);
            bool floor = Random.value < 0.3f;
            // (the base: a level layer a little wider than the thermal, where the rising air first condenses)
            Vector3 c = on + aside * (wide * (floor ? 1.2f : 0.9f)) + Vector3.up * (Random.Range(-0.03f, 0.05f) * depth);
            o.x = c.x; o.y = c.y; o.z = c.z;
            o.vx = 0f; o.vy = 0f; o.vz = 0f;
            // (the middle of a thermal climbs fastest and highest; its rim, mixing with dry air, least: a dome. And its puffs stall
            // at every height on the way: the dome is full, not a shell.)
            float middle = 1f - 0.7f * aside.sqrMagnitude;
            float up = Mathf.Lerp(2.5f, 6f, Mathf.Clamp01(e.running)) * (0.5f + 0.5f * middle) * Random.Range(0.8f, 1.2f);
            float share = Random.value;
            float climbs = floor ? depth * Random.Range(0f, 0.06f) : depth * middle * (0.12f + 0.93f * Mathf.Sqrt(share));
            Vector3 push = Vector3.up * up + aside * (0.3f * up) + new Vector3(Random.Range(-0.4f, 0.4f), 0f, Random.Range(-0.4f, 0.4f));
            if (floor) push *= 0.25f;
            o.kx = push.x; o.ky = push.y; o.kz = push.z;
            // (thrown up and slowed by the air: it comes to rest having gone its speed over its loss)
            o.kloss = push.y / Mathf.Max(climbs, 1f); o.drag = 0.5f;
            // (fewer and larger than the first working heap's (a tenth to a sixth of the width, 6 to 28 a second): that one cost too
            // much, and the billows, now the size of the puffs, carry the finer shapes)
            o.r = wide * Random.Range(0.13f, 0.22f);
            o.grow = 0.002f * wide;
            o.swell = 0.03f;
            o.turb = Random.Range(0.6f, 1.4f);
            o.rise = 0f;
            // (as thick at the top of its climb as a dense puff of smoke: water goes on condensing as the air rises, where smoke
            // only thins; so it is given at birth what it will need when it has widened)
            float widest = o.r + (0.02f + o.swell) * climbs;
            o.mass = Random.Range(0.8f, 1.25f) * 2.0f * e.amount * widest * widest * (Settings.TestCloudMass > 0f ? Settings.TestCloudMass : 1f);
            o.life = stays * Random.Range(0.7f, 1.15f);
            o.fadeIn = 2f;
            float tone = e.shade * Random.Range(0.97f, 1.03f);
            o.ar = tone; o.ag = tone; o.ab = tone;
        }

        void Smoulder(ref P o, in Exhaust e, Vector3 on, Vector3 rim, Vector3 going, float speed, Vector3 aside, float wide, float stays, bool scraped)
        {
            Vector3 c = on + aside * (0.6f * wide);
            float floor = hasGround ? GroundAt(c.x, c.z) : -1e9f;
            if (scraped)
            {
                // Dust: thrown up off the ground behind the wreck, at a part of the speed it is sliding at, and then the air's. Cold:
                // it does not rise as smoke off a fire does, only billows up a little in the stirred air, and spreads low.
                c.y = Mathf.Max(c.y, floor + 0.35f * wide);
                Vector3 back = speed > 1f ? going * (-1f / speed) : Vector3.zero;
                float thrown = Mathf.Min(14f, 0.2f * speed) * Random.Range(0.5f, 1.1f);
                Vector3 push = back * thrown + aside * (0.3f * thrown) + Vector3.up * (thrown * Random.Range(0.25f, 0.6f));
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.vx = going.x * 0.15f; o.vy = 0f; o.vz = going.z * 0.15f;
                o.kx = push.x; o.ky = push.y; o.kz = push.z;
                o.kloss = 2.2f; o.drag = 3f;
                o.r = wide * Random.Range(0.7f, 1.3f);
                o.grow = 0.35f + 0.1f * wide;
                o.turb = 1.8f;
                o.rise = Random.Range(0.1f, 0.45f);
                o.mass = Random.Range(0.6f, 1.3f) * 4.5f * e.amount * o.r * o.r;
                o.life = stays * Random.Range(0.6f, 1.2f);
                o.fadeIn = 0.15f;
                Color ground = groundBare ? groundDust : new Color(0.32f, 0.3f, 0.27f);
                float light = Random.Range(0.85f, 1.15f), tone = e.shade * Random.Range(0.85f, 1.15f);
                // (mostly the ground's own colour: what is thrown up is the ground)
                o.ar = Mathf.Lerp(tone, ground.r * light * 1.15f, 0.7f); o.ag = Mathf.Lerp(tone * (1f - 0.05f * e.warm), ground.g * light * 1.15f, 0.7f); o.ab = Mathf.Lerp(tone * (1f - 0.14f * e.warm), ground.b * light * 1.15f, 0.7f);
            }
            else
            {
                // A plume: it leaves the burning thing as hot gas, unseen at first, its soot coming into view in the first second as
                // it cools (see Deposit), rising at a couple of metres a second and widening as it climbs; it is left behind in the
                // air the thing moves through, with a little of its speed.
                c += Vector3.up * (0.3f * wide);
                if (c.y < floor + 0.2f) c.y = floor + 0.2f;
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.vx = going.x * 0.12f; o.vy = going.y * 0.12f; o.vz = going.z * 0.12f;
                Vector3 push = Vector3.up * Random.Range(0.8f, 2f) + aside * 0.6f;
                o.kx = push.x; o.ky = push.y; o.kz = push.z;
                o.kloss = 2f; o.drag = 3.5f;
                o.r = wide * Random.Range(0.6f, 1.0f);
                o.grow = 0.16f + 0.08f * wide;
                o.turb = Random.Range(0.8f, 1.5f);
                o.rise = Random.Range(2.2f, 3.4f) * Mathf.Clamp(0.7f + 0.6f * wide, 0.8f, 1.6f);
                o.heat = Random.Range(0.38f, 0.46f);
                o.cool = Random.Range(1.3f, 1.8f);
                o.mass = Random.Range(0.7f, 1.4f) * 2.7f * e.amount * o.r * o.r;
                o.life = stays * Random.Range(0.6f, 1.3f);
                o.fadeIn = 0.2f;
                float tone = e.shade * Random.Range(0.82f, 1.22f);
                o.ar = tone; o.ag = tone * (1f - 0.05f * e.warm); o.ab = tone * (1f - 0.14f * e.warm);
            }
            o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
            o.tile = Random.value < 0.4f ? Wispy() : Lumpy();
        }

        /// <summary>
        /// One puff of the mist a split tank sprays (see Vents): driven out of the split at a few metres a second, it spreads and thins
        /// within a second or two as the spray breaks up and evaporates, is left behind in the air the wreck moves through, and lies low:
        /// it is cold, and as heavy as air or more. Small puffs, many and thin, that run together into a stream.
        /// </summary>
        void Spray(ref P o, in Exhaust e, Vector3 on, Vector3 along, Vector3 going, Vector3 aside, float wide, float stays)
        {
            Vector3 c = on + along * (0.3f * wide) + aside * (0.5f * wide);
            if (hasGround) { float floor = GroundAt(c.x, c.z) + 0.3f * wide; if (c.y < floor) c.y = floor; }
            float thrown = Random.Range(3f, 9f) * (0.5f + 0.5f * e.running);
            Vector3 push = along * thrown + aside * (0.4f * thrown);
            o.x = c.x; o.y = c.y; o.z = c.z;
            o.vx = going.x * 0.1f; o.vy = going.y * 0.1f; o.vz = going.z * 0.1f;
            o.kx = push.x; o.ky = push.y; o.kz = push.z;
            o.kloss = 3f; o.drag = 3.5f;
            o.r = wide * Random.Range(0.6f, 1.0f);
            o.grow = 0.5f + 0.15f * wide;
            o.turb = Random.Range(1.2f, 2.2f);
            o.rise = Random.Range(-0.15f, 0.1f);
            o.mass = Random.Range(0.6f, 1.2f) * 1.8f * e.amount * o.r * o.r;
            o.life = stays * Random.Range(0.25f, 0.5f);
            o.fadeIn = 0.12f;
            float tone = e.shade * Random.Range(0.85f, 1.1f);
            o.ar = tone; o.ag = tone * (1f - 0.05f * e.warm); o.ab = tone * (1f - 0.14f * e.warm);
            o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
            o.tile = Wispy();
        }

        /// <summary>
        /// An engine's light on its smoke: a steady fire a third of the way down the flame. (Never under the ground: on a
        /// pad the flame is turned along the pad a metre or two below the nozzle, and what lights the smoke there is the
        /// sheet of fire lying on it. Put where the flame would have reached had nothing stopped it, the light was in the
        /// pad, and the smoke of a launch at night came out of the ground black.)
        /// </summary>
        void VentLamp(ref Vent vent, in Exhaust e, Vector3 rim, Vector3 along)
        {
            Vector3 fire = rim + along * (0.35f * e.flame);
            float glows = Mathf.Max(2.5f, e.nozzle * 5f);
            // (By day the sun is what lights smoke, and a flame's light on it counts for a quarter of what it does at night:
            // at full strength in sunlight, the smoke behind a climbing rocket was orange for as far as the flame's light reached.)
            float shines = 2.4f * e.running * Mathf.Lerp(1f, 0.25f, Mathf.Clamp01(Mathf.Max(sunR, Mathf.Max(sunG, sunB))));
            if (hasGround)
            {
                float floor = GroundAt(fire.x, fire.z) + 1.5f;
                if (fire.y < floor) { fire.y = floor; glows *= 1.6f; }
            }
            if (vent.lamp < 0 || vent.lamp >= lampCount || !lamps[vent.lamp].quiet || lamps[vent.lamp].owner != e.id)
            {
                vent.lamp = AddLamp(fire.x, fire.y, fire.z, glows, shines, 0.6f, (byte)e.tint, true, 0f, 0f);
                lamps[vent.lamp].quiet = true;
                lamps[vent.lamp].owner = e.id;
            }
            else
            {
                ref Lamp lamp = ref lamps[vent.lamp];
                lamp.x = fire.x; lamp.y = fire.y; lamp.z = fire.z; lamp.r = glows; lamp.age = 0f; lamp.peak = shines;
            }
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
        /// Something burning on a ship, its owner says (Exhaust.kind Burning: a wreck on fire, a tank alight), not an engine's jet:
        /// flames standing on it where it burns, over a patch as wide as the fire, licking up off it at a few metres a second as
        /// the hot gas rises (about the speed of a fire's own buoyant gas, the square root of gravity times its width), torn and
        /// flickering, and swept back by the air when the ship moves (the air goes by in this patch: see the mover). Not a jet
        /// thrown out at tens of metres a second from a point, which is what such a fire was drawn as until 2026-10-10: a torch
        /// standing off the wreck. A fire's flames, as the fires on the ground are: see Fires, and firePattern in the shader.
        /// </summary>
        void Blaze(ref Vent vent, in Exhaust e, Vector3 rim, Vector3 along, float fire, float dt, float q)
        {
            float wide = Mathf.Max(0.25f, e.nozzle);
            float lick = Mathf.Sqrt(Mathf.Max(gravity, 1f) * 2f * wide);                  // m/s: how fast its gas rises
            float tall = Mathf.Clamp(e.flame > 0f ? e.flame : 3f * wide, 0.8f * wide, 8f * wide);
            blazeWide = Mathf.Max(blazeWide, wide); blazeSeen = time;
            float span = stepDt > 1e-4f ? stepDt : dt;
            // (enough flame, close enough together, that it burns as one fire and not a few separate flickers: seen the first time,
            // twenty-odd small bits a second made a fire on a tank's side that was mostly its own smoke)
            vent.fire += Mathf.Clamp(45f + 75f * wide, 50f, 240f) * span * Mathf.Clamp(q, 0.4f, 1.2f) * Mathf.Clamp(fire, 0.3f, 1f);
            int bits = Mathf.Min((int)vent.fire, 60);
            vent.fire -= (int)vent.fire;
            if (bits <= 0) return;
            if (fire >= flameWeight) { flameKind = (byte)Mathf.Clamp(e.tint, 0, 7); flameWeight = fire; }
            Vector3 up = along.sqrMagnitude > 1e-4f ? along.normalized : Vector3.up;
            Vector3 across = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(up, across);
            for (int k = 0; k < bits; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                float turn = Random.Range(0f, Mathf.PI * 2f), out_ = Mathf.Sqrt(Random.value);
                // (on the burning part itself, over the width of the fire: its foot touches what burns)
                Vector3 c = rim + (across * Mathf.Cos(turn) + over * Mathf.Sin(turn)) * (out_ * wide) + up * (0.15f * wide * Random.value);
                o.x = c.x; o.y = c.y; o.z = c.z;
                float push = lick * Random.Range(0.5f, 1.1f);
                o.kx = up.x * push; o.ky = up.y * push; o.kz = up.z * push;
                o.kloss = 1.5f; o.drag = 3f;
                o.r = 0.65f * wide * Random.Range(0.7f, 1.2f);
                o.turb = 3f + 4f * out_;
                // (hottest in the middle and low down, cooling as it rises: gone from sight by the height of the flame)
                o.heat = Random.Range(0.95f, 1.08f) * (1f - 0.2f * out_ * out_);
                float lasts = tall / Mathf.Max(lick, 0.5f);
                o.cool = 1.2f / lasts * Random.Range(0.85f, 1.2f);
                o.life = 1.6f * lasts * Random.Range(0.85f, 1.15f);
                o.flame = Mathf.Clamp(fire, 0.3f, 1f) * Random.Range(1f, 1.4f);
                o.rise = lick * Random.Range(0.6f, 1f);
                o.grow = 0.08f;
                o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
                o.tile = Lumpy();
                Born(i);
            }
        }

        /// <summary>
        /// A bit of an engine's gas or smoke that left the nozzle so long ago, within the frame: put on to where, and to how,
        /// the mover would have brought it by now (see RunChunk: the air's hold on it, the jet's push dying away, its
        /// widening, its cooling). Laid along the jet by the speed it left at and no more, as until 2026-10-10, all that
        /// left in one frame then moved as one; and where the air goes on taking the gas back past the ship, faster and
        /// faster, each frame's lot kept its length while the lots drew apart. Behind a fast rocket the smoke began as a
        /// row of balls, one to a frame, each of one heat and one size.
        /// </summary>
        void Since(ref P o, float since, Vector3 air)
        {
            if (since > 0f)
            {
                float hold = grip;
                if ((o.flags & Ballistic) == 0)
                {
                    float x = o.drag * hold * since, follow = x / (1f + x);
                    o.vx += (air.x - o.vx) * follow; o.vy += (air.y - o.vy) * follow; o.vz += (air.z - o.vz) * follow;
                    float keep = 1f / (1f + o.kloss * (hold < 0.55f ? 0.55f : hold) * since);
                    o.kx *= keep; o.ky *= keep; o.kz *= keep;
                    o.r += (o.grow / o.r + 0.012f * o.turb) * since;
                }
                else
                {
                    if (hold > 0f) { float keep = 1f / (1f + 0.35f * hold * since); o.vx *= keep; o.vy *= keep; o.vz *= keep; }
                    o.r += o.grow * since;
                }
                o.x += (o.vx + o.kx) * since; o.y += (o.vy + o.ky) * since; o.z += (o.vz + o.kz) * since;
                o.r += (0.02f + o.swell) * Mathf.Sqrt(o.kx * o.kx + o.ky * o.ky + o.kz * o.kz) * since;
                o.heat *= Mathf.Exp(-o.cool * since);
            }
            o.age = since;
        }

        /// <summary>
        /// An engine's flame: burning gas that leaves the nozzle fast, keeps most of its speed, widens as it goes and
        /// cools as it goes, and is spent where it has cooled. Enough of it every frame that the jet is one body of
        /// fire and not a string of beads: it goes metres in a frame, so each bit is put where it would have got to
        /// had it left at its own moment within the frame. How thick, how hot and how long-lived its gas is goes by
        /// what burns (see Plumes): kerosene's and a solid motor's a blinding white-hot body, hydrogen's a clear veil.
        ///
        /// Where the air is thin there is less to hold the jet together: it opens out and is spent in half the
        /// length, and in a vacuum it fans out from the nozzle's lip, a clear bright lobe that nothing slows or stirs.
        /// </summary>
        void Flame(ref Vent vent, in Exhaust e, Vector3 rim, Vector3 along, Vector3 air, float fire, float dt, float q)
        {
            float length = Mathf.Max(e.flame, 4f * e.nozzle);
            // How fast it looks to go, not how fast it goes, which is ten times this: the length of the flame in a sixth
            // of a second, and never slower than sixty metres a second, however short the flame (a small engine's flame
            // is as quick as a big one's, and shorter because it is over sooner). It keeps most of that speed to the
            // end, and the end of the flame is where it has cooled, not where it has stopped: gas that slowed to a stop
            // would spend most of its time at the far end, and the flame would be thin at the nozzle and thick at its tip.
            float jet = Mathf.Max(6.25f * length, 60f);
            float thin = Thin;                                          // 0 at the ground, 1 in a vacuum
            // (With no air to hold it in, the jet opens out at once and is spent in half the length.)
            float reach = length * Mathf.Lerp(1f, 0.45f, thin), lasts = reach / jet, slows = 0.5f / lasts;
            float opens = Mathf.Lerp(0.035f, 0.36f, thin);              // how much wider it gets for each metre it goes
            float r0 = 0.7f * e.nozzle;
            // (what this kind of flame is like: see Plumes)
            Plume look = Plumes[Mathf.Clamp(e.tint, 0, 7)];
            // How fast the gas at the jet's rim goes outward, against how fast it goes on: in thick air hardly at all; with
            // none to hold it in, it fans out from the nozzle's lip, and the plume is a cone, brightest at the nozzle and
            // thinning away from it. (Sent nearly straight on and left to swell, as at first, it was a ball of mist a
            // little way behind the engine.)
            float fans = Mathf.Lerp(0.6f * opens, 0.5f, thin * thin);
            // (and whatever burns, with no air round it the gas is too thin to be anything but a clear glow: the thickest
            // flame is no thicker there than the clearest is at the ground)
            float dense = Mathf.Lerp(look.dense, Mathf.Min(look.dense, 0.1f) * look.clear, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.9f, thin)));
            // The gas put out now fills the stretch that the gas put out last time has left behind it: as far as that was
            // moved since, which is by the length of the frame before this one, not of this one (the mover runs after
            // this, see Step). Filled by this frame's own length, every frame longer than the last overlapped it and
            // every shorter one left a gap: and one frame in three is longer, the one a grid is handed to the graphics
            // card in. A flame seen from the side was a row of three lumps.
            float span = stepDt > 1e-4f ? stepDt : dt;
            // (Some thirteen hundred bits to a nozzle at a time, whatever its size: a third as many, as at first, and the
            // jet was too thin to burn as one body, and its outline a row of bulges.)
            vent.fire += Mathf.Min(jet / (0.08f * r0), 5000f) * span * Mathf.Clamp(q, 0.4f, 1.2f);
            int bits = Mathf.Min((int)vent.fire, 500);
            vent.fire -= (int)vent.fire;
            if (bits <= 0) return;
            Vector3 across = Vector3.Cross(along, Mathf.Abs(along.y) < 0.9f ? Vector3.up : Vector3.right).normalized, over = Vector3.Cross(along, across);
            if (fire >= flameWeight) { flameKind = (byte)Mathf.Clamp(e.tint, 0, 7); flameWeight = fire; }
            for (int k = 0; k < bits; k++)
            {
                int i = Add();
                if (i < 0) break;
                ref P o = ref p[i];
                // Where in the stretch, and where across the nozzle's mouth, each bit leaves from is not left to chance but taken
                // in turn from a list that fills all three evenly (the R3 sequence of M. Roberts): left to chance, a few dozen
                // bits to a frame bunch here and leave gaps there, and the jet's outline was a row of bulges, new ones every frame.
                int nth = vent.shot++ & 0xFFFFF;
                float since = (k + (float)(nth * 0.8191725133961645 % 1.0)) / bits * span;         // how long ago within that stretch it left
                float turn = (float)(nth * 0.6710436067037893 % 1.0) * Mathf.PI * 2f, out_ = Mathf.Sqrt((float)(nth * 0.5497004779019703 % 1.0));
                Vector3 side = across * Mathf.Cos(turn) + over * Mathf.Sin(turn);
                // (the middle of a jet is fastest, and hottest)
                float speed = jet * Random.Range(0.88f, 1.06f) * (1f - 0.35f * out_ * out_);
                // (Where it fans, more of it near the middle than at the rim: a bright lobe along the jet's line, and a faint skirt.)
                Vector3 push = along * speed + side * (out_ * Mathf.Lerp(1f, out_, thin) * speed * fans);
                // It begins a little way up inside the nozzle, so that the flame is whole at its mouth. (And is put, below, where
                // it would have got to by now, sideways as well as on: where the gas fans out, bits put straight down the jet's
                // line for their first frame were a rod as long as the gas goes in a frame, with the fan beginning at its end.)
                Vector3 c = rim + side * (out_ * 0.8f * e.nozzle) - along * (0.8f * r0);
                o.x = c.x; o.y = c.y; o.z = c.z;
                o.kx = push.x; o.ky = push.y; o.kz = push.z;
                o.kloss = slows; o.drag = 2f;
                o.r = r0 * Random.Range(0.8f, 1.2f);
                // (its edge, where it meets the air, is stirred by it; its middle hardly at all)
                o.turb = (1.8f + 5.4f * out_ * out_) * (1f - thin);
                // A white-hot core, and a thin cooler sheath round it where it has met the air, which is where its colours
                // are. It leaves the nozzle hotter than white-hot, and so stays white for most of its length, and cools out
                // through yellow and orange in the last of it: brightness goes with heat far more steeply than colour does
                // (see JetShines), and a flame that had begun to cool at the nozzle was a dim orange thing for most of its
                // length beside the game's own. In thin air it starts a little cooler, the sooner to show its colours.
                o.heat = (1.02f - look.edge * Mathf.Pow(out_, 3.5f)) * Random.Range(0.92f, 1.08f) * look.hot * Mathf.Lerp(1f, 0.8f, thin);
                o.flame = 0.22f * dense * fire * Random.Range(0.8f, 1.2f) * (1f - 0.45f * thin);
                // (In a vacuum it cools as it spreads, all the faster for having nothing to hold it together: what is seen
                // of it there is a bright lobe at the nozzle, gone within a few nozzles' widths.)
                o.cool = 2.1f * look.cool / lasts * Random.Range(0.85f - 0.3f * thin, 1.2f + 0.5f * thin) * Mathf.Lerp(1f, 0.9f, thin * thin * thin * thin);
                // (some of it spent sooner and some later, by a good deal: so that the flame ends in tongues and not at a line)
                o.life = Mathf.Lerp(1.7f, 1.06f, thin) * lasts * Random.Range(0.6f, 1.4f);
                // (Flame that has spread out is drawn thinner, by how much wider it is than it began: see Deposit. Where the
                // gas fans out that begins at once, so that it is brightest at the nozzle.)
                float began = o.r;
                if (vacuum)
                {
                    // (nothing slows it, and nothing stirs it: it only spreads, each bit by less than the fan does, or the fan is a ball)
                    o.flags = Ballistic | Exhausted;
                    o.grow = 0.4f * fans * speed;
                    began += o.grow * (1f / 120f);          // (as wide as it was reckoned to begin while this was tuned, at sixty frames a second: whatever the frame rate now)
                }
                else
                {
                    o.flags = Exhausted;
                    o.grow = 0.02f;
                    o.swell = opens - 0.02f;
                }
                Since(ref o, since, air);
                o.tint = (byte)Mathf.Clamp(e.tint, 0, 7);
                o.tile = Lumpy();
                Born(i);
                p[i].born = began * Mathf.Lerp(1f, 0.5f, thin);
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
        void Wants(Vector3 rim, Vector3 along, Vector3 going, float speed, float wide, float flame, float young)
        {
            float laidAhead = young > 0f ? LaidAheadYoung : LaidAhead;
            if (speed < 12f || wantCount >= wants.Length) return;
            Vector3 back = going * (-1f / speed);
            float away = Vector3.Dot(along, back);                      // 1: the jet points straight back along the way it has come
            float much = Mathf.SmoothStep(0f, 1f, (speed - 12f) / 28f) * Mathf.SmoothStep(0f, 1f, (away - 0.2f) / 0.4f);
            // (An engine whose young smoke its ship's patch draws: none of what is laid here is to be seen before that
            // patch's smoke begins to give way to it, which is so far behind the nozzle, and it comes on over as far again
            // as that goes: see TuneCuts, which tells that patch the other half of this.)
            if (young > 0f) much = 1f;
            if (much <= 0.01f) return;
            Vector3 line = rim + along * (0.675f * flame);              // a place on the line that what was laid ahead lies along
            float first = young > 0f ? young : 0.45f * flame * away;    // how far back from the nozzle the first of its smoke is
            // (how far the cells of the grid in use smear the smoke: along the trail they may be metres long)
            float cellLong = gridReady ? 1f / Mathf.Min(gix, Mathf.Min(giy, giz)) : 2f, cellWide = gridReady ? 1f / Mathf.Max(gix, Mathf.Max(giy, giz)) : 1f;
            wants[wantCount++] = new Cut
            {
                from = line + back * (first - Vector3.Dot(line - rim, back)), back = back, much = much,
                soft = Mathf.Max(first, 2f),
                wide = 3f * wide + 2f + 2f * cellWide + 0.3f * flame * Mathf.Sqrt(Mathf.Max(0f, 1f - away * away)),
                far = 1.3f * laidAhead * speed + flame + 2.5f * cellLong + 1.5f * young,
                early = laidAhead,
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
                    c.far = Mathf.Max(c.far, wants[b].far); c.much = Mathf.Max(c.much, wants[b].much); c.early = Mathf.Max(c.early, wants[b].early);
                    wants[b].much = 0f;
                }
                c.from = from / n; c.back = back.normalized;
                c.wide += 1.5f * apart; c.soft += most - least;
                cuts[cutCount++] = c;
                early = Mathf.Max(early, Mathf.Lerp(GridLate, c.early, c.much));
            }
            laidEarly = early;
        }

        /// <summary>Tell the shader (see TuneVolume: every frame).</summary>
        void TuneCuts()
        {
            if (riding)
            {
                // A ship's patch: its engines' young smoke gives way, from so far behind the nozzles along the way the ship has
                // come and over as far again, to the trail laid straight into the air it is leaving (see Wants, which tells
                // the patch that holds that trail the same place and the same stretch: and begun, in the shader).
                bool gives = youngShown && handFar > 0f && ridesGoing.sqrMagnitude > 1f;
                Vector3 way = gives ? -ridesGoing.normalized : Vector3.up;
                Vector3 place = ToGrid(youngRim + way * handFar) - slid, wayTurned = ToGrid(way);
                boxMaterial.SetVector("_VolCutA", gives ? new Vector4(place.x, place.y, place.z, 1f) : Vector4.zero);
                boxMaterial.SetVector("_VolCutB", gives ? new Vector4(wayTurned.x, wayTurned.y, wayTurned.z, Mathf.Max(handFar, 2f)) : Vector4.zero);
                boxMaterial.SetVector("_VolCutC", gives ? new Vector4(0f, -1f, 0f, 0f) : Vector4.zero);
                boxMaterial.SetVector("_VolCutD", Vector4.zero);
                boxMaterial.SetVector("_VolCutE", Vector4.zero);
                boxMaterial.SetVector("_VolCutF", Vector4.zero);
                return;
            }
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
