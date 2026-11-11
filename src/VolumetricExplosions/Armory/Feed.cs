using System;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// Everything that names the Volumetric Explosions mod. Kept apart, and in the bodies of methods only, so that this mod
    /// loads, and can say what is missing, where that one is not there: nothing here is looked at until it is called. What that
    /// mod has had only since its way in's Level 6 (Sources.Took, ExhaustKind.Screen, Trails.Burst) is reached only through a
    /// method of its own, asked only where the copy installed says it is that new: an older one goes on working, with BD Armory's
    /// own left wherever this cannot know that ours was taken. So too what it has had only since its Level 7 (Sources.Nuke).
    /// </summary>
    static class Feed
    {
        public static void Hook(Action asked) { VolumetricExplosions.Sources.Asked += asked; }
        public static void Unhook(Action asked) { VolumetricExplosions.Sources.Asked -= asked; }
        public static bool Ready() => VolumetricExplosions.Sources.Ready;

        // (what a source of smoke is, as the explosions mod's ExhaustKind has it: written here as numbers, so that nothing outside this class names that mod)
        public const int Engine = 1, Burning = 2, Spray = 4;

        /// <summary>
        /// Something burning or leaking this frame (see the explosions mod's Exhaust for what each figure is), and what it is (its ExhaustKind: 1 an
        /// engine's jet, 2 something burning with no jet, 4 a spray). Whether the air took any of it, its flame or its smoke.
        /// </summary>
        public static bool Burn(int id, Vector3 rim, Vector3 along, Vector3d going, float nozzle, float flame, float running, float amount, float shade, float warm, float lasts, int tint, float fire, Transform rides, int kind)
        {
            var exhaust = new VolumetricExplosions.Exhaust
            {
                id = id, rim = (Vector3d)rim, along = (Vector3d)along, going = going, nozzle = nozzle, flame = flame, running = running,
                amount = amount, shade = shade, warm = warm, lasts = lasts, tint = tint, fire = fire, rides = rides, kind = kind,
            };
            return VolumetricExplosions.Sources.Burning(exhaust);
        }

        /// <summary>The smoke a small thing leaves as it goes (a gun's muzzle), laid into the patch of air it is in, if any: never one begun for it. True if last frame's was taken.</summary>
        public static bool Smoking(int id, Vector3 at, Vector3d going, float size, float smoke, float shade, float warm, float lasts)
        {
            var trail = new VolumetricExplosions.Trail { id = id, at = (Vector3d)at, going = going, size = size, smoke = smoke, fire = 0f, shade = shade, warm = warm, tint = 0, lasts = lasts };
            return VolumetricExplosions.Trails.Smoking(trail);
        }

        // ---------------------------------------------------------------- Level 6 (the explosions mod's way in as made for this mod)

        static int knows;
        static float knowsAsked;

        /// <summary>Whether the explosions mod installed has Level 6 of its way in (Sources.Took, ExhaustKind.Screen, Trails.Burst). Asked again now and then while it has not: a copy swapped in, as the development build does, may be newer.</summary>
        public static bool Knows
        {
            get
            {
                if (knows == 0 || (knows < 0 && Time.realtimeSinceStartup > knowsAsked))
                {
                    knowsAsked = Time.realtimeSinceStartup + 5f;
                    try { knows = Level() >= 6 ? 1 : -1; } catch (Exception) { knows = -1; }
                }
                return knows > 0;
            }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Level() => VolumetricExplosions.Sources.Level;

        /// <summary>What the air took last frame of the exhaust said with this id: 1 its smoke, 2 its flame, 3 both, 0 neither. Less than nothing where the explosions mod cannot say (older than Level 6).</summary>
        public static int Took(int id)
        {
            if (!Knows) return -1;
            try { return TookNow(id); }
            catch (Exception) { knows = -1; return -1; }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int TookNow(int id) => VolumetricExplosions.Sources.Took(id);

        /// <summary>
        /// A smoke screen this frame (the explosions mod's ExhaustKind.Screen): its middle, the radius it fills, how long it hangs, its colour, how
        /// thick, how the canister moves. False where the explosions mod is older than Level 6 (and nothing is said).
        /// </summary>
        public static bool Screen(int id, Vector3 middle, Vector3d going, float radius, float lasts, float shade, float warm, float amount)
        {
            if (!Knows) return false;
            try { return ScreenNow(id, middle, going, radius, lasts, shade, warm, amount); }
            catch (Exception) { knows = -1; return false; }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool ScreenNow(int id, Vector3 middle, Vector3d going, float radius, float lasts, float shade, float warm, float amount)
        {
            var exhaust = new VolumetricExplosions.Exhaust
            {
                id = id, rim = (Vector3d)middle, along = (Vector3d)Vector3.up, going = going, nozzle = radius, flame = 0f, running = 1f,
                amount = amount, shade = shade, warm = warm, lasts = lasts, tint = 0, fire = 0f, rides = null, kind = VolumetricExplosions.ExhaustKind.Screen,
            };
            return VolumetricExplosions.Sources.Burning(exhaust);
        }

        /// <summary>A burst of so many puffs of smoke at once at a place (a hit's), only into a patch of air already there. True if one took it; false where none is near, or the explosions mod is older than Level 6.</summary>
        public static bool Burst(int id, Vector3 at, Vector3d going, float size, float smoke, float shade, float warm, float lasts, int puffs)
        {
            if (!Knows) return false;
            try { return BurstNow(id, at, going, size, smoke, shade, warm, lasts, puffs); }
            catch (Exception) { knows = -1; return false; }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool BurstNow(int id, Vector3 at, Vector3d going, float size, float smoke, float shade, float warm, float lasts, int puffs)
        {
            var trail = new VolumetricExplosions.Trail { id = id, at = (Vector3d)at, going = going, size = size, smoke = smoke, fire = 0f, shade = shade, warm = warm, tint = 0, lasts = lasts };
            return VolumetricExplosions.Trails.Burst(trail, puffs);
        }

        /// <summary>
        /// Whether a burst said with this id was laid (the answer given the frame after it was): Burst's true is a promise, which a patch put
        /// away before it stepped can break. True where the explosions mod cannot say (a copy of Level 6 from before it could), as Burst said.
        /// </summary>
        public static bool Laid(int id)
        {
            if (!Knows || noLaid) return true;
            try { return LaidNow(id); }
            catch (Exception) { noLaid = true; return true; }
        }
        static bool noLaid;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool LaidNow(int id) => VolumetricExplosions.Trails.BurstLaid(id);

        // ---------------------------------------------------------------- Level 7 (a nuclear explosion)

        static int knowsNuke;
        static float knowsNukeAsked;

        /// <summary>Whether the explosions mod installed has Level 7 of its way in (Sources.Nuke). Asked again now and then while it has not, as Knows is.</summary>
        public static bool KnowsNuke
        {
            get
            {
                if (knowsNuke == 0 || (knowsNuke < 0 && Time.realtimeSinceStartup > knowsNukeAsked))
                {
                    knowsNukeAsked = Time.realtimeSinceStartup + 5f;
                    try { knowsNuke = Level() >= 7 ? 1 : -1; } catch (Exception) { knowsNuke = -1; }
                }
                return knowsNuke > 0;
            }
        }

        /// <summary>
        /// A nuclear explosion of so many kilotons, here and now (BD Armory's NukeFX as it goes off), the bomb going at this speed as BD Armory
        /// keeps it (with the frame's own motion). True if the air laid it; false where it could not (under water, no air mod running), or the
        /// explosions mod is older than Level 7.
        /// </summary>
        public static bool Nuke(Vector3 at, float kilotons, Vector3d going)
        {
            if (!KnowsNuke) return false;
            try { return NukeNow(at, kilotons, going); }
            catch (Exception) { knowsNuke = -1; return false; }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool NukeNow(Vector3 at, float kilotons, Vector3d going) => VolumetricExplosions.Sources.Nuke((Vector3d)at, kilotons, going);

        // ---------------------------------------------------------------- the explosions mod's own catch of BD Armory's explosions

        /// <summary>Whether the explosions mod lays BD Armory's explosions as its own (its Settings.BDArmory, since its 0.10.0: an older one throws).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static bool Catching() => VolumetricExplosions.Settings.BDArmory;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Catch(bool on) => VolumetricExplosions.Settings.BDArmory = on;

        // ---------------------------------------------------------------- particles of this mod's own, drawn with the explosions mod's materials

        /// <summary>
        /// A particle system that only draws, its particles handed to it each frame (the explosions mod's Recipe.Drawn), with that mod's soft
        /// glow, light added (glow: a glint), or its dust, laid over what is behind (a haze). Null where that mod has not made its materials.
        /// </summary>
        public static ParticleSystem Drawn(Transform parent, string name, bool glow, int most)
        {
            try { return DrawnNow(parent, name, glow, most); }
            catch (Exception) { return null; }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static ParticleSystem DrawnNow(Transform parent, string name, bool glow, int most)
        {
            Material m = glow ? VolumetricExplosions.Assets.Glow : VolumetricExplosions.Assets.Dust;
            return m != null ? VolumetricExplosions.Recipe.Drawn(parent, name, m, glow ? -40f : -20f, most, false) : null;
        }

        /// <summary>
        /// The explosions mod's sparks (its Recipe.Sparks: bright streaks that Unity moves, falling and slowing as that mod's own do, and
        /// bouncing off the ground; their fall and drag to be set for the place), under a parent. Null where that mod has not made its
        /// materials.
        /// </summary>
        public static ParticleSystem Sparks(Transform parent)
        {
            try { return SparksNow(parent); }
            catch (Exception) { return null; }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static ParticleSystem SparksNow(Transform parent)
            => VolumetricExplosions.Assets.Spark != null ? VolumetricExplosions.Recipe.Sparks(parent, 9.81f, 1f, true) : null;
    }
}
