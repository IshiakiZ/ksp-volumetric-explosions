using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// The grid maker in C (Native/vfxgrid.c), found, checked and loaded here once.
    ///
    /// Making a grid is nearly all the processor time the mod takes. In the game's own runtime it is so
    /// much work that it has to be spread over every core to keep up, and that slows the game's own
    /// threads: measured with a four-tonne fire burning, a third of every frame went on it. In C the
    /// same sums take a fraction of the time on one thread. So where the library is there, and is the one
    /// built with this version of the mod, the grids are made by it; everywhere else (another kind of
    /// computer, a library the system will not load) by the C# in Site.cs, as before. Either way the
    /// picture is the same.
    ///
    /// It is never loaded while macOS has it marked as downloaded from the internet: the system would
    /// stop the game to ask about it. The mod says so in the log once and carries on without.
    /// </summary>
    public static class Native
    {
        /// <summary>What a grid is to be made from and where it is to go: the same fields in the same order as Grid in vfxgrid.c.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Grid
        {
            public int size, g, a, levels, carried;
            public int count, drawn, sunUp, halves, floored;
            public IntPtr particles;
            public float x0, y0, z0, dX, dY, dZ;
            public float keepX0, keepX1, keepY0, keepY1, keepZ0, keepZ1, fadeX, fadeY, fadeZ;
            public float leastReach, thick, hold, goesX, goesY, goesZ, ahead;
            public float sunX, sunY, sunZ, repeat;
            public float thickest, thickestFlame, something, restFar, mostBefore, mostAbove;
            public float fastest;
            public IntPtr sunThrough, skyThrough;
            public IntPtr carry;
            public IntPtr cellsA, cellsB, cellsC, cellsD;
            public IntPtr cellsRest;
            public IntPtr cellsClear;
            public IntPtr floors;
            public IntPtr cellsE;
            public IntPtr strain;
            public IntPtr bells;
            public long ns0, ns1, ns2, ns3, ns4, ns5, ns6, ns7;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Number();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Begin();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void End(IntPtr work);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Maker(IntPtr work, ref Grid grid);

        [DllImport("libSystem.dylib", EntryPoint = "dlopen")] static extern IntPtr MacOpen(string path, int mode);
        [DllImport("libSystem.dylib", EntryPoint = "dlsym")] static extern IntPtr MacFind(IntPtr library, string name);
        [DllImport("libSystem.dylib", EntryPoint = "getxattr")] static extern IntPtr MacNote(string path, string name, IntPtr value, UIntPtr size, uint position, int options);
        [DllImport("libdl.so.2", EntryPoint = "dlopen")] static extern IntPtr LinuxOpen(string path, int mode);
        [DllImport("libdl.so.2", EntryPoint = "dlsym")] static extern IntPtr LinuxFind(IntPtr library, string name);
        [DllImport("kernel32", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern IntPtr WindowsOpen(string path);
        [DllImport("kernel32", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)] static extern IntPtr WindowsFind(IntPtr library, string name);

        static bool tried;
        static Begin begin;
        static End end;
        static Maker maker;

        /// <summary>Whether grids can be made by the library. (Why not, if not, is in the log.)</summary>
        public static bool Ready
        {
            get
            {
                if (!tried) { tried = true; Load(); }
                return maker != null;
            }
        }

        /// <summary>Room for one site's grids to be worked out in; nothing if there is none to be had.</summary>
        public static IntPtr New() => Ready ? begin() : IntPtr.Zero;

        public static void Free(IntPtr work)
        {
            if (work != IntPtr.Zero && end != null) end(work);
        }

        /// <summary>Make a grid. Nought if it was made.</summary>
        public static int Make(IntPtr work, ref Grid grid) => maker(work, ref grid);

        static void Load()
        {
            try
            {
                if (!Settings.Native) { Addon.Log("grids are made by the mod's own code (native = false in settings.cfg)"); return; }
                bool mac = Application.platform == RuntimePlatform.OSXPlayer, linux = Application.platform == RuntimePlatform.LinuxPlayer, windows = Application.platform == RuntimePlatform.WindowsPlayer;
                if (!mac && !linux && !windows) return;          // (no library is built for anything else)
                if (IntPtr.Size != 8) return;                    // (nor for a game that is not a 64-bit one)
                string path = Find(mac ? ".dylib" : linux ? ".so" : ".dll");
                if (path == null) return;                        // (a copy of the mod without one for this system: the mod's own code makes the grids)
                int system = mac ? 0 : linux ? 1 : 2;
                // (-1: no such note on the file)
                if (mac && MacNote(path, "com.apple.quarantine", IntPtr.Zero, UIntPtr.Zero, 0, 0).ToInt64() >= 0)
                {
                    Addon.Log("grids are made by the mod's own code: macOS has " + Path.GetFileName(path) + " marked as downloaded from the internet and would not load it without asking. " +
                              "(The mod works the same without it, at a lower frame rate while there is smoke. See the README for how to let it be used.)");
                    return;
                }
                IntPtr library = mac ? MacOpen(path, 2 | 4) : linux ? LinuxOpen(path, 2) : WindowsOpen(path);       // (now, and for this mod alone)
                if (library == IntPtr.Zero) { Addon.Log("grids are made by the mod's own code: " + Path.GetFileName(path) + " would not load"); return; }
                Number signature = Call<Number>(library, system, "vfx_signature"), sizeOfP = Call<Number>(library, system, "vfx_size_of_p"), sizeOfGrid = Call<Number>(library, system, "vfx_size_of_grid");
                Begin first = Call<Begin>(library, system, "vfx_new");
                End last = Call<End>(library, system, "vfx_free");
                Maker make = Call<Maker>(library, system, "vfx_grid");
                if (signature == null || sizeOfP == null || sizeOfGrid == null || first == null || last == null || make == null)
                {
                    Addon.Log("grids are made by the mod's own code: " + Path.GetFileName(path) + " is not this mod's library");
                    return;
                }
                // The library reads the particles straight out of the game's memory: it must have been built from this very description of one.
                if (signature() != Signature() || sizeOfP() != Marshal.SizeOf(typeof(Site.P)) || sizeOfGrid() != Marshal.SizeOf(typeof(Grid)))
                {
                    Addon.Log("grids are made by the mod's own code: " + Path.GetFileName(path) + " was built for another version of the mod");
                    return;
                }
                begin = first; end = last; maker = make;
                Addon.Log("grids are made by " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                begin = null; end = null; maker = null;
                Addon.Log("grids are made by the mod's own code (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>The library's file. (The development build leaves a fresh one beside the last under a new name each time it is built: the newest is taken.)</summary>
        static string Find(string ending)
        {
            string folder = Settings.Folder + "PluginData";
            if (!Directory.Exists(folder)) return null;
            string best = null;
            DateTime newest = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(folder, "vfxgrid*" + ending))
            {
                DateTime written = File.GetLastWriteTimeUtc(file);
                if (best == null || written > newest) { best = file; newest = written; }
            }
            return best;
        }

        static T Call<T>(IntPtr library, int system, string name) where T : class
        {
            IntPtr at = system == 0 ? MacFind(library, name) : system == 1 ? LinuxFind(library, name) : WindowsFind(library, name);
            return at == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(at, typeof(T)) as T;
        }

        /// <summary>A number made from the kinds and names of a particle's fields, in order: the same sum as tools/native/genp.py gives the library.</summary>
        static int Signature()
        {
            FieldInfo[] fields = typeof(Site.P).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (x, y) => x.MetadataToken.CompareTo(y.MetadataToken));
            uint sum = 0x811C9DC5;
            foreach (FieldInfo field in fields)
            {
                Type kind = field.FieldType;
                string text = (kind == typeof(float) ? "float" : kind == typeof(byte) ? "byte" : kind == typeof(uint) ? "uint" : kind == typeof(int) ? "int" : kind.Name) + " " + field.Name + ";";
                foreach (char letter in text) sum = unchecked((sum ^ (byte)letter) * 0x01000193);
            }
            return unchecked((int)sum);
        }
    }
}
