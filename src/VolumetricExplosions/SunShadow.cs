using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosions
{
    /// <summary>
    /// What stands between the smoke and the sun throws its shadow into the smoke: a rocket's shadow lies across the cloud
    /// it is climbing out of, the tower's across the cloud round its foot, a hill's across smoke in its lee when the sun is
    /// low. (Before 0.8.0 the smoke was lit through itself alone, and nothing solid shaded it.)
    ///
    /// Once a frame, just before anything is drawn, the patch of smoke nearest the camera is looked at from the sun's side:
    /// a small camera of its own, square on to the sun's light and as wide as the patch's box, draws every solid thing round
    /// the patch (the craft, the space centre, the ground) into a picture of how far along the sun's light the nearest of
    /// them is (see tools/shaderpack/sundepth.glsl). The smoke's shader looks that picture up at every place it lights by
    /// the sun, and where something nearer the sun stands in the way that place is in its shadow, all but the light that
    /// finds its way round inside the cloud. The other patches are lit as before.
    /// </summary>
    sealed class SunShadow
    {
        const int Size = 1024;
        Camera cam;
        Site drawnFor;
        int drawnAt = -10;
        RenderTexture map;
        static readonly int fromNote = Shader.PropertyToID("_SunMapFrom"), alongNote = Shader.PropertyToID("_SunMapAlong"),
                            mapFrom = Shader.PropertyToID("_VolSunMapFrom"), mapRight = Shader.PropertyToID("_VolSunMapRight"), mapUp = Shader.PropertyToID("_VolSunMapUp"),
                            mapAlong = Shader.PropertyToID("_VolSunMapAlong"), mapNote = Shader.PropertyToID("_VolSunMap");
        static readonly Vector4 none = Vector4.zero;
#if DEV
        public static string Said = "";
        /// <summary>(Development build: the sun's-eye picture drawn with the game's shadows on, as through 0.9.0.)</summary>
        public static bool TestShadowsKept;
        /// <summary>(Development build: what other mods put on the sun's-eye camera and on the sun as it draws left there, as
        /// through 0.9.0.)</summary>
        public static bool TestRidersKept;
        /// <summary>(Development build: the picture drawn every frame, not every other.)</summary>
        public static bool TestEveryFrame;
        static float drawMs;
        Matrix4x4 lastBox = Matrix4x4.zero;
        Vector3 lastFrom, lastRight, lastUp, lastAlong;
        float lastScale;
#endif

        public void Frame(List<Site> sites, Vector3 eye)
        {
            Site chosen = null;
            float best = float.MaxValue;
            if (Settings.Shaded && Assets.SunDepth != null && Planetarium.fetch != null && Planetarium.fetch.Sun != null)
                foreach (Site s in sites)
                {
                    if (!s.Shown || s.Count == 0 || !s.SunUp) continue;
                    float d = (s.BoxCentre - eye).sqrMagnitude;
                    if (d < best) { best = d; chosen = s; }
                }
            foreach (Site s in sites) if (s != chosen && s.Marching != null) s.Marching.SetVector(mapAlong, none);
            if (chosen == null) { drawnFor = null; return; }
            if (cam == null) Make();
            // (Drawn every other frame: what stands about the smoke mostly stands still, and what moves (a rocket climbing out
            // of it) moves a metre or so in a frame. The picture and where it was taken from stay together, so between two
            // drawings the smoke reads the last one as it was. A new patch, or one the camera has come to, is drawn at once.)
#if DEV
            if (!TestEveryFrame)
#endif
            if (chosen == drawnFor && Time.frameCount - drawnAt < 2) return;
            drawnFor = chosen;
            drawnAt = Time.frameCount;

            // (the patch's box: its middle, and half the length of its longest diagonal)
            Matrix4x4 box = chosen.BoxMatrix;
            Vector3 centre = box.GetColumn(3);
            float half = 0.5f * Mathf.Sqrt(((Vector3)box.GetColumn(0)).sqrMagnitude + ((Vector3)box.GetColumn(1)).sqrMagnitude + ((Vector3)box.GetColumn(2)).sqrMagnitude);
            Vector3 toSun = ((Vector3)(Planetarium.fetch.Sun.position - (Vector3d)centre)).normalized;
            // (from far enough out on the sun's side to take in what stands high above the smoke: a rocket climbing out of it)
            float back = half + 250f, wide = half + 5f;
            Vector3 from = centre + toSun * back;
            cam.transform.position = from;
            cam.transform.rotation = Quaternion.LookRotation(-toSun, Mathf.Abs(toSun.y) < 0.95f ? Vector3.up : Vector3.forward);
            cam.orthographicSize = wide;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = back + 2f * half + 50f;
            Shader.SetGlobalVector(fromNote, from);
            Shader.SetGlobalVector(alongNote, -toSun);
            // (This is drawn in the middle of the far camera's getting ready to draw (Air.BeforeDrawing), and a mod that sets the light
            // for each camera as it begins sets it for this one too, and leaves it so for the far camera's drawing after: Natural Light
            // gives every camera not of the flight's scene the game's own light, and the far camera then drew the whole ground in it
            // whenever this was drawn, every other frame. From lift-off the land beat light and dark, by 10 levels of 255 over up to
            // 40 % of the picture a frame (2026-10-21, tools/motion), and not at all with this drawing left out. The light is put
            // back afterwards as the far camera had been given it.)
            // (And drawn with the game's shadows off: what stands in the sun is drawn by its distance alone, which no shadow
            // touches, so the game need not work out the sun's shadows again for this little camera, nor run for it what other
            // mods hang on that step.)
            // (And nothing another mod puts on this camera, or on the sun, while it draws is kept. Blackrack's volumetric clouds
            // (his EVE's screen-space shadows) put a script of theirs on every camera that draws an object of theirs on the
            // ground's layer, this one too, and the script hangs the clouds' shadows on the sun before its camera draws and takes
            // them off once it has drawn. For this camera, drawn by hand, they were never taken off: every picture from 0.8.0 on
            // left another pair on the sun, run for every camera's shadows from then on, and in his copy of the game the ground
            // went black near the camera within seconds of smoke and stayed so, smoke or none, through a quickload, until the
            // game was started again (1,619 pairs on the sun after a few minutes of it; ksp-volumetric-explosions issue 3, his
            // clouds' shadows "violently flicker", 2026-10-28; seen and mended 2026-11-02). So as soon as this has drawn,
            // whatever was put on it is taken off (Unburden), and whatever was hung on the sun meanwhile (LightKept.Back).)
            LightKept light = LightKept.Now();
            float shadowsWere = QualitySettings.shadowDistance;
#if DEV
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!TestShadowsKept)
#endif
            QualitySettings.shadowDistance = 0f;
            try { cam.RenderWithShader(Assets.SunDepth, ""); }
            finally { QualitySettings.shadowDistance = shadowsWere; }
#if DEV
            drawMs = Mathf.Lerp(drawMs, (float)((System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency), 0.05f);
#endif
            light.Back();
            Unburden();

#if DEV
            lastBox = box; lastFrom = from; lastRight = cam.transform.right; lastUp = cam.transform.up; lastAlong = -toSun; lastScale = 1f / (2f * wide);
#endif
            Material m = chosen.Marching;
            m.SetVector(mapFrom, new Vector4(from.x, from.y, from.z, 1f / (2f * wide)));
            m.SetVector(mapRight, cam.transform.right);
            m.SetVector(mapUp, cam.transform.up);
            m.SetVector(mapAlong, new Vector4(-toSun.x, -toSun.y, -toSun.z, 1f));
            m.SetTexture(mapNote, map);
#if DEV
            Said = "over a patch " + Mathf.Sqrt(best).ToString("F0") + " m from the camera, " + (2f * wide).ToString("F0") + " m wide, from " + back.ToString("F0") + " m sunward; drawn in " + drawMs.ToString("F2") + " ms, shadows " + (TestShadowsKept ? "on" : "off");
#endif
        }

#if DEV
        /// <summary>(What is in the sun's-eye picture: how much of it has something solid drawn in, and how far along the light the nearest and farthest are.)</summary>
        public string Probe()
        {
            if (map == null) return "no picture";
            var was = RenderTexture.active;
            RenderTexture.active = map;
            var read = new Texture2D(Size, Size, TextureFormat.RFloat, false);
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            read.Apply();
            RenderTexture.active = was;
            Color[] dots = read.GetPixels();
            Object.Destroy(read);
            int solid = 0; float least = float.MaxValue, most = 0f;
            foreach (Color c in dots) { if (c.r < 1e5f) { solid++; if (c.r < least) least = c.r; if (c.r > most) most = c.r; } }
            // (and the picture itself, as a grey one, near light and far dark, into the bridge's pictures folder)
            var grey = new Texture2D(Size, Size, TextureFormat.RGB24, false);
            var px = new Color32[dots.Length];
            for (int n = 0; n < dots.Length; n++) { float v = dots[n].r >= 1e5f ? 0f : 1f - Mathf.Clamp01((dots[n].r - least) / Mathf.Max(most - least, 1f)); byte b = (byte)(255f * v); px[n] = new Color32(b, b, b, 255); }
            grey.SetPixels32(px);
            grey.Apply();
            string file = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), ".ksp-ai-bridge/pictures/sunmap.png");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            System.IO.File.WriteAllBytes(file, grey.EncodeToPNG());
            Object.Destroy(grey);
            // (and along a line straight up through the middle of the patch: where on the picture each place is, as the smoke's shader
            // reckons it, how far along the light it is, and what the picture holds there)
            var line = new System.Text.StringBuilder();
            if (lastBox != Matrix4x4.zero)
            {
                Vector3 mid = lastBox.GetColumn(3), up = ((Vector3)lastBox.GetColumn(1)).normalized;
                float tall = ((Vector3)lastBox.GetColumn(1)).magnitude;
                for (int k = 0; k <= 6; k++)
                {
                    Vector3 p = mid + up * (tall * (k / 6f - 0.5f));
                    Vector3 rel = p - lastFrom;
                    float u = Vector3.Dot(rel, lastRight) * lastScale + 0.5f, v = Vector3.Dot(rel, lastUp) * lastScale + 0.5f, far = Vector3.Dot(rel, lastAlong);
                    int x = Mathf.Clamp((int)(u * Size), 0, Size - 1), y = Mathf.Clamp((int)(v * Size), 0, Size - 1);
                    line.Append(" | ").Append((tall * (k / 6f - 0.5f)).ToString("F0")).Append(" m: uv ").Append(u.ToString("F3")).Append(",").Append(v.ToString("F3")).Append(" far ").Append(far.ToString("F1")).Append(" map ").Append(dots[x + y * Size].r.ToString("F1"));
                }
            }
            return solid + " of " + dots.Length + " dots solid, nearest " + least.ToString("F1") + " m along the light, farthest " + most.ToString("F1") + " m (" + file + ")" + line;
        }
