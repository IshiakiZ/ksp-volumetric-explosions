using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosions
{
    /// <summary>
    /// Keeps every explosion in the scene going. Explosions near each other share a <see cref="Site"/>:
    /// one cloud of particles in one patch of air, so that a second blast shoves the smoke of the first
    /// about and a ship flying through stirs all of it.
    /// </summary>
    public sealed class Air : MonoBehaviour
    {
        public static Air Instance;
        public static int Alive;                       // particles in the scene
        public static float LastMs;                    // what the last frame's work cost
        public static string LastPlan = "";
        public static readonly long[] Cost = new long[4];      // timer ticks this frame: waiting for the particles, the rest of the frame's work, and (on other threads) moving them and building the grid
        static readonly float[] shown = new float[4];
        static float gridMs;

        public static int Threads => Settings.Threads > 0 ? Settings.Threads : Mathf.Clamp(Environment.ProcessorCount - 2, 1, 8);
        /// <summary>How many threads the making of a grid may use at once (it is not in a hurry, and the game needs the processor too).</summary>
        // (for the development build: what each part of a frame's work on the game's own thread takes, see Site.Step and BeforeDrawing)
        public static readonly long[] Part = new long[16];
        static readonly float[] partShown = new float[16];
        static readonly string[] partName = { "dead", "turn aside", "rest", "sprites", "frame", "blasts", "air", "fires", "pieces", "look ahead", "grid over", "tune", "glows", "start", "place", "marks" };
#if DEV
        public static long FlameTicks, FlameBits;
        static long flameTicksWas, flameBitsWas, flameFramesWas;
        /// <summary>(How long laying engines' flames has taken since last asked, a frame on average, and for each bit laid.)</summary>
        public static string FlameCost()
        {
            long frames = Time.frameCount - flameFramesWas, ticks = FlameTicks - flameTicksWas, bits = FlameBits - flameBitsWas;
            flameTicksWas = FlameTicks; flameBitsWas = FlameBits; flameFramesWas = Time.frameCount;
            if (frames <= 0) return "no frames";
            double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return (ms / frames).ToString("F3") + " ms a frame laying flames, " + (bits / (double)frames).ToString("F0") + " bits a frame, " + (bits > 0 ? (ms * 1000.0 / bits).ToString("F2") : "-") + " us a bit, over " + frames + " frames";
        }
#endif
        public static void Lap(int which, ref long lap)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Part[which] += now - lap;
            lap = now;
        }
        // (for the development build: the four parts of "fires" in a patch's step, see Site.Step and the slow frames' log)
        public static readonly long[] FireParts = new long[4];
        [System.Diagnostics.Conditional("DEV")]
        public static void SubLap(int which, ref long at)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            FireParts[which] += now - at;
            at = now;
        }
        public static readonly long[] Stage = new long[8];    // timer ticks the parts of the last grid took: getting ready, putting the particles in, the sun's light, the sky's, what is round about, where it is going, (those four together), packing
        public static readonly System.Threading.Tasks.ParallelOptions Few = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = -1 };

        readonly List<Blast> newborn = new List<Blast>();
        readonly SunShadow sunShadow = new SunShadow();
        readonly List<Site> ranked = new List<Site>();
        static readonly Comparison<Site> byAway = (a, b) => a.AwayNow.CompareTo(b.AwayNow);
        readonly List<Site> sites = new List<Site>();
        readonly List<Scorch> marks = new List<Scorch>();
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        int placedFrame = -1, steppedFrame = -1;
        bool complained;

        void Awake()
        {
            Instance = this;
            Camera.onPreCull += BeforeDrawing;
            Camera.onPreRender += ForCamera;
            Camera.onPostRender += AfterDrawing;
            StartCoroutine(AfterEachFrame());
        }

        // ---- the first explosion's first-time work, done ahead (2026-10-18)
        // (In a fresh game the first explosion stalled it for 129 to 183 ms in one or two frames, out of sight or in view, and the
        // next ones never for more than 43: the air's update 72 ms, the end of the frame 38 and the drawing's setting up 25 for the
        // first time (Keystone's meter, method by method), and the card making the smoke's programs. Fragmentation's warm-up only
        // begins and puts away a patch of air. So a few seconds into each flight scene a fuel crash is set off where it cannot be
        // seen (see where, below), through the game's own way of setting one off (its bang never heard: see Blast's Awake), kept a
        // few frames and taken away; and the card makes the mod's programs as they are loaded (Assets.WarmPrograms).)
        static bool warming;
        static int warmFrame = -1;
        /// <summary>(An explosion that appears now is the warm-up's, set off in this same frame: never to be heard, see Blast.)</summary>
        public static bool WarmingNow => warming && Time.frameCount == warmFrame;
        Site warmSite;
        GameObject warmShip;
        Vector3d warmAt;
        Vector3 warmUp;

        void WarmExhaust()
        {
            if (warmShip == null) return;
            Sources.Burning(new Exhaust
            {
                id = 0x5741524d, rim = warmAt + (Vector3d)(warmUp * 30f), along = -(Vector3d)warmUp, going = Vector3d.zero, nozzle = 1.25f, flame = 12f,
                running = 1f, amount = 1f, shade = 0.5f, warm = 0.3f, lasts = 4f, tint = 1, fire = 1f, rides = warmShip.transform, kind = ExhaustKind.Engine,
            });
        }
        readonly List<Blast> warmBlasts = new List<Blast>();

        IEnumerator WarmUp()
        {
            float until = Time.time + 30f;
            while (Time.time < until && (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed)) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(0.5f);       // (while the scene is still settling)
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Vessel v = FlightGlobals.ActiveVessel;
            if (cam == null || v == null || !Settings.Volume || Assets.Volume == null || v.mainBody == null || !v.mainBody.atmosphere) yield break;
            // (Where: from a camera near the ground, on the ground beyond the horizon in front of it, below the planet's curve, so that
            // it is drawn as a real one is, and the drawing's own first-time work is done too, but hidden; from a camera high up,
            // 20 km behind it, at 300 m. What: a fuel crash, as the first explosion of a flight mostly is: its fireball, its fire on
            // the ground, its burn mark.)
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
            Vector3 ahead = Vector3.ProjectOnPlane(cam.transform.forward, up).normalized;
            if (ahead.sqrMagnitude < 0.5f) ahead = Vector3.ProjectOnPlane(cam.transform.up, up).normalized;     // (looking straight down)
            double camHigh = v.mainBody.GetAltitude(cam.transform.position) - Math.Max(0.0, v.mainBody.TerrainAltitude(v.mainBody.GetLatitude(cam.transform.position), v.mainBody.GetLongitude(cam.transform.position)));
            Vector3d at;
            if (camHigh < 500.0)
            {
                // (twice as far as the horizon and 5 km more, at least 20 km: from 70 m up on Kerbin the horizon is 9 km off, from
                // 400 m 22; a fireball's first frames there stand well under the line the eye can see over the planet's curve)
                double reach = Math.Max(20000.0, 2.0 * Math.Sqrt(2.0 * v.mainBody.Radius * Math.Max(1.0, camHigh)) + 5000.0);
                Vector3d far = (Vector3d)cam.transform.position + (Vector3d)ahead * reach;
                double lat = v.mainBody.GetLatitude(far), lon = v.mainBody.GetLongitude(far);
                double ground = Math.Max(v.mainBody.ocean ? 0.0 : double.MinValue, v.mainBody.TerrainAltitude(lat, lon));
                at = v.mainBody.GetWorldSurfacePosition(lat, lon, ground + 2.0);
            }
            else at = (Vector3d)cam.transform.position + (Vector3d)(-ahead * 20000f + up * 300f);
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            // (and a rocket's exhaust beside it, on a stand-in ship there, fed as the air asks for smoke each frame: the first
            // lift-off of a game stalled it 234-256 ms, the patches of an engine's smoke, its riding patch and the trail it lays,
            // being made for the first time among the rest)
            var before = new HashSet<Site>(sites);
            warmShip = new GameObject("VolumetricExplosions: a stand-in ship, once");
            warmShip.transform.position = (Vector3)at + up * 30f;
            warmAt = at; warmUp = up;
            Sources.Asked += WarmExhaust;
            warming = true;
            try { Records.Fake("fuel=1800 oxidizer=2200 cause=crash speed=60", at); warmFrame = Time.frameCount; FXMonger.Explode(null, at, 1.0); }
            catch (Exception ex) { warming = false; Addon.Log("could not warm the first explosion up: " + ex.Message); yield break; }
            // (and a fire's light, as faint as can be, for three frames where the camera is: the first light of a fire to fall on the
            // ground and the buildings round about made the card build their lit kinds there and then; a light drawn per pixel, as the
            // strongest fire's is, reaching as far as the camera sees much)
            // (and four more drawn the cheap way, per corner, as the lamps round an engine's flame are: the first of those made the card
            // build the lit kinds of everything in sight that take them, at the first lift-off of a game, see Natural Light's Engines)
            GameObject lamp = null;
            try
            {
                lamp = new GameObject("VolumetricExplosions: a faint light, once") { layer = Recipe.Layer };
                lamp.transform.position = cam.transform.position;
                Light l = lamp.AddComponent<Light>();
                l.type = LightType.Point; l.shadows = LightShadows.None; l.renderMode = LightRenderMode.ForcePixel;
                l.intensity = 0.01f; l.range = 1500f; l.color = Color.white;
                for (int k = 0; k < 4; k++)
                {
                    var corner = new GameObject("VolumetricExplosions: a faint light drawn per corner, once") { layer = Recipe.Layer };
                    corner.transform.SetParent(lamp.transform, false);
                    corner.transform.localPosition = new Vector3(k - 1.5f, 0.5f, 0f);
                    Light c = corner.AddComponent<Light>();
                    c.type = LightType.Point; c.shadows = LightShadows.None; c.renderMode = LightRenderMode.ForceVertex;
                    c.intensity = 0.01f; c.range = 1500f; c.color = Color.white;
                }
            }
            catch (Exception) { }
            for (int f = 0; f < 6; f++) yield return null;
            if (lamp != null) Destroy(lamp);
            warming = false;
            // (and its explosion's own things, its bang never let out)
            foreach (Blast b in warmBlasts) if (b != null) Destroy(b.gameObject);
            warmBlasts.Clear();
            Sources.Asked -= WarmExhaust;
            // (and, beside those still there, patches enough to leave three put by for the next ones to be made from, see Spares: at
            // a lift-off from the pad three begin in the one frame, the flame's, the trench's and the pad's smoke's)
            var spare = new List<Site>();
            try
            {
                int madeHere = 0;
                foreach (Site s in sites) if (!before.Contains(s)) madeHere++;
                for (int k = madeHere; k < 3; k++) spare.Add(Site.Make(Plan.Where(at + (Vector3d)(up * (400f + 150f * k)))));
            }
            catch (Exception ex) { Addon.Log("could not put patches by: " + ex); }
            if (warmSite != null) { sites.Remove(warmSite); warmSite.Dispose(); warmSite = null; }
            // (and every patch made there meanwhile: the exhaust's riding patch and its trail)
            for (int n = sites.Count - 1; n >= 0; n--)
            {
                Site s = sites[n];
                if (before.Contains(s)) continue;
                if ((s.FrameOrigin - at).magnitude > 3000.0) continue;
                sites.RemoveAt(n); s.Dispose();
            }
            foreach (Site s in spare) s.Dispose();
            // (and volume textures for nine patches, their names known to the library: see Sending. Six were too few: a climb has up
            // to seven patches at once, and the textures made on the way, whose names are not asked for while anything is drawn,
            // sent a ninth of its grids the slow way through Unity, 5 to 17 ms of a frame each: 2026-10-18)
            try { Site.PutByTextures(9); } catch (Exception ex) { Addon.Log("could not put textures by: " + ex.Message); }
            if (warmShip != null) { Destroy(warmShip); warmShip = null; }
            Addon.Log("the first explosion's first-time work done ahead, out of sight, in " + ((System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + " ms over six frames; " + Spares.Report());
        }

        void Start()
        {
            StartCoroutine(WarmUp());
            // (the launch pads of this scene, for what of an engine's jet goes down a pad's grate: see Pads)
            try { Pads.Look(); }
            catch (Exception ex) { Debug.LogError("[VolumetricExplosions] the launch pads could not be looked at: " + ex); }
        }

        static readonly int cameraNote = Shader.PropertyToID("_VolCamera");

        /// <summary>
        /// Tell the volume shader whether the camera about to draw has a depth picture of the scene to stop the smoke at, what
        /// angle one of its pixels covers, and what the depths the smoke keeps for being put in order are multiplied by (see
        /// Layers: kept in sixteen bits, which go to 65,504; the far camera's in sixty-fourths, to some four thousand km).
        /// </summary>
        void ForCamera(Camera camera)
        {
            int which = camera == depthCamera[0] ? 0 : camera == depthCamera[1] ? 1 : -1;
            bool small = halfOn && which >= 0;
            if (small) Half(which, camera);
            if (Layered && which >= 0) layers.ForCamera(which, camera);
            else layers.NotFor();
            Shader.SetGlobalVector(cameraNote, new Vector4((camera.depthTextureMode & DepthTextureMode.Depth) != 0 ? 1f : 0f, 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight), small ? 1f : 0f,
                                                             which == 1 ? 1f / 64f : 1f));
        }

        readonly Layers layers = new Layers();

        /// <summary>Whether every patch's smoke is put on the screen together, in depth order (see Layers), as it is wherever its shader can be had. (For testing: TestLayers 2 puts each patch on the screen by its own box, as before.)</summary>
        public static bool Layered => Instance != null && Settings.TestLayers != 2f && Layers.Possible;

        readonly CommandBuffer[] halfCommands = new CommandBuffer[2];
        bool halfOn;
        readonly List<Site> halves = new List<Site>();

        /// <summary>
        /// Smoke is soft, and most of what it costs is walking a ray through it for every pixel. So each cloud is
        /// drawn first into a small picture of its own, half as many pixels each way, by each of the two cameras
        /// that draw the flight, just before that camera draws its see-through things; the cloud's box then only
        /// puts the small picture on the screen, enlarged (see tools/shaderpack/enlarge.glsl), in its usual turn
        /// among the see-through things.
        /// </summary>
        void Halves()
        {
            halves.Clear();
            foreach (Site s in sites) if (s.Halved) halves.Add(s);
            Depth(0, depthOurs[0]);                              // (only to have the cameras looked up)
            bool on = halves.Count > 0 && depthCamera[0] != null;
            if (on != halfOn)
            {
                for (int which = 0; which < depthCamera.Length; which++)
                {
                    Camera c = depthCamera[which];
                    if (c == null) continue;
                    if (halfCommands[which] == null) halfCommands[which] = new CommandBuffer { name = "VolumetricExplosions smoke at half size" };
                    if (on) c.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, halfCommands[which]);
                    else c.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, halfCommands[which]);
                }
                halfOn = on;
            }
            if (on) halves.Sort((a, b) => b.Away.CompareTo(a.Away));
        }

        /// <summary>
        /// What one of those two cameras is to draw at half size, made up just before it draws: only the clouds
        /// that have some part in the stretch of distances it covers. (For the other camera such a cloud's small
        /// picture used to be cleared, drawn into and put on the screen all the same, every ray finding nothing.)
        /// </summary>
        void Half(int which, Camera camera)
        {
            CommandBuffer commands = halfCommands[which];
            if (commands == null) return;
            commands.Clear();
            int width = (camera.pixelWidth + 1) / 2, height = (camera.pixelHeight + 1) / 2;
            bool any = false;
            foreach (Site s in halves)
            {
                bool within = s.Within(camera);
                s.DrawnFor(within);
                if (!within) continue;
                commands.SetRenderTarget(s.Small(width, height));
                commands.ClearRenderTarget(false, true, Color.clear);
                commands.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0);
                any = true;
            }
            if (any) commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }

        /// <summary>
        /// The smoke stops at whatever solid thing is in front of it, and a burn mark is thrown onto the
        /// ground, by reading the depth picture a camera keeps of what it has drawn. The game's cameras
        /// keep none unless something asks (its own light cones at the space centre do, at night, which is
        /// easy to be fooled by). So each of the two cameras that draw the flight (one for things within
        /// 400 m, one for things beyond) is asked for one while there is smoke or a mark in its range,
        /// and put back as it was afterwards.
        /// </summary>
        void Depth(int which, bool needed)
        {
            if (!camerasLooked)
            {
                camerasLooked = true;
                foreach (Camera c in Camera.allCameras)
                {
                    if (c.name == "Camera 00") depthCamera[0] = c;
                    if (c.name == "Camera 01") depthCamera[1] = c;
                }
            }
            Camera camera = depthCamera[which];
            if (camera == null) return;
            if (needed && (camera.depthTextureMode & DepthTextureMode.Depth) == 0)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
                depthOurs[which] = true;
            }
            else if (!needed && depthOurs[which])
            {
                camera.depthTextureMode &= ~DepthTextureMode.Depth;
                depthOurs[which] = false;
            }
        }

        readonly Camera[] depthCamera = new Camera[2];
        readonly bool[] depthOurs = new bool[2];
        bool camerasLooked;

        void OnDestroy()
        {
            sunShadow.Dispose();
            Camera.onPreCull -= BeforeDrawing;
            Camera.onPreRender -= ForCamera;
            Camera.onPostRender -= AfterDrawing;
            if (jolted != null) { jolted.localRotation = jolted.localRotation * Quaternion.Inverse(joltTurn); jolted = null; }
            Depth(0, false);
            Depth(1, false);
            for (int which = 0; which < depthCamera.Length; which++)
            {
                if (halfOn && depthCamera[which] != null && halfCommands[which] != null) depthCamera[which].RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, halfCommands[which]);
                if (halfCommands[which] != null) halfCommands[which].Release();
            }
            halfOn = false;
            layers.Dispose();
