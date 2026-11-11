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
        /// covered; whole again under a sixth, and (since 2026-11-04) only after three quarters of a second so, so that it does not
        /// flicker between the two near the line. (And since then each ship's patch is walked for the ships' picture as it is drawn this
        /// frame even in a frame with no Step, the game paused, when nothing else sets it: see Site.WalkShips. Settings.TestShipsHalf 1:
        /// all as until then.)
        /// </summary>
        public static bool ShipsHalf;
        bool shipsHalfNow;                        // (what the patches were set going with this frame: ShipsHalf as it was when they were)
        float shipsUnderFor;                      // (how long the ships' boxes have covered too little for half size, while it is: see Frame)
        const float ShipsWholeAfter = 0.75f;      // (seconds so before the ships' picture is whole again)
#if DEV
        static float wholeCovered, flamesCovered;
        static readonly int[] flamesSaid = new int[2];   // (the last said, for each of the two cameras: see below)
        static int flamesDrawn;
        static string flamesWhy = "";

        /// <summary>(Development build: the burning patches, how many had their flames walked whole the last frame and how much of the
        /// screen their rectangles cover; and the ships' picture, at half size or not.)</summary>
        public static string Whole()
        {
            Air air = Air.Instance;
            if (air == null) return "not running";
            int burning = 0, ships = 0, all = 0;
            foreach (Site s in air.Sites)
            {
                all++;
                if (s.Riding) ships++;
                if (s.Burning) burning++;
            }
            return all + " patches: " + ships + " a ship's, " + burning + " burning; flames walked whole " + flamesDrawn + ", their rectangles " + (flamesCovered * 100f).ToString("F0")
                   + "% of the screen; the ships' picture " + (ShipsHalf ? "at half size" : "whole") + " (they cover " + (wholeCovered * 100f).ToString("F0") + "%); fireswhole " + Settings.FiresWhole + " " + flamesWhy + " | the first burning one, against this camera: " + FirstFlames(air);
        }
#endif

#if DEV
        static string FirstFlames(Air air)
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return "no camera";
            foreach (Site s in air.Sites)
            {
                if (!s.Burning) continue;
                bool got = s.FlameRect(cam.projectionMatrix * cam.worldToCameraMatrix, out Vector4 r);
                return (got ? "a rectangle " : "none ") + Site.FlameWhy;
            }
            return "none burning";
        }
