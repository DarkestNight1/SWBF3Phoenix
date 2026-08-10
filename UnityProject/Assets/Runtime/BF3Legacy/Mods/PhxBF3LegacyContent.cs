using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Knowledge of the community "Star Wars Battlefront III Legacy" release as it
/// actually ships today - the all-in-one 3.1 demo pack - so Phoenix can host it
/// rather than merely notice it is installed.
///
/// The pack is not one mod: it is seven independent SWBF2 addon folders that
/// cross-register content into each other's maps. Each folder's addme.script
/// declares its maps into 'sp_missionselect_listbox_contents' with per-map
/// flags of the form
///
///     mapluafile = "CO3%s_%s", era_x = 1, mode_siege_x = 1, mode_con_x = 1
///
/// where '%s_%s' is filled with (era subst, mode subst) to form the mission
/// script name, e.g. "CO3x_siege". Two of those flag families are NOT stock
/// SWBF2 - the eras 'x'/'y' (BF3 Clone Wars / BF3 Galactic Civil War) and the
/// modes 'siege' (Orbital Assault) and 'uber'. In the original engine they are
/// defined by [GT]Anakin's UI Remaster via its AddNewGameModes() helper, which
/// is why the pack lists that mod as a hard requirement. Phoenix supplies the
/// same helper itself (see PhxBF3LegacyCompat), and this class is where the
/// resulting descriptors live.
///
/// Everything here is DATA about content the user supplies; no mod assets are
/// distributed with Phoenix.
/// </summary>
public static class PhxBF3LegacyContent
{
    /// <summary>The release this table was built against.</summary>
    public const string PackVersion = "3.1";

    // ------------------------------------------------------------------
    // Space layer classification (drives PhxVerticalBattlefront)
    // ------------------------------------------------------------------
    public enum PhxBF3Layer
    {
        Ground,     // planet surface: capital ships loom in-atmosphere
        Space,      // vacuum battlefield: ships sit in orbit
        Interior,   // inside a ship/station: no sky, no space layer
    }

    public class PhxBF3ModComponent
    {
        public string FolderName;      // addon folder, as shipped
        public string DisplayName;
        public string Author;
        public string Summary;
        public string[] MapPrefixes;   // map ids this component registers
    }

    public class PhxBF3MapInfo
    {
        public string Prefix;          // leading id of the mission script, e.g. "CO3"
        public string DisplayName;     // fallback only - the mod's own localization wins
        public string Planet;
        public PhxBF3Layer Layer;
        public string Component;       // owning addon folder
    }

    /// <summary>
    /// A game mode or era selectable in the mission list. 'Key' is the flag as
    /// it appears on a map entry ("mode_siege", "era_x"); 'Subst' is what gets
    /// substituted into the mapluafile pattern ("siege", "x").
    /// </summary>
    public class PhxBF3ModeInfo
    {
        public string Key;
        public string Subst;
        public bool IsEra;
        public string DisplayName;
        public string About;
        public string Icon;            // mode icon / era icon2, a UI texture name

        public PhxBF3ModeInfo Clone()
        {
            return new PhxBF3ModeInfo
            {
                Key = Key, Subst = Subst, IsEra = IsEra,
                DisplayName = DisplayName, About = About, Icon = Icon,
            };
        }
    }


