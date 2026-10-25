using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosions
{
    public sealed partial class Site
    {
        // ================================================================== (since 0.9.0) a blast's push: a setting, off as installed

        /// <summary>
        /// A blast that pushes, and breaks what it knocks harder than it can take ("blasts_push" in the settings file: off as
        /// installed, as it changes the game). Asked for in the study of other mods (BDArmory's blasts push and break parts by the
        /// published airblast equations); the equations here are from the papers, not from any mod.
        ///
        /// What reaches a thing from a blast is the blow of the squeezed air going past it: an impulse, the pressure over the time it
        /// lasts, in pascal seconds. In the open air it is Kinney and Graham's (1985, "Explosive Shocks in Air"), by the distance over
        /// the cube root of the charge in kilograms of TNT, and for thinner air as Sachs's scaling has it: distances as if at sea
        /// level by the cube root of the air's pressure, the blow less by its pressure to the two thirds and more by how much
        /// slower sound is. (Their fit is measured with the blast's own gases in it, near the charge too.) Where the air is thin
        /// the blast's own gases strike before it has stopped them (until they have swept up as much air as they weigh): as much of
        /// their going out falls on each square metre as the sphere they have spread to; in a vacuum that is all there is, and it
        /// reaches only a few tens of metres. The charge is the one the mod gives
        /// every blast (a tenth of the propellant that burns, as is reckoned for liquid propellants going off on a pad), and as much
        /// again and four fifths more on the ground, half of whose blast comes back up off it.
        ///
        /// The blow comes as the front goes by (it leaves faster than sound where it squeezes the air hard, slowing to sound's speed
        /// as it spreads), once to each part: on the area the part turns to the blast (its colliders' extent, across the way the
        /// blast comes), more for the air piling up against it as it strikes (as a shock is turned back off a wall: twice the blow
        /// where it squeezes the air a little, up to eight times where it squeezes it hard; six tenths of that for a part, which is
        /// not a wall); less than a third of it where something stands
        /// between (another part, the ground, a building). It knocks the part away from the blast, and turns it. A part knocked
        /// faster than it could hit the ground and survive (its crash tolerance) is broken, as a crash breaks it, and its own
        /// propellant goes up after it: so a tank's blast can take the tank beside it, as on a real pad. A light, wide thing (a
        /// solar panel, a fairing, a wing) is broken further off than a heavy one.
        /// </summary>
        struct Knock
        {
            public Vector3 at;                  // in the site's axes
            public float charge;                // kilograms of TNT, the ground's share counted
            public float propellant;            // kilograms of gas thrown out that strike (as much as the charge)
            public float pressure, sound;       // the air's, there: pascals, metres a second (no air: nought)
            public float stop;                  // how far out the blast's own gas is stopped by the air (no air: everywhere)
            public float radius, reach;
            public HashSet<uint> hit;
        }

        readonly List<Knock> knocks = new List<Knock>();
        static readonly List<Part> breaking = new List<Part>();
        const float SeaLevel = 101325f;
        const int Solid = (1 << 0) | (1 << 15);         // parts; the ground and buildings

        /// <summary>A blast's push, begun: its charge, and the air it goes out into.</summary>
        void StartKnock(Plan pl, Vector3 c, bool onGround)
        {
#if DEV
            if (knockSaid.Length < 6000) knockSaid += "\nA BLAST (pushes " + Settings.Knock + ", the warm-up's " + pl.warm + ", charge " + pl.charge.ToString("F1") + ", fire " + pl.fire.ToString("F1") + " kg)";
#endif
            if (!Settings.Knock || pl.warm || !HighLogic.LoadedSceneIsFlight) return;
            float tnt = 0.1f * Mathf.Max(pl.charge, pl.fire);
            if (tnt < 0.05f) return;
            double kPa = body != null && body.atmosphere ? FlightGlobals.getStaticPressure(frame.ToWorld(c), body) : 0.0;
            float pressure = (float)(kPa * 1000.0), density = 1.225f * air;
            float charge = onGround ? 1.8f * tnt : tnt, cube = Mathf.Pow(charge, 1f / 3f);
            // (the gas that strikes is as much as the charge itself: the rest of what burns does so in the fireball, slowly)
            float propellant = charge;
            float stop = density > 1e-5f ? Mathf.Pow(3f * propellant / (4f * Mathf.PI * density), 1f / 3f) : float.PositiveInfinity;
            // (as far as the blow could still knock anything: in air some sixty times the charge's cube root at sea level, by Sachs
            // further in thinner air; the gas alone a few tens of metres)
            float reach = 40f * cube;
            if (pressure > 100f) reach = Mathf.Max(reach, 60f * cube * Mathf.Pow(SeaLevel / pressure, 1f / 3f));
            knocks.Add(new Knock { at = c, charge = charge, propellant = propellant, pressure = pressure, sound = sound > 0f ? sound : 340f, stop = stop, radius = 0f, reach = Mathf.Min(reach, 3000f), hit = new HashSet<uint>() });
#if DEV
            Vessel me = FlightGlobals.ActiveVessel;
            knockSaid += ": " + charge.ToString("F1") + " kg of TNT" + (onGround ? " (on the ground)" : "") + " (charge " + pl.charge.ToString("F0") + ", fire " + pl.fire.ToString("F0") + " kg), the air at " + (pressure / 1000f).ToString("F2") + " kPa, its gas stopped at " + (float.IsInfinity(stop) ? "nothing" : stop.ToString("F1") + " m") + ", reaching " + reach.ToString("F0") + " m, " + (me != null ? Vector3.Distance((Vector3)frame.ToWorld(c), me.CoM).ToString("F1") + " m from the craft flown" : "") + ":";
#endif
        }

        /// <summary>Each blast's front, carried out this frame, and what it passed knocked.</summary>
        void Knocks(float dt)
        {
            if (knocks.Count == 0) return;
            if (!Settings.Knock || !HighLogic.LoadedSceneIsFlight || FlightGlobals.VesselsLoaded == null) { knocks.Clear(); return; }
            // (nothing is knocked while the craft are on rails, in time warp: the front goes on, and passes them by)
            bool physics = TimeWarp.CurrentRate <= 1f || TimeWarp.WarpMode == TimeWarp.Modes.LOW;
            for (int n = knocks.Count - 1; n >= 0; n--)
            {
                Knock k = knocks[n];
                float before = k.radius;
                // (its front's speed: a shock's by how hard it squeezes the air, at least sound's; in a vacuum the gas's own)
                float speed = k.pressure > 100f ? k.sound * Mathf.Sqrt(1f + 6f / 7f * Over(k, Mathf.Max(k.radius, 0.2f))) : 2000f;
                k.radius = Mathf.Min(k.reach, k.radius + speed * Mathf.Max(dt, 0f));
                if (physics && k.radius > before)
                {
                    Vector3 middle = (Vector3)frame.ToWorld(k.at);
                    foreach (Vessel v in FlightGlobals.VesselsLoaded)
                    {
                        if (v == null || v.packed || v.state == Vessel.State.DEAD || v.parts == null) continue;
                        for (int i = 0; i < v.parts.Count; i++)
                        {
                            Part part = v.parts[i];
                            if (part == null || part.State == PartStates.DEAD) continue;
                            float d = Vector3.Distance(part.transform.position, middle);
                            if (d <= before || d > k.radius || k.hit.Contains(part.flightID)) continue;
                            k.hit.Add(part.flightID);
                            Strike(k, part, middle, d);
                        }
                    }
                }
#if DEV
                if (k.radius >= k.reach)
                {
                    string craft = "";
                    foreach (Vessel v in FlightGlobals.VesselsLoaded) if (v != null) craft += v.vesselName + (v.packed ? " packed" : "") + " " + v.state + " " + (v.parts != null ? v.parts.Count : -1) + "; ";
                    knockSaid += " [out to its end: " + k.hit.Count + " parts struck; the craft then: " + craft + "]";
                }
#endif
                if (k.radius >= k.reach) knocks.RemoveAt(n);
                else knocks[n] = k;
            }
            // (broken after the round, not in it: a part going changes the craft's list of parts)
            for (int i = 0; i < breaking.Count; i++) if (breaking[i] != null && breaking[i].State != PartStates.DEAD) breaking[i].explode();
            breaking.Clear();
        }

        /// <summary>One part struck by a blast's front.</summary>
        void Strike(Knock k, Part part, Vector3 middle, float d)
        {
            Vector3 at = part.transform.position;
            Vector3 way = d > 1e-3f ? (at - middle) / d : Vector3.up;
            float blow = Impulse(k, d, out float squeeze);
            // (something between the blast and the part takes most of it: another part, the ground, a building)
            bool shielded = Physics.Raycast(middle, way, out RaycastHit hit, Mathf.Max(0f, d - 0.25f), Solid, QueryTriggerInteraction.Ignore)
                            && hit.collider != null && hit.collider.GetComponentInParent<Part>() != part;
            if (shielded) blow *= 0.3f;
            float area = Area(part, way);
            float reflected = 2f + 6f * squeeze / (squeeze + 7f);
            float push = 0.6f * reflected * blow * area;                                           // N s
            float mass = Mathf.Max(1f, (float)(part.mass + part.GetResourceMass()) * 1000f);      // kg
            float knocked = push / mass;
            bool broken = knocked > part.crashTolerance;
            if (broken) breaking.Add(part);
            else
            {
                Rigidbody rb = part.rb != null ? part.rb : part.parent != null ? part.parent.rb : null;
                // (the game's masses are in tonnes: a push in kilonewton seconds)
                if (rb != null) rb.AddForceAtPosition(way * (push / 1000f), at, ForceMode.Impulse);
            }
#if DEV
            if (knockLines < 60 && knockSaid.Length < 6000)
            {
                knockLines++;
                knockSaid += "\n  " + part.partInfo.title + " " + d.ToString("F1") + " m off" + (shielded ? " (shielded)" : "") + ": " + blow.ToString("F0") + " Pa s on " + area.ToString("F2") + " m2, " + mass.ToString("F0") + " kg, knocked " + knocked.ToString("F2") + " m/s against " + part.crashTolerance.ToString("F0") + (broken ? ": broken" : "");
            }
#endif
        }

        /// <summary>The blow of the blast at a distance, in pascal seconds; and how hard its front squeezes the air there, as a share
        /// of the air's own pressure.</summary>
        static float Impulse(Knock k, float d, out float squeeze)
        {
            float cube = Mathf.Pow(k.charge, 1f / 3f), blow = 0f;
            squeeze = 0f;
            float thin = k.pressure / SeaLevel;
            if (k.pressure > 100f)
            {
                float z = Mathf.Max(d * Mathf.Pow(thin, 1f / 3f) / cube, 0.05f);
                // (bar milliseconds for a kilogram at sea level, to pascal seconds; Sachs's scaling)
                blow = ImpulseAt(z) * 100f * cube * Mathf.Pow(thin, 2f / 3f) * (340f / Mathf.Max(k.sound, 80f));
                squeeze = OverAt(z);
            }
            // (the gas itself, where the air is thin: none at a tenth of Kerbin's air at sea level and more, all of it in a vacuum)
            float bare = 1f - Mathf.Clamp01(thin / 0.1f);
            if (d < k.stop && bare > 0f)
            {
                float r = Mathf.Max(d, 0.5f);
                blow = Mathf.Max(blow, bare * Mathf.Sqrt(2f * 4.184e6f * k.charge * k.propellant) / (4f * Mathf.PI * r * r));
            }
            return blow;
        }

        /// <summary>How hard the front squeezes the air at a distance, as a share of the air's own pressure.</summary>
        static float Over(Knock k, float d)
        {
            float z = Mathf.Max(d * Mathf.Pow(k.pressure / SeaLevel, 1f / 3f) / Mathf.Pow(k.charge, 1f / 3f), 0.05f);
            return OverAt(z);
        }

        /// <summary>Kinney and Graham's peak overpressure of a blast in the open air, in atmospheres, at z metres for a kilogram of TNT.</summary>
        static float OverAt(float z)
        {
            float a = z / 4.5f, b = z / 0.048f, c = z / 0.32f, e = z / 1.35f;
            return 808f * (1f + a * a) / Mathf.Sqrt((1f + b * b) * (1f + c * c) * (1f + e * e));
        }

        /// <summary>Kinney and Graham's impulse of a blast in the open air, in bar milliseconds, at z metres for a kilogram of TNT.</summary>
        static float ImpulseAt(float z)
        {
            float a = z / 0.23f, b = z / 1.55f;
            return 0.067f * Mathf.Sqrt(1f + a * a * a * a) / (z * z * Mathf.Pow(1f + b * b * b, 1f / 3f));
        }

        /// <summary>The area a part turns to the blast: its colliders' extent, across the way the blast comes (not its children's).</summary>
        static float Area(Part part, Vector3 way)
        {
            Bounds all = default;
            bool any = false;
            foreach (Collider c in part.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger || c.GetComponentInParent<Part>() != part) continue;
                if (any) all.Encapsulate(c.bounds); else { all = c.bounds; any = true; }
            }
            if (!any) return 0.2f;
            Vector3 s = all.size;
            return Mathf.Max(0.02f, Mathf.Abs(way.x) * s.y * s.z + Mathf.Abs(way.y) * s.x * s.z + Mathf.Abs(way.z) * s.x * s.y);
        }

#if DEV
        static string knockSaid = "";
        static int knockLines;
        /// <summary>(Development build: the last blast's push, and the parts it struck.)</summary>
        public static string Knocked() { string s = knockSaid; knockSaid = ""; knockLines = 0; return s.Length > 0 ? s : "no blast has pushed since last asked"; }
#endif
    }
}
