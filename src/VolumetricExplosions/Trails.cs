using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace VolumetricExplosions
{
    /// <summary>
    /// What a small moving thing leaves in the air this frame: a piece of wreckage trailing smoke, or flame and smoke. Whoever
    /// knows of it says so afresh every frame (see <see cref="Trails"/>).
    /// </summary>
    public struct Trail
    {
        public int id;                 // the same number every frame for the same thing
        public Vector3d at;            // where it is, in the scene's axes
        public Vector3d going;         // how it is moving over the ground, metres a second, in the scene's axes (as a vessel's srf_velocity)
        public float size;             // how big it is (its radius), metres
        public float smoke;            // how much smoke it gives: 0.05 a wisp, 0.3 smouldering, 1 burning hard
        public float fire;             // how much of what it gives is flame, 0 to 1 (a burning thing: flame that turns to smoke where it was)
        public float shade;            // how light the smoke is: 0.03 soot, 0.8 steam or pale dust
        public float warm;             // how brown it is: 0 grey, 1 the brown of dust or burnt kerosene
        public int tint;               // the flame's colours, as a blast's: 0 fuel, 1 solid propellant, 2 monopropellant
        public float lasts;            // seconds the smoke hangs, in thick still air
    }

    /// <summary>
    /// The way in for the smoke of many small things (the pieces of a wreck), made to cost little: each is laid as puffs straight into
    /// the patch of air it is in or near, if there is one, and never begins one of its own. (A patch is a grid worked out and drawn
    /// every frame; beginning one is work for the game's own thread. Every piece of a crash with a patch of its own would halve the
    /// frame rate; this way a hundred of them cost a few hundred particles more.) Where there is no patch near, nothing is laid.
    /// </summary>
    public static class Trails
    {
        internal struct Laid { public Vector3d last; public float due; public int seen; }

        internal static readonly List<Trail> Now = new List<Trail>(256);
        internal static bool[] taken = new bool[256];
        internal static int frame = -1;
        internal static readonly Dictionary<int, Laid> laid = new Dictionary<int, Laid>();
        static HashSet<int> tookBefore = new HashSet<int>(), tookNow = new HashSet<int>();
        static int tookFrame = -1;

        /// <summary>
        /// Say what a thing is leaving in the air this frame (when Sources.Asked is raised). True if the air took what it left the frame
        /// before: it was in or near a patch of air.
        /// </summary>
        public static bool Smoking(in Trail trail)
        {
            if (Air.Instance == null || Assets.Volume == null || !Settings.Volume) return false;
            int f = Time.frameCount;
            if (f != frame)
            {
                frame = f;
                Now.Clear();
                // (the ids taken in the frame before are the answer given in this one)
                if (tookFrame != f) { HashSet<int> swap = tookBefore; tookBefore = tookNow; tookNow = swap; tookNow.Clear(); tookFrame = f; }
                if (laid.Count > 2000) Forget(f);
            }
            if (Now.Count >= 600) return false;
            Now.Add(trail);
            if (taken.Length < Now.Count) System.Array.Resize(ref taken, Now.Count * 2);
            taken[Now.Count - 1] = false;
            return tookBefore.Contains(trail.id);
        }

        internal static void Took(int id) => tookNow.Add(id);

        /// <summary>
        /// Begin a patch of air out of sight and put it away again at once. The first patch begun in a scene takes half a second and more on
        /// the game's thread (the second, a few tens of milliseconds): done while the scene is still settling, the first explosion of a crash
        /// does not stop the game. Where there is no air here, or the mod is not drawing volumes, nothing is done.
        /// </summary>
        public static void Warm(Vector3d world)
        {
            if (Air.Instance == null || Assets.Volume == null || !Settings.Volume) return;
            try
            {
                Plan plan = Plan.Where(world);
                if (plan.vacuum || plan.underwater) return;
                var site = new Site(plan);
                site.Dispose();
            }
            catch (System.Exception ex) { Addon.Log("could not warm up: " + ex.Message); }
        }

        // (at most so many puffs a frame for all the trails together, as the air has room: a crash's hundred pieces are a few hundred particles a
        // second, not thousands)
        const int MostPuffs = 120;
        static int puffFrame = -1, puffsLeft;
        internal static bool Puff()
        {
            int f = Time.frameCount;
            if (f != puffFrame) { puffFrame = f; puffsLeft = Mathf.RoundToInt(MostPuffs * Air.Room()); }
            if (puffsLeft <= 0) return false;
            puffsLeft--;
            return true;
        }

        static void Forget(int f)
        {
            var old = new List<int>();
            foreach (KeyValuePair<int, Laid> pair in laid) if (f - pair.Value.seen > 120) old.Add(pair.Key);
            foreach (int id in old) laid.Remove(id);
        }
    }

    /// <summary>The fifth part of a Site: the trails of small things that are in it (see Trails).</summary>
    public sealed partial class Site
    {
        /// <summary>Each trail said this frame that is in or near this patch of air, and not had by another: puffs along the way it came.</summary>
        void TakeTrails(float dt)
        {
            List<Trail> list = Trails.Now;
            if (list.Count == 0 || Trails.frame != Time.frameCount || riding || leaving || count == 0 || dt <= 0f) return;
            if (FlightGlobals.currentMainBody != body) return;
            float mx = (bx0 + bx1) * 0.5f, my = (by0 + by1) * 0.5f, mz = (bz0 + bz1) * 0.5f;
            float hx = (bx1 - bx0) * 0.5f, hy = (by1 - by0) * 0.5f, hz = (bz1 - bz0) * 0.5f;
            float margin = Mathf.Max(25f, 0.3f * Mathf.Sqrt(hx * hx + hy * hy + hz * hz));
            float q = Mathf.Clamp(Settings.Quality * Air.Room(), 0.15f, 1.5f);
            float lasting = Mathf.Max(0.3f, Settings.Smoke);
            int f = Time.frameCount;
            for (int k = 0; k < list.Count; k++)
            {
                if (Trails.taken[k]) continue;
                Trail tr = list[k];
                if (vacuum && tr.fire <= 0.01f) continue;                   // (nothing for smoke to hang in out here: only flame)
                Vector3 at = frame.ToLocal(tr.at);
                if (Mathf.Abs(at.x - mx) > hx + margin || Mathf.Abs(at.y - my) > hy + margin || Mathf.Abs(at.z - mz) > hz + margin) continue;
                Trails.taken[k] = true;
                Trails.Took(tr.id);
                Vector3 going = frame.DirToLocal(tr.going);
                Trails.laid.TryGetValue(tr.id, out Trails.Laid was);
                bool begun = was.seen > 0 && f - was.seen < 30;
                Vector3 from = begun ? frame.ToLocal(was.last) : at;
                float moved = (at - from).magnitude;
                if (moved > 60f) { from = at; moved = 0f; }
                // A puff for every so much of the way (so that a fast piece leaves a trail and not a row of balls), and so many a second
                // where it is still: more for flame than for smoke, and few for a wisp.
                float size = Mathf.Clamp(tr.size, 0.05f, 2f);
                // (flame is laid closer than smoke, and a wisp sparsely: a hundred pieces each laying a puff every few metres is a few hundred
                // particles a second)
                float spacing = tr.fire > 0.01f ? Mathf.Max(0.6f, size * 1.6f) : Mathf.Max(1.2f, size * 2.4f);
                float rate = (tr.fire > 0.01f ? 14f * tr.fire : 0f) + 7f * Mathf.Clamp01(tr.smoke);
                float due = was.due + (moved / spacing) * Mathf.Lerp(0.15f, 1f, Mathf.Clamp01(tr.smoke + tr.fire)) + rate * dt;
                due *= q;
                int n = Mathf.Min(6, (int)due);
                for (int j = 0; j < n; j++)
                {
                    float s = n == 1 ? 1f : (j + 1f) / n;
                    if (!Trails.Puff() || !TrailPuff(Vector3.Lerp(from, at, s), going, size, tr, lasting)) { n = j; break; }
                }
                // (what could not be laid this frame is not saved up: a trail is thinner when the air is full, not laid in a lump later)
                Trails.laid[tr.id] = new Trails.Laid { last = tr.at, due = Mathf.Min(1f, (due - n) / Mathf.Max(q, 0.01f)), seen = f };
            }
        }

        /// <summary>One puff of a trail: flame that turns to smoke where it was, or smoke alone, or a wisp.</summary>
        bool TrailPuff(Vector3 at, Vector3 going, float size, in Trail tr, float lasting)
        {
            int i = Add();
            if (i < 0) return false;
            ref P o = ref p[i];
            float jitter = 0.1f + 0.2f * size;
            o.x = at.x + Random.Range(-jitter, jitter); o.y = at.y + Random.Range(-jitter, jitter); o.z = at.z + Random.Range(-jitter, jitter);
            o.vx = going.x * 0.1f + Random.Range(-0.4f, 0.4f); o.vy = going.y * 0.1f + Random.Range(-0.3f, 0.5f); o.vz = going.z * 0.1f + Random.Range(-0.4f, 0.4f);
            o.tint = (byte)Mathf.Clamp(tr.tint, 0, 7);
            o.tile = Random.value < 0.4f ? Wispy() : Lumpy();
            bool burning = tr.fire > 0.01f && Random.value < Mathf.Clamp01(tr.fire * 1.5f);
            float smoke = Mathf.Clamp01(tr.smoke);
            if (vacuum)
            {
                o.r = size * Random.Range(0.8f, 1.3f);
                o.heat = Random.Range(0.6f, 0.9f); o.cool = 4f; o.flame = 1f;
                o.flags = Ballistic; o.grow = 1.2f; o.life = 0.6f;
                Born(i);
                return true;
            }
            o.drag = 4.5f; o.kloss = 4f;
            o.turb = 0.7f;
            float tone = Mathf.Clamp(tr.shade, 0.02f, 0.9f) * Random.Range(0.8f, 1.2f);
            float brown = Mathf.Clamp01(tr.warm);
            o.ar = tone; o.ag = tone * (1f - 0.12f * brown); o.ab = tone * (1f - 0.28f * brown);
            if (burning)
            {
                o.r = size * Random.Range(0.8f, 1.4f) * (1f + 0.6f * tr.fire) + 0.05f;
                o.heat = Random.Range(0.7f, 0.95f) * Mathf.Lerp(0.8f, 1f, tr.fire);
                o.cool = 3f;
                o.flame = 1f;
                o.grow = 0.08f;
                o.rise = 0.7f;
                o.mass = Random.Range(0.6f, 1.3f) * o.r * o.r * 4f * Mathf.Max(0.3f, smoke);
                o.life = Random.Range(2.5f, 6f) * tr.lasts / 8f * lasting;
            }
            else
            {
                // smoke alone: a wisp off a small piece is thin and soon gone; a smouldering one heavier and longer
                o.r = size * Random.Range(1f, 1.8f) + 0.12f;
                o.heat = 0f; o.flame = 0f;
                o.grow = 0.12f + 0.1f * (1f - smoke);
                o.rise = 0.25f + 0.4f * smoke;
                o.mass = Random.Range(0.4f, 1f) * o.r * o.r * 4f * Mathf.Lerp(0.12f, 1f, smoke);
                o.life = Random.Range(1.5f, 4f) * Mathf.Lerp(0.6f, 1.6f, smoke) * tr.lasts / 8f * lasting;
                o.fadeIn = 0.15f;
            }
            Born(i);
            return true;
        }
    }

    public static partial class Records
    {
        /// <summary>
        /// What is in a part goes up where it is, and the part itself is not destroyed: another mod keeps what is left of it (Fragmentation
        /// tears a tank open and leaves its top). This notes it now, as a part that died would be noted, for the game's explosion that is
        /// to be set off at that place in the same frame (FXMonger.Explode) to find: the blast is then made from what was in it, and is a
        /// crash's (or a collision's) at that speed. Nothing is noted of what it looked like: there are no pieces of it to make here.
        /// </summary>
        public static void Burst(Part p, Vector3d world, float impact, bool collision) => Burst(p, world, impact, collision, true);

        /// <summary>The same, and with "contents" false a blow with nothing going up: the dust and sparks of a crash at that speed, the part's weight behind it.</summary>
        public static void Burst(Part p, Vector3d world, float impact, bool collision, bool contents)
        {
            try
            {
                if (p == null) return;
                var d = new Death { frame = Time.frameCount, world = world, name = p.partInfo != null ? p.partInfo.name : p.name };
                CelestialBody body = p.vessel != null ? p.vessel.mainBody : FlightGlobals.currentMainBody;
                d.velocity = OverGround(p, body);
                d.dry = Mathf.Max(0.005f, p.mass);
                Vector3 box = p.prefabSize;
                d.size = Mathf.Clamp(Mathf.Max(box.x, Mathf.Max(box.y, box.z)) * 0.5f, 0.2f, 6f);
                if (box.sqrMagnitude < 1e-4f) d.size = Mathf.Clamp(0.5f * Mathf.Pow(d.dry, 0.33f) + 0.3f, 0.3f, 4f);
                if (contents && p.Resources != null)
                    foreach (PartResource r in p.Resources)
                    {
                        if (r == null || r.info == null || r.amount <= 0) continue;
                        Sort(d, r.resourceName, r.amount * r.info.density * 1000.0, r.amount);
                    }
                d.crewed = false;
                d.splashed = p.vessel != null && p.vessel.Splashed;
                d.cause = collision ? Cause.Collision : Cause.Crash;
                d.impact = impact;
                Add(d);
            }
            catch (System.Exception ex) { Addon.Log("could not note a part's burst: " + ex.Message); }
        }
    }
}