#endif

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
        // (the picture of the smoke in front, at half the screen's size, for what is drawn as sprites after the sheet: see
        // FrontPass and sprite.glsl. Two pictures: the nearest patch's, and the rest's.)
        RenderTexture front, front1;
        readonly RenderTargetIdentifier[] frontTargets = new RenderTargetIdentifier[2];
        readonly MaterialPropertyBlock frontBlock = new MaterialPropertyBlock();
        static readonly int frontNote = Shader.PropertyToID("_VolFront"), front1Note = Shader.PropertyToID("_VolFront1");
        /// <summary>(Set for every camera, before each draws: 1 once Layers has drawn its picture of the smoke in front for it.)</summary>
        public static readonly int FrontOnNote = Shader.PropertyToID("_VolFrontOn");
        /// <summary>(For testing: each sprite drawn as how much of it shows through the smoke in front, green all, red none.)</summary>
        public static bool TestFront;
        readonly RenderTargetIdentifier[] atlasBoth = new RenderTargetIdentifier[2], fullBoth = new RenderTargetIdentifier[2];
        int capacity, perRow, slotWide, slotTall;
        float roomyFor;                           // how long the atlas has had more than twice the room needed (it is then made smaller)
        float fullUnused;                         // how long since the ships' picture was last drawn into (it is then let go)
        GameObject sheet;
        MeshRenderer sheetRenderer;
        Material material;
        Mesh quad;
        static readonly int slotNote = Shader.PropertyToID("_VolSlot"), layersNote = Shader.PropertyToID("_VolLayers"), slotsNote = Shader.PropertyToID("_VolSlots"), sheetNote = Shader.PropertyToID("_VolSheet");
        static readonly int holeNote = Shader.PropertyToID("_VolHole"), holeWayNote = Shader.PropertyToID("_VolHoleWay");
        static readonly int holeANote = Shader.PropertyToID("_VolHoleA"), holeBNote = Shader.PropertyToID("_VolHoleB"), holeCNote = Shader.PropertyToID("_VolHoleC"),
            holeSlotsNote = Shader.PropertyToID("_VolHoleSlots");
        // (Since 2026-10-25: the burning patches whose flames are walked whole this frame, by their place in inRange, and each one's
        // rectangle of the screen; at most so many, so much of the screen in all: see ForCamera)
        readonly List<int> flaming = new List<int>();
        Vector4[] holes = new Vector4[16];
        const int FlamesMost = 3;
        const float FlamesCoverMost = 0.35f;
        // (a block of settings of its own for each draw that needs one: see Block)
        readonly List<MaterialPropertyBlock> holeBlocks = new List<MaterialPropertyBlock>(), flameBlocks = new List<MaterialPropertyBlock>();

        /// <summary>A rectangle of the screen (0 to 1 each way from its bottom left corner, see Site.FlameRect) as a walk tells where it
        /// is drawing (_VolHole in volume.glsl: upside down where the game draws into its pictures so, Direct3D), drawn in by so much
        /// each way.</summary>
        static Vector4 Walked(Vector4 r, float inX, float inY)
        {
            r = new Vector4(r.x + inX, r.y + inY, r.z - inX, r.w - inY);
            return SystemInfo.graphicsUVStartsAtTop ? new Vector4(r.x, 1f - r.w, r.z, 1f - r.y) : r;
        }

        /// <summary>A block of settings for one draw, emptied: a patch's one material is walked twice in a frame when its flames are
        /// walked whole, each walk with settings of its own.</summary>
        static MaterialPropertyBlock Block(List<MaterialPropertyBlock> pool, int k)
        {
            while (pool.Count <= k) pool.Add(new MaterialPropertyBlock());
            MaterialPropertyBlock b = pool[k];
            b.Clear();
            return b;
        }
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
            if (!Settings.Half || seer == null) { ShipsHalf = false; shipsUnderFor = 0f; }
            else if (ships.Count == 0)
            {
                // (no ships' patch this frame: whole, but after the same wait as below if half, for in a crash the ships' patches
                // go and come again within a second (four crashes of 2026-11-04: half, whole 0.57 s later with none, half again
                // 1.1 s after), each change making both pictures afresh)
                if (ShipsHalf && Settings.TestShipsHalf != 1f && (shipsUnderFor += Time.unscaledDeltaTime) < ShipsWholeAfter) { }
                else { ShipsHalf = false; shipsUnderFor = 0f; }
            }
            else
            {
                Vector4 r = new Vector4(2f, 2f, -2f, -2f);
                Matrix4x4 v = seer.projectionMatrix * seer.worldToCameraMatrix;
                foreach (Site s in ships) Covers(ref r, v, s.BoxMatrix, seer);
                float covered = Area(r);
                bool wanted = ShipsHalf ? covered > 1f / 6f : covered > 0.25f;
                // (Since 2026-11-04: back to whole only once under the line for ShipsWholeAfter seconds running; to half at once, as
                // before, for the frame rate inside a plume. A plume's edge going to and fro across the line went sharp and soft by turns,
                // and the ground fires' flames with it. Settings.TestShipsHalf 1: back at once, as until then.)
                if (ShipsHalf && !wanted && Settings.TestShipsHalf != 1f)
                {
                    shipsUnderFor += Time.unscaledDeltaTime;
                    if (shipsUnderFor < ShipsWholeAfter) wanted = true;
                }
                else shipsUnderFor = 0f;
                ShipsHalf = wanted;
#if DEV
                wholeCovered = covered;
#endif
                // (for testing: 8 always so, 9 never)
                if (Settings.TestLayers == 8f) ShipsHalf = true; else if (Settings.TestLayers == 9f) ShipsHalf = false;
            }
#if DEV
            // (drawn so from the next frame on, which reads it as shipsHalfNow)
            if (ShipsHalf != shipsHalfNow) Addon.Log("the ships' picture now " + (ShipsHalf ? "half size" : "whole") + " (frame " + Time.frameCount + ", drawn so from the next; " + ships.Count + " ships' patches covering " + wholeCovered.ToString("F2") + ")");
