using System;
using UnityEngine;

namespace VolumetricArmory
{
    /// <summary>
    /// BD Armory's fire and smoke drawn as volumes, in the explosions mod's own air, with a switch between those and BD Armory's
    /// own. A part of Volumetric Explosions since its 0.10.0 (the owner, 2026-11-10: "Volumetric Armory should not be a separate
    /// mod. It should be integrated into the Volumetric Explosions"); made from 2026-11-04 as a mod of its own on Keystone,
    /// Volumetric Armory, which was never released. Its code keeps that name as its namespace.
    ///
    /// Asked for (2026-11-04): "make a new thing: BD armor that is not particles and includes all of the [volu]metric stuff,
    /// the [volu]metric fire, the [volu]metric smoke, and all that ... like the black rack, switch between our clouds and his
    /// clouds. Do the same: switch between BD armor particles and our stuff." (The design is research/armory/DESIGN.md.)
    ///
    /// BD Armory draws its effects as flat particles. This finds them as they start (by name, through BD Armory's own pools and
    /// part modules: nothing of it is referenced, copied or patched) and says each to the air, as Rocket Smoke says an engine's
    /// smoke: a burning part, a holed tank's spray, a missile's flame and trail, a rocket's trail, a gun's smoke, the smoke of
    /// a hit, a flare's trail and a smoke screen; and a nuclear explosion, worked out from the physics of one (asked for on
    /// 2026-11-09: "compatible with everything in the mod, including the nuke"). Where the air takes one, BD Armory's own
    /// particles for it are hidden (their renderers only: BD Armory goes on as it was, its fires still burn and its smoke still
    /// blinds, its nuke still roars and still does its damage); where the air does not (no room, too many flames, vacuum, under
    /// water) BD Armory's own are left. Its explosions are the explosions' own catch (Warheads), which follows the same switch.
    ///
    /// Without BD Armory there is nothing for it to do, and it does nothing. Its settings are the explosions mod's own
    /// (settings.cfg's "armory" lines, and the same on its page in Keystone's window where Keystone is installed): as that mod,
    /// it needs no other.
    /// </summary>
    static class Armory
    {
        /// <summary>One of the settings, as the code here asks it: on or off (never null; a null one, as before the settings were made, reads off).</summary>
        public sealed class Switch
        {
            readonly Func<bool> get;
            public Switch(Func<bool> get) { this.get = get; }
            public static implicit operator bool(Switch s) => s != null && s.get();
        }

        public static readonly Switch On = new Switch(() => VolumetricExplosions.Settings.Armory), Ours = new Switch(() => VolumetricExplosions.Settings.ArmoryOurs);
        // (the kinds, each on its own; and whether BD Armory's hidden particles are left to be worked out unseen: fine details on the page)
        public static readonly Switch Explosions = new Switch(() => VolumetricExplosions.Settings.BDArmory),
            Fires = new Switch(() => VolumetricExplosions.Settings.ArmoryFires), Leaks = new Switch(() => VolumetricExplosions.Settings.ArmoryLeaks),
            Missiles = new Switch(() => VolumetricExplosions.Settings.ArmoryMissiles), Rockets = new Switch(() => VolumetricExplosions.Settings.ArmoryRockets),
            Guns = new Switch(() => VolumetricExplosions.Settings.ArmoryGuns), Hits = new Switch(() => VolumetricExplosions.Settings.ArmoryHits),
            Countermeasures = new Switch(() => VolumetricExplosions.Settings.ArmoryCountermeasures), Nukes = new Switch(() => VolumetricExplosions.Settings.ArmoryNukes),
            Splashes = new Switch(() => VolumetricExplosions.Settings.ArmorySplashes), Cut = new Switch(() => VolumetricExplosions.Settings.ArmoryCut);

        /// <summary>Whether ours are drawn for this kind of BD Armory's effects (none named: for any): this on, ours chosen over BD Armory's own, and the kind on.</summary>
        public static bool Wanted(Switch kind) => On && Ours && (kind == null || kind);

        /// <summary>Ours (true) or BD Armory's own, as the key and the tests set it: kept on the page and in the file where there is a page.</summary>
        public static void Choose(bool ours)
        {
            VolumetricExplosions.Settings.ArmoryOurs = ours;
            VolumetricExplosions.Page.Took();
        }

        /// <summary>
        /// The key that flips between ours and BD Armory's own, pressed this frame with the game's modifier key held (Alt on Windows
        /// and Linux, Option on a Mac): settings.cfg's "armory_key", a key's name as Unity has it ("B", "F9"; "None" for no key).
        /// </summary>
        public static bool KeyPressed
        {
            get
            {
                KeyCode key = VolumetricExplosions.Settings.ArmoryKey;
                if (key == KeyCode.None || !Input.GetKeyDown(key)) return false;
                try { return GameSettings.MODIFIER_KEY.GetKey() && !InputLockManager.IsLocked(ControlTypes.KEYBOARDINPUT); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>How long a solid motor's flame is at full burn in thick air, metres, for a nozzle of this radius: where its smoke
        /// begins (as Keystone's Engines.Flame has it for a solid, which Rocket Smoke uses: one size for both).</summary>
        public static float SolidFlame(float radius) => radius * 44f * 1.15f;

        public static void Log(string text) => VolumetricExplosions.Addon.Log("BD Armory's effects: " + text);
    }
}