    // ------------------------------------------------------------------
    // The 3.1 pack's components
    // ------------------------------------------------------------------
    static readonly PhxBF3ModComponent[] ComponentTable =
    {
        new PhxBF3ModComponent
        {
            FolderName  = "BF3",
            DisplayName = "Battlefront III Legacy - Pre-Demo 3.0",
            Author      = "El_Fabricio",
            Summary     = "The main mod: Coruscant, Cato Neimoidia and Bespin with " +
                          "Orbital Assault, Conquest and Team Deathmatch.",
            MapPrefixes = new[] { "CO3", "CN3", "BS3" },
        },
        new PhxBF3ModComponent
        {
            FolderName  = "BF3Era",
            DisplayName = "Battlefront III Legacy - Era Mod 1.4",
            Author      = "iamashaymin",
            Summary     = "Adds the BF3 eras and sides to the stock SWBF2 maps.",
            MapPrefixes = new[]
            {
                "cor1", "dag1", "dea1", "kam1", "mus1", "tan1", "tat2", "tat3",
                "myg1", "pol1", "yav1", "uta1", "end1", "nab2", "geo1", "fel1",
                "kas2", "hot1",
            },
        },
        new PhxBF3ModComponent
        {
            FolderName  = "BF3MoreMaps",
            DisplayName = "Battlefront III Legacy - MoreMaps Patch 1.9",
            Author      = "iamashaymin, El_Fabricio",
            Summary     = "Dantooine, Death Star II, extra modes for the main maps " +
                          "and the MP map ports.",
            MapPrefixes = new[]
            {
                "DN3", "DS2",
                "MP0", "MP1", "MP2", "MP4", "MP5", "MP6", "MP7", "MP8", "MP9",
                "MP10", "MP11", "MP12", "MP14",
            },
        },
        new PhxBF3ModComponent
        {
            FolderName  = "BF3GCWSpaceDemo",
            DisplayName = "Battlefront III Legacy - Galactic Civil War Space Demo",
            Author      = "iamashaymin",
            Summary     = "A Galactic Civil War space battle (1-flag and space assault).",
            MapPrefixes = new[] { "SP3" },
        },
        new PhxBF3ModComponent
        {
            FolderName  = "BF3Vjun",
            DisplayName = "Battlefront III Legacy - Extended Engagements",
            Author      = "bk2-modder",
            Summary     = "Vjun Orbital Assault, Sulon Conquest/CTF and the " +
                          "Lucrehulk interior.",
            MapPrefixes = new[] { "VF3", "SL3", "LUC" },
        },
        new PhxBF3ModComponent
        {
            FolderName  = "BF3Venator",
            DisplayName = "Battlefront III Legacy - Venator",
            Author      = "bk2-modder",
            Summary     = "Conquest aboard a Venator-class Star Destroyer.",
            MapPrefixes = new[] { "VEN" },
        },
        new PhxBF3ModComponent
        {
            FolderName  = "BF3Cato-Hunt",
            DisplayName = "Battlefront III Legacy - Cato Neimoidia: Hunt",
            Author      = "bk2-modder",
            Summary     = "Hunt mode on Cato Neimoidia (Clone Wars only).",
            MapPrefixes = new[] { "CN3" },
        },
    };

    // ------------------------------------------------------------------
    // Maps. Only the pack's OWN maps are listed - the Era Mod's entries reuse
    // stock SWBF2 map ids, which PhxVerticalBattlefront already classifies.
    //
    // Ordered longest-prefix-first at lookup time so "MP14" wins over "MP1".
    // ------------------------------------------------------------------
    static readonly PhxBF3MapInfo[] MapTable =
    {
        new PhxBF3MapInfo { Prefix = "CO3", DisplayName = "BF3: Coruscant",      Planet = "Coruscant",      Layer = PhxBF3Layer.Ground,   Component = "BF3" },
        new PhxBF3MapInfo { Prefix = "CN3", DisplayName = "BF3: Cato Neimoidia", Planet = "Cato Neimoidia", Layer = PhxBF3Layer.Ground,   Component = "BF3" },
        new PhxBF3MapInfo { Prefix = "BS3", DisplayName = "BF3: Bespin",         Planet = "Bespin",         Layer = PhxBF3Layer.Ground,   Component = "BF3" },
        new PhxBF3MapInfo { Prefix = "DN3", DisplayName = "BF3: Dantooine",      Planet = "Dantooine",      Layer = PhxBF3Layer.Ground,   Component = "BF3MoreMaps" },
        new PhxBF3MapInfo { Prefix = "VF3", DisplayName = "BF3: Vjun",           Planet = "Vjun",           Layer = PhxBF3Layer.Ground,   Component = "BF3Vjun" },
        new PhxBF3MapInfo { Prefix = "SL3", DisplayName = "BF3: Sulon",          Planet = "Sulon",          Layer = PhxBF3Layer.Ground,   Component = "BF3Vjun" },
        new PhxBF3MapInfo { Prefix = "SP3", DisplayName = "BF3: Space Battle",   Planet = "Deep Space",     Layer = PhxBF3Layer.Space,    Component = "BF3GCWSpaceDemo" },
        new PhxBF3MapInfo { Prefix = "DS2", DisplayName = "BF3: Death Star II",  Planet = "Death Star II",  Layer = PhxBF3Layer.Interior, Component = "BF3MoreMaps" },
        new PhxBF3MapInfo { Prefix = "VEN", DisplayName = "BF3: Venator",        Planet = "Venator",        Layer = PhxBF3Layer.Interior, Component = "BF3Venator" },
        new PhxBF3MapInfo { Prefix = "LUC", DisplayName = "BF3: Lucrehulk",      Planet = "Lucrehulk",      Layer = PhxBF3Layer.Interior, Component = "BF3Vjun" },
    };

