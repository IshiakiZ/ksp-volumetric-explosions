#if DEV
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// For the development build: BD Armory's effects set off through BD Armory's own ways in, and what this mod made of them, from
    /// the shell (tools/dev/windows/kx.py call VolumetricArmory.Tests Fire "caliber=50"). Each is a public static string X(string),
    /// for the AI Bridge's dev_call. A craft is the one flown, or "vessel=<part of its name>"; a part "part=<its number on the craft,
    /// or part of its name>", else the first with fuel in it.
    /// </summary>
    public static class Tests
    {
        static Dictionary<string, string> Words(string words)
        {
            var w = new Dictionary<string, string>();
            foreach (string word in (words ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = word.IndexOf('=');
                if (eq > 0) w[word.Substring(0, eq).ToLowerInvariant()] = word.Substring(eq + 1);
                else w[word.ToLowerInvariant()] = "1";
            }
            return w;
        }

        static float Num(Dictionary<string, string> w, string key, float fallback) =>
            w.TryGetValue(key, out string text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

        static Vessel Craft(Dictionary<string, string> w)
        {
            if (w.TryGetValue("vessel", out string name) && name != "active")
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                    if (v != null && v.vesselName != null && v.vesselName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return v;
            return FlightGlobals.ActiveVessel;
        }

        static bool Fuelled(Part p)
        {
            foreach (PartResource r in p.Resources) if (r.amount > 0 && (r.resourceName == "LiquidFuel" || r.resourceName == "Oxidizer" || r.resourceName == "MonoPropellant" || r.resourceName == "SolidFuel")) return true;
            return false;
        }

        static Part PartOf(Vessel v, Dictionary<string, string> w)
        {
            if (v == null) return null;
            if (w.TryGetValue("part", out string which))
            {
                if (int.TryParse(which, out int n) && n >= 0 && n < v.parts.Count) return v.parts[n];
                foreach (Part p in v.parts)
                    if (p.partInfo != null && (p.partInfo.name.IndexOf(which, StringComparison.OrdinalIgnoreCase) >= 0 || p.partInfo.title.IndexOf(which, StringComparison.OrdinalIgnoreCase) >= 0)) return p;
            }
            foreach (Part p in v.parts) if (Fuelled(p)) return p;
            return v.rootPart;
        }

        /// <summary>Call one of BD Armory's static methods, its arguments by their names (those not given as they default).</summary>
        static string Call(MethodInfo method, Dictionary<string, object> args)
        {
            if (method == null) return "BD Armory's method was not found";
            ParameterInfo[] ps = method.GetParameters();
            var values = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (args.TryGetValue(ps[i].Name.ToLowerInvariant(), out object v)) values[i] = v;
                else if (ps[i].HasDefaultValue) values[i] = ps[i].DefaultValue;
                else values[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            }
            try { method.Invoke(null, values); return null; }
            catch (Exception ex) { return method.Name + " failed: " + (ex.InnerException ?? ex).Message; }
        }

        /// <summary>A ray onto a part from outside it, along a way: where it strikes the part's own colliders.</summary>
        static bool Struck(Part p, Vector3 from, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            foreach (Collider c in p.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                Vector3 middle = c.bounds.center;
                var ray = new Ray(middle + from * (c.bounds.extents.magnitude + 5f), -from);
                if (c.Raycast(ray, out hit, c.bounds.extents.magnitude * 2f + 10f)) return true;
            }
            return false;
        }

        static Vector3 Offset(Vessel v, Dictionary<string, string> w, Vector3 at)
        {
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, at);
            Vector3 east = Vector3.Cross(up, (Vector3)v.mainBody.transform.up).normalized;
            Vector3 north = Vector3.Cross(east, up);
            return at + east * Num(w, "east", 0f) + north * Num(w, "north", 0f) + up * Num(w, "up", 0f);
        }

        // ---------------------------------------------------------------- what this mod has made of BD Armory's effects

        /// <summary>
        /// Where in the picture just now the craft flown is, the torpedo last seen running under the sea, and the first and last marks
        /// of white water laid for wakes: each as a fraction of the picture's width from its left and of its height from its top, and how
        /// far from the eye (m). Written to the log too, for a film's cue.
        /// </summary>
        public static string Where(string _)
        {
            Camera eye = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            if (eye == null) return "no camera";
            var say = new StringBuilder("where (across, down, m off):");
            if (FlightGlobals.ActiveVessel != null) Put(say, eye, "craft", FlightGlobals.ActiveVessel.transform.position);
            if (Splashes.Running > 0) Put(say, eye, "torpedo", Splashes.LastAt);
            if (Splashes.Laid > 0 && Splashes.LaidOn != null)
            {
                Put(say, eye, "first mark", (Vector3)(Splashes.LaidOn.position + Splashes.FirstLaid));
                Put(say, eye, "last mark", (Vector3)(Splashes.LaidOn.position + Splashes.LastLaid));
            }
            say.Append(" marks ").Append(Splashes.Laid).Append(", frames running ").Append(Splashes.Running);
            Armory.Log("test: " + say);
            return say.ToString();
        }

        static void Put(StringBuilder say, Camera eye, string name, Vector3 at)
        {
            Vector3 s = eye.WorldToViewportPoint(at);
            say.Append(' ').Append(name).Append(' ').Append(s.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
               .Append((1f - s.y).ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(s.z.ToString("F0", CultureInfo.InvariantCulture)).Append(';');
        }

        /// <summary>How many of each kind have started this flight, how many were drawn as ours at some time, how many left BD Armory's own at some time, and how many renderers are hidden just now.</summary>
        public static string Said(string _)
        {
            Volumes it = Volumes.Instance;
            var say = new StringBuilder();
            say.Append(Armory.Wanted(null) ? "ours" : "BD Armory's own").Append("; Volumetric Explosions' Level 6: ").Append(Feed.Knows ? "yes" : "no")
               .Append(", Level 7 (nuclear explosions): ").Append(Feed.KnowsNuke ? "yes" : "no");
            try { say.Append("; its catch of BD Armory's explosions: ").Append(Feed.Catching() ? "on" : "off"); } catch (Exception) { say.Append("; no catch of BD Armory's explosions in that mod"); }
            say.Append("; cutparticles ").Append(Armory.Cut ? "on" : "off").Append('\n');
            // (switched to BD Armory's own, nothing is looked for: so nothing is counted while it is, as nothing of ours is drawn)
            if (!Armory.Wanted(null)) say.Append("(switched to BD Armory's own: its effects are not looked for, so not counted, while it is)\n");
            var hidden = new int[Volumes.Kinds];
            if (it != null)
            {
                foreach (Volumes.Fx f in it.all) hidden[f.Kind] += f.Smoke.Hiding() + f.Flame.Hiding();
                foreach (Volumes.Launcher m in it.launchers) hidden[Volumes.Missile] += m.Smoke.Hiding() + m.Flame.Hiding();
                foreach (Volumes.Firearm g in it.guns) hidden[Volumes.Gun] += g.Smoke.Hiding();
                foreach (Volumes.HeldFx h in it.heldFx) hidden[Volumes.Nuke] += h.Own.Hiding();
                say.Append(it.launchers.Count).Append(" missiles and ").Append(it.guns.Count).Append(" guns on loaded craft; ").Append(it.all.Count).Append(" of BD Armory's pooled effects known, and ")
                   .Append(it.bombs.Count).Append(" of its nuclear explosions\n");
            }
            else say.Append("not running in this scene\n");
            for (int k = 0; k < Volumes.Kinds; k++)
                say.Append(Volumes.KindNames[k]).Append(": seen ").Append(Volumes.Seen[k]).Append(", ours ").Append(Volumes.Ours[k]).Append(", theirs ").Append(Volumes.Theirs[k]).Append(", hidden renderers ").Append(hidden[k]).Append('\n');
            say.Append("splashes ").Append(Splashes.Taken ? "taken" : "the game's").Append(Splashes.Why.Length > 0 ? " (" + Splashes.Why + ")" : "")
               .Append(": Natural Ocean's ").Append(Splashes.Ours).Append(", left to the explosions mod ").Append(Splashes.Left).Append(", the game's own ").Append(Splashes.Stock)
               .Append(Splashes.Last.Length > 0 ? "; the last: " + Splashes.Last : "").Append('\n');
            say.Append("torpedoes' wakes: ").Append(Splashes.Running).Append(" frames of one running under the sea");
            if (Splashes.Running > 0) say.Append(", ").Append(Splashes.Shallowest.ToString("F1", CultureInfo.InvariantCulture)).Append(" to ").Append(Splashes.Deepest.ToString("F1", CultureInfo.InvariantCulture)).Append(" m down");
            Vessel flown = FlightGlobals.ActiveVessel;
            if (Splashes.Running > 0 && flown != null)
            {
                // (the last one seen, from the craft flown: metres east and north)
                Vector3 from = Splashes.LastAt - flown.transform.position, up = (Vector3)FlightGlobals.getUpAxis(flown.mainBody, flown.transform.position);
                Vector3 east = Vector3.Cross(up, (Vector3)flown.mainBody.transform.up).normalized, north = Vector3.Cross(east, up);
                say.Append(", the last ").Append(Vector3.Dot(from, east).ToString("F0", CultureInfo.InvariantCulture)).Append(" m east and ")
                   .Append(Vector3.Dot(from, north).ToString("F0", CultureInfo.InvariantCulture)).Append(" m north of the craft");
            }
            say.Append(", ").Append(Splashes.Laid).Append(" marks of white water laid\n");
            say.Append("bubble screens under the sea: ").Append(Splashes.Boiling).Append(" frames; sonar decoys: ").Append(Splashes.Trailing).Append(" frames\n");
            say.Append("hits whose sparks were ours: ").Append(HitSparks.Thrown).Append("; sprays spat by craft an EMP shut down: ").Append(HitSparks.Spat).Append('\n');
            say.Append("chaff clouds of ours: ").Append(ChaffClouds.Made).Append(" begun, ").Append(ChaffClouds.Alive).Append(" there now\n");
            say.Append(Volumes.Report);
            return say.ToString();
        }

        /// <summary>"ours" or "theirs": the page's switch, as the key flips it.</summary>
        public static string Switch(string which)
        {
            Armory.Choose((which ?? "").Trim().ToLowerInvariant() != "theirs");
            return Armory.Ours ? "ours" : "BD Armory's own";
        }

        /// <summary>The counts back to nothing.</summary>
        public static string Zero(string _)
        {
            Array.Clear(Volumes.Seen, 0, Volumes.Kinds); Array.Clear(Volumes.Ours, 0, Volumes.Kinds); Array.Clear(Volumes.Theirs, 0, Volumes.Kinds);
            Splashes.Ours = Splashes.Left = Splashes.Stock = 0; Splashes.Last = ""; Splashes.Running = Splashes.Laid = 0; Splashes.Shallowest = float.MaxValue; Splashes.Deepest = 0f; ChaffClouds.Made = 0; HitSparks.Thrown = HitSparks.Spat = 0; Splashes.Boiling = Splashes.Trailing = 0;
            return "counts cleared";
        }

        // ---------------------------------------------------------------- BD Armory's effects, set off

        /// <summary>A part set on fire, as BD Armory sets one (its BulletHitFX.AttachFire): "caliber=50 burntime=-1 leaks=1 surface=0 part=.. vessel=..".</summary>
        public static string Fire(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            Part p = PartOf(v, w);
            if (p == null) return "no part";
            string failed = Call(Bda.AttachFire, new Dictionary<string, object>
            {
                { "hit", Offset(v, w, p.transform.position) }, { "hitpart", p }, { "caliber", Num(w, "caliber", 50f) }, { "sourcevessel", v.vesselName },
                { "burntime", Num(w, "burntime", -1f) }, { "ignitedleaks", (int)Num(w, "leaks", 1f) }, { "surfacefire", Num(w, "surface", 0f) > 0.5f },
            });
            return failed ?? "fire on " + p.partInfo.title + " (" + v.vesselName + ")";
        }

        /// <summary>A leak in a part, as a shot makes one (BulletHitFX.AttachLeak): "caliber=20 part=.. vessel=.. explosive=0 incendiary=0". BD Armory may light it, as it rolls for it.</summary>
        public static string Leak(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            Part p = PartOf(v, w);
            if (p == null) return "no part";
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, p.transform.position);
            Vector3 side = Vector3.Cross(up, p.transform.up);
            if (side.sqrMagnitude < 0.01f) side = p.transform.right;
            if (!Struck(p, side.normalized, out RaycastHit hit)) return "could not find the skin of " + p.partInfo.title;
            string failed = Call(Bda.AttachLeak, new Dictionary<string, object>
            {
                { "hit", hit }, { "hitpart", p }, { "caliber", Num(w, "caliber", 20f) }, { "explosive", Num(w, "explosive", 0f) > 0.5f }, { "incendiary", Num(w, "incendiary", 0f) > 0.5f },
                { "sourcevessel", v.vesselName }, { "inerttank", false }, { "colliderlocalhitpoint", hit.collider.transform.InverseTransformPoint(hit.point) },
            });
            return failed ?? "leak in " + p.partInfo.title + " (" + v.vesselName + ") at " + hit.point.ToString("F1");
        }

        /// <summary>Hits, as shots strike (BulletHitFX.CreateBulletHit, with no part, so no hole in one): "caliber=20 count=1 spread=0 east=.. north=.. up=.. part=.. ground=0" (ground=1: on the ground or a building under that place, facing out of it, spread along it). Past 30 mm BD Armory's darker PenFX.</summary>
        public static string Hit(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            if (v == null) return "no craft";
            Part p = w.ContainsKey("part") ? PartOf(v, w) : null;
            Vector3 at = Offset(v, w, p != null ? p.transform.position : (Vector3)v.CoMD);
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, at);
            bool ground = Num(w, "ground", 0f) > 0.5f;
            if (ground)
            {
                if (!Physics.Raycast(at + up * 400f, -up, out RaycastHit under, 3000f, 1 << 15, QueryTriggerInteraction.Ignore)) return "no ground under " + at.ToString("F1");
                at = under.point;
                up = under.normal;
            }
            int count = Mathf.Clamp((int)Num(w, "count", 1f), 1, 200);
            float spread = Num(w, "spread", 0f);
            var luck = new System.Random(7);
            string failed = null;
            for (int k = 0; k < count && failed == null; k++)
            {
                Vector3 here = at + (count > 1 ? new Vector3((float)luck.NextDouble() - 0.5f, (float)luck.NextDouble() - 0.5f, (float)luck.NextDouble() - 0.5f) * 2f * spread : Vector3.zero);
                if (ground) here -= up * Vector3.Dot(here - at, up);
                failed = Call(Bda.CreateBulletHit, new Dictionary<string, object>
                {
                    { "hitpart", null }, { "position", here }, { "hit", default(RaycastHit) }, { "normaldirection", up }, { "ricochet", false },
                    { "caliber", Num(w, "caliber", 20f) }, { "penetrationfactor", 1f }, { "team", "" },
                });
            }
            return failed ?? count + " hits at " + at.ToString("F1");
        }

        /// <summary>
        /// An EMP into a craft, as BD Armory's EMP warhead hands one over (its ModuleDrainEC on the root part, made if not there: its
        /// incomingDamage): "damage=300 vessel=..". Past the craft's threshold (100, and 100 a seat, 10 to 70 a probe core) BD Armory shuts
        /// it down for fifteen seconds and crackles about its root part for five; past five times that, for good.
        /// </summary>
        public static string Emp(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            if (v == null || v.rootPart == null) return "no craft";
            PartModule drain = null;
            foreach (PartModule m in v.rootPart.Modules)
                if (m != null && m.GetType().Name == "ModuleDrainEC") { drain = m; break; }
            if (drain == null) drain = v.rootPart.AddModule("ModuleDrainEC");
            FieldInfo into = drain != null ? drain.GetType().GetField("incomingDamage", BindingFlags.Instance | BindingFlags.Public) : null;
            if (into == null) return "no ModuleDrainEC with its incomingDamage";
            float damage = Num(w, "damage", 300f);
            into.SetValue(drain, damage);
            return "EMP of " + damage.ToString("F0", CultureInfo.InvariantCulture) + " into " + v.vesselName;
        }

        /// <summary>The flames of a missile into a tank (BulletHitFX.AttachFlames): "part=.. vessel=..". BD Armory allows them only in flight if set to, or landed, on a part with fuel, a few a craft.</summary>
        public static string Flames(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            Part p = PartOf(v, w);
            if (p == null) return "no part";
            string failed = Call(Bda.AttachFlames, new Dictionary<string, object> { { "contactpoint", p.transform.position }, { "hitpart", p } });
            return failed ?? "flames asked for on " + p.partInfo.title + " (BD Armory may refuse them)";
        }

        // ---------------------------------------------------------------- through the part modules

        static List<PartModule> Modules(Vessel v, Type type)
        {
            var found = new List<PartModule>();
            if (v == null || type == null) return found;
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m != null && type.IsInstanceOfType(m)) found.Add(m);
            return found;
        }

        static string Do(PartModule m, string name, params object[] args)
        {
            MethodInfo method = null;
            foreach (MethodInfo one in m.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (one.Name == name && one.GetParameters().Length == args.Length) { method = one; break; }
            if (method == null) return m.GetType().Name + " has no " + name;
            try { method.Invoke(m, args); return null; }
            catch (Exception ex) { return name + " failed: " + (ex.InnerException ?? ex).Message; }
        }

        static string Named(object m, string field)
        {
            FieldInfo f = Getter.FieldOf(m.GetType(), field);
            object v = f != null ? f.GetValue(m) : null;
            return v != null ? v.ToString() : "";
        }

        /// <summary>A missile fired from the craft: "which=0 name=.. how=gui|fire vessel=..". The first not yet fired (name=: the first whose part's name or title holds that, a bomb as well, as BD Armory's bombs are its missiles too), as its own button does (GuiFire), or straight (FireMissile).</summary>
        public static string Missile(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            List<PartModule> all = Modules(v, Bda.Missile);
            if (all.Count == 0) return "no missile on " + (v != null ? v.vesselName : "no craft");
            int which = (int)Num(w, "which", -1f);
            PartModule chosen = null;
            if (which >= 0 && which < all.Count) chosen = all[which];
            else
            {
                w.TryGetValue("name", out string name);
                foreach (PartModule m in all)
                {
                    if (!string.IsNullOrEmpty(name) && m.part.partInfo.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 && m.part.partInfo.title.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (Bda.MissileState == null || Bda.MissileState(m) == 0) { chosen = m; break; }
                }
            }
            if (chosen == null) return (w.ContainsKey("name") ? "no " + w["name"] + " left to fire" : "every missile fired") + " on " + v.vesselName;
            string failed = Do(chosen, w.TryGetValue("how", out string how) && how == "fire" ? "FireMissile" : "GuiFire");
            if (failed != null) return failed;
            // (somewhere to run at, "east=1500 north=0" from the craft, on the sea: fired with no target, BD Armory's guidance of a torpedo steers
            // nowhere, throwing every frame (its SLWGuidance reads the target vessel it has not got). Given the place as its target, with the
            // craft's own targeting info for the vessel that guidance reads (only for the target's size and its way along it).)
            if (w.ContainsKey("east") || w.ContainsKey("north"))
            {
                Vector3 at = Offset(v, w, v.transform.position);
                at -= (Vector3)FlightGlobals.getUpAxis(v.mainBody, at) * (float)FlightGlobals.getAltitudeAtPos((Vector3d)at, v.mainBody);
                string missed = Aim(chosen, v, at);
                string said = missed != null ? "fired " + chosen.part.partInfo.title + ", but " + missed
                            : "fired " + chosen.part.partInfo.title + " from " + v.vesselName + " at a place " + Vector3.Distance(at, v.transform.position).ToString("F0", CultureInfo.InvariantCulture) + " m off";
                Armory.Log("test: " + said);
                return said;
            }
            return "fired " + chosen.part.partInfo.title + " from " + v.vesselName;
        }

        /// <summary>A fired missile of BD Armory's sent at a place: its targeting none (so nothing it looks for moves it), the place its target's, and a vessel's targeting info its target vessel's.</summary>
        static string Aim(PartModule m, Vessel from, Vector3 at)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = m.GetType();
            PropertyInfo mode = t.GetProperty("TargetingMode", all), acquired = t.GetProperty("TargetAcquired", all);
            PropertyInfo position = t.GetProperty("TargetPosition", all), target = t.GetProperty("targetVessel", all);
            if (mode == null || acquired == null || position == null || target == null) return "no way to aim it";
            try
            {
                Component info = from.gameObject.GetComponent(target.PropertyType) ?? from.gameObject.AddComponent(target.PropertyType);
                mode.SetValue(m, Enum.Parse(mode.PropertyType, "None"), null);
                target.SetValue(m, info, null);
                position.SetValue(m, at, null);
                acquired.SetValue(m, true, null);
                return null;
            }
            catch (Exception ex) { return "aiming it failed: " + (ex.InnerException ?? ex).Message; }
        }

        struct Stop { public PartModule Weapon; public float At; }
        static readonly List<Stop> stops = new List<Stop>();

        /// <summary>The stops asked for (a burst's end), from the mod's Update.</summary>
        internal static void Pending()
        {
            for (int n = stops.Count - 1; n >= 0; n--)
            {
                if (Time.time < stops[n].At) continue;
                if (stops[n].Weapon != null) Do(stops[n].Weapon, "AGFireToggle", new KSPActionParam(KSPActionGroup.None, KSPActionType.Deactivate));
                stops.RemoveAt(n);
            }
        }

        static string Shoot(string words, string kind)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            var chosen = new List<PartModule>();
            foreach (PartModule m in Modules(v, Bda.Weapon))
            {
                bool rocket = Named(m, "eWeaponType") == "Rocket";
                if (rocket == (kind == "Rocket") && (kind == "Rocket" || Named(m, "eWeaponType") == "Ballistic")) chosen.Add(m);
            }
            if (chosen.Count == 0) return "no " + (kind == "Rocket" ? "rocket pod" : "gun") + " on " + (v != null ? v.vesselName : "no craft");
            int which = (int)Num(w, "which", -1f);
            float seconds = Num(w, "seconds", kind == "Rocket" ? 1f : 2f);
            var said = new StringBuilder();
            for (int n = 0; n < chosen.Count; n++)
            {
                if (which >= 0 && n != which) continue;
                PartModule m = chosen[n];
                // (switched on first, as its Toggle button does, where it is off)
                string state = Named(m, "weaponState");
                if (state == "Disabled" || state == "PoweringDown") Do(m, "Toggle");
                string failed = Do(m, "AGFireToggle", new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate));
                if (failed != null) return failed;
                stops.Add(new Stop { Weapon = m, At = Time.time + seconds });
                said.Append(m.part.partInfo.title).Append(" (was ").Append(state).Append(") ");
            }
            return "firing for " + seconds.ToString("F1", CultureInfo.InvariantCulture) + " s: " + said;
        }

        /// <summary>A salvo of unguided rockets from the craft's rocket pods: "which=.. seconds=1 vessel=..". The pod switched on and fired, as its action group's "Fire (Toggle)" does.</summary>
        public static string Rockets(string words) => Shoot(words, "Rocket");

        /// <summary>A burst from the craft's guns: "which=.. seconds=2 vessel=..". Each switched on and fired as its "Fire (Toggle)" action does; a turret may need a target in range to fire.</summary>
        public static string Guns(string words) => Shoot(words, "Ballistic");

        static string Drop(string words, string type)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            Vessel v = Craft(w);
            int count = Mathf.Clamp((int)Num(w, "count", 1f), 1, 40), dropped = 0;
            foreach (PartModule m in Modules(v, Bda.Dropper))
            {
                if (Named(m, "cmType") != type) continue;
                for (int k = 0; k < count; k++)
                {
                    MethodInfo drop = m.GetType().GetMethod("DropCM", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    if (drop == null) return "no DropCM";
                    try { if (drop.Invoke(m, null) is bool done && done) dropped++; }
                    catch (Exception ex) { return "DropCM failed: " + (ex.InnerException ?? ex).Message; }
                }
                if (dropped > 0) break;
            }
            return dropped > 0 ? "dropped " + dropped + " (" + type + ")" : "no " + type + " dropper with any left on " + (v != null ? v.vesselName : "no craft");
        }

        /// <summary>Flares dropped from the craft's dispensers: "count=4 vessel=..".</summary>
        public static string Flares(string words) => Drop(words, "Flare");

        /// <summary>
        /// One frame's worth of a smoke screen laid on the ground beside the craft, as a canister that has landed lays it, straight into
        /// the explosions mod (Feed.Screen, not through BD Armory): "east=30 north=0 radius=12 lasts=60 shade=0.92 amount=0.6 id=..".
        /// Asked every frame for a few seconds (a film's cues), it lays a screen whose ball grows as BD Armory's does; for queue item
        /// 15's slump (cold smoke lying on the ground spreading out), which BD Armory's own screens, thrown up tens of metres, never show.
        /// </summary>
        public static string Lay(string words)
        {
            var w = Words(words);
            Vessel v = Craft(w);
            if (v == null) return "no craft";
            Vector3 at = Offset(v, w, v.transform.position);
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, at);
            if (Physics.Raycast(at + up * 60f, -up, out RaycastHit hit, 200f, 1 << 15)) at = hit.point;
            float radius = Num(w, "radius", 12f);
            bool laid = Feed.Screen((int)Num(w, "id", 7311f), at + up * (0.3f * radius), Vector3d.zero, radius, Num(w, "lasts", 60f), Num(w, "shade", 0.92f), 0.03f, Num(w, "amount", 0.6f));
            return laid ? "laid" : "not laid";
        }

        /// <summary>A smoke screen dropped from the craft's dispensers: "count=1 vessel=..".</summary>
        public static string Smoke(string words) => Drop(words, "Smoke");

        /// <summary>Chaff dropped from the craft's dispensers: "count=2 vessel=..".</summary>
        public static string Chaff(string words) => Drop(words, "Chaff");

        /// <summary>A bubble screen dropped from the craft's bubble pods, under the sea: "count=3 vessel=..".</summary>
        public static string Bubbles(string words) => Drop(words, "Bubbles");

        /// <summary>Sonar decoys dropped from the craft's decoy pods, under the sea: "count=2 vessel=..".</summary>
        public static string Decoys(string words) => Drop(words, "Decoy");

        // ---------------------------------------------------------------- a splash

        /// <summary>
        /// A round of BD Armory's going into the sea, as far as its splash goes: one of its bullets, from its own pool, set at a spot on the
        /// sea (as a nuke's: "away=300 bearing=0" from the camera, or "east=.. north=.." from the craft) going down into it at "slant=30"
        /// degrees, "caliber=20 mass=0.1 speed=1000" (mm, kg, m/s), and the game's splash asked for there as BD Armory asks it
        /// (FXMonger.Splash at the round, its calibre over two). The bullet is left in its pool, not flown: nothing is struck.
        /// "count=1 spread=0": so many, so many metres apart along the way it goes (a burst's line of splashes), a frame apart.
        /// </summary>
        public static string Splash(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            var w = Words(words);
            if (!w.ContainsKey("away") && !w.ContainsKey("east") && !w.ContainsKey("north")) w["away"] = "300";
            string wrong = Spot(w, out Vector3d world, out float away);
            if (wrong != null) return wrong;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.ocean || FlightGlobals.getAltitudeAtPos(world, body) > 5.0) return "not on the sea there";
            throwing.Add(new Throw { At = world, Count = Mathf.Clamp((int)Num(w, "count", 1f), 1, 60), Spread = Num(w, "spread", 0f), Caliber = Num(w, "caliber", 20f),
                                      Mass = Num(w, "mass", 0.1f), Speed = Num(w, "speed", 1000f), Slant = Num(w, "slant", 30f) });
            return "splashing " + away.ToString("F0", CultureInfo.InvariantCulture) + " m from the camera; splashes " + (Splashes.Taken ? "taken" : "the game's own");
        }

        struct Throw { public Vector3d At; public int Count, Done; public float Spread, Caliber, Mass, Speed, Slant; }
        static readonly List<Throw> throwing = new List<Throw>();

        /// <summary>(From the mod's Update: the test splashes asked for, one a frame.)</summary>
        internal static void Splashing()
        {
            if (throwing.Count == 0) return;
            Throw p = throwing[0];
            CelestialBody body = FlightGlobals.currentMainBody;
            object pool = Bda.BulletPool != null ? Bda.BulletPool() : null;
            List<GameObject> list = pool != null ? Bda.PoolList(pool) : null;
            GameObject spare = null;
            if (list != null) foreach (GameObject go in list) if (go != null && !go.activeInHierarchy) { spare = go; break; }
            if (body == null || spare == null || Bda.Bullet == null) { throwing.RemoveAt(0); return; }
            Component bullet = spare.GetComponent(Bda.Bullet);
            Vector3 at = (Vector3)p.At;
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(body, p.At);
            Vector3 east = Vector3.Cross(up, (Vector3)body.transform.up).normalized;
            Vector3 way = Quaternion.AngleAxis(-p.Slant, Vector3.Cross(up, east)) * east;
            at += Vector3.ProjectOnPlane(way, up).normalized * (p.Spread * p.Done);
            at -= up * (float)FlightGlobals.getAltitudeAtPos((Vector3d)at, body);
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                Bda.Bullet.GetProperty("currentPosition", any).SetValue(bullet, at, null);
                Bda.Bullet.GetField("currentVelocity", any).SetValue(bullet, way * p.Speed);
                Bda.Bullet.GetField("bulletMass", any).SetValue(bullet, p.Mass);
                Bda.Bullet.GetField("caliber", any).SetValue(bullet, p.Caliber);
                FXMonger.Splash(at, p.Caliber / 2f);
            }
            catch (Exception ex) { Armory.Log("Tests.Splash: " + ex.Message); throwing.Clear(); return; }
            p.Done++;
            if (p.Done >= p.Count) throwing.RemoveAt(0); else throwing[0] = p;
        }

        // ---------------------------------------------------------------- a nuclear explosion

        /// <summary>
        /// Where a test's nuke goes: "away=3000 bearing=0" metres from the camera along the ground, the bearing in degrees to the right of
        /// where the camera looks; or "east=.. north=.." metres from the craft flown. On the ground there or the sea (the planet's own
        /// ground, sounded straight down where the game has made it), and "up=.." metres over that. Null, or what is wrong.
        /// </summary>
        static string Spot(Dictionary<string, string> w, out Vector3d world, out float away)
        {
            world = Vector3d.zero;
            away = 0f;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) return "no planet";
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            Vector3d at;
            if (w.ContainsKey("east") || w.ContainsKey("north"))
            {
                Vessel v = Craft(w);
                if (v == null) return "no craft";
                at = (Vector3d)v.transform.position;
                Vector3 up0 = (Vector3)FlightGlobals.getUpAxis(body, at);
                Vector3 east = Vector3.Cross(up0, (Vector3)body.transform.up).normalized, north = Vector3.Cross(east, up0);
                at += (Vector3d)(east * Num(w, "east", 0f) + north * Num(w, "north", 0f));
            }
            else
            {
                if (cam == null) return "no camera";
                Vector3 from = cam.transform.position;
                Vector3 up0 = (Vector3)FlightGlobals.getUpAxis(body, from);
                Vector3 ahead = Vector3.ProjectOnPlane(cam.transform.forward, up0);
                if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.ProjectOnPlane(cam.transform.up, up0);
                ahead = Quaternion.AngleAxis(Num(w, "bearing", 0f), up0) * ahead.normalized;
                at = (Vector3d)from + (Vector3d)(ahead * Num(w, "away", 3000f));
            }
            double lat = body.GetLatitude(at), lon = body.GetLongitude(at);
            double terrain = body.pqsController != null ? body.TerrainAltitude(lat, lon, true) : 0.0;
            bool sea = body.ocean && terrain < 0.0;
            Vector3d surface = body.GetWorldSurfacePosition(lat, lon, sea ? 0.0 : terrain);
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(body, surface);
            if (!sea && Physics.Raycast((Vector3)surface + up * 1500f, -up, out RaycastHit hit, 3000f, 1 << 15, QueryTriggerInteraction.Ignore)) surface = (Vector3d)hit.point;
            world = surface + (Vector3d)(up * Mathf.Max(1f, Num(w, "up", 0f)));
            if (cam != null) away = (float)(world - (Vector3d)cam.transform.position).magnitude;
            return null;
        }

        static string Where(Dictionary<string, string> w, float kt, float away) =>
            kt.ToString("0.###", CultureInfo.InvariantCulture) + " kt, " + away.ToString("F0", CultureInfo.InvariantCulture) + " m from the camera, "
            + Mathf.Max(1f, Num(w, "up", 0f)).ToString("F0", CultureInfo.InvariantCulture) + " m over the ground";

        /// <summary>
        /// A nuclear explosion straight into the explosions mod (Feed.Nuke, as a BD Armory bomb going off is said, but with no bomb: no damage,
        /// no sound, nothing of BD Armory's): "kt=1.5 away=3000 bearing=0 up=0", or "kt=.. east=.. north=.. up=.." from the craft flown (see
        /// Spot). What the explosions mod makes of it, as it goes: kx.py call VolumetricExplosions.Air Nukes.
        /// </summary>
        public static string Nuke(string words)
        {
            var w = Words(words);
            string wrong = Spot(w, out Vector3d world, out float away);
            if (wrong != null) return wrong;
            float kt = Num(w, "kt", 1.5f);
            CelestialBody body = FlightGlobals.currentMainBody;
            // (still over the ground, said as BD Armory says a bomb's speed: with the frame's own motion, and the planet's turning where the scene does not turn with it)
            Vector3d still = body.inverseRotation ? Vector3d.zero : body.getRFrmVel(world);
            bool laid = Feed.Nuke((Vector3)world, kt, still);
            return (laid ? "laid: " : "not laid: ") + Where(w, kt, away) + (Feed.KnowsNuke ? "" : " (the Volumetric Explosions installed is older than its Level 7: it has no nuclear explosions)");
        }

        /// <summary>
        /// A nuclear explosion through BD Armory's own way (NukeFX.CreateExplosion, as its nukes and nuclear shells set one off, with the
        /// models and the sound its AIR-2 has): its flash, its effects, its sound, and its damage within "radius=100" metres of it (BD
        /// Armory's least: mind what is there), going off after "delay=2.5" seconds; and ours for it as it goes off, if this mod takes it.
        /// "kt=1.5", and where as Nuke has it ("away=3000 bearing=0 up=0" from the camera, or "east=.. north=.." from the craft).
        /// </summary>
        public static string BdNuke(string words)
        {
            if (!Bda.Look()) return Bda.Missing;
            if (Bda.CreateNuke == null) return "no NukeFX.CreateExplosion in this BD Armory";
            var w = Words(words);
            string wrong = Spot(w, out Vector3d world, out float away);
            if (wrong != null) return wrong;
            float kt = Num(w, "kt", 1.5f);
            const string models = "BDArmory/Models/explosion/nuke/";
            string failed = Call(Bda.CreateNuke, new Dictionary<string, object>
            {
                { "position", (Vector3)world }, { "sourcevesselname", "Volumetric Armory's test" }, { "sourceweaponname", "Nuke" },
                { "delay", Num(w, "delay", 2.5f) }, { "blastradius", Num(w, "radius", 100f) }, { "yield", kt }, { "thermalshock", kt }, { "emp", false },
                { "blastsound", models + "nukeBoom" }, { "flashmodel", models + "nukeFlash" }, { "shockmodel", models + "nukeShock" },
                { "blastmodel", models + "nukeBlast" }, { "plumemodel", models + "nukePlume" }, { "debrismodel", models + "nukeScatter" },
                { "modelpath", "" }, { "soundpath", "" }, { "sourcevelocity", Vector3.zero },
            });
            return failed ?? "BD Armory's nuke set, going off after " + Num(w, "delay", 2.5f).ToString("F1", CultureInfo.InvariantCulture) + " s: " + Where(w, kt, away);
        }
    }
}
#endif
