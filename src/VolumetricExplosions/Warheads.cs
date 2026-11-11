using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// (Since 0.10.0.) BD Armory's explosions (its shells, rockets, missiles and bombs, and the ammunition and fuel that go up in a
    /// fight) caught as they go off and laid as ours: sized by the high explosive in them, in kilograms of TNT as BD Armory has them
    /// (see Plan), its own picture and light of them hidden, its sound and its damage left as they are. Found by name: nothing of BD
    /// Armory's is needed, and without it this does nothing.
    ///
    /// BD Armory keeps its explosions in pools, one for each look, and sets one going whenever something goes off. The pools are
    /// looked through once a frame, before the first camera draws (Air.BeforeDrawing), for those begun since: each is hidden there
    /// and then, and taken down as a part that died there (Records), so that one going off with a part of a craft (a missile's
    /// warhead, as the missile goes up through the game's own explosion) is one blast with it. Those left over are laid at the
    /// frame's end, after the game's own (Lay).
    ///
    /// A fight sets off dozens a second. A small shell's (under SmallBelow kilograms) is laid cheaply, with a few dozen bits, no
    /// front and no rising heat (see Plan.small), and opens a patch of air of its own only where there is room for one to spare;
    /// past so many a second (Settings.BDArmoryMost), or a few in one frame, BD Armory's own are left to show.
    ///
    /// (Since 2026-11-04.) Two kinds are always left to BD Armory: an EMP's (its blue flash, from a pool whose model's path names
    /// EMP), and those it sets off as a picture only (its "isFX": no blast behind them, nothing damaged, as an armour's own charge or
    /// a shell timed out). And what is read of each is read through delegates made once, when BD Armory is first looked for, not
    /// by reflection for each explosion in every frame.
    /// </summary>
    public static class Warheads
    {
        /// <summary>Kilograms of TNT under which a blast is a small shell's (BD Armory's 40 mm shell has 0.25, its 76 mm 5.4).</summary>
        internal const float SmallBelow = 1.5f;
        const int MostAFrame = 6;

        static bool looked, complained, anyHidden;
        static Type fxType;
        static FieldInfo pools, pooled;
        // (BD Armory's explosion's own properties, read through delegates made once: see Reader. source is the name of its
        // ExplosionSourceType, never its number: that enum's order is Other, Missile, Bullet, Rocket, BattleDamage, and is BD
        // Armory's to change.)
        static Func<object, float> began, power, caliber;
        static Func<object, Vector3> velocity;
        static Func<object, string> source, weapon;
        static Func<object, bool> picture;

        // (each of BD Armory's pools of explosions, by the path of its model, with its list read once and whether it is an EMP's)
        sealed class Pool { public object of; public List<GameObject> list; public bool emp; }
        static readonly List<Pool> known = new List<Pool>();
        static IDictionary knownFrom;
        static int knownCount = -1;
        // (the keys whose pool was gone when they were read: BD Armory never takes a key out, and puts a new pool in under the old key
        // when the first of its kind goes off after a scene's load has destroyed the last, with the count unchanged)
        static readonly List<object> knownGone = new List<object>();

        sealed class Seen
        {
            public Component fx;
            public float start = float.NaN;
            public bool hidden;
            public Renderer[] drawn;
            public Light[] lights;
        }
        static readonly Dictionary<int, Seen> seen = new Dictionary<int, Seen>();
        static readonly Queue<float> lately = new Queue<float>();
        static readonly List<Death> waiting = new List<Death>();
        static int frameNow = -1, thisFrame, newPatches;
        // (since the flight began, for the log and the development build: caught, of them small shells', left to BD Armory; and
        // its EMPs' and pictures only, which are always its own)
        static int caught, small, leftAlone, theirs;

        /// <summary>Whether BD Armory is there, its explosions' type and the members read of them found.</summary>
        /// <summary>Whether BD Armory is installed at all (its explosions' class is among the game's loaded code).</summary>
        internal static bool Installed
        {
            get
            {
                try { foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies) if (a.assembly != null && a.assembly.GetType("BDArmory.FX.ExplosionFx", false) != null) return true; }
                catch (Exception) { }
                return false;
            }
        }

        static bool Look()
        {
            if (looked) return fxType != null;
            looked = true;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                {
                    Type t = a.assembly != null ? a.assembly.GetType("BDArmory.FX.ExplosionFx", false) : null;
                    if (t == null) continue;
                    const BindingFlags Of = BindingFlags.Public | BindingFlags.Instance;
                    pools = t.GetField("explosionFXPools", BindingFlags.Public | BindingFlags.Static);
                    Type[] kinds = pools != null && pools.FieldType.IsGenericType ? pools.FieldType.GetGenericArguments() : null;
                    pooled = kinds != null && kinds.Length == 2 ? kinds[1].GetField("pool", Of) : null;
                    began = Reader<float>(t, "StartTime");
                    power = Reader<float>(t, "Power");
                    caliber = Reader<float>(t, "Caliber");
                    source = Reader<string>(t, "ExplosionSource");
                    velocity = Reader<Vector3>(t, "Velocity");
                    weapon = Reader<string>(t, "SourceWeaponName");
                    picture = Reader<bool>(t, "isFX");
                    if (pools == null || pooled == null || began == null || power == null) { Addon.Log("BD Armory is here, but not as this version knows it: its explosions are left as they are"); return false; }
                    fxType = t;
                    Addon.Log("BD Armory is here (" + a.assembly.GetName().Version + "): its explosions are laid as ours" + (Settings.BDArmory ? "" : " when bdarmory is true in settings.cfg"));
                    return true;
                }
            }
            catch (Exception ex) { Addon.Log("could not look for BD Armory: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// A reader of one of BD Armory's explosion's properties, made once: a delegate bound to its getter, so that reading it for each
        /// explosion in each frame costs a call and not a reflected look-up (until 2026-11-04, PropertyInfo.GetValue for every one of
        /// its explosions that was showing, every frame, and boxing what it gave). R string reads an enum by its name (and a string
        /// as it is). Null where there is no such property. Where the property is of another type than R, as another version of BD
        /// Armory might have it, through the slow reflected read, and nought where that will not turn into R.
        /// </summary>
        static Func<object, R> Reader<R>(Type t, string name)
        {
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            MethodInfo get = p != null ? p.GetGetMethod() : null;
            if (get == null) return null;
            try
            {
                if (p.PropertyType == typeof(R))
                    return (Func<object, R>)typeof(Warheads).GetMethod(nameof(Bound), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(t, typeof(R)).Invoke(null, new object[] { get });
                if (typeof(R) == typeof(string) && p.PropertyType.IsEnum)
                    return (Func<object, R>)typeof(Warheads).GetMethod(nameof(Named), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(t, p.PropertyType).Invoke(null, new object[] { get });
            }
            catch (Exception ex) { Addon.Log("could not read BD Armory's " + name + " directly (" + ex.Message + "): read the slow way"); }
            return of =>
            {
                try
                {
                    object v = p.GetValue(of, null);
                    if (v is R r) return r;
                    if (typeof(R) == typeof(string)) return (R)(object)v?.ToString();
                    return (R)Convert.ChangeType(v, typeof(R));
                }
                catch (Exception) { return default; }
            };
        }

        static Func<object, R> Bound<T, R>(MethodInfo get)
        {
            var read = (Func<T, R>)Delegate.CreateDelegate(typeof(Func<T, R>), get);
            return of => read((T)of);
        }

        static Func<object, string> Named<T, E>(MethodInfo get)
        {
            var read = (Func<T, E>)Delegate.CreateDelegate(typeof(Func<T, E>), get);
            return of => read((T)of).ToString();
        }

        /// <summary>
        /// BD Armory's pools of explosions, read again only when there are more of them than there were, one has been put away, or a
        /// key whose pool was gone has a pool again (a new look in a fight makes a new pool; a pool, once made, keeps its list; a scene's
        /// load destroys them all, and BD Armory makes each again under its old key the next time one of its kind goes off). Each with
        /// whether it is an EMP's, by the path of its model (its key), as BD Armory names them.
        /// </summary>
        static bool Pools()
        {
            if (!(pools.GetValue(null) is IDictionary all) || all.Count == 0) { known.Clear(); knownGone.Clear(); knownCount = -1; return false; }
            bool fresh = all != knownFrom || all.Count != knownCount;
            if (!fresh)
                foreach (Pool k in known) if (k.of is UnityEngine.Object o && o == null) { fresh = true; break; }
            if (!fresh)
                for (int n = 0; n < knownGone.Count; n++)
                {
                    object v = all[knownGone[n]];
                    if (v != null && !(v is UnityEngine.Object o && o == null)) { fresh = true; break; }
                }
            if (!fresh) return true;
            known.Clear();
            knownGone.Clear();
            foreach (DictionaryEntry kv in all)
            {
                if (kv.Value == null || (kv.Value is UnityEngine.Object o && o == null)) { knownGone.Add(kv.Key); continue; }
                if (!(pooled.GetValue(kv.Value) is List<GameObject> list)) continue;
                string path = kv.Key as string;
                known.Add(new Pool { of = kv.Value, list = list, emp = path != null && path.IndexOf("EMP", StringComparison.Ordinal) >= 0 });
            }
            knownFrom = all;
            knownCount = all.Count;
            return true;
        }

        /// <summary>BD Armory's explosions begun since the last look: hidden and taken down, or left to it (see the class). Before the first camera draws.</summary>
        internal static void Scan(Air air)
        {
            if (!Settings.CatchBDArmory) { if (anyHidden) ShowAll(); return; }
            if (!Look()) return;
            if (frameNow != Time.frameCount) { frameNow = Time.frameCount; thisFrame = 0; newPatches = 0; }
            float now = Time.time;
            while (lately.Count > 0 && now - lately.Peek() > 1f) lately.Dequeue();
            try
            {
                if (!Pools()) return;
                for (int k = 0; k < known.Count; k++)
                {
                    Pool pool = known[k];
                    // (an EMP's blue flash is not a blast of fire and smoke: left as BD Armory draws it, and none of ours)
                    if (pool.emp) continue;
                    List<GameObject> list = pool.list;
                    for (int n = 0; n < list.Count; n++)
                    {
                        GameObject go = list[n];
                        if (go == null || !go.activeInHierarchy) continue;
                        int id = go.GetInstanceID();
                        if (!seen.TryGetValue(id, out Seen s)) seen[id] = s = new Seen { fx = go.GetComponent(fxType) };
                        if (s.fx == null) continue;
                        float start = began(s.fx);
                        if (start == s.start) continue;
                        s.start = start;
                        Begun(air, go, s, now);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!complained) Debug.LogError("[VolumetricExplosions] could not look at BD Armory's explosions: " + ex);
                complained = true;
            }
        }

        static void Begun(Air air, GameObject go, Seen s, float now)
        {
            // (one BD Armory sets off as a picture only, with no blast behind it: its own, as it draws it; shown again where an
            // earlier use of the same one from its pool was hidden)
            bool fx = picture != null && picture(s.fx);
#if DEV
            // (but those Test sets off, which are pictures only so as to damage nothing, are laid as ours unless it is told not to)
            if (fx && s.start == testStart && !testTheirs) fx = false;
#endif
            if (fx) { Hide(go, s, false); theirs++; return; }
            float tnt = power(s.fx);
            Vector3d world = go.transform.position;
            string what = weapon != null ? weapon(s.fx) : null;
            // (a tank set off by the fire in it, BD Armory's FireFX: a fuel blast, not an explosive's)
            bool fuel = what == "Fuel";
            bool shell = tnt < SmallBelow && !fuel;
            bool take = tnt > 0f && Taking(air, world, shell);
            Hide(go, s, take);
            if (!take) { leftAlone++; return; }
            caught++;
            if (shell) small++;
            lately.Enqueue(now);
            thisFrame++;

            // Taken down as a part that died there, moving as it was over the ground (see Records.OverGround).
            Vector3d v = velocity != null ? (Vector3d)velocity(s.fx) : Vector3d.zero;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body != null && !body.inverseRotation) v -= body.getRFrmVel(world);
            // (A shell bursting on its way hardly carries its speed into its smoke, the air it takes in stops it at once: what BD Armory
            // gives is the shell's, a kilometre a second, or the craft's it struck. No more than sixty metres a second of it, with the
            // air to take the rest out, see Fireball.)
            if (v.sqrMagnitude > 3600.0) v = v.normalized * 60.0;
            string from = source != null ? source(s.fx) : null;
            var d = new Death { frame = Time.frameCount, world = world, velocity = v, name = "BD Armory " + (from ?? "explosion"), dry = 0.002f, size = 0.3f };
            if (fuel)
            {
                // (ten kilograms of fuel burning for each of TNT BD Armory reckons the tank at, as the bang's own reckoning goes the
                // other way (Air: a tenth of what burns); and none where the part itself has died there this frame, whose own contents
                // the records have)
                if (!Records.Near(world, 3.0)) { d.fuel = 4.5 * tnt; d.oxidizer = 5.5 * tnt; }
            }
            else d.explosive = tnt;
            Records.Set(d);
            waiting.Add(d);
            if (Settings.Log) Addon.Log("BD Armory's " + (from ?? "explosion") + (what != null ? " (" + what + ")" : "") + ", " + tnt.ToString(tnt < 1f ? "F3" : "F1") + " kg of TNT" + (caliber != null ? ", " + caliber(s.fx).ToString("F0") + " mm" : "") + ": laid as ours");
        }

        /// <summary>Whether to lay this one as ours: not past so many a second nor a few in a frame; a small shell's only where a patch of air takes it or there is room for one to spare.</summary>
        static bool Taking(Air air, Vector3d world, bool shell)
        {
            if (lately.Count >= Settings.BDArmoryMost || thisFrame >= MostAFrame) return false;
            if (!shell) return true;
            if (air.HasPatchFor(world)) return true;
            if (air.Spare() - newPatches > 2) { newPatches++; return true; }
            return false;
        }

        /// <summary>BD Armory's own picture of one, and its light, hidden or shown again (its pools use each again and again).</summary>
        static void Hide(GameObject go, Seen s, bool hide)
        {
            if (hide == s.hidden) return;
            if (s.drawn == null) { s.drawn = go.GetComponentsInChildren<Renderer>(true); s.lights = go.GetComponentsInChildren<Light>(true); }
            foreach (Renderer r in s.drawn) if (r != null) r.enabled = !hide;
            foreach (Light l in s.lights) if (l != null) l.enabled = !hide;
            s.hidden = hide;
            if (hide) anyHidden = true;
        }

        /// <summary>Every one hidden shown again: when this is switched off, and as the flight ends.</summary>
        internal static void ShowAll()
        {
            foreach (Seen s in seen.Values)
            {
                if (!s.hidden || s.drawn == null) continue;
                foreach (Renderer r in s.drawn) if (r != null) r.enabled = true;
                foreach (Light l in s.lights) if (l != null) l.enabled = true;
                s.hidden = false;
            }
            anyHidden = false;
        }

        internal static bool Waiting => waiting.Count > 0;

        /// <summary>At the frame's end, after the game's own explosions have taken what went off with them: the rest laid by themselves.</summary>
        internal static void Lay(Air air)
        {
            foreach (Death d in waiting)
                if (!d.taken) air.Lay(d.world);
            waiting.Clear();
        }

        /// <summary>As the flight ends.</summary>
        internal static void Forget()
        {
            ShowAll();
            seen.Clear();
            lately.Clear();
            waiting.Clear();
            known.Clear(); knownGone.Clear(); knownFrom = null; knownCount = -1;
            if (caught + leftAlone + theirs > 0) Addon.Log("BD Armory's explosions this flight: " + caught + " laid as ours (" + small + " of them small shells'), " + leftAlone + " left as BD Armory's own, and " + theirs + " pictures only, always its own");
            caught = small = leftAlone = theirs = 0;
        }

        /// <summary>(For the development build and the log: what has been caught this flight.)</summary>
        public static string Said(string _) =>
            !Look() ? "BD Armory is not here" : caught + " of BD Armory's explosions laid as ours this flight (" + small + " small shells'), " + leftAlone + " left as its own, " + theirs + " pictures only (isFX) left as its own; " + lately.Count + " in the last second, of at most " + Settings.BDArmoryMost + (Settings.BDArmory ? "" : "; switched off");

        /// <summary>(For the development build: switched on or off while the game runs.)</summary>
        public static string Catch(string v) { Settings.BDArmory = v != "off"; if (!Settings.BDArmory) ShowAll(); return Settings.BDArmory ? "BD Armory's explosions laid as ours" : "BD Armory's own explosions"; }

#if DEV
        static MethodInfo create;
        // (Test's own explosions: when they began, as BD Armory has it (its StartTime, the game's time when it was set going), and
        // whether they are to be left as BD Armory's own pictures only, as theirs=1 asks, to see that rule)
        static float testStart = float.NaN;
        static bool testTheirs;

        /// <summary>
        /// (Development build: BD Armory's own explosions set off through it, as its shells and warheads are, so many metres from the
        /// craft flown, as words: "tnt=0.25 east=40 north=0 up=5 surface=1 source=Bullet caliber=40 model=BDArmory/Models/explosion/30mmExplosion
        /// count=1 spread=0". surface=1 has "up" from the ground there. As effects only (BD Armory's own isfx): nothing is damaged.
        /// Since 2026-11-04 pictures only are left as BD Armory's own (see Begun); these are laid as ours all the same, told by when
        /// they began, unless the words say theirs=1, to see that rule.)
        /// </summary>
        public static string Test(string words)
        {
            if (!Look()) return "BD Armory is not here";
            var w = new Dictionary<string, string>();
            foreach (string word in (words ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = word.IndexOf('=');
                if (eq > 0) w[word.Substring(0, eq).ToLowerInvariant()] = word.Substring(eq + 1);
            }
            float Get(string key, float fallback) => w.TryGetValue(key, out string text) && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (v == null || body == null) return "no craft flown";
            if (create == null)
                foreach (MethodInfo m in fxType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "CreateExplosion" && (create == null || m.GetParameters().Length > create.GetParameters().Length)) create = m;
            if (create == null) return "BD Armory's CreateExplosion was not found";
            ParameterInfo[] ps = create.GetParameters();
            Vector3d up = FlightGlobals.getUpAxis(body, v.CoMD), east = Vector3d.Cross(up, body.transform.up).normalized, north = Vector3d.Cross(east, up);
            int count = Mathf.Clamp((int)Get("count", 1f), 1, 200);
            float spread = Get("spread", 0f), tnt = Get("tnt", 0.25f);
            var luck = new System.Random(11);
            testStart = Time.time;
            testTheirs = w.TryGetValue("theirs", out string leave) && leave == "1";
            for (int k = 0; k < count; k++)
            {
                double e = Get("east", 40f) + (count > 1 ? (luck.NextDouble() - 0.5) * 2.0 * spread : 0.0), n = Get("north", 0f) + (count > 1 ? (luck.NextDouble() - 0.5) * 2.0 * spread : 0.0);
                Vector3d at = v.CoMD + east * e + north * n;
                double high = Get("up", 5f);
                if (w.TryGetValue("surface", out string s) && s == "1")
                {
                    double lat = body.GetLatitude(at), lon = body.GetLongitude(at);
                    at = body.GetWorldSurfacePosition(lat, lon, Math.Max(body.ocean ? 0.0 : double.MinValue, body.TerrainAltitude(lat, lon)) + high);
                }
                else at += up * high;
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    object fallback = ps[i].DefaultValue;
                    args[i] = fallback != null && !(fallback is DBNull) ? fallback : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    switch (ps[i].Name)
                    {
                        case "position": args[i] = (Vector3)at; break;
                        case "tntMassEquivalent": args[i] = tnt * (count > 1 ? (float)(0.6 + 0.8 * luck.NextDouble()) : 1f); break;
                        case "explModelPath": args[i] = w.TryGetValue("model", out string model) ? model : "BDArmory/Models/explosion/explosion"; break;
                        case "soundPath": args[i] = w.TryGetValue("sound", out string sound) ? sound : "BDArmory/Sounds/explode1"; break;
                        case "explosionSourceType": args[i] = Enum.Parse(ps[i].ParameterType, w.TryGetValue("source", out string from) ? from : "Bullet", true); break;
                        case "caliber": args[i] = Get("caliber", 40f); break;
                        case "sourceWeaponName": args[i] = w.TryGetValue("weapon", out string named) ? named : null; break;
                        case "isfx": args[i] = true; break;
                    }
                }
                create.Invoke(null, args);
            }
            return count + " of BD Armory's explosions set off, " + tnt + " kg of TNT" + (count > 1 ? " or so" : "");
        }
#endif
    }
}
