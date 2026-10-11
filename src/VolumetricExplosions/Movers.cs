using System.Collections.Generic;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>
    /// The way in for solid things that move through the air and are not craft: another mod's pieces of wreckage, say.
    /// Craft are found by the air itself (see Site.Wash); anything else says, once a frame, where it is, how it is moving
    /// and how big it is, and the air goes round it and is pushed aside by it as it is by the parts of a craft.
    ///
    /// Say it when the air asks what is burning (Sources.Asked), for each thing:
    ///
    ///     VolumetricExplosions.Movers.Moving(where, srfVelocity, radius);
    ///
    /// Nothing said: nothing changes. (Added for Fragmentation's pieces of wreckage.)
    /// </summary>
    public static class Movers
    {
        public struct Mover { public Vector3d at, going; public float radius; }

        const int Most = 48;
        static readonly List<Mover> list = new List<Mover>(Most);
        static readonly List<Mover> none = new List<Mover>();
        static int frame = -1;

        /// <summary>Something solid is here this frame (in the scene's axes), moving at this speed over the ground, this wide (its radius, metres).</summary>
        public static void Moving(Vector3d at, Vector3d going, float radius)
        {
            if (frame != Time.frameCount) { list.Clear(); frame = Time.frameCount; }
            if (list.Count < Most) list.Add(new Mover { at = at, going = going, radius = radius });
        }

        internal static List<Mover> Now => frame == Time.frameCount ? list : none;
    }

    public sealed partial class Site
    {
        /// <summary>The things other mods said are moving through the air (see Movers): spheres the air has to go round, as the parts of a craft are (see Wash).</summary>
        void TakeMovers()
        {
            List<Movers.Mover> things = Movers.Now;
            if (things.Count == 0) return;
            float mx = (bx0 + bx1) * 0.5f, my = (by0 + by1) * 0.5f, mz = (bz0 + bz1) * 0.5f;
            float reach = 0.5f * Mathf.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0) + (bz1 - bz0) * (bz1 - bz0)) + 12f;
            bool wakes = Settings.Predict || Settings.Wakes || Settings.Vortices;
            foreach (Movers.Mover m in things)
            {
                Vector3 at = frame.ToLocal(m.at);
                Vector3 going = frame.DirToLocal(m.going);
                float speed = going.magnitude;
                float dx = at.x - mx, dy = at.y - my, dz = at.z - mz, far = reach + m.radius, d2 = dx * dx + dy * dy + dz * dz;
                // (each, as a thing that leaves a wake, from as far off as it will come in the time it is looked ahead for: see Wakes.cs)
                float coming = far + speed * LookAheadFor;
                if (wakes && d2 < coming * coming) Moves(at, going, Mathf.Clamp(m.radius, 0.2f, 3.5f));
                if (ballCount >= balls.Length || d2 > far * far || speed < 0.4f) continue;
                balls[ballCount++] = new Ball { x = at.x, y = at.y, z = at.z, vx = going.x, vy = going.y, vz = going.z, r = Mathf.Clamp(m.radius, 0.2f, 3.5f), speed = speed };
            }
        }
    }
}