    // MP<n> ports are stock-style ground maps; one rule beats fifteen entries.
    const string MoreMapsPortPrefix = "MP";

    // ------------------------------------------------------------------
    // Modes and eras the pack relies on. Stock SWBF2 already knows con, tdm,
    // ctf, 1flag, hunt, eli and space, and stock missionlist expands those on
    // its own - they are repeated here so the supplemental expansion can fill
    // them in for mod maps whose flags stock missionlist does not recognise.
    // ------------------------------------------------------------------
    static readonly PhxBF3ModeInfo[] DefaultModes =
    {
        // --- BF3 Legacy additions (absent from stock SWBF2) ---
        new PhxBF3ModeInfo { Key = "mode_siege", Subst = "siege", DisplayName = "Orbital Assault", Icon = "com_mode_orbital",
                             About = "Both factions battle each other with everything they have, from ground to space." },
        new PhxBF3ModeInfo { Key = "mode_uber",  Subst = "uber",  DisplayName = "Uber Mode",       Icon = "com_mode_conquest",
                             About = "The Era Mod's escalated battle ruleset." },

        // --- stock modes, for mod maps stock missionlist skips ---
        new PhxBF3ModeInfo { Key = "mode_con",   Subst = "con",   DisplayName = "Conquest",         Icon = "com_mode_conquest",
                             About = "Both teams have to conquer all command posts to win." },
        new PhxBF3ModeInfo { Key = "mode_tdm",   Subst = "tdm",   DisplayName = "Team Deathmatch",  Icon = "com_mode_teamdm",
                             About = "Two teams fight on the ground to reach a score limit." },
        // No icon: 'com_mode_eli' is declared by BF3MoreMaps' addme but no such
        // texture ships anywhere, and it fails to load at runtime. Leave it
        // unset unless a mod names one via AddNewGameModes.
        new PhxBF3ModeInfo { Key = "mode_eli",   Subst = "eli",   DisplayName = "Hero Deathmatch",
                             About = "Heroes fight on the ground to reach a score limit." },
        new PhxBF3ModeInfo { Key = "mode_hunt",  Subst = "hunt",  DisplayName = "Hunt",             Icon = "com_mode_hunt",
                             About = "Asymmetric hunt between a native faction and an invader." },
        // No icon: no com_mode_ctf / _1flag / _space texture exists in stock
        // shell.lvl or anywhere in the 3.1 pack, and a name that resolves to
        // nothing is worse than none. The mod's own AddNewGameModes data
        // replaces these at runtime if it ever names one.
        new PhxBF3ModeInfo { Key = "mode_ctf",   Subst = "ctf",   DisplayName = "Capture the Flag",
                             About = "Two-flag capture the flag." },
        new PhxBF3ModeInfo { Key = "mode_1flag", Subst = "1flag", DisplayName = "1-Flag CTF",
                             About = "One neutral flag; carry it to the enemy base." },
        new PhxBF3ModeInfo { Key = "mode_space", Subst = "space", DisplayName = "Space Assault",
                             About = "Fleet battle: destroy the enemy capital ship." },
    };

