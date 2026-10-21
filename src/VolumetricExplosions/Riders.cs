using System;
using System.Reflection;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// Where the craft are drawn this frame, against where their last physics step left them. Where the Smooth Running mod carries
    /// the craft on between the game's steps (each frame drawn at its own moment), it says how far through Keystone's Motion; the
    /// patches of air that go along with a ship (its flame, its young smoke) are carried on with that ship, or the flame stands
    /// behind its nozzle by up to a step: six metres at three hundred metres a second. Patches kept beside the ship flown in space
    /// (by an offset from it) are carried on with that ship, or they fall back by up to forty metres a frame in orbit. Looked up by
    /// name, once: this mod needs neither of those mods, and where they are not installed nothing is carried.
    /// </summary>
    static class Riders
    {
        static bool looked;
        static Func<bool> now;
        static Func<Vessel, Vector3, Vector3> carryWith;
        static Func<Vessel, Quaternion, Quaternion> turnWith;
        static Func<Vessel, Vector3> aheadOf;

        static void Look()
        {
            looked = true;
            try
            {
                // (a development build has Keystone in its own library; an installed game has it in its own)
                Type t = typeof(Riders).Assembly.GetType("Keystone.Motion", false);
                if (t == null)
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name != "Keystone") continue;
                        t = a.GetType("Keystone.Motion", false);
                        if (t != null) break;
                    }
                if (t == null) return;
                const BindingFlags any = BindingFlags.Public | BindingFlags.Static;
                MethodInfo with = t.GetMethod("CarryWith", any), turning = t.GetMethod("TurnWith", any), ahead = t.GetMethod("AheadOf", any);
                if (with == null || turning == null || ahead == null) return;           // (an older Keystone: it knew only the craft flown)
                carryWith = (Func<Vessel, Vector3, Vector3>)Delegate.CreateDelegate(typeof(Func<Vessel, Vector3, Vector3>), with);
                turnWith = (Func<Vessel, Quaternion, Quaternion>)Delegate.CreateDelegate(typeof(Func<Vessel, Quaternion, Quaternion>), turning);
                aheadOf = (Func<Vessel, Vector3>)Delegate.CreateDelegate(typeof(Func<Vessel, Vector3>), ahead);
                now = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), t.GetProperty("Now", any).GetGetMethod());
            }
            catch (Exception) { now = null; }
        }

        /// <summary>A thing that rides with the ship v, placed as at v's last physics step, carried on to where v is drawn this frame.</summary>
        public static void Carry(Transform t, Vessel v)
        {
            if (!looked) Look();
            if (now == null || t == null || v == null || !now()) return;
            t.position = carryWith(v, t.position);
            t.rotation = turnWith(v, t.rotation);
        }

        /// <summary>A thing kept beside the ship flown by an offset from it (not turning with it), carried on with it.</summary>
        public static void Along(Transform t)
        {
            if (!looked) Look();
            if (now == null || t == null || !now()) return;
            t.position += aheadOf(FlightGlobals.ActiveVessel);
        }
    }
}