#endif
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
            if (inRange.Count > SlotsMost) Trim();
            int slots = inRange.Count;
            if (slots > 0)
            {
                Atlas(slots, Settings.TestLayers == 5f);
                atlasBoth[0] = atlas; atlasBoth[1] = atlasDepth;
                c.SetRenderTarget(atlasBoth, atlas);
                c.ClearRenderTarget(false, true, Color.clear);
                ChooseCarried(which, camera, view, half);
                // (Since 2026-10-25: a burning patch's flames are walked whole, by themselves, into the ships' picture, and their rectangle
                // of the screen is left to that walk, by its walk at half size (but for a couple of its pixels inside the edge) and by the
                // picture: see Site.FlameRect, and _VolHoleA in layers.glsl. Not where the ships' picture is itself at half size; and only
                // so many, so much of the screen in all, that walking them whole costs little: past that the rest are walked at half size,
                // flames and all, as before.)
                flaming.Clear();
                if (holes.Length < slots) holes = new Vector4[slots + 8];
                bool flamesWhole = Settings.FiresWhole && half && !shipsHalfNow;
                float flamesCover = 0f;
                for (int k = 0; k < slots; k++)
                {
                    holes[k] = Vector4.zero;
                    if (!flamesWhole || flaming.Count >= FlamesMost || !inRange[k].FlameRect(view, out Vector4 r)) continue;
                    // (a patch whose flames were walked whole a frame or two ago goes on so a little past the limit: at the limit, as the
                    // fire grew and died back or the camera came and went, its flames went sharp and soft by turns)
                    float cover = (r.z - r.x) * (r.w - r.y);
                    bool was = Time.frameCount - inRange[k].FlamesWholeAt <= 2;
                    if (flamesCover + cover > FlamesCoverMost + (was ? 0.08f : 0f)) continue;
                    flamesCover += cover;
                    holes[k] = r;
                    flaming.Add(k);
                    inRange[k].FlamesWholeAt = Time.frameCount;
                }
#if DEV
                flamesDrawn = flaming.Count; flamesCovered = flamesCover; flamesWhy = flamesWhole ? "" : "not tried: fireswhole " + Settings.FiresWhole + ", half " + half + ", the ships' picture at half size " + shipsHalfNow;
                // (when the fires walked whole change, and above when the ships' picture goes to half size or back: for lining a film's one-frame changes up with the log, see tools/dev/scratch-2026-11-03-pc/lineup.py; since 2026-11-04)
                if (flaming.Count != flamesSaid[which & 1]) { Addon.Log("fires walked whole, camera " + which + ": " + flamesSaid[which & 1] + " -> " + flaming.Count + " (frame " + Time.frameCount + ", " + (flamesWhole ? "covering " + flamesCover.ToString("F2") : flamesWhy) + ")"); flamesSaid[which & 1] = flaming.Count; }
#endif
                for (int k = 0; k < slots; k++)
                {
                    Site s = inRange[k];
                    int x = (k % perRow) * slotWide, y = (k / perRow) * slotTall;
                    if (Carry(c, which, camera, s, x, y, view)) { Covers(ref rect, view, s.BoxMatrix, camera); continue; }
                    s.Marching.SetVector(slotNote, new Vector4(x, y, 0f, 0f));
                    s.Marching.SetVector(shiftNote, Vector4.zero);
                    s.Marching.SetVector(quarterNote, Vector4.zero);
                    c.SetViewport(new Rect(x, y, slotWide, slotTall));
                    if (holes[k].z > holes[k].x)
                    {
                        // (two of its pixels into the rectangle: see _VolHoleA in layers.glsl)
                        MaterialPropertyBlock b = Block(holeBlocks, k);
                        b.SetVector(holeNote, Walked(holes[k], 4f / wide, 4f / tall));
                        b.SetVector(holeWayNote, new Vector4(1f, 0f, 0f, 0f));
                        c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0, b);
                    }
                    else c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0);
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
            if (slots == 0) flaming.Clear();
            if (shipsIn > 0 || flaming.Count > 0)
            {
                Full(shipsHalfNow ? slotWide : wide, shipsHalfNow ? slotTall : tall);
                fullBoth[0] = full; fullBoth[1] = fullDepth;
                c.SetRenderTarget(fullBoth, full);
                c.ClearRenderTarget(false, true, Color.clear);
                foreach (Site s in ships)
                {
                    if (!s.Within(camera)) continue;
                    s.Marching.SetVector(slotNote, Vector4.zero);
                    // (walked for the picture as it is this frame. Its Step has set it so already in a frame that runs (TuneVolume, from
                    // ShipsHalf as the frame before left it, which is shipsHalfNow), and this writes the same again; but paused, or in a
                    // frame of no length, there is no Step, the camera can still turn and the picture change size, and walked at half size
                    // into the whole picture its smoke was cut by the depth of the wrong pixels of the scene till the game went on. A ship's
                    // patch is walked once for each camera, into this picture only, so its own material, read when the camera draws,
                    // holds. Settings.TestShipsHalf 1: as its Step last left it.)
                    if (Settings.TestShipsHalf != 1f) s.WalkShips(shipsHalfNow && half);
                    c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0);
                    Covers(ref rect, view, s.BoxMatrix, camera);
                }
                // (and the burning patches' flames, walked whole, each within its own rectangle only: see above)
                for (int n = 0; n < flaming.Count; n++)
                {
                    Site s = inRange[flaming[n]];
                    MaterialPropertyBlock b = Block(flameBlocks, n);
                    s.Whole(b);
                    b.SetVector(slotNote, Vector4.zero);
                    b.SetVector(shiftNote, Vector4.zero);
                    b.SetVector(quarterNote, Vector4.zero);
                    b.SetVector(holeNote, Walked(holes[flaming[n]], 0f, 0f));
                    b.SetVector(holeWayNote, new Vector4(2f, 0f, 0f, 0f));
                    c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0, b);
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
            // (a few pixels to spare all round: a pixel at the edge takes its smoke from the slot's pixels about it)
            float spareX = 8f / Mathf.Max(1, wide), spareY = 8f / Mathf.Max(1, tall);
            rect = new Vector4(Mathf.Max(-1f, rect.x - spareX), Mathf.Max(-1f, rect.y - spareY), Mathf.Min(1f, rect.z + spareX), Mathf.Min(1f, rect.w + spareY));
            FrontPass(c, wide, tall, rect);
            c.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            float test = Settings.TestLayers == 1f ? 1f : Settings.TestLayers == 3f ? 2f : Settings.TestLayers == 4f ? 3f : Settings.TestLayers == 6f ? 4f : Settings.TestLayers == 7f ? 5f : 0f;
            material.SetVector(layersNote, new Vector4(slots, shipsIn + flaming.Count > 0 ? (shipsHalfNow ? 2f : 1f) : 0f, perRow, half ? 1f : 0f));
            material.SetVector(slotsNote, new Vector4(slotWide, slotTall, test, 0f));
            material.SetVector(sheetNote, rect.z > rect.x && rect.w > rect.y ? rect : Vector4.zero);
            // (and the burning patches whose flames were walked whole: within each one's rectangle, its slot is passed over)
            int holed = flaming.Count;
            material.SetVector(holeANote, holed > 0 ? Pixels(holes[flaming[0]], wide, tall) : Vector4.zero);
            material.SetVector(holeBNote, holed > 1 ? Pixels(holes[flaming[1]], wide, tall) : Vector4.zero);
            material.SetVector(holeCNote, holed > 2 ? Pixels(holes[flaming[2]], wide, tall) : Vector4.zero);
            material.SetVector(holeSlotsNote, new Vector4(holed > 0 ? flaming[0] : -10f, holed > 1 ? flaming[1] : -10f, holed > 2 ? flaming[2] : -10f, holed > 0 ? 1f : 0f));
        }

        /// <summary>
        /// (Since 2026-11-04.) No more patches for one camera than the sheet's shader puts on the screen, fifteen: its walk goes
        /// through fifteen slots of the atlas, and the sixteenth place in its front pass is the ships' picture, not a slot
        /// (tools/shaderpack/layers.glsl). With fifteen patches staying (Settings.Patches, Air.Most) and those thinning away or giving
        /// their smoke to another beside them, a battle can have more in one camera's range; the sixteenth, the nearest, was walked
        /// into the atlas and never shown. Left out for this camera, this frame: those thinning away first (they are going), the farthest
        /// first; then the farthest. (inRange is the farthest first.)
        /// </summary>
        void Trim()
        {
#if DEV
            int was = inRange.Count;
#endif
            for (int k = 0; k < inRange.Count && inRange.Count > SlotsMost;)
                if (inRange[k].Leaving) inRange.RemoveAt(k); else k++;
            if (inRange.Count > SlotsMost) inRange.RemoveRange(0, inRange.Count - SlotsMost);
#if DEV
            if (Time.frameCount - trimSaid > 60) { trimSaid = Time.frameCount; Addon.Log("more patches in a camera's range than the atlas has slots: " + was + ", " + (was - inRange.Count) + " left out (frame " + Time.frameCount + ")"); }
#endif
        }
        const int SlotsMost = 15;