    static readonly PhxBF3ModeInfo[] DefaultEras =
    {
        new PhxBF3ModeInfo { Key = "era_x", Subst = "x", IsEra = true, DisplayName = "BF3: Clone Wars",           Icon = "rep_icon" },
        new PhxBF3ModeInfo { Key = "era_y", Subst = "y", IsEra = true, DisplayName = "BF3: Galactic Civil War",   Icon = "gcw3" },
        new PhxBF3ModeInfo { Key = "era_c", Subst = "c", IsEra = true, DisplayName = "Clone Wars",                Icon = "rep_icon" },
        new PhxBF3ModeInfo { Key = "era_g", Subst = "g", IsEra = true, DisplayName = "Galactic Civil War",        Icon = "all_icon" },
    };

    // Live registry: seeded from the defaults, then refined by whatever the
    // installed mods declare through AddNewGameModes().
    static Dictionary<string, PhxBF3ModeInfo> Registry;

    static Dictionary<string, PhxBF3ModeInfo> GetRegistry()
    {
        if (Registry == null)
        {
            Registry = new Dictionary<string, PhxBF3ModeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (PhxBF3ModeInfo m in DefaultModes) Registry[m.Key] = m.Clone();
            foreach (PhxBF3ModeInfo e in DefaultEras)  Registry[e.Key] = e.Clone();
        }
        return Registry;
    }


    // ==================================================================
    // Lookup
    // ==================================================================

    public static IReadOnlyList<PhxBF3ModComponent> Components => ComponentTable;

