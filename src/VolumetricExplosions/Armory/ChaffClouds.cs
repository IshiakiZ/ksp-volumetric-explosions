using System.Collections.Generic;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// Chaff (BD Armory's CMChaff), drawn as ours. A cartridge of chaff is a bundle of a great many glass fibres a few centimetres long,
    /// coated with aluminium, thrown out into the air behind the aircraft, where it bursts and blooms in under a second into a cloud some
    /// metres across, which goes on spreading and falls slowly: to a radar, a reflector as big as an aircraft. To the eye it is a small
    /// pale puff that thins within a few seconds, and in it the fibres, each too fine to be seen, flash as they tumble, each one whenever it
    /// lies square to the way halfway between the sun and the eye, where its shine is thrown at the eye: a glitter. BD Armory draws a single
    /// flat picture of strips, from half a metre to forty across, for one to three seconds. Here a cloud of fibres in three dimensions, each
    /// tumbling and catching the sun as it would, with a faint haze in which the cloud is seen while it is thick: its middle left where the
    /// cartridge burst, slowed from the aircraft's speed in a few hundredths of a second, falling at about a metre a second; twelve seconds
    /// in all, the last half fading. No glitter without the sun up; the haze as dim as the light. No air, no bloom: BD Armory's own.
    /// </summary>
    static class ChaffClouds
    {
        const int Fibres = 320, Puffs = 5, Most = 30;
        const float Lasts = 12f, Fading = 6f;
        const float Slide = 0.06f;               // (s at the speed it was thrown: how far on it is slowed to a stop)
        const float Reach = 6f, Bloom = 0.3f;    // (m: how far out its fibres reach once it has bloomed; s: how soon)
        const float Spread = 0.7f, Fall = 1f;    // (m/s: how fast it spreads after, and falls)
        const float Sharp = 280f;                // (how square to the sun's shine a fibre lies to flash: within some three and a half degrees)
        const float HazeMost = 0.38f;

        sealed class Cloud
        {
            public GameObject Go;
            public ParticleSystem Glints, Haze;
            public CelestialBody Body;
            public double Lat, Lon, Alt;
            public float Born;
            public Vector3 Along;                // (the way it was thrown, a unit, or nought: drawn out along it at first)
            public readonly Vector3[] Out = new Vector3[Fibres], Lie = new Vector3[Fibres], Across = new Vector3[Fibres];
            public readonly float[] Spin = new float[Fibres], Phase = new float[Fibres], Sink = new float[Fibres];
            public readonly Vector3[] PuffAt = new Vector3[Puffs];
            public readonly ParticleSystem.Particle[] G = new ParticleSystem.Particle[Fibres], H = new ParticleSystem.Particle[Puffs];
        }

        static readonly List<Cloud> clouds = new List<Cloud>(), spare = new List<Cloud>();
        static GameObject shelf;

        /// <summary>(For Tests.Said: the clouds begun, and those alive.)</summary>
        internal static int Made;
        internal static int Alive => clouds.Count;

        /// <summary>A cartridge of chaff burst at a place in the scene, thrown at a speed (m/s, as BD Armory has it): a cloud of ours begun
        /// there. False where none can be (no air, or the explosions mod's materials not made): BD Armory's own is left.</summary>
        internal static bool Burst(Vector3 at, Vector3 going, float now)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null || !body.atmosphere || FlightGlobals.getStaticPressure((Vector3d)at, body) <= 0.0) return false;
            Cloud c = Take();
            if (c == null) return false;
            float speed = going.magnitude;
            c.Along = speed > 1f ? going / speed : Vector3.zero;
            body.GetLatLonAlt((Vector3d)(at + going * Slide), out c.Lat, out c.Lon, out c.Alt);
            c.Body = body;
            c.Born = now;
            for (int i = 0; i < Fibres; i++)
            {
                // (spread through a ball; each tumbling about an axis of its own, its length square to that axis)
                c.Out[i] = Random.onUnitSphere * Mathf.Pow(Random.value, 1f / 3f);
                Vector3 turn = Random.onUnitSphere, lie = Vector3.Cross(turn, Random.onUnitSphere);
                while (lie.sqrMagnitude < 1e-4f) lie = Vector3.Cross(turn, Random.onUnitSphere);
                lie.Normalize();
                c.Lie[i] = lie;
                c.Across[i] = Vector3.Cross(turn, lie);
                c.Spin[i] = Random.Range(3f, 12f) * (Random.value < 0.5f ? -1f : 1f);
                c.Phase[i] = Random.Range(0f, 2f * Mathf.PI);
                c.Sink[i] = Random.Range(0f, 0.8f);
                c.G[i] = new ParticleSystem.Particle { startSize = 0.2f, startLifetime = 1000f, remainingLifetime = 999f, randomSeed = 1 };
            }
            for (int p = 0; p < Puffs; p++)
            {
                c.PuffAt[p] = Random.insideUnitSphere * 0.45f;
                c.H[p] = new ParticleSystem.Particle { startLifetime = 1000f, remainingLifetime = 999f, randomSeed = (uint)(p + 1), rotation = Random.Range(0f, 360f) };
            }
            c.Go.SetActive(true);
            if (!c.Glints.isPlaying) c.Glints.Play(false);
            if (!c.Haze.isPlaying) c.Haze.Play(false);
            clouds.Add(c);
            Made++;
            Step(c, now, Eye(), Sun());
            return true;
        }

        /// <summary>Once a frame (Volumes.Asked): each cloud where it now is, its fibres where they are and which of them flash.</summary>
        internal static void Step(float now)
        {
            if (clouds.Count == 0) return;
            Camera eye = Eye();
            CelestialBody sun = Sun();
            for (int k = clouds.Count - 1; k >= 0; k--)
            {
                Cloud c = clouds[k];
                float t = now - c.Born;
                if (c.Go == null || t > Lasts || t < -1f || c.Body != FlightGlobals.currentMainBody || eye == null)
                {
                    Put(c);
                    clouds.RemoveAt(k);
                    continue;
                }
                Step(c, now, eye, sun);
            }
        }

        static void Step(Cloud c, float now, Camera eye, CelestialBody sun)
        {
            if (eye == null) return;
            float t = Mathf.Max(0f, now - c.Born);
            Vector3d middleD = c.Body.GetWorldSurfacePosition(c.Lat, c.Lon, c.Alt - Fall * t);
            var middle = (Vector3)middleD;
            c.Go.transform.SetPositionAndRotation(middle, Quaternion.identity);
            var up = (Vector3)(middleD - c.Body.position).normalized;
            Vector3 toSun = sun != null ? (Vector3)(sun.position - middleD).normalized : up;
            // (the sun's height as a sine: a glitter only while it is up, reddened low; the haze lit as the day is)
            float high = Vector3.Dot(toSun, up);
            float glitter = Mathf.Clamp01((high + 0.02f) * 12f);
            float day = Mathf.Clamp01(high * 5f + 0.25f);
            Color tint = Color.Lerp(new Color(1f, 0.62f, 0.38f), new Color(1f, 0.97f, 0.9f), Mathf.Clamp01(high * 6f));
            float fade = t < Lasts - Fading ? 1f : Mathf.Clamp01((Lasts - t) / Fading);
            float reach = Reach * (1f - Mathf.Exp(-t / Bloom)) + Spread * t;
            float drawn = 1.5f * Mathf.Exp(-t / 1.5f);
            Vector3 fromEye = eye.transform.position - middle;
            float bright = 255f * glitter * fade;
            for (int i = 0; i < Fibres; i++)
            {
                Vector3 o = c.Out[i] * reach;
                o += c.Along * (Vector3.Dot(o, c.Along) * drawn);
                o -= up * (c.Sink[i] * t);
                float glint = 0f;
                if (bright > 0.5f)
                {
                    // (the way halfway between the sun's and the eye's; a fibre flashes while its length lies square to it)
                    Vector3 half = toSun + (fromEye - o).normalized;
                    float hl = half.magnitude;
                    if (hl > 1e-4f)
                    {
                        float turned = c.Spin[i] * t + c.Phase[i];
                        float d = (Mathf.Cos(turned) * Vector3.Dot(c.Lie[i], half) + Mathf.Sin(turned) * Vector3.Dot(c.Across[i], half)) / hl;
                        glint = Mathf.Exp(-d * d * Sharp);
                    }
                }
                float v = bright * glint;
                c.G[i].position = o;
                c.G[i].startColor = new Color32((byte)(v * tint.r), (byte)(v * tint.g), (byte)(v * tint.b), 255);
            }
            c.Glints.SetParticles(c.G, Fibres);
            // (the haze: thick for its first second, then thinning as it spreads; a light grey, as dim as the light)
            float thick = HazeMost * (1f - Mathf.Exp(-t / 0.12f)) * Mathf.Exp(-t / 2.4f) * fade;
            float grey = 0.08f + 0.72f * day;
            var shade = new Color32((byte)(255f * grey * 0.97f), (byte)(255f * grey * 0.98f), (byte)(255f * grey), (byte)(255f * thick));
            for (int p = 0; p < Puffs; p++)
            {
                Vector3 o = c.PuffAt[p] * reach;
                o += c.Along * (Vector3.Dot(o, c.Along) * drawn);
                o -= up * (0.3f * t);
                c.H[p].position = o;
                c.H[p].startSize = 1.5f + 1.3f * reach;
                c.H[p].startColor = shade;
            }
            c.Haze.SetParticles(c.H, Puffs);
        }

        /// <summary>(Leaving the scene, or BD Armory's own drawn: every cloud put away.)</summary>
        internal static void Forget()
        {
            for (int k = 0; k < clouds.Count; k++) Put(clouds[k]);
            clouds.Clear();
        }

        static Camera Eye() => FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null ? FlightCamera.fetch.mainCamera : Camera.main;

        static CelestialBody Sun() => Planetarium.fetch != null ? Planetarium.fetch.Sun : null;

        static Cloud Take()
        {
            // (the oldest given up for the newest, past thirty at once)
            if (clouds.Count >= Most) { Put(clouds[0]); clouds.RemoveAt(0); }
            while (spare.Count > 0)
            {
                Cloud c = spare[spare.Count - 1];
                spare.RemoveAt(spare.Count - 1);
                if (c.Go != null && c.Glints != null && c.Haze != null) return c;
            }
            return Make();
        }

        static void Put(Cloud c)
        {
            if (c.Go == null) return;
            c.Glints.SetParticles(c.G, 0);
            c.Haze.SetParticles(c.H, 0);
            c.Go.SetActive(false);
            spare.Add(c);
        }

        static Cloud Make()
        {
            // (the shelf goes with the scene, and every cloud on it)
            if (shelf == null) { shelf = new GameObject("Volumetric Armory: chaff"); spare.Clear(); }
            var go = new GameObject("chaff");
            go.transform.SetParent(shelf.transform, false);
            ParticleSystem glints = Feed.Drawn(go.transform, "glints", true, Fibres), haze = Feed.Drawn(go.transform, "haze", false, Puffs);
            if (glints == null || haze == null) { Object.Destroy(go); return null; }
            // (never less than four pixels or so across: a flash that small is still seen, as a point of light)
            glints.GetComponent<ParticleSystemRenderer>().minParticleSize = 0.0055f;
            return new Cloud { Go = go, Glints = glints, Haze = haze };
        }
    }
}
