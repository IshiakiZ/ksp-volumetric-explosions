using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VolumetricExplosions
{
    public enum Cause { Unknown, Crash, Collision, Overheat, Pressure, GForce, Exhaust }

    /// <summary>What one destroyed part was, taken down in the moment before it went.</summary>
    public sealed class Death
    {
        public int frame;
        public Vector3d world;                  // where, in the scene's coordinates of that frame
        public Vector3d velocity;               // over the ground, in the scene's axes
        public string name;
        public float dry, size;                 // tonnes without contents; rough radius in metres
        public double fuel, oxidizer, solid, mono, gas, ore, vapour;   // kilograms
        public double charge;                   // units of electric charge
        public bool hydrogen, running, crewed, splashed;
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
                d.velocity = OverGround(p, body);
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
                Add(d);
            }
            catch (Exception ex) { Addon.Log("could not note a part's end: " + ex.Message); }
        }

        static void Add(Death d)
        {
            int now = Time.frameCount;
            for (int n = deaths.Count - 1; n >= 0; n--) if (now - deaths[n].frame > 3) deaths.RemoveAt(n);
            if (why.Count > 256) why.Clear();
            deaths.Add(d);
        }

        /// <summary>How fast the part was going over the ground, in the scene's axes.</summary>
        static Vector3d OverGround(Part p, CelestialBody body)
        {
            Rigidbody rb = p.Rigidbody;
            if (rb == null || body == null) return p.vessel != null ? p.vessel.srf_velocity : Vector3d.zero;
            Vector3d v = (Vector3d)rb.velocity + Krakensbane.GetFrameVelocity();
            // Low down the scene turns with the planet and that is already speed over the ground; higher up it is not.
            if (!body.inverseRotation) v -= body.getRFrmVel(p.transform.position);
            return v;
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
            // Other mods' resources, by what their names say.
            else if (name.Contains("hydrogen")) { d.fuel += kg; d.hydrogen = true; }
            else if (name.Contains("oxygen") || name.Contains("oxidi") || name.Contains("nto") || name.Contains("n2o4") || name.Contains("htp") || name.Contains("nitrous")) d.oxidizer += kg;
            else if (name.Contains("solid")) d.solid += kg;
            else if (name.Contains("hydrazine") || name.Contains("mono")) d.mono += kg;
            else if (name.Contains("methane") || name.Contains("kerosene") || name.Contains("methalox") || name.Contains("fuel") || name.Contains("mmh") || name.Contains("udmh") || name.Contains("aerozine") || name.Contains("ethanol") || name.Contains("karbonite")) d.fuel += kg;
            else if (name.Contains("argon") || name.Contains("krypton") || name.Contains("xenon") || name.Contains("nitrogen") || name.Contains("helium") || name.Contains("carbondioxide")) d.gas += kg;
            else if (name.Contains("water") || name.Contains("ammonia")) d.vapour += kg;
            else if (name.Contains("ore") || name.Contains("rock") || name.Contains("dirt") || name.Contains("metal") || name.Contains("regolith")) d.ore += kg;
        }

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
        /// along east, north and up.
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
                    case "hydrogen": d.hydrogen = value != 0; break;
                    case "dry": d.dry = (float)value; break;
                    case "size": d.size = (float)value; break;
                    case "thrust": d.thrust = (float)value; d.running = value > 0; break;
                    case "speed": d.impact = (float)value; break;
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
