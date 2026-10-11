using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("VolumetricExplosions", 0, 5)]
#endif

namespace VolumetricExplosions
{
    /// <summary>
    /// Volumetric Explosions: replaces the game's flat explosion sprites with a small simulation of fire,
    /// smoke and dust made of thousands of particles.
    ///
    /// The game keeps seven explosion effects, weakest to strongest, and makes a copy of one whenever
    /// something blows up. This swaps those seven for our own, so every explosion in the game (crashes,
    /// overheating, staging mishaps, other mods' explosions) comes through here with no further hooks.
    /// Our copies hold nothing but the bang; when one appears, the <see cref="Air"/> works out what blew
    /// up, why and where, and sets the particles off.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Addon : MonoBehaviour
    {
        public const string Version = "0.5.0";

        static GameObject[] ours;            // built once, reused for every flight
        static Transform shelf;              // an inactive parent that keeps the templates from playing
        FXMonger monger;
        GameObject[] stock;
        float giveUpAt;
        bool hooked;

        public static void Log(string text) => Debug.Log("[VolumetricExplosions] " + text);

        void Start()
        {
            Settings.Load();
            if (!Settings.Enabled) { Log("switched off in settings.cfg"); enabled = false; return; }
            giveUpAt = Time.realtimeSinceStartup + 20f;
        }

        void Update()
        {
            if (monger != null) return;
            monger = FindObjectOfType<FXMonger>();
            if (monger == null || monger.explosions == null || monger.explosions.Length == 0)
            {
                monger = null;
                if (Time.realtimeSinceStartup > giveUpAt) { Log("the game's explosion effects were not found; leaving them alone"); enabled = false; }
                return;
            }
            try
            {
                Install();
            }
            catch (Exception ex)
            {
                // Whatever goes wrong here, the player keeps the stock explosions.
                Debug.LogError("[VolumetricExplosions] could not install: " + ex);
                if (stock != null) monger.explosions = stock;
                enabled = false;
            }
        }

        void Install()
        {
            stock = monger.explosions;
            if (!Assets.Load()) { enabled = false; return; }
            if (ours == null || ours.Length != stock.Length || ours[0] == null)
            {
                if (shelf == null)
                {
                    var holder = new GameObject("VolumetricExplosions");
                    holder.SetActive(false);
                    DontDestroyOnLoad(holder);
                    shelf = holder.transform;
                }
                ours = new GameObject[stock.Length];
                for (int n = 0; n < stock.Length; n++)
                {
                    // Strength 0 is the weakest slot ("thud"), 1 the strongest.
                    float strength = stock.Length > 1 ? n / (float)(stock.Length - 1) : 1f;
                    ours[n] = Recipe.Build(shelf, strength, stock[n]);
                }
            }
            Recipe.Layer = stock[stock.Length - 1] != null ? stock[stock.Length - 1].layer : 0;
            Records.Hook();
            hooked = true;
            gameObject.AddComponent<Air>();
            monger.explosions = (GameObject[])ours.Clone();
            Log("v" + Version + " ready (quality " + Settings.Quality + ", up to " + Settings.MaxParticles + " particles, " + Air.Threads + " threads)");
        }

        void OnDestroy()
        {
            // Hand the game its own effects back when the flight scene goes away.
            if (monger != null && stock != null) monger.explosions = stock;
            if (hooked) Records.Unhook();
        }
    }

