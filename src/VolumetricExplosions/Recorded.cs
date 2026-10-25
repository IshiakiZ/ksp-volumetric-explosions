using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VolumetricExplosions
{
    /// <summary>
    /// (Since 0.9.0.) Recordings of real blasts and fires, from Sonniss's #GameAudioGDC bundles (Gamemaster Audio's Explosion
    /// Sound Pack, Lukas Tvrdon's Distant Blast, Rock The Speakerbox's Broken, Pole Position's Burning House Library and Small
    /// Fire, Digital Rain Lab's Driving Forces), heard where the mod made its own before: a bang's roar and rumble is the
    /// recording nearest in kind to how far off the camera is (close: a crack and the fireball's roar; a few hundred metres: a
    /// boom; kilometres: a thud and the rolling echo), made deeper the bigger the blast, under the made crack and through the
    /// air's filter for the distance (see Bangs). "recorded = false" in the settings goes back to the made roar.
    ///
    /// Their licence lets the mod play them but not hand them out as sound files, so they are one packed file,
    /// PluginData/sounds.pack, read here (this mod stands without Keystone; the format is Keystone's Sounds', made by
    /// tools/sounds/pack.py: "KSND", a version byte, the count, for each its name, start and length; then each sound Ogg Vorbis
    /// turned by a running key from its name). A sound is turned back into a file in the game's temporary folder, loaded by
    /// Unity, and the file removed. Built without the recordings (a repository's build: they are never in one), there is no
    /// pack, and the made sounds are heard, as in 0.8.0.
    /// </summary>
    public static class Recorded
    {
        const string Mod = "VolumetricExplosions";
        static readonly Dictionary<string, KeyValuePair<long, int>> where = new Dictionary<string, KeyValuePair<long, int>>(StringComparer.OrdinalIgnoreCase);
        static string pack;
        static bool read, done;
        // (what loads them: a flight's Addon, which goes with its scene; if it goes before they are all in, the next flight's
        // takes up the rest. Until 2026-10-24 they were begun once in a game and handed over only at the end: a revert or a
        // quickload in the first seconds of the first flight left the blasts with no recordings for the rest of the game.)
        static MonoBehaviour loader;
        static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        static readonly List<float[]> nearKept = new List<float[]>(), betweenKept = new List<float[]>(), farKept = new List<float[]>();

        /// <summary>The blasts' recordings by distance, their samples (mono, 44.1 kHz) read on the game's thread for the bangs that
        /// are made on other threads; empty until they are loaded, and if there is no pack.</summary>
        static volatile float[][] near = new float[0][], between = new float[0][], far = new float[0][];

        /// <summary>The rate the recordings are at.</summary>
        public const int Rate = 44100;

        /// <summary>Whether there are recordings to be heard (the pack, and the setting).</summary>
        public static bool On => Settings.Recorded && pack != null;

        /// <summary>A loaded recording by name (a fire's loop), or null.</summary>
        public static AudioClip Clip(string name) => clips.TryGetValue(name, out AudioClip clip) ? clip : null;

        /// <summary>
        /// A blast's recording for a camera so far off (metres), chosen by seed among those of its kind; null where there is none.
        /// Close by (under 250 m) a near one, then a boom up to 1.5 km, then a far one.
        /// </summary>
        public static float[] For(float distance, int seed)
        {
            float[][] kind = distance < 250f ? near : distance < 1500f ? between : far;
            if (kind.Length == 0) kind = near.Length > 0 ? near : between.Length > 0 ? between : far;
            return kind.Length == 0 ? null : kind[(seed & 0x7FFFFFFF) % kind.Length];
        }

        /// <summary>Begin loading them (from each flight's start), or go on with those not yet loaded; nothing once all are in.</summary>
        public static void Begin(MonoBehaviour host)
        {
            if (done || loader != null) return;
            if (read) { if (pack != null) { loader = host; host.StartCoroutine(LoadAll()); } return; }
            read = true;
            string path = KSPUtil.ApplicationRootPath + "GameData/" + Mod + "/PluginData/sounds.pack";
            try
            {
                if (!File.Exists(path)) { Addon.Log("no recordings (PluginData/sounds.pack): the made sounds are heard"); return; }
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "KSND" || reader.ReadByte() != 1) throw new Exception("not a pack of version 1");
                    int count = reader.ReadInt32();
                    for (int n = 0; n < count; n++)
                    {
                        string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
                        long start = reader.ReadInt64();
                        int length = reader.ReadInt32();
                        where[name] = new KeyValuePair<long, int>(start, length);
                    }
                }
                pack = path;
            }
            catch (Exception problem)
            {
                Addon.Log("the recordings could not be read (" + problem.Message + "): the made sounds are heard");
                pack = null;
                return;
            }
            loader = host;
            host.StartCoroutine(LoadAll());
        }

        static IEnumerator LoadAll()
        {
            var names = new List<string>(where.Keys);
            names.Sort(StringComparer.Ordinal);
            int lastIn = -1;
            foreach (string name in names)
            {
                if (clips.ContainsKey(name) || tried.Contains(name)) continue;
                // (one in a frame at most: Unity unpacks each on the game's thread as it hands it over, half a millisecond for
                // each second of sound, and its request for a file can be done at once, so that many came in on one frame:
                // Keystone's Sounds, 2026-10-24)
                if (Time.frameCount == lastIn) yield return null;
                AudioClip clip = null;
                yield return Load(name, c => clip = c);
                lastIn = Time.frameCount;
                tried.Add(name);
                if (clip == null) continue;
                clips[name] = clip;
                // (the blasts' samples kept for the bangs made on other threads: GetData only on this one; each handed over as it
                // comes, so that the first blasts of a flight have what is in by then)
                List<float[]> into = name.StartsWith("blast-near") ? nearKept : name.StartsWith("boom") ? betweenKept : name.StartsWith("blast-far") ? farKept : null;
                if (into == null) continue;
                if (clip.channels != 1 || clip.frequency != Rate) { Addon.Log(name + " is not mono at " + Rate + " Hz: left out"); continue; }
                var samples = new float[clip.samples];
                if (!clip.GetData(samples, 0)) continue;
                into.Add(samples);
                near = nearKept.ToArray(); between = betweenKept.ToArray(); far = farKept.ToArray();
            }
            done = true;
            loader = null;
            Addon.Log("recordings loaded: " + clips.Count + " (blasts near " + near.Length + ", between " + between.Length + ", far " + far.Length + ")");
        }

        // (those asked for already, whether or not they could be loaded: not asked for again)
        static readonly HashSet<string> tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The running key (Keystone's Sounds.Turn), applied in place: twice gives the bytes back.</summary>
        static void Turn(byte[] bytes, string name)
        {
            uint x = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(name)) { x ^= b; x *= 16777619u; }
            if (x == 0) x = 0x9E3779B9u;
            for (int i = 0; i < bytes.Length; i++)
            {
                if ((i & 3) == 0) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; }
                bytes[i] ^= (byte)(x >> ((i & 3) * 8));
            }
        }

        static IEnumerator Load(string name, Action<AudioClip> done)
        {
            AudioClip clip = null;
            string file = null;
            try
            {
                KeyValuePair<long, int> at = where[name];
                byte[] bytes = new byte[at.Value];
                using (var stream = File.OpenRead(pack))
                {
                    stream.Seek(at.Key, SeekOrigin.Begin);
                    int got = 0;
                    while (got < bytes.Length)
                    {
                        int n = stream.Read(bytes, got, bytes.Length - got);
                        if (n <= 0) throw new EndOfStreamException();
                        got += n;
                    }
                }
                Turn(bytes, name);
                string folder = Path.Combine(Application.temporaryCachePath, "volumetric-explosions-sounds");
                Directory.CreateDirectory(folder);
                file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".ogg");
                File.WriteAllBytes(file, bytes);
            }
            catch (Exception problem)
            {
                Addon.Log(name + " could not be read from the pack: " + problem.Message);
                file = null;
            }
            if (file != null)
            {
                string address;
                try { address = new Uri(file).AbsoluteUri; }
                catch (Exception) { address = KSPUtil.ApplicationFileProtocol + file; }
                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(address, AudioType.OGGVORBIS))
                {
                    // (decoded whole on loading: the blasts' samples are read from them, and the fires' loops are short)
                    if (request.downloadHandler is DownloadHandlerAudioClip handler) { handler.compressed = false; handler.streamAudio = false; }
                    yield return request.SendWebRequest();
                    if (!request.isNetworkError && !request.isHttpError)
                    {
                        try { clip = DownloadHandlerAudioClip.GetContent(request); }
                        catch (Exception) { clip = null; }
                    }
                    if (clip != null)
                    {
                        float until = Time.realtimeSinceStartup + 5f;
                        while (clip.loadState == AudioDataLoadState.Loading && Time.realtimeSinceStartup < until) yield return null;
                        if (clip.loadState == AudioDataLoadState.Failed || clip.samples <= 0) { UnityEngine.Object.Destroy(clip); clip = null; }
                    }
                }
                try { File.Delete(file); }
                catch (Exception) { }
            }
            if (clip != null) { clip.name = "Volumetric Explosions " + name; clip.hideFlags = HideFlags.DontUnloadUnusedAsset; }
            done(clip);
        }

#if DEV
        /// <summary>(Development build) what is loaded.</summary>
        public static string Says() => (pack == null ? "no pack" : clips.Count + " of " + where.Count + " loaded") + "; blasts near " + near.Length + ", between " + between.Length + ", far " + far.Length + "; setting " + Settings.Recorded;
#endif
    }
}
