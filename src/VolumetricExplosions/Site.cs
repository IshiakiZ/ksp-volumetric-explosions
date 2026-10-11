using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// One patch of air with explosions in it. Fire, smoke, dust and spray are all the same thing here:
    /// small particles carried by a model of the air around them (the blast's push, a rising bubble of hot
    /// gas that rolls over itself, wind, eddies, and the wash of ships and their engines), and shaded
    /// as a volume: the particles are counted into a grid, light is followed through the grid from the
    /// sun and from the sky, and each particle takes the light that reaches the place where it is.
    ///
    /// The game's own particle system only draws them. Nothing here is a picture of an explosion.
    /// </summary>
    public sealed partial class Site
    {
        public struct P
        {
            public float x, y, z;            // position in the site's frame: x east, y up, z north, metres
            public float vx, vy, vz;         // velocity; follows the air
            public float kx, ky, kz;         // extra velocity from a blast, which the air takes away
            public float r, grow, born;      // radius, how fast it spreads by itself, and the radius it started with
            public float swell;              // how much wider it gets for each metre a blast carries it (thin air only: see Wide)
            public float mass;               // of smoke in it: how much it hides is mass / r squared
            public float heat, cool;         // 1 white hot to 0 out, and how fast it cools, per second
            public float age, life, fadeIn;
            public float rise;               // climbs (negative: settles) at this speed
            public float drag, kloss;        // how firmly the air carries it, and how fast it takes the blast's push away, per second
            public float turb;               // how hard it is being stirred, metres a second
            public float turn, pace, octave; // how far round it is in its billows' round of turns (0 to 1); how fast the air where it is would have it go round, rounds a second; and which of the rounds it is on (see Afresh)
            public float rate;               // how fast it is in fact going round just now, rounds a second
            public float ux, uy, uz;         // the air's own motion where it is, as last looked up
            public float ax, ay, az;         // where this bit of smoke "was": the place whose billows it carries (see Afresh). Three reckonings for the big billows, which take turns (a, b, e),
            public float bx, by, bz;
            public float ex, ey, ez;
            public float cx, cy, cz;         // and three for the small ones, which take turns twice as fast (c, d, f)
            public float dx, dy, dz;
            public float fx, fy, fz;
            public float lit, sky, glow;     // sunlight reaching it, open sky above it, firelight on it
            public float roll, spin;
            public float ar, ag, ab;         // the colour of the smoke or dust itself
            public float flame;              // how brightly it burns (0 for dust and vapour)
            public byte tint, tile, flags;
            public uint seed;
        }

        const byte Ballistic = 1, DiesOnGround = 2, Exhausted = 4;      // (Exhausted: it came out of an engine: see the ships' wash in the mover)
        const byte Raised = 16;              // (an engine's young smoke that has taken up the ground's dust or spray where its jet struck: see the mover)
        const double StillAir = 0.9;         // how much of the time the air is still, as it comes (see Wind)
        const float Downhill = 4f;           // metres a second: how fast heavy smoke lying on the ground would run down a slope that was all but sheer (see the mover)

        struct Thermal { public float x, y, z, a, w, top, turn; }
        struct Lamp { public float x, y, z, vx, vy, vz, r, peak, power, age, life, climb, flash; public byte tint; public bool steady, quiet; public int owner; }      // (vx, vy, vz: the fire it is the light of was thrown on at this speed, which the air takes away)
        struct Fire { public float x, y, z, r, left, total, rate, due, soot, shade, scale, puff, tall, lick; public byte tint; public int lamp; }
        struct Frag { public float x, y, z, vx, vy, vz, age, life, due, size; public byte tint; public bool white, down; }
        struct Ball { public float x, y, z, vx, vy, vz, r, speed; }
        struct Jet { public float x, y, z, dx, dy, dz, r, speed, length; }
        struct Wave { public float x, y, z, start, radius, reach, age, power; public bool ground; }

        // ---- where
        public readonly CelestialBody body;
        public readonly bool space;
        readonly Frame frame;
        readonly GameObject root;
        readonly Transform t;

        // ---- the particles
        P[] p = new P[4096];
        P[] seen = new P[4096];              // the particles as they were when the grid now being worked out was begun
        ParticleSystem.Particle[] draw = new ParticleSystem.Particle[4096];
        int count;
        readonly ParticleSystem cloud, glowBack, glowFront;

        // ---- the same smoke and fire as a volume, where the game can draw one (see Lighting and the shader in tools/shaderpack)
        GameObject box;
        MeshRenderer boxRenderer;
        Material boxMaterial, cloudMaterial;
        Material shownMaterial;              // where the smoke is drawn at half size: what puts that on the screen (see Air.Halves)
        RenderTexture small;
        bool halved;
        Texture3D volume, amounts, around, flow, clear, turns;
        readonly Texture3D[] rests = new Texture3D[5];          // eighteen numbers of sixteen bits, four to a texture
        byte flameKind;                      // which fire's colours the volume shows: those of the biggest fire lately
        float flameWeight;
        Vector3 dustHue = new Vector3(1f, 0.85f, 0.65f);
        Color groundDust;
        bool groundBare, groundWet;
        ParticleSystem sparks, shards, ringFlat, ringAir;
        readonly ParticleSystem.Particle[] glows = new ParticleSystem.Particle[160];

        readonly Thermal[] thermals = new Thermal[24];
        readonly Lamp[] lamps = new Lamp[12];
        readonly Fire[] fires = new Fire[8];
        readonly Frag[] frags = new Frag[96];
        readonly Ball[] balls = new Ball[40];
        readonly Jet[] jets = new Jet[16];
        readonly Wave[] waves = new Wave[4];
        int thermalCount, lampCount, fireCount, fragCount, ballCount, jetCount, waveCount;
        float sound = 340f;                  // the speed of sound here, metres a second
        readonly Light[] lights = new Light[2];
        static int lightsOn;

        // ---- the surroundings
        float air, grip, gravity;            // density against Kerbin at sea level; how firmly that air holds things; m/s²

        /// <summary>
        /// Where the air is thin a blast goes further before the air has stopped it (see the mover), and what it
        /// throws out ends up that much further apart. Each puff of it gets wider by as much on the way, and no
        /// heavier: the same smoke spread through more room. Otherwise a blast on Duna, or high up, is a scatter
        /// of separate balls and not a cloud.
        /// </summary>
        float Wide => vacuum ? 1f : 1f / Mathf.Clamp(grip, 0.55f, 1f);
        /// <summary>How thin the air is, as an engine's flame feels it: 0 at the ground, a quarter at a tenth of the air, 1 with none (by how many times thinner, counted in tens).</summary>
        float Thin => vacuum ? 1f : Mathf.Pow(Mathf.Clamp01(-Mathf.Log10(Mathf.Max(air, 1e-4f)) / 3.2f), 1.2f);
        bool vacuum, sunUp;
        Vector3 sunL;                        // towards the sun, in the site's frame
        float sunR, sunG, sunB, ambR, ambG, ambB, fireR = 1f, fireG = 0.4f, fireB = 0.1f;
        float windX, windZ, wind10, calm;
        float gustScale = 1f;                // how gusty the wind is against what the air makes up itself (a weather mod's wind: see Sources.Wind)
        float windGoneX, windGoneZ;          // how far the air has been carried since this began: the eddies in it are carried with it
        bool hasGround;                      // (what the ground is like is in SiteGround.cs)
        float bx0, by0, bz0, bx1, by1, bz1;  // a box round all the particles

        // ---- this frame
        float time, stepDt, lastBlast, lastRing = -10f, nextLook, scale = 4f;
        float fireballR, fireballAt = -100f;     // the biggest blast of the last few seconds: its radius and when (for the size of its flames' pattern, see TuneFire)
        float blazeWide, blazeSeen = -100f;      // the widest fire burning on the ship this patch goes along with, and when last (see Blaze)
        int tick, chunks;
        Vector3 camL;
        readonly Action<int> chunkBody;
        static Light sunLamp;
        static readonly float[] sine = Sines();

        public int Count => count;
        public bool Finished => !(riding && frame.Riding && time - fedAt < 5f) && count == 0 && fireCount == 0 && fragCount == 0 && lampCount == 0 && waveCount == 0 && time - lastBlast > 4f && pieces.Count == 0 && toBreak.Count == 0 && waiting.Count == 0
                                && (sparks == null || sparks.particleCount == 0) && (shards == null || shards.particleCount == 0);

        /// <summary>A patch of air where something has happened. (rides: a patch that goes along with a ship, for the flames of its engines: see Frame, and Under.)</summary>
        public Site(Plan plan, Transform rides = null)
        {
            body = plan.body;
            riding = rides != null;
            space = plan.space && !riding;
            if (riding) frame = new Frame(body, plan.world, rides);
            else
            {
                // Fixed to the ground under the blast where there is ground near; otherwise to the spot itself.
                Vector3d anchor = !space && plan.surfaceBelow && plan.height < 80f ? plan.surfacePoint : plan.world;
                frame = new Frame(body, anchor, space, plan.velocity + body.getRFrmVel(plan.world));
            }
            root = new GameObject("VolumetricExplosions site") { layer = Recipe.Layer };
            t = root.transform;
            frame.Apply(t);
            cloudMaterial = new Material(Assets.Cloud);
            cloud = Recipe.Drawn(t, "Cloud", cloudMaterial, 0f, Settings.MaxParticles, true);
            glowBack = Recipe.Drawn(t, "Glow", Assets.GlowBehind, 80f, glows.Length, false);
            glowFront = Recipe.Drawn(t, "Flash", Assets.Glow, -60f, glows.Length, false);
            if (Assets.Volume != null)
            {
                box = new GameObject("Volume") { layer = Recipe.Layer };
                box.transform.SetParent(t, false);
                box.AddComponent<MeshFilter>().sharedMesh = Assets.Cube;
                boxRenderer = box.AddComponent<MeshRenderer>();
                boxMaterial = new Material(Assets.Volume) { renderQueue = 3000 };
                volume = new Texture3D(G, G, G, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                amounts = new Texture3D(G, G, G, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                around = new Texture3D(A, A, A, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                flow = new Texture3D(A, A, A, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                for (int n = 0; n < rests.Length; n++) rests[n] = new Texture3D(A, A, A, Assets.Sixteen, UnityEngine.Experimental.Rendering.TextureCreationFlags.None) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                boxMaterial.SetTexture("_Volume", volume);
                boxMaterial.SetTexture("_Amount", amounts);
                boxMaterial.SetTexture("_Around", around);
                boxMaterial.SetTexture("_Detail", Assets.Detail);
                boxMaterial.SetTexture("_Flow", flow);
                for (int n = 0; n < rests.Length; n++) boxMaterial.SetTexture("_Rest" + (char)('A' + n), rests[n]);
                // (one byte a cell, read cell by cell: see Clearance)
                clear = new Texture3D(A, A, A, TextureFormat.R8, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
                boxMaterial.SetTexture("_Clear", clear);
                turns = new Texture3D(A, A, A, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                boxMaterial.SetTexture("_Turns", turns);
                boxRenderer.sharedMaterial = boxMaterial;
                if (Assets.Enlarge != null) shownMaterial = new Material(Assets.Enlarge) { renderQueue = 3000 };
                // (A ship's patch is drawn after those that stay where they are. Its box and its trail's overlap, and two
                // volumes cannot be drawn into one another: drawn in whichever order their boxes happened to be sorted, the
                // flame now showed through the trail's smoke and now did not.)
                if (riding) { boxMaterial.renderQueue = 3001; if (shownMaterial != null) shownMaterial.renderQueue = 3001; }
                boxRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                boxRenderer.receiveShadows = false;
                boxRenderer.enabled = false;
            }
            chunkBody = RunChunk;
            lightBody = Lighting;
            depositBody = Deposit;
            composeBody = Compose;
            clearLayer = ClearLayer;
            gatherBody = Gather;
            aroundBody = () => { long t = System.Diagnostics.Stopwatch.GetTimestamp(); Around(); Air.Stage[4] = System.Diagnostics.Stopwatch.GetTimestamp() - t; };
            aroundLayer = AroundLayer;
            aroundSmooth = AroundSmooth;
            carryBody = () => { long t = System.Diagnostics.Stopwatch.GetTimestamp(); Carry(); Air.Stage[5] = System.Diagnostics.Stopwatch.GetTimestamp() - t; };
            carrySmooth = CarrySmooth;
            for (int c = 0; c < Carried; c++) { carryNext[c] = new float[A3]; carryNow[c] = new float[A3]; }
            for (int n = 0; n < cellsRest.Length; n++) cellsRest[n] = new ushort[A3 * 4];
            sunBody = () => { long t = System.Diagnostics.Stopwatch.GetTimestamp(); Follow(gridSun, sunNext, 0.45f, layers[0], layers[1]); Air.Stage[2] = System.Diagnostics.Stopwatch.GetTimestamp() - t; };        // (less than the smoke really blocks: light also finds its way round inside a cloud)
            skyBody = () => { long t = System.Diagnostics.Stopwatch.GetTimestamp(); Follow(gridUp, skyNext, 0.55f, layers[2], layers[3]); Air.Stage[3] = System.Diagnostics.Stopwatch.GetTimestamp() - t; };
            jobBody = Moving;
            if (Native.Ready) NativeBegin();
            if (nativeWork == IntPtr.Zero) NeedRoom();
            Look();
            Wind(plan);
            if (plan.surfaceBelow && !space && !riding) Ground(plan.surfacePoint, plan.surfaceNormal);
            // (what the ground here is, for what an engine's jet raises from it: see Vents)
            groundDust = plan.dust; groundBare = plan.surfaceBelow && !plan.paved && !plan.water; groundWet = plan.water;
            float strongest = Mathf.Max(plan.dust.r, Mathf.Max(plan.dust.g, plan.dust.b));
            if (strongest > 0.01f) dustHue = new Vector3(plan.dust.r, plan.dust.g, plan.dust.b) / strongest;
            bx0 = by0 = bz0 = -1f; bx1 = by1 = bz1 = 1f;
        }

        public void Dispose()
        {
            if (job != null) { try { job.Wait(); } catch (Exception) { } job = null; }
            if (raysOut) { raysOut = false; rayJob.Complete(); rays.Dispose(); hits.Dispose(); }
            EndSurvey();
            for (int n = 0; n < lights.Length; n++)
                if (lights[n] != null && lights[n].enabled) { lights[n].enabled = false; lightsOn = Mathf.Max(0, lightsOn - 1); }
            if (lighting != null) { try { lighting.Wait(); } catch (Exception) { } lighting = null; }
            NativeEnd();
            RemovePieces();
            if (volume != null) UnityEngine.Object.Destroy(volume);
            if (amounts != null) UnityEngine.Object.Destroy(amounts);
            if (around != null) UnityEngine.Object.Destroy(around);
            if (flow != null) UnityEngine.Object.Destroy(flow);
            if (clear != null) UnityEngine.Object.Destroy(clear);
            if (turns != null) UnityEngine.Object.Destroy(turns);
            foreach (Texture3D rest in rests) if (rest != null) UnityEngine.Object.Destroy(rest);
            if (boxMaterial != null) UnityEngine.Object.Destroy(boxMaterial);
            if (shownMaterial != null) UnityEngine.Object.Destroy(shownMaterial);
            if (shadeMaterial != null) UnityEngine.Object.Destroy(shadeMaterial);
            if (small != null) { small.Release(); UnityEngine.Object.Destroy(small); }
            if (cloudMaterial != null) UnityEngine.Object.Destroy(cloudMaterial);
            if (root != null) UnityEngine.Object.Destroy(root);
            count = 0;
        }

        /// <summary>Hand the grid that has just been finished to the shader, with the box it fills.</summary>
        void ShowVolume()
        {
            volume.SetPixels32(cellsA);
            volume.Apply(false);
            amounts.SetPixels32(cellsB);
            amounts.Apply(false);
            around.SetPixels32(cellsC);
            around.Apply(false);
            flow.SetPixels32(cellsD);
            flow.Apply(false);
            for (int n = 0; n < rests.Length; n++) { rests[n].SetPixelData(cellsRest[n], 0); rests[n].Apply(false); }
            clear.SetPixelData(cellsClear, 0);
            clear.Apply(false);
            turns.SetPixels32(cellsE);
            turns.Apply(false);
            var size = new Vector3(nx1 - nx0, ny1 - ny0, nz1 - nz0);
            var middle = new Vector3((nx0 + nx1) * 0.5f, (ny0 + ny1) * 0.5f, (nz0 + nz1) * 0.5f);
            boxMiddle = middle;
            slid = Vector3.zero;
            box.transform.localRotation = turned ? Quaternion.LookRotation(turnZ, turnY) : Quaternion.identity;
            box.transform.localPosition = FromGrid(middle);
            box.transform.localScale = size;
            boxMaterial.SetVector("_VolSize", size);
            boxMaterial.SetVector("_VolOffset", middle);
            boxRenderer.enabled = true;
        }

        /// <summary>The settings of the volume that change every frame.</summary>
        void TuneVolume()
        {
            if (box == null || !boxRenderer.enabled) return;
            // The grid in use was made from the smoke as it was a moment ago; the shader carries it on by as far as
            // the smoke has moved since (see Carry), and where the whole cloud is flying along, the box goes with it
            // (see shownDrift). (For testing: 1 leaves the smoke where the grid has it.)
            float since = Settings.TestSmoke == 1f ? 0f : Mathf.Clamp(time - shownTime, 0f, 0.3f);
            // (as far as something goes that started at that speed and is losing it to the air at that rate)
            float slowed = shownSlows * since;
            slid = shownDrift * (slowed > 1e-3f ? (1f - Mathf.Exp(-slowed)) / shownSlows : since);
            box.transform.localPosition = FromGrid(boxMiddle + slid);
            // From inside the cloud, what lies outside it is seen through the smoke: draw those sprites first. From outside, after.
            Vector3 eye = ToGrid(camL) - slid;
            bool within = eye.x > gx0 && eye.y > gy0 && eye.z > gz0 && eye.x < gx0 + G / gix && eye.y < gy0 + G / giy && eye.z < gz0 + G / giz;
            cloudMaterial.renderQueue = within ? 2995 : 3001;
            float billows = Mathf.Clamp01(grip * 1.6f);                           // it takes air to make smoke billow: none in a vacuum, less where the air is thin
            float repeat = detailRepeat;                                          // fixed for the site: detail that changed scale with the cloud would swim
            Vector3 gone = ToGrid(new Vector3((float)shownGone.x, (float)shownGone.y, (float)shownGone.z));
            boxMaterial.SetVector("_VolDetail", new Vector4(gone.x, gone.y, gone.z, 1f / repeat));
            // (never by more than a cell or so: the shader passes over empty air in strides of about that, on the strength of there being no smoke within one)
            boxMaterial.SetVector("_VolFlow", new Vector4(2f * shownFastest * since, Assets.SixteenAsHalves ? 1f : 2f * RestFar, 0.9f * cellNow, Assets.SixteenAsHalves ? 0f : -RestFar));
            // The finest step along a ray is fixed for the site too, at half a cell of the detail: the places a ray is
            // sampled at must not shift as the cloud grows. Where there is no smoke the steps are as long as the
            // grid's cells, a whole number of fine steps.
            float texel = repeat / Assets.DetailCells, finest = 0.5f * texel;
            float stride = Mathf.Pow(2f, Mathf.Floor(Mathf.Log(Mathf.Max(cellNow / finest, 1f), 2f)));
            // Drawn at half size and enlarged, or straight onto the screen. At half size a pixel is two of the screen's
            // wide, so steps may be twice as long and the smallest lumps drawn twice as big.
            // (A ship's flames are drawn whole, not at half size: their fine tongues and streaks are what makes them fire,
            // and enlarged from half size they were a blur of colour. Their box is small; it costs little.)
            // (Since 0.5.0 every patch's smoke is put on the screen together, in depth order, by Air's Layers: the box itself is not
            // drawn then, only walked into a picture of its own. See Layers.)
            bool layered = Air.Layered;
            halved = Settings.Half && !riding && (layered || shownMaterial != null);
            Material shown = halved && !layered ? shownMaterial : boxMaterial;
            if (boxRenderer.sharedMaterial != shown) boxRenderer.sharedMaterial = shown;
            if (boxRenderer.forceRenderingOff != layered) boxRenderer.forceRenderingOff = layered;
            float pixel = halved ? 2f : 1f;
            boxMaterial.SetVector("_VolStep", new Vector4(finest, 0f, 0f, 0f));
            boxMaterial.SetVector("_VolGrid", new Vector4(stride, pixel * 1.6f / Mathf.Sqrt(Settings.Quality), pixel * 1.6f / texel, Settings.TestView));
            // (at half size there are a quarter as many rays to walk, and each may be walked further in fine steps)
            boxMaterial.SetVector("_VolParams", new Vector4(Thickest, Mathf.Clamp(56f * Settings.Quality + 16f, 24f, 128f) * (halved ? 1.5f : 1f), Mathf.Clamp(Settings.Detail * 1.15f * billows, 0f, 0.85f), ThickestFlame));
            // (An engine's jet is a smoother thing than a fire in the open, and with no air to tear it, quite smooth.)
            float smooth = riding ? Mathf.Lerp(0.6f, 1f, Thin) : 0f;
            boxMaterial.SetVector("_VolPeak", new Vector4(halved ? 1f : 0f, smooth, Settings.TestLattice, fullLength));
            // (smoke too thin to see is passed over: up to this much of what is behind it hidden, in all, along a ray. See the shader.)
            // (and how long ago the grid in use was made: the billows have gone on taking their turns since)
            boxMaterial.SetVector("_VolThin", new Vector4(Mathf.Clamp(Settings.Thin, 0f, 8f) / 255f, Settings.TestFreeze >= 2f ? 0f : Mathf.Clamp(time - shownTime, 0f, 0.3f), 0f, 0f));
            boxMaterial.SetVector("_VolSun", new Vector4(sunR, sunG, sunB, 1.5f));
            boxMaterial.SetVector("_VolSunDir", (Vector3)(frame.east * sunL.x + frame.up * sunL.y + frame.north * sunL.z));
            Vector3 sunTurned = ToGrid(sunL);
            boxMaterial.SetVector("_VolSunLocal", new Vector4(sunTurned.x, sunTurned.y, sunTurned.z, 2.2f * billows));
            // Out of doors a good deal of light comes from the whole sky, more than the game's own fill light allows for.
            boxMaterial.SetVector("_VolAmb", new Vector4(Mathf.Max(ambR, 0.16f * sunR), Mathf.Max(ambG, 0.17f * sunG), Mathf.Max(ambB, 0.2f * sunB), 0f));
            boxMaterial.SetVector("_VolGlow", new Vector4(fireR, fireG, fireB, 0f));
            // The three strongest fires, for the light they throw on the smoke.
            int first = -1, second = -1, third = -1;
            for (int n = 0; n < lampCount; n++)
            {
                float power = lamps[n].power;
                if (first < 0 || power > lamps[first].power) { third = second; second = first; first = n; }
                else if (second < 0 || power > lamps[second].power) { third = second; second = n; }
                else if (third < 0 || power > lamps[third].power) third = n;
            }
            // (The shader reckons places in the box as it stood when its grid was made; where the box has gone along since, the fires are that much further back in it.)
            boxMaterial.SetVector("_VolLampA", LampFor(first));
            boxMaterial.SetVector("_VolLampB", LampFor(second));
            boxMaterial.SetVector("_VolLampC", LampFor(third));
            boxMaterial.SetVector("_VolLamps", new Vector4(first < 0 ? 0f : 0.25f * lamps[first].power, second < 0 ? 0f : 0.25f * lamps[second].power, third < 0 ? 0f : 0.25f * lamps[third].power, 0f));
            boxMaterial.SetVector("_VolTint", dustHue);
            TuneCuts();
            TuneScene();
            TuneShadow();
            TuneJet();
            TuneFire();
            TuneWakes();
            for (int n = 1; n <= 4; n++)
            {
                // The colours of this kind of fire at a quarter, half, three quarters and full heat.
                float h = n * 1.25f;
                int key = Mathf.Min(4, (int)h), at = (flameKind * 6 + key) * 3;
                float mix = h - key;
                // (An engine's flame is far brighter than a fire in the open: several times as bright as white paper in the sun.)
                float gain = riding ? 0.4f + 0.6f * n : 1f;
                boxMaterial.SetVector("_VolHot" + n, new Vector4(gain * Mathf.Lerp(Flames[at], Flames[at + 3], mix), gain * Mathf.Lerp(Flames[at + 1], Flames[at + 4], mix), gain * Mathf.Lerp(Flames[at + 2], Flames[at + 5], mix), 0f));
            }
        }

        // ---- how a fire in the open is drawn (see firePattern in tools/shaderpack/volume.glsl)
        static readonly int fireNote = Shader.PropertyToID("_VolFire");

        /// <summary>
        /// A fire's flames in the open (not an engine's: see TuneJet): their pattern rises through the burning gas at a couple of
        /// metres a second (flames lick upward faster than the gas they burn in drifts), at a size that goes with the fire: a
        /// fifth of a fireball's radius for a few seconds after it, then half the radius of the biggest fire burning on the ground
        /// (a pool fire's tongues are about as wide as half the pool; a small fire's finer), torn where thin as a fire in the air is.
        /// </summary>
        void TuneFire()
        {
            float biggest = 0f;
            for (int n = 0; n < fireCount; n++) biggest = Mathf.Max(biggest, 0.5f * fires[n].r);
            if (time - fireballAt < 4f) biggest = Mathf.Max(biggest, 0.2f * fireballR);
            if (time - blazeSeen < 1f) biggest = Mathf.Max(biggest, 0.6f * blazeWide); else blazeWide = 0f;
            if (biggest <= 0f) biggest = 0.6f;
            float size = Mathf.Clamp(biggest, 0.3f, 2.5f);
            boxMaterial.SetVector(fireNote, new Vector4(time * 1.8f, 1f / size, vacuum ? 0.3f : 0.9f, Settings.TestFire >= 1f ? 0f : 1f));
        }

        // ---- how a ship's flames are drawn (see jetPattern in tools/shaderpack/volume.glsl)
        Vector3 jetAxisL = Vector3.down, jetFromL;
        float jetNozzle = 0.5f, jetSpeed = 60f, jetScroll, jetStrongest, jetSeen = -100f;
        int jetKind;
        static readonly int jetNote = Shader.PropertyToID("_VolJet"), jetFromNote = Shader.PropertyToID("_VolJetFrom"), jetLookNote = Shader.PropertyToID("_VolJetLook"), jetBodyNote = Shader.PropertyToID("_VolJetBody");

        /// <summary>
        /// A ship's flames: their pattern is held in the nozzle's frame and carried down the jet far slower than the gas
        /// (the gas's own pattern, going a hundred metres a second, was smeared to a blur between frames), at a size that
        /// goes with the nozzle; torn into tongues at the flame's edge in thick air, smooth in a vacuum; sooty for the
        /// fuels that are, with shock diamonds in thick air. (Each frame; the strongest flame is chosen afresh in Vents.)
        /// </summary>
        void TuneJet()
        {
            bool jet = riding && time - jetSeen < 1f;
            if (!jet)
            {
                boxMaterial.SetVector(jetLookNote, Vector4.zero);
                jetStrongest = 0f;
                return;
            }
            float thin = Thin;
            // (Slow: seen from a few metres off, faster than this is many pixels a frame, and the game's smoothing between
            // frames smears it out again. Drawn out along the jet, the pattern reads as flowing all the same.)
            jetScroll += Time.deltaTime * 2.5f;
            Vector3 axis = ToGrid(jetAxisL), from = ToGrid(jetFromL) - slid;
            boxMaterial.SetVector(jetNote, new Vector4(axis.x, axis.y, axis.z, jetScroll));
            boxMaterial.SetVector(jetFromNote, new Vector4(from.x, from.y, from.z, jetNozzle));
            // (What each kind of fire is like. Kerosene: ragged, sooty, plain diamonds. Hydrogen: a clear, smooth flame with strong
            // diamonds in it, which is how such an engine's flame is seen; with tongues torn into it, a hydrogen flame looked like
            // a cloud. Methane between the two. A solid's: dense with glowing grit, ragged, hardly any diamonds. The fuels that
            // keep: something of each. In thin air all of it smooths out into the bell, and the diamonds go.)
            float soot = jetKind == 0 ? 0.35f : jetKind == 1 ? 0.08f : jetKind == 6 ? 0.15f : jetKind == 5 ? 0.05f : 0f;
            float diamonds = jetKind == 4 ? 0.7f : jetKind == 0 ? 0.12f : jetKind == 5 ? 0.4f : jetKind == 6 ? 0.12f : jetKind == 1 ? 0.04f : 0f;
            float tongues = jetKind == 4 ? 0.25f : jetKind == 5 ? 0.45f : jetKind == 6 ? 0.6f : jetKind == 2 ? 0.3f : jetKind == 1 ? 0.8f : 0.9f;
            // (A clear, faint flame is whole wherever there is any of it: measured as a bright one is, all of a hydrogen engine's
            // flame counted as its thin edge and was torn into wisps, like cloud. And four or five diamonds, a few nozzles' widths.)
            float body = jetKind == 4 ? 7f : jetKind == 5 ? 4f : jetKind == 6 || jetKind == 2 ? 3f : 1.6f;
            boxMaterial.SetVector(jetLookNote, new Vector4(tongues * (1f - thin), soot * (1f - thin), diamonds * (1f - thin) * (1f - thin), Mathf.Clamp(1.1f * jetNozzle, 0.3f, 3f)));
            boxMaterial.SetVector(jetBodyNote, new Vector4(body, jetKind == 4 ? 5f : 4f, 0f, 0f));
            jetStrongest = 0f;
        }

        // ---- the shadow the smoke throws on what is under it (see tools/shaderpack/shadow.glsl)
        GameObject shade;
        Material shadeMaterial;
        static readonly int shadowX = Shader.PropertyToID("_ShadowGridX"), shadowY = Shader.PropertyToID("_ShadowGridY"), shadowZ = Shader.PropertyToID("_ShadowGridZ"),
                            shadowSun = Shader.PropertyToID("_ShadowSun"), shadowSunWorld = Shader.PropertyToID("_ShadowSunWorld");
        static readonly Vector3[] corners = new Vector3[16];

        /// <summary>
        /// Every frame, in sunlight: the box the smoke's shadow can fall in (the grid's box drawn out away from the sun as
        /// far down as the ground under it), and what the shadow's shader needs to follow each place there back towards the
        /// sun into the grid: the turn from the scene into the grid's box, and the sun's way in the box's own axes.
        /// </summary>
        void TuneShadow()
        {
            bool wanted = Settings.Shadows && Assets.Shadow != null && box != null && boxRenderer.enabled && gridReady && !space && sunUp && sunL.y > 0.06f && count > 0;
            if (!wanted)
            {
                if (shade != null && shade.activeSelf) shade.SetActive(false);
                return;
            }
            if (shade == null)
            {
                shade = new GameObject("Shadow") { layer = Recipe.Layer };
                shade.transform.SetParent(t, false);
                shade.AddComponent<MeshFilter>().sharedMesh = Assets.Cube;
                MeshRenderer drawn = shade.AddComponent<MeshRenderer>();
                // (after what is solid, as the burn marks are, and before anything see-through: the smoke itself is not darkened)
                shadeMaterial = new Material(Assets.Shadow) { renderQueue = 2470 };
                shadeMaterial.SetTexture("_Volume", volume);
                drawn.sharedMaterial = shadeMaterial;
                drawn.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                drawn.receiveShadows = false;
            }
            if (!shade.activeSelf) shade.SetActive(true);
            Transform b = box.transform;
            Vector3 middle = b.localPosition, size = b.localScale;
            Quaternion turn = b.localRotation;
            float floor = by0, top = float.MinValue;
            for (int n = 0; n < 8; n++)
            {
                Vector3 c = middle + turn * new Vector3(((n & 1) - 0.5f) * size.x, (((n >> 1) & 1) - 0.5f) * size.y, ((n >> 2) - 0.5f) * size.z);
                corners[n] = c;
                if (c.y > top) top = c.y;
                float under = hasGround ? GroundAt(c.x, c.z) : c.y - 60f;
                if (under < floor) floor = under;
            }
            floor -= 3f;
            for (int n = 0; n < 8; n++)
            {
                Vector3 c = corners[n];
                float along = Mathf.Min((c.y - floor) / sunL.y, 900f);
                corners[n + 8] = c - sunL * along;
            }
            Vector3 lo = corners[0], hi = corners[0];
            for (int n = 1; n < 16; n++) { lo = Vector3.Min(lo, corners[n]); hi = Vector3.Max(hi, corners[n]); }
            lo.y = Mathf.Min(lo.y, floor); hi.y = Mathf.Max(hi.y, top);
            shade.transform.localPosition = (lo + hi) * 0.5f;
            shade.transform.localRotation = Quaternion.identity;
            shade.transform.localScale = hi - lo + Vector3.one * 2f;
            Matrix4x4 into = b.worldToLocalMatrix;
            Vector3 sunWorld = ((Vector3)(frame.east * sunL.x + frame.up * sunL.y + frame.north * sunL.z)).normalized;
            Vector3 sunBox = into.MultiplyVector(sunWorld);
            // (as dark as daylight goes: under thick smoke what is left is the sky's light)
            float day = Mathf.Clamp01(sunL.y * 6f + 0.3f);
            shadeMaterial.SetVector(shadowX, into.GetRow(0));
            shadeMaterial.SetVector(shadowY, into.GetRow(1));
            shadeMaterial.SetVector(shadowZ, into.GetRow(2));
            shadeMaterial.SetVector(shadowSun, new Vector4(sunBox.x, sunBox.y, sunBox.z, 0.7f * day));
            shadeMaterial.SetVector(shadowSunWorld, new Vector4(sunWorld.x, sunWorld.y, sunWorld.z, 0f));
        }

        // ---- the lamps of the scene round about that light this patch's smoke at night (see Air.SceneLights)
        readonly Light[] sceneLit = new Light[4];
        float sceneChosen = -10f;
        static readonly int[] sceneAt = { Shader.PropertyToID("_VolSceneA"), Shader.PropertyToID("_VolSceneB"), Shader.PropertyToID("_VolSceneC"), Shader.PropertyToID("_VolSceneD") };
        static readonly int[] sceneTint = { Shader.PropertyToID("_VolSceneTintA"), Shader.PropertyToID("_VolSceneTintB"), Shader.PropertyToID("_VolSceneTintC"), Shader.PropertyToID("_VolSceneTintD") };
        static readonly int[] sceneDir = { Shader.PropertyToID("_VolSceneDirA"), Shader.PropertyToID("_VolSceneDirB"), Shader.PropertyToID("_VolSceneDirC"), Shader.PropertyToID("_VolSceneDirD") };

        /// <summary>
        /// The four lamps of the scene that light this patch most (chosen twice a second), told to the shader every
        /// frame: where each is, how far it reaches, its light, and a spotlight's cone. Only by night: by day the sun's
        /// light is all that shows on smoke, and a lamp the game leaves on in daylight should not.
        /// </summary>
        void TuneScene()
        {
            float night = 1f - Mathf.Clamp01(sunL.y * 6f + 0.3f);
            if (Time.unscaledTime - sceneChosen > 0.5f)
            {
                sceneChosen = Time.unscaledTime;
                for (int n = 0; n < sceneLit.Length; n++) sceneLit[n] = null;
                if (night > 0.01f && !space)
                {
                    float[] best = new float[sceneLit.Length];
                    foreach (Light l in Air.SceneLights)
                    {
                        if (l == null) continue;
                        Vector3 at = frame.ToLocal((Vector3d)l.transform.position);
                        // (how far it is from the smoke's box, against how far it reaches)
                        float dx = Mathf.Max(0f, Mathf.Max(bx0 - at.x, at.x - bx1)), dy = Mathf.Max(0f, Mathf.Max(by0 - at.y, at.y - by1)), dz = Mathf.Max(0f, Mathf.Max(bz0 - at.z, at.z - bz1));
                        float reach = l.range * l.range, away = dx * dx + dy * dy + dz * dz;
                        if (away >= reach) continue;
                        float worth = l.intensity * (1f - away / reach);
                        for (int n = 0; n < best.Length; n++)
                        {
                            if (worth <= best[n]) continue;
                            for (int m = best.Length - 1; m > n; m--) { best[m] = best[m - 1]; sceneLit[m] = sceneLit[m - 1]; }
                            best[n] = worth; sceneLit[n] = l;
                            break;
                        }
                    }
                }
            }
            for (int n = 0; n < sceneLit.Length; n++)
            {
                Light l = sceneLit[n];
                if (l == null || !l.isActiveAndEnabled || night <= 0.01f)
                {
                    boxMaterial.SetVector(sceneAt[n], Vector4.zero);
                    continue;
                }
                Vector3 at = ToGrid(frame.ToLocal((Vector3d)l.transform.position)) - slid, dir = ToGrid(frame.DirToLocal((Vector3d)l.transform.forward));
                Color c = l.color.linear * (0.5f * l.intensity * night);
                boxMaterial.SetVector(sceneAt[n], new Vector4(at.x, at.y, at.z, l.range * l.range));
                boxMaterial.SetVector(sceneTint[n], new Vector4(c.r, c.g, c.b, l.type == LightType.Spot ? Mathf.Cos(0.5f * l.spotAngle * Mathf.Deg2Rad) : -2f));
                boxMaterial.SetVector(sceneDir[n], new Vector4(dir.x, dir.y, dir.z, 0f));
            }
        }

        /// <summary>Where a fire is and the square of its size, as the shader is told it: in the grid's axes, and in the box as it stood when its grid was made.</summary>
        Vector4 LampFor(int n)
        {
            if (n < 0) return Vector4.zero;
            Vector3 at = ToGrid(new Vector3(lamps[n].x, lamps[n].y, lamps[n].z)) - slid;
            return new Vector4(at.x, at.y, at.z, lamps[n].r * lamps[n].r);
        }

        /// <summary>Whether this site's smoke is to be drawn at half size just now, into its own small picture (see Air.Halves: the way it was done before Layers).</summary>
        public bool Halved => halved && !Air.Layered && box != null && boxRenderer.enabled;
        /// <summary>Whether this site has smoke to draw as a volume (a grid has been made for it), and where the middle of its box is.</summary>
        public bool Shown => box != null && boxRenderer.enabled;
        public Vector3 BoxCentre => box.transform.position;
        public Material Marching => boxMaterial;
        public Matrix4x4 BoxMatrix => box.transform.localToWorldMatrix;
        public float Away => camL.magnitude;

        static readonly int drawnNote = Shader.PropertyToID("_VolDrawn");

        /// <summary>
        /// Whether any of this cloud's box lies in the stretch of distances a camera draws. (The game draws the
        /// flight with two cameras, one for what is within 400 m and one for what is beyond: a cloud is nearly
        /// always wholly in the range of one of them, and the other then has nothing of it to draw. The shader
        /// keeps to the same stretch, ray by ray; this only tells beforehand when no ray will find anything.)
        /// </summary>
        public bool Within(Camera camera)
        {
            Matrix4x4 m = camera.worldToCameraMatrix * box.transform.localToWorldMatrix;
            float least = float.MaxValue, most = float.MinValue;
            for (int corner = 0; corner < 8; corner++)
            {
                // (how far ahead of the camera this corner is: a camera looks along its own z, backwards)
                float ahead = -(m.m20 * ((corner & 1) - 0.5f) + m.m21 * (((corner >> 1) & 1) - 0.5f) + m.m22 * ((corner >> 2) - 0.5f) + m.m23);
                if (ahead < least) least = ahead;
                if (ahead > most) most = ahead;
            }
            // (with a little to spare, so that a rounding error never leaves out a cloud the shader would have drawn a sliver of)
            return most > camera.nearClipPlane * 0.99f - 0.5f && least < camera.farClipPlane * 1.01f + 0.5f;
        }

        /// <summary>Tell the shader that puts the small picture on the screen whether one was drawn for the camera about to draw.</summary>
        public void DrawnFor(bool drawn)
        {
            if (shownMaterial != null) shownMaterial.SetVector(drawnNote, new Vector4(drawn ? 1f : 0f, 0f, 0f, 0f));
        }

        /// <summary>The small picture this site's smoke is drawn into, so many pixels each way.</summary>
        public RenderTexture Small(int width, int height)
        {
            if (small != null && small.width == width && small.height == height) return small;
            if (small != null) { small.Release(); UnityEngine.Object.Destroy(small); }
            // (sixteen bits a colour where the card can: fire is far brighter than white, and thin smoke far fainter than one part in 255)
            RenderTextureFormat kind = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
            small = new RenderTexture(width, height, 0, kind) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "VolumetricExplosions smoke" };
            shownMaterial.SetTexture("_VolHalf", small);
            return small;
        }

        /// <summary>How far from the camera the farthest corner of the cloud is.</summary>
        public float Farthest => camL.magnitude + 0.5f * Mathf.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0) + (bz1 - bz0) * (bz1 - bz0));

        public void Place() => frame.Apply(t);
#if DEV
        public Vector3 Local(Vector3d world) { frame.Refresh(); return frame.ToLocal(world); }
        public Vector3d World(Vector3 local) { frame.Refresh(); return frame.ToWorld(local); }
        /// <summary>(Where this patch's smoke is on the whole: the middle of its smoke weighed by how much there is, in the patch's own axes, and how widely it is spread each way.)</summary>
        public Vector3 Middle(out Vector3 spread)
        {
            double m = 0, x = 0, y = 0, z = 0, xx = 0, yy = 0, zz = 0;
            for (int i = 0; i < count; i++)
            {
                if (p[i].life < 0f || p[i].mass <= 0f || (p[i].flags & Ballistic) != 0) continue;
                double w = p[i].mass;
                m += w; x += w * p[i].x; y += w * p[i].y; z += w * p[i].z;
                xx += w * p[i].x * p[i].x; yy += w * p[i].y * p[i].y; zz += w * p[i].z * p[i].z;
            }
            if (m <= 0) { spread = Vector3.zero; return 0.5f * new Vector3(bx0 + bx1, by0 + by1, bz0 + bz1); }
            x /= m; y /= m; z /= m;
            spread = new Vector3((float)Math.Sqrt(Math.Max(xx / m - x * x, 0)), (float)Math.Sqrt(Math.Max(yy / m - y * y, 0)), (float)Math.Sqrt(Math.Max(zz / m - z * z, 0)));
            return new Vector3((float)x, (float)y, (float)z);
        }
        /// <summary>(The same, as text, with the middle's height above the sea.)</summary>
        public string MiddleNow()
        {
            Vector3 middle = Middle(out Vector3 spread);
            double sea = body.GetAltitude(World(middle));
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "middle {0:F1} {1:F1} {2:F1} spread {3:F1} {4:F1} {5:F1} sea {6:F1} riding {7} count {8}",
                                 middle.x, middle.y, middle.z, spread.x, spread.y, spread.z, sea, riding ? 1 : 0, count);
        }
        public Vector3 RootPosition => t.position;
        /// <summary>(Whether the root draws a place where the frame says it is: the same place by both reckonings, and how far apart they come out.)</summary>
        public string Handed()
        {
            frame.Refresh(); frame.Apply(t);
            var at = new Vector3(10f, 0f, 0f);
            Vector3 drawn = t.TransformPoint(at), meant = (Vector3)frame.ToWorld(at);
            return "a place 10 m along the patch's first axis is drawn " + (drawn - meant).magnitude.ToString("F2") + " m from where the frame has it; the root's own right is " + Vector3.Dot(t.right, (Vector3)frame.east).ToString("F2") + " along the frame's 'east'";
        }
        public Vector3d FrameOrigin { get { frame.Refresh(); return frame.origin; } }
#endif

        /// <summary>Whether a new explosion belongs in this patch of air.</summary>
        public bool Takes(Plan plan)
        {
            if (plan.body != body || plan.space != space || leaving || riding) return false;
            frame.Refresh();
            Vector3 at = frame.ToLocal(plan.world);
            float mx = (bx0 + bx1) * 0.5f, my = (by0 + by1) * 0.5f, mz = (bz0 + bz1) * 0.5f;
            float reach = 0.5f * Mathf.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0) + (bz1 - bz0) * (bz1 - bz0)) + 160f;
            float dx = at.x - mx, dy = at.y - my, dz = at.z - mz;
            return dx * dx + dy * dy + dz * dz < reach * reach && at.sqrMagnitude < 4000f * 4000f;
        }

        /// <summary>For the development build: of every third smoke particle, how many lie inside something solid (the ground, a building, a part of a ship).</summary>
        public int Inside(out int tested)
        {
            tested = 0;
            int inside = 0;
            for (int i = 0; i < count; i += 3)
            {
                if (p[i].life < 0f || p[i].heat > 0.5f || p[i].mass <= 0f) continue;
                tested++;
                if (Physics.CheckSphere((Vector3)frame.ToWorld(new Vector3(p[i].x, p[i].y, p[i].z)), 0.05f, (1 << 15) | 1, QueryTriggerInteraction.Ignore)) inside++;
            }
            return inside;
        }

        public string Describe() =>
            count + " particles, " + thermalCount + " thermals, " + lampCount + " lamps, " + fireCount + " fires, " + fragCount + " burning fragments, " + pieces.Count + " pieces (" + toBreak.Count + " parts still to break up), " + ballCount + " hull spheres, " + jetCount +
            " jets, air " + air.ToString("F3") + (vacuum ? " (vacuum)" : "") + (space ? " (space)" : "") + ", wind " + wind10.ToString("F1") + " m/s, ground " +
#if DEV
            DescribeGround() + (vented ? " [engine's smoke" + (turned ? ", grid turned" : "") + ", begun at " + ventFrom.ToString("F0") + ", fed " + Unfed.ToString("F1") + " s ago at " + fedSpeed.ToString("F0") + " m/s" + (leaving ? ", leaving" : "") + ", " + time.ToString("F1") + " s old]" : "") +
#else
            (hasGround ? "found" : "none") +
#endif
            (shownDrift.sqrMagnitude > 0f ? ", the box going along at " + shownDrift.magnitude.ToString("F0") + " m/s (" + slid.magnitude.ToString("F1") + " m since its grid)" : "") +
            // (for the development build: where the grid in use lies and where the camera is, in the site's own axes)
            ", grid " + gx0.ToString("F1") + " to " + (gx0 + G / gix).ToString("F1") + " east, " + gy0.ToString("F1") + " to " + (gy0 + G / giy).ToString("F1") + " up, " + gz0.ToString("F1") + " to " + (gz0 + G / giz).ToString("F1") + " north (cells " +
            (1f / gix).ToString("F2") + " by " + (1f / giy).ToString("F2") + " by " + (1f / giz).ToString("F2") + " m), every particle within " + bx0.ToString("F1") + " to " + bx1.ToString("F1") + ", " + by0.ToString("F1") + " to " + by1.ToString("F1") + ", " + bz0.ToString("F1") + " to " + bz1.ToString("F1") +
            ", camera at " + camL.x.ToString("F1") + ", " + camL.y.ToString("F1") + ", " + camL.z.ToString("F1") + ", nothing spread over less than " + leastReach.ToString("F2") + " m (particles " + spacing.ToString("F2") + " m apart)";

        // ================================================================== the surroundings

        /// <summary>The air, the gravity and the light here, looked up again every half second.</summary>
        void Look()
        {
            Vector3d here = frame.origin;
            double pressure = body.atmosphere ? FlightGlobals.getStaticPressure(here, body) : 0.0;
            double density = pressure > 0 ? FlightGlobals.getAtmDensity(pressure, FlightGlobals.getExternalTemperature(here, body), body) : 0.0;
            air = Mathf.Clamp((float)(density / 1.225), 0f, 8f);
            vacuum = air < 0.004f;
            grip = vacuum ? 0f : Mathf.Clamp(Mathf.Sqrt(air), 0.12f, 2.2f);
            gravity = (float)FlightGlobals.getGeeForceAtPosition(here, body).magnitude;
            sound = pressure > 0 && density > 0 ? Mathf.Clamp((float)body.GetSpeedOfSound(pressure, density), 80f, 900f) : 0f;
            calm = (0.35f + 0.18f * wind10) * Mathf.Min(1f, grip);

            Vector3d sunward = (Planetarium.fetch.Sun.position - here).normalized;
            sunL = frame.DirToLocal(sunward);
            float day = space ? 1f : Mathf.Clamp01(sunL.y * 6f + 0.3f);
            sunUp = day > 0.02f;
            if (sunLamp == null && Sun.Instance != null) sunLamp = Sun.Instance.GetComponent<Light>();
            Color sun = sunLamp != null ? sunLamp.color * sunLamp.intensity : new Color(0.9f, 0.9f, 0.9f);
            // Through a lot of air the low sun is warmer. Light is added up here as amounts of light, not as screen colours.
            float low = body.atmosphere && !space ? Mathf.Clamp01(1f - sunL.y * 4f) : 0f;
            sunR = sun.r * sun.r * day;
            sunG = sun.g * sun.g * day * (1f - 0.35f * low);
            sunB = sun.b * sun.b * day * (1f - 0.62f * low);
            Color sky = RenderSettings.ambientLight;
            ambR = sky.r * sky.r + 0.0004f;
            ambG = sky.g * sky.g + 0.0004f;
            ambB = sky.b * sky.b + 0.0005f;
            Breezes();
        }

        /// <summary>
        /// The game has no wind. One is made up for each place and half hour, so that smoke leans and drifts when there is one;
        /// and near the ground there mostly is none: as it comes (setting "breeze"), nine times in ten the air is still, and
        /// smoke goes straight up on its own heat and hangs where it is. Otherwise it is most often a light air or a light breeze,
        /// half a metre to two and a half a second ten metres up (smoke drifts, a column leans higher up); one time in twelve of
        /// those a gentle or moderate breeze, up to six; and one time in fifty a strong wind, six to ten (so one time in five
        /// hundred in all). "always some" leaves out the still air; "still" has none ever.
        /// </summary>
        void Wind(Plan plan)
        {
            if (space || plan.vacuum || Settings.Wind <= 0f) return;
            int seed = body.bodyName.GetHashCode() ^ (int)(body.GetLatitude(plan.world) * 20.0) * 7919 ^ (int)(body.GetLongitude(plan.world) * 20.0) * 104729 ^ (int)(Planetarium.GetUniversalTime() / 1800.0) * 15485863;
            var random = new System.Random(seed);
            double angle = random.NextDouble() * Math.PI * 2.0, how = random.NextDouble(), still = random.NextDouble(), kind = random.NextDouble();
            float strength = body.bodyName == "Eve" ? 0.45f : body.bodyName == "Duna" ? 1.5f : body.bodyName == "Jool" ? 2.5f : 1f;
            bool calmNow = Settings.Breeze == 0 || (Settings.Breeze == 1 && still < StillAir);
            double speed = calmNow ? 0.0 : kind < 0.9 ? 0.5 + 2.0 * Math.Pow(how, 1.4) : kind < 0.98 ? 2.5 + 3.5 * how : 6.0 + 4.0 * how;
            wind10 = (float)speed * strength * Settings.Wind;
            if (Settings.TestWind >= 0f) { angle = Settings.TestWind * Math.PI / 180.0; wind10 = Settings.TestWindSpeed; }      // (for testing: the same wind every time)
            windX = (float)Math.Cos(angle);
            windZ = (float)Math.Sin(angle);
            calm = (0.35f + 0.18f * wind10) * Mathf.Min(1f, grip);
            Weathered(true);
        }

        /// <summary>
        /// The wind a weather mod says there is here (Sources.Wind), instead of the one made up above: at once when the patch is
        /// made, and eased towards each time it is asked again after. It answers with the wind at the height asked about, its own
        /// way of holding the wind back near the ground already in it (Natural Weather 0.2: next to nothing just over the ground,
        /// four fifths of the ten-metre wind at 2 m on a sunny afternoon, under two thirds on a still night): so it is asked at ten
        /// metres for the wind and its gusts, and at a dozen heights from a quarter of a metre to 400 m for how the wind there
        /// stands against that, which the smoke then goes with height by height (see Profile, for when there is no weather mod:
        /// the two are not put one on the other).
        /// </summary>
        void Weathered(bool now)
        {
            // (a player who asks for still air has it, whatever the weather says)
            if (Sources.Wind == null || space || vacuum || Settings.Wind <= 0f || Settings.Breeze == 0 || Settings.TestWind >= 0f) { weathered = false; return; }
            Sources.Breeze breeze;
            double ut = Planetarium.GetUniversalTime();
            float floor = hasGround ? GroundAt(0f, 0f) : 0f;
            try { breeze = Sources.Wind(frame.origin + frame.up * (floor + 10.0), ut); }
            catch (Exception) { weathered = false; return; }
            try { WeatherAloft(ut, floor, breeze); }
            catch (Exception) { }
            Vector3 local = frame.DirToLocal(breeze.wind);
            float speed = Mathf.Sqrt(local.x * local.x + local.z * local.z) * Settings.Wind;
            float x = speed > 1e-3f ? local.x / speed * Settings.Wind : windX, z = speed > 1e-3f ? local.z / speed * Settings.Wind : windZ;
            // (as gusty as the weather says, against the gusts the air makes for itself, which come to about a fifth of the wind)
            float gusts = Mathf.Clamp(breeze.gusts * Settings.Wind / Mathf.Max(0.4f, 0.19f * speed), 0f, 3f);
            float k = now ? 1f : 0.3f;
            float ox = windX * wind10, oz = windZ * wind10;
            float nx = ox + (x * speed - ox) * k, nz = oz + (z * speed - oz) * k;
            wind10 = Mathf.Sqrt(nx * nx + nz * nz);
            if (wind10 > 1e-3f) { windX = nx / wind10; windZ = nz / wind10; }
            gustScale += (gusts - gustScale) * k;
            calm = (0.35f + 0.18f * wind10) * Mathf.Min(1f, grip);
        }

        static readonly float[] AskedAt = { 0.25f, 0.5f, 1f, 2f, 4f, 7f, 10f, 15f, 25f, 45f, 80f, 150f, 260f, 400f };
        readonly float[] askedSpeed = new float[14];
        bool weathered;

        /// <summary>
        /// (See Weathered.) The weather's wind at heights above the ground under the middle of the patch, against its wind at ten
        /// metres, put into the list the mover reads (between two heights asked about, by the logarithm of the height; below the
        /// lowest, down to nothing at the ground).
        /// </summary>
        void WeatherAloft(double ut, float floor, Sources.Breeze at10)
        {
            Vector3 l10 = frame.DirToLocal(at10.wind);
            float ten = Mathf.Sqrt(l10.x * l10.x + l10.z * l10.z);
            if (ten < 0.05f) { weathered = true; return; }          // (still: how it would go with height does not matter)
            for (int n = 0; n < AskedAt.Length; n++)
            {
                if (AskedAt[n] == 10f) { askedSpeed[n] = 1f; continue; }
                Vector3 l = frame.DirToLocal(Sources.Wind(frame.origin + frame.up * (floor + AskedAt[n]), ut).wind);
                askedSpeed[n] = Mathf.Min(Mathf.Sqrt(l.x * l.x + l.z * l.z) / ten, 4f);
            }
            var list = new float[802];
            int k = 0;
            for (int n = 0; n < list.Length; n++)
            {
                float z = Mathf.Max(n * 0.5f, 0.05f);
                if (z <= AskedAt[0]) { list[n] = askedSpeed[0] * z / AskedAt[0]; continue; }
                while (k < AskedAt.Length - 2 && z > AskedAt[k + 1]) k++;
                float f = Mathf.Clamp01((Mathf.Log(z) - Mathf.Log(AskedAt[k])) / (Mathf.Log(AskedAt[k + 1]) - Mathf.Log(AskedAt[k])));
                list[n] = askedSpeed[k] + (askedSpeed[k + 1] - askedSpeed[k]) * f;
            }
            aloft = list;
            weathered = true;
        }

        /// <summary>
        /// How strong the wind is at a height, against what it is at ten metres: the ground holds it back, and it gains above. Near
        /// the ground as the logarithm of the height over how rough the ground is (the law of the wall: over grass, whose roughness
        /// is some 3 cm, the wind is seven tenths of the ten-metre wind at 2 m, six tenths at 1 m, a third at 20 cm; over water or a
        /// runway it is stronger lower down); and at night, when the ground cools the air on it and the air settles into layers,
        /// the wind near the ground is held back more and gains faster above (the stable air's log-linear law, its Obukhov length
        /// taken as 25 m): half the ten-metre wind at 1 m. This is what leans a column of smoke over more and more as it climbs, and
        /// shears the top off a cloud, while smoke lying on the ground hardly drifts. (Until 2026-10-10 a power law with 0.6 for
        /// anything below a metre: smoke on the ground went with six tenths of the wind.)
        /// </summary>
        static float[] Profile(float rough, bool night)
        {
            var list = new float[802];
            double l = night ? 25.0 : 1e9, ten = Math.Log((10.0 + rough) / rough) + 5.0 * 10.0 / l;
            for (int n = 0; n < list.Length; n++)
            {
                double z = Math.Max(n * 0.5, 0.05);
                list[n] = (float)Math.Min((Math.Log((z + rough) / rough) + 5.0 * Math.Min(z, 200.0) / l) / ten, 2.25);
            }
            // (the first entry stands for the lowest few centimetres, where smoke lying on the ground hardly moves)
            return list;
        }

        // The profile for this patch's ground and the time of day, read from a list (every half metre up to 400 m, and between two
        // entries by where between them it lies): the mover wants it for every particle, and the library's own arithmetic is very
        // slow to call as this game runs it. Made again when the ground or the time of day changes it (see Look).
        float[] aloft = Profile(0.03f, false);
        float aloftRough = 0.03f;
        bool aloftNight, wasWeathered;

        void Breezes()
        {
            if (weathered) { wasWeathered = true; return; }          // (the weather mod's own, see WeatherAloft)
            // (water a third of a millimetre, paving a centimetre, open ground and grass three)
            float rough = groundWet ? 0.0003f : hasGround && !groundBare ? 0.01f : 0.03f;
            bool night = !space && !sunUp;
            if (rough == aloftRough && night == aloftNight && !wasWeathered) return;
            aloftRough = rough; aloftNight = night; wasWeathered = false;
            aloft = Profile(rough, night);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float AloftFrom(float[] list, float height)
        {
            if (height <= 0f) return list[0];
            if (height >= 400f) return list[801];
            float at = height * 2f;
            int n = (int)at;
            return list[n] + (list[n + 1] - list[n]) * (at - n);
        }

        float Aloft(float height) => AloftFrom(aloft, height);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct Bits { [System.Runtime.InteropServices.FieldOffset(0)] public float f; [System.Runtime.InteropServices.FieldOffset(0)] public int i; }

        /// <summary>
        /// A square root, to a few parts in ten million, without calling the library: as this game runs its code,
        /// each such call takes a tenth of a microsecond and more, and the loops over particles and cells make
        /// tens of thousands of them for every frame and every grid. (A first guess from the number's own bits,
        /// then made better twice.)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Root(float x)
        {
            if (x <= 1e-30f) return 0f;
            Bits b = default;
            b.f = x;
            b.i = 0x1fbd1df5 + (b.i >> 1);
            float y = b.f;
            y = 0.5f * (y + x / y);
            return 0.5f * (y + x / y);
        }

        // ================================================================== one frame

        /// <summary>
        /// One frame. The particles themselves are moved on other threads while the game gets on with its
        /// own work (see Moving); here, once that has finished, they are handed to be drawn, the dead are
        /// taken out, new ones are put in, and the next round is started.
        /// </summary>
        public void Step(float dt)
        {
            if (Settings.TestFreeze >= 3f) dt = 0f;                // (for testing: nothing whatever changes, so that two pictures can be compared exactly)
            long began = System.Diagnostics.Stopwatch.GetTimestamp(), lap = began;
            if (job != null) { Task last = job; job = null; last.Wait(); }
            long waited = System.Diagnostics.Stopwatch.GetTimestamp();
            lap = waited;
            // (the puffs the mover found in the way of something coming, split before the list it made goes stale: see Wakes.cs)
            if (splitCount > 0) Refine();

            // Take out the ones that died, filling each gap with the last.
            for (int i = 0; i < count;)
            {
                if (p[i].life < 0f)
                {
                    count--;
                    if (i != count) { p[i] = p[count]; draw[i] = draw[count]; }
                }
                else i++;
            }
            Air.Lap(0, ref lap);
            TurnAside();
            if (!riding) TakeAdopted();
            Air.Lap(1, ref lap);
            Air.Lap(2, ref lap);
            windGoneX += windX * wind10 * dt; windGoneZ += windZ * wind10 * dt;
            // Where the smoke is drawn as a volume none of it is drawn as sprites. (They were once left in with no colour:
            // nothing to see, but thousands of them, each metres across, still to be drawn over one another.)
            cloud.SetParticles(draw, box != null && gridReady ? 0 : count);
            Air.Lap(3, ref lap);

            time += dt;
            tick++;
            flameWeight *= 1f / (1f + dt * 0.25f);
            frame.Coast(dt);
            frame.Refresh();
            if (riding) { Under(); Grates(); Hand(); }
            if (time >= nextLook) { nextLook = time + 0.5f; Look(); Weathered(false); }
            Survey(dt);
            Camera eye = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Camera.main;
            camL = eye != null ? frame.ToLocal(eye.transform.position) : new Vector3(0f, 1e6f, 0f);
            Air.Lap(4, ref lap);

            foreach (Plan plan in waiting) Burst(plan);
            waiting.Clear();
            Air.Lap(5, ref lap);
            Wash();
            Wakes(dt);
            Thermals(dt);
            Waves(dt);
            Lamps(dt);
            Air.Lap(6, ref lap);
            Fires(dt);
            Frags(dt);
            Vents(dt);
            if (trenches.Count > 0) Ends(dt, Air.SmokeQuality);
            Air.Lap(7, ref lap);
            Pieces(dt);
            Air.Lap(8, ref lap);
            LookAhead(dt);
            Air.Lap(9, ref lap);
            Relight();
            Air.Lap(10, ref lap);
            TuneVolume();
            Air.Lap(11, ref lap);
            Glows();
            Air.Lap(12, ref lap);

            // Under pressure for room, old smoke goes sooner.
            hurry = Air.Alive > Settings.MaxParticles * 0.85f ? 2.5f : 1f;
            stepDt = dt;
            chunks = count < 1500 ? 1 : Settings.Threads > 0 ? Settings.Threads : Air.Threads;
            // (which of the rounds that the billows' turns keep to begin afresh in this frame: see Turn)
            int rounds = (int)(time * PerRound[0]);
            turnsWrapped = -1;
            for (int k = 0; k < Octaves && (rounds >> k) != (turnsCounted >> k); k++) turnsWrapped = k;
            turnsCounted = rounds;
            if (count > 0 && Settings.TestFreeze < 0.5f)
            {
                stepGoing = airGoing;
                airGone += (Vector3d)airGoing * dt;
                job = Task.Run(jobBody);
            }
            else if (Settings.TestFreeze < 2f) TurnsOnly(dt);      // (for testing: the particles are held still, but their billows go on taking turns)
            Air.Lap(13, ref lap);
            long done = System.Diagnostics.Stopwatch.GetTimestamp();
            Air.Cost[0] += waited - began; Air.Cost[1] += done - waited; Air.Cost[2] += moved;
        }

        // ---- running into things
        NativeArray<RaycastCommand> rays;
        NativeArray<RaycastHit> hits;
        int[] rayOf = new int[1024];
        uint[] raySeed = new uint[1024];
        static RaycastCommand[] rayList = new RaycastCommand[2048];      // (the queries of the site in hand, before they are handed over: used on the game's own thread only)
        static RaycastHit[] rayBack = new RaycastHit[2048];
        int rayCount, rayEvery = 3;
        bool raysOut;
        JobHandle rayJob;
        Vector3 rayOrigin, rayEast, rayUp, rayNorth;

        /// <summary>
        /// Smoke does not pass through the ground, buildings or ships. A third of the particles each frame
        /// look along the way they are going (the game's own collision queries, run on its worker threads
        /// while everything else goes on); whatever they would run into turns them aside the next frame.
        /// </summary>
        void LookAhead(float dt)
        {
            if (!Settings.Collide || space || count == 0) return;
            // Every third particle in a small cloud; in a big one fewer, so that at most some 1,200 look each frame
            // (setting the queries up is work for the game's own thread).
            int every = Math.Max(3, (count + 1199) / 1200), wanted = count / every + 1;
            if (rayOf.Length < wanted) { rayOf = new int[wanted + 1024]; raySeed = new uint[wanted + 1024]; }
            if (rayList.Length < wanted) rayList = new RaycastCommand[wanted + 1024];
            rayOrigin = (Vector3)frame.origin; rayEast = (Vector3)frame.east; rayUp = (Vector3)frame.up; rayNorth = (Vector3)frame.north;
            // (Worked out number by number, and into a list of the game's own kind which is then handed over whole:
            // done with the vector type's own sums and put into the queries' list one at a time, as it used to
            // be, this took half a millisecond of every frame, most of all the mod asked of the game's own thread.)
            float ox = rayOrigin.x, oy = rayOrigin.y, oz = rayOrigin.z, ex = rayEast.x, ey = rayEast.y, ez = rayEast.z;
            float ux = rayUp.x, uy = rayUp.y, uz = rayUp.z, nx = rayNorth.x, ny = rayNorth.y, nz = rayNorth.z;
            const int mask = (1 << 15) | (1 << 0);                       // the ground and buildings, and the parts of ships
            float ahead = dt * (every + 2f);                             // far enough to cover the frames until its next turn
            RaycastCommand[] list = rayList;
            P[] a = p;
            int[] of = rayOf;
            uint[] seeds = raySeed;
            int n = 0, most = count;
#if DEV
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            for (int i = tick % every; i < most && n < wanted; i += every)
            {
                ref P q = ref a[i];
                float mx = q.vx + q.kx, my = q.vy + q.ky, mz = q.vz + q.kz, speed2 = mx * mx + my * my + mz * mz;
                if (speed2 < 0.0064f || q.life < 0f) continue;           // hanging still (under 8 cm a second): nothing to run into
                float speed = Root(speed2);                              // (not the library's: see Root)
                float inv = 1f / speed, back = q.r * 0.3f;
                mx *= inv; my *= inv; mz *= inv;
                float lx = q.x - mx * back, ly = q.y - my * back, lz = q.z - mz * back, far = speed * ahead + q.r * 0.8f + back;
                list[n] = new RaycastCommand(new Vector3(ox + ex * lx + ux * ly + nx * lz, oy + ey * lx + uy * ly + ny * lz, oz + ez * lx + uz * ly + nz * lz),
                                             new Vector3(ex * mx + ux * my + nx * mz, ey * mx + uy * my + ny * mz, ez * mx + uz * my + nz * mz), far < 30f ? far : 30f, mask, 1);
                of[n] = i;
                seeds[n] = q.seed;
                n++;
            }
            rayCount = n;
            rayEvery = every;
            if (n == 0) return;
#if DEV
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            rays = new NativeArray<RaycastCommand>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            hits = new NativeArray<RaycastHit>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<RaycastCommand>.Copy(list, rays, n);
#if DEV
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            rayJob = RaycastCommand.ScheduleBatch(rays, hits, 256);
            raysOut = true;
#if DEV
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lookParts = n + " queries: made in " + ((t1 - t0) * ms).ToString("F3") + " ms, handed over in " + ((t2 - t1) * ms).ToString("F3") + ", set going in " + ((t3 - t2) * ms).ToString("F3");
#endif
        }
#if DEV
        string lookParts = "";
#endif

        void TurnAside()
        {
            if (!raysOut) return;
            raysOut = false;
            rayJob.Complete();
            if (rayBack.Length < rayCount) rayBack = new RaycastHit[rayCount + 1024];
            NativeArray<RaycastHit>.Copy(hits, rayBack, rayCount);
            RaycastHit[] back = rayBack;
            for (int k = 0; k < rayCount; k++)
            {
                if (back[k].distance <= 0f) continue;                    // (it ran into nothing)
                RaycastHit hit = back[k];
                Collider solid = hit.collider;
                if (solid == null || solid.isTrigger) continue;
                int i = rayOf[k];
                if (i >= count || p[i].seed != raySeed[k]) continue;     // that place in the list has been given to another since
                ref P q = ref p[i];
                Vector3 d = hit.point - rayOrigin, normal = hit.normal;
                float hx = Vector3.Dot(d, rayEast), hy = Vector3.Dot(d, rayUp), hz = Vector3.Dot(d, rayNorth);
                if (grateCount > 0 && OverGrate(hx, hz)) continue;            // (a launch pad's grate: what goes down it goes on down, see the mover)
                float nx = Vector3.Dot(normal, rayEast), ny = Vector3.Dot(normal, rayUp), nz = Vector3.Dot(normal, rayNorth);
                // How far in front of the surface the particle is, and how fast it is closing on it.
                float gap = (q.x - hx) * nx + (q.y - hy) * ny + (q.z - hz) * nz, room = q.r * 0.45f;
                float closing = -((q.vx + q.kx) * nx + (q.vy + q.ky) * ny + (q.vz + q.kz) * nz);
                if (closing <= 0f || gap > room + closing * stepDt * (rayEvery + 3f)) continue;
                if ((q.flags & DiesOnGround) != 0 && gap < 0.5f) { q.life = -1f; continue; }
                if (gap < room)
                {
                    float out_ = Mathf.Min(room - gap, 2f);
                    q.x += nx * out_; q.y += ny * out_; q.z += nz * out_;
                }
                // It cannot go on into the surface: what is left of its motion runs along it.
                float into = q.vx * nx + q.vy * ny + q.vz * nz;
                if (into < 0f) { q.vx -= into * nx; q.vy -= into * ny; q.vz -= into * nz; }
                into = q.kx * nx + q.ky * ny + q.kz * nz;
                if (into < 0f) { q.kx = (q.kx - into * nx) * 0.8f; q.ky = (q.ky - into * ny) * 0.8f; q.kz = (q.kz - into * nz) * 0.8f; }
                into = q.ux * nx + q.uy * ny + q.uz * nz;
                if (into < 0f) { q.ux -= into * nx; q.uy -= into * ny; q.uz -= into * nz; }
            }
            rays.Dispose();
            hits.Dispose();
        }

        float hurry = 1f;
        long moved;
        Task job;
        readonly Action jobBody;
        readonly List<Plan> waiting = new List<Plan>();

        /// <summary>Runs on a worker thread: move every particle on by one frame and work out how it looks, in several parts at once.</summary>
        void Moving()
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (chunks == 1) RunChunk(0);
            else Parallel.For(0, chunks, chunkBody);
            moved = System.Diagnostics.Stopwatch.GetTimestamp() - began;
        }

        void RunChunk(int chunk)
        {
            int from = (int)((long)count * chunk / chunks), to = (int)((long)count * (chunk + 1) / chunks);
            // Everything the loop needs, taken out of the object once.
            // (A patch that goes along with a ship: nothing in it lasts long enough to fall, and the air it is in goes by at the ship's speed.)
            float dt = stepDt, g = riding ? 0f : gravity, hold = grip, still = calm, rush = hurry;
            float byX = ridesGoing.x, byY = ridesGoing.y, byZ = ridesGoing.z;
            float ph = time * 0.22f, tm = time, goneX = windGoneX, goneZ = windGoneZ;
            float k1 = 6.2832f / (scale * 1.7f + 3f), k2 = 6.2832f / (scale * 0.6f + 1.2f), k3 = 6.2832f / (scale * 0.21f + 0.7f);
            float cx = camL.x, cy = camL.y, cz = camL.z, sx = sunL.x, sy = sunL.y, sz = sunL.z;
            float sR = sunR, sG = sunG, sB = sunB, aR = ambR, aG = ambG, aB = ambB, fR = fireR, fG = fireG, fB = fireB;
            float wX = windX, wZ = windZ, w10 = wind10, gs = gustScale;
            float[] al = aloft;
            bool sun = sunUp, floored = hasGround;
            float gY = groundY, gSX = groundSX, gSZ = groundSZ;
            int grates = grateCount;                                           // (a launch pad's grate under a ship's patch: see Grates)
            bool bare = groundBare && riding, wet = groundWet && riding;       // (what an engine's jet raises off the ground under a ship: see Under)
            float dustR = groundDust.r, dustG = groundDust.g, dustB = groundDust.b;
            // (the ground as it really lies, once it has been sounded: see SiteGround.cs)
            Land under = land;
            float[] lie = under != null ? under.h : null;
            float lieX0 = under != null ? under.x0 : 0f, lieZ0 = under != null ? under.z0 : 0f, lieInv = under != null ? under.inv : 0f;
            int now = tick, nThermals = thermalCount, nBalls = ballCount, nJets = jetCount;
            bool volumed = box != null && gridReady;                           // the volume draws what is inside its box; sprites only what is outside
            float vx0 = gx0, vy0 = gy0, vz0 = gz0, vix = gix, viy = giy, viz = giz;
            // (how fast the smoke is being pulled out of shape, as the grid in use has it: see Afresh)
            float[] pulled = gridReady ? strainNow : null;
            bool oneRound = Settings.TestTurns >= 1f;                          // (for testing: every bit of smoke on the same round, as it used to be)
            int wrapped = turnsWrapped;
            float goingX = stepGoing.x, goingY = stepGoing.y, goingZ = stepGoing.z, alongX = goingX * dt, alongY = goingY * dt, alongZ = goingZ * dt;
            const float perBearable = 1f / Bearable;
            float sx0 = gx0 + slid.x, sy0 = gy0 + slid.y, sz0 = gz0 + slid.z, six = gix * 0.5f, siy = giy * 0.5f, siz = giz * 0.5f;
            bool tilt = turned;
            Vector3 tX = turnX, tY = turnY, tZ = turnZ;
            Thermal[] th = thermals;
            Ball[] bl = balls;
            Jet[] jt = jets;
            // (what is left turning behind things that have gone through the smoke, and the way things are about to go: see Wakes.cs)
            int nWakes = Settings.Vortices ? wakeCount : 0, nAheads = Settings.Predict ? aheadCount : 0;
            Wake[] wk = wakes;
            Coming[] ah = aheads;
            float wLoX = wakeLo.x, wLoY = wakeLo.y, wLoZ = wakeLo.z, wHiX = wakeHi.x, wHiY = wakeHi.y, wHiZ = wakeHi.z;
            float hLoX = aheadLo.x, hLoY = aheadLo.y, hLoZ = aheadLo.z, hHiX = aheadHi.x, hHiY = aheadHi.y, hHiZ = aheadHi.z;
            float[] flames = Flames, sin = sine;
            P[] a = p;
            ParticleSystem.Particle[] d = draw;
            for (int i = from; i < to; i++)
            {
                ref P q = ref a[i];
                q.age += q.heat < 0.1f && q.age > 5f ? dt * rush : dt;
                if (q.age >= q.life) { q.life = -1f; continue; }
                // (young smoke handed on to the patch of the ship's trail: it fades here as its copy there comes on, see Hand)
                if ((q.flags & HandedOn) != 0) { float left = q.life - q.age; q.mass *= left > 0f ? left / (left + dt) : 0f; }
                if (q.age < 0f) { d[i].position = new Vector3(q.x, q.y, q.z); d[i].startColor = new Color32(0, 0, 0, 0); continue; }      // (laid ahead of its engine, and waiting for it: see GridLate)
                // ---- its billows take their turns (see Afresh), and where it "was" goes along with the smoke as a whole
                Turn(ref q, tm, dt, wrapped, oneRound);
                q.ax += alongX; q.ay += alongY; q.az += alongZ; q.bx += alongX; q.by += alongY; q.bz += alongZ; q.ex += alongX; q.ey += alongY; q.ez += alongZ;
                q.cx += alongX; q.cy += alongY; q.cz += alongZ; q.dx += alongX; q.dy += alongY; q.dz += alongZ; q.fx += alongX; q.fy += alongY; q.fz += alongZ;
                int turn = (i + now) & 3;
                bool fresh = q.age <= dt * 1.5f;                 // its first frame: everything is worked out at once

                if ((q.flags & Ballistic) == 0)
                {
                    if (turn == 0 || fresh)
                    {
                        // ---- the air where this particle is. It changes slowly, so each particle looks every fourth frame.
                        // How far above the ground it is, and how steeply the ground rises there to the east and to the north.
                        float height = 10f, slopeE = 0f, slopeN = 0f;
                        if (lie != null) height = q.y - Height(lie, lieX0, lieZ0, lieInv, q.x, q.z, out slopeE, out slopeN);
                        else if (floored) { height = q.y - (gY - q.x * gSX - q.z * gSZ); slopeE = -gSX; slopeN = -gSZ; }
                        float w = w10 * AloftFrom(al, height);
                        // The wind comes in gusts, short ones upon long ones, which travel down the wind; and it swings from side
                        // to side, so a plume wanders instead of running dead straight.
                        float down = q.x * wX + q.z * wZ, across = q.z * wX - q.x * wZ;
                        float gust = 1f + gs * (0.22f * sin[(int)((0.7f * tm - 0.02f * down) * 651.8986f) & 4095] + 0.16f * sin[(int)((0.23f * tm - 0.008f * down + 1.3f) * 651.8986f) & 4095]);
                        float swing = (0.2f * sin[(int)((0.31f * tm - 0.012f * down + 0.011f * across) * 651.8986f) & 4095] + 0.12f * sin[(int)((0.11f * tm + 2.1f) * 651.8986f) & 4095]) * w;
                        float ux = wX * w * gust - wZ * swing, uy = 0f, uz = wZ * w * gust + wX * swing;
                        float stir = still;

                        for (int n = 0; n < nThermals; n++)
                        {
                            // A bubble of hot gas going up: inside it the gas climbs through the middle and comes
                            // back down round the outside (Hill's vortex), which is what rolls a fireball into a mushroom.
                            float dx = q.x - th[n].x, dy = q.y - th[n].y, dz = q.z - th[n].z;
                            float r2 = dx * dx + dy * dy + dz * dz, ta = th[n].a, a2 = ta * ta, tw = th[n].w;
                            if (r2 < a2)
                            {
                                // (a blast's turns itself over as hard as such a bubble can; one going up from a fire, more gently)
                                float k = 1.5f * tw * th[n].turn / a2;
                                uy += k * (a2 - 2f * (dx * dx + dz * dz) - dy * dy) + tw;
                                ux += k * dy * dx;
                                uz += k * dy * dz;
                                if (stir < 0.32f * tw) stir = 0.32f * tw;                // the gas inside it is churning
                            }
                            else if (r2 < a2 * 30f)
                            {
                                float r = Root(r2), f = ta * a2 / (2f * r2 * r2 * r), c = 3f * tw * dy;
                                ux += f * c * dx;
                                uy += f * (c * dy - tw * r2);
                                uz += f * c * dz;
                                float edge = 0.32f * tw * a2 / r2;
                                if (stir < edge) stir = edge;
                            }
                        }

                        float amp = q.turb;
                        if (amp > 0.02f)
                        {
                            // Eddies at three sizes. Each is a flow that neither piles the air up nor thins it out. They are carried
                            // along by the wind, as real ones are, and change only slowly as they go: smoke drifting with the wind
                            // stays in the eddy it is in and is wound up by it, instead of being shaken by each one it is blown through.
                            float ex = q.x - goneX, ez = q.z - goneZ;
                            float X = ex * k1, Y = q.y * k1, Z = ez * k1;
                            float e1 = sin[(int)((Z + ph) * 651.8986f) & 4095] + sin[(int)((Y - ph * 0.7f + 3.3f) * 651.8986f) & 4095];
                            float e2 = sin[(int)((X + ph * 0.4f + 2.1f) * 651.8986f) & 4095] + sin[(int)((Z + ph + 1.5708f) * 651.8986f) & 4095];
                            float e3 = sin[(int)((Y - ph * 0.7f + 1.73f) * 651.8986f) & 4095] + sin[(int)((X + ph * 0.4f + 3.67f) * 651.8986f) & 4095];
                            X = ex * k2; Y = q.y * k2; Z = ez * k2;
                            float f1 = sin[(int)((Y + ph * 1.3f + 0.6f) * 651.8986f) & 4095] + sin[(int)((Z - ph * 1.1f + 4.0f) * 651.8986f) & 4095];
                            float f2 = sin[(int)((Z - ph * 1.1f + 2.43f) * 651.8986f) & 4095] + sin[(int)((X + ph * 0.9f + 5.2f) * 651.8986f) & 4095];
                            float f3 = sin[(int)((X + ph * 0.9f + 3.63f) * 651.8986f) & 4095] + sin[(int)((Y + ph * 1.3f + 2.17f) * 651.8986f) & 4095];
                            X = ex * k3; Y = q.y * k3; Z = ez * k3;
                            float g1 = sin[(int)((Z + ph * 1.9f + 4.4f) * 651.8986f) & 4095] + sin[(int)((Y - ph * 1.6f + 0.9f) * 651.8986f) & 4095];
                            float g2 = sin[(int)((X - ph * 1.7f + 1.2f) * 651.8986f) & 4095] + sin[(int)((Z + ph * 1.9f + 5.97f) * 651.8986f) & 4095];
                            float g3 = sin[(int)((Y - ph * 1.6f - 0.67f) * 651.8986f) & 4095] + sin[(int)((X - ph * 1.7f + 2.77f) * 651.8986f) & 4095];
                            ux += amp * (0.40f * e1 + 0.30f * f1 + 0.22f * g1);
                            uy += amp * (0.40f * e2 + 0.30f * f2 + 0.22f * g2);
                            uz += amp * (0.40f * e3 + 0.30f * f3 + 0.22f * g3);
                        }
                        uy += q.rise > 0f ? q.rise * (0.3f + q.heat) : q.rise;
                        if (floored)
                        {
                            // Smoke that has cooled, and dust, and cold vapour, are heavier than the air round them: lying on
                            // sloping ground they run down it, slowly on a gentle slope and faster on a steep one. (What is
                            // still hot enough to climb does not.)
                            float lying = 1f - height / (q.r + 2f);
                            if (lying > 0f && (slopeE != 0f || slopeN != 0f))
                            {
                                float climbs = q.rise > 0f ? q.rise * (0.3f + q.heat) : 0f;
                                if (climbs < 0.5f)
                                {
                                    float runs = Downhill * (lying > 1f ? 1f : lying) * (1f - 2f * climbs) / Root(1f + slopeE * slopeE + slopeN * slopeN);
                                    ux -= slopeE * runs; uz -= slopeN * runs;
                                }
                            }
                            // The air does not go into the ground: near it, it goes along it, up a rise and down a fall. Within
                            // twelve metres of the ground the air is carried up or down with the ground under it (all the way, at
                            // the ground itself). And within three, what is left of its motion towards the ground dies away to
                            // nothing at the ground: smoke brought down by an eddy, or by the air coming back down round a
                            // rising bubble, is not pressed into the ground but goes on along it with whatever way it has sideways.
                            float along = ux * slopeE + uz * slopeN;
                            if (height < 12f) uy += along * (height <= 0f ? 1f : 1f - height * 0.083333f);
                            float into = uy - along;
                            if (into < 0f && height < 3f) uy = along + (height <= 0f ? 0f : into * height * 0.33333f);
                        }
                        q.ux = ux; q.uy = uy; q.uz = uz;
                        // Stirring builds up fast and dies away slowly.
                        float rate = stir > q.turb ? dt * 8f : dt * 1.0f;
                        q.turb += (stir - q.turb) * (rate > 1f ? 1f : rate);
                        // How fast its billows take their turns (see Afresh): a round for as long as the air here takes to pull
                        // the smoke that far out of shape, and no longer than takes it as far as the grid can tell it has come.
                        if (pulled != null)
                        {
                            float px = q.x, py = q.y, pz = q.z;
                            if (tilt) { px = tX.x * q.x + tX.y * q.y + tX.z * q.z; py = tY.x * q.x + tY.y * q.y + tY.z * q.z; pz = tZ.x * q.x + tZ.y * q.y + tZ.z * q.z; }     // (the grid is turned: see TurnAlong)
                            float u = (px - sx0) * six - 0.5f, v = (py - sy0) * siy - 0.5f, w3 = (pz - sz0) * siz - 0.5f;
                            if (u > -0.5f && v > -0.5f && w3 > -0.5f && u < A - 0.5f && v < A - 0.5f && w3 < A - 0.5f)
                            {
                                u = u < 0f ? 0f : u > A - 1.001f ? A - 1.001f : u; v = v < 0f ? 0f : v > A - 1.001f ? A - 1.001f : v; w3 = w3 < 0f ? 0f : w3 > A - 1.001f ? A - 1.001f : w3;
                                int ci = (int)u, cj = (int)v, ck = (int)w3, at = ci + (cj + ck * A) * A;
                                float fu = u - ci, fv = v - cj, fw = w3 - ck;
                                float low = (pulled[at] + (pulled[at + 1] - pulled[at]) * fu) * (1f - fv) + (pulled[at + A] + (pulled[at + A + 1] - pulled[at + A]) * fu) * fv;
                                at += A * A;
                                float high = (pulled[at] + (pulled[at + 1] - pulled[at]) * fu) * (1f - fv) + (pulled[at + A] + (pulled[at + A + 1] - pulled[at + A]) * fu) * fv;
                                float wanted = (low + (high - low) * fw) * perBearable;
                                float rx = q.vx + q.kx - goingX, ry = q.vy + q.ky - goingY, rz = q.vz + q.kz - goingZ;
                                float least = Root(rx * rx + ry * ry + rz * rz) * (1f / FarthestBack);
                                if (wanted < least) wanted = least;
                                wanted = wanted < Slowest ? Slowest : wanted > 1f / QuickestRound ? 1f / QuickestRound : wanted;
                                // (it quickens at once and slackens over a second or so: this is looked at every fourth frame)
                                float eases = wanted > q.pace ? dt * 12f : dt * 3f;
                                q.pace += (wanted - q.pace) * (eases > 1f ? 1f : eases);
                                // Onto a quicker round as soon as the air wants it, a step at each look. (Onto a slower one only as
                                // that round begins: see Turn.)
                                int on = (int)q.octave;
                                if (on > 0 && q.pace > PerRound[on]) q.octave = on - 1;
                            }
                        }
                        if (q.rise > 0f) q.rise *= 1f / (1f + 0.24f * dt);
                    }
                    float airX = q.ux - byX, airY = q.uy - byY, airZ = q.uz - byZ;

                    // (What has just come out of an engine is going the other way from the ship at the speed of its jet: for its
                    // first second and a half it is not the ship's wake's to carry along. Dragged along as other smoke is, a
                    // rocket's smoke climbed after the rocket and wrapped it.)
                    int hulls = (q.flags & Exhausted) != 0 && q.age < 1.5f ? 0 : nBalls;
                    for (int n = 0; n < hulls; n++)
                    {
                        // A ship's hull: the air parts round it, closes in behind, and is dragged along in its wake.
                        float dx = q.x - bl[n].x, dy = q.y - bl[n].y, dz = q.z - bl[n].z;
                        float br = bl[n].r, reach = br * 4f + q.r, r2 = dx * dx + dy * dy + dz * dz;
                        if (r2 > reach * reach) continue;
                        float r = Root(r2) + 1e-4f;
                        float bvx = bl[n].vx, bvy = bl[n].vy, bvz = bl[n].vz;
                        if (r < br)
                        {
                            // Smoke cannot be inside the hull: it goes with it and out to its side.
                            float s = br / r;
                            dx *= s; dy *= s; dz *= s;
                            q.x = bl[n].x + dx; q.y = bl[n].y + dy; q.z = bl[n].z + dz;
                            q.vx = bvx; q.vy = bvy; q.vz = bvz;
                            r = br; r2 = br * br;
                        }
                        float f = br * br * br / (2f * r2 * r2 * r), along = bvx * dx + bvy * dy + bvz * dz;
                        airX += f * (3f * along * dx - bvx * r2);
                        airY += f * (3f * along * dy - bvy * r2);
                        airZ += f * (3f * along * dz - bvz * r2);
                        float speed = bl[n].speed, behind = -along / speed;
                        if (behind > 0f && behind < br * 10f)
                        {
                            float side2 = r2 - behind * behind, width = br * 1.7f;
                            if (side2 < width * width)
                            {
                                float pull = 0.6f * (1f - side2 / (width * width)) / (1f + behind / (br * 4f));
                                airX += bvx * pull; airY += bvy * pull; airZ += bvz * pull;
                                float stir = speed * 0.22f > 6f ? 6f : speed * 0.22f;
                                if (q.turb < stir) q.turb = stir;
                            }
                        }
                    }

                    for (int n = 0; n < nJets; n++)
                    {
                        // A running engine blows it away down the line of the exhaust.
                        float dx = q.x - jt[n].x, dy = q.y - jt[n].y, dz = q.z - jt[n].z;
                        float along = dx * jt[n].dx + dy * jt[n].dy + dz * jt[n].dz, jr = jt[n].r;
                        if (along < -jr || along > jt[n].length) continue;
                        float width = jr + 0.17f * (along > 0f ? along : 0f);
                        float side2 = dx * dx + dy * dy + dz * dz - along * along, edge = 9f * width * width;
                        if (side2 > edge) continue;
                        float fall = 1f - side2 / edge, blow = jt[n].speed * (jr / width) * fall * fall;
                        airX += jt[n].dx * blow; airY += jt[n].dy * blow; airZ += jt[n].dz * blow;
                        float stir = blow * 0.3f > 8f ? 8f : blow * 0.3f;
                        if (q.turb < stir) q.turb = stir;
                    }

                    // (the air left turning behind what has gone through: see Wakes.cs)
                    if (nWakes > 0 && q.x > wLoX && q.x < wHiX && q.y > wLoY && q.y < wHiY && q.z > wLoZ && q.z < wHiZ)
                        Swirled(ref q, wk, nWakes, ref airX, ref airY, ref airZ, sin);

                    // ---- it takes up the air's motion, and the air takes away the blast's push
                    float x = q.drag * hold * dt, follow = x / (1f + x);
                    q.vx += (airX - q.vx) * follow;
                    q.vy += (airY - q.vy) * follow;
                    q.vz += (airZ - q.vz) * follow;
                    // (Thin air stops a blast later, but not without limit: the gas spreads its push over more of it as it goes.)
                    float keep = 1f / (1f + q.kloss * (hold < 0.55f ? 0.55f : hold) * dt);
                    q.kx *= keep; q.ky *= keep; q.kz *= keep;
                    // It spreads by mixing with the air round it: quickly while small, ever more slowly as it widens.
                    q.r += (q.grow / q.r + 0.012f * q.turb) * dt;
                }
                else
                {
                    // Thrown clear, or in a vacuum: nothing but gravity (and a little air, where there is any).
                    q.vy -= g * dt;
                    if (hold > 0f)
                    {
                        float keep = 1f / (1f + 0.35f * hold * dt);
                        q.vx *= keep; q.vy *= keep; q.vz *= keep;
                    }
                    q.r += q.grow * dt;
                }
                q.x += (q.vx + q.kx) * dt;
                q.y += (q.vy + q.ky) * dt;
                q.z += (q.vz + q.kz) * dt;

                if (floored)
                {
                    float floor = (lie != null ? Height(lie, lieX0, lieZ0, lieInv, q.x, q.z) : gY - q.x * gSX - q.z * gSZ) + q.r * 0.4f;
                    if (q.y < floor && grates > 0 && OverGrate(q.x, q.z))
                    {
                        // Over a launch pad's grate, what goes down goes on down, into the trench under it, out of sight: a
                        // flame's gas, and an engine's young smoke, which comes out again at the trench's ends (see Pads).
                        if (q.mass > 0f && q.y < floor - q.r * 0.4f - 1.5f) { q.life = -1f; continue; }
                    }
                    else if (q.y < floor)
                    {
                        if ((q.flags & DiesOnGround) != 0 && q.age > 0.15f) { q.life = -1f; continue; }
                        // Up out of the ground: at once if it is only just under, otherwise at a few metres a second.
                        float below = floor - q.y;
                        q.y += below < 0.3f ? below : 4f * dt > 0.3f ? 4f * dt : 0.3f;
                        // Which way the ground faces here: straight out of it is (-e, 1, -n), made one long.
                        float e = -gSX, n = -gSZ;
                        if (lie != null) Height(lie, lieX0, lieZ0, lieInv, q.x, q.z, out e, out n);
                        float slant = 1f / (1f + e * e + n * n);
                        // It cannot go on into the ground: what is left of its motion runs along it (and so, on a slope, down it).
                        float sinks = q.vy - q.vx * e - q.vz * n;
                        if (sinks < 0f) { sinks *= slant; q.vx += sinks * e; q.vy -= sinks; q.vz += sinks * n; }
                        float driven = q.ky - q.kx * e - q.kz * n;
                        // (An engine's jet stopped by the ground is stirred into the air about it all at once, and its smoke climbs
                        // off the ground the faster for there being so much of it together: as the smoke an engine lays on the
                        // ground itself is, see Vents.)
                        if (driven < -5f && (q.flags & Exhausted) != 0 && q.mass > 0f)
                        {
                            if (q.turb < 4f) q.turb = 4f;
                            if (q.rise < 1.2f) q.rise = 1.2f;
                            // (and off bare ground it raises dust, of the ground's own colour, and off water spray: a part of the puff,
                            // and so much more in it. Once.)
                            if ((q.flags & Raised) == 0 && (bare || wet))
                            {
                                q.flags |= Raised;
                                float raised = 0.25f + 0.35f * ((q.seed >> 8) & 255) * (1f / 255f), light = 0.8f + 0.35f * (q.seed & 255) * (1f / 255f);
                                float tr = wet ? 0.9f : dustR, tg = wet ? 0.92f : dustG, tb = wet ? 0.94f : dustB;
                                q.ar += (tr * light - q.ar) * raised; q.ag += (tg * light - q.ag) * raised; q.ab += (tb * light - q.ab) * raised;
                                q.mass *= 1f + raised;
                            }
                        }
                        if (driven < 0f)
                        {
                            // What a blast was driving into the ground goes out along the ground instead, as gas does (most
                            // of it; a little comes back up): the way it was already going along it, if it was going any way,
                            // and otherwise whichever way this bit of smoke happens to, so that what comes straight down
                            // spreads as a ring.
                            float part = driven * slant, tx = q.kx + part * e, ty = q.ky - part, tz = q.kz + part * n;
                            float down = -driven * Root(slant), along = Root(tx * tx + ty * ty + tz * tz);
                            if (along > 0.1f * down) { float more = 1f + 0.6f * down / along; tx *= more; ty *= more; tz *= more; }
                            else
                            {
                                float whichWay = (q.seed & 4095) * 0.0015339808f, east = Sin(whichWay + 1.5707963f) * 0.6f * down, north = Sin(whichWay) * 0.6f * down;
                                tx += east; ty += east * e + north * n; tz += north;
                            }
                            float back = 0.1f * down * Root(slant);
                            q.kx = tx - e * back; q.ky = ty + back; q.kz = tz - n * back;
                        }
                    }
                }

                float push = (q.kx < 0f ? -q.kx : q.kx) + (q.ky < 0f ? -q.ky : q.ky) + (q.kz < 0f ? -q.kz : q.kz);
                if (push < 0.002f) { q.kx = 0f; q.ky = 0f; q.kz = 0f; push = 0f; }      // (numbers this small slow the processor down)
                // (An engine's jet widens by how far it has really gone. The sum above, which is what a blast's puffs are
                // reckoned by, is that for a jet along one of the patch's axes and up to seven tenths more for one aslant:
                // a rocket's flame grew fatter as the rocket leaned over.)
                else if ((q.flags & Exhausted) != 0) push = Root(q.kx * q.kx + q.ky * q.ky + q.kz * q.kz);
                q.r += (0.02f + q.swell) * push * dt;
                if (q.r > 80f) q.r = 80f;
                q.heat *= 1f / (1f + q.cool * dt);
                if (q.heat < 0.002f) q.heat = 0f;
                // (in the way of something coming, and big: to be split before it gets here, see Wakes.cs)
                if (nAheads > 0 && q.mass > 0f && (q.flags & Split) == 0 && q.age > 0.1f && q.x > hLoX && q.x < hHiX && q.y > hLoY && q.y < hHiY && q.z > hLoZ && q.z < hHiZ)
                    InTheWay(ref q, i, ah, nAheads);
                q.roll += q.spin * dt;
                d[i].position = new Vector3(q.x, q.y, q.z);
                d[i].startSize = q.r * 2.5f;
                d[i].rotation = q.roll;

                // Where the smoke is drawn as a volume, that is all that is drawn of it. (The few particles that stray out of
                // the grid's box used to be drawn as sprites; but a sprite is a soft ball metres across, and from near by
                // even a few of them lay over the volume like a blurred copy of it.)
                if (volumed) { d[i].startColor = new Color32(0, 0, 0, 0); continue; }
                const float mine = 1f;
                if (turn == 1 || fresh) Shade(ref q, sun);
                if ((turn & 1) != 0 && !fresh) continue;

                // ---- how it looks (worked out every other frame; it changes slowly)
                float vx = q.x - cx, vy = q.y - cy, vz = q.z - cz;
                float far = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
                float near = (far - 0.4f - 0.5f * q.r) / (1.2f + q.r);       // fades as the camera goes into it
                near = near < 0f ? 0f : near > 1f ? 1f : near;
                float fade = q.life - q.age < q.life * 0.22f ? (q.life - q.age) / (q.life * 0.22f) : 1f;
                if (q.fadeIn > 0f && q.age < q.fadeIn) fade *= q.age / q.fadeIn;

                float soot = q.heat > 0.5f ? 0f : q.heat < 0.12f ? 1f : (0.5f - q.heat) * 2.6316f;   // smoke shows as the flame dies
                float tau = q.mass / (q.r * q.r);
                float hides = (1f - 1f / (1f + tau + 0.5f * tau * tau)) * soot * fade;
                float spread = q.born * 1.6f / q.r;
                float burns = q.flame * (q.heat < 0.02f ? 0f : q.heat > 0.3f ? 1f : (q.heat - 0.02f) * 3.5714f) * (q.mass > 0f ? 1f : fade) * (spread < 1f ? spread * spread : 1f);

                if (!volumed && q.tile < 8 && tau < 0.28f && q.heat <= 0f && q.mass > 0f && q.age > 8f)
                {
                    // Smoke this thin has been pulled into strings by now: show it as a wisp.
                    q.tile = (byte)(8 + (q.seed & 3));
                    d[i].remainingLifetime = 1000f * (1f - (q.tile + 0.5f) / 16f);
                }
                float cr = 0f, cg = 0f, cb = 0f;
                if (hides > 0.002f)
                {
                    // Sunlight that got through the cloud to here (and a little that scattered round), sky, and firelight.
                    float direct = q.lit * 0.72f + 0.28f * (float)Math.Sqrt(Math.Sqrt(q.lit));
                    float toSun = (vx * sx + vy * sy + vz * sz) / (far + 1e-3f);
                    if (toSun > 0f) { float t4 = toSun * toSun; direct *= 1f + 1.5f * t4 * t4 * (1f - hides); }   // thin smoke in front of the sun shines
                    float open = 0.35f + 0.65f * q.sky;
                    // Beside a fire the eye is taken up by the fire: the daylight on the smoke there counts for little.
                    float dazzle = 1f - 0.85f * (q.glow > 2.5f ? 1f : q.glow * 0.4f);
                    direct *= dazzle; open *= dazzle;
                    // Thinned out, dark smoke looks paler: more of the light gets back out of it.
                    float pale = tau < 1f && q.ar < 0.2f ? 0.5f * (1f - tau) * (0.2f - q.ar) : 0f;
                    cr = (float)Math.Sqrt((q.ar + pale) * (sR * direct + aR * open + fR * q.glow)) * hides;
                    cg = (float)Math.Sqrt((q.ag + pale) * (sG * direct + aG * open + fG * q.glow)) * hides;
                    cb = (float)Math.Sqrt((q.ab + pale) * (sB * direct + aB * open + fB * q.glow)) * hides;
                }
                float covers = hides;
                if (burns > 0.002f)
                {
                    float h = q.heat > 1f ? 5f : q.heat * 5f;
                    int key = (int)h;
                    if (key > 4) key = 4;
                    float mix = h - key;
                    int at = (q.tint * 6 + key) * 3;
                    float bright = burns * 0.62f;
                    cr += (flames[at] + (flames[at + 3] - flames[at]) * mix) * bright;
                    cg += (flames[at + 1] + (flames[at + 4] - flames[at + 1]) * mix) * bright;
                    cb += (flames[at + 2] + (flames[at + 5] - flames[at + 2]) * mix) * bright;
                    covers += 0.38f * burns * (1f - hides);
                }
                float show = near * mine;
                covers *= show;
                // The shader draws colour x alpha and hides alpha squared, so both get the root of what is wanted.
                float av = (float)Math.Sqrt(covers), scaleUp = av > 1e-3f ? show * 255f / av : 0f;
                cr *= scaleUp; cg *= scaleUp; cb *= scaleUp;
                d[i].startColor = new Color32((byte)(cr > 255f ? 255f : cr), (byte)(cg > 255f ? 255f : cg), (byte)(cb > 255f ? 255f : cb), (byte)(av * 255f));
            }
        }

        // Flame colours as they look on the screen, coolest to hottest, six steps for each kind of fire:
        // fuel, solid propellant and burning metal, monopropellant, electrical, hydrogen; and, for engines' flames alone,
        // methane, the fuels that keep (a small upper-stage engine's), and gas heated by a reactor.
        static readonly float[] Flames =
        {
            0f, 0f, 0f,   0.55f, 0.10f, 0.02f,   1.00f, 0.36f, 0.06f,   1.00f, 0.62f, 0.16f,   1.00f, 0.84f, 0.42f,   1.00f, 0.95f, 0.74f,
            0f, 0f, 0f,   0.60f, 0.16f, 0.04f,   1.00f, 0.50f, 0.14f,   1.00f, 0.78f, 0.40f,   1.00f, 0.93f, 0.70f,   1.00f, 0.98f, 0.90f,
            0f, 0f, 0f,   0.35f, 0.20f, 0.06f,   0.80f, 0.62f, 0.25f,   0.95f, 0.85f, 0.50f,   1.00f, 0.95f, 0.75f,   1.00f, 0.98f, 0.90f,
            0f, 0f, 0f,   0.06f, 0.10f, 0.45f,   0.20f, 0.35f, 0.90f,   0.45f, 0.65f, 1.00f,   0.75f, 0.88f, 1.00f,   0.92f, 0.97f, 1.00f,
            0f, 0f, 0f,   0.10f, 0.10f, 0.25f,   0.30f, 0.32f, 0.60f,   0.55f, 0.60f, 0.85f,   0.80f, 0.85f, 1.00f,   0.95f, 0.97f, 1.00f,
            0f, 0f, 0f,   0.14f, 0.08f, 0.30f,   0.38f, 0.26f, 0.66f,   0.66f, 0.50f, 0.90f,   0.90f, 0.78f, 1.00f,   1.00f, 0.93f, 1.00f,
            0f, 0f, 0f,   0.14f, 0.12f, 0.30f,   0.38f, 0.36f, 0.68f,   0.62f, 0.62f, 0.90f,   0.85f, 0.88f, 1.00f,   0.96f, 0.97f, 1.00f,
            0f, 0f, 0f,   0.30f, 0.06f, 0.04f,   0.62f, 0.18f, 0.10f,   0.85f, 0.38f, 0.22f,   1.00f, 0.62f, 0.42f,   1.00f, 0.82f, 0.66f,
        };

        /// <summary>Which of two families a kind of fire is of, the yellow or the blue: one patch of air shows its fire in the colours of one kind, and those of a family are near enough alike to share one.</summary>
        public static int Family(int kind) => kind >= 3 && kind <= 6 ? 1 : 0;
        /// <summary>The family of the flames in a patch that goes along with a ship (see Family).</summary>
        public int FlameFamily { get; private set; }

        static float[] Sines()
        {
            var table = new float[4096];
            for (int n = 0; n < table.Length; n++) table[n] = (float)Math.Sin(n * (2.0 * Math.PI / table.Length));
            return table;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Sin(float angle) => sine[(int)(angle * 651.8986f) & 4095];

        // ================================================================== light through the cloud

        const int G = 64, G2 = G * G, G3 = G * G * G;
        const int Levels = 4;                                    // the grid itself, and three coarser ones for what is too wide to be put on it cheaply
        const int A = 32, A3 = A * A * A;                        // the grid of how much smoke there is round about each place
        const float Thickest = 3f, ThickestFlame = 1.2f;         // what the fullest values in the volume mean, per metre
        // (Room the C# below works in. Where the grids are made by the library instead, see Native, none of it is ever allotted: see NeedRoom.)
        float[][] field;
        float[] thick => field[0];                               // how much the smoke in each cell blocks, per metre
        float[] pale => field[1];                                // the same, times how light the smoke itself is
        float[] dusty => field[2];                               // the same, times how much of it is dust
        float[] flaming => field[3];                             // how much the flame in each cell blocks, per metre
        float[] hotness => field[4];                             // the same, times how hot it is
        float[][][] wide;                                        // the same five on grids of half, a quarter and an eighth as many cells each way
        float[][][][] apart = new float[0][][][];                // what each thread puts on those coarser grids, kept apart until they are added up
        float[][][] spare;
        readonly bool[] wideUsed = new bool[Levels];
        float[] sunThrough = new float[G3], skyThrough = new float[G3], sunNext = new float[G3], skyNext = new float[G3];      // how much smoke lies between each cell and the sun, and the open sky
        readonly Color32[] cellsA = new Color32[G3], cellsB = new Color32[G3], cellsC = new Color32[A3];
        readonly Color32[] cellsD = new Color32[A3];
        readonly byte[] cellsClear = new byte[A3];
        byte[] clearWork;
        readonly Action<int> clearLayer;
        readonly ushort[][] cellsRest = new ushort[5][];
        float[] anything, roundSmoke, roundPale, roundDust;
        float[][] layers;
        float gx0, gy0, gz0, gix, giy, giz;
        // A grid's box lies along the site's own axes: east, up, north. That suits a cloud, which is as wide one way
        // as another. An engine's trail is a line, and as a rule it lies along none of them: a rocket climbing at
        // forty-five degrees leaves three hundred metres of smoke ten metres wide across the diagonal of a box two
        // hundred metres square, whose cells are then metres across, and nothing can be put on a grid finer than
        // its cells: the trail came out a blur thirty metres wide with the rocket inside it. So the grid of a patch
        // of air that an engine going fast begins is turned, once and for good, to lie along the way the engine is
        // going: the long way of the box is along the trail and its cells across the trail are as fine as the trail
        // is narrow. Everything about the smoke itself stays in the site's axes (up is still up for it, and the wind
        // still blows from where it blows); only the copy of it that a grid is made from is turned (see Lighting),
        // and with it everything the shader is told about places in the box.
        //
        // A trail's grid is turned once. The grid of a ship's flames (a patch that goes along with the ship) is turned
        // afresh for every grid, to lie along the jets as the ship now points: so there are three of everything here,
        // the way the next grid is to lie, the way the one being made lies, and the way the one in use lies.
        bool turned, lightTurned, wantTurned, turnDecided, litOnce;
        bool blasted;                                             // whether anything has ever blown up here (a patch of air may hold an engine's smoke and nothing else)
        Vector3 turnX = Vector3.right, turnY = Vector3.up, turnZ = Vector3.forward;      // the grid's three axes, in the site's: of the grid in use,
        Vector3 lightX = Vector3.right, lightY = Vector3.up, lightZ = Vector3.forward;   // of the grid being made,
        Vector3 wantX = Vector3.right, wantY = Vector3.up, wantZ = Vector3.forward;      // and of the next
        Vector3 gridSun, gridUp = Vector3.up;                     // towards the sun and towards the sky in the grid's axes, for the grid being worked out
        float ventedAt;
        readonly bool riding;                                     // this patch goes along with a ship (see Frame)
        Vector3 ridesGoing;                                       // and how fast, over the ground, in its own axes

        /// <summary>Whether this patch goes along with a ship, and with what.</summary>
        public bool Riding => riding;
        /// <summary>Whether there is no air here to hold smoke.</summary>
        public bool Airless => vacuum;
        /// <summary>Whether a ship's patch has held a flame yet (or only its young smoke).</summary>
        public bool HasFlame => hasFlame;
        bool hasFlame;
        public Transform Rides => frame.rides;

        Vector3 ToGrid(Vector3 s) => turned ? new Vector3(turnX.x * s.x + turnX.y * s.y + turnX.z * s.z, turnY.x * s.x + turnY.y * s.y + turnY.z * s.z, turnZ.x * s.x + turnZ.y * s.y + turnZ.z * s.z) : s;
        Vector3 FromGrid(Vector3 g) => turned ? turnX * g.x + turnY * g.y + turnZ * g.z : g;

        /// <summary>Turn this site's grid so that one of its axes lies along this way: the one that is nearest to it already, so that it is turned as little as may be.</summary>
        void TurnAlong(Vector3 way)
        {
            float x = Mathf.Abs(way.x), y = Mathf.Abs(way.y), z = Mathf.Abs(way.z);
            Vector3 axis = x >= y && x >= z ? new Vector3(Mathf.Sign(way.x), 0f, 0f) : y >= z ? new Vector3(0f, Mathf.Sign(way.y), 0f) : new Vector3(0f, 0f, Mathf.Sign(way.z));
            if (Vector3.Dot(way, axis) > 0.9962f) { wantTurned = false; return; }          // (within five degrees of one already)
            Quaternion back = Quaternion.Inverse(Quaternion.FromToRotation(way, axis));
            wantX = back * Vector3.right; wantY = back * Vector3.up; wantZ = back * Vector3.forward;
            wantTurned = true;
        }

        /// <summary>
        /// What is under a patch that goes along with a ship, looked for afresh every frame: the ground or the sea, as a
        /// level (or sloping) plane. (A patch that stays where it is sounds the ground all about it, once: see Survey.)
        /// </summary>
        void Under()
        {
            if (!frame.Riding) return;                              // (the ship is gone: the ground stays where it was last found)
            Vector3 from = (Vector3)frame.origin, up = (Vector3)frame.up, point = from, normal = up;
            bool any = false;
            float below = float.MaxValue;
            if (Physics.Raycast(from + up * 3f, -up, out RaycastHit hit, 163f, 1 << 15, QueryTriggerInteraction.Ignore)) { any = true; below = hit.distance - 3f; point = hit.point; normal = hit.normal; }
            if (body.ocean)
            {
                double altitude = body.GetAltitude(frame.origin);
                if (altitude < 160.0 && altitude < below) { any = true; point = from - up * (float)altitude; normal = up; }
            }
            hasGround = any;
            if (!any) return;
            // (What the ground there is, now and then: what an engine's jet raises off it, see the mover. Paving gives nothing.)
            if (time >= nextGroundKind)
            {
                nextGroundKind = time + 0.5f;
                bool sea = body.ocean && below > (float)body.GetAltitude(frame.origin) - 0.5f;
                bool paved = !sea && hit.collider != null && (hit.collider.GetComponentInParent<PQSCity>() != null || hit.collider.GetComponentInParent<PQSCity2>() != null);
                groundWet = sea; groundBare = !sea && !paved;
                if (groundBare)
                {
                    Color seen = Plan.Ground(body, (Vector3d)point, false);
                    groundDust = new Color(seen.r * seen.r * 1.2f, seen.g * seen.g * 1.2f, seen.b * seen.b * 1.2f);
                }
            }
            Vector3 at = frame.ToLocal((Vector3d)point), n = frame.DirToLocal((Vector3d)normal);
            if (n.y < 0.35f) n = Vector3.up;                        // a wall or a cliff: treat it as level
            groundSX = Mathf.Clamp(n.x / n.y, -0.5f, 0.5f);
            groundSZ = Mathf.Clamp(n.z / n.y, -0.5f, 0.5f);
            groundY = at.y + groundSX * at.x + groundSZ * at.z;
        }
        float nextGroundKind;                                     // (when a ship's patch next looks at what the ground under it is: see Under)
        float nx0, ny0, nz0, nx1, ny1, nz1, nix, niy, niz;       // the grid being worked out
        float cellNow, cellNext;                                  // the smallest side of a cell, metres
        float ax0, ay0, az0, ax1, ay1, az1;                       // the box round every particle, as last worked out
        long lightTook;
        bool gridReady, nextReady;
        int lightCount, lightParts;
        float lightDue, lightTime, lightBefore = -1f, lightRepeat = 9f;     // when the grid in hand was started, when the one before was, and the size of the detail then
        Task lighting;
        readonly Action lightBody;
        // ---- where the smoke "was", and which way it is going (see Afresh and Carry)
        const int Reckonings = 6;                                 // three for the big billows (0 to 2), three for the small (3 to 5)
        const int Counts = 4 + 3 * Reckonings;                    // on the coarser grid, for each cell: how much is there; its velocity; how far it is from where it "was", by each reckoning;
        const int Rate = Counts + Reckonings;                     // how much each reckoning counts for there; and how fast the smoke there is going through its round
        const int Carried = Rate + 1;
        // A round of turns is the time the big billows' three sets take to begin afresh once each (the small ones go
        // round twice in that time). How long a round lasts is each bit of smoke's own affair (see Afresh): one of
        // seven lengths, each twice the one before, from just over half a second to just over half a minute.
        const int Octaves = 7;
        const float QuickestRound = 0.59375f, LongestRound = 38f;
        const float UsualRound = 2.8f;                            // (what every round used to last: for testing against)
        static readonly float[] PerRound = { 1f / 0.59375f, 1f / 1.1875f, 1f / 2.375f, 1f / 4.75f, 1f / 9.5f, 1f / 19f, 1f / 38f };      // rounds a second, for each of the seven
        // How far out of shape the air may pull a set of billows in one round: by its own size and half as much again.
        // Smoke on a round in which it is pulled further than that goes over to the next quicker; smoke pulled less
        // than a third as far as that goes over to the next slower (in which it is then pulled two thirds as far: well
        // inside, so that smoke the air cannot make up its mind about stays where it is).
        // (Tried from 0.8 to 2.4. With more, calm smoke keeps its billows longer, but in the half minute after a fire
        // goes out, while the smoke is settling on to slower rounds, more of it shows them drawn out: twice their
        // length or more for a twentieth of it at 1, a tenth at 1.5, a sixth at 2.)
        const float Bearable = 1.5f;
        const float Lenient = 0.3f;
        const float FarthestBack = 110f;                          // metres: nor does a round last longer than takes the smoke this far from the rest (the place it "was" must stay within what the grid can tell: see RestFar)
        const float Slowest = 0.8f * Lenient / 19f;               // rounds a second: the least the air is taken to want (little enough for smoke to go over to the slowest round of all)
        Vector3 stepGoing;                                        // (which way the pattern is carried in the frame being worked out: see airGoing)
        int turnsCounted, turnsWrapped = -1;                      // how many of the quickest rounds there have been at this site, and the slowest round that began afresh in this frame (-1: none did)
        const float RestFar = 128f;                               // metres: the farthest smoke can be told to be from where it "was"
        const float Ahead = 0.15f;                                // seconds: the longest a grid is in use before the next is ready
        float[][] carryNext = new float[Carried][], carryNow = new float[Carried][];
        float[][] carryTemp;                                      // (room for the smoothing of each to be worked out in)
        readonly Action carryBody;
        readonly Action<int> carrySmooth;
        float[] strainNext = new float[A3], strainNow = new float[A3];      // how fast the smoke is being pulled out of shape at each cell of the coarser grid, per second: for the grid being made, and for the one in use (see Afresh)
        readonly Color32[] cellsE = new Color32[A3];              // whose turn it is at each cell, for the shader: the point of its round the smoke there is at, for the big billows' three reckonings and for the small ones' (see Carry)
        float shownTime, nextFastest = 2f, shownFastest = 2f;     // when the particles the grid in use was made from were as it shows them; the speed the grid of velocities is scaled by
        // Which way the cloud as a whole is flying, where it is flying fast (the fire of a ship that blew up at speed, a
        // crash thrown on ahead of where it hit): for the grid being made, and for the one in use. Between one grid and
        // the next the shader carries the smoke on by what each part of it is doing, but only by a cell or so; a whole
        // cloud doing hundreds of metres a second covers many cells in that time, and used to stand still for three
        // frames and then leap. So the box itself goes along at this speed, and the grid of velocities holds only
        // what is left over. (Nothing of it for a cloud drifting on the wind: under 8 m/s it is all left to the shader, as before.)
        Vector3 nextDrift, shownDrift, slid, boxMiddle;            // (slid: how far the box has gone along since its grid was made)
        // The billows' pattern goes along with the smoke as a whole (see Afresh: where each bit of smoke "was" is kept
        // in step with it). Which way all that can be seen of the smoke is going, taken together, as each grid finds it;
        // how far the pattern has been carried along by now; and how far it had been when the grid in use was made.
        Vector3 nextGoing, airGoing;
        Vector3d airGone, lightGone, shownGone;
        float nextSlows, shownSlows;                               // how fast the air is taking that speed away, per second (in the three frames a grid is in use, a fast cloud in thick air loses a fifth of it)
        readonly Action<int> depositBody, composeBody, gatherBody, aroundLayer, aroundSmooth;
        readonly Action sunBody, skyBody, aroundBody;
        float dX, dY, dZ, dVolume;                               // cell sizes for the pass in hand
        bool lightFloored;                                        // the ground as the grid in hand takes it: whether there is any (and, until it has been sounded, the tilted plane it is: see Floors and Deposit)
        float lightGroundY, lightGroundSX, lightGroundSZ;
        float[][] stands = new float[0][];                        // (room for each thread to work out where each column of a puff lying on the ground stands)
        int[][] rows = new int[0][];
        float grainSize, spacing, leastReach;                     // how fine the grid is and how far apart the particles stand, both taken smoothly (see Lighting), and the smallest ball anything is spread over

        /// <summary>
        /// The grid is worked out on other threads while the game carries on, and swapped in when it is
        /// done: a frame or two late, which smoke does not show.
        /// </summary>
        void Relight()
        {
            if (lighting != null)
            {
                // (when a film is being made, frame by frame, each frame waits for its grid: the film then shows what the game would at its best)
                if (Time.captureFramerate != 0) { try { lighting.Wait(); } catch (Exception) { } }
                if (!lighting.IsCompleted) return;
                lighting = null;
                if (nextReady)
                {
                    float[] swap = sunThrough; sunThrough = sunNext; sunNext = swap;
                    swap = skyThrough; skyThrough = skyNext; skyNext = swap;
                    gx0 = nx0; gy0 = ny0; gz0 = nz0; gix = nix; giy = niy; giz = niz;
                    bx0 = ax0; by0 = ay0; bz0 = az0; bx1 = ax1; by1 = ay1; bz1 = az1;
                    cellNow = cellNext;
                    Air.Cost[3] += lightTook;
                    float[][] other = carryNow; carryNow = carryNext; carryNext = other;
                    float[] pulled = strainNow; strainNow = strainNext; strainNext = pulled;
                    shownTime = lightTime; shownFastest = nextFastest; shownDrift = nextDrift; shownSlows = nextSlows;
                    shownGone = lightGone; airGoing = nextGoing;
                    turned = lightTurned; turnX = lightX; turnY = lightY; turnZ = lightZ;
                    if (box != null) ShowVolume();
                }
                gridReady = nextReady;
            }
            if (count == 0)
            {
                gridReady = false;
                if (box != null) boxRenderer.enabled = false;
                return;
            }
            // Seen from a distance, twenty-five grids a second are plenty for smoke, and leave the processor to the game.
            // Close to, where it moves fast across the screen, and in the first moments of a blast, it gets one as often as they can be made.
            if (Time.captureFramerate == 0 && Time.unscaledTime < lightDue) return;
            float dx = Mathf.Max(0f, Mathf.Max(bx0 - camL.x, camL.x - bx1)), dy = Mathf.Max(0f, Mathf.Max(by0 - camL.y, camL.y - by1)), dz = Mathf.Max(0f, Mathf.Max(bz0 - camL.z, camL.z - bz1));
            float away = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            float wait = time - lastBlast < 2.5f && !vented ? 0f : Mathf.Clamp((away - 12f) * 0.0008f, 0f, 0.04f);
            // (The smoke an engine has left behind changes slowly once the engine has gone on: a few grids a second do for
            // it, the shader carrying it on between them, and several such trails can then hang at once.)
            if (vented && time - fedAt > 1.5f && time - lastBlast > 2.5f) wait = Mathf.Clamp(0.05f + (time - fedAt) * 0.02f + away * 0.0003f, 0.06f, 0.25f);
            // ("As often as they can be made" was some thirty a second when each took the best part of a frame or two to make.
            // The library makes one in a fraction of that, and one for every frame would only be more to hand to the graphics card.)
            if (Settings.Native && nativeWork != IntPtr.Zero && wait < 0.033f) wait = 0.033f;
            lightDue = Time.unscaledTime + wait + Settings.TestGrids;
            // The grid is made from a copy of the particles as they are at this moment. (Made from the list itself, which
            // goes on being moved about and tidied while the grid is worked out, it now and then missed a particle or
            // counted one twice: unnoticed in the body of a cloud, but at its edge a wisp flickered out for a frame.)
            // (An engine's smoke: which way its grid is to lie is known at the engine's second frame here: see Vents.)
            if (vented && !turnDecided) { if (time - ventedAt < 0.15f) return; turnDecided = true; }
            litOnce = true;
            lightTurned = wantTurned; lightX = wantX; lightY = wantY; lightZ = wantZ;
            if (seen.Length < count) seen = new P[p.Length];
            Array.Copy(p, seen, count);
            if (vented)
            {
                // (An engine's newest smoke is taken to be as old as it will be when this grid is seen, and what is still
                // waiting for its nozzle to arrive by then is not there yet: see GridLate.)
                P[] copy = seen;
                float early = laidEarly;
                for (int i = 0; i < count; i++)
                    if ((copy[i].flags & Exhausted) != 0 && copy[i].age < copy[i].fadeIn) { float a = copy[i].age + early; copy[i].age = a < 0f ? 0f : a; }
            }
            lightCount = count;
            lightTime = time;
            lightGone = airGone;
            lightLand = land;
            lightRepeat = detailRepeat;
            Air.Few.MaxDegreeOfParallelism = Settings.Threads > 0 ? Settings.Threads : -1;      // (the setting "threads" holds for the making of a grid as for everything else)
            lighting = Task.Run(lightBody);
        }

        /// <summary>
        /// Turn the particles into smooth fields on a grid: how much smoke and flame is in each cell, how
        /// light the smoke is, how hot the flame. Each particle is spread over a ball of its own size, so
        /// that what is drawn is one body of smoke and not its particles. Then the sun's light and the
        /// sky's light are followed through it: the far side of a cloud is dark, the underside darker, and
        /// smoke under other smoke is in its shadow.
        ///
        /// The grid has the same number of cells however big the cloud, so its cells grow with the cloud.
        /// Nothing that can be seen may depend on their size, or the smoke would change each time they
        /// grow. So how smooth the smoke is comes from the particles alone (which spread steadily as they
        /// mix with the air); the cells only have to be fine enough to hold that.
        /// </summary>
        void Lighting()
        {
            P[] a = seen;
            int n = Math.Min(lightCount, a.Length);
            nextReady = false;
            if (n == 0) return;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, szz = 0, sr = 0;
            int live = 0;
            // Which way it is all going, taken together, and how much of that the box is to go along with. Each particle
            // counts for what can be seen of it just now, as Deposit reckons that: its flame while it burns, its smoke
            // once that shows. (Counted by all the smoke in them, the cool rind of a fireball, which is slower and
            // not to be seen at first, outweighed the fire, and the fire outran its box.)
            double mvx = 0, mvy = 0, mvz = 0, mw = 0, slows = 0;
            for (int i = 0; i < n; i++)
            {
                if (a[i].life < 0f) continue;
                float heat = a[i].heat, age = a[i].age, life = a[i].life;
                float fade = life - age < life * 0.22f ? (life - age) / (life * 0.22f) : 1f;
                if (a[i].fadeIn > 0f && age < a[i].fadeIn) fade *= age / a[i].fadeIn;
                float soot = heat > 0.5f ? 0f : heat < 0.12f ? 1f : (0.5f - heat) * 2.6316f;
                float w = fade * (a[i].mass * soot + a[i].flame * (heat < 0.02f ? 0f : heat > 0.3f ? 1f : (heat - 0.02f) * 3.5714f) * a[i].r * a[i].r) + 1e-6f;
                mvx += w * (a[i].vx + a[i].kx); mvy += w * (a[i].vy + a[i].ky); mvz += w * (a[i].vz + a[i].kz); mw += w;
                slows += w * ((a[i].flags & Ballistic) != 0 ? 0.35f : a[i].drag);
            }
            float driftX = 0f, driftY = 0f, driftZ = 0f;
            nextSlows = mw > 1e-9 ? (float)(slows / mw) * grip : 0f;
            nextGoing = mw > 1e-9 ? new Vector3((float)(mvx / mw), (float)(mvy / mw), (float)(mvz / mw)) : Vector3.zero;
            // (Not the flames of a ship's engines: gas goes through a flame at hundreds of metres a second, but the flame
            // stays on its nozzle. Carried along with what was in it, it left the nozzle between each grid and the next.)
            if (mw > 1e-9 && !riding)
            {
                float ux = (float)(mvx / mw), uy = (float)(mvy / mw), uz = (float)(mvz / mw), speed = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz);
                float share = speed <= 8f ? 0f : speed >= 24f ? 1f : (speed - 8f) / 16f;
                share = share * share * (3f - 2f * share);
                driftX = ux * share; driftY = uy * share; driftZ = uz * share;
            }
            bool tilt = lightTurned;
            float wx0 = float.MaxValue, wy0 = float.MaxValue, wz0 = float.MaxValue, wx1 = float.MinValue, wy1 = float.MinValue, wz1 = float.MinValue;
            gridSun = tilt ? new Vector3(Vector3.Dot(lightX, sunL), Vector3.Dot(lightY, sunL), Vector3.Dot(lightZ, sunL)) : sunL;
            gridUp = tilt ? new Vector3(lightX.y, lightY.y, lightZ.y) : Vector3.up;
            if (tilt)
            {
                // The grid is turned (see TurnAlong): so is this copy of the smoke, from which it is made. Where each bit
                // is, how it is moving, and how far it is from where it "was" by each reckoning: all the grid is told of
                // it that has a direction. (And the box round all of it in the site's own axes is taken first.)
                float xx = lightX.x, xy = lightX.y, xz = lightX.z, yx = lightY.x, yy = lightY.y, yz = lightY.z, zx = lightZ.x, zy = lightZ.y, zz = lightZ.z;
                float d0 = driftX, d1 = driftY, d2 = driftZ;
                driftX = xx * d0 + xy * d1 + xz * d2; driftY = yx * d0 + yy * d1 + yz * d2; driftZ = zx * d0 + zy * d1 + zz * d2;
                for (int i = 0; i < n; i++)
                {
                    if (a[i].life < 0f) continue;
                    ref P q = ref a[i];
                    float e0 = q.x, e1 = q.y, e2 = q.z, r = q.r;
                    if (e0 - r < wx0) wx0 = e0 - r; if (e0 + r > wx1) wx1 = e0 + r;
                    if (e1 - r < wy0) wy0 = e1 - r; if (e1 + r > wy1) wy1 = e1 + r;
                    if (e2 - r < wz0) wz0 = e2 - r; if (e2 + r > wz1) wz1 = e2 + r;
                    q.x = xx * e0 + xy * e1 + xz * e2; q.y = yx * e0 + yy * e1 + yz * e2; q.z = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.vx; e1 = q.vy; e2 = q.vz; q.vx = xx * e0 + xy * e1 + xz * e2; q.vy = yx * e0 + yy * e1 + yz * e2; q.vz = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.kx; e1 = q.ky; e2 = q.kz; q.kx = xx * e0 + xy * e1 + xz * e2; q.ky = yx * e0 + yy * e1 + yz * e2; q.kz = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.ax; e1 = q.ay; e2 = q.az; q.ax = xx * e0 + xy * e1 + xz * e2; q.ay = yx * e0 + yy * e1 + yz * e2; q.az = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.bx; e1 = q.by; e2 = q.bz; q.bx = xx * e0 + xy * e1 + xz * e2; q.by = yx * e0 + yy * e1 + yz * e2; q.bz = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.ex; e1 = q.ey; e2 = q.ez; q.ex = xx * e0 + xy * e1 + xz * e2; q.ey = yx * e0 + yy * e1 + yz * e2; q.ez = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.cx; e1 = q.cy; e2 = q.cz; q.cx = xx * e0 + xy * e1 + xz * e2; q.cy = yx * e0 + yy * e1 + yz * e2; q.cz = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.dx; e1 = q.dy; e2 = q.dz; q.dx = xx * e0 + xy * e1 + xz * e2; q.dy = yx * e0 + yy * e1 + yz * e2; q.dz = zx * e0 + zy * e1 + zz * e2;
                    e0 = q.fx; e1 = q.fy; e2 = q.fz; q.fx = xx * e0 + xy * e1 + xz * e2; q.fy = yx * e0 + yy * e1 + yz * e2; q.fz = zx * e0 + zy * e1 + zz * e2;
                }
            }
            nextDrift = new Vector3(driftX, driftY, driftZ);
            // (a flame's gas is gone before it has got anywhere: its box is the flame's own length and no longer)
            float reaches = riding ? 0.004f : Ahead;
            for (int i = 0; i < n; i++)
            {
                float x = a[i].x, y = a[i].y, z = a[i].z, r = a[i].r;
                if (a[i].life < 0f) continue;
                // (with room for where each will have got to before the next grid is ready, the box going along as it does:
                // the shader carries the smoke on meanwhile, see Carry, and a blast outruns its box otherwise)
                float gx = (a[i].vx + a[i].kx - driftX) * reaches, gy = (a[i].vy + a[i].ky - driftY) * reaches, gz = (a[i].vz + a[i].kz - driftZ) * reaches;
                if (x - r + (gx < 0f ? gx : 0f) < x0) x0 = x - r + (gx < 0f ? gx : 0f);
                if (x + r + (gx > 0f ? gx : 0f) > x1) x1 = x + r + (gx > 0f ? gx : 0f);
                if (y - r + (gy < 0f ? gy : 0f) < y0) y0 = y - r + (gy < 0f ? gy : 0f);
                if (y + r + (gy > 0f ? gy : 0f) > y1) y1 = y + r + (gy > 0f ? gy : 0f);
                if (z - r + (gz < 0f ? gz : 0f) < z0) z0 = z - r + (gz < 0f ? gz : 0f);
                if (z + r + (gz > 0f ? gz : 0f) > z1) z1 = z + r + (gz > 0f ? gz : 0f);
                sx += x; sy += y; sz += z; sxx += (double)x * x; syy += (double)y * y; szz += (double)z * z; sr += r;
                live++;
            }
            if (live == 0 || x1 <= x0) return;
            // The whole box round everything, for the ships and the ground under it...
            // (in the site's own axes whichever way the grid lies)
            if (tilt && wx1 > wx0) { ax0 = wx0; ay0 = wy0; az0 = wz0; ax1 = wx1; ay1 = wy1; az1 = wz1; }
            else { ax0 = x0; ay0 = y0; az0 = z0; ax1 = x1; ay1 = y1; az1 = z1; }
            // ...and the stretch the grid covers: all of the cloud, down to its last wisp, unless something has
            // strayed far from the rest (see Keep). Round that goes a band a few cells wide in which what does
            // lie outside is faded away. Where the band is depends on the cloud alone and moves as smoothly as
            // the cloud does. (It used to be the faces of the grid's box, which sit on whole cells: a wisp at
            // the edge then lost a part of itself each time the box moved a cell.)
            float typical = (float)(sr / live);
            float fresh = lightTime - lastBlast, far = fresh < 0.6f ? 6.4f : fresh > 1.2f ? 3.2f : 6.4f - 3.2f * (fresh - 0.6f) / 0.6f;
            Keep(sx, sxx, live, typical, far, ref x0, ref x1);
            Keep(sy, syy, live, typical, far, ref y0, ref y1);
            Keep(sz, szz, live, typical, far, ref z0, ref z1);
            float small = riding ? 1f : 3f;
            if (x1 - x0 < 2f * small) { float m = (x0 + x1) * 0.5f; x0 = m - small; x1 = m + small; }
            if (y1 - y0 < 2f * small) { float m = (y0 + y1) * 0.5f; y0 = m - small; y1 = m + small; }
            if (z1 - z0 < 2f * small) { float m = (z0 + z1) * 0.5f; z0 = m - small; z1 = m + small; }
            if (vented)
            {
                // An engine's smoke lies along the way the engine went: a trail a hundred metres long and a few wide, or
                // over a pad a cloud a hundred metres wide and a few high. A box fitted as closely as that has cells
                // like sheets of paper in its narrow directions, and each puff then lies across hundreds of them: putting
                // the puffs on the grid took ten times as long as for a blast with as many. So no side of such a box is
                // less than nineteen puffs' radii: its cells are then never much finer than a third of a puff.
                // (A ship's flames are a finer thing, and few: the box is as narrow as they are, with a little to spare.)
                float least = riding ? Math.Max(2f, 5f * typical) : Math.Max(6f, 19f * typical);
                if (x1 - x0 < least) { float m = (x0 + x1) * 0.5f; x0 = m - 0.5f * least; x1 = m + 0.5f * least; }
                if (z1 - z0 < least) { float m = (z0 + z1) * 0.5f; z0 = m - 0.5f * least; z1 = m + 0.5f * least; }
                // (upward only where it lies on the ground: there is no smoke under that)
                if (y1 - y0 < least) { if (hasGround && !tilt) y1 = y0 + least; else { float m = (y0 + y1) * 0.5f; y0 = m - 0.5f * least; y1 = m + 0.5f * least; } }
            }
            float cx = (x1 - x0) / (G - 6f), cy = (y1 - y0) / (G - 6f), cz = (z1 - z0) / (G - 6f);       // the cells that leave room for the band on both sides
            keepX0 = x0 - 2.5f * cx; keepX1 = x1 + 2.5f * cx; keepY0 = y0 - 2.5f * cy; keepY1 = y1 + 2.5f * cy; keepZ0 = z0 - 2.5f * cz; keepZ1 = z1 + 2.5f * cz;
            fadeX = 1f / (2.5f * cx); fadeY = 1f / (2.5f * cy); fadeZ = 1f / (2.5f * cz);
            float wx = keepX1 - keepX0, wy = keepY1 - keepY0, wz = keepZ1 - keepZ0;
            // The smallest thing the grid can hold is a ball a cell or two across, and smaller particles are
            // spread to that. That size follows the size of the cloud smoothly: were it the size of the cells
            // themselves, which change in steps, small puffs would turn fainter in a jump at every step.
            float wanted = Math.Max(cx, Math.Max(cy, cz));
            // (For an engine's trail, which is long one way and narrow the others, the smallest of the three: a puff need
            // be no wider than the cells across the trail can hold, and along it, where the cells are long, each is drawn
            // out to a cell's length anyway, see Deposit. Taken from the longest, every puff of a trail three hundred
            // metres long was spread into a ball thirteen metres across, and the rocket flew inside its own smoke.)
            if (vented) wanted = Math.Min(cx, Math.Min(cy, cz));
            float since = lightBefore < 0f ? 1e3f : lightTime - lightBefore;
            lightBefore = lightTime;
            if (grainSize <= 0f || since > 5f || wanted > grainSize) grainSize = wanted;
            else grainSize += (wanted - grainSize) * (1f - (float)Math.Exp(-since / 3f));
            // Nor is anything spread over less than the particles stand apart, taking the cloud as a whole: where a blast
            // has flung them wide (in thin air, in a vacuum) each would otherwise show as a ball of its own.
            float apartNow = (float)Math.Pow(0.3 * wx * wy * wz / Math.Max(60, n), 1.0 / 3.0);
            if (spacing <= 0f || since > 5f) spacing = apartNow;
            else spacing += (apartNow - spacing) * (1f - (float)Math.Exp(-since / 1.5f));
            // (more so where there is little air to gather the smoke into puffs, which is what hides it elsewhere)
            // (but never over more than a few cells: dust flung far and wide in a vacuum is not a cloud to be filled in)
            leastReach = Math.Max(2.4f * grainSize, Math.Min(4f * grainSize, (2f - 0.75f * Math.Min(1f, grip * 1.6f)) * spacing));
            // (Not an engine's smoke, which is laid puff upon puff along a line: how far apart its puffs stand "taking the
            // cloud as a whole" is the emptiness of the box round the line, and going by that the trail was spread to
            // nine metres across where its puffs were three.)
            if (vented && (!blasted || lightTime - lastBlast > 6f)) leastReach = 2.4f * grainSize;
            // The grid must not be re-fitted a little differently every time, or the smoke shimmers as it is
            // sampled on ever-shifting cells. So the cells only come in set sizes, a quarter apart, which grow
            // as soon as the cloud needs it but shrink only when it needs much less; and the box is placed on
            // whole cells, so that as it follows the cloud its cells stay where they were.
            float test = Settings.TestCell, nudge = Settings.TestShift;
            dX = Rung(cx, ref heldX) * test; dY = Rung(cy, ref heldY) * test; dZ = Rung(cz, ref heldZ) * test;
            wx = dX * G; wy = dY * G; wz = dZ * G;
            // (the box starts on a whole cell at or before the start of that stretch, and being a cell longer than it, reaches past its end)
            x0 = ((float)Math.Floor(keepX0 / dX) + nudge) * dX; y0 = ((float)Math.Floor(keepY0 / dY) + nudge) * dY; z0 = ((float)Math.Floor(keepZ0 / dZ) + nudge) * dZ;
            x1 = x0 + wx; y1 = y0 + wy; z1 = z0 + wz;
            dVolume = dX * dY * dZ;
            nx0 = x0; ny0 = y0; nz0 = z0; nx1 = x1; ny1 = y1; nz1 = z1; nix = 1f / dX; niy = 1f / dY; niz = 1f / dZ;
            cellNext = Math.Min(dX, Math.Min(dY, dZ));

#if DEV
            if (Settings.TestView == 8f && vented && time - fedAt < 0.5f)
                Debug.Log("[VolumetricExplosions] trace: grid of patch " + GetHashCode() % 1000 + " at " + lightTime.ToString("F2") + ": " + n + " puffs, mean radius " + typical.ToString("F1") + ", cells " + dX.ToString("F2") + " " + dY.ToString("F2") + " " + dZ.ToString("F2") +
                          ", grain " + grainSize.ToString("F2") + ", nothing over less than " + leastReach.ToString("F2") + ", apart " + spacing.ToString("F2") + (tilt ? ", turned" : ""));
#endif
            lightCount = n;
            lightFloored = hasGround && !tilt; lightGroundY = groundY; lightGroundSX = groundSX; lightGroundSZ = groundSZ;
            if (lightFloored) Floors();
            // From here on it is all sums, and the library does them where there is one (see Native). What follows
            // after is the same in C#, for everywhere else.
            if (onlyBy != 1 && Settings.Native && nativeWork != IntPtr.Zero && NativeGrid(n, began)) return;
            ManagedGrid(n, began);
        }

        /// <summary>The second half of the making of a grid, in the game's own runtime: the particles onto the grid, the light through it, and the rest of what the shader asks for.</summary>
        void ManagedGrid(int n, long began)
        {
            NeedRoom();
            lightParts = n < 600 ? 1 : Math.Min(Air.Threads, Air.Few.MaxDegreeOfParallelism > 0 ? Air.Few.MaxDegreeOfParallelism : Air.Threads);
            for (int c = 0; c < field.Length; c++) Array.Clear(field[c], 0, G3);
            if (apart.Length < lightParts)
            {
                var more = new float[lightParts][][][];
                for (int part = 0; part < lightParts; part++)
                {
                    if (part < apart.Length) { more[part] = apart[part]; continue; }
                    more[part] = new float[Levels][][];
                    for (int level = 1; level < Levels; level++)
                    {
                        int side = G >> level;
                        more[part][level] = new[] { new float[side * side * side], new float[side * side * side], new float[side * side * side], new float[side * side * side], new float[side * side * side] };
                    }
                }
                apart = more;
            }
            if (stands.Length < lightParts)
            {
                var room = new float[lightParts][];
                var which = new int[lightParts][];
                for (int part = 0; part < lightParts; part++) { room[part] = new float[G2]; which[part] = new int[2 * G]; }
                stands = room; rows = which;
            }
            for (int level = 1; level < Levels; level++)
            {
                if (!wideUsed[level]) continue;                  // (left clear the last time)
                wideUsed[level] = false;
                for (int part = 0; part < apart.Length; part++)
                    for (int c = 0; c < 5; c++) Array.Clear(apart[part][level][c], 0, apart[part][level][c].Length);
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lightParts == 1) Deposit(0);
            else Parallel.For(0, lightParts, Air.Few, depositBody);
            if (wideUsed[1] || wideUsed[2] || wideUsed[3]) Parallel.For(0, field.Length, Air.Few, gatherBody);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            if (box == null) { if (sunUp) Parallel.Invoke(Air.Few, sunBody, skyBody, aroundBody); else Parallel.Invoke(Air.Few, skyBody, aroundBody); }
            else if (sunUp) Parallel.Invoke(Air.Few, sunBody, skyBody, aroundBody, carryBody);
            else Parallel.Invoke(Air.Few, skyBody, aroundBody, carryBody);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (box != null) { Parallel.For(0, G, Air.Few, composeBody); Clearance(); }
            nextReady = true;
            lightTook = System.Diagnostics.Stopwatch.GetTimestamp() - began;
            // (for the development build: what each part of that took)
            Air.Stage[0] = t0 - began; Air.Stage[1] = t1 - t0; Air.Stage[6] = t2 - t1; Air.Stage[7] = began + lightTook - t2;
        }

        /// <summary>Room for the C# above to work in: allotted the first time it is wanted, which is never where the library makes the grids.</summary>
        void NeedRoom()
        {
            if (field != null) return;
            var wideNew = new float[Levels][][];
            for (int level = 1; level < Levels; level++)
            {
                int side = G >> level;
                wideNew[level] = new[] { new float[side * side * side], new float[side * side * side], new float[side * side * side], new float[side * side * side], new float[side * side * side] };
            }
            wide = wideNew;
            var spareNew = new float[5][][];
            for (int c = 0; c < spareNew.Length; c++) spareNew[c] = new[] { new float[G * G / 2 * G / 2], new float[G * G * G / 2] };
            spare = spareNew;
            layers = new[] { new float[G2], new float[G2], new float[G2], new float[G2] };
            anything = new float[A3]; roundSmoke = new float[A3]; roundPale = new float[A3]; roundDust = new float[A3];
            var temp = new float[Carried][];
            for (int c = 0; c < Carried; c++) temp[c] = new float[A3];
            carryTemp = temp;
            clearWork = new byte[(A + 2) * (A + 2) * (A + 2)];
            field = new[] { new float[G3], new float[G3], new float[G3], new float[G3], new float[G3] };
        }

        // ---- the same in C (see Native, and Native/vfxgrid.c)
        IntPtr nativeWork;                                        // the library's own room for this site's grids; nothing where there is no library
        readonly List<GCHandle> held = new List<GCHandle>();      // what of this site's memory the library writes to, kept where it is for as long as the site lasts
        readonly Dictionary<object, IntPtr> heldAt = new Dictionary<object, IntPtr>();
        readonly IntPtr[] carryAt = new IntPtr[Carried], restAt = new IntPtr[5];
        bool nativeComplained;
        int onlyBy;                                               // (for the check below: 1 has the next grid made by the C# whatever else)

#if DEV
        /// <summary>
        /// For the development build: how much of the smoke's small picture hides what is behind it, and how
        /// completely: of its pixels with any smoke in them, the share that hide less than half, half to nine
        /// tenths, nine tenths to 98%, 98% to all but a thousandth, and everything.
        /// </summary>
        public string Opacity()
        {
            if (small == null) return "no picture";
            var read = new Texture2D(small.width, small.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = small;
            read.ReadPixels(new Rect(0, 0, small.width, small.height), 0, 0, false);
            RenderTexture.active = before;
            Color[] c = read.GetPixels();
            UnityEngine.Object.Destroy(read);
            int any = 0, thin = 0, half = 0, most = 0, nearly = 0, all = 0;
            foreach (Color one in c)
            {
                if (one.a < 0.02f) continue;
                any++;
                if (one.a < 0.5f) thin++; else if (one.a < 0.9f) half++; else if (one.a < 0.98f) most++; else if (one.a < 0.999f) nearly++; else all++;
            }
            if (any == 0) return "no smoke in the picture";
            return (100f * any / c.Length).ToString("F0") + "% of the picture has smoke; of that, hiding under half " + (100f * thin / any).ToString("F0") + "%, half to 90% " + (100f * half / any).ToString("F0") +
                   "%, 90 to 98% " + (100f * most / any).ToString("F0") + "%, 98 to 99.9% " + (100f * nearly / any).ToString("F0") + "%, everything " + (100f * all / any).ToString("F0") + "%";
        }

        Color[] kept;

        Color[] Picture()
        {
            var read = new Texture2D(small.width, small.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture before = RenderTexture.active;
            RenderTexture.active = small;
            read.ReadPixels(new Rect(0, 0, small.width, small.height), 0, 0, false);
            RenderTexture.active = before;
            Color[] c = read.GetPixels();
            UnityEngine.Object.Destroy(read);
            return c;
        }

        /// <summary>
        /// For the development build: how long the rounds of the billows are just now (see Afresh), and how far the
        /// smoke in each cell of the coarser grid agrees about whose turn it is (1: all of it at the same point of
        /// its round; 0: at every point of it alike).
        /// </summary>
        public string DescribeTurns()
        {
            var rounds = new List<float>();
            var stirs = new List<float>();
            var pushes = new List<float>();
            var on = new int[Octaves];
            float[] re = new float[A3], im = new float[A3], much = new float[A3];
            float sx0 = gx0 + slid.x, sy0 = gy0 + slid.y, sz0 = gz0 + slid.z, six = gix * 0.5f, siy = giy * 0.5f, siz = giz * 0.5f;
            for (int i = 0; i < count; i++)
            {
                if (p[i].life < 0f || p[i].r <= 0.01f || p[i].mass <= 0f || (p[i].flags & Ballistic) != 0) continue;
                rounds.Add(1f / Mathf.Max(p[i].pace, 1e-4f));
                stirs.Add(p[i].turb);
                pushes.Add(Mathf.Sqrt(p[i].kx * p[i].kx + p[i].ky * p[i].ky + p[i].kz * p[i].kz));
                on[Mathf.Clamp((int)p[i].octave, 0, Octaves - 1)]++;
                float u = (p[i].x - sx0) * six, v = (p[i].y - sy0) * siy, w = (p[i].z - sz0) * siz;
                if (u < 0f || v < 0f || w < 0f || u >= A || v >= A || w >= A) continue;
                int cell = (int)u + ((int)v + (int)w * A) * A;
                float angle = 6.2831853f * p[i].turn;
                re[cell] += p[i].mass * Mathf.Cos(angle); im[cell] += p[i].mass * Mathf.Sin(angle); much[cell] += p[i].mass;
            }
            if (rounds.Count == 0) return "no smoke";
            rounds.Sort();
            stirs.Sort();
            pushes.Sort();
            Func<float, string> at = share => rounds[Mathf.Min((int)(share * rounds.Count), rounds.Count - 1)].ToString("F1");
            Func<List<float>, string> range = list => list[(int)(0.05f * (list.Count - 1))].ToString("F2") + " / " + list[(int)(0.5f * (list.Count - 1))].ToString("F2") + " / " + list[(int)(0.95f * (list.Count - 1))].ToString("F2");
            double agree = 0, all = 0, mixed = 0;
            for (int cell = 0; cell < A3; cell++)
            {
                if (much[cell] <= 0f) continue;
                double r = Math.Sqrt(re[cell] * re[cell] + im[cell] * im[cell]) / much[cell];
                agree += r * much[cell]; all += much[cell];
                if (r < 0.5) mixed += much[cell];
            }
            string each = "";
            for (int k = 0; k < Octaves; k++) each += (k > 0 ? ", " : "") + (100 * on[k] / rounds.Count) + "% on " + (1f / PerRound[k]).ToString("G3") + " s";
            // How far out of shape the billows are, as the grid in use has them. The pattern is read at where the smoke
            // "was"; where that changes more slowly from place to place than the place itself does, a lump of the
            // pattern is spread over more of the smoke: drawn out into a streak, by so many times its own length.
            // (For the big billows' three sets, each counting for as much smoke as there is and as much as it counts there.)
            string shape = "";
            if (gridReady)
            {
                float[][] c = carryNow;
                float perX = 0.25f * gix, perY = 0.25f * giy, perZ = 0.25f * giz;        // (one over twice a cell of the coarser grid)
                var drawn = new List<Vector3>();                // (how far drawn out, how much smoke it counts for, how fast that smoke is going)
                double tight15 = 0, tight2 = 0, tight3 = 0;
                double total = 0;
                for (int k = 1; k < A - 1; k++)
                    for (int j = 1; j < A - 1; j++)
                        for (int i = 1; i < A - 1; i++)
                        {
                            int cell = i + (j + k * A) * A;
                            if (c[0][cell] <= 1e-6f) continue;
                            float speed = Mathf.Sqrt(c[1][cell] * c[1][cell] + c[2][cell] * c[2][cell] + c[3][cell] * c[3][cell]);
                            for (int n = 0; n < 3; n++)
                            {
                                float counts = c[0][cell] * c[Counts + n][cell];
                                if (counts <= 0f) continue;
                                float[] ex = c[4 + 3 * n], ey = c[5 + 3 * n], ez = c[6 + 3 * n];
                                // (how where it "was" changes with the place, each way: its columns)
                                double a0 = (ex[cell + 1] - ex[cell - 1]) * perX + 1.0, a1 = (ey[cell + 1] - ey[cell - 1]) * perX, a2 = (ez[cell + 1] - ez[cell - 1]) * perX;
                                double b0 = (ex[cell + A] - ex[cell - A]) * perY, b1 = (ey[cell + A] - ey[cell - A]) * perY + 1.0, b2 = (ez[cell + A] - ez[cell - A]) * perY;
                                double c0 = (ex[cell + A * A] - ex[cell - A * A]) * perZ, c1 = (ey[cell + A * A] - ey[cell - A * A]) * perZ, c2 = (ez[cell + A * A] - ez[cell - A * A]) * perZ + 1.0;
                                // (the least it changes by, any way at all: the square root of the least of the three numbers that
                                // belong to those columns' products with one another)
                                double m00 = a0 * a0 + a1 * a1 + a2 * a2, m11 = b0 * b0 + b1 * b1 + b2 * b2, m22 = c0 * c0 + c1 * c1 + c2 * c2;
                                double m01 = a0 * b0 + a1 * b1 + a2 * b2, m02 = a0 * c0 + a1 * c1 + a2 * c2, m12 = b0 * c0 + b1 * c1 + b2 * c2;
                                double q = (m00 + m11 + m22) / 3.0, off = m01 * m01 + m02 * m02 + m12 * m12;
                                double spread = Math.Sqrt(((m00 - q) * (m00 - q) + (m11 - q) * (m11 - q) + (m22 - q) * (m22 - q) + 2.0 * off) / 6.0), least = q, most = q;
                                if (spread > 1e-9)
                                {
                                    double d00 = (m00 - q) / spread, d11 = (m11 - q) / spread, d22 = (m22 - q) / spread, d01 = m01 / spread, d02 = m02 / spread, d12 = m12 / spread;
                                    double half3 = 0.5 * (d00 * (d11 * d22 - d12 * d12) - d01 * (d01 * d22 - d12 * d02) + d02 * (d01 * d12 - d11 * d02));
                                    double angle = Math.Acos(half3 < -1.0 ? -1.0 : half3 > 1.0 ? 1.0 : half3) / 3.0;
                                    least = q + 2.0 * spread * Math.Cos(angle + 2.0943951);
                                    most = q + 2.0 * spread * Math.Cos(angle);
                                }
                                // (and the most it changes by: how many times over the pattern is squeezed there)
                                double tight = Math.Sqrt(Math.Max(most, 0.0));
                                if (tight > 1.5) tight15 += counts;
                                if (tight > 2.0) tight2 += counts;
                                if (tight > 3.0) tight3 += counts;
                                drawn.Add(new Vector3((float)(1.0 / Math.Sqrt(Math.Max(least, 0.0004))), counts, speed));
                                total += counts;
                            }
                        }
                if (total > 0)
                {
                    Func<List<Vector3>, double, string> say = (list, all) =>
                    {
                        list.Sort((x, y) => x.x.CompareTo(y.x));
                        double run = 0, over2 = 0, over3 = 0, over5 = 0;
                        float half = 0f, tenth = 0f;
                        foreach (Vector3 one in list)
                        {
                            run += one.y;
                            if (half == 0f && run >= 0.5 * all) half = one.x;
                            if (tenth == 0f && run >= 0.9 * all) tenth = one.x;
                            if (one.x > 2f) over2 += one.y;
                            if (one.x > 3f) over3 += one.y;
                            if (one.x > 5f) over5 += one.y;
                        }
                        return half.ToString("F2") + " times their length half-way, " + tenth.ToString("F2") + " for the worst tenth; more than twice for " + (100.0 * over2 / all).ToString("F1") + "%, three times for "
                            + (100.0 * over3 / all).ToString("F1") + "%, five times for " + (100.0 * over5 / all).ToString("F1") + "%";
                    };
                    shape = "; billows drawn out: " + say(drawn, total) + "; squeezed more than one and a half times for " + (100.0 * tight15 / total).ToString("F1") + "% of the smoke, twice for " + (100.0 * tight2 / total).ToString("F1") + "%, three times for " + (100.0 * tight3 / total).ToString("F1") + "%";
                    // (and the same for the tenth of the smoke that is going fastest: the column over a fire, for one)
                    drawn.Sort((x, y) => y.z.CompareTo(x.z));
                    var quick = new List<Vector3>();
                    double some = 0;
                    foreach (Vector3 one in drawn) { if (some >= 0.1 * total) break; quick.Add(one); some += one.y; }
                    if (quick.Count > 0) shape += "; in the fastest tenth of the smoke (" + quick[quick.Count - 1].z.ToString("F1") + " m/s and up): " + say(quick, some);
                }
            }
            return rounds.Count + " puffs: " + each + shape + "; the air would have a round last (seconds) " + at(0.05f) + " for the quickest twentieth, " + at(0.25f) + " at a quarter, " + at(0.5f) + " half-way, " + at(0.75f) + " at three quarters, " + at(0.95f) + " for the slowest twentieth; stirred at (m/s, least twentieth / half-way / most twentieth) " + range(stirs) + ", pushed by a blast at " + range(pushes) + "; "
                + (all > 0 ? "agreement about whose turn it is, cell by cell: " + (agree / all).ToString("F2") + " on the whole, " + (100.0 * mixed / all).ToString("F0") + "% of the smoke in cells under 0.5" : "none of it inside the grid");
        }

        static readonly Vector3[] Ways =
        {
            new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
            new Vector3(0.7071f, 0.7071f, 0f), new Vector3(0.7071f, -0.7071f, 0f), new Vector3(0.7071f, 0f, 0.7071f), new Vector3(0.7071f, 0f, -0.7071f), new Vector3(0f, 0.7071f, 0.7071f), new Vector3(0f, 0.7071f, -0.7071f),
            new Vector3(0.5774f, 0.5774f, 0.5774f), new Vector3(0.5774f, 0.5774f, -0.5774f), new Vector3(0.5774f, -0.5774f, 0.5774f), new Vector3(-0.5774f, 0.5774f, 0.5774f),
        };

        /// <summary>For the development build: keep the picture of the smoke as it was drawn in the last frame (before it is enlarged and put on the screen), to compare a later one against.</summary>
        public string Keep()
        {
            if (small == null) return "no picture";
            kept = Picture();
            return "kept " + small.width + " by " + small.height;
        }

        /// <summary>
        /// For the development build: how the picture of the smoke now differs from the one kept. In 255ths, as
        /// an eight-bit screen would show them: how much of what is behind is hidden, and the smoke's own light.
        /// </summary>
        public string Differ()
        {
            if (small == null || kept == null) return "nothing kept";
            Color[] now = Picture();
            if (now.Length != kept.Length) return "the picture has changed size";
            int smoky = 0, any = 0, one = 0, two = 0, four = 0, gone = 0;
            double sumHide = 0, sumLight = 0, worstHide = 0, worstLight = 0;
            for (int n = 0; n < now.Length; n++)
            {
                Color a = kept[n], b = now[n];
                if (a.a < 0.5f / 255f && b.a < 0.5f / 255f) continue;
                smoky++;
                double hide = Math.Abs(a.a - b.a) * 255.0, light = Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b))) * 255.0, most = Math.Max(hide, light);
                sumHide += hide; sumLight += light;
                if (hide > worstHide) worstHide = hide;
                if (light > worstLight) worstLight = light;
                if (most > 0.01) any++;
                if (most > 1.0) one++;
                if (most > 2.0) two++;
                if (most > 4.0) four++;
                if (a.a >= 0.5f / 255f && b.a < 0.5f / 255f) gone++;
            }
            if (smoky == 0) return "no smoke in either picture";
            return smoky + " pixels with smoke (" + (100.0 * smoky / now.Length).ToString("F0") + "% of the picture): " + (100.0 * any / smoky).ToString("F1") + "% differ at all, " + (100.0 * one / smoky).ToString("F2") + "% by more than 1 of 255, " +
                   (100.0 * two / smoky).ToString("F2") + "% by more than 2, " + (100.0 * four / smoky).ToString("F3") + "% by more than 4; on average " + (sumHide / smoky).ToString("F3") + " in what is hidden and " + (sumLight / smoky).ToString("F3") +
                   " in the light; at worst " + worstHide.ToString("F2") + " and " + worstLight.ToString("F2") + "; " + (100.0 * gone / smoky).ToString("F1") + "% had a trace of smoke and now have none";
        }

        /// <summary>For the development build: every particle written to a file, a line each (place, radius, how it grows, mass, heat, age, life, flame, the blast's push left in it).</summary>
        public string Dump(string path)
        {
            var text = new System.Text.StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < count; i++)
                text.Append(string.Format(inv, "{0:F2} {1:F2} {2:F2} {3:F3} {4:F3} {5:F4} {6:F3} {7:F2} {8:F2} {9:F3} {10:F2} {11:F2} {12:F2} {13:F3}\n", p[i].x, p[i].y, p[i].z, p[i].r, p[i].grow, p[i].mass, p[i].heat, p[i].age, p[i].life, p[i].flame, p[i].kx, p[i].ky, p[i].kz, p[i].swell));
            System.IO.File.WriteAllText(path, text.ToString());
            return count + " particles written to " + path;
        }

        /// <summary>
        /// For the development build: one grid made both ways from the same particles (the C# on one thread, so
        /// that it adds things up in the same order), and how far apart the two come out. They are the same sums,
        /// but the game's runtime carries some of them at higher precision on the way: so, not the same to the
        /// last bit, but nowhere apart by more than the last place of what is handed to the shader.
        /// </summary>
        public string NativeCheck()
        {
            if (nativeWork == IntPtr.Zero) return "no library";
            if (lighting != null) { try { lighting.Wait(); } catch (Exception) { } }
            if (lightCount == 0 || count == 0) return "no particles";
            int threads = Air.Few.MaxDegreeOfParallelism;
            bool native = Settings.Native;
            Air.Few.MaxDegreeOfParallelism = 1;
            Settings.Native = true;
            try
            {
                onlyBy = 1;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Lighting();
                double managedMs = clock.Elapsed.TotalMilliseconds;
                Color32[] a = (Color32[])cellsA.Clone(), b = (Color32[])cellsB.Clone(), c = (Color32[])cellsC.Clone(), d = (Color32[])cellsD.Clone();
                byte[] clears = (byte[])cellsClear.Clone();
                var rest = new ushort[cellsRest.Length][];
                for (int n = 0; n < rest.Length; n++) rest[n] = (ushort[])cellsRest[n].Clone();
                float[] sun = (float[])sunNext.Clone(), sky = (float[])skyNext.Clone();
                var carried = new float[Carried][];
                for (int n = 0; n < Carried; n++) carried[n] = (float[])carryNext[n].Clone();
                Color32[] e = (Color32[])cellsE.Clone();
                float[] pulls = (float[])strainNext.Clone();
                float fastest = nextFastest;
                onlyBy = 0;
                clock.Restart();
                Lighting();
                double nativeMs = clock.Elapsed.TotalMilliseconds;
                if (!Settings.Native) return "the library would not make a grid";
                var text = new System.Text.StringBuilder();
                text.Append(lightCount + " particles; the C# on one thread " + managedMs.ToString("F1") + " ms, the library " + nativeMs.ToString("F1") + " ms. Apart by at most: ");
                int worst = 0, differ = 0;
                for (int n = 0; n < G3; n++) { int x = Math.Abs((b[n].r << 8 | b[n].g) - (cellsB[n].r << 8 | cellsB[n].g)); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append("smoke " + worst + " of 65535 (" + differ + " cells of " + G3 + " differ)");
                worst = 0; differ = 0;
                for (int n = 0; n < G3; n++) { int x = Math.Abs((b[n].b << 8 | b[n].a) - (cellsB[n].b << 8 | cellsB[n].a)); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", flame " + worst + " of 65535 (" + differ + ")");
                worst = 0; differ = 0;
                for (int n = 0; n < G3; n++) { int x = Math.Abs((a[n].r << 8 | a[n].g) - (cellsA[n].r << 8 | cellsA[n].g)); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", smoke towards the sun " + worst + " of 65535 (" + differ + ")");
                worst = 0; differ = 0;
                for (int n = 0; n < G3; n++) { int x = Math.Max(Math.Abs(a[n].b - cellsA[n].b), Math.Abs(a[n].a - cellsA[n].a)); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", sky and heat " + worst + " of 255 (" + differ + ")");
                worst = 0; differ = 0;
                for (int n = 0; n < A3; n++) { int x = Math.Max(Math.Max(Math.Abs(c[n].r - cellsC[n].r), Math.Abs(c[n].g - cellsC[n].g)), Math.Max(Math.Abs(c[n].b - cellsC[n].b), Math.Abs(c[n].a - cellsC[n].a))); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", round about " + worst + " of 255 (" + differ + " of " + A3 + ")");
                worst = 0; differ = 0;
                for (int n = 0; n < A3; n++) { int x = Math.Max(Math.Max(Math.Abs(d[n].r - cellsD[n].r), Math.Abs(d[n].g - cellsD[n].g)), Math.Max(Math.Abs(d[n].b - cellsD[n].b), Math.Abs(d[n].a - cellsD[n].a))); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", going and squeeze " + worst + " of 255 (" + differ + ")");
                worst = 0; differ = 0;
                for (int n = 0; n < A3; n++) { int x = Math.Max(Math.Max(Math.Abs(e[n].r - cellsE[n].r), Math.Abs(e[n].g - cellsE[n].g)), Math.Max(Math.Abs(e[n].b - cellsE[n].b), Math.Abs(e[n].a - cellsE[n].a))); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", whose turn " + worst + " of 255 (" + differ + ")");
                // (Where the smoke "was", as handed to the shader: read back as metres, since as half-precision numbers a
                // difference of a thousandth of a millimetre in something next to nothing is several places of the number.)
                double apart = 0; int codes = 0;
                bool halves = Assets.SixteenAsHalves;
                for (int k = 0; k < rest.Length; k++)
                    for (int n = 0; n < rest[k].Length; n++)
                    {
                        int number = k * 4 + (n & 3);
                        if (number >= 3 * Reckonings) continue;
                        double one = halves ? Mathf.HalfToFloat(rest[k][n]) : (rest[k][n] / 65535.0 - 0.5) * 2 * RestFar, other = halves ? Mathf.HalfToFloat(cellsRest[k][n]) : (cellsRest[k][n] / 65535.0 - 0.5) * 2 * RestFar;
                        apart = Math.Max(apart, Math.Abs(one - other));
                        // (and the library's packing of its own number against Unity's packing of the same number)
                        int unity = halves ? Mathf.FloatToHalf(carryNext[4 + number][n >> 2]) : Far(carryNext[4 + number][n >> 2]);
                        codes = Math.Max(codes, Math.Abs(unity - cellsRest[k][n]));
                    }
                text.Append(", where it was, as handed over, " + apart.ToString("G3") + " m (the library's packing of a number against the game's own: at most " + codes + " apart)");
                worst = 0; differ = 0;
                for (int n = 0; n < A3; n++) { int x = Math.Abs(clears[n] - cellsClear[n]); if (x > 0) differ++; if (x > worst) worst = x; }
                text.Append(", clear air " + worst + " cells (" + differ + ")");
                double far = 0, most = 0;
                for (int n = 0; n < G3; n++) { far = Math.Max(far, Math.Max(Math.Abs(sun[n] - sunNext[n]), Math.Abs(sky[n] - skyNext[n]))); most = Math.Max(most, Math.Max(Math.Abs(sun[n]), Math.Abs(sky[n]))); }
                text.Append("; light for the sprites " + far.ToString("G3") + " (in numbers up to " + most.ToString("G3") + ")");
                far = 0; most = 0;
                for (int k = 4; k < Counts; k++)
                    for (int n = 0; n < A3; n++) { far = Math.Max(far, Math.Abs(carried[k][n] - carryNext[k][n])); most = Math.Max(most, Math.Abs(carried[k][n])); }
                text.Append(", where it was, in metres, " + far.ToString("G3") + " (up to " + most.ToString("G3") + "), fastest " + fastest.ToString("G6") + " against " + nextFastest.ToString("G6"));
                far = 0; most = 0;
                for (int n = 0; n < A3; n++) { far = Math.Max(far, Math.Abs(carried[Rate][n] - carryNext[Rate][n])); most = Math.Max(most, Math.Abs(carried[Rate][n])); }
                text.Append(", rounds a second " + far.ToString("G3") + " (up to " + most.ToString("G3") + ")");
                far = 0; most = 0;
                for (int n = 0; n < A3; n++) { far = Math.Max(far, Math.Abs(pulls[n] - strainNext[n])); most = Math.Max(most, Math.Abs(pulls[n])); }
                text.Append(", pulled out of shape " + far.ToString("G3") + " a second (up to " + most.ToString("G3") + ")");
                return text.ToString();
            }
            finally
            {
                onlyBy = 0;
                Air.Few.MaxDegreeOfParallelism = threads;
                Settings.Native = native && Settings.Native;
            }
        }
#endif

        IntPtr Hold(object array)
        {
            GCHandle handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            held.Add(handle);
            IntPtr at = handle.AddrOfPinnedObject();
            heldAt[array] = at;
            return at;
        }

        void NativeBegin()
        {
            nativeWork = Native.New();
            if (nativeWork == IntPtr.Zero) return;
            Hold(sunThrough); Hold(skyThrough); Hold(sunNext); Hold(skyNext);
            for (int c = 0; c < Carried; c++) { Hold(carryNext[c]); Hold(carryNow[c]); }
            Hold(cellsA); Hold(cellsB); Hold(cellsC); Hold(cellsD); Hold(cellsClear); Hold(floors); Hold(cellsE); Hold(strainNext); Hold(strainNow); Hold(BellList);
            for (int n = 0; n < cellsRest.Length; n++) restAt[n] = Hold(cellsRest[n]);
            Hold(carryAt); Hold(restAt);
        }

        void NativeEnd()
        {
            if (nativeWork != IntPtr.Zero) { Native.Free(nativeWork); nativeWork = IntPtr.Zero; }
            foreach (GCHandle handle in held) handle.Free();
            held.Clear();
            heldAt.Clear();
        }

        /// <summary>The second half of the making of a grid, by the library. False if it would not (which it says why, once): the C# then makes this grid and every one after.</summary>
        bool NativeGrid(int n, long began)
        {
            var g = new Native.Grid
            {
                size = Marshal.SizeOf(typeof(Native.Grid)), g = G, a = A, levels = Levels, carried = Carried,
                count = n, drawn = box != null ? 1 : 0, sunUp = sunUp ? 1 : 0, halves = Assets.SixteenAsHalves ? 1 : 0,
                x0 = nx0, y0 = ny0, z0 = nz0, dX = dX, dY = dY, dZ = dZ,
                keepX0 = keepX0, keepX1 = keepX1, keepY0 = keepY0, keepY1 = keepY1, keepZ0 = keepZ0, keepZ1 = keepZ1, fadeX = fadeX, fadeY = fadeY, fadeZ = fadeZ,
                leastReach = leastReach, floored = lightFloored ? 1 : 0, floors = heldAt[floors], thick = Settings.Thick, hold = grip < 0.55f ? 0.55f : grip, goesX = nextDrift.x, goesY = nextDrift.y, goesZ = nextDrift.z, ahead = Ahead,
                sunX = gridSun.x, sunY = gridSun.y, sunZ = gridSun.z, repeat = lightRepeat, skyX = gridUp.x, skyY = gridUp.y, skyZ = gridUp.z,
                thickest = Thickest, thickestFlame = ThickestFlame, something = Something, restFar = RestFar, mostBefore = MostBefore, mostAbove = MostAbove,
                sunThrough = heldAt[sunNext], skyThrough = heldAt[skyNext], carry = heldAt[carryAt],
                cellsA = heldAt[cellsA], cellsB = heldAt[cellsB], cellsC = heldAt[cellsC], cellsD = heldAt[cellsD], cellsRest = heldAt[restAt], cellsClear = heldAt[cellsClear], cellsE = heldAt[cellsE], strain = heldAt[strainNext], bells = heldAt[BellList],
            };
            for (int c = 0; c < Carried; c++) carryAt[c] = heldAt[carryNext[c]];
            int refused;
            GCHandle particles = GCHandle.Alloc(seen, GCHandleType.Pinned);
            try
            {
                g.particles = particles.AddrOfPinnedObject();
                refused = Native.Make(nativeWork, ref g);
            }
            finally { particles.Free(); }
            if (refused != 0)
            {
                if (!nativeComplained) Addon.Log("the library would not make a grid (" + refused + "); grids are made by the mod's own code from here on");
                nativeComplained = true;
                Settings.Native = false;
                return false;
            }
            nextFastest = g.fastest;
            nextReady = true;
            lightTook = System.Diagnostics.Stopwatch.GetTimestamp() - began;
            // (for the development build: what each part of that took)
            double ticks = System.Diagnostics.Stopwatch.Frequency * 1e-9;
            long together = g.ns2 + g.ns3 + g.ns4 + g.ns5, rest = (long)((g.ns1 + together + g.ns7) * ticks);
            Air.Stage[0] = lightTook - rest; Air.Stage[1] = (long)(g.ns1 * ticks); Air.Stage[2] = (long)(g.ns2 * ticks); Air.Stage[3] = (long)(g.ns3 * ticks); Air.Stage[4] = (long)(g.ns4 * ticks);
            Air.Stage[5] = (long)(g.ns5 * ticks); Air.Stage[6] = (long)(together * ticks); Air.Stage[7] = (long)(g.ns7 * ticks);
            return true;
        }

        float heldX, heldY, heldZ;                                // the cell sizes in use
        float detailRepeat = 9f, fullLength = 1.5f;               // metres before the detail repeats, and of the thickest smoke that count as "full": set by the first blast

        /// <summary>The smallest of the set sizes that is big enough; the size already in use is kept unless too small or much too big.</summary>
        static float Rung(float wanted, ref float held)
        {
            if (held <= 0f || wanted > held || wanted < held * 0.55f)
                held = (float)Math.Pow(1.26, Math.Ceiling(Math.Log(wanted) / Math.Log(1.26)));
            return held;
        }

        float keepX0, keepX1, keepY0, keepY1, keepZ0, keepZ1, fadeX, fadeY, fadeZ;      // the stretch the grid covers each way, and one over the width of the band at its ends

        /// <summary>
        /// The stretch of one direction the grid is to cover: all of the cloud, unless a few particles have
        /// strayed far from the rest of it (more than three and a bit times the cloud's own spread), which
        /// would stretch the grid until its cells were too coarse for the cloud itself. The mean and the
        /// spread change smoothly as particles move, so the stretch does too.
        ///
        /// In the first moments of a blast nothing has strayed: everything is flying outward together, and
        /// most of it low along the ground, so that what goes highest is far further from the middle than the
        /// spread would suggest. Held to three times the spread, the top of the dome of a hard crash was cut
        /// off flat for a few frames. So for the first half second and a bit the stretch is twice as generous
        /// (far), and it is back to the usual by a second and a bit: while everything is still changing too
        /// fast for the change of grid that brings to be noticed.
        /// </summary>
        static void Keep(double sum, double squares, int count, float typical, float far, ref float low, ref float high)
        {
            double mean = sum / count, spread = Math.Sqrt(Math.Max(0.0, squares / count - mean * mean));
            float reach = (float)(far * spread) + 1.5f * typical;
            low = Math.Max(low, (float)mean - reach);
            high = Math.Min(high, (float)mean + reach);
        }

        // Reading the grid back between its cells blurs what was put on it a little, by an amount that goes with
        // the size of the cells; so does bringing a coarser grid down to a finer one. Each ball is made that much
        // narrower beforehand (these are the amounts, in cells squared, for a ball put on the grid itself and on
        // each of the coarser ones), so that what comes out is as wide as the particle and no wider, whatever the cells.
        static readonly float[] Narrowing = { 1.5f, 1.5f * 5f, 1.5f * 21f, 1.5f * 85f };

        /// <summary>
        /// Put a share of the particles into the grid, each spread over a ball of its own size. A ball many
        /// cells wide is put on a coarser grid instead, where it is only a few cells wide: far less work, and
        /// something that smooth loses nothing by it. Several threads do this at once without taking turns;
        /// on the grid itself, now and then two add to the same cell in the same instant and one addition is
        /// lost, which is far too little to see; on the small coarse grids each thread keeps its own.
        /// </summary>
        void Deposit(int part)
        {
            P[] a = seen;
            int from = (int)((long)lightCount * part / lightParts), to = (int)((long)lightCount * (part + 1) / lightParts);
            float x0 = nx0, y0 = ny0, z0 = nz0, hx = dX, hy = dY, hz = dZ, least = leastReach;
            float widest = Math.Max(hx, Math.Max(hy, hz)), ihx = 1f / hx, ihz = 1f / hz;
            bool drawn = box != null, floored = lightFloored;
            float[] lieOf = floors, stand = stands[part];
            int[] rowsOf = rows[part];
            float[][][] mine = apart[part];
            for (int i = from; i < to; i++)
            {
                float r = a[i].r, heat = a[i].heat, life = a[i].life, age = a[i].age;
                if (r <= 0.01f || life < 0f) continue;
                float fade = life - age < life * 0.22f ? (life - age) / (life * 0.22f) : 1f;
                if (a[i].fadeIn > 0f && age < a[i].fadeIn) fade *= age / a[i].fadeIn;
                float soot = heat > 0.5f ? 0f : heat < 0.12f ? 1f : (0.5f - heat) * 2.6316f;
                float tau = a[i].mass / (r * r);
                if (tau > 8f) tau = 8f;
                float smoke = tau / (2f * r) * soot * fade * Settings.Thick;     // per metre, inside the particle
                float spread = a[i].born * 1.6f / r;                                 // flame that has spread out is thinner
                float flame = a[i].flame * (heat < 0.02f ? 0f : heat > 0.3f ? 1f : (heat - 0.02f) * 3.5714f) * (a[i].mass > 0f ? 1f : fade) * 0.3f / r * (spread < 1f ? spread * spread : 1f);
                if (!drawn) flame = 0f;
                if (smoke < 1e-4f && flame < 1e-4f) continue;
                float px = a[i].x, py = a[i].y, pz = a[i].z;
                if (drawn)
                {
                    // What has strayed beyond the stretch the grid covers is faded away over the band at its ends (see Lighting).
                    float u = (px - keepX0 < keepX1 - px ? px - keepX0 : keepX1 - px) * fadeX, v = (py - keepY0 < keepY1 - py ? py - keepY0 : keepY1 - py) * fadeY, w3 = (pz - keepZ0 < keepZ1 - pz ? pz - keepZ0 : keepZ1 - pz) * fadeZ;
                    float share = u < v ? (u < w3 ? u : w3) : (v < w3 ? v : w3);
                    if (share <= 0f) continue;
                    if (share < 1f) { smoke *= share; flame *= share; }
                }
                // The ball is densest in the middle and fades to nothing at its rim without an edge (an edge would show
                // differently according to where it fell among the cells).
                float reach = 1.474f * r;
                if (reach < least) reach = least;
                // Smoke rests on the ground; none of it is under it. A ball that would reach below the ground is stood
                // on it instead: its top stays where it was, its foot comes up to the ground, and it is as much wider
                // as keeps all of it there, the way a puff of smoke flattens and spreads where it meets the ground.
                // (It used to be put on the grid whole, and the part under the ground, which for smoke lying on it is
                // nearly half, was simply never seen: smoke looked as though it sank in.)
                float wide = reach, tall = reach, ground = 0f, follows = 0f;
                bool resting = false;
                if (floored)
                {
                    // (the ground under its middle: between the four columns of the grid round it, whose heights are for their own middles)
                    float gu = (px - x0) * ihx - 0.5f, gv = (pz - z0) * ihz - 0.5f;
                    if (!(gu > 0f)) gu = 0f; else if (gu > G - 1.001f) gu = G - 1.001f;
                    if (!(gv > 0f)) gv = 0f; else if (gv > G - 1.001f) gv = G - 1.001f;
                    int gi = (int)gu, gk = (int)gv, gAt = gi + gk * G;
                    float fu = gu - gi, fv = gv - gk;
                    float lower = lieOf[gAt] + (lieOf[gAt + 1] - lieOf[gAt]) * fu, upper = lieOf[gAt + G] + (lieOf[gAt + G + 1] - lieOf[gAt + G]) * fu;
                    ground = lower + (upper - lower) * fv;
                    float height = py - ground;
                    if (height < reach)
                    {
                        if (height < 0f) height = 0f;
                        tall = 0.5f * (height + reach);
                        wide = reach * Root(reach / tall);
                        py = ground + tall;
                        // (how far it takes the shape of the ground under it: not at all where it only just touches, wholly once it is half sunk)
                        follows = 2f * (reach - height) / reach;
                        if (follows > 1f) follows = 1f;
                        resting = true;
                    }
                }
                int level = 0;
                float span = 4.5f * widest;
                while (level < Levels - 1 && wide > span) { level++; span *= 2f; }
                float times = 1 << level, narrow = Narrowing[level];
                float lx = hx * times, ly = hy * times, lz = hz * times;
                float rx = wide * wide - narrow * hx * hx, ry = tall * tall - narrow * hy * hy, rz = wide * wide - narrow * hz * hz;
                float fx = 1.05f * lx, fy = 1.05f * ly, fz = 1.05f * lz;
                rx = rx > fx * fx ? Root(rx) : fx;
                ry = ry > fy * fy ? Root(ry) : fy;
                rz = rz > fz * fz ? Root(rz) : fz;
                // (The grid may not be able to make it as flat as it should be. Its foot stays on the ground all the same, and what
                // the grid makes of it below the ground goes unseen, so long as no more than half of it does. And a flame stands on
                // what burns, its densest part at the ground. Lifted clear instead, as until 2026-10-10, a puff stood at least a cell
                // of the grid off the ground: in a grid made for a tall column of smoke, whose cells are a metre or two high, the
                // fire on the ground and the smoke and dust coming off it floated above it. TestFoot 1, with the library off: so.)
                if (resting && Settings.TestFoot >= 1f) { if (py - ry < ground) py = ground + ry; }
                else if (resting && flame > 1e-4f) py = ground + 0.35f * ry;
                else if (resting && py - ry < ground) py = ground + (tall > 0.5f * ry ? tall : 0.5f * ry);
                int side = G >> level;
                float ix = 1f / lx, iy = 1f / ly, iz = 1f / lz;
                int i0 = (int)((px - rx - x0) * ix), i1 = (int)((px + rx - x0) * ix), j0 = (int)((py - ry - y0) * iy), j1 = (int)((py + ry - y0) * iy), k0 = (int)((pz - rz - z0) * iz), k1 = (int)((pz + rz - z0) * iz);
                if (i0 < 0) i0 = 0; if (j0 < 0) j0 = 0; if (k0 < 0) k0 = 0;
                if (i1 > side - 1) i1 = side - 1; if (j1 > side - 1) j1 = side - 1; if (k1 > side - 1) k1 = side - 1;
                if (resting)
                {
                    // The ground under it is not flat, nor the same height all the way across it: each column of it stands
                    // on the ground under that column, so that its foot lies along the ground like a blanket, down a
                    // slope and over an edge. (By no more than its own width, up or down: at the top of a cliff it hangs
                    // over, it does not pour to the bottom.) Worked out here once for every column it reaches: where the
                    // middle of the ball is in that column, and which cells of the column it can reach.
                    int lieAt = FloorsAt[level];
                    for (int k = k0; k <= k1; k++)
                    {
                        int lieRow = lieAt + k * side, standRow = k * side;
                        float lowest = 0f, highest = 0f;
                        for (int c = i0; c <= i1; c++)
                        {
                            float shift = lieOf[lieRow + c] - ground;
                            if (shift > wide) shift = wide; else if (shift < -wide) shift = -wide;
                            shift *= follows;
                            if (shift < lowest) lowest = shift; else if (shift > highest) highest = shift;
                            stand[standRow + c] = py + shift;
                        }
                        int ja = (int)((py + lowest - ry - y0) * iy), jb = (int)((py + highest + ry - y0) * iy);
                        if (ja < 0) ja = 0; if (jb > side - 1) jb = side - 1;
                        rowsOf[2 * k] = ja; rowsOf[2 * k + 1] = jb;
                    }
                }
                float qx = 1f / (rx * rx), qy = 1f / (ry * ry), qz = 1f / (rz * rz), total;
                if (rx >= 3.2f * lx && ry >= 3.2f * ly && rz >= 3.2f * lz)
                {
                    // Over this many cells the weights add up to what the smooth shape holds: no need to count them.
                    total = 0.95744f * rx * ry * rz * ix * iy * iz;
                }
                else if (!resting)
                {
                    total = 0f;
                    for (int k = k0; k <= k1; k++)
                    {
                        float dz = z0 + (k + 0.5f) * lz - pz, wz = 1f - dz * dz * qz;
                        if (wz <= 0f) continue;
                        for (int j = j0; j <= j1; j++)
                        {
                            float dy = y0 + (j + 0.5f) * ly - py, wy = wz - dy * dy * qy;
                            if (wy <= 0f) continue;
                            for (int c = i0; c <= i1; c++)
                            {
                                float dx = x0 + (c + 0.5f) * lx - px, w = wy - dx * dx * qx;
                                if (w > 0f) total += w * w;
                            }
                        }
                    }
                    if (total <= 0f) continue;
                }
                else
                {
                    total = 0f;
                    for (int k = k0; k <= k1; k++)
                    {
                        float dz = z0 + (k + 0.5f) * lz - pz, wz = 1f - dz * dz * qz;
                        if (wz <= 0f) continue;
                        int standRow = k * side, jb = rowsOf[2 * k + 1];
                        for (int j = rowsOf[2 * k]; j <= jb; j++)
                        {
                            float y = y0 + (j + 0.5f) * ly;
                            for (int c = i0; c <= i1; c++)
                            {
                                float dy = y - stand[standRow + c], dx = x0 + (c + 0.5f) * lx - px, w = wz - dy * dy * qy - dx * dx * qx;
                                if (w > 0f) total += w * w;
                            }
                        }
                    }
                    if (total <= 0f) continue;
                }
                // What the whole particle blocks, shared out so that the cells together hold exactly that.
                float worth = 4.19f * r * r * r * ix * iy * iz / total;
                float s = smoke * worth, f = flame * worth;
                float light = (a[i].ar + a[i].ag + a[i].ab) * 0.3333f;
                float warm = a[i].ar > 1e-3f ? (a[i].ar - a[i].ab) / a[i].ar * 2.5f : 0f;        // dust is brown or red; soot and steam are grey
                float sl = s * light, sd = s * (warm < 0f ? 0f : warm > 1f ? 1f : warm), fh = f * (heat > 1.2f ? 1.2f : heat);
                float[][] into = level == 0 ? field : mine[level];
                float[] smokeIn = into[0], paleIn = into[1], dustIn = into[2], flameIn = into[3], hotIn = into[4];
                if (level > 0) wideUsed[level] = true;
                if (!resting)
                {
                    for (int k = k0; k <= k1; k++)
                    {
                        float dz = z0 + (k + 0.5f) * lz - pz, wz = 1f - dz * dz * qz;
                        if (wz <= 0f) continue;
                        for (int j = j0; j <= j1; j++)
                        {
                            float dy = y0 + (j + 0.5f) * ly - py, wy = wz - dy * dy * qy;
                            if (wy <= 0f) continue;
                            int row = (j + k * side) * side;
                            for (int c = i0; c <= i1; c++)
                            {
                                float dx = x0 + (c + 0.5f) * lx - px, w = wy - dx * dx * qx;
                                if (w <= 0f) continue;
                                w *= w;
                                int cellAt = row + c;
                                if (s > 0f) { smokeIn[cellAt] += s * w; paleIn[cellAt] += sl * w; dustIn[cellAt] += sd * w; }
                                if (f > 0f) { flameIn[cellAt] += f * w; hotIn[cellAt] += fh * w; }
                            }
                        }
                    }
                    continue;
                }
                for (int k = k0; k <= k1; k++)
                {
                    float dz = z0 + (k + 0.5f) * lz - pz, wz = 1f - dz * dz * qz;
                    if (wz <= 0f) continue;
                    int standRow = k * side, jb = rowsOf[2 * k + 1];
                    for (int j = rowsOf[2 * k]; j <= jb; j++)
                    {
                        float y = y0 + (j + 0.5f) * ly;
                        int row = (j + k * side) * side;
                        for (int c = i0; c <= i1; c++)
                        {
                            float dy = y - stand[standRow + c], dx = x0 + (c + 0.5f) * lx - px, w = wz - dy * dy * qy - dx * dx * qx;
                            if (w <= 0f) continue;
                            w *= w;
                            int cellAt = row + c;
                            if (s > 0f) { smokeIn[cellAt] += s * w; paleIn[cellAt] += sl * w; dustIn[cellAt] += sd * w; }
                            if (f > 0f) { flameIn[cellAt] += f * w; hotIn[cellAt] += fh * w; }
                        }
                    }
                }
            }
        }

        /// <summary>Add what was put on the coarser grids to one of the fields, bringing it down a grid at a time.</summary>
        void Gather(int which)
        {
            float[] carried = null;
            for (int level = Levels - 1; level >= 1; level--)
            {
                if (!wideUsed[level] && carried == null) continue;
                int side = G >> level;
                float[] here = wide[level][which];
                Array.Clear(here, 0, here.Length);
                if (wideUsed[level])
                    for (int part = 0; part < lightParts; part++)
                    {
                        float[] one = apart[part][level][which];
                        for (int c = 0; c < here.Length; c++) here[c] += one[c];
                    }
                if (carried != null) Widen(carried, side / 2, here, spare[which][0], spare[which][1]);
                carried = here;
            }
            if (carried != null) Widen(carried, G / 2, field[which], spare[which][0], spare[which][1]);
        }

        /// <summary>Add a grid of 'side' cells each way to one of twice as many, each fine cell taking from the coarse ones round it by how near they are.</summary>
        static void Widen(float[] from, int side, float[] into, float[] first, float[] second)
        {
            int twice = side * 2;
            // along the first direction: side x side x side cells become twice x side x side
            for (int k = 0; k < side; k++)
                for (int j = 0; j < side; j++)
                {
                    int src = (j + k * side) * side, dst = (j + k * side) * twice;
                    for (int i = 0; i < side; i++)
                    {
                        float c = from[src + i], below = i > 0 ? from[src + i - 1] : c, above = i < side - 1 ? from[src + i + 1] : c;
                        first[dst + 2 * i] = 0.75f * c + 0.25f * below;
                        first[dst + 2 * i + 1] = 0.75f * c + 0.25f * above;
                    }
                }
            // along the second: twice x twice x side
            for (int k = 0; k < side; k++)
                for (int j = 0; j < side; j++)
                {
                    int src = (j + k * side) * twice, below = (j > 0 ? src - twice : src), above = (j < side - 1 ? src + twice : src), dst = (2 * j + k * twice) * twice;
                    for (int i = 0; i < twice; i++)
                    {
                        float c = first[src + i];
                        second[dst + i] = 0.75f * c + 0.25f * first[below + i];
                        second[dst + twice + i] = 0.75f * c + 0.25f * first[above + i];
                    }
                }
            // along the third, added to what is there
            int layer = twice * twice;
            for (int k = 0; k < side; k++)
            {
                int src = k * layer, below = k > 0 ? src - layer : src, above = k < side - 1 ? src + layer : src, dst = 2 * k * layer;
                for (int i = 0; i < layer; i++)
                {
                    float c = second[src + i];
                    into[dst + i] += 0.75f * c + 0.25f * second[below + i];
                    into[dst + layer + i] += 0.75f * c + 0.25f * second[above + i];
                }
            }
        }

        /// <summary>
        /// What the shader wants to know about the neighbourhood of each place, on a coarser grid: how much
        /// smoke there is round about, over a couple of metres (the billows are cut into the edge of the smoke
        /// by comparing what is at a place with that, so the body of it stays whole); what colour that smoke
        /// is; and whether there is anything near at all, so that empty air can be passed over in long strides.
        /// </summary>
        void Around()
        {
            Parallel.For(0, A, Air.Few, aroundLayer);
            Parallel.For(0, 3, Air.Few, aroundSmooth);
            float[] most = anything, smokes = roundSmoke, pales = roundPale, dusts = roundDust;
            for (int c = 0; c < A3; c++)
            {
                float smoke = smokes[c], about = smoke * (0.125f / Thickest);
                float own = smoke > 1e-7f ? pales[c] / smoke : 0f, dust = smoke > 1e-7f ? dusts[c] / smoke : 0f;
                cellsC[c] = new Color32(RootByte[about >= 1f ? 65535 : about <= 0f ? 0 : (int)(about * 65535f)], (byte)(most[c] > Something ? 255 : 0), RootByte[own >= 1f ? 65535 : own <= 0f ? 0 : (int)(own * 65535f)], (byte)((dust > 1f ? 1f : dust < 0f ? 0f : dust) * 255f));
            }
        }

        /// <summary>
        /// Two more things about the smoke at each place, on the coarser grid, both taken from the particles
        /// that make it up (each counting for as much as it shows there).
        ///
        /// Which way it is going, and how fast. A grid takes a few frames to make and is then in use for a
        /// few more; drawn as it stands, the smoke would hold still for those frames and then jump. So the
        /// shader moves it on meanwhile, each part of it at its own speed.
        ///
        /// And how far it is from where it "was", by each of the six reckonings (see Afresh): the shader reads
        /// the pattern of billows at that place and not at the smoke's own, which is what makes a billow
        /// travel with the smoke it is made of.
        ///
        /// Both are then smoothed a little, and carried out a few cells past the smoke itself, where the
        /// shader may still ask for them. (Only that far, and only from the nearest smoke: smoothed without
        /// limit, a thick cloud's figures swamped those of a thin puff that had strayed metres from it, and
        /// the puff was drawn with the cloud's billows smeared across it.)
        /// </summary>
        void Carry()
        {
            float[][] c = carryNext;
            for (int k = 0; k < Carried; k++) Array.Clear(c[k], 0, A3);
            float[] much = c[0], vx = c[1], vy = c[2], vz = c[3];
            // (the reckonings in order: the big billows' three, then the small ones' three)
            float[] r0x = c[4], r0y = c[5], r0z = c[6], r1x = c[7], r1y = c[8], r1z = c[9], r2x = c[10], r2y = c[11], r2z = c[12];
            float[] r3x = c[13], r3y = c[14], r3z = c[15], r4x = c[16], r4y = c[17], r4z = c[18], r5x = c[19], r5y = c[20], r5z = c[21];
            float[] n0 = c[Counts], n1 = c[Counts + 1], n2 = c[Counts + 2], n3 = c[Counts + 3], n4 = c[Counts + 4], n5 = c[Counts + 5], rates = c[Rate];
            P[] a = seen;
            int n = Math.Min(lightCount, a.Length);
            float x0 = nx0, y0 = ny0, z0 = nz0, hx = 2f * dX, hy = 2f * dY, hz = 2f * dZ, least = leastReach, hold = grip < 0.55f ? 0.55f : grip;
            float ix = 1f / hx, iy = 1f / hy, iz = 1f / hz;
            float goesX = nextDrift.x, goesY = nextDrift.y, goesZ = nextDrift.z;             // (the box goes along with this much of it: see nextDrift)
            for (int i = 0; i < n; i++)
            {
                float r = a[i].r, life = a[i].life, age = a[i].age;
                if (r <= 0.01f || life < 0f) continue;
                float fade = life - age < life * 0.22f ? (life - age) / (life * 0.22f) : 1f;
                if (a[i].fadeIn > 0f && age < a[i].fadeIn) fade *= age / a[i].fadeIn;
                // (roughly how much of it there is to see at a place inside it, smoke or flame)
                float shows = (a[i].mass / (r * r * r) + a[i].flame * a[i].heat / r) * fade;
                if (shows < 1e-6f) continue;
                float px = a[i].x, py = a[i].y, pz = a[i].z;
                // A blast's push is dying away all the while: what counts is what is left of it on the whole while this grid is in use.
                float dies = a[i].kloss * hold * 0.4f * Ahead, left = 1f / (1f + dies);
                float ux = a[i].vx + a[i].kx * left - goesX, uy = a[i].vy + a[i].ky * left - goesY, uz = a[i].vz + a[i].kz * left - goesZ;
                // How much each of its six reckonings counts for just now, by where it is in its round (see Afresh). Where it
                // "was" by a reckoning goes into the sums for as much as that reckoning counts with it: so what a cell ends
                // up with, for each reckoning, is where the smoke that the reckoning matters to "was" by it.
                float round = a[i].turn, rate = a[i].rate;
                if (!(round >= 0f && round < 1f)) round = 0f;
                if (!(rate >= 0f && rate < 8f)) rate = 0f;
                float b0 = BellAt(round), b1 = BellAt(round + Third), b2 = BellAt(round + 2f * Third), b3 = BellAt(2f * round), b4 = BellAt(2f * round + Third), b5 = BellAt(2f * round + 2f * Third);
                float f0x = (a[i].ax - px) * b0, f0y = (a[i].ay - py) * b0, f0z = (a[i].az - pz) * b0, f1x = (a[i].bx - px) * b1, f1y = (a[i].by - py) * b1, f1z = (a[i].bz - pz) * b1, f2x = (a[i].ex - px) * b2, f2y = (a[i].ey - py) * b2, f2z = (a[i].ez - pz) * b2;
                float f3x = (a[i].cx - px) * b3, f3y = (a[i].cy - py) * b3, f3z = (a[i].cz - pz) * b3, f4x = (a[i].dx - px) * b4, f4y = (a[i].dy - py) * b4, f4z = (a[i].dz - pz) * b4, f5x = (a[i].fx - px) * b5, f5y = (a[i].fy - py) * b5, f5z = (a[i].fz - pz) * b5;
                float reach = 1.474f * r;
                if (reach < least) reach = least;
                // It counts for as much as the ball it fills holds (a ball of at least a cell or so across and at most a few)...
                float rx = reach < 1.2f * hx ? 1.2f * hx : reach > 2.5f * hx ? 2.5f * hx : reach, ry = reach < 1.2f * hy ? 1.2f * hy : reach > 2.5f * hy ? 2.5f * hy : reach, rz = reach < 1.2f * hz ? 1.2f * hz : reach > 2.5f * hz ? 2.5f * hz : reach;
                shows *= 0.95744f * rx * ry * rz * ix * iy * iz;
                // ...but goes into the eight cells round it only, by nearness: the smoothing that follows spreads everything a
                // cell or so further anyway, these being smooth things, and spreading each particle over a ball of cells
                // first was a third of all the work of making a grid.
                float u = (px - x0) * ix - 0.5f, v = (py - y0) * iy - 0.5f, w3 = (pz - z0) * iz - 0.5f;
                if (u <= -1f || v <= -1f || w3 <= -1f || u >= A || v >= A || w3 >= A) continue;
                int ci = (int)u, cj = (int)v, ck = (int)w3;
                if (u < ci) ci--; if (v < cj) cj--; if (w3 < ck) ck--;                 // (rounded down, below nothing too)
                float fu = u - ci, fv = v - cj, fw = w3 - ck;
                for (int corner = 0; corner < 8; corner++)
                {
                    int di = corner & 1, dj = (corner >> 1) & 1, dk = corner >> 2, ii = ci + di, jj = cj + dj, kk = ck + dk;
                    if (ii < 0 || jj < 0 || kk < 0 || ii >= A || jj >= A || kk >= A) continue;
                    float w = (di == 0 ? 1f - fu : fu) * (dj == 0 ? 1f - fv : fv) * (dk == 0 ? 1f - fw : fw) * shows;
                    if (w <= 0f) continue;
                    int cell = ii + (jj + kk * A) * A;
                    much[cell] += w;
                    vx[cell] += w * ux; vy[cell] += w * uy; vz[cell] += w * uz;
                    r0x[cell] += w * f0x; r0y[cell] += w * f0y; r0z[cell] += w * f0z;
                    r1x[cell] += w * f1x; r1y[cell] += w * f1y; r1z[cell] += w * f1z;
                    r2x[cell] += w * f2x; r2y[cell] += w * f2y; r2z[cell] += w * f2z;
                    r3x[cell] += w * f3x; r3y[cell] += w * f3y; r3z[cell] += w * f3z;
                    r4x[cell] += w * f4x; r4y[cell] += w * f4y; r4z[cell] += w * f4z;
                    r5x[cell] += w * f5x; r5y[cell] += w * f5y; r5z[cell] += w * f5z;
                    n0[cell] += w * b0; n1[cell] += w * b1; n2[cell] += w * b2; n3[cell] += w * b3; n4[cell] += w * b4; n5[cell] += w * b5;
                    rates[cell] += w * rate;
                }
            }
            Parallel.For(0, Carried, Air.Few, carrySmooth);
            // What is left is sums; each cell's share of them is the smoke's own figure there.
            byte[] known = carryKnown;
            for (int cell = 0; cell < A3; cell++)
            {
                if (much[cell] > 1e-30f)
                {
                    float per = 1f / much[cell];
                    for (int k = 1; k < 4; k++) c[k][cell] *= per;
                    // (where it "was" by each reckoning: the share of the smoke that reckoning counts with; none to speak of, nothing)
                    for (int which = 0; which < Reckonings; which++)
                    {
                        float counts = c[Counts + which][cell], each = counts > 1e-6f * much[cell] ? 1f / counts : 0f;
                        c[4 + 3 * which][cell] *= each; c[5 + 3 * which][cell] *= each; c[6 + 3 * which][cell] *= each;
                        c[Counts + which][cell] = counts * per;
                    }
                    rates[cell] *= per;
                    known[cell] = 1;
                }
                else
                {
                    for (int k = 1; k < Carried; k++) c[k][cell] = 0f;
                    known[cell] = 0;
                }
            }
            // Outward from there, a cell at a time: each empty cell takes the mean of its neighbours that have something.
            float[] sums = carrySums;
            for (int round = 0; round < 5; round++)
            {
                byte mark = (byte)(round + 2);                  // (what is filled in this round only counts from the next)
                for (int k = 0; k < A; k++)
                    for (int j = 0; j < A; j++)
                        for (int i = 0; i < A; i++)
                        {
                            int cell = i + (j + k * A) * A;
                            if (known[cell] != 0) continue;
                            int near = 0;
                            for (int side = 0; side < 6; side++)
                            {
                                int other;
                                if (side == 0) { if (i == 0) continue; other = cell - 1; }
                                else if (side == 1) { if (i == A - 1) continue; other = cell + 1; }
                                else if (side == 2) { if (j == 0) continue; other = cell - A; }
                                else if (side == 3) { if (j == A - 1) continue; other = cell + A; }
                                else if (side == 4) { if (k == 0) continue; other = cell - A * A; }
                                else { if (k == A - 1) continue; other = cell + A * A; }
                                if (known[other] == 0 || known[other] == mark) continue;
                                if (near++ == 0) for (int f = 1; f < Carried; f++) sums[f] = c[f][other];
                                else for (int f = 1; f < Carried; f++) sums[f] += c[f][other];
                            }
                            if (near == 0) continue;
                            float share = 1f / near;
                            for (int f = 1; f < Carried; f++) c[f][cell] = sums[f] * share;
                            known[cell] = mark;
                        }
            }
            float fastest = 2f;
            for (int cell = 0; cell < A3; cell++)
            {
                float ux = vx[cell], uy = vy[cell], uz = vz[cell];
                if (ux < 0f) ux = -ux; if (uy < 0f) uy = -uy; if (uz < 0f) uz = -uz;
                if (ux > fastest) fastest = ux; if (uy > fastest) fastest = uy; if (uz > fastest) fastest = uz;
            }
            nextFastest = fastest;
            float scale = 127.5f / fastest;
            float[] pulled = strainNext;
            for (int k = 0; k < A; k++)
                for (int j = 0; j < A; j++)
                    for (int i = 0; i < A; i++)
                    {
                        int cell = i + (j + k * A) * A;
                        // Where two bodies of smoke from different places meet, "where it was" changes by many metres within a
                        // cell or two, and the pattern read there is squeezed to a fraction of its size: finer than can be drawn,
                        // it would show as speckle. How much it is squeezed goes to the shader, which reads it that much more coarsely.
                        if (known[cell] == 0) { cellsD[cell] = new Color32(127, 127, 127, 0); cellsE[cell] = new Color32(128, 128, 128, 128); pulled[cell] = 0f; continue; }      // (nothing here, nor anywhere near: all of it is nought)
                        int before = i > 0 ? cell - 1 : cell, after = i < A - 1 ? cell + 1 : cell, below = j > 0 ? cell - A : cell, above = j < A - 1 ? cell + A : cell, south = k > 0 ? cell - A * A : cell, north = k < A - 1 ? cell + A * A : cell;
                        float perX = 1f / ((after - before) * hx), perY = A / ((above - below) * hy), perZ = (float)(A * A) / ((north - south) * hz), most = 1f;
                        for (int f = 4; f < Counts; f += 3)
                        {
                            float[] east = c[f], up = c[f + 1], nor = c[f + 2];
                            if (after != before)
                            {
                                float gx = (east[after] - east[before]) * perX + 1f, gy = (up[after] - up[before]) * perX, gz = (nor[after] - nor[before]) * perX, g = gx * gx + gy * gy + gz * gz;
                                if (g > most) most = g;
                            }
                            if (above != below)
                            {
                                float gx = (east[above] - east[below]) * perY, gy = (up[above] - up[below]) * perY + 1f, gz = (nor[above] - nor[below]) * perY, g = gx * gx + gy * gy + gz * gz;
                                if (g > most) most = g;
                            }
                            if (north != south)
                            {
                                float gx = (east[north] - east[south]) * perZ, gy = (up[north] - up[south]) * perZ, gz = (nor[north] - nor[south]) * perZ + 1f, g = gx * gx + gy * gy + gz * gz;
                                if (g > most) most = g;
                            }
                        }
                        // How fast the air here is pulling the smoke out of shape: how differently the smoke of the cells to
                        // either side is moving, each way, with turning round as a whole left out (smoke that only turns
                        // keeps its shape). Per second: it is what sets the pace of the billows' turns here (see Afresh).
                        float xx = 0f, xy = 0f, xz = 0f, yx = 0f, yy = 0f, yz = 0f, zx = 0f, zy = 0f, zz = 0f;
                        if (after != before) { xx = (vx[after] - vx[before]) * perX; yx = (vy[after] - vy[before]) * perX; zx = (vz[after] - vz[before]) * perX; }
                        if (above != below) { xy = (vx[above] - vx[below]) * perY; yy = (vy[above] - vy[below]) * perY; zy = (vz[above] - vz[below]) * perY; }
                        if (north != south) { xz = (vx[north] - vx[south]) * perZ; yz = (vy[north] - vy[south]) * perZ; zz = (vz[north] - vz[south]) * perZ; }
                        float sxy = xy + yx, sxz = xz + zx, syz = yz + zy;
                        float pull = Root(xx * xx + yy * yy + zz * zz + 0.5f * (sxy * sxy + sxz * sxz + syz * syz));
                        pulled[cell] = pull < 1e6f ? pull : 0f;                       // (and nothing that is no number at all)
                        // And whose turn it is here, for the shader. How much the three reckonings of a set count are three points
                        // of one wave, a third of a round apart (see BellAt): so what the shader is given is the wave, as the point
                        // on a circle that those three are the heights of (at its rim where all the smoke here is at the same point
                        // of its round, nearer its middle the less it agrees). It can turn that on by as far as the smoke has gone
                        // round since the grid was made, and so have what each reckoning counts for at every frame, not only at
                        // every grid. (For the big billows' three, and for the small ones'.)
                        float s0 = c[Counts][cell], s1 = c[Counts + 1][cell], s2 = c[Counts + 2][cell], s3 = c[Counts + 3][cell], s4 = c[Counts + 4][cell], s5 = c[Counts + 5][cell];
                        float re = (0.5f * (s1 + s2) - s0) * 170f + 128f, im = (s1 - s2) * 147.224f + 128f, re2 = (0.5f * (s4 + s5) - s3) * 170f + 128f, im2 = (s4 - s5) * 147.224f + 128f;
                        cellsE[cell] = new Color32((byte)(re < 0f ? 0f : re > 255f ? 255f : re), (byte)(im < 0f ? 0f : im > 255f ? 255f : im), (byte)(re2 < 0f ? 0f : re2 > 255f ? 255f : re2), (byte)(im2 < 0f ? 0f : im2 > 255f ? 255f : im2));
                        float squeezed = most > 1.0001f ? 0.72135f * (float)Math.Log(most) * 63.75f : 0f;         // (half its logarithm to base two: four times over, or more, is 255)
                        cellsD[cell] = new Color32((byte)(127.5f + vx[cell] * scale + 0.5f), (byte)(127.5f + vy[cell] * scale + 0.5f), (byte)(127.5f + vz[cell] * scale + 0.5f), (byte)(squeezed > 255f ? 255f : squeezed));
                    }
            // Eighteen numbers a cell, sixteen bits each, four to a texture (the last holds two).
            bool halves = Assets.SixteenAsHalves;
            for (int which = 0; which < cellsRest.Length; which++)
            {
                ushort[] into = cellsRest[which];
                for (int channel = 0; channel < 4; channel++)
                {
                    int number = which * 4 + channel;
                    if (number > 3 * Reckonings) break;
                    float[] from = number < 3 * Reckonings ? c[4 + number] : c[Rate];        // (after the eighteen: how fast the smoke is going through its round)
                    if (halves) for (int cell = 0; cell < A3; cell++) into[cell * 4 + channel] = Mathf.FloatToHalf(from[cell]);
                    else for (int cell = 0; cell < A3; cell++) into[cell * 4 + channel] = (ushort)Far(from[cell]);
                }
            }
        }

        readonly byte[] carryKnown = new byte[A3];
        readonly float[] carrySums = new float[Carried];

        /// <summary>A distance of up to RestFar either way, as a number of sixteen bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Far(float metres)
        {
            float share = metres * (0.5f / RestFar) + 0.5f;
            return share <= 0f ? 0 : share >= 1f ? 65535 : (int)(share * 65535f + 0.5f);
        }

        /// <summary>Smooth one of those grids over a cell or so each way, and no further: along each direction twice, each cell taking a quarter from either neighbour.</summary>
        void CarrySmooth(int which)
        {
            // (Row by row in the order the cells lie in memory, from one copy of the grid into another and back, instead
            // of line by line along each direction: the same sums, in a fraction of the time for the two directions whose
            // lines are scattered through memory.)
            float[] a = carryNext[which], b = carryTemp[which];
            for (int axis = 0; axis < 3; axis++)
                for (int round = 0; round < 2; round++)
                {
                    for (int k = 0; k < A; k++)
                        for (int j = 0; j < A; j++)
                        {
                            int row = (j + k * A) * A;
                            if (axis == 0)
                            {
                                b[row] = 0.5f * a[row] + 0.25f * (a[row] + a[row + 1]);
                                for (int cell = row + 1; cell < row + A - 1; cell++) b[cell] = 0.5f * a[cell] + 0.25f * (a[cell - 1] + a[cell + 1]);
                                b[row + A - 1] = 0.5f * a[row + A - 1] + 0.25f * (a[row + A - 2] + a[row + A - 1]);
                            }
                            else
                            {
                                // (the rows to either side along this direction; at the ends, this row itself)
                                int low = axis == 1 ? (j > 0 ? -A : 0) : (k > 0 ? -A * A : 0), high = axis == 1 ? (j < A - 1 ? A : 0) : (k < A - 1 ? A * A : 0);
                                for (int cell = row; cell < row + A; cell++) b[cell] = 0.5f * a[cell] + 0.25f * (a[cell + low] + a[cell + high]);
                            }
                        }
                    float[] swap = a; a = b; b = swap;
                }
            // (six times over: the result is back in the grid it came from)
        }

        /// <summary>
        /// Where smoke "was". The billows the shader cuts into the smoke are a pattern, and a pattern that
        /// stood still in the air while the smoke moved through it made the whole cloud seethe. So every
        /// particle carries a place with it, the place whose billows it shows, and goes on showing the
        /// same ones wherever it is taken: the billows rise with the smoke, turn in its eddies and spread
        /// as it spreads. New smoke takes its place over from the smoke it is born into (see RestAt), so a
        /// column pouring from a fire carries one unbroken pattern up with it.
        ///
        /// Carried for ever, the pattern would be drawn out past all recognition. So there are three such
        /// reckonings, and by turns each begins afresh: the particle's place becomes the place it is at.
        /// Each counts for nothing at the moment it begins afresh, for most half-way through its life and
        /// for nothing again at its end, rising and falling as a bell curve does (see BellAt); and the three
        /// begin a third of a round apart. With three such curves so spaced, the billows are always as deep
        /// as one set, and what is seen changes at the same even rate all through a round. And there are two
        /// such sets of three: one for the big billows, and one going round twice as fast for the small
        /// ones, as in real smoke, where a small eddy is gone long before a big one.
        ///
        /// How long a round lasts is each bit of smoke's own affair, and it is what makes smoke look like
        /// what it is doing. Billows change because the air pulls them out of shape; where the air is hardly
        /// moving the smoke, they hardly change. So each particle is on one of seven rounds, from just over
        /// half a second to just over half a minute, each twice as long as the one before: the longest in
        /// which the air where it is does not pull the smoke further out of shape than it can bear (see
        /// Bearable; how fast that is happening is worked out with every grid, from how differently the
        /// smoke of neighbouring cells is moving: see Carry). Smoke flung out by a blast, or pouring up from
        /// a fire through slower smoke, goes round in a second or less and never shows a billow long enough
        /// for it to be drawn out into a streak; a cloud drifting on the wind takes ten seconds or twenty; a
        /// wisp left hanging in still air, half a minute and more, which is to say it keeps its shape.
        /// (All the smoke of a site used to go round together, 2.8 seconds a round: calm smoke churned as
        /// fast as the fire, and a few seconds after a blast the billows of the fastest smoke were drawn out
        /// into streaks.)
        ///
        /// All the smoke on one round keeps time (see Turn). Where the grid has smoke on different rounds in
        /// one cell it has, for each reckoning, how much it counts there on the whole, and where the smoke
        /// "was" by it according to the particles it counts for most with: the shader goes by those.
        ///
        /// And where it "was" is reckoned in the air the smoke is in, not over the ground: every such place
        /// is moved along, all the time, with the smoke of the site as a whole (see airGoing), and the
        /// shader takes as much off again. For smoke that keeps the same place that changes nothing. But it
        /// keeps the places near the smoke they belong to however long a round lasts and however far the
        /// wind carries the cloud meanwhile, which the grid needs (see RestFar).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Afresh(ref P q, int sixth)
        {
            // (a round in sixths: at each, the sets whose turn it is. The big billows' three, a, b and e, begin afresh at
            // the end of the round, two thirds through and a third through; the small ones' three, c, d and f, twice each.)
            switch (sixth)
            {
                case 1: q.fx = q.x; q.fy = q.y; q.fz = q.z; break;
                case 2: q.ex = q.x; q.ey = q.y; q.ez = q.z; q.dx = q.x; q.dy = q.y; q.dz = q.z; break;
                case 3: q.cx = q.x; q.cy = q.y; q.cz = q.z; break;
                case 4: q.bx = q.x; q.by = q.y; q.bz = q.z; q.fx = q.x; q.fy = q.y; q.fz = q.z; break;
                case 5: q.dx = q.x; q.dy = q.y; q.dz = q.z; break;
                default: q.ax = q.x; q.ay = q.y; q.az = q.z; q.cx = q.x; q.cy = q.y; q.cz = q.z; break;
            }
        }

        /// <summary>
        /// One particle's billows take their turns for a frame: on by a part of its round, and any set whose turn it
        /// is to begin afresh does.
        ///
        /// It keeps time with all the other smoke on the same round. (Left each to itself, with a round as long as
        /// the air where it had been made it, no two bits of smoke were at the same point of their rounds for long;
        /// and where smoke at every point of its round is mixed, what the grid makes of where it "was" is where it
        /// is, less a fixed distance: a pattern standing still in the air, which is the seething this was all made to
        /// be rid of.) So each of the six lengths of round has its beat, counted from the site's own clock, and a
        /// particle on a round goes at its beat: a little faster or slower for a while, if it is ahead of it or behind,
        /// as smoke new to a round is. The beats are in step with one another, each beginning with every other
        /// beginning of the one twice as quick: so smoke goes over to a slower round just as both begin, without
        /// anything about it changing at that moment, and all the smoke that goes over together is at once in time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Turn(ref P q, float tm, float dt, int wrapped, bool oneRound)
        {
            int k = (int)q.octave;
            if (k < wrapped && q.pace < Lenient * PerRound[k])
            {
                do k++; while (k < wrapped && q.pace < Lenient * PerRound[k]);
                q.octave = k;
            }
            float per = oneRound ? 1f / UsualRound : PerRound[k];
            float beat = tm * per;
            beat -= (int)beat;
            float behind = beat - q.turn;
            behind -= behind > 0.5f ? 1f : behind < -0.5f ? -1f : 0f;
            float catches = 1f + 3f * behind;
            catches = catches < 0.25f ? 0.25f : catches > 2.5f ? 2.5f : catches;
            q.rate = per * catches;
            float was = q.turn, on = was + dt * q.rate;
            int sixth = (int)(on * 6f);
            if (sixth != (int)(was * 6f)) Afresh(ref q, sixth);
            q.turn = on >= 1f ? on - 1f : on;
        }

        /// <summary>For testing, with the particles held still: their billows alone take their turns (what the mover does for each as it moves it).</summary>
        void TurnsOnly(float dt)
        {
            bool oneRound = Settings.TestTurns >= 1f;
            for (int i = 0; i < count; i++) Turn(ref p[i], time, dt, turnsWrapped, oneRound);
        }

        const float Third = 1f / 3f;
        const int BellSteps = 1024;

        /// <summary>
        /// How much a reckoning counts for, by how far through its life it is: nothing as it begins, 1
        /// half-way, nothing at its end, and level at each of those. Three of these a third of a life
        /// apart always add up to one and a half, their squares to one and an eighth, and the squares of
        /// their rates of change to the same figure throughout. (As a list, for the library to read the
        /// very same numbers from.)
        /// </summary>
        static readonly float[] BellList = Bells();

        static float[] Bells()
        {
            var list = new float[BellSteps];
            for (int n = 0; n < BellSteps; n++) list[n] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * (n + 0.5) / BellSteps));
            return list;
        }

        /// <summary>(For a point of the round that is not less than nought.)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float BellAt(float round) => BellList[(int)(round * BellSteps) & (BellSteps - 1)];

        readonly Vector3[] restThere = new Vector3[Reckonings];
        float restTurn, restPace;                                 // (and the point of its round, and its pace, that smoke born there takes over,
        int restOctave;                                           // and which round it is on)

        /// <summary>
        /// How far the smoke at a place is from where it "was", by each reckoning, going by the grid in use:
        /// what smoke born there takes over (left in restThere). Nothing where the grid has nothing to say,
        /// or for a reckoning that has begun afresh since the grid was made.
        /// </summary>
        void RestAt(float x, float y, float z)
        {
            Vector3[] there = restThere;
            for (int n = 0; n < Reckonings; n++) there[n] = Vector3.zero;
            // (where there is nothing to go by: all the smoke of a blast at the same point of its round, going round
            // briskly; the air sets its pace for it within a second)
            bool oneRound = Settings.TestTurns >= 1f;
            restPace = time - lastBlast < 1.5f && !oneRound ? 1f / QuickestRound : 0.4f;
            restOctave = OctaveFor(restPace);
            restTurn = time * (oneRound ? 1f / UsualRound : PerRound[restOctave]);
            restTurn -= Mathf.Floor(restTurn);
            if (!gridReady || box == null) return;
            if (turned) { Vector3 g = ToGrid(new Vector3(x, y, z)); x = g.x; y = g.y; z = g.z; }
            x -= slid.x; y -= slid.y; z -= slid.z;                 // (the grid has gone along with its box since it was made)
            float u = (x - gx0) * gix * 0.5f - 0.5f, v = (y - gy0) * giy * 0.5f - 0.5f, w = (z - gz0) * giz * 0.5f - 0.5f;
            if (u < -0.5f || v < -0.5f || w < -0.5f || u > A - 0.5f || v > A - 0.5f || w > A - 0.5f) return;
            u = u < 0f ? 0f : u > A - 1.001f ? A - 1.001f : u; v = v < 0f ? 0f : v > A - 1.001f ? A - 1.001f : v; w = w < 0f ? 0f : w > A - 1.001f ? A - 1.001f : w;
            int i = (int)u, j = (int)v, k = (int)w;
            float fu = u - i, fv = v - j, fw = w - k;
            float[][] c = carryNow;
            float count0 = 0f, count1 = 0f, count2 = 0f, pull = 0f, any = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                int di = corner & 1, dj = (corner >> 1) & 1, dk = corner >> 2, cell = i + di + ((j + dj) + (k + dk) * A) * A;
                float share = (di == 0 ? 1f - fu : fu) * (dj == 0 ? 1f - fv : fv) * (dk == 0 ? 1f - fw : fw);
                for (int n = 0; n < Reckonings; n++)
                {
                    there[n].x += share * c[4 + 3 * n][cell]; there[n].y += share * c[5 + 3 * n][cell]; there[n].z += share * c[6 + 3 * n][cell];
                }
                count0 += share * c[Counts][cell]; count1 += share * c[Counts + 1][cell]; count2 += share * c[Counts + 2][cell];
                pull += share * strainNow[cell];
                any += share * c[0][cell];
            }
            // (the grid has them in its own axes; the smoke keeps them in the site's)
            if (turned) for (int n = 0; n < Reckonings; n++) there[n] = FromGrid(there[n]);
            if (any <= 0f) return;
            // The point of the round the smoke here is at, on the whole, from how much each of the big billows' three
            // reckonings counts here (three points of one wave, a third of a round apart: its phase).
            float across = count0 - 0.5f * (count1 + count2), up = 0.8660254f * (count1 - count2);
            if (across * across + up * up > 0.01f)
            {
                float round = -Mathf.Atan2(-up, -across) * 0.15915494f;
                restTurn = round - Mathf.Floor(round);
            }
            if (oneRound) return;
            restPace = Mathf.Clamp(pull / Bearable, Slowest, 1f / QuickestRound);
            restOctave = OctaveFor(restPace);
        }

        /// <summary>The round nearest to what the air would have: the one smoke starts on.</summary>
        static int OctaveFor(float pace)
        {
            int k = 0;
            while (k < Octaves - 1 && pace < 0.5f * PerRound[k]) k++;
            return k;
        }

        /// <summary>One layer of the coarser grid: each of its cells from the eight cells of the fine grid inside it.</summary>
        void AroundLayer(int k)
        {
            float[] smokeIn = thick, paleIn = pale, dustIn = dusty, flameIn = flaming, most = anything, smokes = roundSmoke, pales = roundPale, dusts = roundDust;
            const float flameCounts = Thickest / ThickestFlame;
            for (int j = 0; j < A; j++)
            {
                int row = (j + k * A) * A, under = (2 * j + 2 * k * G) * G;
                for (int i = 0; i < A; i++)
                {
                    int at = under + 2 * i;
                    float top = 0f, smoke = 0f, light = 0f, dust = 0f;
                    for (int c = 0; c < 8; c++)
                    {
                        int cell = at + (c & 1) + ((c >> 1) & 1) * G + (c >> 2) * G2;
                        float v = smokeIn[cell] + flameCounts * flameIn[cell];
                        if (v > top) top = v;
                        smoke += smokeIn[cell]; light += paleIn[cell]; dust += dustIn[cell];
                    }
                    most[row + i] = top;
                    smokes[row + i] = smoke; pales[row + i] = light; dusts[row + i] = dust;
                }
            }
        }

        /// <summary>
        /// Smooth one of the grids on the coarser grid (0: how much smoke, 1: how light, 2: how dusty, all as
        /// sums over the eight cells inside each) over a couple of metres, whatever the size of the cells; the
        /// first also spreads "there is something here" to the cells round about. (How much smoke there is
        /// about, as an amount, is an eighth of the first.)
        /// </summary>
        void AroundSmooth(int which)
        {
            float[] a = which == 0 ? roundSmoke : which == 1 ? roundPale : roundDust;
            float width = 0.25f * lightRepeat, bend = 0.06f * lightRepeat;
            for (int axis = 0; axis < 3; axis++)
            {
                float cell = 2f * (axis == 0 ? dX : axis == 1 ? dY : dZ);
                float v = 0.5f * Math.Max(0.05f, width * width / (cell * cell) - 0.25f);
                Smooth(a, axis, (v + 1f - (float)Math.Sqrt(2f * v + 1f)) / v);
                if (which != 0) continue;
                // Anything within a cell, and within as far as the swirls bend the smoke, counts as near.
                int spread = 1 + (int)Math.Ceiling(bend / cell);
                if (spread > 6) spread = 6;
                // (All that is asked of it afterwards is whether it is more than next to nothing: so along each line it is
                // enough to count the cells since the last one that was, coming from either end. Each cell used to take the
                // most of all the cells within reach of it, which was most of the work of this whole part.)
                float[] most = anything, line = aroundLine;
                int stride = axis == 0 ? 1 : axis == 1 ? A : A * A, other1 = axis == 0 ? A : 1, other2 = axis == 2 ? A : A * A;
                for (int j = 0; j < A; j++)
                    for (int i = 0; i < A; i++)
                    {
                        int start = i * other1 + j * other2, since = A;
                        for (int k = 0; k < A; k++)
                        {
                            float here = most[start + k * stride];
                            since = here > Something ? 0 : since + 1;
                            line[k] = here > Something || since <= spread ? 1f : 0f;
                        }
                        since = A;
                        for (int k = A - 1; k >= 0; k--)
                        {
                            int at = start + k * stride;
                            since = most[at] > Something ? 0 : since + 1;
                            most[at] = line[k] > 0f || since <= spread ? 1f : 0f;
                        }
                    }
            }
        }

        readonly float[] aroundLine = new float[A];
        const float Something = 0.0015f;                          // as much smoke or flame, per metre, as counts as there being any

        // The square root of a share (0 to 1, in 65536 steps) as a byte. (Worked out afresh for every cell of every grid, the square roots alone took a tenth of the time.)
        static readonly byte[] RootByte = Roots();
        static byte[] Roots()
        {
            var roots = new byte[65536];
            for (int n = 0; n < roots.Length; n++) roots[n] = (byte)((float)Math.Sqrt(n / 65535f) * 255f);
            return roots;
        }

        /// <summary>Smooth one of the coarse grids along one direction: each row run through once each way, twice over.</summary>
        static void Smooth(float[] a, int axis, float keep)
        {
            // (Row by row in the order the cells lie in memory, each row from the one before it along the direction,
            // instead of line by line: the same sums, sooner done.)
            float lose = 1f - keep;
            for (int round = 0; round < 2; round++)
            {
                if (axis == 0)
                {
                    // (four rows at a time: each row's sum has to wait for its own last cell, but not for the other rows')
                    for (int row = 0; row < A3; row += 4 * A)
                    {
                        float r0 = 0f, r1 = 0f, r2 = 0f, r3 = 0f;
                        for (int c0 = row; c0 < row + A; c0++)
                        {
                            int c1 = c0 + A, c2 = c1 + A, c3 = c2 + A;
                            r0 = a[c0] + (r0 - a[c0]) * keep; a[c0] = r0;
                            r1 = a[c1] + (r1 - a[c1]) * keep; a[c1] = r1;
                            r2 = a[c2] + (r2 - a[c2]) * keep; a[c2] = r2;
                            r3 = a[c3] + (r3 - a[c3]) * keep; a[c3] = r3;
                        }
                        r0 = 0f; r1 = 0f; r2 = 0f; r3 = 0f;
                        for (int c0 = row + A - 1; c0 >= row; c0--)
                        {
                            int c1 = c0 + A, c2 = c1 + A, c3 = c2 + A;
                            r0 = a[c0] + (r0 - a[c0]) * keep; a[c0] = r0;
                            r1 = a[c1] + (r1 - a[c1]) * keep; a[c1] = r1;
                            r2 = a[c2] + (r2 - a[c2]) * keep; a[c2] = r2;
                            r3 = a[c3] + (r3 - a[c3]) * keep; a[c3] = r3;
                        }
                    }
                }
                else
                {
                    int step = axis == 1 ? A : A * A;
                    for (int k = 0; k < A; k++)
                        for (int j = 0; j < A; j++)
                        {
                            int row = (j + k * A) * A;
                            if ((axis == 1 ? j : k) == 0) for (int cell = row; cell < row + A; cell++) a[cell] *= lose;
                            else for (int cell = row; cell < row + A; cell++) a[cell] += (a[cell - step] - a[cell]) * keep;
                        }
                    for (int k = A - 1; k >= 0; k--)
                        for (int j = A - 1; j >= 0; j--)
                        {
                            int row = (j + k * A) * A;
                            if ((axis == 1 ? j : k) == A - 1) for (int cell = row; cell < row + A; cell++) a[cell] *= lose;
                            else for (int cell = row; cell < row + A; cell++) a[cell] += (a[cell + step] - a[cell]) * keep;
                        }
                }
                // What has dwindled to next to nothing is made nothing (far less than can be told apart in what is kept of
                // these): each pass makes what little there is in empty air smaller again, and numbers too small for the
                // processor's ordinary arithmetic are many times slower to work with.
                for (int cell = 0; cell < A3; cell++) if (a[cell] < 1e-9f) a[cell] = 0f;
            }
        }

        /// <summary>
        /// How much smoke lies between each cell and a light, working inward a layer at a time from the side
        /// the light comes in: for a cell, what lay before the place one layer nearer the light that its ray
        /// came through, and half of the cell itself. It is this that is kept, and the shader that works out
        /// how much light gets through it, because this is as smooth as the smoke and can be read between
        /// the cells without error; the light itself falls off far too sharply at the lit face of thick smoke.
        /// </summary>
        void Follow(Vector3 toLight, float[] through, float soften, float[] before, float[] after)
        {
            float cellX = dX, cellY = dY, cellZ = dZ;
            float[] smokeIn = thick;
            float ax = Math.Abs(toLight.x), ay = Math.Abs(toLight.y), az = Math.Abs(toLight.z);
            int sa, su, sv;                      // steps through the list along the main direction of the light and the two across it
            float la, lu, lv, ca, cu, cv;
            if (ay >= ax && ay >= az) { sa = G; su = G2; sv = 1; la = toLight.y; lu = toLight.z; lv = toLight.x; ca = cellY; cu = cellZ; cv = cellX; }
            else if (ax >= az) { sa = 1; su = G; sv = G2; la = toLight.x; lu = toLight.y; lv = toLight.z; ca = cellX; cu = cellY; cv = cellZ; }
            else { sa = G2; su = 1; sv = G; la = toLight.z; lu = toLight.x; lv = toLight.y; ca = cellZ; cu = cellX; cv = cellY; }
            float steep = Math.Abs(la) < 0.2f ? 0.2f : Math.Abs(la);
            int toward = la > 0f ? 1 : -1;
            float du = lu / steep * ca / cu, dv = lv / steep * ca / cv;       // where, one layer nearer the light, the ray to this cell came through
            float path = ca / steep * soften;
            // That place is the same number of cells away for every cell, so the four cells it lies between, and the share of each, are worked out once.
            int ou = (int)Math.Floor(du), ov = (int)Math.Floor(dv);
            float wu = du - ou, wv = dv - ov;
            float w00 = (1f - wu) * (1f - wv), w10 = wu * (1f - wv), w01 = (1f - wu) * wv, w11 = wu * wv;
            int iLow = Math.Max(0, -ou), iHigh = Math.Min(G - 1, G - 2 - ou), jLow = Math.Max(0, -ov), jHigh = Math.Min(G - 1, G - 2 - ov), shift = ou + ov * G;
            for (int m = 0; m < G; m++)
            {
                int layer = toward > 0 ? G - 1 - m : m, start = layer * sa;
                for (int j = 0; j < G; j++)
                {
                    bool inside = m > 0 && j >= jLow && j <= jHigh;
                    int row = j * G, cells = start + j * sv;
                    for (int i = 0; i < G; i++)
                    {
                        float arriving = 0f;
                        if (inside && i >= iLow && i <= iHigh)
                        {
                            int b = row + i + shift;
                            arriving = before[b] * w00 + before[b + 1] * w10 + before[b + G] * w01 + before[b + G + 1] * w11;
                        }
                        int at = cells + i * su;
                        float x = smokeIn[at] * path;
                        through[at] = arriving + 0.5f * x;
                        after[row + i] = arriving + x;
                    }
                }
                float[] swap = before; before = after; after = swap;
            }
        }

        /// <summary>
        /// One layer of the grid packed for the shader: how much smoke and how much flame, how much smoke
        /// lies towards the sun and towards the open sky, and how hot the flame is. Amounts are kept as they
        /// are (in two bytes where one is too coarse) and not, say, as square roots, which would spare a byte:
        /// the graphics card reads between the cells by taking a straight mean of what is stored, and only
        /// the mean of the amounts themselves is right.
        /// </summary>
        void Compose(int k)
        {
            bool sun = sunUp;
            float[] smokeIn = thick, flameIn = flaming, hotIn = hotness;
            for (int j = 0; j < G; j++)
            {
                int row = (j + k * G) * G;
                for (int i = 0; i < G; i++)
                {
                    int at = row + i;
                    float smoke = smokeIn[at], flame = flameIn[at];
                    int deep = smoke >= Thickest ? 65535 : (int)(smoke / Thickest * 65535f + 0.5f), burning = flame >= ThickestFlame ? 65535 : (int)(flame / ThickestFlame * 65535f + 0.5f);
                    float before = sun ? sunNext[at] : MostBefore, above = skyNext[at];
                    int shaded = before >= MostBefore ? 65535 : (int)(before / MostBefore * 65535f + 0.5f);
                    float heat = flame > 1e-4f ? hotIn[at] / flame : 0f;
                    cellsA[at] = new Color32((byte)(shaded >> 8), (byte)(shaded & 255), (byte)(above >= MostAbove ? 255f : above / MostAbove * 255f + 0.5f), (byte)((heat > 1.2f ? 1f : heat / 1.2f) * 255f));
                    cellsB[at] = new Color32((byte)(deep >> 8), (byte)(deep & 255), (byte)(burning >> 8), (byte)(burning & 255));
                }
            }
        }

        /// <summary>
        /// How far it is from each cell of the coarser grid to the nearest that has any smoke or flame in it at
        /// all, counted in cells (the most of the three ways, east, up and north: so every cell nearer than that,
        /// in any direction, is clear). The shader crosses clear air in leaps by it, without looking (see the walk in
        /// tools/shaderpack/volume.glsl); it is worked out from the grid as the shader will read it, to the last bit,
        /// so that a leap can never pass over something the walk would have found.
        /// </summary>
        void Clearance()
        {
            const int W = A + 2, W2 = W * W;
            byte[] d = clearWork;
            Parallel.For(0, A, Air.Few, clearLayer);
            // Twice through, once each way, each cell one more than the least of its neighbours already done.
            // (Round the outside goes a rim of cells that count as far from anything: there is nothing out there.)
            for (int k = 1; k <= A; k++)
                for (int j = 1; j <= A; j++)
                {
                    int at = 1 + j * W + k * W2;
                    for (int i = 1; i <= A; i++, at++)
                    {
                        int v = d[at];
                        if (v == 0) continue;
                        int m = d[at - 1];
                        int o = at - W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                        o = at - W2 - W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                        o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                        o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                        if (m + 1 < v) d[at] = (byte)(m + 1);
                    }
                }
            byte[] into = cellsClear;
            for (int k = A; k >= 1; k--)
                for (int j = A; j >= 1; j--)
                {
                    int at = A + j * W + k * W2, cell = A - 1 + ((j - 1) + (k - 1) * A) * A;
                    for (int i = A; i >= 1; i--, at--, cell--)
                    {
                        int v = d[at];
                        if (v != 0)
                        {
                            int m = d[at + 1];
                            int o = at + W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                            o = at + W2 - W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                            o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                            o += W; if (d[o - 1] < m) m = d[o - 1]; if (d[o] < m) m = d[o]; if (d[o + 1] < m) m = d[o + 1];
                            if (m + 1 < v) { v = m + 1; d[at] = (byte)v; }
                        }
                        into[cell] = (byte)v;
                    }
                }
        }

        const byte ClearFar = 40;                                // (further than any cell of the grid is from any other)

        /// <summary>One layer of that grid, to begin with: nought for a cell with anything in any of the eight cells of the fine grid inside it, far for the rest.</summary>
        void ClearLayer(int k)
        {
            const int W = A + 2, W2 = W * W;
            byte[] d = clearWork;
            Color32[] packed = cellsB;
            if (k == 0)
            {
                // (the rim, once for each grid: the two end layers whole, and the edge of every layer between)
                for (int n = 0; n < W2; n++) { d[n] = ClearFar; d[n + (A + 1) * W2] = ClearFar; }
            }
            int layer = (k + 1) * W2;
            for (int n = 0; n < W; n++) { d[layer + n] = ClearFar; d[layer + (A + 1) * W + n] = ClearFar; d[layer + n * W] = ClearFar; d[layer + n * W + A + 1] = ClearFar; }
            for (int j = 0; j < A; j++)
            {
                int at = layer + (j + 1) * W + 1, under = (2 * j + 2 * k * G) * G;
                for (int i = 0; i < A; i++)
                {
                    int cell = under + 2 * i;
                    Color32 a = packed[cell], b = packed[cell + 1], c = packed[cell + G], e = packed[cell + G + 1], f = packed[cell + G2], g = packed[cell + G2 + 1], h = packed[cell + G2 + G], l = packed[cell + G2 + G + 1];
                    int any = a.r | a.g | a.b | a.a | b.r | b.g | b.b | b.a | c.r | c.g | c.b | c.a | e.r | e.g | e.b | e.a | f.r | f.g | f.b | f.a | g.r | g.g | g.b | g.a | h.r | h.g | h.b | h.a | l.r | l.g | l.b | l.a;
                    d[at + i] = any != 0 ? (byte)0 : ClearFar;
                }
            }
        }

        const float MostBefore = 16f, MostAbove = 8f;            // the most smoke towards the sun and towards the sky that is told apart (in lengths over which light falls to about a third)

        void Shade(ref P q, bool sun)
        {
            if (gridReady)
            {
                Vector3 place = ToGrid(new Vector3(q.x, q.y, q.z));
                float fx = (place.x - gx0) * gix - 0.5f, fy = (place.y - gy0) * giy - 0.5f, fz = (place.z - gz0) * giz - 0.5f;
                if (fx < 0f) fx = 0f; else if (fx > G - 1.001f) fx = G - 1.001f;
                if (fy < 0f) fy = 0f; else if (fy > G - 1.001f) fy = G - 1.001f;
                if (fz < 0f) fz = 0f; else if (fz > G - 1.001f) fz = G - 1.001f;
                int ix = (int)fx, iy = (int)fy, iz = (int)fz;
                float wx = fx - ix, wy = fy - iy, wz = fz - iz;
                int at = ix + iy * G + iz * G2;
                float open = (float)Math.Exp(-Mix(skyThrough, at, wx, wy, wz));
                q.sky += (open - q.sky) * 0.5f;
                float lit = sun ? (float)Math.Exp(-Mix(sunThrough, at, wx, wy, wz)) : 0f;
                q.lit += (lit - q.lit) * 0.5f;
            }
            float glow = 0f;
            for (int n = 0; n < lampCount; n++)
            {
                float dx = q.x - lamps[n].x, dy = q.y - lamps[n].y, dz = q.z - lamps[n].z, r2 = lamps[n].r * lamps[n].r;
                glow += lamps[n].power * r2 / (dx * dx + dy * dy + dz * dz + r2);
            }
            q.glow += (glow - q.glow) * 0.5f;
            if (q.glow < 1e-4f) q.glow = 0f;                 // (numbers this small slow the processor down)
            if (q.lit < 1e-4f) q.lit = 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Mix(float[] grid, int at, float wx, float wy, float wz)
        {
            float low = (grid[at] * (1f - wx) + grid[at + 1] * wx) * (1f - wy) + (grid[at + G] * (1f - wx) + grid[at + G + 1] * wx) * wy;
            float high = (grid[at + G2] * (1f - wx) + grid[at + G2 + 1] * wx) * (1f - wy) + (grid[at + G2 + G] * (1f - wx) + grid[at + G2 + G + 1] * wx) * wy;
            return low + (high - low) * wz;
        }
    }
}
