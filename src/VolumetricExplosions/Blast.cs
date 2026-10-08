using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// The object the game makes a copy of when something blows up. It carries the bang and nothing to
    /// see: as soon as it appears it reports to the <see cref="Air"/>, which works out what kind of
    /// explosion this is and sets the particles off where the game has put it.
    /// </summary>
    public sealed class Blast : MonoBehaviour
    {
        // Filled in by Recipe. Public so that they survive the game copying the object.
        public float strength;
        public bool stockVolume;

        float age;

        void Awake()
        {
            // The game places the copy only after making it, so the Air looks at it at the end of this frame.
            Air.Newborn(this);
        }

        void Start()
        {
            if (!stockVolume) return;
            AudioSource bang = GetComponent<AudioSource>();
            if (bang != null) { bang.pitch = Random.Range(0.8f, 1.2f); bang.volume = GameSettings.SHIP_VOLUME; }
        }

        void Update()
        {
            age += Time.deltaTime;
            if (age > 12f) Destroy(gameObject);        // long enough for the rumble to end
        }
    }
}
