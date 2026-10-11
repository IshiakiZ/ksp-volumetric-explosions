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

            inRange.Clear();
            foreach (Site s in world) if (s.Within(camera)) inRange.Add(s);
            int slots = inRange.Count;
            if (slots > 0)
            {
                Atlas(slots, Settings.TestLayers == 5f);
                atlasBoth[0] = atlas; atlasBoth[1] = atlasDepth;
                c.SetRenderTarget(atlasBoth, atlas);
                c.ClearRenderTarget(false, true, Color.clear);
                for (int k = 0; k < slots; k++)
                {
                    Site s = inRange[k];
                    int x = (k % perRow) * slotWide, y = (k / perRow) * slotTall;
                    s.Marching.SetVector(slotNote, new Vector4(x, y, 0f, 0f));
                    c.SetViewport(new Rect(x, y, slotWide, slotTall));
                    c.DrawMesh(Assets.Cube, s.BoxMatrix, s.Marching, 0, 0);
                    Covers(ref rect, view, s.BoxMatrix, camera);
                }
            }
            int shipsIn = 0;
            foreach (Site s in ships) if (s.Within(camera)) shipsIn++;
            if (shipsIn > 0)
            {
                Full(wide, tall);
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
            }
            if (slots + shipsIn == 0) { NotFor(); return; }
            c.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            // (a few pixels to spare all round: a pixel at the edge takes its smoke from the slot's pixels about it)
            float spareX = 8f / Mathf.Max(1, wide), spareY = 8f / Mathf.Max(1, tall);
            rect = new Vector4(Mathf.Max(-1f, rect.x - spareX), Mathf.Max(-1f, rect.y - spareY), Mathf.Min(1f, rect.z + spareX), Mathf.Min(1f, rect.w + spareY));
            float test = Settings.TestLayers == 1f ? 1f : Settings.TestLayers == 3f ? 2f : Settings.TestLayers == 4f ? 3f : Settings.TestLayers == 6f ? 4f : Settings.TestLayers == 7f ? 5f : 0f;
            material.SetVector(layersNote, new Vector4(slots, shipsIn > 0 ? 1f : 0f, perRow, half ? 1f : 0f));
            material.SetVector(slotsNote, new Vector4(slotWide, slotTall, test, 0f));
            material.SetVector(sheetNote, rect.z > rect.x && rect.w > rect.y ? rect : Vector4.zero);
        }

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
            sheetRenderer.receiveShadows = false;
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
            if (sheet != null) Object.Destroy(sheet);
            if (material != null) Object.Destroy(material);
            if (quad != null) Object.Destroy(quad);
            sheet = null; material = null; quad = null; sheetRenderer = null;
        }
    }
}
