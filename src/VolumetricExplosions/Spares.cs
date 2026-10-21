using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace VolumetricExplosions
{
    /// <summary>
    /// The grids and volume textures of patches of air that have ended, kept for the next patches to begin with (2026-10-18).
    ///
    /// A patch holds ten megabytes of grids (twenty where the mod's library is not, and the grids are made in C#) and a dozen
    /// volume textures, and each patch made all of them new, at once, on the game's thread. At the first lift-off of a game
    /// two patches began in the one frame, the engines' flame and the pad's trench, and making their grids, and the
    /// collecting of garbage that so much new memory brought on, were the most of a stall of a fifth of a second. Patches come
    /// and go all through an explosion, too.
    ///
    /// Kept: those of three patches (Most), no more, and let go when the scene goes. Handed out cleared, as new ones are; a
    /// texture's pixels are not cleared, as a patch writes the whole of each before it is drawn (see Site.ShowVolume).
    ///
    /// And made ahead (Site.TopUp): while a rocket climbs its trail's patches begin every few seconds and none has ended yet, so
    /// there were none put away to take, and each new one made its grids anew (30 ms) and sent its new textures to the card the
    /// first time (10 ms). So two patches' worth of grids are kept ready, made on a worker thread, and a patch's worth of
    /// textures, made here a frame at a time and sent to the card as they are made. The grids are kept under a lock (the
    /// worker gives, the game's thread takes); the textures are the game's thread's alone.
    /// </summary>
    public static class Spares
    {
        static int reused, made, texturesReused, texturesMade;
        /// <summary>(For the development build's report: how many patches have been made, and of them how many ride on a ship.)</summary>
        public static int Patches, Riding;

        /// <summary>
        /// The most memory kept in grids, bytes: three patches' worth where the library makes the grids (a patch is some 15 MB, most of
        /// it the sixty grids that carry its smoke on between grids), one and a half where C# does. (No more than the game's memory
        /// had grown to hold them anyway: the runtime's collector does not give what it frees back to the system.)
        /// </summary>
        const long Most = 48L << 20;
        /// <summary>The most textures kept of each size and kind: nine patches' worth (a patch has five of the one kind), as the warm-up
        /// makes (see Site.PutByTextures).</summary>
        const int MostTextures = 48;

        static long kept;
        static readonly object locked = new object();
        static readonly List<Action> drops = new List<Action>();
        static readonly List<Func<string>> says = new List<Func<string>>();
        static readonly Dictionary<string, Stack<Texture3D>> textures = new Dictionary<string, Stack<Texture3D>>();
        static readonly Dictionary<Texture3D, string> keyOf = new Dictionary<Texture3D, string>();     // (each texture handed out, by the size and kind it was asked for as)

        static class Of<T> where T : struct
        {
            public static readonly Dictionary<int, Stack<T[]>> Kept = new Dictionary<int, Stack<T[]>>();
            public static readonly int Size = Marshal.SizeOf(typeof(T));
            static bool listed;

            // (under the lock)
            public static void List()
            {
                if (listed) return;
                listed = true;
                drops.Add(() => Kept.Clear());
                says.Add(() =>
                {
                    string t = "";
                    foreach (KeyValuePair<int, Stack<T[]>> k in Kept) t += " " + typeof(T).Name + "[" + k.Key + "] x" + k.Value.Count;
                    return t;
                });
            }
        }

        /// <summary>An array of so many, cleared: one put away (cleared as it was, see Give), or a new one.</summary>
        public static T[] Take<T>(int n) where T : struct
        {
            T[] a = null;
            lock (locked)
            {
                if (Of<T>.Kept.TryGetValue(n, out Stack<T[]> stack) && stack.Count > 0)
                {
                    a = stack.Pop();
                    kept -= (long)n * Of<T>.Size;
                    reused++;
                }
                else made++;
            }
            return a ?? new T[n];
        }

        /// <summary>How many arrays of so many are kept.</summary>
        public static int Count<T>(int n) where T : struct
        {
            lock (locked) return Of<T>.Kept.TryGetValue(n, out Stack<T[]> stack) ? stack.Count : 0;
        }

        /// <summary>How many volume textures of this size and kind are kept.</summary>
        public static int Count(int side, TextureFormat format) => textures.TryGetValue(side + " " + format, out Stack<Texture3D> stack) ? stack.Count : 0;
        public static int Count(int side, GraphicsFormat format) => textures.TryGetValue(side + " g" + format, out Stack<Texture3D> stack) ? stack.Count : 0;

        /// <summary>
        /// Put an array away for another patch (or let it go, where enough are kept already). It is cleared first, on a worker:
        /// cleared as it was taken, on the game's thread, a new patch's 15 MB of them took 1 to 2 ms of that frame (2026-10-18).
        /// (clean: a new array, nothing to clear.)
        /// </summary>
        public static void Give<T>(T[] a, bool clean = false) where T : struct
        {
            if (a == null) return;
            long size = (long)a.Length * Of<T>.Size;
            int round;
            lock (locked)
            {
                if (kept + size > Most) return;
                kept += size;
                round = dropped;
                if (clean) { Put(a); return; }
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                Array.Clear(a, 0, a.Length);
                lock (locked) { if (round == dropped) Put(a); }
            });
        }

        // (under the lock)
        static void Put<T>(T[] a) where T : struct
        {
            Of<T>.List();
            if (!Of<T>.Kept.TryGetValue(a.Length, out Stack<T[]> stack)) Of<T>.Kept[a.Length] = stack = new Stack<T[]>();
            stack.Push(a);
        }

        static int dropped;           // (how many times everything has been let go: an array still being cleared then is let go too)

        /// <summary>Every array of a list of them put away, and the list emptied.</summary>
        public static void GiveAll<T>(T[][] list) where T : struct
        {
            if (list == null) return;
            for (int k = 0; k < list.Length; k++) { Give(list[k]); list[k] = null; }
        }

        /// <summary>A volume texture of this size and kind: one put away, or a new one.</summary>
        public static Texture3D Volume(int side, TextureFormat format, FilterMode filter)
        {
            string key = side + " " + format;
            Texture3D t = TakeTexture(key);
            if (t != null) texturesReused++;
            else { t = new Texture3D(side, side, side, format, false); texturesMade++; }
            keyOf[t] = key;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = filter;
            return t;
        }

        /// <summary>The same, of a kind given as the graphics card has it.</summary>
        public static Texture3D Volume(int side, GraphicsFormat format, FilterMode filter)
        {
            string key = side + " g" + format;
            Texture3D t = TakeTexture(key);
            if (t != null) texturesReused++;
            else { t = new Texture3D(side, side, side, format, TextureCreationFlags.None); texturesMade++; }
            keyOf[t] = key;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = filter;
            return t;
        }

        /// <summary>A new volume texture of this size and kind, sent to the card at once (its first sending is the dear one), and put by.</summary>
        public static void PutByNew(int side, TextureFormat format, FilterMode filter)
        {
            var t = new Texture3D(side, side, side, format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = filter };
            keyOf[t] = side + " " + format;
            texturesMade++;
            t.Apply(false);
            Sending.Sent(t);
            Give(t);
        }

        public static void PutByNew(int side, GraphicsFormat format, FilterMode filter)
        {
            var t = new Texture3D(side, side, side, format, TextureCreationFlags.None) { wrapMode = TextureWrapMode.Clamp, filterMode = filter };
            keyOf[t] = side + " g" + format;
            texturesMade++;
            t.Apply(false);
            Sending.Sent(t);
            Give(t);
        }

        static Texture3D TakeTexture(string key)
        {
            if (!textures.TryGetValue(key, out Stack<Texture3D> stack)) return null;
            // (one the library knows the name of first, see Sending: its grids go without the game's thread waiting)
            if (stack.Count > 0 && !Sending.Known(stack.Peek()))
            {
                Texture3D[] all = stack.ToArray();           // (top first)
                for (int k = 0; k < all.Length; k++)
                    if (all[k] != null && Sending.Known(all[k]))
                    {
                        Texture3D found = all[k];
                        stack.Clear();
                        for (int j = all.Length - 1; j >= 0; j--) if (j != k && all[j] != null) stack.Push(all[j]);
                        return found;
                    }
            }
            while (stack.Count > 0)
            {
                Texture3D t = stack.Pop();
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Put a volume texture away for another patch (or destroy it, where enough are kept already).</summary>
        public static void Give(Texture3D t)
        {
            if (t == null) return;
            if (!keyOf.TryGetValue(t, out string key)) { Sending.Forget(t); UnityEngine.Object.Destroy(t); return; }
            if (!textures.TryGetValue(key, out Stack<Texture3D> stack)) textures[key] = stack = new Stack<Texture3D>();
            if (stack.Count >= MostTextures) { keyOf.Remove(t); Sending.Forget(t); UnityEngine.Object.Destroy(t); return; }
            stack.Push(t);
        }

        /// <summary>Let everything kept go (the scene is going).</summary>
        public static void Drop()
        {
            lock (locked)
            {
                foreach (Action drop in drops) drop();
                kept = 0;
                dropped++;
            }
            Native.Drop();
            foreach (Stack<Texture3D> stack in textures.Values) foreach (Texture3D t in stack) if (t != null) { Sending.Forget(t); UnityEngine.Object.Destroy(t); }
            textures.Clear();
            keyOf.Clear();
        }

        static string Kinds()
        {
            string t = "";
            lock (locked) foreach (Func<string> say in says) t += say();
            return t;
        }

        /// <summary>(For the development build: what is kept.)</summary>
        public static string Report()
        {
            int count = 0;
            foreach (Stack<Texture3D> stack in textures.Values) count += stack.Count;
            long now;
            lock (locked) now = kept;
            return (now / 1048576.0).ToString("F1") + " MB of grids and " + count + " volume textures kept for the next patches; so far " + reused + " grids and " + texturesReused + " textures used again, "
                   + made + " grids and " + texturesMade + " textures made new; " + Patches + " patches made (" + Riding + " riding on ships);" + Kinds();
        }
    }
}