#endif

#if DEV
        GameObject block;
        /// <summary>(For trying: a plain cube "distance size" metres sunward of the patch's middle, a shadow that must cut through it; "off" takes it away.)</summary>
        public string Block(string arg)
        {
            if (block != null) Object.Destroy(block);
            block = null;
            if (arg == "off" || lastBox == Matrix4x4.zero) return "no block";
            string[] w = arg.Split(' ');
            float distance = float.Parse(w[0]), size = float.Parse(w[1]);
            block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(block.GetComponent<Collider>());
            block.layer = 0;
            block.transform.position = (Vector3)lastBox.GetColumn(3) - lastAlong * distance;
            block.transform.localScale = Vector3.one * size;
            return "a block of " + size + " m, " + distance + " m sunward of the patch's middle";
        }

        /// <summary>(What hangs on the sun's-eye camera: another mod's scripts put on it.)</summary>
        public string Parts()
        {
            if (cam == null) return "no camera yet";
            var said = new System.Text.StringBuilder();
            foreach (Component c in cam.GetComponents<Component>()) said.Append(c.GetType().FullName).Append("; ");
            return said.ToString();
        }
#endif

        static readonly List<MonoBehaviour> riders = new List<MonoBehaviour>();

        /// <summary>Whatever another mod has put on this camera as it drew (a script of its own, command buffers), taken off: nothing
        /// of the mod's own is on it but the camera.</summary>
        void Unburden()
        {
#if DEV
            if (TestRidersKept) return;
#endif
            cam.GetComponents(riders);
            foreach (MonoBehaviour b in riders) if (b != null) Object.Destroy(b);
            riders.Clear();
            if (cam.commandBufferCount > 0) cam.RemoveAllCommandBuffers();
        }

        static readonly LightEvent[] steps = (LightEvent[])System.Enum.GetValues(typeof(LightEvent));

        /// <summary>The scene's light as the cameras' hooks have set it for the camera about to draw: the light all round, and the sun's;
        /// and what hangs on the sun's steps.</summary>
        struct LightKept
        {
            AmbientMode mode;
            SphericalHarmonicsL2 probe;
            Color sky, equator, ground;
            float strength, sunStrength;
            Color sunColour;
            Light sun;
            int hooked;
            CommandBuffer[][] hooks;

            public static LightKept Now()
            {
                Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
                var kept = new LightKept
                {
                    mode = RenderSettings.ambientMode, probe = RenderSettings.ambientProbe,
                    sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor,
                    strength = RenderSettings.ambientIntensity,
                    sun = sun, sunStrength = sun != null ? sun.intensity : 0f, sunColour = sun != null ? sun.color : Color.white,
                };
                if (sun != null)
                {
                    kept.hooked = sun.commandBufferCount;
                    kept.hooks = new CommandBuffer[steps.Length][];
                    for (int i = 0; i < steps.Length; i++) kept.hooks[i] = sun.GetCommandBuffers(steps[i]);
                }
                return kept;
            }

            /// <summary>Whatever has changed since, as it was. (The colours and the way the light is made first: a change of the way makes
            /// the light all round afresh, and the light itself, where it was set outright, is then put back over that.)</summary>
            public void Back()
            {
                if (RenderSettings.ambientSkyColor != sky) RenderSettings.ambientSkyColor = sky;
                if (RenderSettings.ambientEquatorColor != equator) RenderSettings.ambientEquatorColor = equator;
                if (RenderSettings.ambientGroundColor != ground) RenderSettings.ambientGroundColor = ground;
                if (RenderSettings.ambientIntensity != strength) RenderSettings.ambientIntensity = strength;
                if (RenderSettings.ambientMode != mode) RenderSettings.ambientMode = mode;
                if (RenderSettings.ambientProbe != probe) RenderSettings.ambientProbe = probe;
                if (sun != null)
                {
                    if (sun.intensity != sunStrength) sun.intensity = sunStrength;
                    if (sun.color != sunColour) sun.color = sunColour;
                }
#if DEV
                if (TestRidersKept) return;
#endif
                // (a command buffer hung on one of the sun's steps while the sun's-eye camera drew was hung for that camera: off again)
                if (sun != null && hooks != null && sun.commandBufferCount != hooked)
                    for (int i = 0; i < steps.Length; i++)
                        foreach (CommandBuffer c in sun.GetCommandBuffers(steps[i]))
                            if (System.Array.IndexOf(hooks[i], c) < 0) sun.RemoveCommandBuffer(steps[i], c);
            }
        }

        void Make()
        {
            var holder = new GameObject("VolumetricExplosions the sun's view of the smoke");
            Object.DontDestroyOnLoad(holder);
            cam = holder.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.aspect = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(1e6f, 0f, 0f, 0f);          // (as far along the light as can be: nothing in the way)
            // (the craft, and the space centre's buildings and the ground: not the smoke's own particles, nor the game's, nor its effects)
            cam.cullingMask = (1 << 0) | (1 << 15);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.depthTextureMode = DepthTextureMode.None;
            map = new RenderTexture(Size, Size, 16, RenderTextureFormat.RFloat) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "VolumetricExplosions what stands in the sun" };
            map.Create();
            cam.targetTexture = map;
        }

        public void Dispose()
        {
            if (cam != null) Object.Destroy(cam.gameObject);
            if (map != null) { map.Release(); Object.Destroy(map); }
            cam = null; map = null;
        }
    }
}