    /// <summary>
    /// What the player can change in GameData/VolumetricExplosions/PluginData/settings.cfg.
    ///
    /// It is in PluginData because the game reads every .cfg file anywhere else under GameData as one of its own configs, and
    /// after any change to one of those the game's next start is a slow one (ModuleManager works every config out again, and
    /// the game measures every part's drag again). The game does not look into a PluginData folder. Until version 0.3.2 the
    /// file was beside the library: one found there is moved, once, with whatever the player had set in it.
    /// </summary>
    public static class Settings
    {
        public static bool Enabled = true, Light = true, Debris = true, Shockwave = true, Shake = true, Scorch = true, Push = true, Collide = true, Volume = true, Half = true, Native = true, Log = false;
        public static bool SoundTravels = true;        // the bang is heard when its sound has had time to get to the camera, not on the instant
        public static bool Shadows = true;             // the smoke throws its shadow on what is under it, in sunlight (see tools/shaderpack/shadow.glsl)
        public static bool Built = false;              // use the shaders built in the Unity editor even where the hand-packed OpenGL ones would do (for comparing the two)
        public static bool Predict = true;             // the way things moving through smoke are about to go is looked ahead along, and the smoke in their way made finer before they get there (see Wakes.cs)
        public static bool Wakes = true;               // what goes through smoke leaves a tunnel in it, drawn at the pixel, that fills in again (see Wakes.cs, and carve in volume.glsl)
        public static bool Vortices = true;            // the air behind what goes through smoke is left turning, and carries the smoke round: a vortex street behind blunt things, two tubes behind wings
        public static float Quality = 1f, Smoke = 1f, Size = 1f, Wind = 1f, Detail = 0.6f, Thick = 1.6f, Bend = 1f;
        public static int Breeze = 1;                  // when there is a wind for smoke to drift on: 0 never (still air always, whatever the weather), 1 as it comes (still nine times in ten, see Site.Wind; a weather mod's wind where there is one), 2 always some
        public static float Thin = 2f;                 // smoke too thin to see is not drawn: up to this many steps of an eight-bit picture's worth along any one line of sight (0: all of it is drawn)
        public static int MaxParticles = 30000, Threads = 0;
        /// <summary>For measuring how steady the picture is (development build; never read from the file): move the samples along each ray by this part of a step, make the grid's cells this much bigger, move the grid by this part of a cell, draw only part of the picture (see the shader), and (1) leave the smoke where each grid has it instead of carrying it on between grids.</summary>
        public static float TestLattice = 0f, TestCell = 1f, TestShift = 0f, TestView = 0f, TestSmoke = 0f;
        public static float TestGround;                // (for seeing what the sounding of the ground is worth: 1 takes the ground as one tilted plane, as it used to be taken)
        public static float TestWind = -1f, TestWindSpeed = 4f;      // (for testing one thing against another in the same wind: which way it blows, in degrees round from east through north, and how hard, in metres a second ten metres up. Less than nought: the wind of the place and time, as usual)
        public static float TestTurns;                 // (for seeing what it is worth that each bit of smoke goes through its billows' rounds at its own pace: 1 puts all of it on the same round, as it used to be)
        public static float TestGrids, TestFreeze;     // (for finding what a fire costs: seconds more to wait between one grid and the next; 1 leaves the particles where they are, 2 stops the billows taking turns as well, 3 stops the site's clock altogether)
        public static float TestYoung;                 // (for setting one against the other: 1 lays an engine's smoke straight into the air it leaves it in, as before 0.5.0, whatever ship it says it is on; 2 does that and leaves out the pads' trenches as well)
        public static float TestMover, TestMoverSize = 0.5f;      // (development build, for seeing wakes: a ball this wide, a radius in metres, sent at this speed, metres a second, east through the middle of the newest cloud, again and again, as another mod's piece of wreckage would be. Nought: none)
        public static float TestBurn, TestBurnKind = 2f;  // (development build, for seeing a fire on a ship: a fire this wide, a radius in metres, burning on the side of the craft being flown, told as an engine's jet (kind 1) or as something burning (2). Nought: none)
        public static float TestFoot;                  // (for setting one against the other: 1 lifts a flame lying on the ground clear of it, as smoke is, as until 2026-10-10; only where the grids are made by the mod's own code, so with Native off)
        public static float TestFire;                  // (for setting one against the other: 1 draws a fire in the open as it was until 2026-10-10, a glow following the billows, instead of in sharp tongues lit by their heat)
        public static float TestSoot;                  // (for setting one against the other: 1 makes the smoke of a fire as it was until 2026-10-09: a fireball's soot shown dot by dot, burning things' smoke thrown as an engine's)
        public static float TestLayers;                // (for seeing what putting the patches' smoke in depth order pixel by pixel is worth, see Layers: 1 lays them over one another in the order the game used to draw them in; 2 has each patch put on the screen by its own box, as before 0.5.0; 3 shows each patch in a colour of its own, in depth order, and 4 the same in the old order; 5 puts one slot to a row of the atlas; 6 shows how far behind the nearest patch's smoke the next lies (red, 0 to 10 m) and how widely each is spread along the ray (green the nearest, blue the next, 0 to 5 m); 7 lays them over one another in depth order, mixed where close, as until 2026-10-10)

        public static string Folder => KSPUtil.ApplicationRootPath + "GameData/VolumetricExplosions/";

