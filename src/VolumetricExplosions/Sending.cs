using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricExplosions
{
    /// <summary>
    /// A patch's grids handed to the graphics card without the game's thread waiting for it, where the game draws with OpenGL (the
    /// Mac, Linux) and the mod's library is there (2026-10-18; Native/volumesend.c says why and how). Each grid is copied into the
    /// library's own memory here, a quarter of a millisecond for the whole of one, and sent from the render thread once a frame
    /// (Flush, at the end of the air's LateUpdate, before anything of the frame is drawn). Anywhere else (Direct3D, no library), and
    /// for a texture the card has never had (its first sending makes room for it there), Unity's own Apply, as before.
    /// </summary>
    public static class Sending
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Send(uint texture, int side, int kind, IntPtr data, int bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Take(double[] into);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Pointer();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Mode(int way);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Slowest(double[] into, int most);
        static Send send;
        static Take take;
        static Mode mode;
        static Slowest slowest;
        /// <summary>
        /// How the render thread hands the grids to the card (Native/volumesend.c): 0 straight from the library's memory, where the
        /// driver may wait for the card to finish reading a texture it is given anew; 1 through a buffer of the card's own, which
        /// the card copies from in its own order, with nothing to wait for. The second since 2026-10-18: on four climbs of the test
        /// rocket on this Mac, taken in turn, the render thread spent 0.47 ms a frame sending through the buffer against 0.74 ms
        /// straight, no texture held it up (straight, one 0.25 MB texture sent three frames before waited 4.1 ms for the card), and
        /// the frames were the same; its worst event is the copy of the frame's grids into the buffer, 4-6 ms for 15-18 MB.
        /// </summary>
        public static int Way = 1;
        static IntPtr onEvent;
        static bool tried, queued;
        static readonly Dictionary<Texture3D, uint> names = new Dictionary<Texture3D, uint>();

        /// <summary>Whether grids can be sent by the library here.</summary>
        public static bool Ready
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    try
                    {
                        // (OpenGL only: on Direct3D a texture's native pointer is not a GL name)
                        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLCore || !Settings.Native || !Native.Ready) return false;
                        IntPtr s = Native.Symbol("vs_send"), e = Native.Symbol("vs_event"), t = Native.Symbol("vs_take");
                        if (s == IntPtr.Zero || e == IntPtr.Zero) return false;
                        IntPtr eventFn = ((Pointer)Marshal.GetDelegateForFunctionPointer(e, typeof(Pointer)))();
                        if (eventFn == IntPtr.Zero) return false;
                        send = (Send)Marshal.GetDelegateForFunctionPointer(s, typeof(Send));
                        if (t != IntPtr.Zero) take = (Take)Marshal.GetDelegateForFunctionPointer(t, typeof(Take));
                        // (a library from before the two ways has neither: it sends straight, as it always did)
                        IntPtr m = Native.Symbol("vs_mode"), w = Native.Symbol("vs_slow");
                        if (m != IntPtr.Zero) { mode = (Mode)Marshal.GetDelegateForFunctionPointer(m, typeof(Mode)); Way = mode(Way); }
                        if (w != IntPtr.Zero) slowest = (Slowest)Marshal.GetDelegateForFunctionPointer(w, typeof(Slowest));
                        onEvent = eventFn;
                        Addon.Log("the smoke's grids are sent to the graphics card from its own thread");
                    }
                    catch (Exception ex) { send = null; Addon.Log("the smoke's grids are sent to the graphics card as before (" + ex.Message + ")"); }
                }
                return send != null;
            }
        }

        /// <summary>
        /// Send a grid to a volume texture that the card has had at least once (see Sent): kind 0 four bytes a texel, 1 one byte, 2
        /// four sixteen-bit numbers 0 to 1, 3 four half floats; data where the grid is (pinned), bytes how much. False if it was not
        /// queued: then send it as before.
        /// </summary>
        public static bool Put(Texture3D texture, int kind, IntPtr data, int bytes)
        {
            if (send == null || texture == null || data == IntPtr.Zero || TestOff) return false;
            if (!names.TryGetValue(texture, out uint name)) return false;
#if DEV
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int ok = send(name, texture.width, kind, data, bytes);
            Timed(1, t0);
            if (ok == 0) return false;
#else
            if (send(name, texture.width, kind, data, bytes) == 0) return false;
#endif
            queued = true;
            return true;
        }

        /// <summary>
        /// A texture has been sent through Unity once, so the card has room for it: from now on it can be sent here, once its name
        /// is known. Its name is not asked for now: asking makes the game's thread wait for the render thread to catch up, 2 ms
        /// at the pad and 8 to 17 ms while smoke is being drawn (44 times on a climb, 2026-10-18). So it is asked for later, out of
        /// sight (Name): in the explosions mod's warm-up, and once nothing has been drawn for a few seconds.
        /// </summary>
        public static void Sent(Texture3D texture)
        {
            if (texture == null || !Ready || names.ContainsKey(texture) || toName.Contains(texture)) return;
            toName.Add(texture);
        }

        static readonly List<Texture3D> toName = new List<Texture3D>();

        /// <summary>The names of so many of the textures sent once asked for (each waits for the render thread). How many are left.</summary>
        public static int Name(int most)
        {
            for (int k = toName.Count - 1; k >= 0 && most > 0; k--)
            {
                Texture3D t = toName[k];
                toName.RemoveAt(k);
                if (t == null || names.ContainsKey(t)) continue;
                most--;
                Ask(t);
            }
            return toName.Count;
        }

        static void Ask(Texture3D texture)
        {
            try
            {
#if DEV
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                IntPtr at = texture.GetNativeTexturePtr();
                Timed(0, t0);
#else
                IntPtr at = texture.GetNativeTexturePtr();
#endif
                if (at != IntPtr.Zero) names[texture] = (uint)at.ToInt64();
            }
            catch (Exception) { }
        }

        public static bool Known(Texture3D texture) => texture != null && names.ContainsKey(texture);

        /// <summary>A texture destroyed: forgotten.</summary>
        public static void Forget(Texture3D texture)
        {
            if (texture != null) { names.Remove(texture); toName.Remove(texture); }
            if (names.Count > 600)
            {
                var gone = new List<Texture3D>();
                foreach (Texture3D t in names.Keys) if (t == null) gone.Add(t);
                foreach (Texture3D t in gone) names.Remove(t);
            }
        }

        /// <summary>(The air, once a frame, after every patch has stepped.) What was queued this frame is sent, on the render thread.</summary>
        public static void Flush()
        {
            if (!queued || onEvent == IntPtr.Zero) return;
            queued = false;
#if DEV
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            GL.IssuePluginEvent(onEvent, 0);
            Timed(2, t0);
#else
            GL.IssuePluginEvent(onEvent, 0);
#endif
        }

        /// <summary>(For testing: every grid through Unity, as before, to compare.)</summary>
        public static bool TestOff;
