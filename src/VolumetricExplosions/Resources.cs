using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VolumetricExplosions
{
    public static partial class Records
    {
        /// <summary>
        /// What a resource counts as when a part holding it goes up. The stock ones are known by name in Sort; other
        /// mods' are looked up here first, and only guessed at by their names when they are not listed.
        ///
        /// The list is PluginData/resources.cfg (one line a resource: "LqdMethane = LiquidFuel"; the kinds are the stock
        /// resources' names, and Hydrogen, Vapour and None), and any VOLUMETRIC_EXPLOSIONS_RESOURCES node another mod
        /// or a ModuleManager patch puts among the game's configs, which may add to it or override it (RealBattery.cfg,
        /// beside the library, is one such: it takes only with RealBattery installed). The list is in PluginData, as
        /// settings.cfg is, so that changing it costs the game no slow start.
        ///
        /// A line may end in a factor, "LqdHydrogen = Hydrogen *2.8": the resource counts for so many times its weight of
        /// the stock one (for electric charge, so many times its units), for what it is worth beside it. Without one, as
        /// much as it weighs.
        /// </summary>
        enum Kind { None, Fuel, Hydrogen, Oxidizer, Solid, Mono, Gas, Vapour, Ore, Charge }

        struct Counts { public Kind kind; public double factor; }

        static readonly Dictionary<string, Counts> kinds = new Dictionary<string, Counts>();

        /// <summary>Read the list again, as the settings are read: at the start of each flight.</summary>
        public static void LoadKinds()
        {
            kinds.Clear();
            int read = 0;
            try
            {
                string path = Settings.Folder + "PluginData/resources.cfg";
                if (File.Exists(path))
                {
                    ConfigNode file = ConfigNode.Load(path);
                    if (file != null) foreach (ConfigNode node in file.GetNodes("VOLUMETRIC_EXPLOSIONS_RESOURCES")) read += Take(node);
                }
                if (GameDatabase.Instance != null)
                    foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes("VOLUMETRIC_EXPLOSIONS_RESOURCES")) read += Take(node);
            }
            catch (Exception ex) { Addon.Log("could not read resources.cfg (" + ex.Message + "); other mods' resources are taken by their names"); }
            if (Settings.Log) Addon.Log(read + " of other mods' resources known by name (resources.cfg)");
        }

        static int Take(ConfigNode node)
        {
            int taken = 0;
            foreach (ConfigNode.Value v in node.values)
            {
                if (v == null || string.IsNullOrEmpty(v.name)) continue;
                // "Kind", or "Kind *factor".
                string text = (v.value ?? "").Trim(), word = text;
                double factor = 1.0;
                int star = text.IndexOf('*');
                if (star >= 0)
                {
                    word = text.Substring(0, star).Trim();
                    if (!double.TryParse(text.Substring(star + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out factor) || factor <= 0.0)
                    {
                        Addon.Log("resources.cfg: \"" + v.name + " = " + v.value + "\" has no factor that can be read after the *; taken as 1");
                        factor = 1.0;
                    }
                }
                Kind kind;
                switch (word.ToLowerInvariant())
                {
                    case "liquidfuel": case "fuel": kind = Kind.Fuel; break;
                    case "hydrogen": kind = Kind.Hydrogen; break;
                    case "oxidizer": case "oxidiser": kind = Kind.Oxidizer; break;
                    case "solidfuel": case "solid": kind = Kind.Solid; break;
                    case "monopropellant": case "mono": kind = Kind.Mono; break;
                    case "xenongas": case "gas": kind = Kind.Gas; break;
                    case "vapour": case "vapor": kind = Kind.Vapour; break;
                    case "ore": kind = Kind.Ore; break;
                    case "electriccharge": case "charge": kind = Kind.Charge; break;
                    case "none": case "ignore": kind = Kind.None; break;
                    default:
                        Addon.Log("resources.cfg: \"" + v.name + " = " + v.value + "\" names no kind this mod knows; left out");
                        continue;
                }
                kinds[v.name.Trim().ToLowerInvariant()] = new Counts { kind = kind, factor = factor };
                taken++;
            }
            return taken;
        }

        /// <summary>Count the resource as its listed kind, if it has one, times its factor. (As Sort does: electricity in units, everything else in kilograms.)</summary>
        static bool Listed(Death d, string name, double kg, double units)
        {
            if (!kinds.TryGetValue(name, out Counts as_)) return false;
            kg *= as_.factor;
            switch (as_.kind)
            {
                case Kind.Fuel: d.fuel += kg; break;
                case Kind.Hydrogen: d.fuel += kg; d.hydrogen = true; break;
                case Kind.Oxidizer: d.oxidizer += kg; break;
                case Kind.Solid: d.solid += kg; break;
                case Kind.Mono: d.mono += kg; break;
                case Kind.Gas: d.gas += kg; break;
                case Kind.Vapour: d.vapour += kg; break;
                case Kind.Ore: d.ore += kg; break;
                case Kind.Charge: d.charge += units * as_.factor; break;
            }
            return true;
        }
    }
}
