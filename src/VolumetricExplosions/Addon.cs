using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

#if !DEV
[assembly: KSPAssembly("VolumetricExplosions", 0, 6)]
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
        public const string Version = "0.12.0";

        static GameObject[] ours;            // built once, reused for every flight
        static Transform shelf;              // an inactive parent that keeps the templates from playing
        FXMonger monger;
        GameObject[] stock;
        float giveUpAt;
        bool hooked;

        public static void Log(string text) => Debug.Log("[VolumetricExplosions] " + text);

        void Start()
        {
            Page.Flush();             // (a change on Keystone's page not yet written: written before the file is read again)
            Settings.Load();
            Page.FromSettings();
            if (!Settings.Enabled) { Log("switched off in settings.cfg"); enabled = false; return; }
            Records.LoadKinds();
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

        // (Since 2026-11-19: each ship's believed speed, for the speed of a part that dies: see Records.Pace.)
        void FixedUpdate()
        {
            if (hooked) Records.Watch();
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
        public static bool Embers = true;          // (since 0.10.0: glowing bits carried up off a fire, see Site.Ember)
        /// <summary>Whether the smoke tells a game that smooths its picture between frames where it is going (see Layers, and tools/shaderpack/motion.glsl). "follow" in the settings file.</summary>
        public static bool Follow = true;
        /// <summary>(Since 2026-10-25.) Whether the flames of a patch with fire burning in the open in it are walked at the screen's own size, by themselves, as a ship's flame is (see Site.FlameRect, Layers.ForCamera); off, at half size with its smoke and enlarged, as until then. "fireswhole" in the settings file.</summary>
        public static bool FiresWhole = true;
        /// <summary>(for trying: how much the smoke must hide before its motion is written, and the least that moving smoke is said to have moved, in widths of the picture: see motion.glsl)</summary>
        public static float FollowFrom = 0.15f, FollowLeast = 0f;
        public static bool SoundTravels = true;        // the bang is heard when its sound has had time to get to the camera, not on the instant
        public static bool Knock = false;              // (since 0.9.0) blasts push craft, and break what they knock harder than it can take (see Site.Knocks): off as installed
        public static bool Raze = true;                // (since 0.12.0) a blast strong enough where a building of the space centre stands brings it down (see Razing)
        public static bool BehindHis = true;           // (since 0.12.0) smoke that lies behind Blackrack's volumetric clouds is dimmed by them (see Layers.HisClouds)
        public static bool Sharp = true;               // (since 0.12.0) with "half" on, smoke that stays where it is is still walked at the screen's full size while the frame rate holds (see Layers.Roomy)
        public static float SharpFps = 50f;            // (since 0.12.0) the frame rate under which it goes back to half size
        public static bool Haze = true;                // (since 0.12.0) far smoke is seen through the air between: paler and bluer the further off (see Layers, _VolAir)
        public static bool Shadows = true;             // the smoke throws its shadow on what is under it, in sunlight (see tools/shaderpack/shadow.glsl)
        public static bool Shaded = true;              // (since 0.8.0) what stands in the sun (the craft, the tower, buildings, the ground) throws its shadow into the smoke (see SunShadow)
        public static bool Built = false;              // use the shaders built in the Unity editor even where the hand-packed OpenGL ones would do (for comparing the two)
        public static bool Predict = true;             // the way things moving through smoke are about to go is looked ahead along, and the smoke in their way made finer before they get there (see Wakes.cs)
        public static bool Wakes = true;               // what goes through smoke leaves a tunnel in it, drawn at the pixel, that fills in again (see Wakes.cs, and carve in volume.glsl)
        public static bool Vortices = true;            // the air behind what goes through smoke is left turning, and carries the smoke round: a vortex street behind blunt things, two tubes behind wings
        public static bool BDArmory = true;            // (since 0.10.0) BD Armory's explosions caught and laid as ours, sized by their explosive (see Warheads); "bdarmory" in the settings file
        public static int BDArmoryMost = 40;           // and at most so many of them a second: past it, BD Armory's own are left to show; "bdarmory_most"
        // (Since 0.10.0) BD Armory's fire and smoke drawn as volumes in this air (the code in Armory/, made as a mod of its own,
        // Volumetric Armory, and never released as one): "armory" in the settings file, on as it comes and nothing without BD Armory;
        // "armory_ours" false for BD Armory's own everywhere, its explosions too (what the key flips); "armory_key" the key that
        // flips it, with the game's modifier key held; then each kind of effect, and whether what is hidden is worked out.
        public static bool Armory = true, ArmoryOurs = true;
        public static KeyCode ArmoryKey = KeyCode.B;
        public static bool ArmoryFires = true, ArmoryLeaks = true, ArmoryMissiles = true, ArmoryRockets = true, ArmoryGuns = true, ArmoryHits = true,
                           ArmoryCountermeasures = true, ArmoryNukes = true, ArmorySplashes = true, ArmoryCut = true;
        /// <summary>Whether BD Armory's explosions are laid as ours just now: "bdarmory", unless the armory's switch is at BD Armory's own.</summary>
        public static bool CatchBDArmory => BDArmory && (!Armory || ArmoryOurs);
        /// <summary>(Since 2026-11-04, for a battle.) The most patches of air at once, explosions' and engines' trails together (see Air.Most): 6 to 15, nine until then. More hold more fires and trails at once, at some 40 MB of memory and a little of the processor each. "patches" in the settings file.</summary>
        public static int Patches = 12;
        /// <summary>(Since 2026-11-04, for a battle.) The most grids begun in one frame, over all the patches: those covering the most of the screen first, the rest the next frame (see Site.MayGrid). Nought: as many as are due, as until then. "gridsaframe" in the settings file.</summary>
        public static int GridsAFrame = 3;
        /// <summary>(for setting one against the other: 1 hands every grid's slow fields to the card where Unity sends them (Direct3D), as until 2026-11-04, not every other grid where that cannot be seen: see Site.SlowFieldsGo)</summary>
        public static float TestSlowFields;
        public static float Quality = 1f, Smoke = 1f, Size = 1f, Wind = 1f, Detail = 0.6f, Thick = 1.6f, Bend = 1f;
        // (since 2026-10-20) A cloud started by a blast at the sea's surface is water thrown up, not smoke: its billows are cut so
        // much as deep as smoke's, and shade one another so much as strongly (see Site.TuneVolume). 1: as smoke, as until then. (A
        // field, so that a development build can try others while it runs: dev_fx_set.)
        public static float SeaBillows = 0.5f;
        public static int Breeze = 1;                  // when there is a wind for smoke to drift on: 0 never (still air always, whatever the weather), 1 as it comes (still nine times in ten, see Site.Wind; a weather mod's wind where there is one), 2 always some
        public static float Thin = 2f;                 // smoke too thin to see is not drawn: up to this many steps of an eight-bit picture's worth along any one line of sight (0: all of it is drawn)
        public static int MaxParticles = 30000, Threads = 0;
        /// <summary>For measuring how steady the picture is (development build; never read from the file): move the samples along each ray by this part of a step, make the grid's cells this much bigger, move the grid by this part of a cell, draw only part of the picture (see the shader), and (1) leave the smoke where each grid has it instead of carrying it on between grids.</summary>
        public static float TestCloudGrids = 0f, TestCloudRepeat = 0f, TestCloudMass = 0f;      // (development build, for tuning clouds made of thermals: how much longer between grids than usual; the billows' repeat as a share of the thermal's radius; how much thicker)
        public static float TestLattice = 0f, TestCell = 1f, TestShift = 0f, TestView = 0f, TestSmoke = 0f;
        /// <summary>(development build, 2026-11-21: 1 writes to the log, each frame, where a ship's strongest nozzle is on its patch's cells, its flame's bits (how many, their heat, age and place, how many were laid) and its grid's flame: for the flame's glow that swelled and faded)</summary>
        public static float TestJetPhase = 0f;
        /// <summary>(for trying, since 2026-11-21: 1 lays an engine's flame as until then, its gusts slow as well as quick and its bits fewer as the scene fills; see Site.Flame)</summary>
        public static float TestJetSteady = 0f;
        /// <summary>(for trying: more than nought shows, instead of the smoke, how many times each ray went round its loop, over this many: red all, green in smoke, blue leaps over clear air)</summary>
        public static float TestRounds = 0f;
        /// <summary>(for trying: 1 lights the ground and the craft as until 0.7.1: as strong by night as by day, the quick flicker alone, left to the game to draw cheaply; 2 as until 2026-11-10: a blast's flash takes a fire's farther-reaching light at once, and under cloud by day a fire's light is as strong as by night)</summary>
        public static float TestFireLight = 0f;
        // An engine's flame (see Site.Flame, the mover at JetGas, and TuneJet): its gas leaves at so many of the flame's lengths a
        // second; the first so much of the flame is the stretch it is not slowed in; how uneven it comes out (0: as even as
        // before 0.6.4); how far aside the whole jet wavers, against how far it goes on; and its drawn pattern runs down it
        // at so many of its own sizes a second at the nozzle, and half as fast so much of the flame's length down. (Fields
        // here so that a development build can try others while it runs: dev_fx_set.)
        public static float JetPace = 9f, JetCore = 0.25f, JetUneven = 1f, JetWavers = 0.05f, JetRuns = 30f, JetRunsOn = 0.33f;
        // (and the line its pattern is drawn along comes round to the nozzle's so many of the gas's own times late: the time the gas would take over the flame's length at the speed it leaves at)
        public static float JetLags = 1.2f;
        // (Since 2026-11-20.) And the far part of its pattern boils, its lumps made afresh where they are: so many times a second, from
        // so much of the flame's length down (begun), fully by so much (full); JetBoils nought: carried down it and no more, as until
        // then. See jetPattern in the shader.
        public static float JetBoils = 8f, JetBoilFrom = 0.15f, JetBoilFull = 0.6f;
        // A re-entry glow's head (see Site.Plasma): its shock layer's heat against the glow's own, and how thick its layer and its
        // sheath are laid, against the glow's. (Fields so that a development build can try others while it runs: dev_fx_set.)
        public static float PlasmaHead = 1.2f, PlasmaLayer = 0.75f, PlasmaSheath = 0.25f;
        // An engine's young smoke (see Site.YoungSmoke): it cools into sight at so much of its heat in the time the flame's gas
        // takes over the flame's reach, each puff at between so many times that and so many. (Fields so that a development
        // build can try others while it runs: dev_fx_set.)
        public static float YoungCools = 0.67f, YoungCoolLo = 0.6f, YoungCoolHi = 1.4f;
        // An engine's trail (see Site.Lingers): it stays in the air so many times as long as its owner says its smoke lasts,
        // thinning as it spreads (nought: a few seconds, less the faster it was laid, as until 2026-10-25); and its old puffs that
        // stand closer together than so much of their width are made one (see Site.ThinOut; nought: never). (Fields so that a
        // development build can try others while it runs: dev_fx_set.)
        public static float TrailLingers = 20f, TrailThins = 0.3f;
        // (and low down, in the air the ground stirs, it spreads so many times faster again than above it: see Vents. Nought: no faster.)
        public static float TrailSpreads = 3f;
        // (and where the air holds as many patches as it may, two old patches of a trail are made one, not the oldest let go: see Air.Merge)
        public static bool TrailMerges = true;
        // A fire in the open, as it is drawn (see Site.TuneFire, and firePattern and the flame in tools/shaderpack/volume.glsl): how
        // much of a third, finer size of lump is in its flames' pattern; how far that pattern is drawn out upward; half the width of
        // the step from no flame to flame where it is torn into tongues; how much the billows cut it into tongues; how bright its
        // folded flame is, and how dim the creases between its billows against their tops; how steeply its light on the smoke round
        // it falls off (1 as one over the square of the distance, 2 the fourth power), and how strong that light is; how hot its
        // smoke is when it begins to shine of itself, and how much; how fast its pattern rises through its gas, metres a second;
        // the size of its tongues against the fire's; and how high a fire on the ground burns, against the height its width gives
        // its flames (see Stands: its gas cools by so much more slowly on the way up), and how hot its gas is as it leaves the
        // ground, against what it was (the grid keeps heat up to 1.2: gas that comes off hotter stands at that, the brightest, for
        // its first stretch, as a real fire's flame burns at an even heat low down before it cools at its tips); how hot its flame
        // is drawn, against its gas's heat; and how much flame it takes to burn rather than be torn away. (Fields so that a
        // development build can try others while it runs: dev_fx_set. Until 2026-10-26: 0, 0.62, 0.05, 0.4, 0.45, 0.25, 1, 8,
        // 0.28, 0.45, 1.8, 1, 1, 1, 1, 0.35.)
        // (Since 2026-10-26, for "still the fire doesn't really look like fire", the owner, 2026-10-25: held still and drawn in its
        // parts, a fire 120 m off was a few small flames at the ground, and the great orange body round them its smoke, lit orange by
        // its light far out into the cloud and shining of itself wherever the gas was warm; its gas cooled from the moment it left the
        // ground, so that only its lowest two or three metres burned bright, where a fire of that width stands eight. Its gas now
        // comes off hotter (the grid holds it at its brightest for its first stretch, and it climbs faster, drawing its flames up
        // into tongues) and cools more slowly, and is drawn as if a little cooler, so that the foot of a fire is yellow and not a
        // white dome; its smoke shines only where it is hottest, and its light on the smoke falls off more steeply; its pattern's
        // lumps taller, finer at the edge and less those of the smoke's billows. Filmed from 60, 120 and 220 m by day, and at dusk
        // and at night: tall tongues of flame in black smoke, at no cost that could be measured on the PC (Direct3D), its flames
        // walked whole too. tools/dev/windows/firelook.py.)
        public static float FireFine = 0.25f, FireDrawnUp = 0.42f, FireEdge = 0.045f, FireBillows = 0.25f, FireBright = 0.55f, FireCreases = 0.4f,
            FireLightFalls = 1.7f, FireLight = 8f, FireShineFrom = 0.40f, FireShine = 0.15f, FireRises = 1.8f, FireTongues = 1f, FireTall = 1.5f, FireHeat = 1.6f, FireShown = 0.8f, FireCut = 0.35f;
        // (Since 2026-11-16 brighter, its creases less dark and its flame drawn hotter: FireBright 0.45, FireCreases 0.25 and FireShown 0.7 until
        // then. Set beside NASA's photographs of a jet-fuel fireball by daylight (EC84-31808, -31809: yellow-white at its heart, orange at its
        // edges, opaque, black smoke rolling round its foot), a crash's fireball was orange and grey in equal parts. Filmed at 0.65, 0.5 and 0.9
        // too (`fire1`): at 0.9 the young fireball was white.)
        // The fires' light on the smoke (see Site.SmokeLamps; since 2026-11-03): each of its three slots keeps to its fire until that
        // is out or a fire with no slot is so many times as strong (without its flicker); a slot going over to another fire fades
        // out over so many seconds and in again there. The light of a fire that puffs keeps so much of its puff's swing on the smoke
        // (1: all of it, as until then: 0.6 to 1.1 of its strength; 0.6: 0.8 to 1.1), the light it throws on the ground all of it.
        // And the colour of the fires' light on the smoke goes over to a new strongest fire's in about three times so many seconds
        // (nought: at once, as until then). (Fields so that a development build can try others while it runs: dev_fx_set.)
        public static float FireSlotOutdone = 1.6f, FireSlotFade = 0.3f, FireSmokePuff = 0.6f, FireHueEases = 0.1f;
        public static float TestGround;                // (for seeing what the sounding of the ground is worth: 1 takes the ground as one tilted plane, as it used to be taken)
        public static float TestWind = -1f, TestWindSpeed = 4f;      // (for testing one thing against another in the same wind: which way it blows, in degrees round from east through north, and how hard, in metres a second ten metres up. Less than nought: the wind of the place and time, as usual)
        public static float TestTurns;                 // (for seeing what it is worth that each bit of smoke goes through its billows' rounds at its own pace: 1 puts all of it on the same round, as it used to be)
        public static float TestGrids, TestFreeze;     // (for finding what a fire costs: seconds more to wait between one grid and the next; 1 leaves the particles where they are, 2 stops the billows taking turns as well, 3 stops the site's clock altogether)
        public static float TestGridFloor;             // (for comparing: 1 gives the nearest patch with fire in the open a grid at most every 33 ms, as the others and as until 2026-11-09, not one every frame (Site.Relight))
        public static float TestTrace;                 // (development build: 1 logs each grid of the nearest patch with fire as it is shown: see Site.ShowVolume)
        public static float TestGridThreads;           // (for comparing: 1 has the library make each grid on one thread, as until 2026-11-09; 0 on as many as the processor allows; 2 to 8 lay the particles by so many (Native.Threads))
        public static float TestJoin;                  // (for comparing: 1 begins a trail's next patch a quarter of a second ahead of its nozzle, as before 0.8.0, not where the last patch left off (Site.Vents))
        public static float TestRing;                  // (for comparing: 1 leaves out the ring of dust a front lifts off bare ground (Site.RingDust), and pieces' puffs where they land and their longer burning (Site.Strike), as before 0.8.0)
        public static float TestThin;                  // (for comparing: 1 lays smoke thinner than a pixel and a half as it is, unwidened (see Site.Thinnest), as before 0.8.0)
        public static float TestYoung;                 // (for setting one against the other: 1 lays an engine's smoke straight into the air it leaves it in, as before 0.5.0, whatever ship it says it is on; 2 does that and leaves out the pads' trenches as well)
        public static float TestMover, TestMoverSize = 0.5f;      // (development build, for seeing wakes: a ball this wide, a radius in metres, sent at this speed, metres a second, east through the middle of the newest cloud, again and again, as another mod's piece of wreckage would be. Nought: none)
        public static float TestBurn, TestBurnKind = 2f;  // (development build, for seeing a fire on a ship: a fire this wide, a radius in metres, burning on the side of the craft being flown, told as an engine's jet (kind 1) or as something burning (2). Nought: none)
        public static float TestFoot;                  // (for setting one against the other: 1 lifts a flame lying on the ground clear of it, as smoke is, as until 2026-10-10; only where the grids are made by the mod's own code, so with Native off)
        public static float TestFire;                  // (for setting one against the other: 1 draws a fire in the open as it was until 2026-10-10, a glow following the billows, instead of in sharp tongues lit by their heat; 2 as it was until 2026-10-18: its thick flame burnt whole, its light through any smoke, its hot smoke unlit by its heat, the daylight taken off the smoke round it, and a fuel blast's dust as a dry crash's)
        public static float TestDusk;                  // (for setting one against the other: 1 lights a fire in the open as on the morning of 2026-10-18: its light stopped by a tenth of the smoke on its way, four times as strong, its smoke's glow up to the flame's own heat)
        public static float TestRise;                  // (for setting one against the other: 1 lets a fuel fireball rise at half the root of its radius and its rind at 0.3 of it, as until 2026-10-18, lying on the ground as a dome)
        public static float TestPlasma;                // (for setting one against the other: a re-entry glow is laid as until 2026-10-21 again since 2026-11-20, its shock layer no denser nor hotter than the sheath behind it, the sheath spread a body's width round the craft, the wake as long as the craft's own reckoning, and all of it soft-edged and torn as an engine's flame, and its colours go with how strong it is, pink-violet when faint and gold when strong; 1: all of it gold and as hot as the strongest, as until 2026-10-21; 2: the thin bright head of 2026-10-21 to 2026-11-20, see Site.Plasma)
        public static float TestGlare;                 // (for setting one against the other: 1 draws the glare behind every fire at its full strength by day, as until 2026-10-18)
        public static float TestFlash;                 // (for setting one against the other: 1 draws a blast's flash over all smoke, as until 0.7.0, however much of it lies between
                                                       // the camera and the flash; 2 shows it dimmed but tells the log, once a second, how much smoke each flash was dimmed by; 3 leaves the picture's smoothing its
                                                       // memory through a nuclear flash, as until 2026-11-09: see Air.Forget)
        public static float TestSmokeBlend;            // (for setting one against the other: 1 shows the smoke from each grid as it comes, as until 2026-11-16; 2 the smoke gone over from grid to grid but the billows' places from each as it comes: see Site.FireWas)
        public static float TryF = -1f;                // (trying: how keenly the smoke's picture is enlarged from half size, 0 to 0.7; below nought, as set in Layers.Keen)
        public static float TryG = -1f, TryH = -1f, TryI = -1f, TryJ = -1f, TryK = -1f, TryN = -1f;     // (trying, the smoke's surface: G how far it is drawn with one (0: not, as until 2026-11-16), H the least smoke there is any of, I how deep the billows cut, J over how much it comes on, K how thick it is inside, N how much smoke round about it takes; below nought, as set in Site.TuneVolume)
        public static float TryV = -1f;                // (trying: how strong the light of a rocket's flame on its pad's cloud is; below nought, as set in Site.FlameLights)
        public static float TryQ = -1f, TryR = -1f;     // (trying: Q how far the light of a rocket's flame on its pad's cloud reaches, as so much of the flame's length; R how high over the grate it is, as so much of the flame out in the open over it; below nought, as set in Site.FlameReach and FlameFoot)
        public static float TestPadLamp;               // (for setting one against the other: 1 puts the light of a rocket's flame on its pad's cloud as until 2026-11-19, four tenths of the way down the flame, down in the trench: see Site.Ends)
        public static float TestLampLine;              // (for setting one against the other: 1 lights the smoke from one point of a rocket's flame over its pad, at its foot, as until 2026-11-20, not from all along it: see Site.Ends, FlameLine, and lampFrom in volume.glsl)
        public static float TestPadShine;              // (for setting one against the other: 1 puts out the light of a rocket's flame on its pad's cloud over a second once its jet leaves the grate, as until 2026-11-20, not as it climbs away: see Pad.Shine)
        public static float TestDeck;                  // (for setting one against the other: 1 makes no steam off the pad's deck round the flame's foot, as until 2026-11-19: see Site.Ends)
        public static float TestSpray;                 // (for setting one against the other: 1 draws no streaks of water for a blast at sea, as until 2026-11-19: see Site.Splash)
        public static float TestLand;                  // (for setting one against the other: 1 lets water or earth a blast threw up go in the frame it comes down, as until 2026-11-20, not thin away: see Site.ComeDown)
        public static float TestSeed;                  // (for setting one against the other: above nought, a splash's chance, its own and the game's, starts from this number, so that two films of it lay the same jets; nought, as always, each from its own: see Site.Splash)
        public static float TestVent;                  // (for setting one against the other: 1 lays an explosive's blast under the water with no grey gas coming up through its dome, as until 2026-11-20; 2 its gas but its plumes white, as first made that day; 3 its gas climbing as hot gas and lasting 6 to 16 s, as made at 00:2x that day, where it now goes down with the water and pales: see Site.Splash)
        public static float TryVent = -1f;             // (trying: how grey an explosive's gas is coming up through a blast at sea's dome, 0 to 1; below nought, as set in Site.Splash)
        public static float TryPlume = -1f;            // (trying: how grey the plumes an explosive's gas throws up through a blast at sea's dome are at most, as so much of its gas's grey, 0 to 1; below nought, as set in Site.Splash)
        public static float TryTip = -1f, TryTipThin = -1f;     // (trying: how much faster the lumps at the head of a blast at sea's jets widen, and how much thinner they are, 0 and 0 as until 2026-11-20; below nought, as set in Site.TipSpread and TipThin)
        public static float TrySprayGrow = -1f, TrySpraySpread = -1f;     // (trying: how much wider a blast at sea's streaks of water get over their lives, 2.2 / 0.6 as until 2026-11-20, and how far round their jets they go, 1 as until then (TryZ 0.2 their drawing out as then); below nought, as set in Site.SprayGrows and SpraySpread, 2.2 and 2)
        public static float TryGasSettle = -1f, TryGasLife = -1f;     // (trying: how fast an explosive's gas out of a blast at sea settles, metres a second, more or less, where it climbed 0.3 to 0.9 until 2026-11-20 (TestVent 3); how long it lasts, so many times the 6 to 16 s it lasted then; below nought, as set in Site.GasSettles and GasLasts, 1 and 0.5)
        public static float TrySurge = -1f;            // (trying: how far a blast under the sea's mist runs out low along the water as one widening ring, 0 as until 2026-11-20; below nought, as set in Site.MistSurge)
        public static float TrySprayDrag = -1f;        // (trying: how hard the air holds back a blast at sea's streaks of water, as the slowing of each metre a second of its speed by each metre a second of it at the sea's air, 0.004 as until 2026-11-20; below nought, as set in Site.SprayDrag)
        public static float TrySteamStops = -1f;       // (trying: how much a launch's steam stops its rocket's flame's light on the way through it, as a share of what it would stop along a straight line: 0 as until 2026-11-20; below nought, as set in Site.SteamStops)
        public static float TrySteamLevel = -1f;       // (trying: a launch's steam by night, its flame's light at its strongest so much of the day's: 1 as until 2026-11-20; below nought, as set in Site.SteamNightLevel)
        public static float TryShoulder = -1f;         // (trying: the camera's shoulder on the smoke, how far light too strong for the picture goes over to white: 0 as until 2026-11-20; below nought, as set in Site.Shoulder)
        public static float TrySteamScatter = -1f, TrySteamReach = -1f;     // (trying: a launch's steam by night, how much of its flame's light it sends on inside itself to where the straight way does not reach, 0 as until 2026-11-20, and how far that goes, as a share of the straight way's stopping; below nought, as set in Site.SteamScatter)
        public static float TrySteamWhite = -1f;       // (trying: a launch's steam by night, how much whiter its flame's light the straight way is than what comes round through the steam, 0 none as until 2026-11-20; below nought, as set in Site.SteamWhite)
        public static float TrySteamKnee = -1f;        // (trying: a launch's steam by night, how much of the top of the picture's range a camera's tone curve bends, colour by colour, 0 none as until 2026-11-20; below nought, as set in Site.SteamKnee)
        public static float TryJetFades = -1f, TryJetSoot = -1f, TryJetStreaks = -1f, TryJetStreaksFar = -1f, TryJetEndCool = -1f, TryJetCool = -1f, TryJetHot = -1f, TryJetEdge = -1f, TryJetEndTorn = -1f, TryJetTongues = -1f;     // (trying, since 2026-11-20: the sooty engines' flames, kerosene's and a solid motor's, as Site.Plumes has them; below nought, as set there. See Site.PlumeOf)
        public static float TryJetSheath = -1f, TryJetSheathFrom = -1f, TryJetSheathTo = -1f, TryJetSheathNozzle = -1f;     // (trying: an engine's flame's outer part cooler than its core, by how much of its heat at the edge, 0 as until 2026-11-20; between what thicknesses of flame it goes from that to none; and how much of it there is at the nozzle; below nought, as set in Site.JetSheaths)
        public static float TryJetHue = -1f, TryJetGain = -1f;     // (trying: a kerosene engine's flame, how far its colours go over to the orange of the Falcon 9's, 0 as until 2026-11-20, and so many times as bright in the thick air, 1 as until then; below nought, as set in Site.JetFuelHue and JetFuelGain)
        public static float TrySceneStops = -1f, TrySceneScatter = -1f, TrySceneReach = -1f, TrySceneRelief = -1f;     // (trying: the lamps of the scene's light on smoke at night (the floodlights): how much of what the smoke between would stop along a straight line it stops, how much goes round inside the smoke, how far that goes as a share, and how much the billows shade it, all 0 as until 2026-11-20 (lit evenly through); below nought, as set in Site.SceneStops)
        public static float TryLampLine = -1f;         // (trying: how far up a rocket's flame over its pad, from its foot to the nozzle's mouth, its light on the smoke comes from all along, 0 the foot alone as until 2026-11-20; below nought, as set in Site.FlameLine)
        public static float TryS = -1f;                // (trying: how much steam boils up off the pad's deck round the flame's foot, as so much of what the trench's ends make; below nought, as set in Site.DeckSteam)
        public static float TryX = -1f;                // (trying: how many of its own sizes a burning piece's flame is long, the rest of its trail smoke: 0 as until 2026-11-19, by the clock; below nought, as set in Site.Trail, 3)
        public static float TryW = -1f;                // (trying: the fires' light on the smoke at night, so many times as strong as by day: 1 as until 2026-11-19; below nought, as set in Site.FireGain's use for the smoke, 2.5, and for a launch's steam, Site.SteamNight, 2.5 until 2026-11-20)
        public static float TryT = -1f, TryU = -1f;     // (trying: T the big billows' size, as so many to what there were until 2026-11-16; U how much the small billows count for in the billows' shade; below nought, as set in Site.TuneVolume)
        public static float TestRound = -1f;           // (trying: 0 a nuclear explosion's cap stays twice as wide as it is tall once it has rolled up, as until 2026-11-19; below nought, as set in SiteNuke's Shape)
        public static float TryP = -1f;                // (trying: how far back along its stream new smoke's place among the billows is put, 1 all the way, 0 not at all as until 2026-11-16; below nought, as set in Site.Streams)
        public static float TryE = -1f;                // (trying: the most metres the billows' pattern repeats over for a blast's smoke; below nought, as set in Site.Burst)
        public static float TestMass;                  // (for setting one against the other: 1 lays every blast of a crash as a fireball of its own, as until 2026-11-16: see Site.Burst, "one fire of many blasts")
        public static float TestTakes;                 // (for setting one against the other: 1 gives a blast to any patch of air within 160 m, as until 2026-11-16: see Site.Takes)
        public static float TestTrail;                 // (for setting one against the other: 1 throws a blast's burning pieces as until 2026-11-16, all much alike, their trails thick and laid a step's worth at one place)
        public static float TryA = -1f, TryB = -1f, TryC = -1f, TryD = -1f;     // (for a trial in hand: what each stands for is said where it is read; below nought, not set)
        public static float TryL = -1f, TryM = -1f, TryY = -1f, TryZ = -1f, TryO = -1f;     // (trying, a blast at sea's streaks of water: L how many, so many times what its size gives; M how high, as so much of its jet's height; Y how thick to the eye, so many times as set; Z how far each is drawn out by its speed; O how wide, so many times as set; below nought, as set in Site.Splash)
        public static float TestFine = -1f;            // (trying: how many pixels a cell of the billows' pattern is read across at the least; below nought, as set in Site.TuneVolume)
        public static float TestEdge = -1f, TestEdge2 = -1f;     // (trying: how hard the smoke's cut edge is, and how much thicker just inside it; below nought, as set in Site.TuneVolume)
        public static float TestSoot;                  // (for setting one against the other: 1 makes the smoke of a fire as it was until 2026-10-09: a fireball's soot shown dot by dot, burning things' smoke thrown as an engine's)
        public static float TestFireSlots;             // (for setting one against the other: 1 gives the smoke the light of a patch's three strongest fires as each frame has them, as until 2026-11-03, not each slot kept to its fire: see Site.SmokeLamps)
        public static float TestBurnSmoke;             // (for setting one against the other: 1 sends up the smoke of something burning with a flame of its own from just over its foot, already dark, as until 2026-11-03, not hot from its flames' tips: see Site.Smoulder)
        public static float TestFlameFloor;            // (for setting one against the other: 1 gives a ship's patch the ground as the one plane found under the ship, as until 2026-11-03, with no hollow where a fire burns on lower ground: see Site.Floors)
        public static float TestFlamePlaces;           // (for setting one against the other: 1 counts every ship's patch against the places for ships' flames until it is put away, empty or not and its ship gone or not, as until 2026-11-04: see Site.HoldsFlamePlace)
        public static float TestFlameNear;             // (for setting one against the other: 1 gives the places for ships' flames to whichever ship asks first, kept while it asks, as until 2026-11-04, not to the nearest: see Air.SeatFor and Air.Sit)
        public static float TestContact;               // (for setting one against the other: 1 lays smoke lying on the ground as until 2026-11-04, a dome on the ground that touches it at a point and is thinnest there, so that it seemed to hover; 2 lays it flat-bottomed instead of half sunk, its lower half drawn out four times into the ground, for comparing the two new ways. Both new ways keep a flame's own foot as it was: see Site.Deposit)
        public static float TestBaseBillows;           // (for setting one against the other: 1 counts the ground as an edge of the smoke when the billows are cut, as until 2026-11-04, the empty cells under it thinning the smoke round about the foot: see Site.AroundLayer)
        public static float TestFootCarry;             // (for setting one against the other: 1 lets the shader carry the smoke at the foot of a cloud upward between one grid and the next as fast as the rest of it, as until 2026-11-04, reading where there is nothing under the ground and lifting the foot off it: see Site.Carry)
        public static float TestGroundShade;           // (for setting one against the other: 1 leaves the ground under lying smoke as light as the open ground round it, and the smoke's shadow as dark under an overcast as in sunshine, as until 2026-11-04; 2, in a development build, the sky's part alone as dark as it goes, to see where it falls: see Site.TuneShadow and shadow.glsl)
        public static float TestFireBlend;             // (for setting one against the other: 1 shows a fire that stands from each new grid as it comes, as until 2026-11-04, not going over from the grid before's flame: see _VolFireWasSize in volume.glsl)
        public static float TestSlump;                 // (for setting one against the other: 1 leaves cold smoke lying on flat ground where it lies, as until 2026-11-04, not spreading out from where it lies thickest as heavy air does: see Site.Slumps)
        public static float TestShipsHalf;             // (for setting one against the other: 1 sends the ships' picture to half size and back as until 2026-11-04: a box with a corner behind the camera taken as the whole screen, here and for which grid comes first, a rectangle off the screen past a corner as covering it, back to whole at once, and each ship's patch walked as its last Step left it, which differs from the picture as drawn only in a frame with no Step, the game paused: see Layers.Covers, Layers.ShipsHalf and Site.WalkShips)
        public static float TestSceneCone;             // (for setting one against the other: 1 lets the scene's spotlights (the floodlights) light smoke every way, not only inside their cones; 2 puts none of the scene's lamps on smoke at all: see Site.TuneScene)
        public static float TestLayers;                // (for seeing what putting the patches' smoke in depth order pixel by pixel is worth, see Layers: 1 lays them over one another in the order the game used to draw them in; 2 has each patch put on the screen by its own box, as before 0.5.0; 3 shows each patch in a colour of its own, in depth order, and 4 the same in the old order; 5 puts one slot to a row of the atlas; 6 shows how far behind the nearest patch's smoke the next lies (red, 0 to 10 m) and how widely each is spread along the ray (green the nearest, blue the next, 0 to 5 m); 7 lays them over one another in depth order, mixed where close, as until 2026-10-10; 8 walks the ships' patches at half size always, and 9 never: see Layers.ShipsHalf)
        public static float TestBlaze;                 // (for setting one against the other, and for measuring what it costs: 1 leaves the fire a blast leaves on the ground as it was until 0.6.2: out within half a minute whatever was spilt, a pool of its own for every blast, however near the last, and nothing burning round about it. 2 is the new fire without the patches of spilt fuel that burn round about the pool; 3 has those patches after sundown too, when as installed they are left out because they cannot light the ground. See Site.AddFire, and Plan)

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
                node.TryGetValue("embers", ref Embers);
                node.TryGetValue("shockwave", ref Shockwave);
                node.TryGetValue("scorch", ref Scorch);
                node.TryGetValue("shake", ref Shake);
                node.TryGetValue("push", ref Push);
                node.TryGetValue("blasts_push", ref Knock);
                node.TryGetValue("blasts_raze", ref Raze);
                node.TryGetValue("behind_his_clouds", ref BehindHis);
                node.TryGetValue("far_haze", ref Haze);
                node.TryGetValue("sharp_when_fast", ref Sharp);
                node.TryGetValue("sharp_fps", ref SharpFps);
                node.TryGetValue("collide", ref Collide);
                node.TryGetValue("sound_travels", ref SoundTravels);
                node.TryGetValue("shadows", ref Shadows);
                node.TryGetValue("shaded", ref Shaded);
                node.TryGetValue("predict", ref Predict);
                node.TryGetValue("wakes", ref Wakes);
                node.TryGetValue("vortices", ref Vortices);
                node.TryGetValue("bdarmory", ref BDArmory);
                node.TryGetValue("bdarmory_most", ref BDArmoryMost);
                node.TryGetValue("armory", ref Armory);
                node.TryGetValue("armory_ours", ref ArmoryOurs);
                string armoryKey = null;
                if (node.TryGetValue("armory_key", ref armoryKey) && !string.IsNullOrEmpty(armoryKey))
                    try { ArmoryKey = (KeyCode)Enum.Parse(typeof(KeyCode), armoryKey.Trim(), true); } catch (Exception) { ArmoryKey = KeyCode.B; }
                node.TryGetValue("armory_fires", ref ArmoryFires);
                node.TryGetValue("armory_leaks", ref ArmoryLeaks);
                node.TryGetValue("armory_missiles", ref ArmoryMissiles);
                node.TryGetValue("armory_rockets", ref ArmoryRockets);
                node.TryGetValue("armory_guns", ref ArmoryGuns);
                node.TryGetValue("armory_hits", ref ArmoryHits);
                node.TryGetValue("armory_countermeasures", ref ArmoryCountermeasures);
                node.TryGetValue("armory_nukes", ref ArmoryNukes);
                node.TryGetValue("armory_splashes", ref ArmorySplashes);
                node.TryGetValue("armory_cut", ref ArmoryCut);
                node.TryGetValue("patches", ref Patches);
                node.TryGetValue("gridsaframe", ref GridsAFrame);
                node.TryGetValue("log", ref Log);
                node.TryGetValue("volume", ref Volume);
                node.TryGetValue("half", ref Half);
                node.TryGetValue("follow", ref Follow);
                node.TryGetValue("fireswhole", ref FiresWhole);
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
                // (since 0.10.0, fine details on Keystone's page as well: a fire's look, how long trails stay, water thrown up)
                node.TryGetValue("fire_bright", ref FireBright);
                node.TryGetValue("fire_tall", ref FireTall);
                node.TryGetValue("fire_tongues", ref FireTongues);
                node.TryGetValue("fire_light", ref FireLight);
                node.TryGetValue("trail_lingers", ref TrailLingers);
                node.TryGetValue("sea_billows", ref SeaBillows);
                Quality = Mathf.Clamp(Quality, 0.2f, 3f);
                Smoke = Mathf.Clamp(Smoke, 0f, 4f);
                Size = Mathf.Clamp(Size, 0.3f, 3f);
                Wind = Mathf.Clamp(Wind, 0f, 4f);
                Detail = Mathf.Clamp(Detail, 0f, 1f);
                Thick = Mathf.Clamp(Thick, 0.3f, 4f);
                Thin = Mathf.Clamp(Thin, 0f, 8f);
                Bend = Mathf.Clamp(Bend, 0f, 2f);
                MaxParticles = Mathf.Clamp(MaxParticles, 2000, 120000);
                BDArmoryMost = Mathf.Clamp(BDArmoryMost, 0, 400);
                Patches = Mathf.Clamp(Patches, 6, 15);
                GridsAFrame = Mathf.Clamp(GridsAFrame, 0, 12);
                Threads = Mathf.Clamp(Threads, 0, 32);
                FireBright = Mathf.Clamp(FireBright, 0.1f, 1.5f);
                FireTall = Mathf.Clamp(FireTall, 0.5f, 3f);
                FireTongues = Mathf.Clamp(FireTongues, 0.5f, 2f);
                FireLight = Mathf.Clamp(FireLight, 0f, 24f);
                TrailLingers = Mathf.Clamp(TrailLingers, 1f, 60f);
                SeaBillows = Mathf.Clamp(SeaBillows, 0f, 1f);
            }
            catch (Exception ex) { Addon.Log("could not read settings.cfg (" + ex.Message + "); using the defaults"); }
        }
    }

    /// <summary>The textures and the materials made from them.</summary>
    public static class Assets
    {
        public static Material Cloud, Glow, GlowBehind, Spark, Ring, Shard, Scorch, Embers, Dust;
        /// <summary>The kinds of burn mark, each with its embers (since 0.8.0: no two marks alike; see Scorch in Air.cs). The first is Scorch's and Embers' own picture.</summary>
        public static Texture2D[] ScorchKinds = new Texture2D[0], EmberKinds = new Texture2D[0];
        /// <summary>
        /// The ground under a nuclear explosion (scorchwide.png) and what glows in it while fresh (emberswide.png): made by
        /// tools/fxbake/nukemark.py, laid by Air.LayNuke. Null where the files are missing: such a mark is then one of the others, enlarged.
        /// </summary>
        public static Texture2D ScorchWide, EmbersWide;
        public static Shader Volume;                   // the smoke-as-a-volume shader; null where the game cannot use it
        public static Shader VolumeRelease;            // the same without the testing switches' code (6-13 % cheaper; see Site.TuneVolume); null where missing
        public static Shader Mark;                     // the shader that throws a burn mark onto whatever is there; null where the game cannot use it
        public static Shader Shock;                    // the shader that bends the picture behind a blast's shock front; null where the game cannot use it
        public static Shader Enlarge;                  // the shader that puts smoke drawn at half size on the screen; null where the game cannot use it
        public static Shader Shadow;                   // the shader that throws the smoke's shadow on what is under it; null where the game cannot use it
        public static Shader Motion;                   // the shader that writes where the smoke is going into the game's picture of motion (see Layers); null where the game cannot use it
        public static Shader Carry;                    // the shader that carries a patch's smoke over from the frame before where only a quarter of it is walked (see Layers); null where the game cannot use it
        public static Shader Layers;                   // the shader that puts every patch's smoke on the screen together, in depth order (see Layers); null where the game cannot use it
        public static Shader Sprite;                   // the sprites' own shader, which takes off them the smoke in front (sprite.glsl); null where it cannot be had
        /// <summary>Whether the sprites are drawn by the mod's own shader and Layers' shader draws the picture of the smoke in front for it
        /// (on OpenGL from layersfront.bundle, which has that picture; on Direct3D both from the one bundle).</summary>
        public static bool FrontReady => Sprite != null && layersFront;
        static bool layersFront;
        public static readonly int SpriteKindNote = Shader.PropertyToID("_SpriteKind");
        const float CloudSoften = 0.45f;
        /// <summary>The puffs' kind for sprite.glsl: premultiplied, softened, the smoke in front taken off them unless they are drawn
        /// before the smoke (from within the cloud, Site).</summary>
        public static Vector4 CloudKind(bool within) => new Vector4(0f, CloudSoften, within ? 1f : 0f, 0f);
        public static Shader SunDepth;                 // the shader that draws what stands between the smoke and the sun, as the sun sees it (see SunShadow); null where the game cannot use it
        public static Mesh Sheet;                      // one triangle that covers the whole screen
        public static Texture3D Detail;
        public static Mesh Cube;
        // Four numbers of sixteen bits to a cell, read smoothly between cells (for where the smoke "was"): plain numbers where
        // the graphics card will read those smoothly, as every desktop card should; otherwise half-precision ones.
        public static UnityEngine.Experimental.Rendering.GraphicsFormat Sixteen = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_UNorm;
        public static bool SixteenAsHalves;
        static bool volumeTried, markTried, shockTried, enlargeTried, shadowTried, layersTried, motionTried, sunDepthTried, carryTried;
        public const int Tiles = 4;                    // the particle sheet is 4 by 4
        static int builtForLimit = -1;

        static bool programsWarmed;

        /// <summary>
        /// The card made to build the mod's programs now, as they are loaded, not at the first explosion's first frame, when it stalled
        /// the game (2026-10-18; see Air.WarmUp): every pass of each of the mod's shaders, and of the particle shaders it draws with.
        /// Once a game.
        /// </summary>
        static void WarmPrograms(params Shader[] shaders)
        {
            if (programsWarmed) return;
            programsWarmed = true;
            try
            {
                var warm = new ShaderVariantCollection();
                int n = 0;
                foreach (Shader s in shaders)
                {
                    if (s == null) continue;
                    try { if (warm.Add(new ShaderVariantCollection.ShaderVariant(s, UnityEngine.Rendering.PassType.Normal))) n++; }
                    catch (Exception) { }
                }
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                warm.WarmUp();
                Addon.Log(n + " of the mod's programs made ahead on the card, in " + ((System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + " ms");
            }
            catch (Exception ex) { Addon.Log("the programs could not be made ahead: " + ex.Message); }
        }

        public static bool Load()
        {
            // The game's texture quality setting can change between flights; the textures allow for it (see Texture).
            LoadVolume();
            LoadMark();
            LoadShadow();
            LoadShock();
            LoadSunDepth();
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
            WarmPrograms(Volume, VolumeRelease, Mark, Shadow, Shock, SunDepth, Enlarge, Layers, Motion, Carry, Sprite, premultiplied, additive, blended);
            Texture2D puff = Texture("puff.png"), glow = Texture("glow.png"), spark = Texture("spark.png"), ring = Texture("ring.png"), shard = Texture("shard.png"), scorch = Texture("scorch.png"), embers = Texture("embers.png");
            if (puff == null || glow == null || spark == null || ring == null || shard == null || scorch == null || embers == null) return false;
            // The second number softens a particle where it meets the ground or a ship instead of ending in a hard line.
            Cloud = Make(premultiplied, puff, false, CloudSoften);
            Glow = Make(additive, glow, true, 0.25f);
            Spark = Make(additive, spark, true, 0f);
            Ring = Make(additive, ring, true, 0.6f);
            Shard = Make(blended, shard, true, 0f);
            Dust = Make(blended, glow, true, 0.3f);
            // Burn marks are drawn straight after the solid ground and before anything see-through, so that smoke is never behind them.
            Scorch = Make(blended, scorch, true, 0f);
            Scorch.renderQueue = 2460;
            Embers = Make(additive, embers, true, 0f);
            Embers.renderQueue = 2461;
            // (the other kinds of mark, scorch1.png and embers1.png on, as many as there are: see tools/fxbake's Burn)
            var marks = new List<Texture2D> { scorch };
            var glows = new List<Texture2D> { embers };
            for (int k = 1; k < 16 && File.Exists(Settings.Folder + "PluginData/scorch" + k + ".png") && File.Exists(Settings.Folder + "PluginData/embers" + k + ".png"); k++)
            {
                Texture2D one = Texture("scorch" + k + ".png"), hot = Texture("embers" + k + ".png");
                if (one == null || hot == null) break;
                marks.Add(one);
                glows.Add(hot);
            }
            ScorchKinds = marks.ToArray();
            EmberKinds = glows.ToArray();
            bool wide = File.Exists(Settings.Folder + "PluginData/scorchwide.png") && File.Exists(Settings.Folder + "PluginData/emberswide.png");
            ScorchWide = wide ? Texture("scorchwide.png") : null;
            EmbersWide = wide ? Texture("emberswide.png") : null;
            // The order things are drawn in: the glow of a fire, then the volume of smoke over it, then what is left as sprites, then flashes and sparks.
            GlowBehind = new Material(Glow) { renderQueue = 2990 };
            Cloud.renderQueue = 3001;
            Shard.renderQueue = 3001;
            Dust.renderQueue = 3001;
            Glow.renderQueue = 3002;
            Spark.renderQueue = 3002;
            Ring.renderQueue = 3002;
            // (and where the sprites' own shader is there, drawn by it, as the game's draw them, less the smoke in front of each: see
            // sprite.glsl. Not the fire's glow, which is drawn before the smoke and covered by it; nor its flashes, which SiteBlasts
            // dims already by all the smoke along the line to each. Not the burn marks, drawn before anything see-through.)
#if DEV
            premultipliedWas = premultiplied; additiveWas = additive; blendedWas = blended;
#endif
            if (FrontReady)
            {
                Sprited(Cloud, CloudKind(false));
                Sprited(Glow, new Vector4(1f, 0.25f, 1f, 0f));
                Sprited(GlowBehind, new Vector4(1f, 0.25f, 1f, 0f));
                Sprited(Spark, new Vector4(1f, 0f, 0f, 0f));
                Sprited(Ring, new Vector4(1f, 0.6f, 0f, 0f));
                Sprited(Shard, new Vector4(2f, 0f, 0f, 0f));
                Sprited(Dust, new Vector4(2f, 0.3f, 0f, 0f));
            }
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
                if (UseBuilt) { Volume = BuiltShader("Volume"); VolumeRelease = BuiltShader("VolumeRelease"); }
                else
                {
                    string path = Settings.Folder + "PluginData/volume.bundle";
                    if (!File.Exists(path)) { Addon.Log("smoke is drawn as sprites: " + path + " is missing"); return; }
                    AssetBundle bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null) { Addon.Log("smoke is drawn as sprites: the shader bundle did not load"); return; }
                    Shader[] found = bundle.LoadAllAssets<Shader>();
                    bundle.Unload(false);
                    foreach (Shader shader in found) if (shader != null && shader.isSupported) Volume = shader;
                    // (and the copy without the testing switches, where it is: see Site.TuneVolume)
                    string release = Settings.Folder + "PluginData/volumerelease.bundle";
                    AssetBundle second = File.Exists(release) ? AssetBundle.LoadFromFile(release) : null;
                    if (second != null)
                    {
                        foreach (Shader shader in second.LoadAllAssets<Shader>()) if (shader != null && shader.isSupported) VolumeRelease = shader;
                        second.Unload(false);
                    }
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
#if DEV
        static Shader premultipliedWas, additiveWas, blendedWas;
        /// <summary>(Development build: the sprites drawn by the game's own particle shaders again, "off", as before 2026-10-28, or by
        /// sprite.glsl, "on": for films of before and after. A patch keeps a copy of the puffs' material: see Air.SpriteTest.)</summary>
        public static string SpriteTest(string v)
        {
            if (Cloud == null || premultipliedWas == null) return "no sprites' materials yet";
            if (!FrontReady) return "no sprite shader here (layers with the smoke in front " + layersFront + ", sprite " + (Sprite != null) + ")";
            if (v == "off")
            {
                Reshaded(Cloud, premultipliedWas);
                Reshaded(Glow, additiveWas); Reshaded(GlowBehind, additiveWas); Reshaded(Spark, additiveWas); Reshaded(Ring, additiveWas);
                Reshaded(Shard, blendedWas); Reshaded(Dust, blendedWas);
                return "the sprites by the game's own particle shaders, laid over the smoke or under it whole" + Queues();
            }
            Sprited(Cloud, CloudKind(false));
            Sprited(Glow, new Vector4(1f, 0.25f, 1f, 0f));
            Sprited(GlowBehind, new Vector4(1f, 0.25f, 1f, 0f));
            Sprited(Spark, new Vector4(1f, 0f, 0f, 0f));
            Sprited(Ring, new Vector4(1f, 0.6f, 0f, 0f));
            Sprited(Shard, new Vector4(2f, 0f, 0f, 0f));
            Sprited(Dust, new Vector4(2f, 0.3f, 0f, 0f));
            return "the sprites by " + Sprite.name + " (supported " + Sprite.isSupported + "), less the smoke in front of each" + Queues();
        }

        /// <summary>(A patch's own copy of the puffs' material, as SpriteTest left the puffs': its turn and whether it is within kept.)</summary>
        public static void SpritePuffs(Material m, bool on)
        {
            if (m == null || premultipliedWas == null || !FrontReady) return;
            if (!on) { Reshaded(m, premultipliedWas); return; }
            Vector4 kind = m.HasProperty(SpriteKindNote) ? m.GetVector(SpriteKindNote) : CloudKind(false);
            Sprited(m, new Vector4(0f, CloudSoften, kind.z, 0f));
        }

        static string Queues() => string.Format("; their turns: puffs {0}, glow {1}, glow behind {2}, sparks {3}, rings {4}, shards {5}, dust {6}",
            Cloud.renderQueue, Glow.renderQueue, GlowBehind.renderQueue, Spark.renderQueue, Ring.renderQueue, Shard.renderQueue, Dust.renderQueue);
#endif

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

        /// <summary>The shader that draws what stands between the smoke and the sun (see tools/shaderpack/sundepth.glsl and SunShadow). Without it nothing throws a shadow into the smoke.</summary>
        static void LoadSunDepth()
        {
            if (sunDepthTried) return;
            sunDepthTried = true;
            try { SunDepth = UseBuilt ? BuiltShader("SunDepth") : OpenGLShader("sundepth.bundle"); }
            catch (Exception ex)
            {
                SunDepth = null;
                Addon.Log("nothing throws a shadow into the smoke: " + ex.Message);
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
            try
            {
                // (on OpenGL the copy that also draws the picture of the smoke in front, where there is one: only then is the sprites'
                // shader used, so that a sprite.bundle beside an older layers.bundle never reads a picture of something else)
                Shader withFront = Volume != null && !UseBuilt ? OpenGLShader("layersfront.bundle") : null;
                Layers = withFront ?? (Volume != null ? OpenGLShader("layers.bundle") : null);
                layersFront = Layers != null && (UseBuilt || withFront != null);
            }
            catch (Exception ex)
            {
                Layers = null;
                Addon.Log("each patch of smoke is put on the screen by itself: " + ex.Message);
            }
            try { Sprite = layersFront ? OpenGLShader("sprite.bundle") : null; }
            catch (Exception ex)
            {
                Sprite = null;
                Addon.Log("the sprites are drawn over the smoke or under it whole: " + ex.Message);
            }
            Addon.Log(Sprite != null ? "the sprites are drawn less the smoke in front of each" : "the sprites are drawn over the smoke or under it whole, by their turn");
            LoadMotion();
            LoadCarry();
        }

        /// <summary>
        /// The shader by which a patch walked a quarter of its pixels a frame has the rest carried over from the frame before
        /// (tools/shaderpack/carry.glsl; see Layers). Without it every patch is walked whole, as before.
        /// </summary>
        static void LoadCarry()
        {
            if (carryTried) return;
            carryTried = true;
            try { Carry = Layers != null ? OpenGLShader("carry.bundle") : null; }
            catch (Exception ex)
            {
                Carry = null;
                Addon.Log("smoke is walked whole every frame: " + ex.Message);
            }
        }

        /// <summary>
        /// The shader by which smoke says where it is going (tools/shaderpack/motion.glsl). Without it, a game that
        /// smooths its picture between frames smears smoke that moves.
        /// </summary>
        static void LoadMotion()
        {
            if (motionTried) return;
            motionTried = true;
            try { Motion = Layers != null ? OpenGLShader("motion.bundle") : null; }
            catch (Exception ex)
            {
                Motion = null;
                Addon.Log("moving smoke is not followed by the game's smoothing between frames: " + ex.Message);
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

        /// <summary>A sprite material drawn by the mod's own shader instead (its picture and queue kept): kind, softening and
        /// whether the smoke in front is left on it, as sprite.glsl reads them, and the game's tint of mid grey.</summary>
        static void Sprited(Material m, Vector4 kind)
        {
            Reshaded(m, Sprite);
            m.SetVector(SpriteKindNote, kind);
            m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        /// <summary>A material given another shader, its turn kept: Unity puts a material's turn back to its new shader's own when the
        /// shader is changed (Transparent, 3000, the sheet's own), which would lose the order of fire, smoke and sprites set in Load.</summary>
        static void Reshaded(Material m, Shader shader)
        {
            int turn = m.renderQueue;
            m.shader = shader;
            m.renderQueue = turn;
        }

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
