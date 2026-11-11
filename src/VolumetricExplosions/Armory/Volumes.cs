using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// In flight: BD Armory's effects found as they start and said to the explosions mod's air, once a frame when it asks (its
    /// Sources.Asked, in LateUpdate: before any camera draws, and after BD Armory has started this frame's effects in its own
    /// FixedUpdate and Update). The rule everywhere: ours where the air took it, BD Armory's own where it did not; the smoke and
    /// the flame of each decided apart (a missile whose flame found no place in the air keeps BD Armory's flame, and has our
    /// smoke).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Volumes : MonoBehaviour
    {
        public static string Report = "", Why = "";
        internal static Volumes Instance;

        // ---- the kinds, for the counts (see Tests.Said)
        internal const int Fire = 0, Leak = 1, Flame = 2, Hit = 3, Rocket = 4, Flare = 5, Screen = 6, Missile = 7, Gun = 8, Nuke = 9, Chaff = 10, Bubbles = 11, Decoy = 12, Kinds = 13;
        internal static readonly string[] KindNames = { "fires", "leaks", "flames", "hits", "rockets", "flares", "smoke screens", "missiles", "guns", "nukes", "chaff", "bubble screens", "sonar decoys" };
        // (since the flight began: how many of each started, how many were drawn as ours at some time, how many were left BD Armory's own at some time)
        internal static readonly int[] Seen = new int[Kinds], Ours = new int[Kinds], Theirs = new int[Kinds];

        /// <summary>One of BD Armory's pooled effects (a fire, a leak, a hit, a rocket, a flare, a smoke screen), kept for as long as its object is: a pool uses the same one again and again.</summary>
        internal sealed class Fx
        {
            public GameObject Go;
            public Component It;                // BD Armory's own component on it (a FireFX, a PooledRocket ...)
            public int Kind, Key;
            public int Id;                      // the number it is said to the air by: a fresh one for each start
            public float Start = float.NaN;     // BD Armory's own time it started, as last read: a change is a new start
            public float Began;                 // when this start was first seen
            public bool Active, Dark, Done, Counted, Refused, Promised;
            public int SeenAt, BurstFrame;
            public Part Part;
            public Hidden Smoke = Hidden.None, Flame = Hidden.None;
            public bool Built;
            public KSPParticleEmitter[] Sizes;  // (a missile into a tank's flames: the emitters whose shrinking says how much is left)
            public float Size0;
            public float SmokeAt = -100f, FlameAt = -100f;     // when the air last took its smoke, its flame
        }

        /// <summary>A missile (its MissileLauncher), from when it is first seen on a loaded craft.</summary>
        internal sealed class Launcher
        {
            public PartModule Module;
            public int Id;
            public Hidden Smoke = Hidden.None, Flame = Hidden.None;
            public bool Built, Burning, Counted, Refused;
            public readonly List<Transform> Nozzles = new List<Transform>(), Boosters = new List<Transform>();
            public float SmokeAt = -100f, FlameAt = -100f, BurnedAt = -100f, Lit;
        }

        /// <summary>A gun (its ModuleWeapon): its muzzles' smoke, the world-space emitters BD Armory puffs at each shot.</summary>
        internal sealed class Firearm
        {
            public PartModule Module;
            public int Id;
            public Hidden Smoke = Hidden.None;
            public readonly List<Transform> Muzzles = new List<Transform>();
            public bool Built, Known, Firing, Counted, Refused;
            public float Fired, ShotAt = -100f, TakenAt = -100f, BurstAt;
        }

        /// <summary>One of BD Armory's nuclear explosions (its NukeFX), kept for as long as its object is: its pool uses the same one again.</summary>
        internal sealed class Bomb
        {
            public GameObject Go;
            public Component It;
            public int Key, SeenAt;
            public float Start = float.NaN;     // BD Armory's own time it was set (its StartTime), as last read: a change is a new bomb
            public bool Active, Fired, Taken;   // in use; gone off; and said to the air, and taken by it
            public float FiredAt;
            public string[] Paths;              // the models of the effects it sets off as it goes off
            public Light Light;                 // BD Armory's own light for it, switched off while ours is drawn
            public bool LightHidden, LightWas;
        }

        /// <summary>One of the effects a bomb of ours set off (its flash, shock, blast, plume or debris), hidden until it goes back to its pool.</summary>
        internal sealed class HeldFx
        {
            public GameObject Go;
            public Component It;
            public int Key;
            public float Start;
            public Hidden Own;
        }

        internal readonly Dictionary<int, Fx> fx = new Dictionary<int, Fx>();
        internal readonly List<Fx> all = new List<Fx>();
        internal readonly List<Launcher> launchers = new List<Launcher>();
        internal readonly List<Firearm> guns = new List<Firearm>();
        readonly HashSet<PartModule> known = new HashSet<PartModule>();
        readonly List<object> rocketPools = new List<object>();
        internal readonly List<Bomb> bombs = new List<Bomb>();
        readonly Dictionary<int, Bomb> bombOf = new Dictionary<int, Bomb>();
        internal readonly List<HeldFx> heldFx = new List<HeldFx>();
        readonly HashSet<int> heldKeys = new HashSet<int>();
        readonly List<object> nukePools = new List<object>(), emitterPools = new List<object>();
        readonly List<string> emitterKeys = new List<string>();
        int nukePoolsCount = -1, emitterPoolsCount = -1;

        bool hooked, anyHidden, cutWas = true;
        float lookAt, tellAt;
        int askedFrame = -10;
        float now;
        int frame;
        Vector3d frameGoing;

        // (how long after the air last took a thing's smoke or flame BD Armory's own for it stays hidden: a frame or two refused is not worth a flash of theirs)
        const float Hold = 0.3f;

        // ---- the numbers this mod says its sources by: a block of its own, 0xBDA00000 to 0xBDAFFFFF, eight to each source (a gun's muzzles each
        // have one). Rocket Smoke's, Engine Flames' and Fragmentation's are made from craft's and parts' flight numbers, spread over every number
        // there is, so no block can be wholly clear of them; this one is clear of Fragmentation's own blocks (900000 and 1800000 up), and meets
        // one of the others' only by chance, about one in five thousand for each.
        // The serial goes round after 131072 numbers (a long session's hits), while a missile, a gun or a fire keeps the one it was given for
        // as long as it is there: so a number still held is passed over, not given twice (two things said by one number are one vent to the
        // air, jumping between them).
        const int IdBase = unchecked((int)0xBDA00000);
        static int serial;
        static readonly HashSet<int> ids = new HashSet<int>();
        static int NextId()
        {
            for (int tries = 0; tries <= 0x1FFFF; tries++)
            {
                serial = (serial + 1) & 0x1FFFF;
                int id = IdBase + serial * 8;
                if (ids.Add(id)) return id;
            }
            return IdBase;
        }
        static void LetGo(ref int id) { if (id != 0) ids.Remove(id); id = 0; }

        void Awake()
        {
            Instance = this;
            // (made once: a method handed on as it is would be made afresh at every call)
            scan = Scan; missiles = SayMissiles; gunsSaid = SayGuns; nukes = SayNukes; look = Look;
            splashes = () => Splashes.Keep(Armory.Wanted(Armory.Splashes) && Bda.BulletPool != null);
            wakes = () => Splashes.Rise(now);
            chaff = () => ChaffClouds.Step(now);
            sparks = HitSparks.Step;
            crackle = () => { if (Armory.Hits) HitSparks.Crackle(now); };
        }

        void Start()
        {
            // (Nothing without BD Armory, and no word of it on the mod's line in Keystone's list: most who have the explosions have no BD Armory.)
            if (!Bda.Look()) { Why = Bda.Missing; Report = Bda.HitFX == null ? "" : Why; return; }
            Feed.Hook(Asked);
            hooked = true;
            GameEvents.onGameSceneLoadRequested.Add(Leaving);
        }

        void OnDestroy()
        {
            if (hooked) { try { Feed.Unhook(Asked); } catch (Exception) { } }
            GameEvents.onGameSceneLoadRequested.Remove(Leaving);
            ShowAll();
            Splashes.PutBack();
            if (Instance == this) { Instance = null; ids.Clear(); Hidden.Forget(); }
        }

        // (BD Armory's own, everything, before the scene goes: its objects may outlive this one, as pooled things do)
        void Leaving(GameScenes scene) { ShowAll(); Splashes.PutBack(); Splashes.Forget(); ChaffClouds.Forget(); HitSparks.Forget(); }

        void Update()
        {
            if (!hooked) return;
            if (Armory.On && Armory.KeyPressed) Flip();
            // (the explosions mod asks once a frame while its air is there: where it has stopped asking, all of BD Armory's own is shown again;
            // but not while the game is paused, when it does not ask, and BD Armory's own is as still as ours)
            if (anyHidden && Time.deltaTime > 0f && Time.frameCount - askedFrame > 2) ShowAll();
#if DEV
            Tests.Pending();
            Tests.Splashing();
#endif
        }

        /// <summary>Ours or BD Armory's own, from the key: and a line on the screen saying which, for setting the two side by side.</summary>
        static void Flip()
        {
            Armory.Choose(!Armory.Ours);
            try { ScreenMessages.PostScreenMessage(Armory.Ours ? "BD Armory's effects: ours" : "BD Armory's effects: its own", 2.5f, ScreenMessageStyle.UPPER_CENTER); } catch (Exception) { }
        }

        // (BD Armory's explosions are the explosions' own catch, Warheads, which reads the same switch: Settings.CatchBDArmory. While this
        // was a mod of its own it drove that mod's "bdarmory" setting from here, and put it back as it went.)

        // ---------------------------------------------------------------- once a frame

        static readonly string[] failed = new string[12];

        void Asked()
        {
            askedFrame = Time.frameCount;
            if (!Armory.Wanted(null) || !Feed.Ready())
            {
                if (anyHidden) ShowAll();
                Splashes.PutBack();
                ChaffClouds.Forget();
                HitSparks.Forget();
                if (Time.realtimeSinceStartup > tellAt) { tellAt = Time.realtimeSinceStartup + 1f; Tell(); }
                return;
            }
            now = Time.time;
            frame = Time.frameCount;
            frameGoing = Krakensbane.GetFrameVelocity();
            if (Time.realtimeSinceStartup > lookAt) { lookAt = Time.realtimeSinceStartup + 1f; Guarded(0, look); }
            bool cutting = Armory.Cut;
            if (cutting != cutWas) { cutWas = cutting; Recut(cutting); }
            // (each in a try of its own: one kind's fault is not to leave the rest as BD Armory's own)
            Guarded(1, scan);
            Guarded(2, missiles);
            Guarded(3, gunsSaid);
            Guarded(4, nukes);
            Guarded(5, splashes);
            Guarded(6, wakes);
            Guarded(7, chaff);
            Guarded(8, sparks);
            Guarded(9, crackle);
            if (Time.realtimeSinceStartup > tellAt) { tellAt = Time.realtimeSinceStartup + 1f; Tell(); }
        }

        static void Guarded(int which, Action what)
        {
            try { what(); }
            catch (Exception ex)
            {
                if (failed[which] == null) { failed[which] = ex.GetType().Name; Armory.Log("failed (" + which + "): " + ex); }
            }
        }

        Action scan, missiles, gunsSaid, nukes, look, splashes, wakes, chaff, sparks, crackle;

        void Scan()
        {
            bool cutting = cutWas;
            PoolOf(Bda.FirePool, Fire, Bda.FireFX, Bda.FireStart, Armory.Fires, false, cutting);
            PoolOf(Bda.LeakPool, Leak, Bda.LeakFX, Bda.LeakStart, Armory.Leaks, false, cutting);
            PoolOf(Bda.FlamePool, Flame, Bda.Flames, null, Armory.Fires, false, cutting);
            PoolOf(Bda.HitPool, Hit, Bda.HitFX, Bda.HitStart, Armory.Hits, false, cutting);
            PoolOf(Bda.PenPool, Hit, Bda.HitFX, Bda.HitStart, Armory.Hits, true, cutting);
            for (int n = 0; n < rocketPools.Count; n++) Pool(rocketPools[n], Rocket, Bda.Rocket, Bda.RocketStart, Armory.Rockets, false, cutting);
            PoolOf(Bda.FlarePool, Flare, Bda.Flare, Bda.FlareStart, Armory.Countermeasures, false, cutting);
            PoolOf(Bda.SmokePool, Screen, Bda.Smoke, null, Armory.Countermeasures, false, cutting);
            PoolOf(Bda.ChaffPool, Chaff, Bda.Chaff, null, Armory.Countermeasures, false, cutting);
            PoolOf(Bda.BubblePool, Bubbles, Bda.Bubble, null, Armory.Countermeasures, false, cutting);
            PoolOf(Bda.DecoyPool, Decoy, Bda.Decoy, null, Armory.Countermeasures, false, cutting);
            // (those not seen this frame are over: gone back to their pool)
            for (int n = 0; n < all.Count; n++)
            {
                Fx f = all[n];
                if (f.Active && f.SeenAt != frame) End(f);
            }
        }

        void PoolOf(Func<object> pool, int kind, Type type, Func<object, float> start, Armory.Switch wanted, bool dark, bool cutting)
        {
            if (pool == null) return;
            Pool(pool(), kind, type, start, wanted, dark, cutting);
        }

        void Pool(object pool, int kind, Type type, Func<object, float> start, Armory.Switch wanted, bool dark, bool cutting)
        {
            if (pool == null || type == null) return;
            List<GameObject> list = Bda.PoolList(pool);
            if (list == null) return;
            bool want = wanted == null || wanted;
            for (int n = 0; n < list.Count; n++)
            {
                GameObject go = list[n];
                if (go == null || !go.activeInHierarchy) continue;
                int key = go.GetInstanceID();
                if (!fx.TryGetValue(key, out Fx f))
                {
                    Component it = go.GetComponent(type);
                    if (it == null) it = go.GetComponentInChildren(type);
                    f = new Fx { Go = go, It = it, Kind = kind, Key = key, Dark = dark };
                    fx[key] = f;
                    all.Add(f);
                }
                f.SeenAt = frame;
                if (f.It == null) continue;
                float began = start != null ? start(f.It) : 0f;
                if (!f.Active || (start != null && began != f.Start))
                {
                    if (f.Active) End(f);
                    Begin(f, began);
                }
                if (!want) { Release(f); continue; }
                switch (kind)
                {
                    case Fire: SayFire(f, cutting); break;
                    case Leak: SayLeak(f, cutting); break;
                    case Flame: SayFlames(f, cutting); break;
                    case Hit: SayHit(f, cutting); break;
                    case Rocket: SayRocket(f, cutting); break;
                    case Flare: SayFlare(f, cutting); break;
                    case Screen: SayScreen(f, cutting); break;
                    case Chaff: SayChaff(f, cutting); break;
                    case Bubbles: SayBubbles(f, cutting); break;
                    case Decoy: SayDecoy(f); break;
                }
            }
        }

        // (which of an object's emitters make smoke: named for it, as BD Armory's models have them, "smokePoint", "smokeObject", "smoke", "darkSmoke")
        static bool Smoky(KSPParticleEmitter e) => e.gameObject.name.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0;
        static bool Rcs(KSPParticleEmitter e) => e.gameObject.name.StartsWith("rcs", StringComparison.OrdinalIgnoreCase);
        static readonly Func<KSPParticleEmitter, bool> SmokeOf = Smoky, FlameOf = e => !Smoky(e), WorldOf = e => e.useWorldSpace,
            MissileSmokeOf = e => e.useWorldSpace && !Rcs(e), MissileFlameOf = e => !e.useWorldSpace && !Rcs(e), FlareSmokeOf = e => e.gameObject.name == "pEmitter",
            SparksOf = e => e.gameObject.name.IndexOf("spark", StringComparison.OrdinalIgnoreCase) >= 0;

        void Begin(Fx f, float began)
        {
            f.Active = true;
            f.Start = began;
            f.Began = now;
            LetGo(ref f.Id);
            f.Id = NextId();
            f.SmokeAt = f.FlameAt = -100f;
            f.Done = f.Counted = f.Refused = f.Promised = false;
            Seen[f.Kind]++;
            // (what it is on: a fire, a leak and a missile's flames hang from their part)
            f.Part = f.Kind == Fire || f.Kind == Leak || f.Kind == Flame ? f.Go.GetComponentInParent<Part>() : null;
            if (!f.Built)
            {
                // (once for each object: a pool's objects keep what hangs from them)
                f.Built = true;
                switch (f.Kind)
                {
                    case Fire: f.Smoke = Hidden.Of(f.Go, SmokeOf); f.Flame = Hidden.Of(f.Go, FlameOf); break;
                    case Flame:
                        f.Smoke = Hidden.Of(f.Go, SmokeOf); f.Flame = Hidden.Of(f.Go, FlameOf);
                        var sizes = new List<KSPParticleEmitter>();
                        foreach (KSPParticleEmitter e in f.Go.GetComponentsInChildren<KSPParticleEmitter>(true)) if (e != null && !Smoky(e)) sizes.Add(e);
                        f.Sizes = sizes.ToArray();
                        break;
                    case Hit: f.Smoke = Hidden.Of(f.Go, SmokeOf); f.Flame = Hidden.Of(f.Go, SparksOf); break;
                    case Rocket: f.Smoke = Hidden.Of(f.Go, WorldOf); break;
                    case Flare: f.Smoke = Hidden.Of(f.Go, FlareSmokeOf); break;
                    default: f.Smoke = Hidden.Of(f.Go, null); break;       // (a leak and a smoke screen: all of it)
                }
            }
            if (f.Kind == Flame) f.Size0 = Biggest(f.Sizes);
        }

        void End(Fx f)
        {
            f.Active = false;
            f.Promised = false;
            LetGo(ref f.Id);
            Release(f);
        }

        static void Release(Fx f) { f.Smoke.Show(); f.Flame.Show(); }

        static float Biggest(KSPParticleEmitter[] emitters)
        {
            float most = 0f;
            if (emitters != null) foreach (KSPParticleEmitter e in emitters) if (e != null && e.maxSize > most) most = e.maxSize;
            return most;
        }

        /// <summary>
        /// What the air took of a thing said to it this frame, and BD Armory's own hidden or shown as that says: its smoke where our smoke was
        /// taken, its flame where our flame was. Below the explosions mod's Level 6 nothing tells the two apart: the thing was said with no flame,
        /// and whether it was taken at all is whether its smoke was.
        /// </summary>
        void Answer(Fx f, bool taken, bool six, bool cutting)
        {
            int flags = six ? Feed.Took(f.Id) : -1;
            if (flags < 0) flags = !six && taken ? 1 : 0;
            if ((flags & 1) != 0) f.SmokeAt = now;
            if ((flags & 2) != 0) f.FlameAt = now;
            bool smoke = now - f.SmokeAt < Hold, flame = now - f.FlameAt < Hold;
            f.Smoke.Set(smoke, cutting);
            f.Flame.Set(flame, cutting);
            if (smoke || flame) { anyHidden = true; if (!f.Counted) { f.Counted = true; Ours[f.Kind]++; } }
            else if (!f.Refused && now - f.Began > 0.1f) { f.Refused = true; Theirs[f.Kind]++; }
        }

        Vector3d GoingOf(Part p, Vessel v)
        {
            Rigidbody rb = p.rb;
            return (rb != null ? (Vector3d)rb.velocity : v.srf_velocity - frameGoing) + frameGoing;
        }

        /// <summary>Up, and back from the way a burning thing goes the faster it goes: the way its flame and smoke lean.</summary>
        static Vector3 Leaning(Vector3 up, Vector3d going)
        {
            Vector3 back = -(Vector3)going;
            float speed = back.magnitude;
            return (up + (speed > 0.5f ? back / speed * Mathf.Min(1.5f, speed / 15f) : Vector3.zero)).normalized;
        }

        // ---------------------------------------------------------------- a fire on a part, or on the ground (BD Armory's FireFX)

        void SayFire(Fx f, bool cutting)
        {
            Part p = f.Part;
            Transform t = f.Go.transform;
            // (taken off its part as it goes out: what is left of it BD Armory's own)
            if (p == null || p.vessel == null || t.parent == null) { Release(f); return; }
            Vessel v = p.vessel;
            if (v.packed) return;
            float scale = Bda.FireScale != null ? Bda.FireScale(f.It) : -1f;
            if (scale <= 0f) scale = 0.8f;                // (not yet worked out by BD Armory, in its first step)
            float dying = Bda.FireDying != null ? Bda.FireDying(f.It) : -1f;
            // (once it stops it fades as its last particles do, about a second; then BD Armory puts it away)
            float left = dying > 0f ? Mathf.Clamp01(1f - (now - dying) / 1.2f) : 1f;
            if (left <= 0f) return;
            float grow = Mathf.Clamp01((now - f.Began) / 0.4f + 0.3f) * left;
            Vector3 at = t.position;
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, at);
            Vector3d going = GoingOf(p, v);
            // (as big as BD Armory draws it: its particles are its burnScale to 1.2 times that across)
            float size = Mathf.Clamp(0.6f * scale, 0.25f, 2f);
            bool six = Feed.Knows;
            bool taken = Feed.Burn(f.Id, at, Leaning(up, going), going, size, size * 4.5f, 0.35f * grow, 0.7f * grow, 0.07f, 0.25f, 14f, 0, six ? grow : 0f, p.transform, Feed.Burning);
            Answer(f, taken, six, cutting);
        }

        // ---------------------------------------------------------------- a holed tank's spray (FuelLeakFX)

        void SayLeak(Fx f, bool cutting)
        {
            Part p = f.Part;
            Transform t = f.Go.transform;
            if (p == null || p.vessel == null || t.parent == null) { Release(f); return; }
            Vessel v = p.vessel;
            if (v.packed) return;
            float life = Bda.LeakLife != null ? Bda.LeakLife(f.It) : 20f, dying = Bda.LeakDying != null ? Bda.LeakDying(f.It) : -1f;
            // (stopped, run dry, or lit: what is left of its spray is left as it is, hidden or not, until BD Armory puts it away)
            if (dying > 0f || now - f.Start > life) return;
            // (in vacuum, BD Armory's own: there is no air to hold a mist)
            if (v.staticPressurekPa < 1.0) { Release(f); return; }
            Vector3 along = t.up;
            float nozzle = Mathf.Clamp(0.05f * Mathf.Abs(t.lossyScale.x), 0.05f, 0.5f);
            bool taken = Feed.Burn(f.Id, t.position + along * 0.1f, along, GoingOf(p, v), nozzle, 1.5f, 0.7f, 0.35f, 0.82f, 0f, 4f, 0, 0f, null, Feed.Spray);
            Answer(f, taken, Feed.Knows, cutting);
        }

        // ---------------------------------------------------------------- a missile into a tank: flames that die down (FlameEffect2, its DecalEmitterScript)

        void SayFlames(Fx f, bool cutting)
        {
            Part p = f.Part;
            if (p == null || p.vessel == null || f.Go.transform.parent == null) { Release(f); return; }
            Vessel v = p.vessel;
            if (v.packed) return;
            float size = Biggest(f.Sizes);
            if (size < 0.1f) return;                      // (all but gone: BD Armory's own last of it, hidden or not)
            float left = f.Size0 > 0f ? Mathf.Clamp01(size / f.Size0) : 1f;
            Vector3 at = f.Go.transform.position;
            Vector3 up = (Vector3)FlightGlobals.getUpAxis(v.mainBody, at);
            Vector3d going = GoingOf(p, v);
            float r = Mathf.Clamp(0.35f * size, 0.1f, 1.5f);
            bool six = Feed.Knows;
            bool taken = Feed.Burn(f.Id, at, Leaning(up, going), going, r, r * 4.5f, 0.35f * left, 0.6f * left, 0.07f, 0.25f, 12f, 0, six ? left : 0f, p.transform, Feed.Burning);
            Answer(f, taken, six, cutting);
        }

        // ---------------------------------------------------------------- a hit (BulletHitFX: up to 30 mm, and PenFX, darker, past it)

        void SayHit(Fx f, bool cutting)
        {
            // (once, as it starts: a few puffs into the smoke already there; BD Armory's own takes a fifth of a second)
            if (f.Done)
            {
                // (the air's true was a promise, which a patch put away before it stepped can break: asked the frame after, and the one after
                // that; BD Armory's own shown again where it was not kept. Not cut meanwhile: its particles are there to show)
                if (!f.Promised || frame <= f.BurstFrame) return;
                if (Feed.Laid(f.Id)) { f.Promised = false; f.Smoke.Set(true, cutting); Ours[Hit]++; }
                else if (frame - f.BurstFrame >= 2) { f.Promised = false; f.Smoke.Show(); Theirs[Hit]++; }
                return;
            }
            f.Done = true;
            float caliber = Bda.HitCaliber != null ? Bda.HitCaliber(f.It) : 20f;
            Transform t = f.Go.transform;
            // (its sparks the explosions mod's own, thrown off the face it struck, which BD Armory turns the hit to, and going on with what
            // it struck, whatever becomes of its smoke; BD Armory's own hidden where ours were thrown)
            if (HitSparks.Throw(t.position, t.forward, HitSparks.Carried(t.position), caliber, f.Dark, now)) { f.Flame.Set(true, false); anyHidden = true; HitSparks.Thrown++; }
            if (!Feed.Knows) { Theirs[Hit]++; return; }
            float size = Mathf.Clamp(0.02f * caliber, 0.3f, 2.5f);
            int puffs = Mathf.Clamp(2 + Mathf.RoundToInt(caliber / 15f), 3, 10);
            // (out from the face it struck, along the face's normal, which BD Armory turns the hit to)
            if (Feed.Burst(f.Id, t.position + t.forward * (0.5f * size), Vector3d.zero, size, 0.6f, f.Dark ? 0.12f : 0.42f, 0.05f, f.Dark ? 6f : 4f, puffs))
            {
                f.Smoke.Set(true, false);
                anyHidden = true;
                f.Promised = true;
                f.BurstFrame = frame;
            }
            else Theirs[Hit]++;
        }

        // ---------------------------------------------------------------- an unguided rocket's trail (PooledRocket)

        void SayRocket(Fx f, bool cutting)
        {
            if (Bda.RocketBurns == null) return;
            float burns = Bda.RocketBurns(f.It);
            if (now - f.Start > burns) return;            // (its motor out: its trail left as it is until it strikes)
            Transform t = f.Go.transform;
            float caliber = Bda.RocketCaliber != null ? Bda.RocketCaliber(f.It) : 70f;
            float r = Mathf.Clamp(0.75f * caliber / 1000f, 0.04f, 0.2f);
            Vector3 rim = t.position - t.forward * (6f * r);
            // (BD Armory leaves no trail out of the air, nor do we)
            if (FlightGlobals.getStaticPressure((Vector3d)rim) <= 0.0) { Release(f); return; }
            Vector3d going = Bda.RocketGoing != null ? (Vector3d)Bda.RocketGoing(f.It) : (Vector3d)(-t.forward * 300f);
            // (a rocket a second long is not worth a place among the flames: its smoke only, laid straight into the air)
            bool taken = Feed.Burn(f.Id, rim, -t.forward, going, r, Armory.SolidFlame(r), 1f, 0.8f, 0.82f, 0.06f, 10f, 1, 0f, null, Feed.Engine);
            Answer(f, taken, Feed.Knows, cutting);
        }

        // ---------------------------------------------------------------- a flare's trail of white smoke (CMFlare)

        void SayFlare(Fx f, bool cutting)
        {
            // (burnt out: its trail left as it is. Told by the time it burns, BD Armory's lifeTime (5 s): its "alive", once false, stays so for
            // every later use of the flare from its pool)
            float life = Bda.FlareLife != null ? Bda.FlareLife(f.It) : 5f;
            if (life <= 0f) life = 5f;
            if (now - f.Start > life) return;
            // (BD Armory set to leave no smoke behind its flares: nor do we)
            if (Bda.FlareSmoke != null && !Bda.FlareSmoke()) { Release(f); return; }
            Vector3 at = f.Go.transform.position;
            if (FlightGlobals.getStaticPressure((Vector3d)at) <= 0.0) { Release(f); return; }
            // (its own motion, which BD Armory keeps with the frame's own in it)
            Vector3 v = Bda.FlareGoing != null ? Bda.FlareGoing(f.It) : Vector3.zero;
            float speed = v.magnitude;
            Vector3 along = speed > 1f ? -v / speed : Vector3.up;
            bool taken = Feed.Burn(f.Id, at, along, (Vector3d)v, 0.12f, 0.6f, 1f, 0.5f, 0.85f, 0f, 10f, 1, 0f, null, Feed.Engine);
            Answer(f, taken, Feed.Knows, cutting);
        }

        // ---------------------------------------------------------------- a smoke screen (CMSmoke)

        void SayScreen(Fx f, bool cutting)
        {
            // (said for its first second and a half, while the cloud is let out; what the air took of it then hangs some thirty seconds, BD
            // Armory's own hidden all that while; refused, or with an explosions mod too old to know a screen, BD Armory's own)
            if (now - f.Began > 1.5f)
            {
                if (!f.Counted && !f.Refused) { f.Refused = true; Theirs[Screen]++; }
                return;
            }
            if (!Feed.Knows) return;
            Vector3 v = Bda.SmokeGoing != null ? Bda.SmokeGoing(f.It) : Vector3.zero;
            // (a screen as wide as BD Armory's own blocks the sight of sensors: 16 m round; white-grey, as a screen's smoke is: at amount 1 and shade .78
            // it lay so thick that its own shadow made it a dark grey thunderhead, darker than a burning tank's, tried 2026-11-04)
            Feed.Screen(f.Id, f.Go.transform.position, (Vector3d)v + frameGoing, 16f, 30f, 0.92f, 0.03f, 0.6f);
            int took = Feed.Took(f.Id);
            if (took > 0 && (took & 1) != 0) f.SmokeAt = now;
            if (f.SmokeAt > 0f)
            {
                f.Smoke.Set(true, cutting);
                anyHidden = true;
                if (!f.Counted) { f.Counted = true; Ours[Screen]++; }
            }
        }

        // ---------------------------------------------------------------- chaff (CMChaff)

        void SayChaff(Fx f, bool cutting)
        {
            // (begun once, at its first sight, where BD Armory's cartridge has just burst: ours a cloud of its own that lasts some twelve
            // seconds (ChaffClouds), BD Armory's own flat picture of it hidden for the one to three seconds it lasts; none begun, its own)
            if (!f.Done)
            {
                f.Done = true;
                Vector3 going = Bda.ChaffGoing != null ? Bda.ChaffGoing(f.It) : Vector3.zero;
                if (ChaffClouds.Burst(f.Go.transform.position, going, now)) { f.Counted = true; Ours[Chaff]++; }
                else { f.Refused = true; Theirs[Chaff]++; }
            }
            if (f.Counted)
            {
                f.Smoke.Set(true, cutting);
                anyHidden = true;
            }
        }

        // ---------------------------------------------------------------- bubble screens (CMBubble) and sonar decoys (CMDecoy)

        void SayBubbles(Fx f, bool cutting)
        {
            // (each frame while it lets out its air, ten seconds: white water over it (Splashes.Boil), BD Armory's own grey puff under the
            // water hidden; none before it is in the water; where there is no sea of Natural Ocean's, its own)
            int boiling = Splashes.Boil(f.Go.transform.position, now - f.Began, now);
            if (boiling < 0)
            {
                if (!f.Counted && !f.Refused) { f.Refused = true; Theirs[Bubbles]++; }
                return;
            }
            if (boiling == 0 || f.Refused) return;
            if (!f.Counted) { f.Counted = true; Ours[Bubbles]++; }
            f.Smoke.Set(true, cutting);
            anyHidden = true;
        }

        void SayDecoy(Fx f)
        {
            // (each frame of its half minute under the water: a line of bubbles on the water over it while it is near the surface
            // (Splashes.Trail); BD Armory's own specks of foam about it left as they are. Its noise done (BD Armory's own shrunk to
            // nothing, its last specks fading), no more)
            if (f.Go.transform.localScale.sqrMagnitude < 1e-6f) return;
            if (Splashes.Trail(f.Go.transform.position, now) && !f.Counted) { f.Counted = true; Ours[Decoy]++; }
        }

        // ---------------------------------------------------------------- missiles (MissileLauncher)

        void SayMissiles()
        {
            if (Bda.MissileState == null || Bda.MissileThrottle == null) return;
            bool want = Armory.Missiles == null || Armory.Missiles;
            bool cutting = cutWas;
            for (int n = 0; n < launchers.Count; n++)
            {
                Launcher m = launchers[n];
                PartModule module = m.Module;
                // (destroyed: BD Armory's own at once, not at the next look, up to a second on: its exhaust, put back in BD Armory's pool, may
                // be the next missile's from the same launcher)
                if (module == null) { Release(m); m.Burning = false; continue; }
                Part p = module.part;
                Vessel v = p != null ? p.vessel : null;
                bool torpedo = Bda.MissileClass != null && Bda.Torpedo >= 0 && Bda.MissileClass(module) == Bda.Torpedo;
                if (!want && !torpedo) { Release(m); continue; }
                if (v == null || v.packed) continue;
                int state = Bda.MissileState(module);
                float throttle = Bda.MissileThrottle(module);
                // (its thrust as it is: nought while it coasts between its boost and its cruise, still in the boost's state, its motor out; its
                // boost's or cruise's thrust only where the thrust now cannot be read)
                float kN = Bda.MissileNow != null ? Bda.MissileNow(module)
                         : state == 2 ? (Bda.MissileBoost != null ? Bda.MissileBoost(module) : 0f) : (Bda.MissileCruise != null ? Bda.MissileCruise(module) : 0f);
                // (burning: boosting or cruising (BD Armory's states 2 and 3), its throttle open, with thrust)
                bool burning = (state == 2 || state == 3) && throttle > 0.01f && kN > 0f;
                // (a torpedo: no flame or smoke, its own specks of foam under the water left as BD Armory's, and while it drives itself under the
                // sea (BD Armory's own wake runs only there) its track laid on the water over it: Splashes.Wake. With the splashes' switch.)
                if (torpedo)
                {
                    if (burning && v.altitude < 0.0 && Armory.Wanted(Armory.Splashes)) Splashes.Wake(p.transform.position, now);
                    continue;
                }
                if (!burning)
                {
                    // (the last of its trail fades over a few seconds after its motor stops, hidden as it was; then BD Armory's own again)
                    if (m.Burning && now - m.BurnedAt > 5f) { Release(m); m.Burning = false; }
                    continue;
                }
                if (!m.Built) Build(m, p);
                if (!m.Burning) { m.Burning = true; m.Lit = now; Seen[Missile]++; m.Counted = m.Refused = false; }
                m.BurnedAt = now;
                Transform t = p.transform;
                Vector3 rim = Nozzle(state == 2 ? m.Boosters : m.Nozzles, state == 2 ? m.Nozzles : m.Boosters, t);
                // (as big as a solid motor of its thrust, as Keystone's Engines guesses a nozzle: an AIM-120's boost about 0.18 m; its flame as Engine
                // Flames draws a solid motor's, and its smoke as Rocket Smoke's: thick, white with a little grey, and it stays)
                float r = Mathf.Clamp(0.0245f * Mathf.Sqrt(kN), 0.08f, 0.5f);
                float flame = Armory.SolidFlame(r) * (0.3f + 0.7f * Mathf.Sqrt(throttle));
                bool six = Feed.Knows;
                // (it thrusts along its part's forward: its exhaust goes back)
                bool taken = Feed.Burn(m.Id, rim, -t.forward, v.srf_velocity, r, flame, throttle, 1f, 0.82f, 0.06f, 16f, 1, six ? throttle : 0f, t, Feed.Engine);
                int flags = six ? Feed.Took(m.Id) : -1;
                if (flags < 0) flags = !six && taken ? 1 : 0;
                if ((flags & 1) != 0) m.SmokeAt = now;
                if ((flags & 2) != 0) m.FlameAt = now;
                bool smoke = now - m.SmokeAt < Hold, lit = now - m.FlameAt < Hold;
                m.Smoke.Set(smoke, cutting);
                m.Flame.Set(lit, cutting);
                if (smoke || lit) { anyHidden = true; if (!m.Counted) { m.Counted = true; Ours[Missile]++; } }
                else if (!m.Refused && now - m.Lit > 0.1f) { m.Refused = true; Theirs[Missile]++; }
            }
        }

        static Vector3 Nozzle(List<Transform> first, List<Transform> then, Transform part)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int pass = 0; pass < 2 && count == 0; pass++)
            {
                List<Transform> list = pass == 0 ? first : then;
                for (int n = 0; n < list.Count; n++)
                    if (list[n] != null && list[n].gameObject.activeInHierarchy) { sum += list[n].position; count++; }
            }
            return count > 0 ? sum / count : part.position;
        }

        /// <summary>A missile's emitters (its part's own and the exhaust BD Armory hangs on its nozzles), once, the first time it burns: its smoke the ones that lay their particles in the world, its flame those that keep them with the missile; its steering jets left as they are.</summary>
        static void Build(Launcher m, Part p)
        {
            m.Built = true;
            m.Smoke = Hidden.Of(Own(p, MissileSmokeOf));
            m.Flame = Hidden.Of(Own(p, MissileFlameOf));
            m.Nozzles.Clear(); m.Boosters.Clear();
            foreach (Transform t in p.FindModelTransforms("exhaustTransform")) if (t != null) m.Nozzles.Add(t);
            string boost = Bda.MissileBoostNozzles != null ? Bda.MissileBoostNozzles(m.Module) : null;
            if (!string.IsNullOrEmpty(boost)) foreach (Transform t in p.FindModelTransforms(boost)) if (t != null) m.Boosters.Add(t);
        }

        static void Release(Launcher m) { m.Smoke.Show(); m.Flame.Show(); }

        // ---------------------------------------------------------------- guns' smoke (ModuleWeapon)

        void SayGuns()
        {
            if (Bda.GunFired == null) return;
            bool want = Armory.Guns == null || Armory.Guns;
            bool cutting = cutWas;
            for (int n = 0; n < guns.Count; n++)
            {
                Firearm g = guns[n];
                PartModule module = g.Module;
                if (module == null) { g.Smoke.Show(); continue; }
                Part p = module.part;
                Vessel v = p != null ? p.vessel : null;
                if (!want) { g.Smoke.Show(); continue; }
                if (v == null || v.packed) continue;
                // (a shot is told by the time it was fired changing)
                float fired = Bda.GunFired(module);
                if (!g.Known) { g.Known = true; g.Fired = fired; continue; }
                if (fired != g.Fired)
                {
                    g.Fired = fired;
                    if (!g.Firing) { Seen[Gun]++; g.Counted = g.Refused = false; g.BurstAt = now; }
                    g.ShotAt = now;
                }
                g.Firing = now - g.ShotAt < 0.3f;
                if (!g.Firing)
                {
                    // (its last puffs hang a few seconds after it stops, hidden as they were; then BD Armory's own again)
                    if (g.Smoke.On && now - g.ShotAt > 5f) g.Smoke.Show();
                    continue;
                }
                if (!g.Built) Build(g, p);
                if (g.Muzzles.Count == 0) continue;
                float caliber = Bda.GunCaliber != null ? Bda.GunCaliber(module) : 30f;
                float size = Mathf.Clamp(0.012f * caliber, 0.15f, 1.5f);
                bool taken = true;
                for (int k = 0; k < g.Muzzles.Count && k < 8; k++)
                {
                    Transform t = g.Muzzles[k];
                    if (t == null) continue;
                    if (!Feed.Smoking(g.Id + k, t.position, v.srf_velocity, size, 0.6f, 0.7f, 0.04f, 5f)) taken = false;
                }
                if (taken) g.TakenAt = now;
                bool hide = now - g.TakenAt < Hold;
                g.Smoke.Set(hide, cutting);
                if (hide) { anyHidden = true; if (!g.Counted) { g.Counted = true; Ours[Gun]++; } }
                else if (!g.Refused && now - g.BurstAt > 0.1f) { g.Refused = true; Theirs[Gun]++; }
            }
        }

        /// <summary>A gun's smoke: the emitters on its part that lay their particles in the world (BD Armory puffs them at every shot; its flashes are the ones that do not), at its muzzles.</summary>
        static void Build(Firearm g, Part p)
        {
            g.Built = true;
            List<KSPParticleEmitter> smoke = Own(p, GunSmokeOf);
            foreach (KSPParticleEmitter e in smoke) g.Muzzles.Add(e.transform);
            g.Smoke = Hidden.Of(smoke);
        }

        // (a gun's smoke lingers: a world-space emitter whose particles live less than half a second is a flash, as the GoalKeeper's is, and
        // the muzzle flash is BD Armory's own)
        static readonly Func<KSPParticleEmitter, bool> GunSmokeOf = e => e.useWorldSpace && e.maxEnergy >= 0.5f;

        /// <summary>
        /// The emitters that are a part's own (inactive ones too) that the test picks out: under its transform with no other part between (a
        /// craft's parts hang from the part they are on), and none of BD Armory's effects hung on it for a while, a fire, a leak, a missile's
        /// flames, a hit (the pools' objects, which go back to their pool and are used again elsewhere).
        /// </summary>
        static List<KSPParticleEmitter> Own(Part p, Func<KSPParticleEmitter, bool> pick)
        {
            var chosen = new List<KSPParticleEmitter>();
            foreach (KSPParticleEmitter e in p.GetComponentsInChildren<KSPParticleEmitter>(true))
                if (e != null && pick(e) && OwnOf(e, p)) chosen.Add(e);
            return chosen;
        }

        static bool OwnOf(KSPParticleEmitter e, Part p)
        {
            Transform top = p.transform;
            for (Transform t = e.transform; t != null; t = t.parent)
            {
                if (t == top) return true;
                if (t.GetComponent<Part>() != null) return false;
                if (Pooled(t, Bda.FireFX) || Pooled(t, Bda.LeakFX) || Pooled(t, Bda.Flames) || Pooled(t, Bda.HitFX)) return false;
            }
            return false;
        }

        static bool Pooled(Transform t, Type type) => type != null && t.GetComponent(type) != null;

        // ---------------------------------------------------------------- a nuclear explosion (BD Armory's NukeFX)

        /// <summary>
        /// BD Armory's nuclear explosions, once a frame: each NukeFX in its pools, from when it is set (its StartTime) to when it goes back to
        /// its pool, a few seconds after it has gone off. As it goes off (its hasDetonated, or its going back to its pool once its time has
        /// come: see the end) it is said to the explosions mod's air, once
        /// (Feed.Nuke: all of it is worked out there, from its yield, for minutes). Where the air took it, BD Armory's own light for it is
        /// switched off while the NukeFX lasts, and the effects it set off as it went off (its flash, shock, blast, plume and debris:
        /// FXEmitters, pooled by their models; those of its own models that began within a second and a half of its going off) are hidden
        /// until each goes back to its pool: its blast and its plume last up to a minute and a half, longer than the NukeFX itself. Its
        /// sound and its damage are its own.
        /// </summary>
        void SayNukes()
        {
            if (Bda.Nuke == null || Bda.NukePools == null || Bda.NukeDetonated == null || Bda.NukeYield == null || Bda.NukeAt == null) return;
            Pools();
            bool want = Armory.Wanted(Armory.Nukes), cutting = cutWas;
            for (int p = 0; p < nukePools.Count; p++)
            {
                List<GameObject> list = Bda.PoolList(nukePools[p]);
                if (list == null) continue;
                for (int n = 0; n < list.Count; n++)
                {
                    GameObject go = list[n];
                    if (go == null || !go.activeInHierarchy) continue;
                    int key = go.GetInstanceID();
                    if (!bombOf.TryGetValue(key, out Bomb b))
                    {
                        b = new Bomb { Go = go, It = go.GetComponent(Bda.Nuke), Key = key };
                        bombOf[key] = b;
                        bombs.Add(b);
                    }
                    b.SeenAt = frame;
                    if (b.It == null) continue;
                    float began = Bda.NukeStart != null ? Bda.NukeStart(b.It) : 0f;
                    if (!b.Active || began != b.Start) { if (b.Active) EndBomb(b); BeginBomb(b, began); }
                    if (!b.Fired && Bda.NukeDetonated(b.It)) Went(b, want);
                    if (!b.Taken) continue;
                    HideLight(b);
                    if (now - b.FiredAt < 1.5f) HideFx(b, cutting);
                }
            }
            for (int n = 0; n < bombs.Count; n++)
            {
                Bomb b = bombs[n];
                if (b.Active && b.SeenAt != frame)
                {
                    // Gone back to its pool without its going off seen. BD Armory's NukeFX goes off and, with nothing in its reach to
                    // strike and the time of its own picture (its MaxTime) already past, as when it was set to go off later than that,
                    // puts itself away in the same physics step: its hasDetonated set and cleared again before any frame can see it.
                    // (Found 2026-11-09: Tests.BdNuke at 100 m and 2.5 s on the grass, BD Armory's own drawn and not ours, "nukes:
                    // seen 0".) One gone back once its time had come has gone off.
                    if (!b.Fired && b.It != null && Bda.NukeDelay != null && now - b.Start >= Bda.NukeDelay(b.It)) Went(b, want);
                    EndBomb(b);
                }
                // (and what it set off as it went, hidden as it comes for a second and a half, as for one that is still about)
                if (!b.Active && b.Taken && now - b.FiredAt < 1.5f) HideFx(b, cutting);
            }
            FxGone();
        }

        /// <summary>A bomb gone off: said to the air, unless it went off a while ago (as when switched back to ours from BD Armory's own),
        /// when it is left its own.</summary>
        void Went(Bomb b, bool want)
        {
            b.Fired = true;
            b.FiredAt = now;
            Seen[Nuke]++;
            bool late = Bda.NukeDelay != null && now - b.Start > Bda.NukeDelay(b.It) + 0.5f;
            if (want && !late) SayNuke(b);
            if (b.Taken) Ours[Nuke]++; else Theirs[Nuke]++;
        }

        void BeginBomb(Bomb b, float began)
        {
            b.Active = true;
            b.Start = began;
            b.Fired = b.Taken = false;
            b.FiredAt = -100f;
            // (the models it sets off as it goes off: what is looked for among BD Armory's pooled effects then)
            var paths = new List<string>();
            foreach (Func<object, string> path in Bda.NukePaths)
            {
                string one = path != null ? path(b.It) : null;
                if (!string.IsNullOrWhiteSpace(one)) paths.Add(one);
            }
            b.Paths = paths.ToArray();
        }

        void EndBomb(Bomb b)
        {
            b.Active = false;
            ShowLight(b);
        }

        /// <summary>The bomb said to the air as it goes off: where it is, how many kilotons, how it moves.</summary>
        void SayNuke(Bomb b)
        {
            float kt = Bda.NukeYield(b.It);
            Vector3 at = Bda.NukeAt(b.It);
            Vector3d going = Bda.NukeGoing != null ? (Vector3d)Bda.NukeGoing(b.It) : Vector3d.zero;
            b.Taken = kt > 0f && Feed.Nuke(at, kt, going);
        }

        void HideLight(Bomb b)
        {
            if (b.LightHidden || Bda.NukeLight == null) return;
            Light light = Bda.NukeLight(b.It);
            if (light == null) return;
            b.Light = light;
            b.LightWas = light.enabled;
            light.enabled = false;
            b.LightHidden = true;
            anyHidden = true;
        }

        static void ShowLight(Bomb b)
        {
            if (!b.LightHidden) return;
            b.LightHidden = false;
            if (b.Light != null) b.Light.enabled = b.LightWas;
            b.Light = null;
        }

        /// <summary>The effects a bomb of ours has set off as it went off, found among BD Armory's pools of them by their models, and hidden.</summary>
        void HideFx(Bomb b, bool cutting)
        {
            if (b.Paths == null || b.Paths.Length == 0) return;
            for (int p = 0; p < emitterPools.Count; p++)
            {
                if (!Of(emitterKeys[p], b.Paths)) continue;
                List<GameObject> list = Bda.PoolList(emitterPools[p]);
                if (list == null) continue;
                for (int n = 0; n < list.Count; n++)
                {
                    GameObject go = list[n];
                    if (go == null || !go.activeInHierarchy) continue;
                    int key = go.GetInstanceID();
                    if (heldKeys.Contains(key)) continue;
                    Component it = Bda.Emitter != null ? go.GetComponent(Bda.Emitter) : null;
                    float began = it != null && Bda.EmitterStart != null ? Bda.EmitterStart(it) : b.FiredAt;
                    if (began < b.FiredAt - 0.5f || began > b.FiredAt + 1.5f) continue;
                    var held = new HeldFx { Go = go, It = it, Key = key, Start = began, Own = Hidden.Of(go, null) };
                    held.Own.Set(true, cutting);
                    heldFx.Add(held);
                    heldKeys.Add(key);
                    anyHidden = true;
                }
            }
        }

        // (a pool's key is its model's path and its sound's, one after the other)
        static bool Of(string key, string[] paths)
        {
            foreach (string path in paths) if (key.StartsWith(path, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The effects hidden for a bomb of ours that have gone back to their pool, or been set off afresh since: shown again.</summary>
        void FxGone()
        {
            for (int n = heldFx.Count - 1; n >= 0; n--)
            {
                HeldFx h = heldFx[n];
                if (h.Go != null && h.Go.activeInHierarchy && (h.It == null || Bda.EmitterStart == null || Bda.EmitterStart(h.It) == h.Start)) continue;
                h.Own.Show();
                heldKeys.Remove(h.Key);
                heldFx.RemoveAt(n);
            }
        }

        /// <summary>BD Armory's pools of nuclear explosions and of the effects they set off, listed afresh when either's count changes (a pool is made the first time its model is used).</summary>
        void Pools()
        {
            if (Bda.NukePools() is System.Collections.IDictionary bombsBy && bombsBy.Count != nukePoolsCount)
            {
                nukePoolsCount = bombsBy.Count;
                nukePools.Clear();
                foreach (object pool in bombsBy.Values) if (pool != null) nukePools.Add(pool);
            }
            if (Bda.EmitterPools != null && Bda.EmitterPools() is System.Collections.IDictionary fxBy && fxBy.Count != emitterPoolsCount)
            {
                emitterPoolsCount = fxBy.Count;
                emitterPools.Clear();
                emitterKeys.Clear();
                foreach (System.Collections.DictionaryEntry e in fxBy)
                    if (e.Value != null) { emitterPools.Add(e.Value); emitterKeys.Add(e.Key as string ?? ""); }
            }
        }

        // ---------------------------------------------------------------- once a second

        /// <summary>The missiles and guns on the loaded craft, the pools of rockets (one for each kind of rocket fired yet), and the objects gone.</summary>
        void Look()
        {
            // (each kind of rocket has its pool, made the first time one is fired)
            rocketPools.Clear();
            if (Bda.RocketPools != null && Bda.RocketPools() is System.Collections.IDictionary pools)
                foreach (object pool in pools.Values) if (pool != null) rocketPools.Add(pool);
            // (and those of nuclear explosions and their effects, listed afresh next frame: one made anew may leave the count as it was)
            nukePoolsCount = emitterPoolsCount = -1;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || !v.loaded) continue;
                foreach (Part part in v.parts)
                {
                    if (part == null || part.Modules == null) continue;
                    for (int m = 0; m < part.Modules.Count; m++)
                    {
                        PartModule module = part.Modules[m];
                        if (module == null || known.Contains(module)) continue;
                        if (Bda.Missile != null && Bda.Missile.IsInstanceOfType(module))
                        {
                            known.Add(module);
                            launchers.Add(new Launcher { Module = module, Id = NextId() });
                        }
                        else if (Bda.Weapon != null && Bda.Weapon.IsInstanceOfType(module))
                        {
                            known.Add(module);
                            guns.Add(new Firearm { Module = module, Id = NextId() });
                        }
                    }
                }
            }
            // (gone: destroyed, or on a craft no longer loaded)
            for (int n = launchers.Count - 1; n >= 0; n--)
            {
                Launcher m = launchers[n];
                if (m.Module != null && m.Module.part != null && m.Module.part.vessel != null && m.Module.part.vessel.loaded) continue;
                Release(m);
                LetGo(ref m.Id);
                if (m.Module != null) known.Remove(m.Module);
                launchers.RemoveAt(n);
            }
            for (int n = guns.Count - 1; n >= 0; n--)
            {
                Firearm g = guns[n];
                if (g.Module != null && g.Module.part != null && g.Module.part.vessel != null && g.Module.part.vessel.loaded) continue;
                g.Smoke.Show();
                LetGo(ref g.Id);
                if (g.Module != null) known.Remove(g.Module);
                guns.RemoveAt(n);
            }
            known.RemoveWhere(m => m == null);
            for (int n = all.Count - 1; n >= 0; n--)
            {
                if (all[n].Go != null) continue;
                Release(all[n]);
                LetGo(ref all[n].Id);
                fx.Remove(all[n].Key);
                all.RemoveAt(n);
            }
            for (int n = bombs.Count - 1; n >= 0; n--)
            {
                if (bombs[n].Go != null) continue;
                ShowLight(bombs[n]);
                bombOf.Remove(bombs[n].Key);
                bombs.RemoveAt(n);
            }
        }

        // ---------------------------------------------------------------- everything shown again

        /// <summary>
        /// BD Armory's own, everything: switched off, the mod off, the scene going, or the explosions mod no longer asking. Only shown: each
        /// keeps its number and its start, so that switched back each goes on as it was (a fire not said afresh as a new one, a hit not
        /// burst again); what was put away meanwhile is ended, and what was started again begun, by the next scan as ever.
        /// </summary>
        internal void ShowAll()
        {
            for (int n = 0; n < all.Count; n++) { all[n].Promised = false; Release(all[n]); }
            foreach (Launcher m in launchers) Release(m);
            foreach (Firearm g in guns) g.Smoke.Show();
            foreach (HeldFx h in heldFx) h.Own.Show();
            heldFx.Clear();
            heldKeys.Clear();
            foreach (Bomb b in bombs) { ShowLight(b); b.Taken = false; }
            anyHidden = false;
        }

        /// <summary>The page's cutparticles changed: what is hidden just now has its particles worked out, or not, as it now says.</summary>
        void Recut(bool cutting)
        {
            foreach (Fx f in all) { if (f.Smoke.On) f.Smoke.Set(true, cutting); if (f.Flame.On) f.Flame.Set(true, cutting); }
            foreach (Launcher m in launchers) { if (m.Smoke.On) m.Smoke.Set(true, cutting); if (m.Flame.On) m.Flame.Set(true, cutting); }
            foreach (Firearm g in guns) if (g.Smoke.On) g.Smoke.Set(true, cutting);
            foreach (HeldFx h in heldFx) if (h.Own.On) h.Own.Set(true, cutting);
        }

        /// <summary>The words for the mod's line in Keystone's list: how many of BD Armory's effects are drawn as ours just now, and how many as its own.</summary>
        void Tell()
        {
            if (Why.Length > 0) { Report = Why; return; }
            if (!Armory.On) { Report = ""; return; }
            if (!Armory.Ours) { Report = "BD Armory's own effects are drawn (switched to them)."; return; }
            if (!Feed.Ready()) { Report = "Volumetric Explosions cannot draw volumes here: BD Armory's own effects are left."; return; }
            int ours = 0, theirs = 0;
            foreach (Fx f in all) if (f.Active) { if (f.Smoke.On || f.Flame.On) ours++; else theirs++; }
            foreach (Launcher m in launchers) if (m.Burning) { if (m.Smoke.On || m.Flame.On) ours++; else theirs++; }
            foreach (Firearm g in guns) if (g.Firing) { if (g.Smoke.On) ours++; else theirs++; }
            foreach (Bomb b in bombs) if (b.Active && b.Fired) { if (b.Taken) ours++; else theirs++; }
            Report = (ours + theirs == 0 ? "None of BD Armory's effects just now." : ours + " of BD Armory's effects drawn as volumes, " + theirs + " as its own.")
                     + (Feed.Knows ? "" : " (Volumetric Explosions is older than this mod: hits and smoke screens are BD Armory's own, and so are flames.)")
                     + (Bda.Missing.Length > 0 ? " " + Bda.Missing : "");
        }
    }
}
