using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// (Since 0.8.0.) The bang made from the blast itself, as it would reach the camera.
    ///
    /// What a blast sounds like (the research, see the wiki's page on shock fronts): close by, a crack: the pressure jumps at
    /// the front and falls back over a few milliseconds (a Friedlander wave; the bigger the charge, the longer, as the cube
    /// root of its weight: a few milliseconds for kilograms, tens for tonnes), then dips below the air's for longer. Its
    /// sharpness is high notes, and the air takes those off with distance (some tens of decibels a kilometre at a few kilohertz,
    /// next to nothing at a hundred hertz): from a kilometre off a blast is a thump, from several a deep boom. After it comes
    /// the rumble: the fuel's fireball roaring as it burns (a fuel blast is a burning, the roar lasting as long as its fire) and
    /// the bang coming back off the ground, hills and buildings, later and later and lower, for seconds, the longer the further
    /// and larger. On the ground the wave and its reflection off the ground arrive together, twice as strong.
    ///
    /// So each blast's sound is made for it (on another thread, from its charge, how far off the camera is and whether it went
    /// off near the ground): the pulse, its reflection, a deep roar with the fire's crackle in it as long as the fire, a few
    /// echoes, all of it through a low-pass filter at the cut-off the air leaves at that distance and held down at its loudest as
    /// a microphone near a blast is overloaded; and played from where the blast was, when its sound gets to the camera, as loud
    /// as the distance leaves it (see Blast), instead of the game's two recordings. The game's own come back with `own_bang =
    /// false` in the settings, and wherever these cannot be made in time (a blast's sound is due before its own is ready: then
    /// the game's are played, as before).
    /// </summary>
    public static class Bangs
    {
        const int Rate = 44100;

        sealed class Made { public float[] samples; public int rate; }
        static readonly Dictionary<long, Made> ready = new Dictionary<long, Made>();
        static readonly Dictionary<long, AudioClip> clips = new Dictionary<long, AudioClip>();
        static readonly HashSet<long> asked = new HashSet<long>();
        static readonly object gate = new object();

        /// <summary>
        /// The class a blast's sound is made for: its charge in steps of a factor of two, the camera's distance in steps of half an
        /// octave, and whether it went off near the ground. (Made once for each, and kept: a dozen blasts of a crash share a few.)
        /// </summary>
        public static long Key(float charge, float distance, bool ground)
        {
            int c = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(Mathf.Max(charge, 0.05f), 2f)), -5, 20);
            int d = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.Log(Mathf.Max(distance, 10f) / 10f, 2f)), 0, 26);
            return (c + 10) * 1000L + d * 10L + (ground ? 1 : 0);
        }

        /// <summary>Begin making the sound for a blast (when it goes off: by the time its sound gets to the camera it is ready).</summary>
        public static void Ask(float charge, float distance, bool ground, float fireSeconds)
        {
            long key = Key(charge, distance, ground);
            lock (gate) { if (!asked.Add(key)) return; }
            // (made at the class's own charge and distance, so that what is kept is what the key says)
            int c = (int)(key / 1000L) - 10, d = (int)(key / 10L % 100L);
            float qCharge = Mathf.Pow(2f, c), qDistance = 10f * Mathf.Pow(2f, d * 0.5f);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Made m = Make(qCharge, qDistance, ground, fireSeconds, (int)(key & 0x7FFFFFFF));
                    lock (gate) ready[key] = m;
                }
                catch (Exception ex) { Debug.LogError("[VolumetricExplosions] could not make a bang: " + ex.Message); }
            });
        }

        /// <summary>The quiet before the crack at the start of every bang (seconds), which its playing begins that much early for.</summary>
        public const float Lead = 0.01f;

        /// <summary>The clip for a blast, if it has been made (on the game's thread: the sound turned into a clip the first time).</summary>
        public static AudioClip Clip(float charge, float distance, bool ground)
        {
            long key = Key(charge, distance, ground);
            if (clips.TryGetValue(key, out AudioClip clip) && clip != null) return clip;
            Made m;
            lock (gate) { if (!ready.TryGetValue(key, out m)) return null; }
            clip = AudioClip.Create("Volumetric Explosions bang " + key, m.samples.Length, 1, m.rate, false);
            clip.SetData(m.samples, 0);
            clips[key] = clip;
            // (and only a few dozen kept: the oldest go)
            if (clips.Count > 40)
            {
                foreach (KeyValuePair<long, AudioClip> one in clips) { if (one.Key == key) continue; if (one.Value != null) UnityEngine.Object.Destroy(one.Value); clips.Remove(one.Key); lock (gate) { ready.Remove(one.Key); asked.Remove(one.Key); } break; }
            }
            return clip;
        }

        /// <summary>
        /// The sound itself: charge in kilograms of TNT (a tenth of the propellant that burns), the camera's distance in metres,
        /// near the ground or not, how long the fire burns (seconds).
        /// </summary>
        static Made Make(float charge, float distance, bool ground, float fireSeconds, int seed)
        {
            var luck = new System.Random(seed);
            float Rnd() => (float)luck.NextDouble();
            float cube = Mathf.Pow(Mathf.Max(charge, 0.05f), 1f / 3f);
            // (far off only the low notes are left: the sound made at half the rate there)
            int rate = distance > 1500f ? Rate / 2 : Rate;
            // How long the rumble goes on: the fire's roar and the echoes, longer the larger and the further.
            float tail = Mathf.Clamp(1.5f + 0.9f * cube + 1.2f * Mathf.Log10(Mathf.Max(distance, 10f) / 10f) + 0.6f * fireSeconds, 1.5f, 11f);
            // ---- (since 0.9.0) a recording of a real blast in place of the made roar, where the mod has recordings (see Recorded):
            // the one nearest in kind to the distance, slowed (deeper and longer) the bigger the blast, as a bigger fireball's roar is
            float[] heard = Recorded.On ? Recorded.For(distance, seed) : null;
            float step = 1f;
            int heardLength = 0;
            if (heard != null && heard.Length > 2)
            {
                float pitch = Mathf.Clamp(1.4f * Mathf.Pow(cube, -0.25f), 0.6f, 1.25f) * (0.95f + 0.1f * Rnd());
                step = pitch * Recorded.Rate / rate;
                heardLength = (int)((heard.Length - 2) / step);
            }
            else heard = null;
            int n = Math.Max((int)((tail + 0.5f) * rate), (int)(Lead * rate) + heardLength + rate / 4);
            var s = new float[n];
            // ---- the crack: a Friedlander wave, its positive phase as the cube root of the charge, a little longer with distance
            float td = Mathf.Clamp(0.0035f * cube * (1f + 0.12f * Mathf.Log(1f + distance / 50f)), 0.002f, 0.09f);
            int start = (int)(Lead * rate);
            float reflect = ground ? 0.85f : 0.35f;
            float reflectDelay = ground ? 0.0006f : Mathf.Clamp(6f / Mathf.Max(distance, 20f), 0.001f, 0.08f);
            void Pulse(float at, float amp, float length)
            {
                int i0 = start + (int)(at * rate), len = (int)(length * 6f * rate);
                for (int i = 0; i < len && i0 + i < n; i++)
                {
                    float t = i / (float)rate;
                    float x = t / length;
                    s[i0 + i] += amp * (1f - x) * Mathf.Exp(-2.2f * x);
                }
            }
            Pulse(0f, 1f, td);
            Pulse(reflectDelay, reflect, td * 1.15f);
            // ---- the roar: a deep noise (even up to about 150 Hz, falling above) with the fire's crackle in it, swelling in a
            // twentieth of a second and dying away over the tail, as loud against the crack as the distance has made it (close by the
            // crack is everything; far off the rumble is most of what is heard)
            float roarLevel = Mathf.Lerp(0.18f, 0.55f, Mathf.Clamp01(Mathf.Log10(Mathf.Max(distance, 10f) / 30f) / 2.3f)) * (0.7f + 0.3f * Mathf.Clamp01(fireSeconds / 3f));
            if (heard != null)
            {
                // (the recording read at its step, a sample's worth between two of its own taken in between; begun over 5 ms)
                float heardLevel = 2f * roarLevel;
                int fadeIn = Math.Max(1, rate / 200);
                for (int i = 0; i < heardLength && start + i < n; i++)
                {
                    float pos = i * step;
                    int i0 = (int)pos;
                    float x = heard[i0] + (heard[i0 + 1] - heard[i0]) * (pos - i0);
                    s[start + i] += heardLevel * x * Mathf.Clamp01(i / (float)fadeIn);
                }
            }
            float brown = 0f, crackle = 0f;
            for (int i = 0; heard == null && i < n - start; i++)
            {
                float t = i / (float)rate;
                brown = brown * 0.98f + (Rnd() * 2f - 1f) * 0.2f;
                crackle = crackle * 0.7f + (Rnd() * 2f - 1f) * 0.3f;
                // (a fuel fire roars in surges: slow swells on the noise)
                float surge = 0.75f + 0.25f * Mathf.Sin(t * 2.1f * 6.2832f) * Mathf.Sin(t * 0.73f * 6.2832f + 1.3f);
                float env = Mathf.Clamp01(t / 0.05f) * Mathf.Exp(-t / (0.32f * tail)) * surge;
                s[start + i] += roarLevel * env * (brown + 0.5f * crackle);
            }
            // ---- echoes off the ground and whatever stands about: later and fainter, each softer than the last
            int echoes = 3 + luck.Next(4);
            for (int k = 0; k < echoes; k++)
            {
                float at = 0.15f + Rnd() * (0.4f + 0.25f * tail) * (1f + k * 0.4f);
                Pulse(at, (0.35f - 0.04f * k) * (0.6f + 0.4f * Rnd()), td * (1.6f + 0.8f * k));
            }
            // ---- what the air leaves of it at that distance: the high notes taken off, a pole at the cut-off (twice over, a
            // steeper fall), and the low rumble of the ground's own give under it for a ground blast close by
            float cut = Mathf.Clamp(2600f * Mathf.Pow(1000f / Mathf.Max(distance, 30f), 0.85f), 110f, 15000f);
            float a = Mathf.Exp(-2f * Mathf.PI * cut / rate);
            for (int pass = 0; pass < 2; pass++)
            {
                float y = 0f;
                for (int i = 0; i < n; i++) { y = (1f - a) * s[i] + a * y; s[i] = y; }
            }
            // (and nothing below twenty hertz: a speaker cannot, and it only takes the room a loudspeaker has)
            float hp = 0f, prev = 0f, b = Mathf.Exp(-2f * Mathf.PI * 20f / rate);
            for (int i = 0; i < n; i++) { float x = s[i]; hp = b * (hp + x - prev); prev = x; s[i] = hp; }
            // ---- its loudest moments held down as a microphone near a blast is overloaded (a soft limit, tanh), so that it sounds as
            // loud as the game's own bang at the same distance and is still the sharper at its start: measured as recorded from the
            // game, a tonne's worth of propellant at 300 m and 1500 m, momentary loudness within a decibel of the game's (it was 10 dB
            // under, nearly all of it one crack and bass too deep for small speakers)
            float peak = 1e-6f;
            for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(s[i]));
            const float Drive = 3.5f;
            float level = (float)Math.Tanh(Drive);
            for (int i = 0; i < n; i++) s[i] = (float)Math.Tanh(Drive * s[i] / peak) / level;
            // ---- as loud as it may be, and faded out at its very end
            float most = 1e-6f;
            for (int i = 0; i < n; i++) most = Mathf.Max(most, Mathf.Abs(s[i]));
            float gain = 0.92f / most;
            int fade = Math.Min(n / 4, rate / 2);
            for (int i = 0; i < n; i++) s[i] *= gain * (i > n - fade ? (n - i) / (float)fade : 1f);
            return new Made { samples = s, rate = rate };
        }

#if DEV
        /// <summary>(Development build) which bangs are made and kept.</summary>
        public static string Says()
        {
            lock (gate) return clips.Count + " clips made into sounds, " + ready.Count + " made, " + asked.Count + " asked for";
        }
#endif
    }
}