#if DEV
        int trimSaid = -100;
#endif

        /// <summary>
        /// The picture of the smoke in front (layers.glsl's start of main), drawn by the sheet's own shader at half the screen's size, after
        /// every patch is walked and before the sheet puts them on the screen; then handed to everything drawn after (sprite.glsl), with
        /// _VolFrontOn. A sprite behind a cloud of smoke is then hidden by as much of it as lies in front of it, pixel by pixel. (Until
        /// 2026-10-28 each sprite was laid over all the smoke or under all of it, by its queue: the owner, "fire or other smoke will
        /// show in front of the main smoke even if it's behind".) Only with the sprite shader there to read it, and pictures of sixteen
        /// bits a number to keep how far off the smoke is.
        /// </summary>
        void FrontPass(CommandBuffer c, int wide, int tall, Vector4 rect)
        {
            if (!Assets.FrontReady || ColourFormat != RenderTextureFormat.ARGBHalf || !(rect.z > rect.x && rect.w > rect.y)) return;
            int fw = (wide + 1) / 2, ft = (tall + 1) / 2;
            if (front == null || front.width != fw || front.height != ft)
            {
                Release(ref front);
                Release(ref front1);
                front = Picture(fw, ft, RenderTextureFormat.ARGBHalf, "VolumetricExplosions the smoke in front: the nearest patch's");
                front1 = Picture(fw, ft, RenderTextureFormat.ARGBHalf, "VolumetricExplosions the smoke in front: the rest's");
            }
            frontTargets[0] = front;
            frontTargets[1] = front1;
            c.SetRenderTarget(frontTargets, front);
            c.ClearRenderTarget(false, true, Color.clear);
            frontBlock.SetVector(slotsNote, new Vector4(slotWide, slotTall, 0f, 1f));
            frontBlock.SetVector(sheetNote, rect);
            c.DrawMesh(quad, Matrix4x4.identity, material, 0, 0, frontBlock);
            c.SetGlobalTexture(frontNote, front);
            c.SetGlobalTexture(front1Note, front1);
            c.SetGlobalVector(FrontOnNote, new Vector4(1f, TestFront ? 1f : 0f, 0f, 0f));
        }

        /// <summary>A rectangle of the screen (0 to 1 each way from its bottom left corner) in pixels, as the picture tells where it is.</summary>
        static Vector4 Pixels(Vector4 r, int wide, int tall) => new Vector4(r.x * wide, r.y * tall, r.z * wide, r.w * tall);

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
                float covers = Area(r);
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

        /// <summary>What Covers found of a box: nothing of it before the camera, some of it, or (the camera in it) the whole screen.</summary>
        internal enum Reach { None, Part, All }

        /// <summary>
        /// Widen the part of the screen the sheet covers to take in a patch's box (and, from Site.Sight, how much of the screen a patch
        /// covers, for which grid comes first: the one rule for both).
        ///
        /// Until 2026-11-04 a box with a corner less than half the near distance ahead of the camera was taken as anywhere on the screen,
        /// all of it. A ship's box with one corner just behind the camera, as a ship's patch is when the camera flies by its trail or sits by
        /// its nozzle, then counted as the whole screen: the ships' picture went to half size at once and back the next moment, and with it
        /// the ground fires' flames from sharp to soft, the whole picture in one frame (the 34 % flash at 29.5 s in fire-crash-lineup-e).
        /// Now the box is cut where it crosses that plane (each of its twelve edges cut there, and the cut points taken with the corners in
        /// front of it), and only what is in front counts. That holds every pixel that can show the box's smoke: a ray's smoke is walked
        /// only from the near plane on and anything nearer thrown away (tools/shaderpack/volume.glsl), so a pixel shows it only where the
        /// back of the box lies past the near plane, within the part of the box in front of half of it; and that part, a box cut by a
        /// plane, seen in perspective, lies within the rectangle of its corners on the screen. (On Direct3D the card does not cut the box at
        /// the near plane itself, volume.glsl holding its depth in; this does not lean on that. research/layers/covers-flash.md.)
        /// A box that holds the camera is still all the screen: it is all round it. One wholly behind adds nothing.
        /// (Settings.TestShipsHalf 1: as until then, everywhere this rule is used.)
        /// </summary>
        internal static Reach Covers(ref Vector4 rect, Matrix4x4 view, Matrix4x4 box, Camera camera)
        {
            Matrix4x4 m = view * box;
            float least = camera.nearClipPlane * 0.5f;
            bool behind = false, any = false;
            for (int n = 0; n < 8; n++)
            {
                Vector4 p = m * new Vector4(cube[n].x, cube[n].y, cube[n].z, 1f);
                seenCorners[n] = p;
                if (p.w < least) { behind = true; continue; }
                Take(ref rect, p.x / p.w, p.y / p.w);
                any = true;
            }
            if (!behind) return Reach.Part;
            // (the camera in the box, or the old rule: all of it)
            Vector3 inBox = box.inverse.MultiplyPoint3x4(camera.transform.position);
            if (Settings.TestShipsHalf == 1f || Mathf.Abs(inBox.x) <= 0.5f && Mathf.Abs(inBox.y) <= 0.5f && Mathf.Abs(inBox.z) <= 0.5f)
            {
                rect = new Vector4(-1f, -1f, 1f, 1f);
                return Reach.All;
            }
            // (each edge from a corner behind the plane to one in front of it, cut where it crosses: there its w is the plane's)
            for (int e = 0; e < 24; e += 2)
            {
                Vector4 a = seenCorners[boxEdges[e]], b = seenCorners[boxEdges[e + 1]];
                if ((a.w < least) == (b.w < least)) continue;
                Vector4 q = a + (b - a) * ((least - a.w) / (b.w - a.w));
                Take(ref rect, q.x / least, q.y / least);
                any = true;
            }
            return any ? Reach.Part : Reach.None;
        }
        static readonly Vector4[] seenCorners = new Vector4[8];   // (Covers's, one call at a time: on the main thread only)
        // (the twelve edges of the box, the corners as Corners numbers them: those one bit apart)
        static readonly int[] boxEdges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };

        static void Take(ref Vector4 rect, float x, float y)
        {
            if (x < rect.x) rect.x = x;
            if (y < rect.y) rect.y = y;
            if (x > rect.z) rect.z = x;
            if (y > rect.w) rect.w = y;
        }

        /// <summary>
        /// How much of the screen a rectangle of it covers (-1 to 1 each way), 0 to 1. (Each way's overlap no less than nothing, as
        /// Site.Sight has it: until 2026-11-04 a rectangle wholly off the screen past a corner, its two overlaps each below nothing, gave
        /// their product, as much as the whole screen. Settings.TestShipsHalf 1: so again.)
        /// </summary>
        internal static float Area(Vector4 r)
        {
            if (Settings.TestShipsHalf == 1f)
                return r.z > r.x && r.w > r.y ? Mathf.Clamp01((Mathf.Min(r.z, 1f) - Mathf.Max(r.x, -1f)) * (Mathf.Min(r.w, 1f) - Mathf.Max(r.y, -1f)) * 0.25f) : 0f;
            return Mathf.Clamp01(Mathf.Max(0f, Mathf.Min(r.z, 1f) - Mathf.Max(r.x, -1f)) * Mathf.Max(0f, Mathf.Min(r.w, 1f) - Mathf.Max(r.y, -1f)) * 0.25f);
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
            Release(ref front);
            Release(ref front1);
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
