using System;
using UnityEngine;

namespace VolumetricExplosions
{
    /// <summary>The sky over a patch of air, where it is high up.</summary>
    public sealed partial class Site
    {
        /// <summary>
        /// (Since 0.8.0, for Rocket Smoke's trail at dusk.) How high the sun stands for this patch's smoke, as the sine of its
        /// height over the patch's own horizon rather than over its level.
        ///
        /// Smoke high up sees the sun long after the ground under it is in the planet's shadow: from h above a world of radius R
        /// the horizon lies acos(R / (R + h)) below level, 7.4 degrees from 5 km up on Kerbin, 17.8 from 30 km (5.6 from 30 km on
        /// Earth). So at dusk a rocket's trail is lit gold and pink by a sun that has set for everyone below it, the higher the
        /// longer, against a dark sky (the "twilight phenomenon" of launches seen after sunset), and the planet's shadow climbs it
        /// as the sun goes down. Taken as the patch's own sun height, all of a trail went dark with the ground. A ray of that
        /// light that only just clears the horizon has come a long way through the low air: it is as red as the sun on the
        /// horizon is, as the reckoning that uses this has it for a low sun.
        /// </summary>
        float SunOverHorizon(Vector3d here, float sunUp)
        {
            if (space || body == null) return sunUp;
            double r = (here - body.position).magnitude, R = body.Radius;
            if (r <= R + 50.0) return sunUp;
            double dip = Math.Acos(Math.Min(R / r, 1.0));
            double e = Math.Asin(Math.Max(-1.0, Math.Min(1.0, (double)sunUp)));
            return (float)Math.Sin(Math.Min(e + dip, Math.PI / 2.0));
        }
    }
}
