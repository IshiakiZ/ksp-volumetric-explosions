using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VolumetricExplosions
{
    public enum Cause { Unknown, Crash, Collision, Overheat, Pressure, GForce, Exhaust, Collapse }     // (Collapse since 2026-11-19: a building of the space centre coming down, see Razing.Dust)

    /// <summary>What one destroyed part was, taken down in the moment before it went.</summary>
    public sealed class Death
    {
        public int frame;
        public Vector3d world;                  // where, in the scene's coordinates of that frame
        public Vector3d velocity;               // over the ground, in the scene's axes
        public string name;
        public float dry, size;                 // tonnes without contents; rough radius in metres
        public float tall;                      // (since 2026-11-19) how high a building that came down stood, metres (see Razing.Dust); nought for a part
        public double fuel, oxidizer, solid, mono, gas, ore, vapour;   // kilograms
        public double charge;                   // units of electric charge
        public double explosive;                // (since 0.10.0) kilograms of TNT a warhead or a shell set off (BD Armory's: see Warheads)
        public bool hydrogen, running, crewed, splashed;
        public bool flung;                      // (since 2026-11-19) going faster than its ship can have been going when it died: thrown by the physics in its last step (see Records.Pace)
        public float thrust;
        public Cause cause;
        public float impact;                    // metres a second, for a crash or a collision
        public bool taken;
        public List<Shell> shells;              // what it looked like, for the pieces it may leave
        public Vector3 at, going;               // where it was and how it was moving, in the frame of the Site its explosion went to
    }

    /// <summary>
    /// The game tells an explosion effect only where and how hard. Everything else that decides what the
    /// explosion should look like (what was in the tank, whether it hit the ground or burned up) is noted
    /// here from the game's events, in the same frame, and matched to the effect by place.
    /// </summary>
    public static partial class Records
    {
        struct Why { public Cause cause; public float value; public int frame; }

        static readonly Dictionary<uint, Why> why = new Dictionary<uint, Why>();
        static readonly List<Death> deaths = new List<Death>();

        /// <summary>The game's events will only call methods that belong to an object, so the callbacks live on this one.</summary>
        sealed class Ears
        {
            public void WillDie(Part p) => OnWillDie(p);
            public void Crash(EventReport r) => Note(r, Cause.Crash, true);
            public void Collision(EventReport r) => Note(r, Cause.Collision, true);
            public void Overheat(EventReport r) => Note(r, Cause.Overheat, false);
            public void Pressure(EventReport r) => Note(r, Cause.Pressure, false);
            public void G(EventReport r) => Note(r, Cause.GForce, false);
            public void Exhaust(EventReport r) => Note(r, Cause.Exhaust, false);
        }

        static Ears ears;

        public static void Hook()
        {
            if (ears != null) Unhook();
            ears = new Ears();
            GameEvents.onPartWillDie.Add(ears.WillDie);
            GameEvents.onCrash.Add(ears.Crash);
            GameEvents.onCrashSplashdown.Add(ears.Crash);
            GameEvents.onCollision.Add(ears.Collision);
            GameEvents.onOverheat.Add(ears.Overheat);
            GameEvents.onOverPressure.Add(ears.Pressure);
            GameEvents.onOverG.Add(ears.G);
            GameEvents.onSplashDamage.Add(ears.Exhaust);
        }

        public static void Unhook()
        {
            if (ears != null)
            {
                GameEvents.onPartWillDie.Remove(ears.WillDie);
                GameEvents.onCrash.Remove(ears.Crash);
                GameEvents.onCrashSplashdown.Remove(ears.Crash);
                GameEvents.onCollision.Remove(ears.Collision);
                GameEvents.onOverheat.Remove(ears.Overheat);
                GameEvents.onOverPressure.Remove(ears.Pressure);
                GameEvents.onOverG.Remove(ears.G);
                GameEvents.onSplashDamage.Remove(ears.Exhaust);
                ears = null;
            }
            why.Clear();
            deaths.Clear();
            paces.Clear();
        }

        static void Note(EventReport report, Cause cause, bool withValue)
        {
            if (report == null || report.origin == null) return;
            why[report.origin.flightID] = new Why { cause = cause, value = withValue ? report.param : 0f, frame = Time.frameCount };
        }

        static void OnWillDie(Part p)
        {
            try
            {
                if (p == null || p.transform == null) return;
                var d = new Death { frame = Time.frameCount, world = p.transform.position, name = p.partInfo != null ? p.partInfo.name : p.name };
                CelestialBody body = p.vessel != null ? p.vessel.mainBody : FlightGlobals.currentMainBody;
                d.velocity = OverGround(p, body, out float most, out d.flung);
                d.dry = Mathf.Max(0.005f, p.mass);
                Vector3 box = p.prefabSize;
                d.size = Mathf.Clamp(Mathf.Max(box.x, Mathf.Max(box.y, box.z)) * 0.5f, 0.2f, 6f);
                if (box.sqrMagnitude < 1e-4f) d.size = Mathf.Clamp(0.5f * Mathf.Pow(d.dry, 0.33f) + 0.3f, 0.3f, 4f);
                if (p.Resources != null)
                    foreach (PartResource r in p.Resources)
                    {
                        if (r == null || r.info == null || r.amount <= 0) continue;
                        Sort(d, r.resourceName, r.amount * r.info.density * 1000.0, r.amount);
                    }
                for (int m = 0; m < p.Modules.Count; m++)
                    if (p.Modules[m] is ModuleEngines engine && engine.EngineIgnited && engine.finalThrust > 0.5f) { d.running = true; d.thrust += engine.finalThrust; }
                d.crewed = p.protoModuleCrew != null && p.protoModuleCrew.Count > 0;
                d.splashed = p.vessel != null && p.vessel.Splashed;
                if (Settings.Debris) d.shells = Shell.Of(p);
                if (why.TryGetValue(p.flightID, out Why reason) && Time.frameCount - reason.frame <= 3)
                {
                    d.cause = reason.cause;
                    d.impact = reason.value;
                    why.Remove(p.flightID);
                }
                else if (p.temperature > p.maxTemp * 0.97 || (p.skinMaxTemp > 0 && p.skinTemperature > p.skinMaxTemp * 0.97)) d.cause = Cause.Overheat;
                // (2026-11-19: the game's speed for a crash or a collision is its physics' as well, and no harder than two ships at
                // their believed speeds can strike each other. A Kerbal X torn out of its launch clamps by a test that moved it, set
                // going at twenty-five metres a second, struck the runway at twenty-five to twenty-seven thousand by the game's word.
                // See Pace.)
                if (most > 0f && d.impact > 2f * most) d.impact = 2f * most;
#if DEV
                if (most < 0f && (d.impact > 500f || d.velocity.magnitude > 500.0)) Addon.Log("dev: no believed speed for " + d.name + " (" + d.cause + " at " + d.impact.ToString("F0") + ", going " + d.velocity.magnitude.ToString("F0") + "): " + PaceSaid(p));
#endif
                Add(d);
            }
            catch (Exception ex) { Addon.Log("could not note a part's end: " + ex.Message); }
        }

        /// <summary>(Since 0.10.0.) What another mod set off here this frame, taken down as if a part had died there (see Warheads).</summary>
        internal static void Set(Death d) => Add(d);

        /// <summary>(Since 0.10.0.) Whether a part that died this frame, not yet taken by an explosion, was within so many metres of here.</summary>
        internal static bool Near(Vector3d world, double within)
        {
            int now = Time.frameCount;
            foreach (Death d in deaths)
                if (!d.taken && now - d.frame <= 1 && d.explosive <= 0 && (d.world - world).sqrMagnitude < within * within) return true;
            return false;
        }

        static void Add(Death d)
        {
            int now = Time.frameCount;
            for (int n = deaths.Count - 1; n >= 0; n--) if (now - deaths[n].frame > 3) deaths.RemoveAt(n);
            if (why.Count > 256) why.Clear();
            deaths.Add(d);
        }

        /// <summary>How fast the part was going over the ground, in the scene's axes: no faster than its ship can have been going (see Pace).</summary>
        static Vector3d OverGround(Part p, CelestialBody body) => OverGround(p, body, out _, out _);

        /// <summary>(Since 2026-11-19.) The same, with the most its ship's believed speed allows it (below nought where no ship near it
        /// has been watched), and whether it was going faster than that: thrown by the physics in its last step.</summary>
        static Vector3d OverGround(Part p, CelestialBody body, out float most, out bool flung)
        {
            most = -1f; flung = false;
            Rigidbody rb = p.Rigidbody;
            if (rb == null || body == null) return p.vessel != null ? Believed(p, p.vessel.srf_velocity, out most, out flung) : Vector3d.zero;
            Vector3d v = (Vector3d)rb.velocity + Krakensbane.GetFrameVelocity();
            // Low down the scene turns with the planet and that is already speed over the ground; higher up it is not.
            if (!body.inverseRotation) v -= body.getRFrmVel(p.transform.position);
            return Believed(p, v, out most, out flung);
        }

        /// <summary>
        /// (Since 2026-11-19.) How fast each ship in the physics range was going, as far as it can be believed: its speed over the ground,
        /// followed upward no faster than sixty times Kerbin's pull (a missile's boost is some thirty to fifty), downward at once. The game's
        /// physics can throw a part at hundreds or thousands of metres a second for the step before it dies (parts pushed into each other or
        /// into the ground are pushed out at whatever speed undoes it in one step), and through the ground. The blast took that as the
        /// wreck's speed, and its fireball flew on with it: a Kerbal X thrown along the runway at fifty metres a second laid fireballs going at
        /// up to two thousand, and a second after it the sky over the crash was strewn with dozens of round balls of fire for hundreds of
        /// metres, each a fireball's puffs strung out on its way (the films of that day, trail_dump1 to 3, with the development build's
        /// Air.Fires and Air.Blasts). A dying part's speed is now no more than a quarter again its ship's believed speed and twenty-five
        /// metres a second: a ship new to the physics range or just come off another (a piece torn off in the crash) starts from the fastest
        /// believed of those within half a kilometre of it, and failing that of those anywhere in the physics range.
        /// (Found later that day: those films' Kerbal X stood in its launch clamps, and the test that threw it, moving it with the clamps
        /// holding it, tore it out of them; every part went flying at up to tens of millions of metres a second in that one step. With the
        /// clamps let go first it slid along the runway at twenty-five metres a second, as thrown, and no part's speed was cut. So that swarm
        /// was the test's; this stays as a guard against the physics doing the same to a craft caught in something as it is moved hard.)
        /// </summary>
        struct Pace { public float speed, t; public Vector3d at; }
        static readonly Dictionary<uint, Pace> paces = new Dictionary<uint, Pace>();
        static readonly List<uint> pacesGone = new List<uint>();
        const float Speeding = 600f;            // (m/s each second: how fast a believed speed may rise)
        const float PaceSlack = 25f;             // (m/s a part may go beyond a quarter again its ship's believed speed)
#if DEV
        internal static int SpeedsCut; internal static float SpeedCutMost;     // (development build: how many dying parts' speeds were cut, and the most a speed was, see Air.Blasts)
#endif

        /// <summary>In each physics step: every ship's believed speed (see Pace).</summary>
        internal static void Watch()
        {
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return;
            float now = Time.fixedTime, step = Mathf.Max(Time.fixedDeltaTime, 1e-3f);
            for (int n = 0; n < loaded.Count; n++)
            {
                Vessel v = loaded[n];
                if (v == null || v.packed) continue;
                float speed = (float)v.srf_velocity.magnitude;
                Vector3d at = v.CoMD;
                if (paces.TryGetValue(v.persistentId, out Pace was) && now - was.t < 1f)
                {
                    if (now <= was.t) continue;                                             // (the same step once more)
                    speed = Mathf.Min(speed, was.speed + Speeding * (now - was.t));
                }
                else
                {
                    float near = Nearby(at, now, v.persistentId);
                    if (near >= 0f) speed = Mathf.Min(speed, near + Speeding * step);
                }
                paces[v.persistentId] = new Pace { speed = speed, t = now, at = at };
            }
            if (paces.Count > 64)
            {
                pacesGone.Clear();
                foreach (KeyValuePair<uint, Pace> kv in paces) if (now - kv.Value.t > 5f || now < kv.Value.t) pacesGone.Add(kv.Key);
                for (int n = 0; n < pacesGone.Count; n++) paces.Remove(pacesGone[n]);
            }
        }

#if DEV
        static string PaceSaid(Part p)
        {
            float now = Time.fixedTime;
            string s = "now " + now.ToString("F2") + ", vessel " + (p.vessel == null ? "none" : p.vessel.persistentId + (p.vessel.packed ? " packed" : "") + (p.vessel.loaded ? "" : " unloaded"));
            if (p.vessel != null && paces.TryGetValue(p.vessel.persistentId, out Pace own)) s += ", its pace " + own.speed.ToString("F0") + " from " + (now - own.t).ToString("F2") + " s ago";
            else s += ", no pace of its own";
            float bestFar = float.MaxValue; Pace best = default(Pace);
            foreach (KeyValuePair<uint, Pace> kv in paces) { float far = (float)(kv.Value.at - (Vector3d)p.transform.position).magnitude; if (far < bestFar) { bestFar = far; best = kv.Value; } }
            return s + ", " + paces.Count + " paces, the nearest " + (bestFar < float.MaxValue ? bestFar.ToString("F0") + " m off, " + (now - best.t).ToString("F2") + " s ago, " + best.speed.ToString("F0") + " m/s" : "none");
        }
#endif

        /// <summary>The fastest believed speed of the ships seen in the last step or two within half a kilometre of here (or so far); below nought if none.</summary>
        static float Nearby(Vector3d at, float now, uint not, double within = 500.0)
        {
            float best = -1f;
            foreach (KeyValuePair<uint, Pace> kv in paces)
            {
                if (kv.Key == not || now - kv.Value.t > 0.1f || now < kv.Value.t) continue;
                if ((kv.Value.at - at).sqrMagnitude < within * within && kv.Value.speed > best) best = kv.Value.speed;
            }
            return best;
        }

        /// <summary>A dying part's speed, cut to what its ship's believed speed allows (see Pace). As it was where no ship near it has been watched.</summary>
        static Vector3d Believed(Part p, Vector3d v, out float most, out bool flung)
        {
            float now = Time.fixedTime, ship = -1f;
            most = -1f; flung = false;
            if (p.vessel != null && paces.TryGetValue(p.vessel.persistentId, out Pace was) && now - was.t < 0.5f && now >= was.t) ship = was.speed;
            if (ship < 0f) ship = Nearby(p.transform.position, now, 0u);
            // (2026-11-19: and failing that the fastest anywhere in the physics range. A Kerbal X torn out of its launch clamps by
            // a test that moved it burst in one step of the physics, every part a piece of its own flung at thirty to sixty
            // kilometres a second, and each died 550 to 1250 metres from where the craft had been watched, in the sea or the sky,
            // where none of the ships was within half a kilometre to lend it a speed.)
            if (ship < 0f) ship = Nearby(p.transform.position, now, 0u, double.MaxValue);
            if (ship < 0f) return v;
            most = PaceSlack + 1.25f * ship;
            double speed = v.magnitude;
            if (speed <= most) return v;
            flung = true;
#if DEV
            SpeedsCut++; SpeedCutMost = Mathf.Max(SpeedCutMost, (float)speed);
#endif
            return v * (most / speed);
        }

        static void Sort(Death d, string resource, double kg, double units)
        {
            string name = resource.ToLowerInvariant();
            if (name == "electriccharge") d.charge += units;
            else if (name == "liquidfuel") d.fuel += kg;
            else if (name == "oxidizer") d.oxidizer += kg;
            else if (name == "solidfuel") d.solid += kg;
            else if (name == "monopropellant") d.mono += kg;
            else if (name == "xenongas") d.gas += kg;
            else if (name == "ore") d.ore += kg;
            else if (name == "ablator" || name == "intakeair") { }
            // Other mods' resources: as resources.cfg lists them (see Resources.cs), or else by what their names say.
            else if (Listed(d, name, kg, units)) { }
            else if (name.Contains("hydrogen")) { d.fuel += kg; d.hydrogen = true; }
            else if (name.Contains("oxygen") || name.Contains("oxidi") || name.Contains("nto") || name.Contains("n2o4") || name.Contains("htp") || name.Contains("nitrous")) d.oxidizer += kg;
            else if (name.Contains("solid")) d.solid += kg;
            else if (name.Contains("hydrazine") || name.Contains("mono")) d.mono += kg;
            else if (name.Contains("methane") || name.Contains("kerosene") || name.Contains("methalox") || name.Contains("fuel") || name.Contains("mmh") || name.Contains("udmh") || name.Contains("aerozine") || name.Contains("ethanol") || name.Contains("karbonite")) d.fuel += kg;
            else if (name.Contains("argon") || name.Contains("krypton") || name.Contains("xenon") || name.Contains("nitrogen") || name.Contains("helium") || name.Contains("carbondioxide")) d.gas += kg;
            else if (name.Contains("water") || name.Contains("ammonia")) d.vapour += kg;
            else if (name.Contains("ore") || name.Contains("rock") || name.Contains("dirt") || name.Contains("metal") || name.Contains("regolith")) d.ore += kg;
        }

#if DEV
        /// <summary>(Development build, 2026-11-19: for a blast with no part found for it, the nearest part that died in the last few frames, how far and how many frames ago, and whether it was handed out already.)</summary>
        internal static string NearestSaid(Vector3d world)
        {
            int now = Time.frameCount; double best = double.MaxValue; Death got = null;
            for (int n = 0; n < deaths.Count; n++)
            {
                Death d = deaths[n];
                if (now - d.frame > 5) continue;
                double far = (d.world - world).magnitude;
                if (far < best) { best = far; got = d; }
            }
            return got == null ? "no part died in the last five frames" : "the nearest part that died, " + got.name + ", " + best.ToString("F0") + " m off, " + (now - got.frame) + " frames ago" + (got.taken ? ", taken already" : "");
        }
#endif

        /// <summary>The parts that died at this place just now. Each is handed out once.</summary>
        public static void Take(Vector3d world, List<Death> into)
        {
            into.Clear();
            int now = Time.frameCount;
            double slack = Krakensbane.GetFrameVelocity().magnitude * 0.05;
            for (int n = 0; n < deaths.Count; n++)
            {
                Death d = deaths[n];
                if (d.taken || now - d.frame > 1) continue;
                // The game merges blasts within ten metres of each other into one, placed between them.
                double reach = 14.0 + (now != d.frame ? slack : 0.0);
                if ((d.world - world).sqrMagnitude > reach * reach) continue;
                d.taken = true;
                into.Add(d);
            }
        }

        /// <summary>
        /// For trying looks out without flying a crash: a made-up part that "dies" this frame, described as
        /// words such as "fuel=1800 oxidizer=2200 cause=crash speed=60 east=40". Speeds are metres a second
        /// along east, north and up; tnt is kilograms of high explosive set off with it (a warhead's, as BD Armory's).
        /// </summary>
        public static void Fake(string words, Vector3d world)
        {
            var d = new Death { frame = Time.frameCount, world = world, name = "test", dry = 0.5f, size = 1f };
            if (FlightGlobals.ActiveVessel != null) d.velocity = FlightGlobals.ActiveVessel.srf_velocity;      // it starts out moving as the ship being flown does
            CelestialBody body = FlightGlobals.currentMainBody;
            Vector3d up = body != null ? FlightGlobals.getUpAxis(body, world) : Vector3d.up;
            Vector3d east = body != null ? Vector3d.Cross(up, body.transform.up).normalized : Vector3d.right;
            Vector3d north = Vector3d.Cross(east, up);
            foreach (string word in (words ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = word.IndexOf('=');
                if (eq <= 0) continue;
                string key = word.Substring(0, eq).ToLowerInvariant(), text = word.Substring(eq + 1);
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                switch (key)
                {
                    case "fuel": d.fuel = value; break;
                    case "oxidizer": d.oxidizer = value; break;
                    case "solid": d.solid = value; break;
                    case "mono": d.mono = value; break;
                    case "gas": d.gas = value; break;
                    case "ore": d.ore = value; break;
                    case "vapour": d.vapour = value; break;
                    case "charge": d.charge = value; break;
                    case "tnt": d.explosive = value; break;
                    case "hydrogen": d.hydrogen = value != 0; break;
                    case "dry": d.dry = (float)value; break;
                    case "size": d.size = (float)value; break;
                    case "tall": d.tall = (float)value; break;
                    case "thrust": d.thrust = (float)value; d.running = value > 0; break;
                    case "speed": d.impact = (float)value; break;
                    case "rest": d.velocity = Vector3d.zero; break;            // (still on the ground, not moving as the ship flown does: a building's dust, see Razing.Dust)
                    case "east": d.velocity += east * value; break;
                    case "north": d.velocity += north * value; break;
                    case "up": d.velocity += up * value; break;
                    case "cause":
                        if (Enum.TryParse(text, true, out Cause cause)) d.cause = cause;
                        break;
                }
            }
            Add(d);
        }
    }
}
