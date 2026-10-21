using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// The object the game makes a copy of when something blows up. It carries the bang and nothing to
    /// see: as soon as it appears it reports to the <see cref="Air"/>, which works out what kind of
    /// explosion this is and sets the particles off where the game has put it.
    ///
    /// The bang is heard when it gets here. The game starts it on the instant, however far off the
    /// explosion is, which suits neither the eye (the flash is seen at once, and should be) nor the shock
    /// front, which the mod sends out at the speed of sound and which jolts the camera when it arrives. So
    /// the sound is held back for as long as sound takes to cover the distance to the camera, in the air of
    /// the place: about a second for every 340 metres at sea level on Kerbin. Where there is no air the game
    /// plays its bang anyway, and it is left at once as it was.
    /// </summary>
    public sealed class Blast : MonoBehaviour
    {
        // Filled in by Recipe. Public so that they survive the game copying the object.
        public float strength;
        public bool stockVolume;
        // (since 0.8.0, filled in by the Air once it knows what went off: its charge in kilograms of TNT, whether near the ground,
        // and how long its fire burns; for its own bang, see Bangs)
        public float charge = -1f, fireSeconds;
        public bool nearGround;
        AudioSource own;
        static readonly AnimationCurve Level = AnimationCurve.Constant(0f, 1f, 1f);

        float age;
        AudioSource bang;
        bool held, wasMute;
        bool hushed;                // the warm-up's (Air.WarmUp): never heard at all
        float wait;                 // seconds until the sound gets to the camera
        float lasts = 12f;          // long enough for the rumble to end

        static AudioListener ears;
#if DEV
        static readonly System.Collections.Generic.List<string> heard = new System.Collections.Generic.List<string>();
        float far;
        /// <summary>(For the development build: the last few bangs: how far off each was, how long its sound was to take, and when it was really started.)</summary>
        public static string Heard() { string all = string.Join("\n", heard.ToArray()); heard.Clear(); return all.Length > 0 ? all : "none"; }
#endif

        void Awake()
        {
            // The game places the copy only after making it, so the Air looks at it at the end of this frame.
            Air.Newborn(this);
            // (And it starts the bang straight after making the copy, before this can know how far off it is: so the copy is
            // made deaf first, and what was started is started again when its time comes.)
            // (The one the Air sets off out of sight at the start of each flight, to do the first explosion's first-time work ahead
            // (Air.WarmUp), is never heard, whatever the air where it is. Held back only as any other, it was heard wherever there was
            // no air to carry its sound: from a camera high up it goes off 300 m over it, so in orbit in no air at all, and the game's
            // bang was let out at once, for the six frames the warm-up keeps it: "Whenever you revert the flight or load into a new
            // world, it plays an explosion sound for like a quarter of a second" (the owner, 2026-10-21). Made deaf, and its loudness
            // nothing, before the game starts its crack on it.)
            if (Air.WarmingNow)
            {
                hushed = true;
                bang = GetComponent<AudioSource>();
                if (bang != null) { bang.mute = true; bang.volume = 0f; }
                return;
            }
            if (!Settings.SoundTravels) return;
            bang = GetComponent<AudioSource>();
            if (bang == null) return;
            wasMute = bang.mute;
            bang.mute = true;
            held = true;
        }

        void Start()
        {
            if (bang == null) bang = GetComponent<AudioSource>();
            if (hushed)
            {
                if (bang != null) bang.Stop();
#if DEV
                heard.Add("the warm-up's bang, " + Distance().ToString("F0") + " m off, where sound would take " + Travel(transform.position).ToString("F2") + " s: not heard");
#endif
                return;
            }
            if (stockVolume && bang != null) { bang.pitch = Random.Range(0.8f, 1.2f); bang.volume = GameSettings.SHIP_VOLUME; }
            if (!held) return;
            wait = Travel(transform.position);
#if DEV
            Transform to = Ears();
            far = to != null ? (to.position - transform.position).magnitude : -1f;
            if (wait < 0.06f) heard.Add("a bang " + far.ToString("F0") + " m off (the listener is on " + (to != null ? to.name : "nothing") + "): sound would take " + wait.ToString("F2") + " s, so it is left as the game started it");
#endif
            // (near enough that nobody could tell: as the game has it)
            if (wait < 0.06f) { bang.mute = wasMute; held = false; return; }
            bang.Stop();
            lasts += wait;
        }

        /// <summary>
        /// Where the game is listening from: the one listener that is switched on (the camera's, outside the craft or in the
        /// cockpit). There are others that are not: every crew portrait's camera carries one.
        /// </summary>
        static Transform Ears()
        {
            if (ears == null || !ears.isActiveAndEnabled)
            {
                ears = null;
                foreach (AudioListener one in FindObjectsOfType<AudioListener>()) if (one != null && one.isActiveAndEnabled) { ears = one; break; }
            }
            return ears != null ? ears.transform : Camera.main != null ? Camera.main.transform : null;
        }

        /// <summary>How far the camera is from the blast, metres.</summary>
        float Distance()
        {
            Transform to = Ears();
            return to != null ? (to.position - transform.position).magnitude : 1000f;
        }

        /// <summary>(Since 0.8.0, by the Air once it knows what went off.) What went off, and its own bang begun to be made.</summary>
        public void Knows(float kilograms, bool ground, float burns)
        {
            charge = kilograms; nearGround = ground; fireSeconds = burns;
            if (Settings.OwnBang && Settings.SoundTravels && held && charge > 0f) Bangs.Ask(charge, Distance(), nearGround, fireSeconds);
        }

        /// <summary>How long sound takes from there to the camera, in the air of the place (nothing where there is no air to carry it).</summary>
        static float Travel(Vector3 from)
        {
            Transform to = Ears();
            CelestialBody body = FlightGlobals.currentMainBody;
            if (to == null || body == null || !body.atmosphere) return 0f;
            double pressure = FlightGlobals.getStaticPressure((Vector3d)from, body);
            double density = pressure > 0 ? FlightGlobals.getAtmDensity(pressure, FlightGlobals.getExternalTemperature((Vector3d)from, body), body) : 0.0;
            if (pressure <= 0 || density <= 0) return 0f;
            float speed = Mathf.Clamp((float)body.GetSpeedOfSound(pressure, density), 80f, 900f);
            return Mathf.Min((to.position - from).magnitude / speed, 30f);
        }

        /// <summary>
        /// Its own bang (see Bangs), on a source of its own, to begin that many seconds from now by the sound's clock. Its loudness
        /// is set here and not left to the speaker's fall-off with distance, which did not act on it (recorded: as loud at 1500 m
        /// as at 300 m, where the game's fell 12 dB): as loud as it goes up to a distance that grows as the cube root of the charge
        /// (300 m for a tonne of TNT), then falling as one over the distance, as a blast wave's pressure does. The speaker is still
        /// set in the world, for the side it is heard from.
        /// </summary>
        void Own(AudioClip made, float after)
        {
            if (own == null) own = gameObject.AddComponent<AudioSource>();
            own.clip = made;
            own.playOnAwake = false; own.loop = false; own.dopplerLevel = 0f; own.priority = 32;
            own.spatialBlend = 1f;
            own.rolloffMode = AudioRolloffMode.Custom;
            own.maxDistance = 30000f;
            own.SetCustomCurve(AudioSourceCurveType.CustomRolloff, Level);
            float near = Mathf.Clamp(30f * Mathf.Pow(charge, 1f / 3f), 10f, 800f), d = Distance();
            own.volume = GameSettings.SHIP_VOLUME * Mathf.Min(1f, near / Mathf.Max(d, 1f));
#if DEV
            heard.Add("  its own: " + d.ToString("F0") + " m off, " + charge.ToString("F0") + " kg, full up to " + near.ToString("F0") + " m, volume " + own.volume.ToString("F3"));
#endif
            own.pitch = Random.Range(0.94f, 1.06f);
            own.PlayScheduled(AudioSettings.dspTime + after);
            if (bang != null) { bang.Stop(); bang.mute = true; }
            lasts = Mathf.Max(lasts, age + after * Mathf.Max(Time.timeScale, 0.01f) + made.length / Mathf.Max(own.pitch, 0.5f) + 0.5f);
        }

        void Update()
        {
            age += Time.deltaTime;
            // (its own bang is begun a little ahead on the sound's own clock, so that it starts as the sound arrives and not up to a frame
            // and a sound buffer after it, which is heard close by, where the wait is short: measured 0.12 s late at 300 m before)
            if (held && Settings.OwnBang && charge > 0f && age >= wait - Mathf.Clamp(2f * Time.deltaTime, 0.1f, 0.3f))
            {
                AudioClip made = Bangs.Clip(charge, Distance(), nearGround);
                if (made != null)
                {
                    held = false;
                    float after = Mathf.Max(0f, wait - age - Bangs.Lead) / Mathf.Max(Time.timeScale, 0.01f);
#if DEV
                    heard.Add("a bang " + far.ToString("F0") + " m off: sound takes " + wait.ToString("F2") + " s, its own bang set for " + (age + after).ToString("F2") + " s after the flash");
#endif
                    Own(made, after);
                }
            }
            if (held && age >= wait)
            {
                held = false;
#if DEV
                heard.Add("a bang " + far.ToString("F0") + " m off: sound takes " + wait.ToString("F2") + " s, started " + age.ToString("F2") + " s after the flash");
#endif
                if (bang != null)
                {
                    bang.mute = wasMute;
                    // (the long rumble the effect carries, if it was to start by itself; and on top of it the crack the game chose for
                    // an explosion of this strength, which it keeps a note of on the copy)
                    if (bang.clip != null && bang.playOnAwake) bang.Play();
                    FXObjectPhoneHome note = GetComponent<FXObjectPhoneHome>();
                    if (note != null && note.parent != null && note.parent.effectSound != null) bang.PlayOneShot(note.parent.effectSound, GameSettings.SHIP_VOLUME);
                }
            }
            if (age > lasts) Destroy(gameObject);
        }
    }
}
