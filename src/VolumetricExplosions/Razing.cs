using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// (Since 0.12.0.) Buildings brought down by a blast. The owner, 2026-11-16: "Make the nuke destroy all the buildings nearby if
    /// they blow it up and it's in the vicinity", and, asked which explosions, "Any strong blast".
    ///
    /// The game brings a building of the space centre down only when something strikes it hard enough. A blast strikes it too:
    /// what decides is how hard the front squeezes the air where the building stands. Ordinary buildings are wrecked at about
    /// five pounds to the square inch over the air's own pressure, a third of an atmosphere (Glasstone and Dolan, chapter 5:
    /// most buildings collapse there; reinforced concrete at two or three times that, which the game's buildings are not told
    /// apart by). So a blast brings down every building where its peak overpressure, as Kinney and Graham give it for the charge
    /// and the distance (Site.OverAt, Sachs's scaling for thinner air), reaches a third of an atmosphere: for a kiloton, half
    /// its yield as blast, within some 450 m; for 20 kt 1.2 km; for a megaton 4.5 km; for a big rocket's two tonnes of
    /// propellant going up on the pad, 50 m. Each falls when the front reaches it, at about the speed of sound, not on the
    /// instant. The game's own way of bringing one down is used (DestructibleBuilding.Demolish): its collapse, its smoke, its
    /// note in the flight's log, and its repair in the space centre as after any wreck.
    ///
    /// Not where the game is set to keep its buildings whole (the difficulty's "indestructible facilities"), nor with
    /// "blasts_raze = false" in the settings, nor where there is no air to carry a blast (there the gas of the charge itself
    /// would have to reach the building: within its own few tens of metres, which a crash there does anyway).
    /// </summary>
    internal static class Razing
    {
        /// <summary>The peak overpressure that wrecks a building, as a share of the air's own pressure at sea level (5 psi).</summary>
        const float Wrecks = 0.34f;

        struct Falls { public DestructibleBuilding building; public float at; }
        /// <summary>
        /// How many buildings have come down in this flight, by a blast or by the game's own doing: a patch of smoke sounds the
        /// ground under it again when this has changed (Site.Survey). Until then smoke lay for up to ten seconds on the roof
        /// of a building that was no longer there, cut off level in the air (the film of 2026-11-16, raze1/fuel-by-pad).
        /// </summary>
        internal static int Fallen;
        // (the game's events take an object's own methods: a static one is turned down with a null reference in the event's own
        // code, as Hush found on 2026-11-03, and as this did on its first try, which stopped the whole mod at its start)
        sealed class Hooks { internal void Fell(DestructibleBuilding b) { Fallen++; Note(b); Dust(b); } }
        // (Where a building has just come down, and when. The game draws a collapse with explosions of its own, which reach
        // this mod as blasts like any other: counted, each brought down the building beside it, and that one the next, a chain
        // through the whole launch pad from one tank going up beside it (the film of 2026-11-16, raze4). A blast that goes off
        // within a building that fell in the last twenty seconds, or within fifteen metres of it, is the collapse's own.)
        struct Fall { public Vector3 at; public float reach, when; }
        static readonly List<Fall> fell = new List<Fall>();
        static void Note(DestructibleBuilding b)
        {
            try
            {
                if (b == null) return;
                bool any = false;
                Bounds box = new Bounds(b.transform.position, Vector3.zero);
                foreach (Renderer r in b.GetComponentsInChildren<Renderer>(true)) { if (r == null) continue; if (any) box.Encapsulate(r.bounds); else { box = r.bounds; any = true; } }
                if (fell.Count > 64) fell.RemoveAt(0);
                fell.Add(new Fall { at = box.center, reach = Mathf.Min(box.extents.magnitude, 400f) + 15f, when = Time.time });
            }
            catch (Exception) { }
        }
        static bool OfACollapse(Vector3 at)
        {
            for (int n = fell.Count - 1; n >= 0; n--)
            {
                if (Time.time - fell[n].when > 20f) { fell.RemoveAt(n); continue; }
                if ((fell[n].at - at).sqrMagnitude < fell[n].reach * fell[n].reach) return true;
            }
            return false;
        }
#if DEV
        /// <summary>(Development build, 2026-11-19: the particle systems each building of the space centre plays as it is brought down,
        /// and as each piece of it falls, with their colours, sizes and materials; NAME for one building only.)</summary>
        public static string FxSaid(string name)
        {
            var say = new System.Text.StringBuilder();
            foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
            {
                if (b == null || (!string.IsNullOrEmpty(name) && b.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                say.Append(b.name).Append(" (").Append(b.id).Append("): ");
                Seq(say, "demolition", b.DemolitionFXPrefab);
                if (b.CollapsibleObjects != null)
                    foreach (DestructibleBuilding.CollapsibleObject c in b.CollapsibleObjects) if (c != null) Seq(say, "piece " + (c.collapseObject != null ? c.collapseObject.name : "?"), c.SecondaryFXPrefab);
                say.Append("\n");
            }
            return say.ToString();
        }
        static void Seq(System.Text.StringBuilder say, string what, VFXSequencer q)
        {
            say.Append(what).Append(" [");
            if (q == null || q.FXList == null) { say.Append("none] "); return; }
            foreach (VFXSequencer.SequenceFX fx in q.FXList)
            {
                ParticleSystem ps = fx.particleSystem;
                if (ps == null) { say.Append("(no particles) "); continue; }
                ParticleSystem.MainModule m = ps.main;
                ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
                Color c = m.startColor.color;
                say.Append(ps.name).Append(" at ").Append(fx.startTime.ToString("F1")).Append(" s, colour ").Append(c.r.ToString("F2")).Append(",").Append(c.g.ToString("F2")).Append(",").Append(c.b.ToString("F2"))
                   .Append(", size ").Append(m.startSize.constantMax.ToString("F0")).Append(", lives ").Append(m.startLifetime.constantMax.ToString("F1"))
                   .Append(", ").Append(r != null && r.sharedMaterial != null ? r.sharedMaterial.name + "/" + (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?") : "no material").Append("; ");
            }
            say.Append("] ");
        }
#endif
        /// <summary>
        /// (Since 2026-11-19.) The game's own fire, explosions and smoke over a building of the space centre that comes down, silenced;
        /// its collapse and its sounds are kept, and this mod lays its dust (Dust). Each building goes down under sprites of explosions
        /// a hundred to two hundred metres wide, and of fire, as if it were full of fuel, and a wall of smoke drawn near black: a
        /// section of the runway broken by an empty Kerbal X sliding onto it at twenty-five metres a second went up in a yellow ball
        /// of fire wider than the view and a flash that filled the sky, none of it this mod's (the film of that day, spark_slide3).
        /// A building that is knocked down goes in grey dust at its foot, as the Cape's towers do in NASA's photographs of their
        /// demolition. Each flight; put back as it ends (Loud).
        /// </summary>
        internal static void Quiet()
        {
            int n = 0;
            try
            {
                foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
                {
                    if (b == null) continue;
                    n += Quiet(b.DemolitionFXPrefab);
                    if (b.CollapsibleObjects != null)
                        foreach (DestructibleBuilding.CollapsibleObject c in b.CollapsibleObjects) if (c != null) n += Quiet(c.SecondaryFXPrefab);
                }
            }
            catch (Exception ex) { Addon.Log("could not quiet the buildings' own explosions: " + ex.Message); }
#if DEV
            Addon.Log("dev: the buildings' own explosions and fire quieted, " + n + " particle systems");
#endif
        }

        static readonly List<ParticleSystem> quieted = new List<ParticleSystem>();
        static int Quiet(VFXSequencer q)
        {
            if (q == null || q.FXList == null) return 0;
            int n = 0;
            foreach (VFXSequencer.SequenceFX fx in q.FXList)
            {
                ParticleSystem ps = fx.particleSystem;
                if (ps == null) continue;
                // (all of them: "Explosion_01" to "_08" and "Fire_01" to "_03" the fire, "Particle System - Collapse Cloud" the dust, which
                // is drawn near black: a wall of black smoke stood over the fallen runway end in the film of that day once the fire was gone.
                // This mod's own dust is laid in its place, see Dust.)
                foreach (ParticleSystem one in ps.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.EmissionModule e = one.emission;
                    if (!e.enabled) continue;
                    e.enabled = false; quieted.Add(one); n++;
                }
            }
            return n;
        }

        /// <summary>
        /// (Since 2026-11-19.) A building that comes down throws up its dust: this mod's, laid by its own blasts with nothing burning
        /// (Plan's Cause.Collapse, see Site.Rubble). The building is measured from its own colliders as it stood (each one's box as it
        /// lies, not the scene's square box round it, which for a length of runway lying slantwise in the scene's axes was 367 m
        /// across and 363 m tall: see Measure), along the ground the way its longest part lies and up from the ground; the dust is
        /// laid along its length, a cloud for each stretch of it as long as it is wide (one to four), each as high as it stood. At
        /// most a dozen clouds in five seconds, as when a nuclear explosion brings the whole space centre down.
        /// </summary>
        static void Dust(DestructibleBuilding b)
        {
            try
            {
                if (b == null || Air.Instance == null || !HighLogic.LoadedSceneIsFlight) return;
#if DEV
                if (asItWas) return;
#endif
                if (Time.time > dustFrom + 5f) { dustFrom = Time.time; dustLaid = 0; }
                if (dustLaid >= 12) return;
                CelestialBody body = FlightGlobals.currentMainBody;
                if (body == null) return;
                Vector3 o = b.transform.position;
                if (!Measure(b, body, out Vector3 up, out Vector3 along, out Vector3 side, out float h0, out float s0, out float w0, out float tall, out float length, out float wide)) return;
                // (a cloud for each stretch of it as long as it is wide, and at least sixty metres: up to four; each as far round as
                // its stretch, up to seventy metres. Until a film of the space plane hangar coming down, at most forty: the hangar,
                // 176 m long and 110 wide, had one cloud a third of its size.)
                int clouds = Mathf.Clamp(Mathf.CeilToInt(length / Mathf.Max(wide, 60f)), 1, 4);
                clouds = Mathf.Min(clouds, 12 - dustLaid);
                float reach = Mathf.Clamp(0.5f * Mathf.Max(length / clouds, wide), 5f, 70f);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                for (int n = 0; n < clouds; n++)
                {
                    Vector3d at = (Vector3d)(o + up * (h0 + 1f) + along * (s0 + (n + 0.5f) * length / clouds) + side * (w0 + 0.5f * wide));
                    Records.Fake("rest=1 cause=collapse speed=10 dry=1 size=" + reach.ToString("F1", inv) + " tall=" + tall.ToString("F1", inv), at);
                    FXMonger.Explode(null, at, 0.5);
                    dustLaid++;
                }
#if DEV
                Addon.Log("dev: dust for " + b.name + ": " + length.ToString("F0") + " m long, " + wide.ToString("F0") + " m wide, " + tall.ToString("F0") + " m tall; " + clouds + " cloud(s) " + reach.ToString("F0") + " m round");
#endif
            }
            catch (Exception ex) { Addon.Log("could not lay a fallen building's dust: " + ex.Message); }
        }
        /// <summary>
        /// (Since 2026-11-19.) A building measured as it stands, from its parts' boxes as they lie (Boxes): which way is up from
        /// the ground there, the way its longest part lies along the ground and the way across it; and from its own place (its
        /// transform's), where it begins up from the ground (h0: the ground, not the foot of its foundations), along it and
        /// across it, and how tall, long and wide it is. False if it has nothing to measure.
        /// </summary>
        static bool Measure(DestructibleBuilding b, CelestialBody body, out Vector3 up, out Vector3 along, out Vector3 side, out float h0, out float s0, out float w0, out float tall, out float length, out float wide)
        {
            Vector3 o = b.transform.position;
            up = ((Vector3)FlightGlobals.getUpAxis(body, o)).normalized;
            Boxes(b, measured);
            along = Vector3.zero; side = Vector3.zero; h0 = s0 = w0 = tall = length = wide = 0f;
            if (measured.Count == 0) return false;
            float longest = 0f;
            foreach (Box x in measured)
                for (int k = 0; k < 3; k++)
                {
                    Vector3 flat = Vector3.ProjectOnPlane(k == 0 ? x.ax * x.ex : k == 1 ? x.ay * x.ey : x.az * x.ez, up);
                    if (flat.magnitude > longest) { longest = flat.magnitude; along = flat / longest; }
                }
            if (longest <= 0f) along = Vector3.ProjectOnPlane(b.transform.forward, up).normalized;
            side = Vector3.Cross(up, along);
            float h1 = float.MinValue, s1 = float.MinValue, w1 = float.MinValue;
            h0 = s0 = w0 = float.MaxValue;
            foreach (Box x in measured)
                for (int k = 0; k < 8; k++)
                {
                    Vector3 r = x.Corner(k) - o;
                    float h = Vector3.Dot(r, up), s = Vector3.Dot(r, along), w = Vector3.Dot(r, side);
                    h0 = Mathf.Min(h0, h); h1 = Mathf.Max(h1, h); s0 = Mathf.Min(s0, s); s1 = Mathf.Max(s1, s); w0 = Mathf.Min(w0, w); w1 = Mathf.Max(w1, w);
                }
            // (from the ground, not from the foot of its foundations)
            Vector3 middle = o + along * (0.5f * (s0 + s1)) + side * (0.5f * (w0 + w1));
            float ground = (float)(body.TerrainAltitude(body.GetLatitude(middle), body.GetLongitude(middle)) - body.GetAltitude(o));
            if (ground > h0 && ground < h1) h0 = ground;
            tall = Mathf.Max(1f, h1 - h0); length = s1 - s0; wide = w1 - w0;
            return true;
        }
        static readonly List<Box> measured = new List<Box>();
#if DEV
        /// <summary>(Development build, 2026-11-19: every building of the space centre as Measure has it, and where it stands from the ship flown, metres east and north.)</summary>
        public static string Sizes(string _)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            Vessel v = FlightGlobals.ActiveVessel;
            if (body == null || v == null) return "no flight";
            Vector3 vup = ((Vector3)FlightGlobals.getUpAxis(body, v.transform.position)).normalized;
            Vector3 east = Vector3.Cross(vup, body.transform.up).normalized, north = Vector3.Cross(east, vup);
            var lines = new List<string>();
            foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
            {
                if (b == null) continue;
                if (!Measure(b, body, out Vector3 up, out Vector3 along, out Vector3 side, out float h0, out float s0, out float w0, out float tall, out float length, out float wide)) { lines.Add(b.name + ": nothing to measure"); continue; }
                Vector3 mid = b.transform.position + along * (s0 + 0.5f * length) + side * (w0 + 0.5f * wide) - v.transform.position;
                lines.Add(b.name + (b.IsDestroyed ? " (in ruins)" : "") + ": " + length.ToString("F0") + " x " + wide.ToString("F0") + " x " + tall.ToString("F0") + " m tall, " + measured.Count + " parts; at " + Vector3.Dot(mid, east).ToString("F0") + " m east, " + Vector3.Dot(mid, north).ToString("F0") + " m north");
            }
            return string.Join(" | ", lines.ToArray());
        }
#endif
        static float dustFrom = -100f;
        static int dustLaid;

        /// <summary>(Since 2026-11-19.) The buildings' own explosions and fire given back, as the flight ends: see Quiet.</summary>
        internal static void Loud()
        {
            foreach (ParticleSystem one in quieted)
                if (one != null) { ParticleSystem.EmissionModule e = one.emission; e.enabled = true; }
            quieted.Clear();
        }

        static Hooks hooks;
        /// <summary>A flight begun: the game's word of each building that comes down is listened for.</summary>
        internal static void Listen()
        {
            if (hooks != null) return;
            hooks = new Hooks();
            GameEvents.OnKSCStructureCollapsing.Add(hooks.Fell);
        }
        static readonly List<Falls> due = new List<Falls>();
        static DestructibleBuilding[] all;
        static Box[][] room;            // each building's parts, as they stood when found (see foundAt)
#if DEV
        static readonly List<string> said = new List<string>();
        /// <summary>(Development build: what the last blasts did to the buildings, and forgotten.)</summary>
        public static string Said(string _) { string s = string.Join("\n", said.ToArray()); said.Clear(); return s.Length > 0 ? s : "no blast has looked at the buildings"; }
        /// <summary>(Development build: how many of the space centre's buildings stand, and which lie in ruins.)</summary>
        public static string Standing(string _)
        {
            int n = 0, down = 0; string which = "";
            foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
            { if (b == null) continue; n++; if (b.IsDestroyed) { down++; if (which.Length < 900) which += " " + b.name; } }
            return (n - down) + " of " + n + " buildings stand" + (down > 0 ? "; in ruins:" + which : "") + (due.Count > 0 ? "; " + due.Count + " still to fall" : "");
        }
        /// <summary>(Development build, 2026-11-19: "loud", the buildings' own explosions, fire and smoke given back and none of this mod's dust, the game as it was, for a film beside the quiet one; else quieted again.)</summary>
        public static string Sound(string how)
        {
            if ((how ?? "").Trim() == "loud") { Loud(); asItWas = true; return "the buildings' own explosions given back, and no dust of this mod's"; }
            asItWas = false; Loud(); Quiet(); return "the buildings' own explosions quieted";
        }
        static bool asItWas;            // (development build: "loud", the game's own collapse as it was, with none of this mod's dust)
        /// <summary>(Development build, 2026-11-19: the building of that name brought down by the game's own demolition, for filming a collapse.)</summary>
        public static string Bring(string name)
        {
            // (NAME/EAST/NORTH: of the buildings of that name, the one nearest so many metres east and north of the ship flown, as
            // Sizes gives them: three are called mainBuilding)
            string[] w = (name ?? "").Trim().Split('/');
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = FlightGlobals.currentMainBody;
            Vector3 near = Vector3.zero;
            if (w.Length == 3 && v != null && body != null)
            {
                Vector3 vup = ((Vector3)FlightGlobals.getUpAxis(body, v.transform.position)).normalized;
                Vector3 east = Vector3.Cross(vup, body.transform.up).normalized, north = Vector3.Cross(east, vup);
                near = v.transform.position + east * float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) + north * float.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture);
            }
            DestructibleBuilding best = null;
            float bestFar = float.MaxValue;
            foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
            {
                if (b == null || b.IsDestroyed || !string.Equals(b.name, w[0], StringComparison.OrdinalIgnoreCase)) continue;
                float far = w.Length == 3 ? (b.transform.position - near).magnitude : 0f;
                if (far < bestFar) { best = b; bestFar = far; }
            }
            if (best == null) return "no standing building called " + name;
            best.Demolish();
            return best.name + " brought down" + (w.Length == 3 ? ", " + bestFar.ToString("F0") + " m from the place named" : "");
        }
        /// <summary>(Development build: every building that lies in ruins put up again, the game's own repair without its cost: after a test.)</summary>
        public static string Repair(string _)
        {
            int n = 0;
            foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
                if (b != null && b.IsDestroyed) { b.Repair(); n++; }
            due.Clear();
            return n + " buildings put up again";
        }
#endif

        /// <summary>How far out a charge wrecks buildings, metres: where Kinney and Graham's overpressure falls to Wrecks, in air so much thinner than at sea level.</summary>
        internal static float Reach(float tnt, float thin)
        {
            if (!(tnt > 0f) || !(thin > 0.001f)) return 0f;
            // (the overpressure against the air's own there: thinner air is squeezed as hard by less)
            float lo = 0.05f, hi = 60f;
            for (int n = 0; n < 24; n++)
            {
                float z = 0.5f * (lo + hi);
                if (Site.OverAtOpen(z) > Wrecks) lo = z; else hi = z;
            }
            return lo * Mathf.Pow(tnt, 1f / 3f) / Mathf.Pow(thin, 1f / 3f);
        }

        /// <summary>(Since 2026-11-19.) A box as it lies in the scene: its middle, its three axes (each of unit length) and how far it reaches along each.</summary>
        struct Box
        {
            public Vector3 mid, ax, ay, az;
            public float ex, ey, ez;
            internal static Box Of(Bounds b) => new Box { mid = b.center, ax = Vector3.right, ay = Vector3.up, az = Vector3.forward, ex = b.extents.x, ey = b.extents.y, ez = b.extents.z };
            internal float SqrDistance(Vector3 p)
            {
                Vector3 r = p - mid;
                float dx = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(r, ax)) - ex), dy = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(r, ay)) - ey), dz = Mathf.Max(0f, Mathf.Abs(Vector3.Dot(r, az)) - ez);
                return dx * dx + dy * dy + dz * dz;
            }
            internal Vector3 Corner(int k) => mid + ax * ((k & 1) != 0 ? ex : -ex) + ay * ((k & 2) != 0 ? ey : -ey) + az * ((k & 4) != 0 ? ez : -ez);
        }

        /// <summary>
        /// (Since 2026-11-19.) The boxes of a building's parts as they lie: each collider's own (a box's, a mesh's, a ball's, a
        /// capsule's, turned and scaled as its object is), or the scene's square box round each of its pictures where it has no
        /// colliders.
        /// </summary>
        static void Boxes(DestructibleBuilding b, List<Box> into)
        {
            into.Clear();
            foreach (Collider c in b.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                Bounds local;
                if (c is BoxCollider box) local = new Bounds(box.center, box.size);
                else if (c is MeshCollider mesh && mesh.sharedMesh != null) local = mesh.sharedMesh.bounds;
                else if (c is SphereCollider ball) local = new Bounds(ball.center, Vector3.one * (2f * ball.radius));
                else if (c is CapsuleCollider cap)
                {
                    Vector3 size = Vector3.one * (2f * cap.radius);
                    size[Mathf.Clamp(cap.direction, 0, 2)] = Mathf.Max(cap.height, 2f * cap.radius);
                    local = new Bounds(cap.center, size);
                }
                else { into.Add(Box.Of(c.bounds)); continue; }
                Transform t = c.transform;
                Vector3 ax = t.TransformVector(new Vector3(local.extents.x, 0f, 0f)), ay = t.TransformVector(new Vector3(0f, local.extents.y, 0f)), az = t.TransformVector(new Vector3(0f, 0f, local.extents.z));
                float ex = ax.magnitude, ey = ay.magnitude, ez = az.magnitude;
                into.Add(new Box
                {
                    mid = t.TransformPoint(local.center),
                    ax = ex > 1e-5f ? ax / ex : t.right, ay = ey > 1e-5f ? ay / ey : t.up, az = ez > 1e-5f ? az / ez : t.forward,
                    ex = ex, ey = ey, ez = ez
                });
            }
            if (into.Count == 0) foreach (Renderer r in b.GetComponentsInChildren<Renderer>()) if (r != null) into.Add(Box.Of(r.bounds));
        }

        static void Find()
        {
            if (all != null && all.Length > 0 && all[0] != null) return;
            all = UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>();
            room = new Box[all.Length][];
            foundAt = new Vector3[all.Length];
            var parts = new List<Box>();
            for (int n = 0; n < all.Length; n++)
            {
                // (the room a building takes, part by part: each of its colliders' box as it lies, or its pictures' where it has
                // none; a point where it has neither. One box round all of a building's parts took in the open ground between
                // them; and until 2026-11-19 each part's was the scene's square box round it, which for a length of runway
                // lying slantwise in the scene's axes was 367 m across and 363 m tall: a blast anywhere in that brought it down.)
                Boxes(all[n], parts);
                if (parts.Count == 0) parts.Add(Box.Of(new Bounds(all[n].transform.position, Vector3.zero)));
                room[n] = parts.ToArray();
                foundAt[n] = all[n].transform.position;
            }
        }

        /// <summary>
        /// A blast, where it went off (the scene's own place, this frame), worth so many kilograms of TNT in the open air (a
        /// charge on the ground is told as its 1.8 times already), in air of such a pressure (pascals) and such a speed of
        /// sound: the buildings it will bring down are noted, each to fall as its front arrives.
        /// </summary>
        internal static void Blast(Vector3 at, float tnt, float pressure, float sound)
        {
            try
            {
                if (!Settings.Raze || !HighLogic.LoadedSceneIsFlight || !(tnt > 0.5f) || pressure < 1000f) return;
                Game game = HighLogic.CurrentGame;
                if (game != null && game.Parameters != null && game.Parameters.Difficulty.IndestructibleFacilities) return;
                if (OfACollapse(at)) return;
                float reach = Reach(tnt, pressure / 101325f);
                // (nothing of the space centre within a blast's reach of a craft far from it: the buildings are looked for only then)
                if (SpaceCenter.Instance == null || (SpaceCenter.Instance.transform.position - at).magnitude > reach + 12000f) return;
                Find();
                if (all == null) return;
                int down = 0;
                for (int n = 0; n < all.Length; n++)
                {
                    DestructibleBuilding b = all[n];
                    if (b == null || b.IsDestroyed) continue;
                    // (the game's bounds are the scene's as they were when found: the floating origin has moved since, so the
                    // building's own place now and its room's size then)
                    // (the blast's place among the building's parts as they stood when found)
                    Vector3 then = at - (b.transform.position - foundAt[n]);
                    float d = float.MaxValue;
                    for (int k = 0; k < room[n].Length; k++) d = Mathf.Min(d, room[n][k].SqrDistance(then));      // (Box's, as it lies)
                    d = Mathf.Sqrt(d);
                    if (d > reach) continue;
                    bool waits = false;
                    for (int k = 0; k < due.Count; k++) if (due[k].building == b) { waits = true; break; }
                    if (waits) continue;
                    // (the front outruns sound near a strong blast: a fifth faster on the whole way to where it wrecks)
                    due.Add(new Falls { building = b, at = Time.time + d / (1.2f * Mathf.Clamp(sound, 80f, 900f)) });
                    down++;
#if DEV
                    if (said.Count < 60) said.Add("  " + b.name + ", " + d.ToString("F0") + " m off: falls in " + (d / (1.2f * Mathf.Clamp(sound, 80f, 900f))).ToString("F2") + " s");
#endif
                }
#if DEV
                if (said.Count < 60 && (down > 0 || tnt > 500f)) said.Add("a blast of " + tnt.ToString("F0") + " kg of TNT wrecks buildings out to " + reach.ToString("F0") + " m: " + down + " of " + all.Length + " to fall");
#endif
            }
            catch (Exception ex)
            {
                if (!complained) Debug.LogError("[VolumetricExplosions] a blast could not look at the buildings: " + ex);
                complained = true;
            }
        }
        static bool complained;
        static Vector3[] foundAt;

        /// <summary>Every frame of a flight: the buildings whose blast has reached them fall.</summary>
        internal static void Step()
        {
            if (due.Count == 0) return;
            for (int n = due.Count - 1; n >= 0; n--)
            {
                if (Time.time < due[n].at) continue;
                DestructibleBuilding b = due[n].building;
                due.RemoveAt(n);
                try { if (b != null && !b.IsDestroyed) b.Demolish(); }
                catch (Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] a building could not be brought down: " + ex);
                    complained = true;
                }
            }
        }

        /// <summary>A scene gone: nothing waits to fall.</summary>
        internal static void Forget()
        {
            due.Clear(); all = null; room = null; foundAt = null; fell.Clear();
            if (hooks != null) { GameEvents.OnKSCStructureCollapsing.Remove(hooks.Fell); hooks = null; }
        }
    }
}
