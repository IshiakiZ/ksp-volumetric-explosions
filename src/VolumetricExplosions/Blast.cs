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

        float age;
        AudioSource bang;
        bool held, wasMute;
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

        void Update()
        {
            age += Time.deltaTime;
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
