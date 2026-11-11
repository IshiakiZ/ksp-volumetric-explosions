using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// BD Armory's splashes into the sea, drawn by Natural Ocean as big as what made each (since 2026-11-09; the owner: "compatible with
    /// everything in the mod"). BD Armory asks for the game's own splash (FXMonger.Splash) where its bullets and rockets go into the
    /// water and above its explosions under it, saying how big as a calibre or a charge; the game takes that size as a share of its
    /// biggest and holds it to the biggest, so every round from 2 mm up threw the same white plume. Here the game's splash effects are
    /// swapped for copies of themselves that find, as each comes, what made it (as Volumetric Explosions swaps the game's explosions):
    ///
    ///  * a bullet or a rocket of BD Armory's at that spot (in its pools, gone into the water there this frame): the game's splash
    ///    hidden, and Natural Ocean's drawn for the blow of the round, its mass at its speed. A gun's round throws up a slender spout
    ///    of spray, leaning back the way it came, as high as the jet such a blow's crater would send up (Spray.Spout; an older Natural
    ///    Ocean, a crown, Spray.Splash); a shell or rocket of a megajoule and a half or more, the whole blow, its crater, crown and
    ///    jet, the sea dented and the sound of it (Spray.Struck, as a craft striking the sea has it).
    ///  * one of its explosions under the water there, set going within half a second (and the explosions mod drawing BD Armory's
    ///    explosions): the game's hidden, as that mod throws up its own water.
    ///  * anything else (a part of a craft coming down into the sea, or a splash out where Natural Ocean draws no sea): the game's own,
    ///    as it was.
    ///
    /// The game gathers splashes near each other into one, put off to the end of the frame (its splashMonger): by then a bullet that
    /// made one has gone back to its pool, so while the splashes are swapped each is made at once, as asked. Put back as they were when
    /// the switch is BD Armory's own, the kind is off, or the scene goes.
    /// </summary>
    static class Splashes
    {
        static FXMonger monger;
        static GameObject[] stock, ours;
        static bool mongered, taken;
        static Transform shelf;

        // Natural Ocean, found by name: its spout and crown of spray, its whole blow, whether its sea is drawn, how high its water stands at a place
        static bool looked;
        static MethodInfo splash, struck, waterAt, spout, mark, spotOf;
        static PropertyInfo drawn;

        // (what each of BD Armory's objects is, found once each: its pools keep the same objects)
        static readonly Dictionary<int, Component> parts = new Dictionary<int, Component>();

        /// <summary>(For Tests.Said: splashes drawn by Natural Ocean for BD Armory's rounds, left to the explosions mod, and the game's own.)</summary>
        internal static int Ours, Left, Stock;
        internal static string Last = "", Why = "";

        /// <summary>The blow (J) from which a round's splash is the whole of one (Natural Ocean's Struck): a crater of 2.5 m.</summary>
        const float Whole = 1.5e6f;

        static void LookForSea()
        {
            if (looked) return;
            looked = true;
            const BindingFlags statics = BindingFlags.Public | BindingFlags.Static;
            splash = struck = waterAt = spout = mark = spotOf = null;
            drawn = null;
            // (the last loaded: a development build's copy of Natural Ocean, swapped in as the game runs, is the one running; a player's game
            // has the one)
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type spray = a.GetType("NaturalOcean.Spray", false), sea = a.GetType("NaturalOcean.Sea", false);
                if (spray == null || sea == null) continue;
                splash = spray.GetMethod("Splash", statics, null, new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) }, null);
                struck = spray.GetMethod("Struck", statics, null, new[] { typeof(CelestialBody), typeof(Vector3d), typeof(float), typeof(float), typeof(Vector3), typeof(float) }, null);
                // (a spout, for a small round: since Natural Ocean of 2026-11-09; an older one throws its crown, Splash, instead)
                spout = spray.GetMethod("Spout", statics, null, new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) }, null);
                drawn = sea.GetProperty("Drawn", statics);
                waterAt = sea.GetMethod("WaterAt", statics, null, new[] { typeof(Vector3d), typeof(Vector3).MakeByRefType() }, null);
                // (and for torpedoes' wakes: white water laid at a place on the still sea, and where a place in the scene lies on it)
                spotOf = sea.GetMethod("SpotOf", statics, null, new[] { typeof(Vector3d), typeof(Vector2d).MakeByRefType() }, null);
                Type foam = a.GetType("NaturalOcean.Foam", false);
                mark = foam != null ? foam.GetMethod("Mark", statics, null, new[] { typeof(Vector2d), typeof(float), typeof(float) }, null) : null;
            }
            Why = splash == null ? "no Natural Ocean (or not one this knows): the game's own splashes" : struck == null || drawn == null ? "a Natural Ocean this does not know all of: the game's own splashes" : "";
        }

        /// <summary>Once a frame (Volumes.Asked): the game's splashes swapped for ours while they are wanted, and put back when not.</summary>
        internal static void Keep(bool want)
        {
            if (want && !taken) Take();
            else if (!want && taken) PutBack();
        }

        static void Take()
        {
            LookForSea();
            if (splash == null || struck == null || drawn == null) return;
            if (monger == null) monger = UnityEngine.Object.FindObjectOfType<FXMonger>();
            if (monger == null || monger.splashes == null || monger.splashes.Length == 0) return;
            stock = monger.splashes;
            if (ours == null || ours.Length != stock.Length || Array.IndexOf(ours, null) >= 0)
            {
                if (shelf == null)
                {
                    var holder = new GameObject("VolumetricArmory splashes");
                    holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(holder);
                    shelf = holder.transform;
                }
                ours = new GameObject[stock.Length];
                for (int n = 0; n < stock.Length; n++)
                {
                    if (stock[n] == null) { ours = null; return; }
                    // (a copy of the game's own, which it is again wherever the splash is not BD Armory's: its look, sound and life as they were)
                    GameObject copy = UnityEngine.Object.Instantiate(stock[n], shelf, false);
                    copy.name = stock[n].name + " (Volumetric Armory)";
                    copy.AddComponent<SplashStandIn>();
                    ours[n] = copy;
                }
            }
            monger.splashes = (GameObject[])ours.Clone();
            mongered = monger.splashMonger;
            monger.splashMonger = false;
            taken = true;
        }

        /// <summary>The game's splashes as they were.</summary>
        internal static void PutBack()
        {
            looked = false;                 // (looked for again when next wanted: see LookForSea)
            if (!taken) return;
            taken = false;
            if (monger == null) return;
            if (stock != null) monger.splashes = stock;
            monger.splashMonger = mongered;
        }

        internal static bool Taken => taken;

        /// <summary>
        /// A splash of the game's made at a place in the scene (on the sea's level), stood in for: what made it. 0, the game's own (left to
        /// show); 1, a round of BD Armory's, drawn here by Natural Ocean (loud: the whole blow, with its own sound); 2, one of BD Armory's
        /// explosions under the water, which the explosions mod splashes.
        /// </summary>
        internal static int Made(Vector3 at, out bool loud)
        {
            loud = false;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (!taken || !Bda.Here || body == null) { Stock++; return 0; }
            if (!(drawn.GetValue(null, null) is bool sea) || !sea) { Stock++; Last = "the game's own: no sea of Natural Ocean's drawn"; return 0; }
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(body, (Vector3d)at);
            float now = Time.time;
            if (Armory.Wanted(Armory.Explosions) && UnderExplosion(at, up, now)) { Left++; return 2; }
            if (!Round(at, up, out float mass, out float caliber, out Vector3 going))
            {
                Stock++;
                Last = "the game's own: no round of BD Armory's there" + (nearest < float.MaxValue ? " (the nearest " + Mathf.Sqrt(nearest).ToString("F1") + " m off, of " + seenRounds + ")" : " (none looked at)");
                return 0;
            }
            float g = (float)(body.GeeASL * 9.80665), speed = going.magnitude;
            float blow = 0.5f * mass * speed * speed;
            // (the crater a blow of that energy digs, as Natural Ocean has it for a craft: its radius to the quarter power of the energy)
            float crater = Mathf.Pow(blow / (4000f * g), 0.25f);
            if (blow >= Whole && speed >= 5f)
            {
                // (the whole blow's first spray, its plume, Natural Ocean throws at a share of the speed the thing struck at: a craft's
                // hundreds of metres a second. A shell's thousand and more threw it some hundred metres high for a 120 mm one, where a
                // shell's splash stands some tens of metres (photographs of naval gunnery's water columns): told no more than forty times
                // its crater's radius a second, with the mass that keeps the blow's energy, which sizes its crater)
                float told = Mathf.Min(speed, 40f * crater);
                struck.Invoke(null, new object[] { body, (Vector3d)at, 2f * blow / (told * told) / 1000f, told, going * (told / speed), 4f * caliber / 1000f });
                loud = true;
                Last = "whole blow: " + caliber.ToString("F0") + " mm, " + mass.ToString("F2") + " kg at " + speed.ToString("F0") + " m/s, " + (blow / 1e6f).ToString("F1") + " MJ, crater " + crater.ToString("F1") + " m";
            }
            else
            {
                // (on the water as it stands there, its waves and all)
                Vector3 on = at;
                if (waterAt != null)
                {
                    object[] args = { (Vector3d)at, null };
                    if (waterAt.Invoke(null, args) is float level) on = at + up * (level - (float)FlightGlobals.getAltitudeAtPos((Vector3d)at, body));
                }
                if (spout != null)
                {
                    // (a spout as high as the jet such a crater would send up, two and a half times its radius, as Natural Ocean's whole blow
                    // has it, so that the one goes over into the other at the whole blow's size: 2.7 m for a 20 mm round, 3.9 m for a
                    // 30 mm, 6.4 m at a megajoule and a half)
                    float high = Mathf.Clamp(2.6f * crater, 0.8f, 7f), foot = Mathf.Clamp(0.35f * crater, 0.15f, 1f);
                    spout.Invoke(null, new object[] { on, up, going, high, foot });
                    Last = "spout: " + caliber.ToString("F0") + " mm, " + mass.ToString("F3") + " kg at " + speed.ToString("F0") + " m/s, " + (blow / 1e3f).ToString("F0") + " kJ, " + high.ToString("F1") + " m high";
                }
                else
                {
                    // (an older Natural Ocean: a crown thrown as high as such a crater's, as it throws one from a craft's; its spray rises at
                    // 0.45 of the speed it is told)
                    float high = Mathf.Clamp(1.5f * crater, 0.6f, 6f);
                    float rise = Mathf.Sqrt(2f * g * high);
                    splash.Invoke(null, new object[] { on, up, rise / 0.45f, Mathf.Clamp(crater, 0.3f, 2.5f) });
                    Last = "crown: " + caliber.ToString("F0") + " mm, " + mass.ToString("F3") + " kg at " + speed.ToString("F0") + " m/s, " + (blow / 1e3f).ToString("F0") + " kJ, " + high.ToString("F1") + " m high";
                }
            }
            Ours++;
            return 1;
        }

        static Component Of(GameObject go, Type type)
        {
            int key = go.GetInstanceID();
            if (parts.TryGetValue(key, out Component c) && c != null) return c;
            c = go.GetComponent(type);
            if (parts.Count > 20000) parts.Clear();
            parts[key] = c;
            return c;
        }

        /// <summary>One of BD Armory's explosions set going within half a second, under the water straight below the splash.</summary>
        static bool UnderExplosion(Vector3 at, Vector3 up, float now)
        {
            if (Bda.Explosion == null || Bda.ExplosionPools == null || Bda.ExplosionAt == null || Bda.ExplosionStart == null) return false;
            if (!(Bda.ExplosionPools() is IDictionary pools)) return false;
            foreach (object pool in pools.Values)
            {
                List<GameObject> list = pool != null ? Bda.PoolList(pool) : null;
                if (list == null) continue;
                for (int n = 0; n < list.Count; n++)
                {
                    GameObject go = list[n];
                    if (go == null || !go.activeInHierarchy) continue;
                    Component x = Of(go, Bda.Explosion);
                    if (x == null || now - Bda.ExplosionStart(x) > 0.5f) continue;
                    Vector3 d = Bda.ExplosionAt(x) - at;
                    if ((d - up * Vector3.Dot(d, up)).sqrMagnitude < 15f * 15f) return true;
                }
            }
            return false;
        }

        /// <summary>The bullet or rocket of BD Armory's that went into the water at the spot: its mass (kg), calibre (mm) and motion.</summary>
        static bool Round(Vector3 at, Vector3 up, out float mass, out float caliber, out Vector3 going)
        {
            mass = 0f; caliber = 0f; going = Vector3.zero;
            nearest = float.MaxValue; seenRounds = 0;
            float best = float.MaxValue;
            bool found = false, live = false;
            // (bullets: a round goes on into the water, or ricochets, or is done with and put back in its pool; by the time this is asked
            // the game may have stepped it on some steps more)
            if (Bda.Bullet != null && Bda.BulletPool != null && Bda.BulletAt != null && Bda.BulletGoing != null && Bda.BulletMass != null)
            {
                List<GameObject> list = Bda.BulletPool() is object pool ? Bda.PoolList(pool) : null;
                if (list != null)
                    for (int n = 0; n < list.Count; n++)
                        Consider(list[n], Bda.Bullet, Bda.BulletAt, Bda.BulletGoing, Bda.BulletMass, Bda.BulletCaliber, 1f, at, up, ref best, ref found, ref live, ref mass, ref caliber, ref going);
            }
            if (Bda.Rocket != null && Bda.RocketPools != null && Bda.RocketAt != null && Bda.RocketGoing != null && Bda.RocketMass != null && Bda.RocketPools() is IDictionary pools)
                foreach (object pool in pools.Values)
                {
                    List<GameObject> list = pool != null ? Bda.PoolList(pool) : null;
                    if (list == null) continue;
                    // (a rocket's mass is in tonnes)
                    for (int n = 0; n < list.Count; n++)
                        Consider(list[n], Bda.Rocket, Bda.RocketAt, Bda.RocketGoing, Bda.RocketMass, Bda.RocketCaliber, 1000f, at, up, ref best, ref found, ref live, ref mass, ref caliber, ref going);
                }
            return found && mass > 0f;
        }

        static void Consider(GameObject go, Type type, Func<object, Vector3> where, Func<object, Vector3> moving, Func<object, float> weight, Func<object, float> bore, float toKg,
                             Vector3 at, Vector3 up, ref float best, ref bool found, ref bool live, ref float mass, ref float caliber, ref Vector3 going)
        {
            if (go == null) return;
            bool active = go.activeInHierarchy;
            if (live && !active) return;
            Component c = Of(go, type);
            if (c == null) return;
            Vector3 v = moving(c);
            // (one still flying may be some steps on from where it went in; one put back in its pool lies where it was done with)
            float reach = active ? 4f + 0.06f * v.magnitude : 4f;
            Vector3 d = where(c) - at;
            float upward = Vector3.Dot(d, up);
            float flat = (d - up * upward).sqrMagnitude;
            seenRounds++;
            if (d.sqrMagnitude < nearest) nearest = d.sqrMagnitude;
            if (flat > reach * reach || Mathf.Abs(upward) > reach) return;
            float far = flat + upward * upward;
            // (one still flying before any put back; the nearest of those)
            if (active && !live) { live = true; best = float.MaxValue; }
            if (far >= best) return;
            best = far;
            found = true;
            mass = weight(c) * toKg;
            caliber = bore != null ? bore(c) : 0f;
            going = v;
        }

        // (for what is said of a splash left the game's: the nearest of BD Armory's rounds to it, squared, and how many were looked at)
        static float nearest;
        static int seenRounds;

        internal static void Failed(Exception ex)
        {
            if (failedOnce) return;
            failedOnce = true;
            Armory.Log("a splash failed: " + ex);
        }
        static bool failedOnce;

        // ---------------------------------------------------------------- torpedoes' wakes

        // A torpedo running under the sea leaves a track on it: what it churns and the gas or air it trails come up behind it as
        // bubbles, and where they break the surface the water is white, in a narrow line that widens and fades (the "torpedo track"
        // of the second world war, which a lookout saw well behind the torpedo that made it: the bubbles take some seconds to come up,
        // and it runs on meanwhile). BD Armory draws its torpedoes' wakes as white specks under the water behind them; here, while
        // the sea is Natural Ocean's, the track is laid on the water itself, into its white water's maps (Foam.Mark), at the place over
        // where the torpedo was once its bubbles are up: a metre's rise a second (from the specks of a few millimetres that rise at a
        // quarter of that to the large ones of a churned trail that rise at a metre and more), so from BD Armory's three metres of
        // running depth the track shows three seconds and a hundred metres behind. The deeper it runs, the later, the wider (the
        // bubbles spread as they rise) and the fainter, and nothing from below a dozen metres. Only while it is driving itself under
        // the water, as BD Armory's own wake is. Each mark some four metres across from three metres down: the first tried, a metre
        // and a half, was too narrow for the sea's coarser maps of white water, which a few hundred metres off left only scattered
        // flecks of it (2026-11-09).

        struct Rising { public Vector3d From; public CelestialBody Body; public float Due, Wide, White; }
        static readonly List<Rising> rising = new List<Rising>();

        /// <summary>(For Tests.Said: the frames a torpedo was seen running under the sea, the marks of white water laid for them, and how deep it ran, m.)</summary>
        internal static int Running, Laid;
        internal static float Shallowest = float.MaxValue, Deepest;
        /// <summary>(For Tests.Said: where the last one seen running was, in the scene.)</summary>
        internal static Vector3 LastAt;
        /// <summary>(For Tests.Where: the first and the last mark laid, from the middle of the body they were laid on.)</summary>
        internal static Vector3d FirstLaid, LastLaid;
        internal static CelestialBody LaidOn;

        /// <summary>(Natural Ocean's sea drawn here, on a body with a sea: the body, or null.)</summary>
        static CelestialBody SeaHere()
        {
            LookForSea();
            if (mark == null || spotOf == null || drawn == null) return null;
            if (!(drawn.GetValue(null, null) is bool sea) || !sea) return null;
            CelestialBody body = FlightGlobals.currentMainBody;
            return body != null && body.ocean ? body : null;
        }

        /// <summary>A torpedo of BD Armory's driving itself under the sea this frame, at a place in the scene (its middle).</summary>
        internal static void Wake(Vector3 at, float now)
        {
            CelestialBody body = SeaHere();
            if (body == null) return;
            float deep = -(float)FlightGlobals.getAltitudeAtPos((Vector3d)at, body);
            if (deep <= 0f) return;
            Running++;
            LastAt = at;
            Shallowest = Mathf.Min(Shallowest, deep);
            Deepest = Mathf.Max(Deepest, deep);
            float white = 0.6f * Mathf.Clamp01(1.15f - deep / 12f);
            if (white < 0.03f || rising.Count >= 800) return;
            rising.Add(new Rising { From = (Vector3d)at - body.position, Body = body, Due = now + Mathf.Clamp(deep, 0.3f, 12f), Wide = 1.2f + 0.3f * deep, White = white });
        }

        // ---------------------------------------------------------------- bubble screens and sonar decoys

        // A bubble screen (BD Armory's CMBubble) is air let out under the water to hide a craft from sonar: its bubbles rise and
        // break on the surface over it, a patch of white water that spreads as they do, for as long as it lets them out (ten
        // seconds). BD Armory draws it as a grey puff of smoke under the water, which sinks with it. Here, while the sea is Natural
        // Ocean's, white water laid over it as a torpedo's track is (Rise), some fourteen marks a second, each a third white
        // (twenty, half white, made a solid sheet of it), each at a place scattered over a patch that grows from four metres across
        // to twenty-four (BD Armory's own screen hides what is within 24 m of it), rising from where the screen is at a metre and a
        // half a second (its bubbles come up as a plume, faster than a torpedo's few), so later and wider and fainter the deeper it
        // has sunk. A sonar decoy (CMDecoy) makes its noise under the water for half a minute, going on as it was thrown and
        // sinking: a thin line of bubbles on the water over it while it is near the surface, fainter than a torpedo's, nothing from
        // below eight metres; BD Armory's own specks of foam about it left as they are, as a torpedo's are.

        /// <summary>(For Tests.Said: the frames a bubble screen was seen under the sea, and a decoy.)</summary>
        internal static int Boiling, Trailing;

        /// <summary>
        /// A bubble screen of BD Armory's this frame, at a place in the scene, let go so long ago (s): 1 under the sea and its white water
        /// laid, 0 not yet in the water, less than nothing where there is no sea of Natural Ocean's (BD Armory's own is left).
        /// </summary>
        internal static int Boil(Vector3 at, float age, float now)
        {
            CelestialBody body = SeaHere();
            if (body == null) return -1;
            float deep = -(float)FlightGlobals.getAltitudeAtPos((Vector3d)at, body);
            if (deep <= 0f) return 0;
            Boiling++;
            if (UnityEngine.Random.value > 14f * Time.deltaTime || rising.Count >= 800) return 1;
            float white = 0.35f * Mathf.Clamp01(1.25f - deep / 60f);
            if (white < 0.03f) return 1;
            float reach = Mathf.Min(2f + 2.5f * age, 12f);
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(body, at);
            Vector3 east = Vector3.Cross(up, (Vector3)body.transform.up).normalized, north = Vector3.Cross(east, up);
            Vector2 o = UnityEngine.Random.insideUnitCircle * reach;
            Vector3 there = at + east * o.x + north * o.y;
            rising.Add(new Rising { From = (Vector3d)there - body.position, Body = body, Due = now + Mathf.Clamp(deep / 1.5f, 0.3f, 12f), Wide = 2f + 0.08f * deep, White = white });
            return 1;
        }

        /// <summary>A sonar decoy of BD Armory's this frame, at a place in the scene: true if it is under the sea and its line of bubbles laid.</summary>
        internal static bool Trail(Vector3 at, float now)
        {
            CelestialBody body = SeaHere();
            if (body == null) return false;
            float deep = -(float)FlightGlobals.getAltitudeAtPos((Vector3d)at, body);
            if (deep <= 0f) return false;
            Trailing++;
            float white = 0.3f * Mathf.Clamp01(1.15f - deep / 8f);
            if (white < 0.03f || UnityEngine.Random.value > 15f * Time.deltaTime || rising.Count >= 800) return true;
            rising.Add(new Rising { From = (Vector3d)at - body.position, Body = body, Due = now + Mathf.Clamp(deep, 0.3f, 12f), Wide = 0.9f + 0.3f * deep, White = white });
            return true;
        }

        /// <summary>Once a frame (Volumes.Asked): the wakes' bubbles that are up, laid on the water as white.</summary>
        internal static void Rise(float now)
        {
            if (rising.Count == 0) return;
            CelestialBody body = FlightGlobals.currentMainBody;
            // (every one looked at: two torpedoes at two depths are due in another order than they were asked, and the first tried, which
            // stopped at the first not yet due, held a shallow one's track back behind a deep one's, 2026-11-09)
            int kept = 0;
            for (int n = 0; n < rising.Count; n++)
            {
                Rising r = rising[n];
                // (any due more than half a minute on: left behind by a jump in the clock)
                if (r.Due > now && r.Due < now + 30f) { rising[kept++] = r; continue; }
                if (r.Due > now || r.Body != body || mark == null || spotOf == null) continue;
                object[] args = { r.Body.position + r.From, null };
                if (!(spotOf.Invoke(null, args) is bool on) || !on || !(args[1] is Vector2d spot)) continue;
                mark.Invoke(null, new object[] { spot, r.Wide, r.White });
                if (Laid == 0) FirstLaid = r.From;
                LastLaid = r.From;
                LaidOn = r.Body;
                Laid++;
            }
            rising.RemoveRange(kept, rising.Count - kept);
        }

        /// <summary>(Leaving the scene: nothing carried over.)</summary>
        internal static void Forget() { rising.Clear(); }
    }

    /// <summary>
    /// (Volumetric Armory) A copy of one of the game's splash effects, which the game makes in its place while BD Armory's splashes are
    /// drawn by Natural Ocean (see Splashes): before its first frame it asks what made it, and is the game's own splash for what is not
    /// BD Armory's, or hidden (its particles neither made nor drawn, and, for a blow Natural Ocean sounds itself, its sound silent).
    /// </summary>
    public sealed class SplashStandIn : MonoBehaviour
    {
        void Start()
        {
            int made;
            bool loud;
            try { made = Splashes.Made(transform.position, out loud); }
            catch (Exception ex) { Splashes.Failed(ex); return; }
            if (made == 0) return;
            foreach (KSPParticleEmitter e in GetComponentsInChildren<KSPParticleEmitter>(true)) e.emit = false;
            foreach (ParticleSystem s in GetComponentsInChildren<ParticleSystem>(true)) s.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            if (loud) foreach (AudioSource a in GetComponentsInChildren<AudioSource>(true)) a.mute = true;
        }
    }
}