    public static PhxBF3ModComponent GetComponent(string folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return null;
        foreach (PhxBF3ModComponent c in ComponentTable)
        {
            if (string.Equals(c.FolderName, folderName, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    /// <summary>
    /// Map info for a mission script name ("CO3x_siege" -> Coruscant), or null
    /// if the script does not belong to the BF3 Legacy pack's own maps.
    /// </summary>
    public static PhxBF3MapInfo GetMapInfo(string mapScript)
    {
        if (string.IsNullOrEmpty(mapScript)) return null;

        PhxBF3MapInfo best = null;
        foreach (PhxBF3MapInfo m in MapTable)
        {
            if (mapScript.StartsWith(m.Prefix, StringComparison.OrdinalIgnoreCase) &&
                (best == null || m.Prefix.Length > best.Prefix.Length))
            {
                best = m;
            }
        }
        if (best != null) return best;

        // MoreMaps ports: MP<digits>. Guard on the digit so an unrelated
        // "mpxyz" addon map is not swallowed by this rule.
        if (mapScript.Length > MoreMapsPortPrefix.Length &&
            mapScript.StartsWith(MoreMapsPortPrefix, StringComparison.OrdinalIgnoreCase) &&
            char.IsDigit(mapScript[MoreMapsPortPrefix.Length]))
        {
            return new PhxBF3MapInfo
            {
                Prefix = MoreMapsPortPrefix,
                DisplayName = "BF3 Legacy: MoreMaps port",
                Planet = "Various",
                Layer = PhxBF3Layer.Ground,
                Component = "BF3MoreMaps",
            };
        }
        return null;
    }

    /// <summary>Mode/era descriptor for a map-entry flag key, or null.</summary>
    public static PhxBF3ModeInfo GetModeInfo(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return GetRegistry().TryGetValue(key, out PhxBF3ModeInfo info) ? info : null;
    }

    /// <summary>
    /// Record (or refine) a mode/era declared by an installed mod. Called from
    /// the Lua-side AddNewGameModes() shim; any field left null keeps whatever
    /// the built-in table already had.
    /// </summary>
    public static void RegisterModeInfo(string key, string displayName, string about, string icon)
    {
        if (string.IsNullOrEmpty(key)) return;

        bool isEra = key.StartsWith("era_", StringComparison.OrdinalIgnoreCase);
        bool isMode = key.StartsWith("mode_", StringComparison.OrdinalIgnoreCase);
        if (!isEra && !isMode) return;

        Dictionary<string, PhxBF3ModeInfo> reg = GetRegistry();
        if (!reg.TryGetValue(key, out PhxBF3ModeInfo info))
        {
            info = new PhxBF3ModeInfo
            {
                Key = key,
                Subst = key.Substring(isEra ? 4 : 5),
                IsEra = isEra,
                DisplayName = key,
            };
            reg[key] = info;
        }

        if (!string.IsNullOrEmpty(displayName)) info.DisplayName = displayName;
        if (!string.IsNullOrEmpty(about))       info.About = about;
        if (!string.IsNullOrEmpty(icon))        info.Icon = icon;

        Debug.Log($"[BF3Legacy] Registered {(isEra ? "era" : "mode")} '{key}' " +
                  $"as '{info.DisplayName}' (subst '{info.Subst}', icon '{info.Icon}')");
    }


    // ==================================================================
    // Mission list expansion
    // ==================================================================

    /// <summary>
    /// Reads a map's entry in sp/mp_missionselect_listbox_contents and returns
    /// the eras it declares. Stock missionlist only expands eras it knows, so
    /// the BF3 eras 'x' and 'y' are invisible to it; this reads the flags
    /// directly and therefore covers stock and modded eras alike.
    /// </summary>
    public static List<PhxBF3ModeInfo> ExpandEras(PhxLuaRuntime.Table mapEntry)
    {
        return Expand(mapEntry, true);
    }

    /// <summary>
    /// Same for modes. A map entry spells a mode out per era
    /// ("mode_con_x", "mode_con_y"), so the era suffix is stripped and
    /// duplicates collapsed - the menu crosses modes with eras itself.
    /// </summary>
    public static List<PhxBF3ModeInfo> ExpandModes(PhxLuaRuntime.Table mapEntry)
    {
        return Expand(mapEntry, false);
    }

    static List<PhxBF3ModeInfo> Expand(PhxLuaRuntime.Table mapEntry, bool eras)
    {
        List<PhxBF3ModeInfo> found = new List<PhxBF3ModeInfo>();
        if (mapEntry == null) return found;

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<object, object> kv in mapEntry)
        {
            string key = kv.Key as string;
            if (string.IsNullOrEmpty(key)) continue;

            // Flags are normally the number 1. Present-but-off (false or 0)
            // means the combination exists in the table but is not playable.
            if (kv.Value is bool b && !b) continue;
            if (kv.Value is double d && d == 0.0) continue;
            if (kv.Value is float f && f == 0f) continue;

            string lookup;
            if (eras)
            {
                if (!key.StartsWith("era_", StringComparison.OrdinalIgnoreCase)) continue;
                lookup = key;
            }
            else
            {
                if (!key.StartsWith("mode_", StringComparison.OrdinalIgnoreCase)) continue;

                // "mode_con_x" -> "mode_con". Entries without an era suffix are
                // taken as-is.
                int lastUnderscore = key.LastIndexOf('_');
                lookup = lastUnderscore > "mode".Length ? key.Substring(0, lastUnderscore) : key;
            }

            if (!seen.Add(lookup)) continue;

            PhxBF3ModeInfo info = GetModeInfo(lookup);
            if (info == null)
            {
                // Unknown to us and to stock: still offer it rather than
                // silently dropping a playable combination.
                info = new PhxBF3ModeInfo
                {
                    Key = lookup,
                    Subst = lookup.Substring(eras ? 4 : 5),
                    IsEra = eras,
                    DisplayName = lookup.Substring(eras ? 4 : 5),
                };
                Debug.Log($"[BF3Legacy] Mission list declares unknown {(eras ? "era" : "mode")} " +
                          $"'{lookup}' - offering it with a generated name");
            }
            found.Add(info);
        }
        return found;
    }
}
