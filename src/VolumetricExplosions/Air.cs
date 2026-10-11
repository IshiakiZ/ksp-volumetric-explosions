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
        public static void Lap(int which, ref long lap)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Part[which] += now - lap;
            lap = now;
        }
        public static readonly long[] Stage = new long[8];    // timer ticks the parts of the last grid took: getting ready, putting the particles in, the sun's light, the sky's, what is round about, where it is going, (those four together), packing
        public static readonly System.Threading.Tasks.ParallelOptions Few = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = -1 };

        readonly List<Blast> newborn = new List<Blast>();
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

        void Start()
        {
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
                        LastPlan = plan.label ?? "";
                        SiteFor(plan).Explode(plan);
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
            var site = new Site(plan);
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
            Site oldest = null;
            foreach (Site s in sites)
            {
                if (s.Riding) { flames++; continue; }               // (the flames of ships are counted by themselves: see MostFlames)
                if (s.Leaving) continue;
                staying++;
                if (s.Vented && s.Unfed > 1f && (oldest == null || s.Unfed > oldest.Unfed)) oldest = s;
            }
            if (staying < Most) return;
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
                if (exhaust.rides != null && jet && Settings.TestYoung < 1f) smoke = TakeYoung(exhaust);
                if (!smoke)
                {
                    Exhaust rest = exhaust;
                    rest.amount *= 1f - trench;
                    smoke = rest.amount > 0.001f && TakeSmoke(rest) || trench > 0.01f;
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
        bool TakeYoung(in Exhaust exhaust)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.atmosphere) return false;
            Site site = null;
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
                if (plan.vacuum || plan.space || plan.underwater) { noAirUntil = Time.time + 0.5f; return false; }
                try { site = new Site(plan, exhaust.rides); }
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
                        if (plan.vacuum || plan.space || plan.underwater) { s.Handing.Clear(); continue; }       // (no air there to hold it: it is let go)
                        if (Staying() >= Most) LetOneGo();
                        try { to = new Site(plan); }
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
                        try { s = new Site(plan); }
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
                try { site = new Site(plan, exhaust.rides); }
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
                if (sites[n].Feeds(body, exhaust.rim)) site = sites[n];
                else if (sites[n].Has(exhaust.id)) break;
            }
            if (site == null)
            {
                if (exhaust.amount <= 0.001f || exhaust.running <= 0.02f) return false;
                // (where there turned out to be no air, it is not asked again at every frame: finding out takes a look at the ground and the air)
                if (Time.time < noAirUntil) return false;
#if DEV
                if (sites.Count > 0 && Settings.TestView == 8f) Debug.Log("[VolumetricExplosions] a new patch for engine " + exhaust.id + ": the newest would not have it: " + sites[sites.Count - 1].WhyNot(body, exhaust.rim));
#endif
                Plan plan = Plan.Where(exhaust.rim);
                if (plan.vacuum || plan.space || plan.underwater) { noAirUntil = Time.time + 0.5f; return false; }              // no air to hold smoke
                if (Staying() >= Most) LetOneGo();
                try { site = new Site(plan); }
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

        void LateUpdate()
        {
            steppedFrame = Time.frameCount;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            clock.Restart();
            if (Cost[3] > 0) gridMs = Cost[3] * 1000f / System.Diagnostics.Stopwatch.Frequency;
            for (int n = 0; n < Cost.Length; n++) { shown[n] = Mathf.Lerp(shown[n], Cost[n] * 1000f / System.Diagnostics.Stopwatch.Frequency, 0.1f); Cost[n] = 0; }
            for (int n = 0; n < Part.Length; n++) { partShown[n] = Mathf.Lerp(partShown[n], Part[n] * 1000f / System.Diagnostics.Stopwatch.Frequency, 0.05f); Part[n] = 0; }
            Pads.Frame();
            if (sites.Count > 0) LookForLights();
#if DEV
            TestMover();
            TestBurn();
#endif
            Sources.Ask();
            Trenches(dt);
            int alive = 0;
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
            Deliver();
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
        }

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
            placedFrame = Time.frameCount;
            long lap = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (Site s in sites) s.Place();
            foreach (Scorch m in marks) m.Place();
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
        public struct Front { public Vector3 middle, up; public float radius, push; public bool ground; }

        CommandBuffer shockCommands;
        Camera shockCamera;
        Material shockMaterial;
        RenderTexture picture;
        readonly List<Front> fronts = new List<Front>();
        static readonly int[] frontMiddle = Names("_ShockC"), frontPush = Names("_ShockP"), frontGround = Names("_ShockG");

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
            fronts.Sort((a, b) => b.push.CompareTo(a.push));
            for (int n = 0; n < frontMiddle.Length; n++)
            {
                if (n >= fronts.Count) { shockMaterial.SetVector(frontMiddle[n], Vector4.zero); continue; }
                Front f = fronts[n];
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
        public static string Inside()
        {
            if (Instance == null) return "not running";
            int tested = 0, inside = 0;
            foreach (Site s in Instance.sites) { inside += s.Inside(out int some); tested += some; }
            return inside + " of " + tested;
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
    /// for the first few seconds. It fades over a few minutes.
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
                thrown = new Material(Assets.Mark) { renderQueue = 2460 };
                thrown.SetTexture("_MarkTex", Assets.Scorch.mainTexture);
                thrown.SetTexture("_MarkEmbers", Assets.Embers.mainTexture);
                thrown.SetVector("_MarkSize", new Vector4(2f * radius / Mathf.Max(64, Assets.Scorch.mainTexture.width), 0f, 0f, 0f));
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