        public static void Load()
        {
            try
            {
                string path = Folder + "PluginData/settings.cfg", was = Folder + "settings.cfg";
                if (File.Exists(was))
                {
                    try
                    {
                        Directory.CreateDirectory(Folder + "PluginData");
                        File.Copy(was, path, true);
                        File.Delete(was);
                        Addon.Log("settings.cfg is now kept in the PluginData folder, where changing it does not slow the game's next start: yours was moved there");
                    }
                    catch (Exception ex) { path = was; Addon.Log("settings.cfg could not be moved into PluginData (" + ex.Message + "): read where it is"); }
                }
                if (!File.Exists(path)) return;
                ConfigNode file = ConfigNode.Load(path);
                ConfigNode node = file != null ? file.GetNode("VOLUMETRIC_EXPLOSIONS") : null;
                if (node == null) return;
                node.TryGetValue("enabled", ref Enabled);
                node.TryGetValue("light", ref Light);
                node.TryGetValue("debris", ref Debris);
                node.TryGetValue("shockwave", ref Shockwave);
                node.TryGetValue("scorch", ref Scorch);
                node.TryGetValue("shake", ref Shake);
                node.TryGetValue("push", ref Push);
                node.TryGetValue("collide", ref Collide);
                node.TryGetValue("sound_travels", ref SoundTravels);
                node.TryGetValue("shadows", ref Shadows);
                node.TryGetValue("predict", ref Predict);
                node.TryGetValue("wakes", ref Wakes);
                node.TryGetValue("vortices", ref Vortices);
                node.TryGetValue("log", ref Log);
                node.TryGetValue("volume", ref Volume);
                node.TryGetValue("half", ref Half);
                node.TryGetValue("native", ref Native);
                node.TryGetValue("built", ref Built);
                node.TryGetValue("detail", ref Detail);
                node.TryGetValue("thick", ref Thick);
                node.TryGetValue("thin", ref Thin);
                node.TryGetValue("bend", ref Bend);
                node.TryGetValue("quality", ref Quality);
                node.TryGetValue("smoke", ref Smoke);
                node.TryGetValue("size", ref Size);
                node.TryGetValue("wind", ref Wind);
                string breeze = (node.GetValue("breeze") ?? "").Trim().ToLowerInvariant();
                Breeze = breeze.StartsWith("still") || breeze == "never" || breeze == "none" ? 0 : breeze.StartsWith("always") ? 2 : 1;
                node.TryGetValue("max_particles", ref MaxParticles);
                node.TryGetValue("threads", ref Threads);
                Quality = Mathf.Clamp(Quality, 0.2f, 3f);
                Smoke = Mathf.Clamp(Smoke, 0f, 4f);
                Size = Mathf.Clamp(Size, 0.3f, 3f);
                Wind = Mathf.Clamp(Wind, 0f, 4f);
                Detail = Mathf.Clamp(Detail, 0f, 1f);
                Thick = Mathf.Clamp(Thick, 0.3f, 4f);
                Thin = Mathf.Clamp(Thin, 0f, 8f);
                Bend = Mathf.Clamp(Bend, 0f, 2f);
                MaxParticles = Mathf.Clamp(MaxParticles, 2000, 120000);
                Threads = Mathf.Clamp(Threads, 0, 32);
            }
            catch (Exception ex) { Addon.Log("could not read settings.cfg (" + ex.Message + "); using the defaults"); }
        }
    }

    /// <summary>The textures and the materials made from them.</summary>
    public static class Assets
    {
        public static Material Cloud, Glow, GlowBehind, Spark, Ring, Shard, Scorch, Embers;
        public static Shader Volume;                   // the smoke-as-a-volume shader; null where the game cannot use it
        public static Shader Mark;                     // the shader that throws a burn mark onto whatever is there; null where the game cannot use it
        public static Shader Shock;                    // the shader that bends the picture behind a blast's shock front; null where the game cannot use it
        public static Shader Enlarge;                  // the shader that puts smoke drawn at half size on the screen; null where the game cannot use it
        public static Shader Shadow;                   // the shader that throws the smoke's shadow on what is under it; null where the game cannot use it
        public static Shader Layers;                   // the shader that puts every patch's smoke on the screen together, in depth order (see Layers); null where the game cannot use it
        public static Mesh Sheet;                      // one triangle that covers the whole screen
        public static Texture3D Detail;
        public static Mesh Cube;
        // Four numbers of sixteen bits to a cell, read smoothly between cells (for where the smoke "was"): plain numbers where
        // the graphics card will read those smoothly, as every desktop card should; otherwise half-precision ones.
        public static UnityEngine.Experimental.Rendering.GraphicsFormat Sixteen = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_UNorm;
        public static bool SixteenAsHalves;
        static bool volumeTried, markTried, shockTried, enlargeTried, shadowTried, layersTried;
        public const int Tiles = 4;                    // the particle sheet is 4 by 4
        static int builtForLimit = -1;