#if DEV
        /// <summary>(Development build: "off" every grid through Unity; "straight" or "buffer" the render thread's two ways; anything else the render thread as it is.)</summary>
        public static string Switch(string on)
        {
            string w = (on ?? "").Trim();
            TestOff = w == "off";
            if ((w == "straight" || w == "buffer") && mode != null) Way = mode(w == "buffer" ? 1 : 0);
            return TestOff ? "grids through Unity" : "grids sent by the render thread, " + (Way == 1 ? "through a buffer of the card's own" : "straight from the library's memory");
        }
#endif

#if DEV
        // (what the game's thread spent: asking for names, copying grids in, issuing the event; in all, at worst, how many)
        static readonly double[] spent = new double[3], worst = new double[3];
        static readonly int[] calls = new int[3];
        static void Timed(int k, long from)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            spent[k] += ms; calls[k]++;
            if (ms > worst[k]) worst[k] = ms;
        }
        static string Own()
        {
            string[] what = { "names asked", "grids copied in", "events issued" };
            string t = "";
            for (int k = 0; k < 3; k++) { t += "; " + what[k] + " " + calls[k] + ": " + spent[k].ToString("F1") + " ms in all, " + worst[k].ToString("F2") + " at worst"; spent[k] = 0; worst[k] = 0; calls[k] = 0; }
            return t;
        }
#endif

        /// <summary>(For the development build: what has been sent since this was last asked.)</summary>
        public static string Report()
        {
            if (send == null) return "the smoke's grids are sent as before" + (tried ? "" : " (not tried yet)");
            if (take == null) return "sending from the render thread";
            var n = new double[8];
            take(n);
            return n[0] + " textures sent by the render thread in " + n[4] + " events (" + n[1] + " refused, " + n[2] + " replaced before they went, " + n[3] + " GL errors); "
                   + n[5].ToString("F1") + " ms of the render thread's in all, " + n[6].ToString("F2") + " ms at worst; " + names.Count + " textures known, " + n[7] + " waiting"
#if DEV
                   + Own()
#endif
                   + Slowest5();
        }

        /// <summary>
        /// The slowest of the render thread's sendings since the last report, slowest first: how long, how many textures and how much,
        /// how long its slowest texture took and how long before that texture had last been sent (a texture sent again within a
        /// frame or two is one the card may still have been reading), which way, and how many seconds ago.
        /// </summary>
        static string Slowest5()
        {
            if (slowest == null) return "";
            var o = new double[1 + 5 * 8];
            int n = slowest(o, 5);
            if (n == 0) return "";
            var b = new System.Text.StringBuilder("; the slowest:");
            for (int i = 0; i < n; i++)
            {
                int k = 1 + i * 8;
                b.Append(i == 0 ? " " : ", ").Append(o[k + 1].ToString("F1")).Append(" ms (").Append((int)o[k + 2]).Append(" textures, ").Append((o[k + 3] / 1048576.0).ToString("F1"))
                 .Append(" MB; its slowest ").Append(o[k + 4].ToString("F1")).Append(" ms for ").Append((o[k + 5] / 1048576.0).ToString("F2")).Append(" MB, ")
                 .Append(o[k + 6] < 0 ? "not sent before" : "sent " + o[k + 6].ToString("F0") + " ms before").Append("; ").Append(o[k + 7] > 0.5 ? "buffer" : "straight")
                 .Append(", ").Append(((o[0] - o[k]) / 1000.0).ToString("F1")).Append(" s ago)");
            }
            return b.ToString();
        }
    }
}