#if DEV
            if (moverBall != null) Destroy(moverBall);
#endif
            NoShocks();
            if (shockCommands != null) shockCommands.Release();
            if (shockMaterial != null) Destroy(shockMaterial);
            if (picture != null) { picture.Release(); Destroy(picture); }
            foreach (Site s in sites) s.Dispose();
            sites.Clear();
            Spares.Drop();
            foreach (Scorch m in marks) m.Dispose();
            marks.Clear();
            if (Instance == this) { Instance = null; Alive = 0; }
        }

        public static void Newborn(Blast blast)
        {
            if (Instance != null) Instance.newborn.Add(blast);
        }

        /// <summary>How much of the normal number of particles a new explosion may have: fewer as the scene fills up.</summary>
        public static float Room() => Mathf.Clamp((Settings.MaxParticles - Alive) / (Settings.MaxParticles * 0.6f), 0.1f, 1f);

        IEnumerator AfterEachFrame()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                if (newborn.Count == 0) continue;
                // By now the game has put each new effect where its explosion is, and the notes on what
                // blew up are from this same frame, in the same coordinates.
                try
                {
                    foreach (Blast blast in newborn)
                    {
                        if (blast == null) continue;
                        Plan plan = Plan.Make((Vector3d)blast.transform.position, blast.strength);
                        plan.warm = WarmingNow;
                        LastPlan = plan.label ?? "";
                        // (what went off, for its own bang (Bangs): its charge taken as a tenth of the propellant that burns, as for liquid
                        // propellants going off on a pad, and no less than a part breaking up gives; near the ground if within its reach of it;
                        // its fire's roar as long as the fireball burns, some seconds for tonnes)
                        bool low = plan.surfaceBelow && (plan.surfacePoint - (Vector3d)blast.transform.position).magnitude < Math.Max(8.0, plan.reach * 1.5);
                        blast.Knows(Mathf.Max(0.1f * Mathf.Max(plan.fire, plan.charge), 0.4f + 6f * blast.strength * blast.strength), low, plan.radius > 0f ? Mathf.Clamp(0.45f * Mathf.Pow(Mathf.Max(plan.fire, 1f), 0.26f), 0.5f, 6f) : 0f);
                        Site at = SiteFor(plan);
                        at.Explode(plan);
                        if (warming) { warmSite = at; warmBlasts.Add(blast); }
                    }
                }
                catch (Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] could not set an explosion off: " + ex);
                    complained = true;
                }
                newborn.Clear();
            }
        }

        const int Most = 9;                 // patches of air at once: explosions' and engines' trails together

        Site SiteFor(Plan plan)
        {
            foreach (Site s in sites)
                if (s.Takes(plan)) return s;
            if (Staying() >= Most) LetOneGo();
            var site = Site.Make(plan);
            sites.Add(site);
            return site;
        }

        /// <summary>
        /// Make room for one more patch of air. Of the trails that engines have left and gone on from, the one left
        /// longest ago is let thin away, which takes it a couple of seconds, and meanwhile it does not count. Where
        /// there is no such trail, or more are thinning away already than there is any room for, the emptiest patch
        /// of all goes at once.
        /// </summary>
        void LetOneGo()
        {
            int staying = 0, flames = 0;
            Site oldest = null, cloud = null;
            foreach (Site s in sites)
            {
                if (s.Riding) { flames++; continue; }               // (the flames of ships are counted by themselves: see MostFlames)
                if (s.Leaving) continue;
                staying++;
                if (s.Vented && s.Unfed > 1f && (oldest == null || s.Unfed > oldest.Unfed)) oldest = s;
                if (s.Cloudy && (cloud == null || s.Count < cloud.Count)) cloud = s;
            }
            if (staying < Most) return;
            // (clouds give way first: they are the weather's to make again when there is room)
            if (cloud != null) { cloud.Leave(); return; }
            if (oldest != null && sites.Count - flames < Most + 3) { oldest.Leave(); return; }
            Site least = null;
            foreach (Site s in sites)
                if (!s.Riding && (least == null || s.Leaving && !least.Leaving || s.Leaving == least.Leaving && s.Count < least.Count)) least = s;
            if (least == null) return;
            least.Dispose();
            sites.Remove(least);
        }

        /// <summary>How many patches of air there are that count against the most there may be: not those thinning away, nor the flames of ships.</summary>
        int Staying()
        {
            int staying = 0;
            foreach (Site s in sites) if (!s.Leaving && !s.Riding) staying++;
            return staying;
        }

        float noAirUntil;

        /// <summary>
        /// What an engine is putting out this frame. Its flame goes into the patch of air that goes along with its ship;
        /// so does its smoke, where its owner says what ship it is on (see Exhaust.rides), while it is young (see
        /// SiteYoung), and otherwise straight into one that stays where it is. What of its jet goes down a launch pad's
        /// grate comes out at the ends of the pad's trench (see Pads, and Trenches).
        /// </summary>
        internal bool Take(in Exhaust exhaust)
        {
            if (Assets.Volume == null || !Settings.Volume) return false;
            float trench = 0f;
            if (exhaust.amount > 0.001f && exhaust.running > 0.02f && Pads.All.Count > 0 && Settings.TestYoung < 2f)
            {
                trench = Pads.Into(exhaust, out Pad pad);
                if (pad != null && trench > 0.01f) pad.Feed(exhaust, trench, Site.SmokeMade(exhaust, (float)exhaust.going.magnitude, SmokeQuality, out _, out _));
                else trench = 0f;
            }
            bool flame = exhaust.fire > 0.01f && exhaust.rides != null && TakeFlame(exhaust);
            bool smoke = false;
            if (exhaust.amount > 0.001f)
            {
                // (young smoke that goes down the grate goes there of itself, and is let go: see the mover)
                // (Only an engine's jet: what its owner says is something burning, dust or a spray goes straight into a patch that
                // stays where it is, where it is made as such (see Site.Vents), whatever ship it is on; its flame still goes along
                // with the ship.)
                bool jet = exhaust.kind == ExhaustKind.Engine || exhaust.kind == ExhaustKind.Unset;
                Site young = null;
                if (exhaust.rides != null && jet && Settings.TestYoung < 1f) smoke = TakeYoung(exhaust, out young);
                if (!smoke)
                {
                    Exhaust rest = exhaust;
                    rest.amount *= 1f - trench;
                    smoke = rest.amount > 0.001f && TakeSmoke(rest) || trench > 0.01f;
                }
                else if (Site.LaidStraight(exhaust))
                {
                    // (Going at any speed, its trail is laid straight into the air it leaves it in, as well: what its ship's
                    // patch makes of its smoke is then only what is seen of it near the ship. See Site.LaidStraight.)
                    Exhaust rest = exhaust;
                    rest.amount *= 1f - trench;
                    rest.young = Mathf.Max(young != null ? young.HandFar : 0f, Site.YoungFar(exhaust));
                    // (whether any patch has been laid its trail until now: where none has, its ship's patch has been making
                    // copies of its smoke for the air, and what is laid begins at the nozzle, where those leave off)
                    rest.begins = true;
                    foreach (Site s in sites) if (!s.Riding && s.Laying(exhaust.id)) { rest.begins = false; break; }
                    if (rest.amount > 0.001f) TakeSmoke(rest);
                }
            }
            return flame || smoke;
        }

        /// <summary>How many puffs smoke is made in, against the usual: fewer as the air fills up (see Site.Vents).</summary>
        public static float SmokeQuality => Mathf.Clamp(Settings.Quality * Room(), 0.15f, 1.5f);

        /// <summary>
        /// An engine's smoke while it is young: into the patch of air that goes along with its ship, with the ship's
        /// flames if it has any (begun if there is none, as for a flame). Where there is no air to hold smoke, nothing,
        /// and so too where as many ships have such patches as there may be (it then goes straight into a patch that
        /// stays where it is, as it did before there were these).
        /// </summary>
        bool TakeYoung(in Exhaust exhaust, out Site site)
        {
            site = null;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.atmosphere) return false;
            int riding = 0;
            int family = Site.Family(exhaust.tint);
            foreach (Site s in sites)
            {
                if (!s.Riding) continue;
                riding++;
                if (s.Rides == exhaust.rides && (site == null || s.FlameFamily == family)) site = s;
            }
            if (site == null)
            {
                if (exhaust.running <= 0.02f || riding >= MostFlames || Time.time < noAirUntil) return false;
                Plan plan = Plan.Where(exhaust.rim);
                if (plan.air < Site.TrailAir || plan.underwater) { noAirUntil = Time.time + 0.5f; return false; }
                try { site = Site.Make(plan, exhaust.rides); }
                catch (Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] could not begin an engine's young smoke: " + ex);
                    complained = true;
                    return false;
                }
                sites.Add(site);
            }
            if (site.Airless) return false;
            return site.Fed(exhaust);
        }

        /// <summary>
        /// (After every patch has stepped.) The young smoke each ship's patch has handed on, to the patch that holds its
        /// trail: the one it gave to last while that will still have it, or the newest that will, or one begun there.
        /// </summary>
        void Deliver()
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) return;
            for (int n = 0; n < sites.Count; n++)
            {
                Site s = sites[n];
                if (!s.Riding || s.Handing.Count == 0) continue;
                Site.Handoff first = s.Handing[0];
                Vector3d at = body.position + body.BodyFrame.LocalToWorld(first.at.xzy).xzy;
                Site to = s.TrailSite;
                if (to == null || to.Leaving || !sites.Contains(to) || !to.Keeps(body, at))
                {
                    to = null;
                    for (int m = sites.Count - 1; m >= 0 && to == null; m--)
                        if (!sites[m].Riding && sites[m].Keeps(body, at)) to = sites[m];
                    if (to == null)
                    {
                        Plan plan = Plan.Where(at);
                        if (plan.air < Site.TrailAir || plan.underwater) { s.Handing.Clear(); continue; }       // (no air there to hold it: it is let go)
                        plan.space = false;                                                                    // (an engine's trail high up is the air's, where it was laid, not a wreck's cloud flying on)
                        if (Staying() >= Most) LetOneGo();
                        try { to = Site.Make(plan); }
                        catch (Exception ex)
                        {
                            if (!complained) Debug.LogError("[VolumetricExplosions] could not begin an engine's trail: " + ex);
                            complained = true;
                            s.Handing.Clear();
                            continue;
                        }
                        sites.Add(to);
                    }
                    s.TrailSite = to;
                }
                to.Adopt(s.Handing, s.HandNozzle, s.ShipGoing);
                s.Handing.Clear();
            }
        }

        /// <summary>
        /// (After the engines have said what they are putting out.) Each launch pad whose trench is being fed, or is still
        /// emptying, gives its smoke to the patch of air over the pad: the one it gave to last, or one that will have it,
        /// or one begun there.
        /// </summary>
        void Trenches(float dt)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) return;
            foreach (Pad pad in Pads.All)
            {
                pad.End(dt);
                if (pad.fill <= 0f || pad.made <= 0f) continue;
                Site s = pad.site;
                if (s == null || s.Leaving || !sites.Contains(s) || s.Riding)
                {
                    s = null;
                    for (int m = sites.Count - 1; m >= 0 && s == null; m--)
                        if (!sites[m].Riding && sites[m].Keeps(body, (Vector3d)pad.middle)) s = sites[m];
                    if (s == null)
                    {
                        Plan plan = Plan.Where((Vector3d)pad.middle);
                        if (plan.vacuum || plan.underwater) continue;
                        if (Staying() >= Most) LetOneGo();
                        try { s = Site.Make(plan); }
                        catch (Exception ex)
                        {
                            if (!complained) Debug.LogError("[VolumetricExplosions] could not begin a pad's smoke: " + ex);
                            complained = true;
                            continue;
                        }
                        sites.Add(s);
                    }
                    pad.site = s;
                }
                s.Trench(pad, pad.nozzle);
            }
        }

        const int MostFlames = 4;                      // ships at once whose engines' flames are drawn

        /// <summary>An engine's flame: into the patch that goes along with its ship, begun if there is none. (In any air or none: a flame needs no air to be seen in.)</summary>
        bool TakeFlame(in Exhaust exhaust)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null) return false;
            Site site = null;
            int riding = 0;
            foreach (Site s in sites)
            {
                if (!s.Riding) continue;
                riding++;
                // (or one that has only held its young smoke so far: see TakeYoung)
                if (s.Rides == exhaust.rides && (s.FlameFamily == Site.Family(exhaust.tint) || !s.HasFlame)) site = s;
            }
            if (site == null)
            {
                if (exhaust.running <= 0.02f || riding >= MostFlames) return false;
                Plan plan = Plan.Where(exhaust.rim);
                if (plan.underwater) return false;
                try { site = Site.Make(plan, exhaust.rides); }
                catch (Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] could not begin an engine's flame: " + ex);
                    complained = true;
                    return false;
                }
                sites.Add(site);
            }
            return site.Fed(exhaust);
        }

        /// <summary>An engine's smoke for this frame: into the newest patch of air that it belongs in, or a new one begun where it is.</summary>
        bool TakeSmoke(in Exhaust exhaust)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.atmosphere) return false;
            // The newest patch that will have it; but none older than the one it has been feeding. (An older one that
            // reaches further would have it back, for a frame, and then not: its smoke went to and fro between them.)
            Site site = null;
            for (int n = sites.Count - 1; n >= 0 && site == null; n--)
            {
                if (sites[n].Feeds(body, exhaust)) site = sites[n];
                else if (!sites[n].Riding && sites[n].Has(exhaust.id)) break;      // (not the patch that goes along with its ship, which has its flame, and its smoke while that is young: that never holds its trail)
            }
            if (site == null)
            {
                if (exhaust.amount <= 0.001f || exhaust.running <= 0.02f) return false;
                // (where there turned out to be no air, it is not asked again at every frame: finding out takes a look at the ground and the air)
                if (Time.time < noAirUntil) return false;
#if DEV
                if (sites.Count > 0 && Settings.TestView == 8f) Debug.Log("[VolumetricExplosions] a new patch for engine " + exhaust.id + ": the newest would not have it: " + sites[sites.Count - 1].WhyNot(body, exhaust.rim));
#endif
                // (a cloud pushes nothing out: explosions and trails keep three patches' room of their own)
                if (exhaust.kind == ExhaustKind.Thermal && Staying() >= Most - 3) return false;
                Plan plan = Plan.Where(exhaust.rim);
                // (no air to hold smoke: for an engine's jet, the thinnest air its trail is laid in, see Site.TrailAir; for anything else, a vacuum's)
                bool jet = exhaust.kind == ExhaustKind.Engine || exhaust.kind == ExhaustKind.Unset;
                if ((jet ? plan.air < Site.TrailAir : plan.vacuum || plan.space) || plan.underwater) { noAirUntil = Time.time + 0.5f; return false; }
                if (jet) plan.space = false;
                if (Staying() >= Most) LetOneGo();
                try { site = Site.Make(plan); }
                catch (Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] could not begin an engine's smoke: " + ex);
                    complained = true;
                    return false;
                }
                sites.Add(site);
            }
            if (!site.Fed(exhaust)) return false;
            // (the patch it was feeding until a moment ago still has the head of its trail: see Passed)
            for (int n = sites.Count - 1; n >= 0; n--)
                if (sites[n] != site && sites[n].Vented) sites[n].Passed(exhaust);
            return true;
        }

        public void Mark(Plan plan, float radius)
        {
            if (!Settings.Scorch || Assets.Scorch == null) return;
            if (marks.Count >= 24) { marks[0].Dispose(); marks.RemoveAt(0); }
            marks.Add(new Scorch(plan, radius));
        }

        /// <summary>
        /// The lamps of the scene that may light smoke at night (see Site.TuneScene): its point lights and spotlights,
        /// but not those on ships (an engine's own light on its smoke is its fire's, see Site.VentLamp) and not ours.
        /// Looked for twice a second while there is smoke.
        /// </summary>
        public static readonly List<Light> SceneLights = new List<Light>();
        float lightsLooked = -10f;

        void LookForLights()
        {
            if (Time.unscaledTime - lightsLooked < 0.5f) return;
            lightsLooked = Time.unscaledTime;
            SceneLights.Clear();
            foreach (Light l in FindObjectsOfType<Light>())
            {
                if (l == null || !l.isActiveAndEnabled || !(l.intensity > 0.01f) || !(l.range > 0.5f) || l.range > 5000f || float.IsInfinity(l.intensity)) continue;
                // (a lamp with a colour below nothing takes light away: some mods use such lamps for shade, which is not light to put on smoke)
                Color c = l.color;
                if (!(c.r >= 0f && c.g >= 0f && c.b >= 0f) || c.r + c.g + c.b <= 0.001f) continue;
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                // (only lamps that light the world the smoke is in: the cabins drawn inside ships, and the planets seen from afar, have
                // lamps of their own near the scene's origin, which is where the ship being flown is)
                if ((l.cullingMask & ((1 << 15) | 1)) == 0) continue;
                Transform top = l.transform.root;
                if (top != null && top.name.StartsWith("VolumetricExplosions", StringComparison.Ordinal)) continue;
                if (l.GetComponentInParent<Part>() != null) continue;
                SceneLights.Add(l);
            }
        }

        float quietFor;

        void LateUpdate()
        {
            steppedFrame = Time.frameCount;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            clock.Restart();
            if (Cost[3] > 0) gridMs = Cost[3] * 1000f / System.Diagnostics.Stopwatch.Frequency;
            for (int n = 0; n < Cost.Length; n++) { shown[n] = Mathf.Lerp(shown[n], Cost[n] * 1000f / System.Diagnostics.Stopwatch.Frequency, 0.1f); Cost[n] = 0; }
            for (int n = 0; n < Part.Length; n++) { partShown[n] = Mathf.Lerp(partShown[n], Part[n] * 1000f / System.Diagnostics.Stopwatch.Frequency, 0.05f); Part[n] = 0; }
            for (int n = 0; n < FireParts.Length; n++) FireParts[n] = 0;
#if DEV
            Mark(-1);
#endif
            Pads.Frame();
            if (sites.Count > 0) LookForLights();
#if DEV
            long topFrom = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            // (what the next patches will take, made ahead while there are patches: see Spares)
            if (sites.Count > 0 && !warming) Site.TopUp();
#if DEV
            topMs = (System.Diagnostics.Stopwatch.GetTimestamp() - topFrom) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
#endif
            // (the names of textures sent once, asked for once nothing has been drawn for a few seconds: see Sending)
            quietFor = sites.Count > 0 || warming ? 0f : quietFor + dt;
            if (quietFor > 3f) Sending.Name(2);
#if DEV
            TestMover();
            TestBurn();
#endif
#if DEV
            Mark(0);
#endif
            Sources.Ask();
#if DEV
            Mark(1);
#endif
            Trenches(dt);
            // (the patches nearest the camera first in line for new grids: see Site.Relight)
            ranked.Clear();
            foreach (Site s in sites) if (s.Count > 0) ranked.Add(s);
            ranked.Sort(byAway);
            for (int k = 0; k < ranked.Count; k++) ranked[k].GridRank = k;
            int alive = 0;
#if DEV
            Mark(2);
#endif
            for (int n = sites.Count - 1; n >= 0; n--)
            {
                Site s = sites[n];
                try { s.Step(dt); }
                catch (Exception ex)
                {
                    if (!complained) Debug.LogError("[VolumetricExplosions] an explosion stopped: " + ex);
                    complained = true;
                    s.Dispose();
                    sites.RemoveAt(n);
                    continue;
                }
                alive += s.Count;
                if (s.Finished) { s.Dispose(); sites.RemoveAt(n); }
            }
#if DEV
            Mark(3);
#endif
            Deliver();
#if DEV
            Mark(4);
#endif
            // (the grids the patches showed this frame, sent by the render thread before anything is drawn: see Sending)
            Sending.Flush();
#if DEV
            Mark(5);
#endif
            Alive = alive;
            if (Assets.Volume != null || Assets.Mark != null || Assets.Shock != null)
            {
                bool near = false, far = false;
                if (Assets.Volume != null)
                    foreach (Site s in sites)
                        if (s.Count > 0) { near = true; if (s.Farthest > 360f) far = true; }
                if (Assets.Shock != null)
                    foreach (Site s in sites)
                        if (s.WaveCount > 0) near = true;       // (a front leaves alone what stands in front of it)
                if (Assets.Mark != null && marks.Count > 0)
                {
                    Camera eye = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
                    if (eye != null)
                    {
                        Vector3 from = eye.transform.position;
                        foreach (Scorch m in marks)
                        {
                            float away = m.Away(from);
                            if (away < 460f) near = true;
                            if (away > 340f && away < 4000f) far = true;
                        }
                    }
                }
                Depth(0, near);
                Depth(1, far);
            }
            long lap = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int n = marks.Count - 1; n >= 0; n--)
                if (!marks[n].Step(dt)) { marks[n].Dispose(); marks.RemoveAt(n); }
            Lap(15, ref lap);
            LastMs = (float)clock.Elapsed.TotalMilliseconds;
