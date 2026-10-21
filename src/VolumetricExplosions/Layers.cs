using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosions
{
    /// <summary>
    /// Every patch of air's smoke on the screen in true depth order (since 0.5.0; see tools/shaderpack/layers.glsl).
    ///
    /// Until then each patch was put on the screen whole by its own box, one over another in the order the game sorts
    /// see-through things in: by how far the middle of each box is from the camera, and the ships' patches (flames) after
    /// all the rest. Patches overlap, and their boxes differ in size: a small patch half inside a big one, its middle
    /// nearer the camera, was drawn over the big one's smoke even where it lay behind it (pale smoke over black, in a crash).
    ///
    /// Now, just before each of the two cameras that draw the flight draws its see-through things, each patch in its range
    /// is walked by itself into a picture of its own, keeping with its colour how far along each ray its smoke lies: those
    /// that stay where they are each into a slot of one shared picture (the atlas), at half the screen's size each way (the
    /// screen's size with "half = false"); those that go along with ships all into one picture at the screen's full size,
    /// where a flame's fine tongues keep their sharpness. Then one sheet, over the part of the screen they are on and in its
    /// turn among the see-through things, puts them all on the screen together: pixel by pixel, in the order of where each
    /// one's smoke lies along that pixel's ray.
    /// </summary>
    sealed class Layers
    {
        /// <summary>Whether it can be done here: the shader is there, and the graphics card can draw into the pictures it needs.</summary>
        public static bool Possible => Assets.Layers != null && ColourFormat != RenderTextureFormat.Default && DepthFormat != RenderTextureFormat.Default;

        /// <summary>
        /// Whether the ships' patches are to be walked at half the screen's size each way, as the rest are, and enlarged as the rest
        /// are (read by Site when it sets a patch going, for the frame after this one's: see Frame). A ship's flame is drawn whole
        /// for its fine tongues, which enlarged from half size were a blur of colour when they were a few pixels wide. But where the
        /// ships' boxes cover much of the screen, the camera close by or in the plume, a tongue is tens of pixels wide, and walking
        /// every pixel of the screen costs four times as much: at 1440 lines a booster's smoke took a fast card from 180 frames a
        /// second to 80, and from inside it to 15 (ksp-rocket-smoke's first issue, 2026-10-18). From a quarter of the screen
        /// covered; whole again under a sixth, so that it does not flicker between the two near the line.
        /// </summary>
        public static bool ShipsHalf;
        bool shipsHalfNow;                        // (what the patches were set going with this frame: ShipsHalf as it was when they were)

        static RenderTextureFormat colourFormat = RenderTextureFormat.Default, depthFormat = RenderTextureFormat.Default;
        static bool formatsLooked;

        // (sixteen bits a colour where the card can: fire is far brighter than white, and thin smoke far fainter than one part in 255)
        static RenderTextureFormat ColourFormat { get { LookAtFormats(); return colourFormat; } }
        // (two numbers: how far along the ray the smoke lies, on the whole, and how widely it is spread along it, each times how much it
        // hides. Sixteen bits are enough to tell one patch's smoke from another's (a quarter of a metre at 400 m, see Air.ForCamera for the
        // far camera), and every card can mix them as it draws)
        static RenderTextureFormat DepthFormat { get { LookAtFormats(); return depthFormat; } }

        static void LookAtFormats()
        {
            if (formatsLooked) return;
            formatsLooked = true;
            colourFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf
                         : SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32) ? RenderTextureFormat.ARGB32 : RenderTextureFormat.Default;
            depthFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf) ? RenderTextureFormat.RGHalf
                        : SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat) ? RenderTextureFormat.RGFloat
                        : SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.Default;
        }

        readonly Matrix4x4[] seen = new Matrix4x4[2];      // what took a place in the scene to its place on each camera's picture, the last time it drew
        readonly int[] seenFrame = { -10, -10 };
        readonly CommandBuffer[] commands = new CommandBuffer[2];
        readonly Camera[] attached = new Camera[2];
        readonly List<Site> world = new List<Site>(), ships = new List<Site>(), inRange = new List<Site>();
        readonly float[] away = new float[64];
        RenderTexture atlas, atlasDepth, full, fullDepth;
        readonly RenderTargetIdentifier[] atlasBoth = new RenderTargetIdentifier[2], fullBoth = new RenderTargetIdentifier[2];
        int capacity, perRow, slotWide, slotTall;
        float roomyFor;                           // how long the atlas has had more than twice the room needed (it is then made smaller)
        float fullUnused;                         // how long since the ships' picture was last drawn into (it is then let go)
        GameObject sheet;
        MeshRenderer sheetRenderer;
        Material material;
        Mesh quad;
        static readonly int slotNote = Shader.PropertyToID("_VolSlot"), layersNote = Shader.PropertyToID("_VolLayers"), slotsNote = Shader.PropertyToID("_VolSlots"), sheetNote = Shader.PropertyToID("_VolSheet");
        static readonly Vector3[] cube = Corners();

        // ---- a patch walked a quarter of its pixels a frame, the rest carried over (2026-10-18; research/rendering/notes/smoke-temporal.md)
        /// <summary>
        /// Whether a patch with no fire in it that covers much of the screen (a booster's exhaust cloud with the camera in or by it) is
        /// walked at one pixel in each two by two of its slot a frame, the four in turn, and the other three carried over from the
        /// frame before, from where their smoke was then (tools/shaderpack/carry.glsl). In the lab, inside a booster's cloud, 60-66 %
        /// less of the walk, the picture within 0.2 of 255 of walking it whole frame by frame, with the camera flying at up to 150 m/s
        /// and turning 90 degrees a second and the smoke going on between grids. False: every patch walked whole, as before (for
        /// setting the two side by side).
        /// </summary>
        public static bool Carrying = true;
        /// <summary>Whether a patch walked so is walked whole for a frame when its grid is made anew (what the old grid carried on since is then given up at once; in the lab it did not show).</summary>
        public static bool CarryAnewOnGrid = false;
        /// <summary>(Development read-out: the patches carried over for the last camera that drew, and why the rest were not.)</summary>
        public static string CarrySays => !Carrying ? "off" : !Settings.Half ? "not at half size" : Settings.TestLayers != 0f ? "a test of the layers" : Assets.Carry == null ? "no shader" :
            saidCarried + " of " + saidPatches + " patches walked a quarter at a time (" + saidFires + " with fire, " + saidSmall + " covering too little of the screen)";
        static int saidCarried, saidPatches, saidFires, saidSmall;
