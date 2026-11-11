using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// (Since 0.10.0.) The settings on a page of their own in Keystone's window, where Keystone is installed: settings.cfg's, kept
    /// in step with the file both ways. The file is read as the game starts and at every flight, and the page set from it; a
    /// change on the page goes into the mod's settings at once (most are used as they are, the rest at the next flight) and is
    /// written into the file a second later, each value in its own line, the file's notes left as they are. The finer ones are
    /// in the page's Advanced view (Keystone 0.11.0; an older Keystone shows them all). Keystone is looked up by name, as Riders
    /// does: this mod does not need it, and without it the file alone is the settings, as before.
    /// </summary>
    static class Page
    {
        sealed class One
        {
            public string Key;
            public bool Fine;
            public Func<object> Get;               // the mod's setting now, as Keystone's has it (bool, float or int)
            public Action<object> Set;             // and the mod's setting set from Keystone's
            public Func<string> Written;           // as the file has it
            public object Made;                    // Keystone's setting
            public PropertyInfo Value;
        }

        static readonly List<One> all = new List<One>();
        static object mod;
        static bool made, syncing;
        static float writeAt = -1f;
        static readonly CultureInfo Plain = CultureInfo.InvariantCulture;
        static readonly string[] Breezes = { "still", "as it comes", "always some" };

        static One Toggle(string key, string label, bool installed, Func<bool> get, Action<bool> set, string hint, bool fine = false) =>
            new One { Key = key, Fine = fine, Get = () => get(), Set = v => set((bool)v), Written = () => get() ? "true" : "false", Made = new object[] { "toggle", label, installed, hint } };
        static One Slider(string key, string label, float installed, float least, float most, int decimals, Func<float> get, Action<float> set, string hint, bool fine = false) =>
            new One { Key = key, Fine = fine, Get = () => get(), Set = v => set((float)v), Written = () => get().ToString(decimals == 0 ? "0" : "0.###", Plain), Made = new object[] { "slider", label, installed, least, most, decimals, hint } };

        /// <summary>The page, once (as the game starts): nothing where Keystone is not installed.</summary>
        public static void Make()
        {
            if (made) return;
            made = true;
            try
            {
                Type kit = typeof(Page).Assembly.GetType("Keystone.Kit", false);
                if (kit == null)
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "Keystone") continue;
                        kit = a.GetType("Keystone.Kit", false);
                        if (kit != null) break;
                    }
                if (kit == null) return;
                MethodInfo register = kit.GetMethod("Register", new[] { typeof(string), typeof(string), typeof(string) });
                if (register == null) return;
                mod = register.Invoke(null, new object[] { "Volumetric Explosions", Addon.Version, "Explosions, fire and smoke made of a small simulation: fireballs, smoke that billows, drifts in the wind and is pushed about by what goes through it, shock fronts, debris, burn marks. The same settings as settings.cfg in its PluginData folder: a change here is written there. Some take effect at the next flight." });
                if (mod == null) return;
                Type m = mod.GetType();
                MethodInfo toggle = m.GetMethod("Toggle", new[] { typeof(string), typeof(string), typeof(bool), typeof(string) });
                MethodInfo slider = m.GetMethod("Slider", new[] { typeof(string), typeof(string), typeof(float), typeof(float), typeof(float), typeof(string), typeof(int), typeof(string) });
                MethodInfo choice = m.GetMethod("Choice", new[] { typeof(string), typeof(string), typeof(int), typeof(string[]), typeof(string) });
                MethodInfo heading = m.GetMethod("Heading", new[] { typeof(string) });
                MethodInfo advanced = m.GetMethod("Advanced", Type.EmptyTypes), simple = m.GetMethod("Simple", Type.EmptyTypes);
                if (toggle == null || slider == null || choice == null || heading == null) return;
                bool fineNow = false;
                void Fine(bool fine)
                {
                    if (fine == fineNow || advanced == null || simple == null) return;
                    (fine ? advanced : simple).Invoke(mod, null);
                    fineNow = fine;
                }
                void Add(One one)
                {
                    Fine(one.Fine);
                    object[] a = (object[])one.Made;
                    one.Made = (string)a[0] == "toggle" ? toggle.Invoke(mod, new object[] { one.Key, a[1], a[2], a[3] })
                        : slider.Invoke(mod, new object[] { one.Key, a[1], a[2], a[3], a[4], "", a[5], a[6] });
                    one.Value = one.Made.GetType().GetProperty("Value");
                    all.Add(one);
                }
                void Head(string text) { Fine(false); heading.Invoke(mod, new object[] { text }); }

                One on = Toggle("enabled", "Volumetric Explosions", true, () => Settings.Enabled, v => Settings.Enabled = v, "Off: the game's own explosions, from the next flight.");
                Add(on);
                Head("How they look");
                Add(Slider("quality", "Quality", 1f, 0.2f, 3f, 2, () => Settings.Quality, v => Settings.Quality = v, "How many particles, and how finely the smoke is stepped through: 0.2 light to 3 heavy."));
                Add(Slider("max_particles", "Most particles at once", 30000f, 2000f, 120000f, 0, () => Settings.MaxParticles, v => Settings.MaxParticles = Mathf.RoundToInt(v), "The most particles alive in the scene at once.", true));
                Add(Slider("size", "How big explosions are", 1f, 0.3f, 3f, 2, () => Settings.Size, v => Settings.Size = v, "0.3 to 3."));
                Add(Slider("smoke", "How long smoke hangs", 1f, 0f, 4f, 2, () => Settings.Smoke, v => Settings.Smoke = v, "How long smoke hangs in the air: 0 none, to 4."));
                Add(Slider("thick", "How thick the smoke is", 1.6f, 0.3f, 4f, 2, () => Settings.Thick, v => Settings.Thick = v, "0.3 thin, to 4."));
                Add(Slider("detail", "Billows in its edge", 0.6f, 0f, 1f, 2, () => Settings.Detail, v => Settings.Detail = v, "How deeply the billows are cut into the edge of the smoke: 0 smooth, to 1.", true));
                Add(Slider("thin", "Thin smoke left out", 2f, 0f, 8f, 1, () => Settings.Thin, v => Settings.Thin = v, "Smoke too thin to see is not drawn (a small saving): how many 255ths of what is behind it a line of sight's smoke may hide before any of it is drawn. 0 draws all of it.", true));
                Add(Toggle("volume", "Smoke and fire as a true volume", true, () => Settings.Volume, v => Settings.Volume = v, "Off: soft sprites everywhere.", true));
                Add(Toggle("half", "The volume drawn at half size", true, () => Settings.Half, v => Settings.Half = v, "With half as many pixels each way, and enlarged: several times less work, a little softer close up. Off: at the screen's full size.", true));
                Add(Slider("sea_billows", "Water thrown up: its billows", 0.5f, 0f, 1f, 2, () => Settings.SeaBillows, v => Settings.SeaBillows = v, "How deeply the billows of the water a blast at the sea throws up are cut, and how strongly they shade one another, against smoke's: 1 as smoke.", true));
                Add(Toggle("light", "Explosions light what is round them", true, () => Settings.Light, v => Settings.Light = v, "The ground and the wreck round a blast or a fire lit by it."));
                Add(Toggle("debris", "Debris", true, () => Settings.Debris, v => Settings.Debris = v, "Pieces of the destroyed parts thrown out."));
                Add(Toggle("scorch", "Burn marks", true, () => Settings.Scorch, v => Settings.Scorch = v, "Burn marks on the ground."));
                Add(Toggle("shadows", "Smoke's shadow", true, () => Settings.Shadows, v => Settings.Shadows = v, "Smoke throws its shadow on the ground, buildings and ships under it, in sunlight."));
                Add(Toggle("shaded", "Shadows into the smoke", true, () => Settings.Shaded, v => Settings.Shaded = v, "What stands in the sun (the craft, the tower, buildings, the ground) throws its shadow into the smoke nearest the camera.", true));
                Head("Fire");
                Add(Toggle("embers", "Embers", true, () => Settings.Embers, v => Settings.Embers = v, "A fire on the ground sends up embers, glowing bits carried up in its heat that dim and go out."));
                Add(Slider("fire_bright", "How bright the flames are", 0.45f, 0.1f, 1.5f, 2, () => Settings.FireBright, v => Settings.FireBright = v, "How bright a fire's folded flame is.", true));
                Add(Slider("fire_tall", "How tall fires burn", 1.5f, 0.5f, 3f, 2, () => Settings.FireTall, v => Settings.FireTall = v, "How high a fire on the ground burns, against the height its width gives its flames.", true));
                Add(Slider("fire_tongues", "How big its tongues are", 1f, 0.5f, 2f, 2, () => Settings.FireTongues, v => Settings.FireTongues = v, "The size of a fire's tongues of flame, against the fire's.", true));
                Add(Slider("fire_light", "How it lights its smoke", 8f, 0f, 24f, 1, () => Settings.FireLight, v => Settings.FireLight = v, "How strongly a fire's light falls on the smoke round it.", true));
                Head("Shock fronts");
                Add(Toggle("shockwave", "Shock fronts", true, () => Settings.Shockwave, v => Settings.Shockwave = v, "The shock front of a blast, in air: it bends the picture behind it as it races outward."));
                Add(Slider("bend", "How strongly they bend the picture", 1f, 0f, 2f, 2, () => Settings.Bend, v => Settings.Bend = v, "And jolt the camera: 0 not at all, to 2.", true));
                Add(Toggle("shake", "They shake the camera", true, () => Settings.Shake, v => Settings.Shake = v, "When a shock front reaches the camera, the camera is jolted and the whole picture gives for a moment."));
                Head("Wind and what moves the smoke");
                // (the breeze is a choice of three: a Keystone Choice, made apart from the others)
                One breeze = new One { Key = "breeze", Get = () => Settings.Breeze, Set = v => Settings.Breeze = Mathf.Clamp((int)v, 0, 2), Written = () => Breezes[Mathf.Clamp(Settings.Breeze, 0, 2)] };
                breeze.Made = choice.Invoke(mod, new object[] { "breeze", "Wind for the smoke", 1, new[] { "still", "as it comes", "always some" }, "When there is a wind for smoke to drift on: still (never, whatever the weather); as it comes (still nine times in ten, mostly a light air otherwise, a strong wind very rarely; or the weather mod's wind where there is one); always some." });
                breeze.Value = breeze.Made.GetType().GetProperty("Value");
                all.Add(breeze);
                Add(Slider("wind", "How strong the wind is", 1f, 0f, 4f, 2, () => Settings.Wind, v => Settings.Wind = v, "Made up or the weather's: 0 always still air, to 4."));
                Add(Slider("trail_lingers", "How long engines' trails stay", 20f, 1f, 60f, 1, () => Settings.TrailLingers, v => Settings.TrailLingers = v, "An engine's trail stays in the air so many times as long as its own smoke lasts, thinning as it spreads.", true));
                Add(Toggle("push", "Ships and exhaust push smoke", true, () => Settings.Push, v => Settings.Push = v, "Ships and engine exhaust push smoke about.", true));
                Add(Toggle("predict", "Smoke made finer ahead of what moves", true, () => Settings.Predict, v => Settings.Predict = v, "The way each thing moving through smoke is about to go is looked ahead along, and the smoke in its way made finer before it gets there.", true));
                Add(Toggle("wakes", "Wakes", true, () => Settings.Wakes, v => Settings.Wakes = v, "What goes through smoke leaves a tunnel in it, sharp at first, that fills in again.", true));
                Add(Toggle("vortices", "Eddies", true, () => Settings.Vortices, v => Settings.Vortices = v, "The air behind what goes through smoke is left turning and carries the smoke round: eddies behind blunt things, two tubes behind wings.", true));
                Add(Toggle("collide", "Smoke stopped by what is solid", true, () => Settings.Collide, v => Settings.Collide = v, "Smoke does not pass through the ground, buildings or ships.", true));
                Head("Sound");
                Add(Toggle("sound_travels", "Sound takes its time", true, () => Settings.SoundTravels, v => Settings.SoundTravels = v, "The bang is heard when its sound gets to the camera (a second for every 340 metres at sea level), as the shock front does. Off: on the instant, as the game has it."));
                Add(Toggle("own_bang", "The bang made from the blast", true, () => Settings.OwnBang, v => Settings.OwnBang = v, "A crack close by, a boom and a long rumble far off, as big as what went off. Off: the game's own two recordings.", true));
                Add(Toggle("recorded", "Recordings of real blasts and fires", true, () => Settings.Recorded, v => Settings.Recorded = v, "Where the download has them. Off: the roar made by the mod alone.", true));
                Head("With BD Armory");
                Add(Toggle("armory", "BD Armory's fire and smoke as volumes", true, () => Settings.Armory, v => Settings.Armory = v, "With BD Armory: a burning part's fire and its smoke, a holed tank's spray, a missile's flame and the trail it leaves hanging, rockets' trails, guns' smoke, the puff and sparks where a shot strikes, a flare's trail, a smoke screen's cloud, chaff, and a nuke as a nuclear explosion, drawn in this mod's air. Only how they look: its fires still burn, its smoke still blinds its sensors, its sound and damage are its own. Off: all of them as BD Armory draws them. Nothing without BD Armory."));
                Add(Toggle("armory_ours", "Ours (off: BD Armory's own)", true, () => Settings.ArmoryOurs, v => Settings.ArmoryOurs = v, "Off: BD Armory's own particles everywhere, its explosions too, at once. In flight the key flips this and says on the screen which is drawn, for setting the two side by side: B with the game's modifier key held (Alt; Option on a Mac), or the key named as armory_key in settings.cfg. Where there is no room for a volume (or no air for one) BD Armory's own are drawn either way."));
                Add(Toggle("bdarmory", "BD Armory's explosions as these", true, () => Settings.BDArmory, v => Settings.BDArmory = v, "With BD Armory: its shells, rockets, missiles and bombs go off as these, sized by their explosive (its sound and damage stay its own). Off: its own."));
                Add(Slider("bdarmory_most", "Most of BD Armory's a second", 40f, 0f, 400f, 0, () => Settings.BDArmoryMost, v => Settings.BDArmoryMost = Mathf.RoundToInt(v), "The most of BD Armory's explosions a second laid as these: past it, its own show until the second is up.", true));
                Add(Toggle("armory_fires", "Fires", true, () => Settings.ArmoryFires, v => Settings.ArmoryFires = v, "A burning part, a fire on the ground, the flames of a missile into a tank: a fire in the air there, its smoke rising off it.", true));
                Add(Toggle("armory_leaks", "Leaks", true, () => Settings.ArmoryLeaks, v => Settings.ArmoryLeaks = v, "A holed tank's spray of fuel, out of the hole and away.", true));
                Add(Toggle("armory_missiles", "Missiles", true, () => Settings.ArmoryMissiles, v => Settings.ArmoryMissiles = v, "A missile's flame and its trail of smoke, which hangs where it went (a torpedo's track on the sea is with the splashes).", true));
                Add(Toggle("armory_rockets", "Rockets", true, () => Settings.ArmoryRockets, v => Settings.ArmoryRockets = v, "An unguided rocket's trail of smoke while its motor burns (its little flame is left as it is).", true));
                Add(Toggle("armory_guns", "Guns", true, () => Settings.ArmoryGuns, v => Settings.ArmoryGuns = v, "The smoke out of a gun's muzzle as it fires (its flash is left as it is).", true));
                Add(Toggle("armory_hits", "Hits and EMPs", true, () => Settings.ArmoryHits, v => Settings.ArmoryHits = v, "The puff of smoke where a shot strikes, into the smoke already there, and its sparks: bright streaks thrown off the face it struck, falling and dimming (its flash is left as it is). And sparks spat from the parts of a craft an EMP shut down.", true));
                Add(Toggle("armory_countermeasures", "Countermeasures", true, () => Settings.ArmoryCountermeasures, v => Settings.ArmoryCountermeasures = v, "A flare's trail of white smoke, a smoke screen's cloud, and chaff's: a pale puff that thins in a few seconds, glittering in the sun as its fibres tumble. On Natural Ocean's sea, a bubble screen's white water boiling up over it, and a sonar decoy's line of bubbles.", true));
                Add(Toggle("armory_nukes", "Nuclear explosions", true, () => Settings.ArmoryNukes, v => Settings.ArmoryNukes = v, "A nuke's flash and the light it throws, its fireball rising into a mushroom cloud that stands for minutes, the dust drawn up under it and the dust its blast lifts off the ground, worked out from the physics of one, in place of BD Armory's own (its sound, its EMP and its damage are its own as ever).", true));
                Add(Toggle("armory_splashes", "Splashes and wakes", true, () => Settings.ArmorySplashes, v => Settings.ArmorySplashes = v, "With Natural Ocean: its bullets', shells' and rockets' splashes into the sea, as big as each one's blow: a spout of spray for a gun's rounds, the whole blow with its crater and sound for a big shell; and a torpedo's track, white water on the sea over it.", true));
                Add(Toggle("armory_cut", "Hidden particles not worked out", true, () => Settings.ArmoryCut, v => Settings.ArmoryCut = v, "BD Armory's particles hidden for ours are not worked out either while they are hidden: cheaper. Off: they are only not drawn.", true));
                Head("The game");
                Add(Toggle("blasts_push", "Blasts push craft", false, () => Settings.Knock, v => Settings.Knock = v, "A blast pushes craft, and breaks what it knocks harder than it can take (a tank's blast can take the tank beside it). It changes the game, so it is off as installed."));
                Add(Slider("patches", "Most patches of air at once", 12f, 6f, 15f, 0, () => Settings.Patches, v => Settings.Patches = Mathf.Clamp(Mathf.RoundToInt(v), 6, 15), "Explosions' and engines' trails together: more hold more fires and trails at once in a battle, at some 40 MB of memory each.", true));
                Add(Slider("gridsaframe", "Most grids made a frame", 3f, 0f, 12f, 0, () => Settings.GridsAFrame, v => Settings.GridsAFrame = Mathf.Clamp(Mathf.RoundToInt(v), 0, 12), "Over all the smoke: what covers the most of the screen first, the rest the next frame. 0: as many as are due.", true));
                Add(Slider("threads", "Worker threads", 0f, 0f, 32f, 0, () => Settings.Threads, v => Settings.Threads = Mathf.RoundToInt(v), "For moving the particles and (without the library) making the grids: 0 picks by itself.", true));
                Add(Toggle("native", "The mod's own library", true, () => Settings.Native, v => Settings.Native = v, "Make the grids with the mod's own library where there is one (far less processor time). Off: always the C# (the picture is the same).", true));
                Add(Toggle("log", "Write each explosion to the log", false, () => Settings.Log, v => Settings.Log = v, "How each explosion was read (what blew up, why, where), in KSP.log.", true));
                Fine(false);

                PropertyInfo onSwitch = m.GetProperty("Switch");
                if (onSwitch != null && onSwitch.CanWrite) onSwitch.SetValue(mod, on.Made, null);
                FieldInfo status = m.GetField("Status");
                if (status != null) status.SetValue(mod, (Func<string>)(() => Settings.Enabled ? VolumetricArmory.Volumes.Report : "switched off: the game's own explosions"));
                // (the armory's key among every mod's keys, where BD Armory is installed: Keystone 0.6 and later list a key a mod reads by itself)
                MethodInfo uses = m.GetMethod("Uses", new[] { typeof(KeyCode), typeof(bool), typeof(string) });
                if (uses != null && Settings.ArmoryKey != KeyCode.None && Warheads.Installed) uses.Invoke(mod, new object[] { Settings.ArmoryKey, true, "BD Armory's effects: ours or its own, in flight" });
                FromSettings();
                EventInfo changed = m.GetEvent("Changed");
                if (changed != null) changed.AddEventHandler(mod, (Action)Changed);
                var keeper = new GameObject("Volumetric Explosions: its page kept with settings.cfg");
                UnityEngine.Object.DontDestroyOnLoad(keeper);
                keeper.AddComponent<PageKeeper>();
            }
            catch (Exception ex) { mod = null; all.Clear(); Addon.Log("no page in Keystone's window (" + (ex.InnerException ?? ex).Message + "); the settings are settings.cfg's alone"); }
        }

        /// <summary>The page set from the mod's settings as the file has them (as the game starts, and at each flight).</summary>
        public static void FromSettings()
        {
            if (mod == null) return;
            syncing = true;
            try { foreach (One one in all) one.Value.SetValue(one.Made, Convert.ChangeType(one.Get(), one.Value.PropertyType, Plain), null); }
            catch (Exception ex) { Addon.Log("the page could not be set from settings.cfg: " + ex.Message); }
            finally { syncing = false; }
        }

        static void Changed()
        {
            if (syncing || mod == null) return;
            try { foreach (One one in all) one.Set(one.Value.GetValue(one.Made, null)); }
            catch (Exception ex) { Addon.Log("a setting on the page could not be taken: " + ex.Message); }
            writeAt = Time.realtimeSinceStartup + 1f;
        }

        public static void Tick() { if (writeAt >= 0f && Time.realtimeSinceStartup >= writeAt) Write(); }

        /// <summary>A setting was changed by the mod itself (the armory's key): the page set from it, and the file a second on.</summary>
        public static void Took() { if (mod == null) return; FromSettings(); writeAt = Time.realtimeSinceStartup + 1f; }