#if DEV
            Mark(6);
            if (LastMs > 10f)
            {
                string[] what = { "before", "asked", "trenches", "steps", "shown", "sent", "the rest" };
                int collections = GC.CollectionCount(0);
                var t = new System.Text.StringBuilder("a frame of the air's took " + LastMs.ToString("F1") + " ms (" + sites.Count + " patches, frame " + Time.frameCount + ", top-up " + topMs.ToString("F1") + " ms, collections " + (collections - collectionsWere) + " since the last such frame):");
                collectionsWere = collections;
                for (int k = 0; k < what.Length; k++) t.Append(' ').Append(what[k]).Append(' ').Append(((frameMarks[k + 1] - frameMarks[k]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1"));
                for (int n = 0; n < Part.Length; n++) { double d = Part[n] * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (d >= 1.0) t.Append("; ").Append(partName[n]).Append(' ').Append(d.ToString("F1")); }
                string[] fireNames = { "fires", "frags", "vents", "ends" };
                t.Append(" (of fires:");
                for (int n = 0; n < 4; n++) t.Append(' ').Append(fireNames[n]).Append(' ').Append((FireParts[n] * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1"));
                t.Append(')');
                Addon.Log(t.ToString());
            }
#endif
        }

#if DEV
        // (for the development build: where a slow frame of the air's went, see the end of LateUpdate)
        static readonly long[] frameMarks = new long[8];
        static double topMs;
        static int collectionsWere;
        static void Mark(int k) => frameMarks[k + 1] = System.Diagnostics.Stopwatch.GetTimestamp();
#endif

        /// <summary>
        /// Just before anything is drawn, put each cloud where its patch of ground (or its wreck) is now.
        /// The game moves its whole world about under the camera; done this late, the clouds never lag it.
        /// </summary>
        void BeforeDrawing(Camera camera)
        {
            // Not for the pictures the game draws for its interface (a kerbal's portrait, every few frames): those are
            // drawn in the middle of the frame, before anything has been moved. Done then, and so not again for the
            // cameras that draw the flight, the clouds were a frame behind the world every few frames, and smoke
            // drawn at half size was drawn where its box had been.
            if (camera.targetTexture != null || steppedFrame != Time.frameCount) return;
            if (placedFrame == Time.frameCount) return;
            // Nor before the flight's own cameras: on the first of them to draw (the far one, else the near one), not on one drawn
            // before them (the far planets' and the sky's, which draws nothing of this mod's). The Smooth Running mod carries the
            // craft on to the frame's moment just before the first camera that draws the frame, and of its hook and this one
            // whichever was made first went first: placed before it, every patch riding a craft was laid where the craft's last
            // physics step had left it, behind the craft as drawn by up to a step and jumping as the steps fell between frames. A
            // craft coming in from orbit left its re-entry glow lengths behind it (the owner, 2026-10-20, in the development build's
            // films, whose copies are swapped in while the game runs, hooks and all). The craft are put back only after the near
            // camera has drawn.
            if (!camerasLooked) Depth(0, depthOurs[0]);
            if (camera != depthCamera[0] && camera != depthCamera[1] && (depthCamera[0] != null || depthCamera[1] != null)) return;
            placedFrame = Time.frameCount;
            long lap = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (Site s in sites) s.Place();
            foreach (Scorch m in marks) m.Place();
            Vector3 eyeAt = depthCamera[0] != null ? depthCamera[0].transform.position : camera.transform.position;
            sunShadow.Frame(sites, eyeAt);
            foreach (Site s in sites) s.SeeFlashes(eyeAt, sites);
            Halves();
            if (Layered) layers.Frame(sites, depthCamera, depthCamera[0] != null ? depthCamera[0].transform.position : camera.transform.position);
            else layers.Off();
            Shake();
            Shocks();
            Lap(14, ref lap);
        }

        float joltLeft, joltOf;
        Transform jolted;
        Quaternion joltTurn = Quaternion.identity;
        const float JoltLasts = 0.5f;

        /// <summary>
        /// A shock front has reached the camera: shake it, by at most so many degrees, for half a second,
        /// and have the whole picture give and spring back (see Shocks).
        /// </summary>
        public void Jolt(float degrees)
        {
            degrees *= Settings.Bend;
            if (!Settings.Shake || degrees < 0.03f) return;
            if (degrees < joltOf * joltLeft / JoltLasts) return;         // (a stronger one is still going)
            joltOf = degrees;
            joltLeft = JoltLasts;
            pulseOf = Mathf.Min(0.08f, 0.06f * degrees);
            pulseLeft = PulseLasts;
        }

        float pulseLeft, pulseOf;
        const float PulseLasts = 0.4f;

        /// <summary>How far the picture is drawn in from its corners just now, in halves of the screen (so, less than nothing): the picture swells, and bounces back to rest.</summary>
        float Pulse()
        {
            if (pulseLeft <= 0f) return 0f;
            pulseLeft -= Time.deltaTime;
            if (pulseLeft <= 0f) return 0f;
            float t = PulseLasts - pulseLeft;
            // (never outward: there is no picture beyond the edge of the screen to draw in)
            return -pulseOf * Mathf.Exp(-t / 0.11f) * (0.5f + 0.5f * Mathf.Cos(t * (2f * Mathf.PI / 0.16f))) * Mathf.Min(1f, pulseLeft / 0.1f);
        }

        /// <summary>
        /// The shake is put on the flight camera just before it draws and taken off again as soon as it has
        /// (see AfterDrawing), so that the game's own handling of the camera never sees it.
        /// </summary>
        void Shake()
        {
            if (joltLeft <= 0f) return;
            joltLeft -= Time.deltaTime;
            if (joltLeft <= 0f || FlightCamera.fetch == null || MapView.MapIsEnabled) { joltLeft = 0f; return; }
            float left = joltLeft / JoltLasts, t = (JoltLasts - joltLeft) * 22f + 3.7f;
            float size = joltOf * left * left;
            joltTurn = Quaternion.Euler((Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 2f * size, (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 2f * size, (Mathf.PerlinNoise(t, t * 0.6f) - 0.5f) * size);
            jolted = FlightCamera.fetch.transform;
            jolted.localRotation = jolted.localRotation * joltTurn;
        }

        void AfterDrawing(Camera camera)
        {
            if (jolted == null || camera.name != "Camera 00") return;
            jolted.localRotation = jolted.localRotation * Quaternion.Inverse(joltTurn);
            jolted = null;
        }

        /// <summary>A shock front as it is drawn: a ball of this radius about this place, pushing the picture so far (in heights of the screen) where it is seen edge-on.</summary>
        public struct Front { public Vector3 middle, up; public float radius, push; public bool ground; public Vector4 cloud; public Vector3 sun, sunLight, sky, fire; }      // (cloud: see Site.Condense; 0, it leaves none)

        CommandBuffer shockCommands;
        Camera shockCamera;
        Material shockMaterial;
        RenderTexture picture;
        readonly List<Front> fronts = new List<Front>();
        static readonly int[] frontMiddle = Names("_ShockC"), frontPush = Names("_ShockP"), frontGround = Names("_ShockG"), frontCloud = Names("_ShockW");

        static int[] Names(string name)
        {
            var ids = new int[4];
            for (int n = 0; n < ids.Length; n++) ids[n] = Shader.PropertyToID(name + n);
            return ids;
        }

        static Vector4 With(Vector3 v, float w) => new Vector4(v.x, v.y, v.z, w);

        void NoShocks()
        {
            if (shockCamera != null) { shockCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, shockCommands); shockCamera = null; }
        }

        /// <summary>
        /// The shock front of a blast bends the picture behind it. So it is drawn last of all by the camera
        /// for near things: once that camera has drawn everything else, the picture as it stands is copied,
        /// and drawn again from the copy with each pixel read from a little to one side, where a front is
        /// (see tools/shaderpack/shock.glsl). The four fronts that show most are drawn.
        /// </summary>
        void Shocks()
        {
            if (Assets.Shock == null) return;
            fronts.Clear();
            foreach (Site s in sites) s.Fronts(fronts);
            float pulse = Pulse();
            Depth(0, depthOurs[0]);                              // (only to have the cameras looked up)
            Camera camera = depthCamera[0];
            if ((fronts.Count == 0 && pulse == 0f) || camera == null || MapView.MapIsEnabled) { NoShocks(); return; }
            if (shockCommands == null)
            {
                shockCommands = new CommandBuffer { name = "VolumetricExplosions shock fronts" };
                shockMaterial = new Material(Assets.Shock);
                shockMaterial.SetVector("_ShockSheet", new Vector4(1f, 1f, 0.5f, 1f));
            }
            // (kept in the camera's own range of brightness: other mods have it draw brighter than white, to glow afterwards)
            RenderTextureFormat kind = camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            if (picture == null || picture.width != camera.pixelWidth || picture.height != camera.pixelHeight || picture.format != kind)
            {
                if (picture != null) { picture.Release(); Destroy(picture); }
                picture = new RenderTexture(camera.pixelWidth, camera.pixelHeight, 0, kind) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "VolumetricExplosions picture" };
                shockMaterial.SetTexture("_VolScene", picture);
            }

            // What the shader needs to know of the camera: where it is and which way each pixel looks, and how to read how far off what it has drawn is.
            Transform lens = camera.transform;
            float tall = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), wide = tall * camera.aspect;
            shockMaterial.SetVector("_ShockEye", lens.position);
            shockMaterial.SetVector("_ShockRight", With(lens.right * wide, wide));
            shockMaterial.SetVector("_ShockUp", With(lens.up * tall, tall));
            shockMaterial.SetVector("_ShockAhead", With(lens.forward, camera.pixelHeight / (float)Mathf.Max(1, camera.pixelWidth)));
            float near = camera.nearClipPlane, far = camera.farClipPlane;
            bool backwards = SystemInfo.usesReversedZBuffer;
            shockMaterial.SetVector("_ShockDepth", new Vector4((backwards ? far / near - 1f : 1f - far / near) / far, (backwards ? 1f : far / near) / far,
                (camera.depthTextureMode & DepthTextureMode.Depth) != 0 ? 1f : 0f, backwards ? 0f : 1f));
            // (one that leaves a cloud behind it before any that does not, however faint its bending now)
            fronts.Sort((a, b) => (b.push + (b.cloud.x > 0f ? 1f : 0f)).CompareTo(a.push + (a.cloud.x > 0f ? 1f : 0f)));
            bool lit = false;
            for (int n = 0; n < frontMiddle.Length; n++)
            {
                if (n >= fronts.Count) { shockMaterial.SetVector(frontMiddle[n], Vector4.zero); shockMaterial.SetVector(frontCloud[n], Vector4.zero); continue; }
                Front f = fronts[n];
                shockMaterial.SetVector(frontCloud[n], f.cloud);
                if (f.cloud.x > 0f && !lit)
                {
                    shockMaterial.SetVector("_ShockSun", f.sun);
                    shockMaterial.SetVector("_ShockSunLight", With(f.sunLight, 1.5f));
                    shockMaterial.SetVector("_ShockSky", f.sky);
                    shockMaterial.SetVector("_ShockFire", f.fire);
                    lit = true;
                }
                shockMaterial.SetVector(frontMiddle[n], With(f.middle, f.radius));
                // A front is a seventh as wide as it has spread, but never looks narrower than an eighth of the screen's height: it has to be seen.
                shockMaterial.SetVector(frontPush[n], new Vector4(f.push, Mathf.Clamp(0.14f * f.radius, 2.5f, 20f), 0.12f, Mathf.Min(0.07f, 4f * f.push)));
                shockMaterial.SetVector(frontGround[n], With(f.up, f.ground ? 1.2f * f.push : 0f));
            }
            shockMaterial.SetVector("_ShockPulse", new Vector4(pulse, 0.04f, 0f, 0f));

            shockCommands.Clear();
            shockCommands.Blit(BuiltinRenderTextureType.CameraTarget, picture);
            shockCommands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            shockCommands.DrawMesh(Assets.Sheet, Matrix4x4.identity, shockMaterial, 0, 0);
            if (shockCamera != camera)
            {
                NoShocks();
                camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, shockCommands);
                shockCamera = camera;
            }
        }

#if DEV
        // (for finding a fault: what the game's picture of how far each pixel has moved since the last frame, which the
        // smoothing between frames goes by, has in it. "arm" starts taking a copy of it each frame; anything else reads
        // the copy: how many of its dots say something moved, where on the screen they lie, and how far at the most.)
        static RenderTexture motionCopy;
        static CommandBuffer motionGrab;
        static Camera motionCam;
        public static string Motion(string arg)
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return "no camera";
            if (arg == "off")
            {
                if (motionCam != null && motionGrab != null) motionCam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, motionGrab);
                motionCam = null;
                return "off";
            }
            if (arg == "arm")
            {
                if (motionCopy == null) { motionCopy = new RenderTexture(Mathf.Max(8, cam.pixelWidth / 4), Mathf.Max(8, cam.pixelHeight / 4), 0, RenderTextureFormat.ARGBFloat) { filterMode = FilterMode.Point }; motionCopy.Create(); }
                if (motionGrab == null) { motionGrab = new CommandBuffer { name = "VolumetricExplosions: a copy of the motion picture (test)" }; motionGrab.Blit(BuiltinRenderTextureType.MotionVectors, motionCopy); }
                if (motionCam != cam) { if (motionCam != null) motionCam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, motionGrab); cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, motionGrab); motionCam = cam; }
                return "armed on " + cam.name + "; it keeps " + cam.depthTextureMode;
            }
            if (motionCopy == null) return "arm first";
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = motionCopy;
            var read = new Texture2D(motionCopy.width, motionCopy.height, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, motionCopy.width, motionCopy.height), 0, 0);
            read.Apply();
            RenderTexture.active = was;
            Color[] px = read.GetPixels();
            Destroy(read);
            int w = motionCopy.width, h = motionCopy.height, moved = 0, x0 = w, x1 = -1, y0 = h, y1 = -1;
            float most = 0f; double sum = 0.0, sumX = 0.0, sumY = 0.0;
            for (int i = 0; i < px.Length; i++)
            {
                float m = Mathf.Sqrt(px[i].r * px[i].r + px[i].g * px[i].g);
                if (m < 2e-4f) continue;
                moved++; sum += m; sumX += px[i].r; sumY += px[i].g; if (m > most) most = m;
                int x = i % w, y = i / w;
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
            string said = moved + " of " + px.Length + " dots say something moved";
            if (moved > 0) said += ": between " + (x0 / (float)w).ToString("F2") + ", " + (y0 / (float)h).ToString("F2") + " and " + (x1 / (float)w).ToString("F2") + ", " + (y1 / (float)h).ToString("F2") + " of the picture (from its lower left); by " + (most * cam.pixelWidth).ToString("F2") + " pixels at the most, " + (sum / moved * cam.pixelWidth).ToString("F2") + " on the whole (" + (sumX / moved * cam.pixelWidth).ToString("F2") + " across and " + (sumY / moved * cam.pixelHeight).ToString("F2") + " up, on the whole)";
            // (and where each patch's box lies on the screen, to set beside it)
            if (Instance != null)
                foreach (Site s in Instance.sites)
                {
                    Vector4 r = new Vector4(2f, 2f, -2f, -2f);
                    Matrix4x4 m = cam.projectionMatrix * cam.worldToCameraMatrix * s.BoxMatrix;
                    for (int n = 0; n < 8; n++)
                    {
                        Vector4 c = m * new Vector4((n & 1) - 0.5f, ((n >> 1) & 1) - 0.5f, (n >> 2) - 0.5f, 1f);
                        if (c.w <= 0.01f) continue;
                        r.x = Mathf.Min(r.x, c.x / c.w); r.y = Mathf.Min(r.y, c.y / c.w); r.z = Mathf.Max(r.z, c.x / c.w); r.w = Mathf.Max(r.w, c.y / c.w);
                    }
                    said += " | a patch's box: " + (r.x * 0.5f + 0.5f).ToString("F2") + ", " + (r.y * 0.5f + 0.5f).ToString("F2") + " to " + (r.z * 0.5f + 0.5f).ToString("F2") + ", " + (r.w * 0.5f + 0.5f).ToString("F2") + (s.Riding ? " (rides)" : "");
                    said += "; " + s.FollowSays;
                }
            return said;
        }

        Site moverSite;
        Vector3 moverFrom;
        float moverStarted = -1f;
        GameObject moverBall;

        /// <summary>
        /// (The wind a weather mod gives the air (Sources.Wind) over the ground under the craft being flown, every quarter of an hour
        /// for the next six: at 2 m, 10 m and 20 m up, as it answers (its own way of holding the wind back near the ground in it), and
        /// its gusts at 10 m.)
        /// </summary>
        public static string WindDay()
        {
            if (Sources.Wind == null) return "no weather wind: Sources.Wind is not set";
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no craft";
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            double above = v.heightFromTerrain > 0f ? v.heightFromTerrain : v.altitude - v.terrainAltitude;
            Vector3d ground = v.CoMD - up * above;
            double now = Planetarium.GetUniversalTime();
            var say = new System.Text.StringBuilder("hours from now: the weather's wind at 2 m, 10 m, 20 m (gusts at 10 m), m/s\n");
            for (int k = 0; k <= 24; k++)
            {
                double ut = now + k * 900.0;
                say.Append((k * 0.25f).ToString("F2")).Append(" h:");
                float gusts = 0f;
                foreach (double h in new[] { 2.0, 10.0, 20.0 })
                {
                    Sources.Breeze b;
                    try { b = Sources.Wind(ground + up * h, ut); }
                    catch (Exception ex) { return say.Append(" failed: ").Append(ex.Message).ToString(); }
                    Vector3d level = b.wind - up * Vector3d.Dot(b.wind, up);
                    say.Append(" ").Append(level.magnitude.ToString("F1"));
                    if (h == 10.0) gusts = b.gusts;
                }
                say.Append(" (").Append(gusts.ToString("F1")).Append(")\n");
            }
            return say.ToString();
        }

        /// <summary>
        /// (For seeing a fire on a ship, Settings.TestBurn: a fire this wide, a radius in metres, burning on the side of the craft
        /// being flown, half way up its root part, told to the air as another mod tells of a burning wreck: as an engine's jet
        /// (TestBurnKind 1, which is what such a fire was taken for until 2026-10-10) or as something burning (2).)
        /// </summary>
        void TestBurn()
        {
            if (Settings.TestBurn <= 0f) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.rootPart == null) return;
            Vector3d up = (v.CoMD - v.mainBody.position).normalized;
            // (on the hull at the height of the craft's middle, on the side towards the camera, so that it can be seen where it
            // stands: where a line from the camera's side to the middle first meets one of the craft's own parts)
            Vector3d toCamera = FlightCamera.fetch != null ? (Vector3d)FlightCamera.fetch.mainCamera.transform.position - v.CoMD : (Vector3d)v.transform.right;
            toCamera -= up * Vector3d.Dot(toCamera, up);
            Vector3 side = (Vector3)toCamera.normalized;
            Vector3d rim = v.CoMD + toCamera.normalized * 0.6;
            foreach (RaycastHit hit in Physics.RaycastAll((Vector3)v.CoMD + side * 20f, -side, 20f, ~0, QueryTriggerInteraction.Ignore))
            {
                Part part = hit.collider != null ? hit.collider.GetComponentInParent<Part>() : null;
                if (part != null && part.vessel == v && ((Vector3d)hit.point - v.CoMD).sqrMagnitude > (rim - v.CoMD).sqrMagnitude) rim = (Vector3d)hit.point;
            }
            var e = new Exhaust
            {
                id = 777001, rim = rim, along = up, going = (Vector3d)v.rb_velocityD + Krakensbane.GetFrameVelocity(), nozzle = Settings.TestBurn, flame = 4.5f * Settings.TestBurn,
                running = 0.35f, amount = 0.6f, shade = 0.05f, warm = 0.3f, lasts = 16f, tint = 0, fire = 0.8f, rides = v.transform,
                kind = Settings.TestBurnKind >= 2f ? ExhaustKind.Burning : ExhaustKind.Engine,
            };
            Take(e);
        }

        /// <summary>(Where the smoke of the newest patch that stays where it is lies on the whole: see Site.Middle.)</summary>
        public static string Cloud()
        {
            if (Instance == null) return "no air";
            for (int n = Instance.sites.Count - 1; n >= 0; n--)
                if (!Instance.sites[n].Riding && Instance.sites[n].Count > 0) return "patch " + n + ": " + Instance.sites[n].MiddleNow();
            return "no cloud";
        }

        /// <summary>
        /// (For seeing wakes, Settings.TestMover.) A ball sent east at that speed through the middle of the newest cloud (where its
        /// smoke is on the whole), from 70 m short of it to 70 m beyond, again and again, said to the air as another mod's piece of
        /// wreckage is (see Movers), and drawn as a dark ball so it can be seen.
        /// </summary>
        void TestMover()
        {
            float speed = Settings.TestMover;
            if (speed <= 0f)
            {
                if (moverBall != null) { Destroy(moverBall); moverBall = null; }
                moverSite = null; moverStarted = -1f;
                return;
            }
            if (moverSite == null || !sites.Contains(moverSite) || Time.time - moverStarted > 140f / speed)
            {
                moverSite = null;
                for (int n = sites.Count - 1; n >= 0 && moverSite == null; n--)
                    if (!sites[n].Riding && sites[n].Shown && sites[n].Count > 0) moverSite = sites[n];
                if (moverSite == null) return;
                moverFrom = moverSite.Middle(out Vector3 _) - new Vector3(70f, 0f, 0f);
                moverStarted = Time.time;
            }
            Vector3 local = moverFrom + new Vector3(speed * (Time.time - moverStarted), 0f, 0f);
            Vector3d at = moverSite.World(local);
            Vector3d east = moverSite.World(local + Vector3.right) - at;
            float size = Mathf.Max(0.05f, Settings.TestMoverSize);
            Movers.Moving(at, east * speed, size);
            if (moverBall == null)
            {
                moverBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                moverBall.name = "VolumetricExplosions test mover";
                Destroy(moverBall.GetComponent<Collider>());
                moverBall.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.12f, 0.11f, 0.1f) };
            }
            moverBall.transform.position = (Vector3)at;
            moverBall.transform.localScale = Vector3.one * (2f * size);
        }

        /// <summary>(Whether the newest patch of air is drawn where it is reckoned to be: see Site.Handed.)</summary>
        public static string Handed() => Instance == null || Instance.sites.Count == 0 ? "no patch of air" : Instance.sites[Instance.sites.Count - 1].Handed();
        /// <summary>(Every patch that engines are feeding, and where its newest puffs are against the first running nozzle of the craft being flown.)</summary>
        public static string Trail()
        {
            if (Instance == null || FlightGlobals.ActiveVessel == null) return "nothing";
            Vector3d nozzle = (Vector3d)FlightGlobals.ActiveVessel.transform.position;
            foreach (Part part in FlightGlobals.ActiveVessel.parts)
            {
                ModuleEngines e = part.FindModuleImplementing<ModuleEngines>();
                if (e != null && e.finalThrust > 0f && e.thrustTransforms.Count > 0) { nozzle = (Vector3d)e.thrustTransforms[0].position; break; }
            }
            var say = new System.Text.StringBuilder();
            for (int n = Instance.sites.Count - 1; n >= 0 && n >= Instance.sites.Count - 3; n--)
                if (Instance.sites[n].Vented) say.Append("patch ").Append(n).Append(" (unfed ").Append(Instance.sites[n].Unfed.ToString("F2")).Append(" s): ").Append(Instance.sites[n].Trail(nozzle)).Append("\n");
            return say.ToString();
        }

        /// <summary>(The launch pads found, and what is going down each one's trench.)</summary>
        public static string PadsNow()
        {
            var say = new System.Text.StringBuilder(Pads.All.Count + " pads");
            foreach (Pad pad in Pads.All)
                say.Append("\n").Append(pad.fx != null ? pad.fx.transform.parent.parent.name : "?").Append(": grate ").Append((2f * pad.halfA).ToString("F1")).Append(" x ").Append((2f * pad.halfB).ToString("F1"))
                   .Append(" m, ").Append(pad.ends.Length).Append(" ends, made ").Append(pad.made.ToString("F1")).Append(", fill ").Append(pad.fill.ToString("F2")).Append(", strongest ").Append(pad.strongest.ToString("F2"))
                   .Append(pad.site != null ? ", into a patch of " + pad.site.Count : "");
            return say.ToString();
        }