        public static bool Load()
        {
            // The game's texture quality setting can change between flights; the textures allow for it (see Texture).
            LoadVolume();
            LoadMark();
            LoadShadow();
            LoadShock();
            LoadEnlarge();
            LoadLayers();
            if (Cloud != null && builtForLimit == QualitySettings.masterTextureLimit) return true;
            // "Premultiplied" is what lets one particle both glow like fire and hide what is behind it like smoke.
            Shader premultiplied = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
            Shader additive = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("KSP/Particles/Additive");
            Shader blended = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("KSP/Particles/Alpha Blended");
            if (premultiplied == null || additive == null || blended == null)
            {
                Addon.Log("this build of the game lacks a particle shader the mod needs; keeping the stock explosions");
                return false;
            }
            Texture2D puff = Texture("puff.png"), glow = Texture("glow.png"), spark = Texture("spark.png"), ring = Texture("ring.png"), shard = Texture("shard.png"), scorch = Texture("scorch.png"), embers = Texture("embers.png");
            if (puff == null || glow == null || spark == null || ring == null || shard == null || scorch == null || embers == null) return false;
            // The second number softens a particle where it meets the ground or a ship instead of ending in a hard line.
            Cloud = Make(premultiplied, puff, false, 0.45f);
            Glow = Make(additive, glow, true, 0.25f);
            Spark = Make(additive, spark, true, 0f);
            Ring = Make(additive, ring, true, 0.6f);
            Shard = Make(blended, shard, true, 0f);
            // Burn marks are drawn straight after the solid ground and before anything see-through, so that smoke is never behind them.
            Scorch = Make(blended, scorch, true, 0f);
            Scorch.renderQueue = 2460;
            Embers = Make(additive, embers, true, 0f);
            Embers.renderQueue = 2461;
            // The order things are drawn in: the glow of a fire, then the volume of smoke over it, then what is left as sprites, then flashes and sparks.
            GlowBehind = new Material(Glow) { renderQueue = 2990 };
            Cloud.renderQueue = 3001;
            Shard.renderQueue = 3001;
            Glow.renderQueue = 3002;
            Spark.renderQueue = 3002;
            Ring.renderQueue = 3002;
            builtForLimit = QualitySettings.masterTextureLimit;
            return true;
        }

