using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// BD Armory, found by name: its types among the game's loaded libraries, the pools it keeps its effects in, and what this mod
    /// reads of each effect. Nothing of BD Armory is referenced (this mod loads and runs without it) and nothing of it is changed:
    /// it is only read. Each thing read is read through a getter made once (see Getter), not looked up again for every object
    /// every frame.
    /// </summary>
    static class Bda
    {
        public static bool Looked, Here;
        public static string Missing = "";

        public static Type FireFX, LeakFX, Flames, HitFX, Rocket, Weapon, Missile, Flare, Smoke, Chaff, Bubble, Decoy, Dropper, Pool, Nuke, Emitter, Bullet, Explosion, Shock;

        // the pools (BD Armory's static ObjectPools, each with a List<GameObject> of the effects, active while they are in use)
        public static Func<object> FirePool, LeakPool, FlamePool, HitPool, PenPool, RocketPools, FlarePool, SmokePool, ChaffPool, BubblePool, DecoyPool;
        public static Func<object, List<GameObject>> PoolList;
        public static Func<bool> FlareSmoke;

        // a fire on a part (BD Armory's FireFX, its own type not public): when it began, how big it burns, when it began to go out
        public static Func<object, float> FireStart, FireScale, FireDying;
        // a leak (FuelLeakFX, not public): when it began, how long it lasts, when it stopped
        public static Func<object, float> LeakStart, LeakLife, LeakDying;
        // a hit (BulletHitFX): when, and of what calibre
        public static Func<object, float> HitStart, HitCaliber;
        // an unguided rocket (PooledRocket): when it was fired, how long it thrusts, its calibre, how it moves (with the frame's own motion)
        public static Func<object, float> RocketStart, RocketBurns, RocketCaliber;
        public static Func<object, Vector3> RocketGoing;
        // a bullet (PooledBullet, in ModuleWeapon's bulletPool) and a rocket: where it is, how it moves, its mass (a bullet's in kg, a
        // rocket's in tonnes) and calibre; an explosion (ExplosionFx, in its explosionFXPools by model): where, and when it was set going
        // (for their splashes: see Splashes)
        public static Func<object> BulletPool, ExplosionPools;
        public static Func<object, Vector3> BulletAt, BulletGoing, RocketAt, ExplosionAt;
        public static Func<object, float> BulletMass, BulletCaliber, RocketMass, ExplosionStart;
        // a missile (MissileLauncher): how far it is through its flight (Idle, Drop, Boost, Cruise, PostThrust), its throttle, its thrusts, what it is
        public static Func<object, int> MissileState, MissileClass;
        public static Func<object, float> MissileThrottle, MissileBoost, MissileCruise, MissileNow;
        public static Func<object, string> MissileBoostNozzles;
        public static int Torpedo = -1;
        // a gun (ModuleWeapon): when it last fired, its calibre
        public static Func<object, float> GunFired, GunCaliber;
        // a flare (CMFlare): when it was dropped, how long it burns, how it moves; a smoke screen's canister (CMSmoke): how it moves
        // (not its "alive": BD Armory sets that false as a flare burns out and never true again, so a flare from its pool used a second
        // time is not alive from the first)
        public static Func<object, float> FlareStart, FlareLife;
        public static Func<object, Vector3> FlareGoing, SmokeGoing, ChaffGoing;
        // a nuclear explosion (NukeFX, pooled in its own nukePool): when it was set, how many kilotons, where it is, how it moves (with the
        // frame's own motion, as BD Armory keeps it), whether it has gone off, its own light (only with BD Armory's LightFX on), and the
        // models of its flash, shock front, blast, plume of dust and debris; and the effects it sets off as it goes off (FXEmitter, not
        // public, pooled in its FXPools by model and sound): when each began
        public static Func<object> NukePools, EmitterPools;
        public static Func<object, float> NukeStart, NukeDelay, NukeYield, EmitterStart;
        public static Func<object, Vector3> NukeAt, NukeGoing;
        public static Func<object, bool> NukeDetonated;
        public static Func<object, Light> NukeLight;
        public static Func<object, string>[] NukePaths = new Func<object, string>[0];

        // (for the development build's tests: BD Armory's own ways to set each effect off)
        public static MethodInfo AttachFire, AttachLeak, AttachFlames, CreateBulletHit, CreateNuke;

        static Type Find(string full)
        {
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                Type t = a.assembly != null ? a.assembly.GetType(full, false) : null;
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Look for BD Armory (once; the development build may ask again). True if it is here, with what this mod needs of it.</summary>
        public static bool Look(bool again = false)
        {
            if (Looked && !again) return Here;
            Looked = true;
            Here = false;
            try
            {
                HitFX = Find("BDArmory.FX.BulletHitFX");
                if (HitFX == null) { Missing = "BD Armory is not installed."; return false; }
                FireFX = Find("BDArmory.FX.FireFX");
                LeakFX = Find("BDArmory.FX.FuelLeakFX");
                Flames = Find("BDArmory.FX.DecalEmitterScript");
                Rocket = Find("BDArmory.Bullets.PooledRocket");
                Weapon = Find("BDArmory.Weapons.ModuleWeapon");
                Missile = Find("BDArmory.Weapons.Missiles.MissileLauncher");
                Flare = Find("BDArmory.CounterMeasure.CMFlare");
                Smoke = Find("BDArmory.CounterMeasure.CMSmoke");
                Chaff = Find("BDArmory.CounterMeasure.CMChaff");
                Bubble = Find("BDArmory.CounterMeasure.CMBubble");
                Decoy = Find("BDArmory.CounterMeasure.CMDecoy");
                Shock = Find("BDArmory.Damage.EMPShock");
                Dropper = Find("BDArmory.CounterMeasure.CMDropper");
                Pool = Find("BDArmory.Utils.ObjectPool");
                Nuke = Find("BDArmory.FX.NukeFX");
                Emitter = Find("BDArmory.FX.FXEmitter");
                Bullet = Find("BDArmory.Bullets.PooledBullet");
                Explosion = Find("BDArmory.FX.ExplosionFx");
                Type settings = Find("BDArmory.Settings.BDArmorySettings");

                PoolList = Getter.Of<List<GameObject>>(Pool, "pool");
                FirePool = Getter.Static<object>(HitFX, "FireFXPool");
                LeakPool = Getter.Static<object>(HitFX, "leakFXPool");
                FlamePool = Getter.Static<object>(HitFX, "flameFXPool");
                HitPool = Getter.Static<object>(HitFX, "bulletHitFXPool");
                PenPool = Getter.Static<object>(HitFX, "penetrationFXPool");
                RocketPools = Getter.Static<object>(Weapon, "rocketPool");
                FlarePool = Getter.Static<object>(Dropper, "flarePool");
                SmokePool = Getter.Static<object>(Dropper, "smokePool");
                ChaffPool = Getter.Static<object>(Dropper, "chaffPool");
                BubblePool = Getter.Static<object>(Dropper, "bubblePool");
                DecoyPool = Getter.Static<object>(Dropper, "decoyPool");
                FlareSmoke = Getter.Static<bool>(settings, "FlareSmoke");

                FireStart = Getter.Of<float>(FireFX, "startTime");
                FireScale = Getter.Of<float>(FireFX, "burnScale");
                FireDying = Getter.Of<float>(FireFX, "disableTime");
                LeakStart = Getter.Of<float>(LeakFX, "startTime");
                LeakLife = Getter.Of<float>(LeakFX, "lifeTime");
                LeakDying = Getter.Of<float>(LeakFX, "disableTime");
                HitStart = Getter.Of<float>(HitFX, "startTime");
                HitCaliber = Getter.Of<float>(HitFX, "caliber");
                RocketStart = Getter.Of<float>(Rocket, "startTime");
                RocketBurns = Getter.Of<float>(Rocket, "thrustTime");
                RocketCaliber = Getter.Of<float>(Rocket, "caliber");
                RocketGoing = Getter.Of<Vector3>(Rocket, "currentVelocity");
                MissileState = Getter.Of<int>(Missile, "MissileState");
                MissileClass = Getter.Of<int>(Missile, "weaponClass");
                MissileThrottle = Getter.Of<float>(Missile, "Throttle");
                MissileBoost = Getter.Of<float>(Missile, "thrust");
                MissileCruise = Getter.Of<float>(Missile, "cruiseThrust");
                MissileNow = Getter.Of<float>(Missile, "currentThrust");
                MissileBoostNozzles = Getter.Of<string>(Missile, "boostExhaustTransformName");
                GunFired = Getter.Of<float>(Weapon, "timeFired");
                GunCaliber = Getter.Of<float>(Weapon, "caliber");
                FlareStart = Getter.Of<float>(Flare, "startTime");
                FlareLife = Getter.Of<float>(Flare, "lifeTime");
                FlareGoing = Getter.Of<Vector3>(Flare, "velocity");
                SmokeGoing = Getter.Of<Vector3>(Smoke, "velocity");
                ChaffGoing = Getter.Of<Vector3>(Chaff, "velocity");
                NukePools = Getter.Static<object>(Nuke, "nukePool");
                EmitterPools = Getter.Static<object>(Emitter, "FXPools");
                NukeStart = Getter.Of<float>(Nuke, "StartTime");
                NukeDelay = Getter.Of<float>(Nuke, "detonationTimer");
                NukeYield = Getter.Of<float>(Nuke, "yield");
                NukeAt = Getter.Of<Vector3>(Nuke, "Position");
                NukeGoing = Getter.Of<Vector3>(Nuke, "Velocity");
                NukeDetonated = Getter.Of<bool>(Nuke, "hasDetonated");
                NukeLight = Getter.Of<Light>(Nuke, "LightFx");
                NukePaths = new[] { Getter.Of<string>(Nuke, "flashModelPath"), Getter.Of<string>(Nuke, "shockModelPath"), Getter.Of<string>(Nuke, "blastModelPath"),
                                    Getter.Of<string>(Nuke, "plumeModelPath"), Getter.Of<string>(Nuke, "debrisModelPath") };
                EmitterStart = Getter.Of<float>(Emitter, "StartTime");
                BulletPool = Getter.Static<object>(Weapon, "bulletPool");
                ExplosionPools = Getter.Static<object>(Explosion, "explosionFXPools");
                BulletAt = Getter.Of<Vector3>(Bullet, "currentPosition");
                BulletGoing = Getter.Of<Vector3>(Bullet, "currentVelocity");
                BulletMass = Getter.Of<float>(Bullet, "bulletMass");
                BulletCaliber = Getter.Of<float>(Bullet, "caliber");
                RocketAt = Getter.Of<Vector3>(Rocket, "currentPosition");
                RocketMass = Getter.Of<float>(Rocket, "rocketMass");
                ExplosionAt = Getter.Of<Vector3>(Explosion, "Position");
                ExplosionStart = Getter.Of<float>(Explosion, "StartTime");
                // (which of its weapon classes is a torpedo: no flame or smoke; its track on the sea is ours, Splashes.Wake, its own specks of foam under the water left)
                FieldInfo cls = Getter.FieldOf(Missile, "weaponClass");
                if (cls != null && cls.FieldType.IsEnum)
                    try { Torpedo = Convert.ToInt32(Enum.Parse(cls.FieldType, "SLW")); } catch (Exception) { Torpedo = -1; }

                const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                AttachFire = HitFX.GetMethod("AttachFire", statics);
                AttachLeak = HitFX.GetMethod("AttachLeak", statics);
                AttachFlames = HitFX.GetMethod("AttachFlames", statics);
                CreateBulletHit = HitFX.GetMethod("CreateBulletHit", statics);
                CreateNuke = Nuke != null ? Nuke.GetMethod("CreateExplosion", statics) : null;

                // (what this mod cannot do without: the pools' lists; the rest is each kind's own, and a kind whose readers are missing is left as BD Armory draws it)
                if (PoolList == null) { Missing = "BD Armory is here, but not as this mod knows it (its ObjectPool has no pool): its own effects are left."; return false; }
                var lost = new List<string>();
                if (FireFX == null || FirePool == null || FireStart == null) lost.Add("fires");
                if (LeakFX == null || LeakPool == null || LeakStart == null) lost.Add("leaks");
                if (Flames == null || FlamePool == null) lost.Add("flames");
                if (HitPool == null || HitStart == null) lost.Add("hits");
                if (Rocket == null || RocketPools == null || RocketStart == null || RocketBurns == null) lost.Add("rockets");
                if (Missile == null || MissileState == null || MissileThrottle == null) lost.Add("missiles");
                if (Weapon == null || GunFired == null) lost.Add("guns");
                if (Flare == null || FlarePool == null || FlareStart == null) lost.Add("flares");
                if (Smoke == null || SmokePool == null) lost.Add("smoke screens");
                if (Chaff == null || ChaffPool == null) lost.Add("chaff");
                if (Bubble == null || BubblePool == null) lost.Add("bubble screens");
                if (Decoy == null || DecoyPool == null) lost.Add("sonar decoys");
                if (Shock == null) lost.Add("an EMP's crackle");
                if (Nuke == null || NukePools == null || NukeDetonated == null || NukeYield == null || NukeAt == null) lost.Add("nukes");
                if (Bullet == null || BulletPool == null || BulletAt == null || BulletGoing == null || BulletMass == null) lost.Add("bullets' splashes");
                Missing = lost.Count == 0 ? "" : "Not found in this BD Armory, and left as it draws them: " + string.Join(", ", lost.ToArray()) + ".";
                Here = true;
                Armory.Log("BD Armory is here (" + HitFX.Assembly.GetName().Version + ")" + (Missing.Length > 0 ? ". " + Missing : ""));
            }
            catch (Exception ex)
            {
                Missing = "BD Armory could not be read (" + ex.GetType().Name + "): its own effects are left.";
                Armory.Log(Missing + " " + ex.Message);
                Here = false;
            }
            return Here;
        }
    }

    /// <summary>
    /// Getters for what is read of BD Armory's objects, made once each: a little method made as the game runs that reads the one field or
    /// property (private ones too) of an object passed to it, as fast as code built for it, with nothing made in memory for each read. Where
    /// such a method cannot be made, the slow way, through reflection, which makes a little garbage at each read of a number.
    /// </summary>
    static class Getter
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>A field of the type or of a type it is made from, of any visibility.</summary>
        public static FieldInfo FieldOf(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any);
                if (f != null) return f;
            }
            return null;
        }

        static MethodInfo GetterOf(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, Any);
                if (p != null) return p.GetGetMethod(true);
            }
            return null;
        }

        static bool Fits(Type from, Type to) => to.IsAssignableFrom(from) || (from.IsEnum && to == typeof(int));

        static T As<T>(object value)
        {
            if (value is T t) return t;
            if (value == null) return default(T);
            return (T)Convert.ChangeType(value, typeof(T));
        }

        /// <summary>A reader of a field or property of the type's objects, as a T (an enum read as its number); null if there is none such.</summary>
        public static Func<object, T> Of<T>(Type type, string name)
        {
            if (type == null) return null;
            FieldInfo f = FieldOf(type, name);
            MethodInfo get = f == null ? GetterOf(type, name) : null;
            if ((f == null || f.IsStatic) && (get == null || get.IsStatic)) return null;
            Type from = f != null ? f.FieldType : get.ReturnType;
            if (!Fits(from, typeof(T))) return null;
            try
            {
                var made = new DynamicMethod("VolumetricArmory_" + type.Name + "_" + name, typeof(T), new[] { typeof(object) }, type, true);
                ILGenerator il = made.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, type);
                if (f != null) il.Emit(OpCodes.Ldfld, f);
                else il.Emit(OpCodes.Callvirt, get);
                if (from.IsValueType && !typeof(T).IsValueType) il.Emit(OpCodes.Box, from);
                il.Emit(OpCodes.Ret);
                var reader = (Func<object, T>)made.CreateDelegate(typeof(Func<object, T>));
                return reader;
            }
            catch (Exception)
            {
                if (f != null) return o => As<T>(f.GetValue(o));
                return o => As<T>(get.Invoke(o, null));
            }
        }

        /// <summary>A reader of a static field or property of the type, as a T; null if there is none such.</summary>
        public static Func<T> Static<T>(Type type, string name)
        {
            if (type == null) return null;
            FieldInfo f = FieldOf(type, name);
            MethodInfo get = f == null ? GetterOf(type, name) : null;
            if ((f == null || !f.IsStatic) && (get == null || !get.IsStatic)) return null;
            Type from = f != null ? f.FieldType : get.ReturnType;
            if (!Fits(from, typeof(T))) return null;
            try
            {
                var made = new DynamicMethod("VolumetricArmory_" + type.Name + "_" + name, typeof(T), Type.EmptyTypes, type, true);
                ILGenerator il = made.GetILGenerator();
                if (f != null) il.Emit(OpCodes.Ldsfld, f);
                else il.Emit(OpCodes.Call, get);
                if (from.IsValueType && !typeof(T).IsValueType) il.Emit(OpCodes.Box, from);
                il.Emit(OpCodes.Ret);
                return (Func<T>)made.CreateDelegate(typeof(Func<T>));
            }
            catch (Exception)
            {
                if (f != null) return () => As<T>(f.GetValue(null));
                return () => As<T>(get.Invoke(null, null));
            }
        }
    }
}
