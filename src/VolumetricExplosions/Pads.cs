using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// A launch pad, as far as an engine's exhaust goes. The middle of each of the game's pads is a grate over a flame
    /// trench: what an engine standing over it blows down through the grate comes out at the trench's two ends, one on
    /// each side of the pad, low down and in a gale, each end a hole in a sloping wall with the ground in front of it
    /// burnt. The game knows as much itself: each pad has a "receiver" over its grate (a box its own smoke effect is
    /// driven from, see LaunchPadFX), and its own smoke comes out of the trench's ends. Both are taken from there.
    /// </summary>
    public sealed class Pad
    {
        public LaunchPadFX fx;
        public BoxCollider grate;                            // the receiver: its top is the grate
        public Transform[] ends = new Transform[0];          // the trench's ends, each pointing out
        public float[] wide = new float[0], tall = new float[0], due = new float[0];      // half the width and half the height of each end, metres; and the puffs owing at each (see Site.Ends)
        public int[] lamp = new int[0];                      // the fire's light at each end, in the patch its smoke goes into (see Site.Ends)
        // (Since 2026-11-16: the flame of the engine putting the most down the grate, for its light on the pad's cloud: the mouth of its
        // nozzle and which way its jet goes, in the scene's axes; how long its flame is; and its lamp in the patch, see Site.Ends)
        public Vector3d flameAt, flameAlong;
        public float flameLong;
        public int flameLamp = -1;
#if DEV
        public bool lampKeptDev;                             // (development: whether Site.Ends kept the flame's lamp up last frame)
#endif
        public float deckDue;                                // (the puffs owing off the deck round the flame's foot: see Site.Ends)
        // (this frame: the middle of the grate's top, its two level axes and their half lengths, and up)
        public Vector3 middle, axisA, axisB, up;
        public float halfA, halfB;
        // What is going down it.
        public float made;                                   // smoke a second (as a puff's mass reckons smoke: see Site.SmokeMade)
        public float fill;                                   // 0 to 1: how full the trench is (it takes a moment to fill when engines light, and to empty when they stop)
        public float strongest;                              // how hard the hardest-burning engine over it is burning
        public float shade = 0.8f, warm, lasts = 10f, nozzle = 0.5f;
        public int tint;
        public Site site;                                    // the patch of air its smoke goes into
        // (Since 2026-11-20: 0 to 1, how much the flame over it lights its cloud. Up as the trench fills while an engine burns over the
        // pad, its jet down the grate or not, and less as the flame climbs away, out between 250 and 400 m; down over 1.2 s after.)
        public float shine;
        bool fedNow, shoneNow;
        float nextMade, nextStrongest, most, shineMost, shineReach, shineLong, shineStrongest;
        Vector3d shineAt, shineAlong;

        public void Begin() { fedNow = false; shoneNow = false; nextMade = 0f; nextStrongest = 0f; most = 0f; shineMost = 0f; shineReach = 0f; }

        /// <summary>An engine sends this share of its jet, and of the smoke it makes a second, down the grate this frame.</summary>
        public void Feed(in Exhaust e, float share, float smoke)
        {
            fedNow = true;
            nextMade += smoke * share;
            if (e.running > nextStrongest) nextStrongest = e.running;
            // (the colour of the trench's smoke is that of the engine putting the most down it)
            if (smoke * share > most) { most = smoke * share; shade = e.shade; warm = e.warm; lasts = e.lasts; tint = e.tint; nozzle = e.nozzle; flameAt = e.rim; flameAlong = e.along; flameLong = e.flame; }
        }

        /// <summary>
        /// (Since 2026-11-20.) An engine burning over the pad whose jet does not go down its grate: a rocket risen off it, or tipped
        /// or blown so that its jet misses the grate. Its flame lights the pad's cloud still, from where it now is, its light falling
        /// off with the way to it (see Site.Ends), and less again as it climbs away (reach: 1 near, 0 far; see Pads.Over). The
        /// longest and hardest-burning such flame. Until then the flame's light on the cloud went with the trench's filling, and
        /// went out over 1.2 s once the jet left the grate: in the Kerbal X's night launch 5.3 s in, its nozzle 80 m up and its
        /// flame 49 m long, the flame's end among the billows still. The lamp being nine times what the smoke's light is held at
        /// (see FlameLights, and firelight in volume.glsl), the fading showed only in its last tenth, in two frames at 6.4 s, on
        /// all of the cloud at once: the jump 6.4 s into every night launch film (`lift20_shine_old`; none in `_now`).
        /// </summary>
        public void Shine(in Exhaust e, float reach)
        {
            shoneNow = true;
            float bright = e.flame * e.running * reach;
            if (bright <= shineMost) return;
            shineMost = bright; shineReach = reach; shineAt = e.rim; shineAlong = e.along; shineLong = e.flame; shineStrongest = e.running;
        }

        public void End(float dt)
        {
            if (fedNow)
            {
                // (smoothly: as a rocket rises off the grate less and less of its jet goes down it)
                made += (nextMade - made) * (1f - Mathf.Exp(-dt / 0.25f));
                strongest = nextStrongest;
                fill = Mathf.Min(1f, fill + dt / 0.35f);
            }
            else
            {
                fill = Mathf.Max(0f, fill - dt / 1.2f);
                if (fill <= 0f) made = 0f;
                // (the flame of an engine burning over it all the same, for its light: see Shine)
                if (shoneNow) { flameAt = shineAt; flameAlong = shineAlong; flameLong = shineLong; strongest = shineStrongest; }
            }
            float towards = fedNow ? 1f : shoneNow ? shineReach : 0f;
            shine = towards > shine ? Mathf.Min(towards, shine + dt / 0.35f) : Mathf.Max(towards, shine - dt / 1.2f);
#if DEV
            // (development: say in the log when a jet begins and stops going down the grate, and how high its nozzle is then)
            if (fedNow != fedDev)
            {
                fedDev = fedNow;
                Debug.Log("[VolumetricExplosions] (dev) a pad's grate " + (fedNow ? "fed" : "no longer fed") + " at " + Time.time.ToString("F2") + " s: nozzle " + Vector3d.Dot(flameAt - (Vector3d)middle, (Vector3d)up).ToString("F0") + " m over the grate, flame " + flameLong.ToString("F1") + " m, shine " + shine.ToString("F2") + (shoneNow ? ", reach " + shineReach.ToString("F2") : ""));
            }
#endif
        }
#if DEV
        bool fedDev;
#endif
    }

    static class Pads
    {
        static readonly List<Pad> all = new List<Pad>();
        static readonly FieldInfo emitters = typeof(LaunchPadFX).GetField("ps", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static List<Pad> All => all;

        /// <summary>Every pad in the scene (when the air begins: the pads of a scene are there from its start).</summary>
        public static void Look()
        {
            all.Clear();
            foreach (LaunchPadFX fx in Object.FindObjectsOfType<LaunchPadFX>())
            {
                BoxCollider box = fx != null ? fx.GetComponent<BoxCollider>() : null;
                if (box == null) continue;
                var pad = new Pad { fx = fx, grate = box };
                var ends = new List<Transform>();
                var wide = new List<float>();
                var tall = new List<float>();
                if (emitters != null && emitters.GetValue(fx) is ParticleSystem[] made)
                    foreach (ParticleSystem s in made)
                    {
                        if (s == null) continue;
                        // (the game's own smoke there is a flattened cone: as wide as the hole and half as tall)
                        Vector3 scale = s.transform.lossyScale;
                        float r = Mathf.Max(s.shape.radius, 1f);
                        ends.Add(s.transform);
                        wide.Add(Mathf.Clamp(r * Mathf.Abs(scale.x), 1.5f, 8f));
                        tall.Add(Mathf.Clamp(r * Mathf.Abs(scale.y), 1f, 5f));
                    }
                pad.ends = ends.ToArray(); pad.wide = wide.ToArray(); pad.tall = tall.ToArray(); pad.due = new float[pad.ends.Length];
                pad.lamp = new int[pad.ends.Length];
                for (int n = 0; n < pad.lamp.Length; n++) pad.lamp[n] = -1;
                all.Add(pad);
            }
        }

        /// <summary>Each frame, before the engines say what they are putting out: where each grate is now.</summary>
        public static void Frame()
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            for (int n = all.Count - 1; n >= 0; n--)
            {
                Pad pad = all[n];
                if (pad.fx == null || pad.grate == null) { all.RemoveAt(n); continue; }
                pad.Begin();
                Transform t = pad.grate.transform;
                Vector3 c = t.TransformPoint(pad.grate.center);
                Vector3 up = body != null ? (Vector3)FlightGlobals.getUpAxis(body, (Vector3d)c) : t.up;
                Vector3 scale = t.lossyScale, size = pad.grate.size;
                Vector3[] axes = { t.right, t.up, t.forward };
                float[] half = { 0.5f * Mathf.Abs(size.x * scale.x), 0.5f * Mathf.Abs(size.y * scale.y), 0.5f * Mathf.Abs(size.z * scale.z) };
                int vertical = 0;
                for (int k = 1; k < 3; k++) if (Mathf.Abs(Vector3.Dot(axes[k], up)) > Mathf.Abs(Vector3.Dot(axes[vertical], up))) vertical = k;
                int a = vertical == 0 ? 1 : 0, b = vertical == 2 ? 1 : 2;
                float side = Vector3.Dot(axes[vertical], up) >= 0f ? 1f : -1f;
                pad.middle = c + axes[vertical] * (side * half[vertical]);
                pad.up = up;
                pad.axisA = Vector3.ProjectOnPlane(axes[a], up).normalized; pad.halfA = half[a];
                pad.axisB = Vector3.ProjectOnPlane(axes[b], up).normalized; pad.halfB = half[b];
            }
        }

        /// <summary>
        /// How much of an engine's jet goes down a pad's grate, and which pad's: where the jet's line meets the grate's
        /// top, the jet as wide there as it has spread by then (a tenth or so of the way it has come), and less of it the
        /// further it has come, until a rocket fifty metres up is pushing its smoke out over the pad and not down the trench.
        /// </summary>
        public static float Into(in Exhaust e, out Pad which)
        {
            which = null;
            float best = 0f;
            Vector3 rim = (Vector3)e.rim, along = (Vector3)e.along;
            foreach (Pad pad in all)
            {
                if (pad.grate == null || (pad.middle - rim).sqrMagnitude > 160f * 160f) continue;
                float down = -Vector3.Dot(along, pad.up);
                if (down < 0.4f) continue;                                          // (not pointing down at it)
                float t = Vector3.Dot(rim - pad.middle, pad.up) / down;            // how far along the jet the grate's top is
                if (t < -1f || t > 95f) continue;
                Vector3 hit = rim + along * t - pad.middle;
                float r = e.nozzle * 2.1f + 0.12f * Mathf.Max(t, 0f);
                float share = Overlap(Vector3.Dot(hit, pad.axisA), r, pad.halfA) * Overlap(Vector3.Dot(hit, pad.axisB), r, pad.halfB) * (1f - Mathf.SmoothStep(0f, 1f, (t - 35f) / 55f));
                if (share > best) { best = share; which = pad; }
            }
            return best;
        }

        /// <summary>
        /// (Since 2026-11-20.) The pad an engine burns over, near enough for its flame to light the pad's cloud whether its jet goes
        /// down the grate or not: its nozzle's mouth within 400 m of the grate, its jet pointing down at the ground; and how much
        /// (reach): all of it to 250 m, none at 400, by then its light on the cloud a hundredth of what it was close by. Null for none.
        /// </summary>
        public static Pad Over(in Exhaust e, out float reach)
        {
            Pad best = null;
            float near = 400f * 400f;
            Vector3 rim = (Vector3)e.rim, along = (Vector3)e.along;
            foreach (Pad pad in all)
            {
                if (pad.grate == null || -Vector3.Dot(along, pad.up) < 0.4f) continue;
                float d = (pad.middle - rim).sqrMagnitude;
                if (d < near) { near = d; best = pad; }
            }
            reach = best != null ? 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(250f, 400f, Mathf.Sqrt(near))) : 0f;
            return best;
        }

        /// <summary>How much of a stretch this wide about this place lies within so far either side of nought.</summary>
        static float Overlap(float at, float r, float half) => Mathf.Clamp01((Mathf.Min(at + r, half) - Mathf.Max(at - r, -half)) / (2f * r));
    }

    /// <summary>
    /// (Since 2026-11-22.) The painted ground of the space centre and the other launch sites (their decals: the burnt fan of
    /// streaks in front of each end of a pad's trench, the lines and marks on the paving) drawn before the smoke, not over it.
    /// The game draws them among the see-through things (queue 3000, the shader "KSP/Scenery/Decal/Blended"), each multiplying
    /// the colour already drawn under it, and this mod's smoke is drawn in the same queue, the farther of the two first. Where
    /// the paint came after the smoke it multiplied the smoke as well: from the camera of the Kerbal X's night launch, the fan in
    /// front of the trench's mouth showed through the bottom of the cloud for seven seconds, brighter than the cloud where the
    /// flame's light fell on it and darker where none did, hard-edged as paint is (the owner, 2026-11-22, of three pictures of
    /// it: "in the bottom middle part of the lit-up smoke, you can see that there are these rendering issues where it shows the
    /// textures of rectangles under the smoke"). Now drawn first of the see-through things that go over the ground (2502, after the
    /// sea's 2501): before the smoke's sheet (3000) and this mod's glow behind it (2990), the fog Natural Weather lays on the
    /// ground (2999) and Scatterer's haze (2997, 2998), so that the paint is under all of them, as the ground it is on is. Not
    /// with the solid things, as this mod's own burn marks and Fragmentation's scrapes are (2460, 2455): the "Blended" kind
    /// has a pass for shadows, and among the solid things it would throw them, and be in the picture of depth that the smoke
    /// stops at. Moved when the air begins and once more when the scene about the ship has settled (see Air.WarmUp: the paint
    /// of what came after, such as the flags on the buildings); put back as they were when the air goes.
    /// </summary>
    static class Paint
    {
        const int Queue = 2502;
        static readonly Dictionary<Material, int> moved = new Dictionary<Material, int>();
#if DEV
        public static bool Kept;                             // (for the development build: the paint drawn over the smoke, as until 2026-11-22)
        public static int Count => moved.Count;
#endif

        /// <summary>Every such material the game has loaded, whether what wears it is in sight or not (each is shared by all that wear it).</summary>
        public static void Move()
        {
#if DEV
            if (Kept) return;
#endif
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || m.renderQueue <= Queue || moved.ContainsKey(m)) continue;
                if (!m.shader.name.StartsWith("KSP/Scenery/Decal", System.StringComparison.Ordinal)) continue;
                moved[m] = m.renderQueue;
                m.renderQueue = Queue;
            }
        }

        public static void Back()
        {
            foreach (KeyValuePair<Material, int> was in moved) if (was.Key != null) was.Key.renderQueue = was.Value;
            moved.Clear();
        }
    }
}