        /// <summary>
        /// The shader that draws smoke as a true volume. It is OpenGL text in a bundle made by
        /// tools/shaderpack, so it works where the game runs on OpenGL (the Mac and Linux versions, and
        /// Windows started with -force-glcore). Elsewhere the particles are drawn as soft sprites instead.
        /// </summary>
        static void LoadVolume()
        {
            if (volumeTried) return;
            volumeTried = true;
            if (!Settings.Volume) return;
            if (!SystemInfo.supports3DTextures) { Addon.Log("smoke is drawn as sprites: this graphics card has no 3D textures"); return; }
            if (UseBuilt && BuiltPath() == null)
            {
                Addon.Log("smoke is drawn as sprites: the volume shader that ships with the mod is for OpenGL and this is " + SystemInfo.graphicsDeviceType + ", and there is no PluginData/shaders-" + BuiltFor + ".bundle");
                return;
            }
            try
            {
                if (UseBuilt) Volume = BuiltShader("Volume");
                else
                {
                    string path = Settings.Folder + "PluginData/volume.bundle";
                    if (!File.Exists(path)) { Addon.Log("smoke is drawn as sprites: " + path + " is missing"); return; }
                    AssetBundle bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null) { Addon.Log("smoke is drawn as sprites: the shader bundle did not load"); return; }
                    Shader[] found = bundle.LoadAllAssets<Shader>();
                    bundle.Unload(false);
                    foreach (Shader shader in found) if (shader != null && shader.isSupported) Volume = shader;
                }
                if (Volume == null) { Addon.Log("smoke is drawn as sprites: the volume shader is not supported here"); return; }
                if (!SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_UNorm, UnityEngine.Experimental.Rendering.FormatUsage.Linear))
                {
                    Sixteen = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                    SixteenAsHalves = true;
                }
                Detail = LoadDetail();
                if (Detail == null) { Volume = null; Addon.Log("smoke is drawn as sprites: PluginData/detail.bin is missing"); return; }
                NeedCube();
                Addon.Log("smoke is drawn as a volume" + (UseBuilt ? " (by the shaders built for " + SystemInfo.graphicsDeviceType + ", from shaders-" + BuiltFor + ".bundle)" : ""));
            }
            catch (Exception ex)
            {
                Volume = null;
                Addon.Log("smoke is drawn as sprites: " + ex.Message);
            }
        }

        /// <summary>
        /// The mod's four shaders are OpenGL text, packed by hand. Where the game does not run on OpenGL (Direct3D, on
        /// Windows) they come instead from a bundle built for that kind of computer in the Unity editor, from the same
        /// text turned into HLSL (see tools/unityshaders). "built = true" in settings.cfg uses that bundle on OpenGL
        /// too, which is how the two are compared.
        /// </summary>
        static bool UseBuilt => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore || Settings.Built;

        static string BuiltFor => Application.platform == RuntimePlatform.WindowsPlayer ? "windows" : Application.platform == RuntimePlatform.LinuxPlayer ? "linux" : "mac";

        static string BuiltPath()
        {
            string path = Settings.Folder + "PluginData/shaders-" + BuiltFor + ".bundle";
            return File.Exists(path) ? path : null;
        }

        static Dictionary<string, Shader> built;

        /// <summary>One of the shaders from that bundle, by the last part of its name ("Volume", "Enlarge", "Mark", "Shock"). Null if it is not there or cannot be used here.</summary>
        static Shader BuiltShader(string name)
        {
            if (built == null)
            {
                built = new Dictionary<string, Shader>();
                string path = BuiltPath();
                AssetBundle bundle = path != null ? AssetBundle.LoadFromFile(path) : null;
                if (bundle != null)
                {
                    foreach (Shader shader in bundle.LoadAllAssets<Shader>())
                    {
                        if (shader == null) continue;
                        if (!shader.isSupported) { Addon.Log("the built shader " + shader.name + " is not supported by this graphics card"); continue; }
                        built[shader.name.Substring(shader.name.LastIndexOf('/') + 1)] = shader;
                    }
                    bundle.Unload(false);
                }
            }
            return built.TryGetValue(name, out Shader one) ? one : null;
        }

        /// <summary>One of the mod's smaller shaders: OpenGL text in a bundle of its own (see tools/shaderpack), or the same from the built bundle (see UseBuilt). Null if it cannot be had.</summary>
        static Shader OpenGLShader(string file)
        {
            if (UseBuilt) return BuiltShader(char.ToUpperInvariant(file[0]) + file.Substring(1, file.IndexOf('.') - 1));
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore) return null;
            string path = Settings.Folder + "PluginData/" + file;
            if (!File.Exists(path)) return null;
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) return null;
            Shader[] found = bundle.LoadAllAssets<Shader>();
            bundle.Unload(false);
            foreach (Shader shader in found) if (shader != null && shader.isSupported) return shader;
            return null;
        }

        static void NeedCube()
        {
            if (Cube != null) return;
            GameObject shape = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Cube = shape.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(shape);
        }

        /// <summary>
        /// The shader for the shock front of a blast (see tools/shaderpack/shock.glsl). Without it a blast shows
        /// the white ring it used to.
        /// </summary>
        static void LoadShock()
        {
            if (shockTried) return;
            shockTried = true;
            try
            {
                Shock = OpenGLShader("shock.bundle");
                if (Shock == null) return;
                Sheet = new Mesh { name = "VolumetricExplosions sheet" };
                Sheet.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
                Sheet.triangles = new[] { 0, 1, 2 };
            }
            catch (Exception ex)
            {
                Shock = null;
                Addon.Log("shock fronts are drawn as rings: " + ex.Message);
            }
        }

        /// <summary>
        /// The shader that puts smoke drawn at half size on the screen (see tools/shaderpack/enlarge.glsl). Without
        /// it the smoke is drawn at the screen's full size, which costs several times as much.
        /// </summary>
        static void LoadEnlarge()
        {
            if (enlargeTried) return;
            enlargeTried = true;
            try { Enlarge = OpenGLShader("enlarge.bundle"); }
            catch (Exception ex)
            {
                Enlarge = null;
                Addon.Log("smoke is drawn at full size: " + ex.Message);
            }
        }

        /// <summary>
        /// The shader that puts every patch's smoke on the screen together, each part of it in front of or behind the others as
        /// it really lies (see tools/shaderpack/layers.glsl and Layers). Without it each patch is put on the screen by its own
        /// box, one over another, as before 0.5.0.
        /// </summary>
        static void LoadLayers()
        {
            if (layersTried) return;
            layersTried = true;
            try { Layers = Volume != null ? OpenGLShader("layers.bundle") : null; }
            catch (Exception ex)
            {
                Layers = null;
                Addon.Log("each patch of smoke is put on the screen by itself: " + ex.Message);
            }
        }

        /// <summary>
        /// The shader that throws burn marks onto the ground (see tools/shaderpack/mark.glsl). Without it the
        /// marks are flat sheets dropped onto the ground from above.
        /// </summary>
        static void LoadMark()
        {
            if (markTried) return;
            markTried = true;
            try
            {
                Mark = OpenGLShader("mark.bundle");
                if (Mark != null) NeedCube();
            }
            catch (Exception ex)
            {
                Mark = null;
                Addon.Log("burn marks are drawn as flat sheets: " + ex.Message);
            }
        }

        /// <summary>
        /// The shader that throws the smoke's shadow on what is under it (see tools/shaderpack/shadow.glsl). Without it
        /// the smoke throws none, as before 0.5.0.
        /// </summary>
        static void LoadShadow()
        {
            if (shadowTried) return;
            shadowTried = true;
            try
            {
                Shadow = OpenGLShader("shadow.bundle");
                if (Shadow != null) NeedCube();
            }
            catch (Exception ex)
            {
                Shadow = null;
                Addon.Log("the smoke throws no shadow: " + ex.Message);
            }
        }

        /// <summary>The cube of cellular noise that gathers the smoke into puffs (made by tools/fxbake): four bytes a cell, the same number of cells each way.</summary>
        static Texture3D LoadDetail()
        {
            string path = Settings.Folder + "PluginData/detail.bin";
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            int n = (int)Math.Round(Math.Pow(bytes.Length / 4, 1.0 / 3.0));
            if (n < 16 || bytes.Length != n * n * n * 4) return null;
            var cells = new Color32[n * n * n];
            for (int k = 0; k < cells.Length; k++) cells[k] = new Color32(bytes[k * 4], bytes[k * 4 + 1], bytes[k * 4 + 2], bytes[k * 4 + 3]);
            var texture = new Texture3D(n, n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, name = "VolumetricExplosions/detail" };
            texture.SetPixels32(cells);
            texture.Apply(true);
            DetailCells = n;
            return texture;
        }

        public static int DetailCells = 96;

        static Material Make(Shader shader, Texture2D texture, bool tinted, float soften)
        {
            var material = new Material(shader) { mainTexture = texture };
            // The legacy shaders multiply by a tint of mid grey and then double the result.
            if (tinted && material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (soften > 0f && material.HasProperty("_InvFade"))
            {
                material.EnableKeyword("SOFTPARTICLES_ON");
                material.SetFloat("_InvFade", soften);
            }
            return material;
        }

        static Texture2D Texture(string name)
        {
            // Kept in PluginData so the game does not load (and compress) them itself.
            string path = Settings.Folder + "PluginData/" + name;
            try
            {
                var raw = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                if (!raw.LoadImage(File.ReadAllBytes(path)))
                {
                    Addon.Log("could not read " + path);
                    return null;
                }
                // The game's "texture quality" setting throws away the top levels of every mip-mapped texture,
                // which would halve these or worse. Stored enlarged by the same factor, what is left is the original.
                int scale = 1 << Mathf.Clamp(QualitySettings.masterTextureLimit, 0, 3);
                while (scale > 1 && Mathf.Max(raw.width, raw.height) * scale > Mathf.Min(8192, SystemInfo.maxTextureSize)) scale >>= 1;
                int width = raw.width, height = raw.height;
                Color32[] pixels = raw.GetPixels32();
                UnityEngine.Object.Destroy(raw);
                if (scale > 1)
                {
                    var larger = new Color32[width * scale * height * scale];
                    for (int y = 0; y < height * scale; y++)
                    {
                        int from = (y / scale) * width, to = y * width * scale;
                        for (int x = 0; x < width * scale; x++) larger[to + x] = pixels[from + x / scale];
                    }
                    pixels = larger;
                    width *= scale;
                    height *= scale;
                }
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, true);
                texture.SetPixels32(pixels);
                texture.Apply(true, true);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 1;
                texture.name = "VolumetricExplosions/" + name;
                return texture;
            }
            catch (Exception ex)
            {
                Addon.Log("missing texture " + path + " (" + ex.Message + "); keeping the stock explosions");
                return null;
            }
        }
    }
}
