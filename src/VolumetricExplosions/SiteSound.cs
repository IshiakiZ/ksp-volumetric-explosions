using UnityEngine;

namespace VolumetricExplosions
{
    public sealed partial class Site
    {
        // ---- (since 0.9.0, where the mod has its recordings: see Recorded) a fire's roar while it burns

        // (the loop playing, and the one it took over from while that fades out)
        AudioSource fireSound, fireSoundGoing;
        string fireSoundName = "";
        float fireSoundGoingFrom;

        /// <summary>
        /// The fires of this patch heard while they burn: a recording of a real fire in a loop, from where they burn on the
        /// whole, chosen by how much of them burns just now (a house fire at its height for a big pool, the same fire low as it
        /// dies down, firewood for a small one), the change between two of them over two seconds. As loud as it goes within a
        /// distance that grows with the fire, then falling as one over the distance, and duller the further off, as the air
        /// takes the high sounds first. Nothing where there is no air to carry it, without the recordings, or past 4 km.
        /// </summary>
        void FireSound(float dt)
        {
            float burning = 0f;
            Vector3 at = Vector3.zero;
            for (int n = 0; n < fireCount; n++)
            {
                float strength = Mathf.Min(1f, fires[n].left / Mathf.Max(0.4f * fires[n].total, 0.01f));
                float w = fires[n].r * fires[n].r * strength;
                burning += w; at += new Vector3(fires[n].x, fires[n].y, fires[n].z) * w;
            }
            float distance = burning > 0f ? (at / burning - camL).magnitude : 0f;
            if (!Recorded.On || vacuum || air < 0.05f || burning < 0.2f || distance > 4000f) { FireSoundOff(dt); return; }
            at /= burning;
            string want = burning > 25f ? "fire-big" : burning > 6f ? "fire-low" : "fire-small";
            if (want != fireSoundName)
            {
                AudioClip clip = Recorded.Clip(want);
                if (clip != null)
                {
                    if (fireSoundGoing != null) Object.Destroy(fireSoundGoing.gameObject);
                    fireSoundGoing = fireSound; fireSoundGoingFrom = fireSound != null ? fireSound.volume : 0f;
                    fireSound = NewFireSound(clip);
                    fireSoundName = want;
                }
            }
            if (fireSound == null) return;
            // (full within a distance that grows with the fire, then as one over the distance; the air duller with distance)
            float near = Mathf.Clamp(8f * Mathf.Sqrt(burning) + 10f, 10f, 150f);
            float loud = GameSettings.SHIP_VOLUME * Mathf.Clamp(0.3f + 0.1f * Mathf.Log(1f + burning), 0.3f, 0.8f) * Mathf.Min(1f, near / Mathf.Max(distance, 1f));
            Vector3 world = (Vector3)frame.ToWorld(at);
            fireSound.transform.position = world;
            fireSound.volume = Mathf.MoveTowards(fireSound.volume, loud, dt / 2f);
            float dull = Mathf.Clamp(22000f * Mathf.Pow(100f / Mathf.Max(distance, 100f), 0.7f), 900f, 22000f);
            AudioLowPassFilter filter = fireSound.GetComponent<AudioLowPassFilter>();
            if (filter != null) filter.cutoffFrequency = dull;
            if (fireSoundGoing != null)
            {
                fireSoundGoing.transform.position = world;
                fireSoundGoing.volume = Mathf.MoveTowards(fireSoundGoing.volume, 0f, dt * Mathf.Max(fireSoundGoingFrom, 0.05f) / 2f);
                if (fireSoundGoing.volume <= 0f) { Object.Destroy(fireSoundGoing.gameObject); fireSoundGoing = null; }
            }
        }

        AudioSource NewFireSound(AudioClip clip)
        {
            var holder = new GameObject("VolumetricExplosions fire's sound");
            holder.transform.SetParent(t, false);
            AudioSource s = holder.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = true; s.playOnAwake = false; s.dopplerLevel = 0f; s.priority = 64;
            s.spatialBlend = 1f;
            // (the loudness set by the distance here, not left to the speaker's fall-off: see Blast.Own)
            s.rolloffMode = AudioRolloffMode.Custom;
            s.maxDistance = 30000f;
            s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
            s.volume = 0f;
            holder.AddComponent<AudioLowPassFilter>().cutoffFrequency = 22000f;
            // (begun somewhere in the loop: two fires begun together do not roar in step)
            s.time = Random.Range(0f, Mathf.Max(0f, clip.length - 0.1f));
            s.Play();
            return s;
        }

        /// <summary>The fire's sound let fade (dt: a frame's time) and taken away when it is gone, or at once (dt 0, as the patch ends).</summary>
        void FireSoundOff(float dt)
        {
            foreach (AudioSource s in new[] { fireSound, fireSoundGoing })
            {
                if (s == null) continue;
                s.volume = dt > 0f ? Mathf.MoveTowards(s.volume, 0f, dt / 1.5f) : 0f;
            }
            if (fireSound != null && fireSound.volume <= 0f) { Object.Destroy(fireSound.gameObject); fireSound = null; fireSoundName = ""; }
            if (fireSoundGoing != null && fireSoundGoing.volume <= 0f) { Object.Destroy(fireSoundGoing.gameObject); fireSoundGoing = null; }
        }
    }
}
