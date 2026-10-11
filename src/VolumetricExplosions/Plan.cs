using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// What one explosion is going to be: decided once, from what blew up, why, and where, and then
    /// handed to the <see cref="Site"/> that sets the particles off.
    /// </summary>
    public sealed class Plan
    {
        // ---- where
        public CelestialBody body;
        public Vector3d world, up;
        public float air;                  // density of the air against Kerbin at sea level (0 in a vacuum)
        public float gravity;              // metres a second squared
        public bool oxygen, vacuum, space; // space: high up in a vacuum, where the cloud flies along with the wreck
        public bool ground;                // the blast touches the ground (or the water)
        public float height;               // metres above the ground or the water, when one is near
        public bool surfaceBelow;          // there is ground or water within reach below
        public Vector3d surfacePoint, surfaceNormal;
        public bool water, underwater, paved;
        public Color dust;                 // colour of the ground there
        public float daylight;             // 0 night to 1 day

        // ---- what
        public Vector3d velocity;          // of the wreck over the ground
        public Cause cause;
        public float impact;               // metres a second
        public float fire;                 // kilograms of propellant that burn
        public float radius;               // of the fireball, metres (0: no fire)
        public float reach;                // of the blast itself: dust, sparks, debris
        public float soot;                 // 0 clean flame to 1 thick black smoke
        public float smokeShade;           // how light the smoke is, 0.03 soot to 0.8 steam
        public int tint;                   // flame colours: 0 fuel, 1 solid propellant and burning metal, 2 monopropellant, 3 electric, 4 hydrogen
        public float vapour;               // kilograms of cold cloud that does not burn
        public float sparks, chunks, dirt; // how many of each to throw
        public bool whiteTrails;           // burning propellant leaves white trails, not dark ones
        public float burn;                 // seconds a fire stays burning on the ground afterwards
        public string label;
        public List<Death> wreck;          // the destroyed parts that may leave pieces
        public Vector3 at, going;          // the place and the wreck's motion, in the frame of the Site the explosion went to

        static readonly List<Death> parts = new List<Death>();

        public static Plan Make(Vector3d world, float strength)
        {
            Plan p = Where(world);
            Records.Take(world, parts);
            CelestialBody body = p.body;
            float below = p.height;
            What(p, strength, body, below);
            return p;
        }

        /// <summary>
        /// The place alone: its air, the ground or the water under it, the light. What a blast is made of comes after
        /// (see What); smoke that is no blast's, a running engine's, needs no more than this.
        /// </summary>
        public static Plan Where(Vector3d world)
        {
            var p = new Plan { world = world };
            p.body = FlightGlobals.currentMainBody ?? FlightGlobals.getMainBody(world);
            CelestialBody body = p.body;
            p.up = FlightGlobals.getUpAxis(body, world);
            p.gravity = (float)FlightGlobals.getGeeForceAtPosition(world, body).magnitude;
            double altitude = body.GetAltitude(world);

            // ---- the air
            double pressure = body.atmosphere ? FlightGlobals.getStaticPressure(world, body) : 0.0;
            double density = pressure > 0 ? FlightGlobals.getAtmDensity(pressure, FlightGlobals.getExternalTemperature(world, body), body) : 0.0;
            p.air = Mathf.Clamp((float)(density / 1.225), 0f, 8f);
            p.vacuum = p.air < 0.004f;
            p.oxygen = body.atmosphere && body.atmosphereContainsOxygen && p.air > 0.02f;

            // ---- the ground and the water
            Vector3 up = (Vector3)p.up;
            float below = float.MaxValue;
            if (Physics.Raycast((Vector3)world + up * 3f, -up, out RaycastHit hit, 700f, 1 << 15, QueryTriggerInteraction.Ignore))
            {
                below = Mathf.Max(0f, hit.distance - 3f);
                p.surfaceBelow = true;
                p.surfacePoint = (Vector3d)hit.point;
                p.surfaceNormal = (Vector3d)hit.normal;
                p.paved = hit.collider != null && (hit.collider.GetComponentInParent<PQSCity>() != null || hit.collider.GetComponentInParent<PQSCity2>() != null);
            }
            if (body.ocean && altitude < below)
            {
                // The sea is nearer than the sea bed.
                p.water = altitude < 600.0;
                p.underwater = altitude < -0.75;
                if (p.water)
                {
                    below = (float)Mathf.Max(0f, (float)altitude);
                    p.surfaceBelow = true;
                    p.surfacePoint = world - p.up * altitude;
                    p.surfaceNormal = p.up;
                    p.paved = false;
                }
            }
            p.height = below;
            p.space = p.vacuum && below > 2000f;
            // The colours below are as the ground looks on the screen; what the dust reflects is roughly the square of that.
            Color seen = Ground(body, world, p.paved);
            p.dust = new Color(seen.r * seen.r * 1.2f, seen.g * seen.g * 1.2f, seen.b * seen.b * 1.2f);

            Vector3d sunward = (Planetarium.fetch.Sun.position - world).normalized;
            p.daylight = Mathf.Clamp01((float)Vector3d.Dot(p.up, sunward) * 5f + 0.4f);
            return p;
        }

        /// <summary>What blew up there, and so what the blast is to be.</summary>
        static void What(Plan p, float strength, CelestialBody body, float below)
        {
            Vector3d world = p.world;
            // ---- what blew up
            double fuel = 0, oxidizer = 0, solid = 0, mono = 0, gas = 0, ore = 0, charge = 0, vapour = 0, dry = 0, thrust = 0;
            bool hydrogen = false, any = parts.Count > 0;
            Vector3d momentum = Vector3d.zero;
            int overheated = 0;
            foreach (Death d in parts)
            {
                fuel += d.fuel; oxidizer += d.oxidizer; solid += d.solid; mono += d.mono; gas += d.gas; ore += d.ore; charge += d.charge; vapour += d.vapour;
                dry += d.dry; thrust += d.thrust; hydrogen |= d.hydrogen;
                momentum += d.velocity * d.dry;
                if (d.cause == Cause.Overheat) overheated++;
                if (d.cause != Cause.Unknown && (p.cause == Cause.Unknown || d.impact > p.impact)) p.cause = d.cause;
                p.impact = Mathf.Max(p.impact, d.impact);
            }
            foreach (Death d in parts)
                if (d.shells != null) { if (p.wreck == null) p.wreck = new List<Death>(); p.wreck.Add(d); }
            if (any) p.velocity = momentum / System.Math.Max(1e-6, dry);
            else
            {
                Vessel near = Nearest(world);
                if (near != null) p.velocity = near.srf_velocity;
            }
            if (p.cause == Cause.Unknown && overheated > 0) p.cause = Cause.Overheat;
            float speed = (float)p.velocity.magnitude;
            if (p.impact <= 0f && (p.cause == Cause.Crash || p.cause == Cause.Collision)) p.impact = speed;

            // ---- what burns here
            double pair = System.Math.Min(fuel, oxidizer / 1.222);          // fuel that has its own oxidiser with it
            double spareFuel = fuel - pair, spareOxidizer = oxidizer - pair * 1.222;
            double burnsInAir = p.oxygen ? spareFuel * 0.7 * Mathf.Clamp01(p.air * 1.5f) : 0.0;
            double fromPair = pair * 2.222, fromSolid = solid * 0.8, fromMono = mono * 0.4;
            double fire = fromPair + burnsInAir + fromSolid + fromMono;
            vapour += (spareFuel - (p.oxygen ? spareFuel * 0.7 * Mathf.Clamp01(p.air * 1.5f) : 0.0)) + spareOxidizer * 0.5 + gas * 3.0;
            if (thrust > 0) fire += 4.0 + thrust * 0.05;                  // a running engine goes up with what is in its lines

            float soot = 0.8f, shade = 0.11f;
            p.tint = 0;
            if (fire > 0.5)
            {
                // Each propellant's share of the fire sets how dirty it burns and what the smoke looks like.
                double w = fromPair + burnsInAir + fromSolid + fromMono + 1e-6;
                soot = (float)((fromPair * 0.8 + burnsInAir * 1.0 + fromSolid * 0.5 + fromMono * 0.12) / w);
                shade = (float)((fromPair * 0.11 + burnsInAir * 0.08 + fromSolid * 0.45 + fromMono * 0.6) / w);
                if (fromSolid > fromPair + burnsInAir && fromSolid > fromMono) p.tint = 1;
                else if (fromMono > fromPair + burnsInAir) p.tint = 2;
                if (hydrogen) { p.tint = 4; soot *= 0.05f; shade = 0.7f; }
                p.whiteTrails = p.tint == 1;
            }

            if (!any)
            {
                // Nothing known about the source (another mod's explosion, a building): go by how hard the game says it was.
                fire = strength > 0.08f ? 1500.0 * Mathf.Pow(strength, 2.2f) : 0.0;
                dry = 0.2 + strength * 2.0;
            }
            else if (fire < 1.0 && charge > 40 && vapour < 1.0)
            {
                // A battery or a probe core: a blue-white electrical flash.
                fire = System.Math.Min(charge / 60.0, 10.0);
                p.tint = 3; soot = 0.15f; shade = 0.25f;
            }
            else if (fire < 1.0 && strength > 0.3f && vapour < 1.0 && ore < 1.0)
                fire = 20.0 * strength * strength;                         // an empty part still goes with a small bang
            if (p.cause == Cause.Overheat)
            {
                // Burning up rather than bursting: bright, clean, and showering sparks.
                fire += dry * 25.0;
                soot *= 0.35f;
                if (fromPair + burnsInAir < 5.0 && p.tint == 0) p.tint = 1;
            }
            if (p.underwater) { vapour += fire; fire = 0; }
            if (p.vacuum) soot = 0f;                                       // nothing for smoke to form in or hang in

            p.fire = (float)fire;
            p.soot = Mathf.Clamp01(soot);
            p.smokeShade = shade;
            p.vapour = (float)vapour;
            // Real fireballs are wider still (about 5.8 m times the cube root of the kilograms); this is that shape of law, a little smaller.
            p.radius = fire > 0.5 ? Mathf.Min(45f, Settings.Size * 1.75f * Mathf.Pow((float)fire, 0.29f)) : 0f;
            float heft = Mathf.Pow((float)System.Math.Max(0.02, dry), 0.33f);
            p.reach = Mathf.Max(p.radius, Settings.Size * (1.0f + 1.1f * heft + Mathf.Min(p.impact, 150f) * 0.03f));
            p.ground = p.surfaceBelow && below < p.reach * 0.9f + 1.5f;

            p.sparks = (14f + 22f * heft * (1f + Mathf.Min(p.impact, 120f) / 40f) + Mathf.Sqrt((float)solid) * 3f + Mathf.Sqrt((float)charge) * 2f) * (p.cause == Cause.Overheat ? 2.5f : 1f);
            p.chunks = p.radius > 1.5f ? Mathf.Min(18f, 2f + p.radius * 0.7f) + Mathf.Min(14f, Mathf.Sqrt((float)solid) * 0.35f) : 0f;
            p.dirt = p.ground && !p.water ? (p.paved ? 0.35f : 1f) * (1f + (float)ore / 300f) : 0f;
            bool spills = fromPair + burnsInAir > 20.0 && (p.oxygen || fromPair > 20.0);
            p.burn = p.ground && !p.water && !p.vacuum && spills ? Mathf.Clamp(2f + 0.4f * Mathf.Sqrt((float)(fromPair + burnsInAir)), 3f, 30f) * Mathf.Max(0.3f, Settings.Smoke) : 0f;

            p.label = (any ? parts.Count + " part(s)" : "unknown source") + ", " + p.cause + (p.impact > 0 ? " at " + p.impact.ToString("F0") + " m/s" : "") +
                ", fire " + p.fire.ToString("F0") + " kg (radius " + p.radius.ToString("F1") + " m, soot " + p.soot.ToString("F2") + ", tint " + p.tint + "), vapour " + p.vapour.ToString("F0") +
                " kg, air " + p.air.ToString("F3") + (p.oxygen ? " with oxygen" : "") + (p.space ? ", space" : "") + (p.ground ? (p.water ? ", on water" : p.paved ? ", on paving" : ", on the ground") : p.height < 1e6f ? ", " + p.height.ToString("F0") + " m up" : ", nothing below") +
                (p.underwater ? ", under water" : "") + ", " + body.bodyName;
            if (Settings.Log) Addon.Log("blast: " + p.label);
        }

        /// <summary>The loaded vessel closest to the blast: the wreck it came from, or what is left of it.</summary>
        static Vessel Nearest(Vector3d world)
        {
            Vessel best = null;
            double bestDistance = 150.0 * 150.0;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.rootPart == null) continue;
                double d = (v.CoMD - world).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = v; }
            }
            return best;
        }

        /// <summary>The colour of the dust a blast would raise here.</summary>
        internal static Color Ground(CelestialBody body, Vector3d world, bool paved)
        {
            if (paved) return new Color(0.30f, 0.29f, 0.27f);
            string biome = "";
            try { biome = ScienceUtil.GetExperimentBiome(body, body.GetLatitude(world), body.GetLongitude(world)) ?? ""; } catch { }
            biome = biome.ToLowerInvariant();
            if (biome.Contains("ice") || biome.Contains("pole") || biome.Contains("snow") || biome.Contains("glacier")) return new Color(0.72f, 0.74f, 0.78f);
            switch (body.bodyName)
            {
                case "Kerbin":
                    if (biome.Contains("desert") || biome.Contains("badlands") || biome.Contains("shore") || biome.Contains("beach")) return new Color(0.50f, 0.42f, 0.28f);
                    if (biome.Contains("tundra")) return new Color(0.50f, 0.48f, 0.44f);
                    if (biome.Contains("mountain")) return new Color(0.36f, 0.33f, 0.30f);
                    return new Color(0.30f, 0.25f, 0.17f);
                case "Mun": return new Color(0.33f, 0.33f, 0.34f);
                case "Minmus": return new Color(0.52f, 0.66f, 0.60f);
                case "Duna": return new Color(0.55f, 0.27f, 0.14f);
                case "Ike": return new Color(0.25f, 0.24f, 0.24f);
                case "Eve": return new Color(0.36f, 0.24f, 0.46f);
                case "Gilly": return new Color(0.40f, 0.33f, 0.27f);
                case "Moho": return new Color(0.36f, 0.26f, 0.20f);
                case "Dres": return new Color(0.42f, 0.40f, 0.38f);
                case "Laythe": return new Color(0.42f, 0.38f, 0.30f);
                case "Vall": return new Color(0.60f, 0.68f, 0.72f);
                case "Tylo": return new Color(0.55f, 0.53f, 0.50f);
                case "Bop": return new Color(0.32f, 0.26f, 0.20f);
                case "Pol": return new Color(0.60f, 0.55f, 0.35f);
                case "Eeloo": return new Color(0.70f, 0.72f, 0.75f);
            }
            return new Color(0.40f, 0.36f, 0.30f);
        }
    }
}