#if DEV
        /// <summary>(Development build, for films side by side: "off" walks every patch whole, anything else carries; says what it does.)</summary>
        public static string Carry(string on) { Carrying = on == null || on.Trim() != "off"; return CarrySays; }
        public static string Carry() => CarrySays;
#endif

        const int CarriedMost = 2;                // (at most so many at a time: each keeps four pictures the size of its slot and two of a quarter of it)
        sealed class Carried
        {
            public RenderTexture Quarter, QuarterDepth, WasA, WasADepth, WasB, WasBDepth;
            public bool AIsLast;                  // (which of the two pictures of what it was is last frame's)
            public int Frame = -10, Turn, Width, Height;
            public Camera Seen;
            public Matrix4x4 Box, Draws;          // its box, and what took a place in the scene to the camera's picture (the smoothing's shake and all), last frame
            public Vector3 Eye, Ahead;            // where the camera was from the box's middle and which way it looked, last frame
            public float GridAge;                 // how long ago its grid had been made, last frame (less now: made anew)
            public bool Covering;                 // whether it covered enough of the screen to be carried, last frame
            public float Covers;                  // how much of the screen it covers now
            public readonly MaterialPropertyBlock Own = new MaterialPropertyBlock(), Slot = new MaterialPropertyBlock();
        }
        readonly Dictionary<Site, Carried>[] carried = { new Dictionary<Site, Carried>(), new Dictionary<Site, Carried>() };
        readonly List<Site> carriedGone = new List<Site>();
        readonly List<Site> carryChosen = new List<Site>();
        Material carryMaterial;
        readonly RenderTargetIdentifier[] two = new RenderTargetIdentifier[2];
        static readonly Vector2Int[] turns = { new Vector2Int(0, 0), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(0, 1) };
        static readonly int shiftNote = Shader.PropertyToID("_VolShift"), quarterNote = Shader.PropertyToID("_VolQuarter"), thinNote = Shader.PropertyToID("_VolThin"), lampsNote = Shader.PropertyToID("_VolLamps"),
            carryAtNote = Shader.PropertyToID("_CarryAt"), carryQNote = Shader.PropertyToID("_CarryQ"), carryCamNote = Shader.PropertyToID("_CarryCam"),
            carryRayXNote = Shader.PropertyToID("_CarryRayX"), carryRayYNote = Shader.PropertyToID("_CarryRayY"), carryRayZNote = Shader.PropertyToID("_CarryRayZ"),
            carryWasXNote = Shader.PropertyToID("_CarryWasX"), carryWasYNote = Shader.PropertyToID("_CarryWasY"), carryWasWNote = Shader.PropertyToID("_CarryWasW"),
            carryBoxXNote = Shader.PropertyToID("_CarryBoxX"), carryBoxYNote = Shader.PropertyToID("_CarryBoxY"), carryBoxZNote = Shader.PropertyToID("_CarryBoxZ"),
            carryQuarterNote = Shader.PropertyToID("_CarryQuarter"), carryQuarterDepthNote = Shader.PropertyToID("_CarryQuarterDepth"),
            carryWasNote = Shader.PropertyToID("_CarryWas"), carryWasDepthNote = Shader.PropertyToID("_CarryWasDepth");

        static Vector3[] Corners()
        {
            var c = new Vector3[8];
            for (int n = 0; n < 8; n++) c[n] = new Vector3((n & 1) - 0.5f, ((n >> 1) & 1) - 0.5f, (n >> 2) - 0.5f);
            return c;
        }

        /// <summary>
        /// Once a frame, before anything is drawn and after every patch has been put where it is (see Air.BeforeDrawing): which
        /// patches there are to draw, the farthest first; the cameras told to draw them; the sheet put where the nearest is.
        /// </summary>
        public void Frame(List<Site> sites, Camera[] cameras, Vector3 eye)
        {
            world.Clear();
            ships.Clear();
            foreach (Site s in sites)
            {
                if (!s.Shown) continue;
                if (s.Riding) ships.Add(s); else world.Add(s);
            }
            Order(world, eye);
            Order(ships, eye);
            // (how much of the screen the ships' boxes cover, the corners of the rectangle that holds them all: see ShipsHalf)
            shipsHalfNow = ShipsHalf;
            Camera seer = cameras.Length > 0 && cameras[0] != null ? cameras[0] : cameras.Length > 1 ? cameras[1] : null;
            if (!Settings.Half || ships.Count == 0 || seer == null) ShipsHalf = false;
            else
            {
                Vector4 r = new Vector4(2f, 2f, -2f, -2f);
                Matrix4x4 v = seer.projectionMatrix * seer.worldToCameraMatrix;
                foreach (Site s in ships) Covers(ref r, v, s.BoxMatrix, seer);
                float covered = r.z > r.x && r.w > r.y ? Mathf.Clamp01((Mathf.Min(r.z, 1f) - Mathf.Max(r.x, -1f)) * (Mathf.Min(r.w, 1f) - Mathf.Max(r.y, -1f)) * 0.25f) : 0f;
                ShipsHalf = ShipsHalf ? covered > 1f / 6f : covered > 0.25f;
                // (for testing: 8 always so, 9 never)
                if (Settings.TestLayers == 8f) ShipsHalf = true; else if (Settings.TestLayers == 9f) ShipsHalf = false;
            }
            bool any = world.Count + ships.Count > 0;
            for (int which = 0; which < 2; which++)
            {
                Camera c = which < cameras.Length ? cameras[which] : null;
                if (attached[which] != null && (attached[which] != c || !any))
                {
                    attached[which].RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, commands[which]);
                    attached[which] = null;
                }
                if (!any || c == null || attached[which] == c) continue;
                if (commands[which] == null) commands[which] = new CommandBuffer { name = "VolumetricExplosions smoke, patch by patch" };
                commands[which].Clear();
                c.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, commands[which]);
                attached[which] = c;
            }
            if (!any)
            {
                if (sheetRenderer != null && sheetRenderer.enabled) sheetRenderer.enabled = false;
                Room(0, Time.unscaledDeltaTime);
                LetFullGo(Time.unscaledDeltaTime);
                return;
            }
            NeedSheet();
            if (!sheetRenderer.enabled) sheetRenderer.enabled = true;
            // (The sheet takes its turn among the other see-through things of the scene by how far it is from the camera, as
            // the boxes of the patches did: from where the nearest patch is.)
            Site nearest = ships.Count > 0 && (world.Count == 0 || (ships[ships.Count - 1].BoxCentre - eye).sqrMagnitude < (world[world.Count - 1].BoxCentre - eye).sqrMagnitude) ? ships[ships.Count - 1] : world[world.Count - 1];
            sheet.transform.position = nearest.BoxCentre;
            Room(world.Count, Time.unscaledDeltaTime);
            if (ships.Count == 0) LetFullGo(Time.unscaledDeltaTime); else fullUnused = 0f;
            LetCarriedGo();
        }

        /// <summary>The farthest first (by the middle of each box, as the game sorts see-through things: so that the test that lays them on in that order shows what used to be).</summary>
        void Order(List<Site> list, Vector3 eye)
        {
            int n = Mathf.Min(list.Count, away.Length);
            for (int k = 0; k < n; k++) away[k] = (list[k].BoxCentre - eye).sqrMagnitude;
            for (int k = 1; k < n; k++)
            {
                Site s = list[k];
                float d = away[k];
                int j = k - 1;
                while (j >= 0 && away[j] < d) { list[j + 1] = list[j]; away[j + 1] = away[j]; j--; }
                list[j + 1] = s; away[j + 1] = d;
            }
        }

        /// <summary>
        /// Just before one of the two cameras draws (Camera.onPreRender): what it is to draw of the smoke, into the pictures, and
        /// what the sheet is to put on the screen for it.
        /// </summary>
        public void ForCamera(int which, Camera camera)
        {
            CommandBuffer c = commands[which];
            if (c == null || attached[which] != camera || material == null) { NotFor(); return; }
            c.Clear();
            bool half = Settings.Half;
            int wide = camera.pixelWidth, tall = camera.pixelHeight;
            slotWide = half ? (wide + 1) / 2 : wide;
            slotTall = half ? (tall + 1) / 2 : tall;
            Matrix4x4 view = camera.projectionMatrix * camera.worldToCameraMatrix;
            Vector4 rect = new Vector4(2f, 2f, -2f, -2f);

            // (whether this camera's picture is smoothed between frames, and what takes a place in the scene to its place
            // on the picture now, without the smoothing's shake, and took it there a frame ago)
            bool follows = Settings.Follow && Assets.Motion != null && (camera.depthTextureMode & DepthTextureMode.MotionVectors) != 0;
            Matrix4x4 seesNow = camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix;
            Matrix4x4 seesBefore = seenFrame[which] == Time.frameCount - 1 ? seen[which] : seesNow;
            seen[which] = seesNow; seenFrame[which] = Time.frameCount;

            inRange.Clear();
            foreach (Site s in world) if (s.Within(camera)) inRange.Add(s);
            int slots = inRange.Count;
            if (slots > 0)
            {
                Atlas(slots, Settings.TestLayers == 5f);
                atlasBoth[0] = atlas; atlasBoth[1] = atlasDepth;
                c.SetRenderTarget(atlasBoth, atlas);
                c.ClearRenderTarget(false, true, Color.clear);
                ChooseCarried(which, camera, view, half);
                for (int k = 0; k < slots; k++)
                {
                    Site s = inRange[k];
                    int x = (k % perRow) * slotWide, y = (k / perRow) * slotTall;
                    if (Carry(c, which, camera, s, x, y, view)) { Covers(ref rect, view, s.BoxMatrix, camera); continue; }
                    s.Marching.SetVector(slotNote, new Vector4(x, y, 0f, 0f));
                    s.Marching.SetVector(shiftNote, Vector4.zero);
                    s.Marching.SetVector(quarterNote, Vector4.zero);
                    c.SetViewport(new Rect(x, y, slotWide, slotTall));
                    c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0);
                    Covers(ref rect, view, s.BoxMatrix, camera);
                }
                // Where this camera's picture is smoothed between frames (it then keeps a picture of how far each pixel has
                // moved): the smoke writes into that picture where it is going, each patch over its own smoke, so that the
                // smoothing follows smoke that moves instead of smearing it (see tools/shaderpack/motion.glsl).
                if (follows)
                {
                    c.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
                    for (int k = 0; k < slots; k++)
                    {
                        Site s = inRange[k];
                        Material m = s.Follows(atlas, atlasDepth, new Vector4((k % perRow) * slotWide, (k / perRow) * slotTall, half ? 1f : 0f, 0f), seesNow, seesBefore, Time.deltaTime);
                        if (m != null) c.DrawMesh(Assets.Cube, s.BoxMatrix, m, 0, 0);
                    }
                }
            }
            int shipsIn = 0;
            foreach (Site s in ships) if (s.Within(camera)) shipsIn++;
            if (shipsIn > 0)
            {
                Full(shipsHalfNow ? slotWide : wide, shipsHalfNow ? slotTall : tall);
                fullBoth[0] = full; fullBoth[1] = fullDepth;
                c.SetRenderTarget(fullBoth, full);
                c.ClearRenderTarget(false, true, Color.clear);
                foreach (Site s in ships)
                {
                    if (!s.Within(camera)) continue;
                    s.Marching.SetVector(slotNote, Vector4.zero);
                    c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0);
                    Covers(ref rect, view, s.BoxMatrix, camera);
                }
                // (and the ships' flames say where they are going: with the ship. Left to the ground's motion behind them,
                // a flame over ground that streams past was smeared along the ground's way.)
                if (follows)
                {
                    c.SetRenderTarget(BuiltinRenderTextureType.MotionVectors);
                    foreach (Site s in ships)
                    {
                        if (!s.Within(camera)) continue;
                        Material m = s.Follows(full, fullDepth, new Vector4(0f, 0f, shipsHalfNow ? 1f : 0f, 0f), seesNow, seesBefore, Time.deltaTime);
                        if (m != null) c.DrawMesh(Assets.Cube, s.BoxMatrix, m, 0, 0);
                    }
                }
            }
            if (slots + shipsIn == 0) { NotFor(); return; }
            c.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            // (a few pixels to spare all round: a pixel at the edge takes its smoke from the slot's pixels about it)
            float spareX = 8f / Mathf.Max(1, wide), spareY = 8f / Mathf.Max(1, tall);
            rect = new Vector4(Mathf.Max(-1f, rect.x - spareX), Mathf.Max(-1f, rect.y - spareY), Mathf.Min(1f, rect.z + spareX), Mathf.Min(1f, rect.w + spareY));
            float test = Settings.TestLayers == 1f ? 1f : Settings.TestLayers == 3f ? 2f : Settings.TestLayers == 4f ? 3f : Settings.TestLayers == 6f ? 4f : Settings.TestLayers == 7f ? 5f : 0f;
            material.SetVector(layersNote, new Vector4(slots, shipsIn > 0 ? (shipsHalfNow ? 2f : 1f) : 0f, perRow, half ? 1f : 0f));
            material.SetVector(slotsNote, new Vector4(slotWide, slotTall, test, 0f));
            material.SetVector(sheetNote, rect.z > rect.x && rect.w > rect.y ? rect : Vector4.zero);
        }

        /// <summary>
        /// Which of this camera's patches are walked a quarter at a time this frame (see Carrying): those with no fire in them
        /// (no lamp lit: a fire, a blast's flash), from a quarter of the screen covered (whole again under a sixth, so that it does not
        /// go to and fro near the line, as ShipsHalf), the most covered first, two at most. At half size only: the slot's two by two.
        /// </summary>
        void ChooseCarried(int which, Camera camera, Matrix4x4 view, bool half)
        {
            carryChosen.Clear();
            Dictionary<Site, Carried> mine = carried[which];
            bool may = Carrying && half && carryMaterial != null && Settings.TestLayers == 0f;
            int fires = 0, small = 0;
            foreach (Site s in inRange)
            {
                mine.TryGetValue(s, out Carried k);
                if (!may) { if (k != null) k.Covering = false; continue; }
                Vector4 lamps = s.Marching.GetVector(lampsNote);
                if (Mathf.Max(lamps.x, Mathf.Max(lamps.y, lamps.z)) > 0f) { fires++; if (k != null) k.Covering = false; continue; }
                Vector4 r = new Vector4(2f, 2f, -2f, -2f);
                Covers(ref r, view, s.BoxMatrix, camera);
                float covers = r.z > r.x && r.w > r.y ? Mathf.Clamp01((Mathf.Min(r.z, 1f) - Mathf.Max(r.x, -1f)) * (Mathf.Min(r.w, 1f) - Mathf.Max(r.y, -1f)) * 0.25f) : 0f;
                bool covering = k != null && k.Covering ? covers > 1f / 6f : covers > 0.25f;
                if (!covering) { small++; if (k != null) k.Covering = false; continue; }
                if (k == null) { k = new Carried(); mine[s] = k; }
                k.Covering = true;
                k.Covers = covers;
                carryChosen.Add(s);
            }
            // (the most covered kept, the rest walked whole)
            while (carryChosen.Count > CarriedMost)
            {
                int least = 0;
                for (int n = 1; n < carryChosen.Count; n++) if (mine[carryChosen[n]].Covers < mine[carryChosen[least]].Covers) least = n;
                mine[carryChosen[least]].Covering = false;
                carryChosen.RemoveAt(least);
            }
            saidCarried = carryChosen.Count; saidPatches = inRange.Count; saidFires = fires; saidSmall = small;
        }

        /// <summary>
        /// A patch walked a quarter of its slot's pixels this frame, the rest carried over from the frame before (see Carrying), into
        /// its slot of the atlas: true where it is so drawn. Walked whole into its own picture and copied into its slot where what it
        /// was cannot be trusted: its first frame so, a frame missed (the map, a pause), another camera, a slot of another size, the
        /// camera jumped or turned sharply, and (with CarryAnewOnGrid) a new grid.
        /// </summary>
        bool Carry(CommandBuffer c, int which, Camera camera, Site s, int x, int y, Matrix4x4 draws)
        {
            if (!carryChosen.Contains(s) || !carried[which].TryGetValue(s, out Carried k)) return false;
            if (k.WasA == null || k.Width != slotWide || k.Height != slotTall)
            {
                ReleaseCarried(k);
                k.Width = slotWide; k.Height = slotTall;
                int qw = (slotWide + 1) / 2, qh = (slotTall + 1) / 2;
                k.Quarter = Picture(qw, qh, ColourFormat, "VolumetricExplosions smoke walked a quarter at a time");
                k.QuarterDepth = Picture(qw, qh, DepthFormat, "VolumetricExplosions how far off that smoke is");
                k.WasA = Smooth(Picture(slotWide, slotTall, ColourFormat, "VolumetricExplosions smoke as it was (1)"));
                k.WasADepth = Smooth(Picture(slotWide, slotTall, DepthFormat, "VolumetricExplosions how far off it was (1)"));
                k.WasB = Smooth(Picture(slotWide, slotTall, ColourFormat, "VolumetricExplosions smoke as it was (2)"));
                k.WasBDepth = Smooth(Picture(slotWide, slotTall, DepthFormat, "VolumetricExplosions how far off it was (2)"));
                k.Frame = -10;
            }
            Matrix4x4 box = s.BoxMatrix;
            Vector3 eye = camera.transform.position - s.BoxCentre, ahead = camera.transform.forward;
            float gridAge = s.Marching.GetVector(thinNote).y;
            // (A camera's jump between two frames of more than 30 m against the patch, or a turn of more than 15 degrees: a camera
            // switched, or the view cut. The lab's flights at 150 m/s and turns of 90 degrees a second are a fortieth of that.)
            bool trusted = k.Frame == Time.frameCount - 1 && k.Seen == camera && (eye - k.Eye).sqrMagnitude < 900f && Vector3.Angle(ahead, k.Ahead) < 15f
                           && !(CarryAnewOnGrid && gridAge < k.GridAge);
            RenderTexture last = k.AIsLast ? k.WasA : k.WasB, lastDepth = k.AIsLast ? k.WasADepth : k.WasBDepth;
            RenderTexture next = k.AIsLast ? k.WasB : k.WasA, nextDepth = k.AIsLast ? k.WasBDepth : k.WasADepth;
            Vector4 q4 = Vector4.zero;
            s.Marching.SetVector(slotNote, Vector4.zero);
            if (trusted)
            {
                Vector2Int q = turns[k.Turn & 3];
                k.Turn++;
                q4 = new Vector4(q.x, q.y, 1f, which == 1 ? 1f / 64f : 1f);
                // (the picture moved by part of a pixel; where the game draws into its pictures upside down, the move up and down turned over)
                float upside = SystemInfo.graphicsUVStartsAtTop ? -1f : 1f;
                s.Marching.SetVector(shiftNote, new Vector4(2f * (q.x - 0.5f) / slotWide, upside * 2f * (q.y - 0.5f) / slotTall, 0f, 0f));
                s.Marching.SetVector(quarterNote, new Vector4(q.x, q.y, 1f, 0f));
                two[0] = k.Quarter; two[1] = k.QuarterDepth;
                c.SetRenderTarget(two, k.Quarter);
                c.ClearRenderTarget(false, true, Color.clear);
                c.SetViewport(new Rect(0f, 0f, k.Quarter.width, k.Quarter.height));
                c.DrawMesh(Assets.Cube, box, s.Marching, 0, 0);
            }
            else
            {
                // (walked whole into its own picture, which the slot then copies)
                k.Turn = 0;
                q4 = new Vector4(0f, 0f, 2f, which == 1 ? 1f / 64f : 1f);
                s.Marching.SetVector(shiftNote, Vector4.zero);
                s.Marching.SetVector(quarterNote, Vector4.zero);
                two[0] = next; two[1] = nextDepth;
                c.SetRenderTarget(two, next);
                c.ClearRenderTarget(false, true, Color.clear);
                c.SetViewport(new Rect(0f, 0f, slotWide, slotTall));
                c.DrawMesh(Assets.Cube, box, s.Marching, 0, 0);
            }
            // (the numbers of the carrying over: a pixel's ray now, last frame's camera, and how the box has moved since)
            Matrix4x4 inv = draws.inverse;
            Vector3 at = camera.transform.position;
            Matrix4x4 wasDraws = trusted ? k.Draws : draws, moved = trusted ? k.Box * box.inverse : Matrix4x4.identity;
            CarryNumbers(k.Own, q4, inv, at, wasDraws, moved, k, trusted ? last : next, trusted ? lastDepth : nextDepth);
            CarryNumbers(k.Slot, q4, inv, at, wasDraws, moved, k, trusted ? last : next, trusted ? lastDepth : nextDepth);
            // (carried into its own picture of what it was, for the next frame; walked whole, it is that already)
            if (trusted)
            {
                k.Own.SetVector(carryAtNote, new Vector4(0f, 0f, slotWide, slotTall));
                two[0] = next; two[1] = nextDepth;
                c.SetRenderTarget(two, next);
                c.SetViewport(new Rect(0f, 0f, slotWide, slotTall));
                c.DrawMesh(quad, Matrix4x4.identity, carryMaterial, 0, 0, k.Own);
            }
            // (and into its slot, which the sheet lays on the screen as before)
            k.Slot.SetVector(carryAtNote, new Vector4(x, y, slotWide, slotTall));
            c.SetRenderTarget(atlasBoth, atlas);
            c.SetViewport(new Rect(x, y, slotWide, slotTall));
            c.DrawMesh(quad, Matrix4x4.identity, carryMaterial, 0, 0, k.Slot);
            k.AIsLast = !k.AIsLast;
            k.Frame = Time.frameCount; k.Seen = camera; k.Box = box; k.Draws = draws; k.Eye = eye; k.Ahead = ahead; k.GridAge = gridAge;
            return true;
        }

        void CarryNumbers(MaterialPropertyBlock b, Vector4 q4, Matrix4x4 inv, Vector3 at, Matrix4x4 wasDraws, Matrix4x4 moved, Carried k, RenderTexture was, RenderTexture wasDepth)
        {
            b.SetVector(carryQNote, q4);
            b.SetVector(carryCamNote, new Vector4(at.x, at.y, at.z, 1f));
            b.SetVector(carryRayXNote, RayRow(inv, at, 0)); b.SetVector(carryRayYNote, RayRow(inv, at, 1)); b.SetVector(carryRayZNote, RayRow(inv, at, 2));
            b.SetVector(carryWasXNote, wasDraws.GetRow(0)); b.SetVector(carryWasYNote, wasDraws.GetRow(1)); b.SetVector(carryWasWNote, wasDraws.GetRow(3));
            b.SetVector(carryBoxXNote, moved.GetRow(0)); b.SetVector(carryBoxYNote, moved.GetRow(1)); b.SetVector(carryBoxZNote, moved.GetRow(2));
            b.SetTexture(carryQuarterNote, k.Quarter); b.SetTexture(carryQuarterDepthNote, k.QuarterDepth);
            b.SetTexture(carryWasNote, was); b.SetTexture(carryWasDepthNote, wasDepth);
        }

        /// <summary>
        /// A row taking a pixel's place on the picture (-1 to 1 each way, then 1 and 0) to the way of its ray from the camera at `at`:
        /// the place on the far plane, less the camera's, times the far plane's own w (the rays are made unit lengths in the shader).
        /// </summary>
        static Vector4 RayRow(Matrix4x4 inv, Vector3 at, int i)
        {
            Vector4 r = inv.GetRow(i), r3 = inv.GetRow(3);
            return new Vector4(r.x - at[i] * r3.x, r.y - at[i] * r3.y, r.z - at[i] * r3.z + r.w - at[i] * r3.w, 0f);
        }

        /// <summary>(Once a frame: the pictures of patches not walked a quarter at a time in the last two frames let go, gone patches' with them.)</summary>
        void LetCarriedGo()
        {
            for (int which = 0; which < 2; which++)
            {
                carriedGone.Clear();
                foreach (KeyValuePair<Site, Carried> e in carried[which]) if (e.Value.Frame < Time.frameCount - 2) carriedGone.Add(e.Key);
                foreach (Site s in carriedGone) { ReleaseCarried(carried[which][s]); carried[which].Remove(s); }
            }
        }

        static void ReleaseCarried(Carried k)
        {
            Release(ref k.Quarter); Release(ref k.QuarterDepth); Release(ref k.WasA); Release(ref k.WasADepth); Release(ref k.WasB); Release(ref k.WasBDepth);
        }

        static RenderTexture Smooth(RenderTexture rt) { rt.filterMode = FilterMode.Bilinear; return rt; }

        /// <summary>For any other camera (and one of the two with nothing of the smoke in its range): the sheet draws nothing.</summary>
        public void NotFor()
        {
            if (material != null) material.SetVector(sheetNote, Vector4.zero);
        }

        /// <summary>Widen the part of the screen the sheet covers to take in a patch's box. (A box with a corner behind the camera can be anywhere on the screen: all of it.)</summary>
        static void Covers(ref Vector4 rect, Matrix4x4 view, Matrix4x4 box, Camera camera)
        {
            Matrix4x4 m = view * box;
            for (int n = 0; n < 8; n++)
            {
                Vector4 p = m * new Vector4(cube[n].x, cube[n].y, cube[n].z, 1f);
                if (p.w < camera.nearClipPlane * 0.5f) { rect = new Vector4(-1f, -1f, 1f, 1f); return; }
                float x = p.x / p.w, y = p.y / p.w;
                if (x < rect.x) rect.x = x;
                if (y < rect.y) rect.y = y;
                if (x > rect.z) rect.z = x;
                if (y > rect.w) rect.w = y;
            }
        }

        /// <summary>
        /// The atlas, with room for so many slots of the size in hand. It is made bigger at once when it must be, in steps
        /// (two slots, four, eight, sixteen), and smaller only once it has had more than twice the room it needs for some
        /// seconds: a crash adds patches one after another, and a new picture each time would be a jolt each time.
        /// </summary>
        void Atlas(int slots, bool oneToARow)
        {
            int wanted = slots <= 1 ? 1 : slots <= 2 ? 2 : slots <= 4 ? 4 : slots <= 8 ? 8 : 16;
            int keep = Mathf.Max(wanted, atlas != null ? capacity : 0);
            int most = Mathf.Max(1, Mathf.Min(16384, SystemInfo.maxTextureSize) / Mathf.Max(1, slotWide));
            int row = oneToARow ? 1 : Mathf.Min(keep, most);
            int rows = (keep + row - 1) / row;
            capacity = keep;
            perRow = row;
            if (atlas != null && atlas.width == row * slotWide && atlas.height == rows * slotTall) return;
            Release(ref atlas);
            Release(ref atlasDepth);
            atlas = Picture(perRow * slotWide, rows * slotTall, ColourFormat, "VolumetricExplosions smoke, each patch in its slot");
            atlasDepth = Picture(perRow * slotWide, rows * slotTall, DepthFormat, "VolumetricExplosions how far off each patch's smoke is");
            material.SetTexture("_VolAtlas", atlas);
            material.SetTexture("_VolAtlasDepth", atlasDepth);
        }

        /// <summary>(Once a frame: an atlas with more than twice the room needed for five seconds is let go, and made again as big as is wanted when next it is.)</summary>
        void Room(int needed, float dt)
        {
            if (atlas == null) return;
            if (needed * 2 < capacity) roomyFor += dt; else roomyFor = 0f;
            if (roomyFor < 5f) return;
            roomyFor = 0f;
            Release(ref atlas);
            Release(ref atlasDepth);
            capacity = 0;
        }

        void Full(int wide, int tall)
        {
            if (full != null && full.width == wide && full.height == tall) return;
            Release(ref full);
            Release(ref fullDepth);
            full = Picture(wide, tall, ColourFormat, "VolumetricExplosions ships' smoke and flames");
            fullDepth = Picture(wide, tall, DepthFormat, "VolumetricExplosions how far off the ships' smoke is");
            material.SetTexture("_VolFull", full);
            material.SetTexture("_VolFullDepth", fullDepth);
        }

        void LetFullGo(float dt)
        {
            if (full == null) return;
            fullUnused += dt;
            if (fullUnused < 5f) return;
            Release(ref full);
            Release(ref fullDepth);
        }

        static RenderTexture Picture(int wide, int tall, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(wide, tall, 0, format) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = name };
            rt.Create();
            return rt;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }

        /// <summary>The sheet the smoke is put on the screen by: four corners, 0 and 1 each way, which the shader lays over the part of the screen the smoke is on.</summary>
        void NeedSheet()
        {
            if (sheet != null) return;
            material = new Material(Assets.Layers) { renderQueue = 3000 };
            quad = new Mesh { name = "VolumetricExplosions sheet for the smoke" };
            quad.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f) };
            quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            // (never left out for being off the screen: where it is drawn is the shader's to say)
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            sheet = new GameObject("VolumetricExplosions layers") { layer = Recipe.Layer };
            sheet.AddComponent<MeshFilter>().sharedMesh = quad;
            sheetRenderer = sheet.AddComponent<MeshRenderer>();
            sheetRenderer.sharedMaterial = material;
            sheetRenderer.shadowCastingMode = ShadowCastingMode.Off;
            sheetRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;      // (see Site.TuneShadow)
            sheetRenderer.receiveShadows = false;
            if (Assets.Carry != null) carryMaterial = new Material(Assets.Carry);
            NotFor();
        }

        /// <summary>Nothing drawn any more: the cameras let go of, the sheet hidden. (The pictures are kept a moment, in case.)</summary>
        public void Off()
        {
            for (int which = 0; which < 2; which++)
            {
                if (attached[which] == null) continue;
                attached[which].RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, commands[which]);
                attached[which] = null;
            }
            if (sheetRenderer != null && sheetRenderer.enabled) sheetRenderer.enabled = false;
        }

        public void Dispose()
        {
            Off();
            for (int which = 0; which < 2; which++) if (commands[which] != null) { commands[which].Release(); commands[which] = null; }
            Release(ref atlas);
            Release(ref atlasDepth);
            Release(ref full);
            Release(ref fullDepth);
            for (int which = 0; which < 2; which++) { foreach (Carried k in carried[which].Values) ReleaseCarried(k); carried[which].Clear(); }
            if (carryMaterial != null) Object.Destroy(carryMaterial);
            carryMaterial = null;
            if (sheet != null) Object.Destroy(sheet);
            if (material != null) Object.Destroy(material);
            if (quad != null) Object.Destroy(quad);
            sheet = null; material = null; quad = null; sheetRenderer = null;
        }
    }
}