#if DEV
        /// <summary>(Development build) The page made now (a reloaded build does not run the game's start again), and what it holds.</summary>
        public static string MakeNow() { Make(); return Says(); }
        public static string Says()
        {
            if (mod == null) return made ? "no page (no Keystone, or it failed: see the log)" : "not made yet";
            var said = new System.Text.StringBuilder(all.Count + " settings on the page:");
            foreach (One one in all) said.Append(' ').Append(one.Key).Append('=').Append(one.Written());
            return said.Append(writeAt >= 0f ? " (to be written)" : "").ToString();
        }
#endif

        /// <summary>What is waiting to be written, written now (before the file is read again, at a flight's start).</summary>
        public static void Flush() { if (writeAt >= 0f) Write(); }

        static readonly Regex Line = new Regex(@"^(\s*)([A-Za-z_]+)(\s*=\s*)([^/]*?)(\s*)(//.*)?$");

        /// <summary>
        /// The file brought into step with the page: each setting's line given its value (its note and its place left as they are),
        /// a setting the file has no line for put in before its closing brace, a file that is not there made whole.
        /// </summary>
        static void Write()
        {
            writeAt = -1f;
            if (mod == null) return;
            string path = Settings.Folder + "PluginData/settings.cfg";
            try
            {
                var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
                var done = new HashSet<string>();
                int close = -1, open = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("VOLUMETRIC_EXPLOSIONS", StringComparison.Ordinal)) open = i;
                    if (open >= 0 && t == "}") { close = i; break; }
                    Match k = Line.Match(lines[i]);
                    if (open < 0 || !k.Success) continue;
                    One one = all.Find(o => o.Key == k.Groups[2].Value);
                    if (one == null || done.Contains(one.Key)) continue;
                    string value = one.Written(), was = k.Groups[4].Value;
                    // (the notes kept in their column where the value is no longer than the one it takes the place of)
                    string pad = k.Groups[6].Success ? new string(' ', Math.Max(1, was.Length + k.Groups[5].Value.Length - value.Length)) : "";
                    lines[i] = k.Groups[1].Value + k.Groups[2].Value + k.Groups[3].Value + value + pad + (k.Groups[6].Success ? k.Groups[6].Value : "");
                    done.Add(one.Key);
                }
                if (open < 0)
                {
                    lines.Clear();
                    lines.Add("// Volumetric Explosions settings. Change a value, save, and start a new flight (or change them on its page in Keystone's window).");
                    lines.Add("VOLUMETRIC_EXPLOSIONS");
                    lines.Add("{");
                    close = 3;
                    lines.Add("}");
                }
                else if (close < 0) { lines.Add("}"); close = lines.Count - 1; }
                foreach (One one in all)
                    if (!done.Contains(one.Key)) lines.Insert(close++, "\t" + one.Key + " = " + one.Written());
                Directory.CreateDirectory(Settings.Folder + "PluginData");
                File.WriteAllText(path, string.Join("\n", lines.ToArray()) + "\n");
            }
            catch (Exception ex) { Addon.Log("settings.cfg could not be written (" + ex.Message + "): the page's settings are in use until the next start"); }
        }
    }

    /// <summary>Writes the page's changes into settings.cfg a second after they are made, wherever the game is.</summary>
    sealed class PageKeeper : MonoBehaviour
    {
        void Update() => Page.Tick();
        void OnApplicationQuit() => Page.Flush();
    }

    /// <summary>The settings read and the page made as the game starts, so that they are in Keystone's window before any flight.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class PageAtStart : MonoBehaviour
    {
        void Start()
        {
            try { Settings.Load(); Page.Make(); } catch (Exception ex) { Addon.Log("its page: " + ex.Message); }
            Destroy(gameObject);
        }
    }
}
