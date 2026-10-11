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
        bool fedNow;
        float nextMade, nextStrongest, most;

        public void Begin() { fedNow = false; nextMade = 0f; nextStrongest = 0f; most = 0f; }

        /// <summary>An engine sends this share of its jet, and of the smoke it makes a second, down the grate this frame.</summary>
        public void Feed(in Exhaust e, float share, float smoke)
        {
            fedNow = true;
            nextMade += smoke * share;
            if (e.running > nextStrongest) nextStrongest = e.running;
            // (the colour of the trench's smoke is that of the engine putting the most down it)
            if (smoke * share > most) { most = smoke * share; shade = e.shade; warm = e.warm; lasts = e.lasts; tint = e.tint; nozzle = e.nozzle; }
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
            }
        }
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

        /// <summary>How much of a stretch this wide about this place lies within so far either side of nought.</summary>
        static float Overlap(float at, float r, float half) => Mathf.Clamp01((Mathf.Min(at + r, half) - Mathf.Max(at - r, -half)) / (2f * r));
    }
}
