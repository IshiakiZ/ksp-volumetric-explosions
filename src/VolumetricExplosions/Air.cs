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

        static readonly int cameraNote = Shader.PropertyToID("_VolCamera");

        /// <summary>Tell the volume shader whether the camera about to draw has a depth picture of the scene to stop the smoke at, and what angle one of its pixels covers.</summary>
        void ForCamera(Camera camera)
        {
            int which = camera == depthCamera[0] ? 0 : camera == depthCamera[1] ? 1 : -1;
            bool small = halfOn && which >= 0;
            if (small) Half(which, camera);
            Shader.SetGlobalVector(cameraNote, new Vector4((camera.depthTextureMode & DepthTextureMode.Depth) != 0 ? 1f : 0f, 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight), small ? 1f : 0f, 0f));
        }

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

        Site SiteFor(Plan plan)
        {
            foreach (Site s in sites)
                if (s.Takes(plan)) return s;
            if (sites.Count >= 6)
            {
                // Make room by letting the emptiest one go.
                Site least = sites[0];
                foreach (Site s in sites) if (s.Count < least.Count) least = s;
                least.Dispose();
                sites.Remove(least);
            }
            var site = new Site(plan);
            sites.Add(site);
            return site;
        }

        public void Mark(Plan plan, float radius)
        {
            if (!Settings.Scorch || Assets.Scorch == null) return;
            if (marks.Count >= 24) { marks[0].Dispose(); marks.RemoveAt(0); }
            marks.Add(new Scorch(plan, radius));
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

        public Frame(CelestialBody body, Vector3d world, bool space, Vector3d orbitalVelocity)
        {
            this.body = body;
            this.space = space;
            up = FlightGlobals.getUpAxis(body, world);
            // (Named east, but in Unity's left-handed axes this points west. Nothing here cares which: it is only the patch's own level axis.)
            east = Vector3d.Cross(body.transform.up, up);
            east = east.sqrMagnitude < 1e-8 ? Vector3d.Cross(Vector3d.forward, up).normalized : east.normalized;
            north = Vector3d.Cross(up, east);
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