#if DEV
        /// <summary>(Each patch's own lights on the scene: see Site.LightsSaid. And how many lights the game draws for every dot.)</summary>
        public static string FireLights()
        {
            if (Instance == null) return "not running";
            var say = new System.Text.StringBuilder("pixel lights " + QualitySettings.pixelLightCount);
            foreach (Site s in Instance.sites) say.Append("\n").Append(s.LightsSaid());
            return say.ToString();
        }
#endif

#if DEV
        /// <summary>(What the sun's view of the smoke was taken over last: see SunShadow.)</summary>
        public static string SunShadowNow() => SunShadow.Said + (Instance != null ? "; " + Instance.sunShadow.Probe() : "");
        public static string SunBlock(string arg) => Instance != null ? Instance.sunShadow.Block(arg) : "not running";
#if DEV
        /// <summary>(Development build: blasts push craft or not, for this run: "on", "off"; the settings file is not touched.)</summary>
        public static string Pushes(string v) { Settings.Knock = v == "on"; return Settings.Knock ? "blasts push craft" : "blasts push nothing"; }
#endif
#endif

        /// <summary>(The lamps of the scene that may light smoke, as last looked for.)</summary>
        public static string LightsNow()
        {
            var say = new System.Text.StringBuilder(SceneLights.Count + " lamps");
            foreach (Light l in SceneLights)
                if (l != null) say.Append("\n").Append(l.name).Append(" (").Append(l.transform.root.name).Append("): ").Append(l.type).Append(", intensity ").Append(l.intensity.ToString("G4")).Append(", range ").Append(l.range.ToString("G4"))
                                  .Append(", colour ").Append(l.color.ToString()).Append(l.type == LightType.Spot ? ", cone " + l.spotAngle.ToString("F0") : "").Append(", ").Append(l.renderMode);
            return say.ToString();
        }

        /// <summary>(What has gone through the smoke of each patch: the things followed ahead, the puffs split, the wakes. See Wakes.cs.)</summary>
        public static string Wakes()
        {
            if (Instance == null) return "no air";
            var say = new System.Text.StringBuilder();
            for (int n = 0; n < Instance.sites.Count; n++) say.Append("patch ").Append(n).Append(": ").Append(Instance.sites[n].WakesNow()).Append("\n");
            return say.ToString();
        }

        /// <summary>(Each ship's patch: its young smoke and flame, and what it hands on.)</summary>
        public static string Young()
        {
            if (Instance == null) return "no air";
            var say = new System.Text.StringBuilder();
            foreach (Site s in Instance.sites) say.Append(s.YoungNow()).Append("\n");
            return say.ToString();
        }

        /// <summary>(Every puff of smoke there is, in every patch, against the first running nozzle of the craft being flown and the way it is going: written to a file, see Site.Column.)</summary>
        public static string Column()
        {
            Vessel ship = FlightGlobals.ActiveVessel;
            if (Instance == null || ship == null) return "nothing";
            Vector3d nozzle = (Vector3d)ship.transform.position;
            foreach (Part part in ship.parts)
            {
                ModuleEngines e = part.FindModuleImplementing<ModuleEngines>();
                if (e != null && e.finalThrust > 0f && e.thrustTransforms.Count > 0) { nozzle = (Vector3d)e.thrustTransforms[0].position; break; }
            }
            Vector3d going = ship.srf_velocity;
            if (going.sqrMagnitude < 1.0) return "the ship is not moving";
            var say = new System.Text.StringBuilder();
            say.Append("# speed ").Append(going.magnitude.ToString("F1")).Append(" dt ").Append(Time.deltaTime.ToString("F4")).Append(" altitude ").Append(ship.altitude.ToString("F0")).Append("\n");
            for (int n = 0; n < Instance.sites.Count; n++) Instance.sites[n].Column(say, n, nozzle, -going.normalized);
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vfx_column.txt");
            System.IO.File.WriteAllText(path, say.ToString());
            return path;
        }

        /// <summary>For the development build: how completely the smoke hid what was behind it in the last frame (see Site.Opacity).</summary>
        public static string Opacity()
        {
            if (Instance == null || Instance.sites.Count == 0) return "no explosion";
            var text = new System.Text.StringBuilder();
            foreach (Site s in Instance.sites) text.Append(s.Opacity()).Append("; ");
            return text.ToString();
        }

        /// <summary>For the development build: the particles of the first site there is, written to a file in the system's folder for such things (see Site.Dump).</summary>
        public static string Dump()
        {
            if (Instance == null || Instance.sites.Count == 0) return "no explosion";
            return Instance.sites[0].Dump(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vfx_particles.txt"));
        }

        /// <summary>For the development build: a grid made both ways for every site there is, and how far apart the two come out (see Site.NativeCheck).</summary>
        public static string NativeCheck()
        {
            if (Instance == null) return "not running";
            var text = new System.Text.StringBuilder();
            foreach (Site s in Instance.sites) text.Append(s.NativeCheck()).Append("; ");
            return text.Length > 0 ? text.ToString() : "no explosion";
        }

        /// <summary>For the development build: keep the picture of the first site's smoke, and (Differ) say how the picture now differs from it (see Site.Keep).</summary>
        public static string Keep() => Instance == null || Instance.sites.Count == 0 ? "no explosion" : Instance.sites[0].Keep();
        public static string Differ() => Instance == null || Instance.sites.Count == 0 ? "no explosion" : Instance.sites[0].Differ();

        /// <summary>For the development build: how long the billows' rounds are, and how far neighbouring smoke agrees about them (see Site.DescribeTurns).</summary>
        public static string Turns()
        {
            if (Instance == null) return "not running";
            var text = new System.Text.StringBuilder();
            foreach (Site s in Instance.sites) text.Append(s.DescribeTurns()).Append("; ");
            return text.Length > 0 ? text.ToString() : "no explosion";
        }

        /// <summary>For the development build: the ground as each site has it, against a careful sounding of the same points (see Site.CheckGround).</summary>
        public static string Ground()
        {
            if (Instance == null) return "not running";
            var text = new System.Text.StringBuilder();
            foreach (Site s in Instance.sites) text.Append(s.CheckGround()).Append("; ");
            return text.Length > 0 ? text.ToString() : "no explosion";
        }
#endif

        /// <summary>For the development build: how much of the smoke is inside solid things just now.</summary>
#if DEV
        static Light testLight;
        /// <summary>(for finding out: a point light "east north up intensity range [r g b]" metres from the craft, over the ground there; "off" takes it away)</summary>
        public static string TestLight(string arg)
        {
            if (arg == "off") { if (testLight != null) Destroy(testLight.gameObject); testLight = null; return "off"; }
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no craft";
            string[] w = arg.Split(' ');
            float e = float.Parse(w[0]), n = float.Parse(w[1]), u = float.Parse(w[2]), i = float.Parse(w[3]), range = float.Parse(w[4]);
            CelestialBody b = v.mainBody;
            Vector3d up = (v.CoMD - b.position).normalized, east = Vector3d.Cross(b.angularVelocity.normalized, up).normalized;
            if (east.sqrMagnitude < 0.5) east = Vector3d.Cross(Vector3d.up, up).normalized;
            Vector3d north = Vector3d.Cross(up, east);
            Vector3d at = v.CoMD + east * e + north * n;
            double ground = b.TerrainAltitude(b.GetLatitude(at), b.GetLongitude(at), true);
            at = b.GetWorldSurfacePosition(b.GetLatitude(at), b.GetLongitude(at), ground + u);
            if (testLight == null) { var go = new GameObject("VolumetricExplosions test light"); testLight = go.AddComponent<Light>(); testLight.type = LightType.Point; testLight.shadows = LightShadows.None; }
            testLight.transform.position = at;
            testLight.intensity = i; testLight.range = range;
            testLight.color = w.Length >= 8 ? new Color(float.Parse(w[5]), float.Parse(w[6]), float.Parse(w[7])) : Color.red;
            testLight.renderMode = LightRenderMode.ForcePixel;
            return "light at " + at + ", ground " + ground.ToString("F1") + " m, pixel lights allowed " + QualitySettings.pixelLightCount;
        }
#endif

        public static string Inside()
        {
            if (Instance == null) return "not running";
            int tested = 0, inside = 0;
            foreach (Site s in Instance.sites) { inside += s.Inside(out int some); tested += some; }
            return inside + " of " + tested;
        }

        /// <summary>For the development build: what is going on, in one line.</summary>
        // A trial for Natural Weather's rework (2026-10-11): a heap of cloud made as smoke is made. Thermals (ExhaustKind.Thermal)
        // hung over the ship: one broad one and four lesser round it, as a heap is a cluster of turrets. Called again: off.
        // (The user, of the noise clouds: "I don't think they look realistic because of how they're made. Our volumetric
        // explosions: the smoke looks almost real.")
        static bool thermals;
        public static float ThermalHigh = 450f, ThermalWide = 110f, ThermalDeep = 380f, ThermalAmount = 1f;
        public static string Thermals()
        {
            thermals = !thermals;
            if (thermals) Sources.Asked += FeedThermals; else Sources.Asked -= FeedThermals;
            return thermals ? "thermals on, " + ThermalHigh + " m up, " + ThermalWide + " m wide, " + ThermalDeep + " m deep" : "thermals off";
        }

        static void FeedThermals()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            Vector3d up = FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
            Vector3d east = Vector3d.Cross(up, v.mainBody.transform.up).normalized, north = Vector3d.Cross(east, up);
            for (int k = 0; k < 5; k++)
            {
                double turn = k * 2.399963, out_ = k == 0 ? 0.0 : 1.25 * ThermalWide;
                float size = k == 0 ? 1f : 0.62f;
                Vector3d at = v.CoMD + up * ThermalHigh + east * (out_ * Math.Cos(turn)) + north * (out_ * Math.Sin(turn));
                var e = new Exhaust { id = 990000 + k, rim = at, along = up, going = Vector3d.zero, nozzle = ThermalWide * size, flame = ThermalDeep * (k == 0 ? 1f : 0.6f),
                                      running = k == 0 ? 0.8f : 0.3f, amount = ThermalAmount, shade = 1f, warm = 0f, lasts = 100f, tint = 0, fire = 0f, rides = null, kind = ExhaustKind.Thermal };
                Sources.Burning(e);
            }
        }

        /// <summary>For the development build: what is going on, in one line.</summary>
        public static string Stats()
        {
            if (Instance == null) return "not running";
            string text = Alive + " particles in " + Instance.sites.Count + " site(s), " + LastMs.ToString("F2") + " ms (waiting " + shown[0].ToString("F2") + ", the rest " + shown[1].ToString("F2") + "; on other threads " + shown[2].ToString("F2") + " moving, " + gridMs.ToString("F1") + " per grid), " + Instance.marks.Count + " scorch mark(s)";
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            text += " | grid parts, ms: ready " + (Stage[0] * ms).ToString("F1") + ", particles " + (Stage[1] * ms).ToString("F1") + ", sun " + (Stage[2] * ms).ToString("F1") + ", sky " + (Stage[3] * ms).ToString("F1") + ", around " + (Stage[4] * ms).ToString("F1") + ", carry " + (Stage[5] * ms).ToString("F1") + " (those four together " + (Stage[6] * ms).ToString("F1") + "), packing " + (Stage[7] * ms).ToString("F1");
            text += " | parts of a frame, ms:";
            for (int n = 0; n < Part.Length; n++) text += " " + partName[n] + " " + partShown[n].ToString("F3");
            foreach (Site s in Instance.sites) text += " | " + s.Describe();
            return text;
        }
    }

    /// <summary>A patch of the ground (or of the space beside a ship) that things are measured from.</summary>
    public sealed class Frame
    {
        public readonly CelestialBody body;
        public readonly bool space;
        Vector3d originB, eastB, upB, northB;          // fixed to the body
        public Vector3d rel, velocity;                 // in space: offset from the ship being flown, and velocity, in the scene's axes
        public Vector3d origin, east, up, north;       // where all that is in the scene this frame
        public Transform rides;                        // what it goes along with, if anything (see the second way of making one)
        Vector3 ridesAt;                               // where on that its middle is, in that thing's own axes
        bool rode;

        /// <summary>
        /// A frame that goes along with something: a ship. Its middle stays at the same place on the ship; its axes are
        /// still east, up and north there, whichever way the ship is turned. (An engine's flame is worked out in such a
        /// frame. In one that stays where it is, a rocket doing three hundred metres a second would leave its flame
        /// behind between one frame and the next.)
        /// </summary>
        public Frame(CelestialBody body, Vector3d world, Transform rides) : this(body, world, false, Vector3d.zero)
        {
            this.rides = rides;
            ridesAt = rides.InverseTransformPoint((Vector3)world);
            rode = true;
        }

        /// <summary>Whether it goes along with something still: not if that has gone (a ship that broke up, or was put away), when the frame stays where it last was.</summary>
        public bool Riding => rode && rides != null;

        void Axes()
        {
            up = FlightGlobals.getUpAxis(body, origin);
            east = Vector3d.Cross(up, body.transform.up);
            east = east.sqrMagnitude < 1e-8 ? Vector3d.Cross(up, Vector3d.forward).normalized : east.normalized;
            north = Vector3d.Cross(east, up);
        }

        public Frame(CelestialBody body, Vector3d world, bool space, Vector3d orbitalVelocity)
        {
            this.body = body;
            this.space = space;
            up = FlightGlobals.getUpAxis(body, world);
            // East, up and north must be to one another as a Unity object's own right, up and forward are (right is up
            // crossed with forward): the patch's root is turned to face north with up as its up (see Apply), everything
            // drawn hangs from that root, and everything worked out is reckoned along these three. Until 0.4.0 "east"
            // was the other way round, pointing west. The sums were all of a piece, and so was the drawing, but each
            // was the other's mirror image: whatever lay east of the middle of a patch was drawn as far west of it.
            // A blast is in the middle of its own patch, so it never showed there (though its smoke went round a
            // building on the wrong side, and was lit from the wrong side when the sun was low in the east or west).
            // A rocket's smoke, laid along a path leaning away from the patch's middle, was drawn leaning the other way.
            east = Vector3d.Cross(up, body.transform.up);
            east = east.sqrMagnitude < 1e-8 ? Vector3d.Cross(up, Vector3d.forward).normalized : east.normalized;
            north = Vector3d.Cross(east, up);
            origin = world;
            if (space)
            {
                Vessel ship = FlightGlobals.ActiveVessel;
                rel = ship != null ? world - ship.CoMD : Vector3d.zero;
                velocity = orbitalVelocity;
            }
            else
            {
                originB = body.GetRelSurfacePosition(world);
                eastB = body.GetRelSurfaceDirection(east);
                upB = body.GetRelSurfaceDirection(up);
                northB = body.GetRelSurfaceDirection(north);
            }
        }

        /// <summary>Work out where the frame is in the scene now.</summary>
        public void Refresh()
        {
            if (rode)
            {
                if (rides != null) { origin = (Vector3d)rides.TransformPoint(ridesAt); Axes(); return; }
                // (what it went along with is gone: from here on it is fixed to the ground under where it last was)
                rode = false; rides = null;
                originB = body.GetRelSurfacePosition(origin);
                eastB = body.GetRelSurfaceDirection(east); upB = body.GetRelSurfaceDirection(up); northB = body.GetRelSurfaceDirection(north);
            }
            if (space)
            {
                Vessel ship = FlightGlobals.ActiveVessel;
                if (ship != null) origin = ship.CoMD + rel;
                return;
            }
            origin = body.BodyFrame.LocalToWorld(originB.xzy).xzy + body.position;
            east = body.BodyFrame.LocalToWorld(eastB.xzy).xzy;
            up = body.BodyFrame.LocalToWorld(upB.xzy).xzy;
            north = body.BodyFrame.LocalToWorld(northB.xzy).xzy;
        }

        /// <summary>In space the patch coasts along its own orbit beside the ship.</summary>
        public void Coast(float dt)
        {
            if (!space) return;
            Vessel ship = FlightGlobals.ActiveVessel;
            if (ship == null) return;
            velocity += FlightGlobals.getGeeForceAtPosition(origin, body) * dt;
            rel += (velocity - ship.obt_velocity) * dt;
        }

        public void Apply(Transform t)
        {
            Refresh();
            t.position = (Vector3)origin;
            t.rotation = Quaternion.LookRotation((Vector3)north, (Vector3)up);
        }

        public Vector3 ToLocal(Vector3d world)
        {
            Vector3d d = world - origin;
            return new Vector3((float)Vector3d.Dot(d, east), (float)Vector3d.Dot(d, up), (float)Vector3d.Dot(d, north));
        }

        public Vector3 DirToLocal(Vector3d v) => new Vector3((float)Vector3d.Dot(v, east), (float)Vector3d.Dot(v, up), (float)Vector3d.Dot(v, north));

        public Vector3d ToWorld(Vector3 local) => origin + east * local.x + up * local.y + north * local.z;

        /// <summary>Where an object stands in the scene, restated in this frame.</summary>
        public Matrix4x4 ToLocal(Matrix4x4 world)
        {
            var m = Matrix4x4.identity;
            for (int j = 0; j < 3; j++)
            {
                Vector3 axis = DirToLocal((Vector3d)(Vector3)world.GetColumn(j));
                m.SetColumn(j, new Vector4(axis.x, axis.y, axis.z, 0f));
            }
            Vector3 at = ToLocal((Vector3d)(Vector3)world.GetColumn(3));
            m.SetColumn(3, new Vector4(at.x, at.y, at.z, 1f));
            return m;
        }
    }

    /// <summary>
    /// A burn mark left on the ground: a small sheet laid over the terrain, charred in the middle with soot
    /// and streaks thrown outward, drawn out along the way a crash was going, with embers glowing in it
    /// for the first few seconds. It fades over a few minutes. (Since 0.8.0 as photographs of real ones show them:
    /// lopsided, in lobes and broad tongues of soot, each mark one of several kinds: see tools/fxbake's Burn.)
    /// </summary>
    public sealed class Scorch
    {
        const int Cells = 10;
        const float Lasts = 300f, FadesFor = 60f, Glows = 14f;

        readonly Frame frame;
        readonly GameObject holder;
        readonly Mesh mesh, hot;
        readonly Material thrown;                    // where the game can do it, the mark is thrown onto whatever is there (see tools/shaderpack/mark.glsl)
        readonly Color32[] colours;
        readonly float strength;
        readonly bool burning;
        float age, shownAs = -1f;

        /// <summary>How far from the camera it is.</summary>
        public float Away(Vector3 eye) => (holder.transform.position - eye).magnitude;

        public Scorch(Plan plan, float radius)
        {
            frame = new Frame(plan.body, plan.surfacePoint, false, Vector3d.zero);
            holder = new GameObject("VolumetricExplosions scorch") { layer = Recipe.Layer };
            frame.Apply(holder.transform);
            strength = Mathf.Clamp01(0.5f + plan.radius * 0.05f);
            burning = plan.radius > 1f && !plan.vacuum;

            // Something that came in at a slant leaves a mark drawn out ahead of it; a blast on the spot leaves a round one.
            Vector3 going = frame.DirToLocal(plan.velocity);
            going.y = 0f;
            float speed = going.magnitude, longer = 1f + Mathf.Min(2f, speed / 50f);
            float turn = speed > 8f ? Mathf.Atan2(going.z, going.x) : UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float cos = Mathf.Cos(turn), sin = Mathf.Sin(turn), ahead = (longer - 1f) * radius * 0.45f;
            Vector3 up = (Vector3)frame.up;
            // (one of the kinds of mark, at random, so that no two are alike: it reaches furthest ahead, the way a crash was going)
            int kind = Assets.ScorchKinds.Length > 1 ? UnityEngine.Random.Range(0, Assets.ScorchKinds.Length) : 0;
            Texture picture = kind < Assets.ScorchKinds.Length ? Assets.ScorchKinds[kind] : Assets.Scorch.mainTexture;
            Texture glowing = kind < Assets.EmberKinds.Length ? Assets.EmberKinds[kind] : Assets.Embers.mainTexture;

            if (Assets.Mark != null && Assets.Cube != null)
            {
                // A flat box laid on the ground there, tilted the way the ground lies: found by sounding it under
                // the middle of the mark and four places round it. The shader does the rest.
                var found = new Vector3[5];
                var hit = new bool[5];
                int hits = 0;
                for (int n = 0; n < 5; n++)
                {
                    float a = ahead + (n == 1 ? 0.6f : n == 2 ? -0.6f : 0f) * radius * longer, b = (n == 3 ? 0.6f : n == 4 ? -0.6f : 0f) * radius;
                    float x = a * cos - b * sin, z = a * sin + b * cos;
                    Vector3 above = (Vector3)frame.ToWorld(new Vector3(x, 30f, z));
                    hit[n] = Physics.Raycast(above, -up, out RaycastHit under, 80f, 1 << 15, QueryTriggerInteraction.Ignore);
                    found[n] = new Vector3(x, hit[n] ? 30f - under.distance : 0f, z);
                    if (hit[n]) hits++;
                }
                // Height falls by 'along' for each metre the way the thing was going, and by 'across' for each metre to the side.
                float along = hit[1] && hit[2] ? Mathf.Clamp((found[2].y - found[1].y) / (1.2f * radius * longer), -1.2f, 1.2f) : 0f;
                float across = hit[3] && hit[4] ? Mathf.Clamp((found[4].y - found[3].y) / (1.2f * radius), -1.2f, 1.2f) : 0f;
                float height = 0f;
                for (int n = 0; n < 5; n++) if (hit[n]) height += found[n].y;
                height = hits > 0 ? height / hits : 0f;
                var forward = new Vector3(cos, -along, sin);                // the way it was going, lying on the ground
                var sideways = new Vector3(-sin, -across, cos);
                Vector3 normal = Vector3.Cross(sideways, forward).normalized;
                if (normal.y < 0f) normal = -normal;
                forward = Vector3.ProjectOnPlane(forward, normal).normalized;
                var box = new GameObject("mark") { layer = Recipe.Layer };
                box.transform.SetParent(holder.transform, false);
                box.transform.localPosition = new Vector3(ahead * cos, height, ahead * sin);
                box.transform.localRotation = Quaternion.LookRotation(Vector3.Cross(forward, normal), normal);
                // Deep enough to take in ground that is not quite flat under it, and no deeper: whatever stands inside the box is marked too.
                box.transform.localScale = new Vector3(2f * radius * longer, Mathf.Clamp(0.5f * radius, 1.2f, 5f), 2f * radius);
                box.AddComponent<MeshFilter>().sharedMesh = Assets.Cube;
                var renderer = box.AddComponent<MeshRenderer>();
                // (a mark drawn on the ground has no motion of its own for the game's smoothing to take: see Site.TuneShadow)
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                thrown = new Material(Assets.Mark) { renderQueue = 2460 };
                thrown.SetTexture("_MarkTex", picture);
                thrown.SetTexture("_MarkEmbers", glowing);
                thrown.SetVector("_MarkSize", new Vector4(2f * radius / Mathf.Max(64, picture.width), 0f, 0f, 0f));
                renderer.sharedMaterial = thrown;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                Tint();
                return;
            }

            // Otherwise: a grid of points dropped onto the ground, so that the mark follows its shape as nearly as a sheet can.
            int side = Cells + 1;
            var points = new Vector3[side * side];
            var uv = new Vector2[side * side];
            colours = new Color32[side * side];
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    float u = i / (float)Cells, v = j / (float)Cells;
                    // Along the way it was going (a), and across (b).
                    float a = (u * 2f - 1f) * radius * longer + ahead, b = (v * 2f - 1f) * radius;
                    float x = a * cos - b * sin, z = a * sin + b * cos, y = 0f;
                    Vector3 above = (Vector3)frame.ToWorld(new Vector3(x, 30f, z));
                    if (Physics.Raycast(above, -up, out RaycastHit hit, 80f, 1 << 15, QueryTriggerInteraction.Ignore)) y = 30f - hit.distance;
                    points[j * side + i] = new Vector3(x, y + 0.07f, z);
                    uv[j * side + i] = new Vector2(u, v);
                }
            var triangles = new int[Cells * Cells * 6];
            int t = 0;
            for (int j = 0; j < Cells; j++)
                for (int i = 0; i < Cells; i++)
                {
                    int a = j * side + i;
                    triangles[t++] = a; triangles[t++] = a + side; triangles[t++] = a + 1;
                    triangles[t++] = a + 1; triangles[t++] = a + side; triangles[t++] = a + side + 1;
                }
            mesh = Sheet("scorch", points, uv, triangles, holder, Assets.Scorch);
            if (burning && Assets.Embers != null)
            {
                var glow = new GameObject("embers") { layer = Recipe.Layer };
                glow.transform.SetParent(holder.transform, false);
                glow.transform.localPosition = new Vector3(0f, 0.02f, 0f);
                hot = Sheet("embers", points, uv, triangles, glow, Assets.Embers);
            }
            Tint();
        }

        static Mesh Sheet(string name, Vector3[] points, Vector2[] uv, int[] triangles, GameObject on, Material material)
        {
            var sheet = new Mesh { name = "VolumetricExplosions " + name };
            sheet.vertices = points;
            sheet.uv = uv;
            sheet.triangles = triangles;
            sheet.RecalculateBounds();
            on.AddComponent<MeshFilter>().sharedMesh = sheet;
            var renderer = on.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;      // (see Site.TuneShadow)
            renderer.receiveShadows = false;
            return sheet;
        }

        void Tint()
        {
            // It appears as the fire dies down, not under the flash.
            float shown = Mathf.Clamp01(age / 2.5f) * Mathf.Clamp01((Lasts - age) / FadesFor) * strength;
            if (thrown != null)
            {
                float still = burning && age < Glows ? 1f - age / Glows : 0f, embers = still * still * (0.75f + 0.25f * Mathf.PerlinNoise(age * 6f, 0.3f)) * Mathf.Clamp01(age / 1.5f);
                thrown.SetVector("_MarkTint", new Vector4(1f, 1f, 1f, shown));
                thrown.SetVector("_MarkGlow", new Vector4(embers, embers, embers, 0f));
                return;
            }
            if (Mathf.Abs(shown - shownAs) > 0.004f)
            {
                shownAs = shown;
                var c = new Color32(255, 255, 255, (byte)(255f * shown));
                for (int n = 0; n < colours.Length; n++) colours[n] = c;
                mesh.colors32 = colours;
            }
            if (hot == null) return;
            if (age > Glows) { hot.Clear(); return; }
            float left = 1f - age / Glows, glow = left * left * (0.75f + 0.25f * Mathf.PerlinNoise(age * 6f, 0.3f)) * Mathf.Clamp01(age / 1.5f);
            var g = new Color32((byte)(255f * glow), (byte)(255f * glow), (byte)(255f * glow), 255);
            for (int n = 0; n < colours.Length; n++) colours[n] = g;
            hot.colors32 = colours;
            shownAs = -1f;                                  // the list was reused: set the mark's own again next time
        }

        /// <returns>false when it has faded away.</returns>
        public bool Step(float dt)
        {
            age += dt;
            if (age > Lasts) return false;
            Tint();
            return true;
        }

        public void Place() => frame.Apply(holder.transform);

        public void Dispose()
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
            if (thrown != null) UnityEngine.Object.Destroy(thrown);
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (hot != null) UnityEngine.Object.Destroy(hot);
        }
    }
}
