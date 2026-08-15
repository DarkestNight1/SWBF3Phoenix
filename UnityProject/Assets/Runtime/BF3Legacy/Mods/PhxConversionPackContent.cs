using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Native support for the community "Star Wars Battlefront Conversion Pack"
/// (ModDB, Gametoast: Teancum, Maveritchell et al) - the mod that brings the
/// Battlefront (2004) maps missing from Battlefront II back into it, plus a
/// third era (Knights of the Old Republic), new game modes and a large amount
/// of extra unit content.
///
/// Unlike the BF3 Legacy pack, the Conversion Pack ships as ONE addon folder
/// (as installed: <c>GameData/addon/BF1</c>) that registers all of its maps
/// through the stock addon contract. What it needs from Phoenix is therefore
/// not a loader of its own, but the three things the stock shell would have
/// given it and Phoenix's re-implemented shell did not:
///
///  1. Its data has to be found at all. The pack ships the casing the mod
///     tools wrote (<c>data/_LVL_PC/SIDE/patch.lvl</c>), which resolves on
///     Windows and does not on a case-sensitive file system - see
///     <see cref="PhxPath.ResolveCaseInsensitive"/>.
///  2. Its names have to be readable. Mod-supplied strings live in the addon's
///     core.lvl, which the main menu now mounts (PhxGame.MountAddonShellData),
///     so the pack's own localization is used wherever it has one.
///  3. Its eras and modes have to be selectable. The pack declares eras and
///     modes stock Battlefront II has never heard of; the mission list reads
///     map flags directly (PhxBF3LegacyContent.ExpandEras / ExpandModes), and
///     this class supplies the descriptors - name, blurb, icon - those flags
///     resolve against when the pack itself provides none.
///
/// Everything here is DATA about content the user supplies. No mod assets are
/// distributed with Phoenix, and nothing in this file loads or modifies the
/// pack's files.
///
/// What is known about the pack from its release notes is encoded below; what
/// is NOT knowable without the user's own install (the exact era letter it
/// picked, the id of every one of its maps) is discovered at runtime from the
/// pack's own mission list, and anything left over can be corrected by the
/// user in <c>conversionpack.json</c> without rebuilding - see
/// <see cref="OverridesPath"/>.
/// </summary>
public static class PhxConversionPackContent
{
    /// <summary>The release this table was built against.</summary>
    public const string PackVersion = "2.2";

    const string LogTag = "[ConversionPack]";

    // The layer vocabulary is shared with the BF3 Legacy tables rather than
    // duplicated, because PhxVerticalBattlefront consumes one or the other and
    // "a mod map's space layer" means the same thing in both.
    public class PhxCPMapInfo
    {
        public string Id;            // map id as it appears in the script name, e.g. "rhn1"
        public string DisplayName;   // fallback only - the pack's own localization wins
        public string Planet;
        public PhxBF3LegacyContent.PhxBF3Layer Layer;
    }


    // ------------------------------------------------------------------
    // Detection
    // ------------------------------------------------------------------

    // The folder the pack installs as. Its readme identifies a correct install
    // by "GameData/addon/BF1", and uninstalling means deleting that folder.
    static readonly string[] KnownFolderNames = { "BF1" };

    // Users do rename the folder (and repacks ship it renamed), so a name that
    // says what it is counts too.
    static readonly string[] FolderNameFragments = { "conversionpack", "conversion_pack", "conv_pack", "convpack" };

    // Relative to the addon folder. Present in a correct install of 2.0 + the
    // 2.2 patch; the pack's own install check is "are patch.lvl and patch2.lvl
    // in addon/BF1/data/_LVL_PC/SIDE".
    const string SidePatch1 = "data/_lvl_pc/side/patch.lvl";
    const string SidePatch2 = "data/_lvl_pc/side/patch2.lvl";
    const string MissionLVL = "data/_lvl_pc/mission.lvl";

    public static bool IsInstalled { get; private set; }

    /// <summary>Addon folder the pack was found in, or null.</summary>
    public static string FolderName { get; private set; }

    /// <summary>Absolute path of that folder, or null.</summary>
    public static PhxPath FolderPath { get; private set; }

    /// <summary>Whether the pack is installed but its addme.script is disabled.</summary>
    public static bool IsDisabled { get; private set; }

    /// <summary>Human-readable problems found with the install (may be empty).</summary>
    public static IReadOnlyList<string> InstallProblems => Problems;

    static readonly List<string> Problems = new List<string>();

    /// <summary>Reset before a mod scan; <see cref="Recognize"/> fills it in again.</summary>
    public static void BeginScan()
    {
        IsInstalled = false;
        IsDisabled = false;
        FolderName = null;
        FolderPath = null;
        Problems.Clear();
        OwnedMapIds.Clear();
    }

    /// <summary>
    /// Offer one addon folder to the pack's detector. Returns true if this
    /// folder IS the Conversion Pack.
    /// </summary>
    public static bool Recognize(string folderName, PhxPath folderPath, bool enabled)
    {
        if (string.IsNullOrEmpty(folderName) || folderPath == null) return false;
        if (IsInstalled) return false;   // one install is all the game can have

        bool nameMatches = false;
        foreach (string known in KnownFolderNames)
        {
            if (string.Equals(folderName, known, StringComparison.OrdinalIgnoreCase)) nameMatches = true;
        }
        if (!nameMatches)
        {
            string squashed = folderName.ToLowerInvariant().Replace(" ", "").Replace("-", "");
            foreach (string fragment in FolderNameFragments)
            {
                if (squashed.Contains(fragment)) nameMatches = true;
            }
        }

        // The side patches are the pack's own install fingerprint, so a folder
        // carrying both is the pack whatever it has been renamed to. Requiring
        // BOTH keeps an unrelated mod that happens to ship a "patch.lvl" out.
        bool signatureMatches = HasFile(folderPath, SidePatch1) && HasFile(folderPath, SidePatch2);

        if (!nameMatches && !signatureMatches) return false;

        IsInstalled = true;
        IsDisabled = !enabled;
        FolderName = folderName;
        FolderPath = folderPath;

        Diagnose(folderPath, signatureMatches);
        return true;
    }

    static void Diagnose(PhxPath folderPath, bool signatureMatches)
    {
        if (!signatureMatches)
        {
            // The single most common broken install: 2.0 extracted without the
            // 2.2 patch on top of it, or the archive unpacked one level off.
            // Every side the pack's maps ask for comes out of these two files.
            Problems.Add(
                "SIDE/patch.lvl and SIDE/patch2.lvl are missing from the pack folder. " +
                "Install Conversion Pack 2.0 first, then the 2.2 patch over it - without " +
                "them the pack's units, heroes and vehicles will not load.");
        }
        if (!HasFile(folderPath, MissionLVL))
        {
            Problems.Add(
                "data/_LVL_PC/mission.lvl is missing - the pack's maps cannot be loaded. " +
                "Re-extract the download into GameData/addon.");
        }
    }

    static bool HasFile(PhxPath folderPath, string relative)
    {
        PhxPath p = (folderPath / relative).OnDisk();
        return p.Exists() && p.IsFile();
    }

    /// <summary>
    /// Called once a mod scan has visited every addon folder. Registers the
    /// pack's mode and era descriptors and reports the install.
    /// </summary>
    public static void FinishScan()
    {
        if (!IsInstalled)
        {
            return;
        }

        // Built-ins first, the user's corrections on top of them.
        RegisterDescriptors();
        LoadOverrides();

        Debug.Log($"{LogTag} Battlefront Conversion Pack detected in addon folder " +
                  $"'{FolderName}'{(IsDisabled ? " (disabled)" : "")} - " +
                  $"{ModeTable.Length} mode and {EraTable.Length} era descriptor(s) registered.");

        foreach (string problem in Problems)
        {
            Debug.LogWarning($"{LogTag} {problem}");
        }
    }


    /// <summary>
    /// Called once every addon's addme.script has run, i.e. once the pack has
    /// had its chance to register its maps.
    /// </summary>
    /// <remarks>
    /// Silence is the failure mode worth catching here: an addme.script that
    /// dies part way through leaves the pack installed, detected and with no
    /// maps in the list, and nothing else in the log says why the 25 maps the
    /// user installed are not there.
    /// </remarks>
    public static void OnAddonScriptsExecuted()
    {
        if (!IsInstalled || PhxGame.Instance == null) return;

        // Collect the pack's map ids while the concrete script names are in
        // front of us. The mission list works in PATTERNS ("rhn1%s_%s"), which
        // no addon ever registers - only the expanded names are registered -
        // so pattern-to-owner has to go through the id.
        OwnedMapIds.Clear();
        int owned = 0;
        foreach (string script in PhxGame.Instance.GetRegisteredAddonScripts())
        {
            string folder = PhxGame.Instance.GetAddonFolderForScript(script);
            if (folder == null || !string.Equals(folder, FolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            owned++;
            foreach (string candidate in MapIdCandidates(script)) OwnedMapIds.Add(candidate);
        }

        if (owned > 0)
        {
            // Script count, not map count: one map registers a script per
            // era/mode combination it offers.
            Debug.Log($"{LogTag} Conversion Pack {PackVersion}: {owned} map script(s) " +
                      $"registered from '{FolderName}'.");
            return;
        }

        if (IsDisabled)
        {
            Debug.Log($"{LogTag} The Conversion Pack is installed but disabled, so none of its maps " +
                      "were registered. Re-enable it in GameData/addon/modorder.txt or restore its " +
                      "addme.script.");
            return;
        }

        Debug.LogWarning($"{LogTag} The Conversion Pack is installed and enabled, but registered no " +
                         "maps - its addme.script did not run to completion. Any Lua error logged " +
                         "above this line is the reason.");
    }


    // ------------------------------------------------------------------
    // Modes and eras
    // ------------------------------------------------------------------
    //
    // The pack adds a third era (Knights of the Old Republic) and several game
    // modes on top of Battlefront II's own. Battlefront II's shell knows
    // neither, so a map declaring them lists nothing playable unless something
    // names them - that is what these are for.
    //
    // Each descriptor only ever applies if a map actually declares that flag,
    // so listing the alternate spellings a mod might have used costs nothing
    // and covers the pack whichever one it picked. Anything still unnamed is
    // offered anyway with a generated label (see PhxBF3LegacyContent.Expand),
    // logged, and can be corrected in conversionpack.json.

    // No icon field: an icon is a texture NAME, and naming one that no mounted
    // lvl contains shows an empty box rather than nothing. Which UI textures
    // the pack ships is its business, so icons come from the pack itself (its
    // own AddNewGameModes call) or from the user's conversionpack.json.
    class PhxCPDescriptor
    {
        public string Key;
        public string DisplayName;
        public string About;
    }

    static readonly PhxCPDescriptor[] EraTable =
    {
        new PhxCPDescriptor { Key = "era_k", DisplayName = "Knights of the Old Republic",
                              About = "Four thousand years before the Clone Wars: the Old Republic " +
                                      "against the Sith Empire." },
        new PhxCPDescriptor { Key = "era_o", DisplayName = "Old Republic",
                              About = "The Old Republic against the Sith Empire." },
    };

    static readonly PhxCPDescriptor[] ModeTable =
    {
        new PhxCPDescriptor { Key = "mode_xl", DisplayName = "XL",
                              About = "Conquest at several times the usual unit count." },
        new PhxCPDescriptor { Key = "mode_classic", DisplayName = "Classic Conquest",
                              About = "Conquest played the way the original Battlefront did it." },
        new PhxCPDescriptor { Key = "mode_cla", DisplayName = "Classic Conquest",
                              About = "Conquest played the way the original Battlefront did it." },
        new PhxCPDescriptor { Key = "mode_hero", DisplayName = "Hero Assault",
                              About = "Heroes and villains only." },
    };

    static void RegisterDescriptors()
    {
        foreach (PhxCPDescriptor era in EraTable)
        {
            PhxBF3LegacyContent.RegisterModeInfo(era.Key, Named(era.Key, era.DisplayName),
                                                 era.About, null, "ConversionPack");
        }
        foreach (PhxCPDescriptor mode in ModeTable)
        {
            PhxBF3LegacyContent.RegisterModeInfo(mode.Key, Named(mode.Key, mode.DisplayName),
                                                 mode.About, null, "ConversionPack");
        }
    }

    /// <summary>
    /// Re-read the pack's own strings now that the shell's data is mounted.
    /// </summary>
    /// <remarks>
    /// Descriptors are registered during the mod scan, which happens long
    /// before any localization exists. Once the menu is up, the pack's core.lvl
    /// is mounted and its own names - in the user's language - are available;
    /// they are always better than the built-in fallbacks, so they replace
    /// them. Called from PhxMainMenu, and safe to call repeatedly.
    /// </remarks>
    public static void RefreshLocalizedNames()
    {
        if (!IsInstalled) return;

        foreach (PhxCPDescriptor era in EraTable)   Localize(era.Key, true);
        foreach (PhxCPDescriptor mode in ModeTable) Localize(mode.Key, false);
    }

    static void Localize(string key, bool isEra)
    {
        PhxEnvironment env = PhxGame.GetEnvironment();
        if (env == null) return;

        // A name the user wrote themselves outranks everything, including the
        // pack's own string - they edited the file to change what they saw.
        if (UserNamedKeys.Contains(key)) return;

        string subst = key.Substring(isEra ? 4 : 5);
        string kind = isEra ? "era" : "mode";

        // Battlefront II's own shell strings are addressed as
        // "ifs.missionselect.<something>", and mods follow that convention when
        // they add to it. Nothing forces them to, so this is a lookup that is
        // allowed to miss - a miss just keeps the built-in fallback.
        string[] candidates =
        {
            $"ifs.missionselect.{kind}.{subst}",
            $"ifs.missionselect.{kind}_{subst}",
            $"ifs.missionselect.{subst}",
            $"ifs.missionselect.{key}",
            $"ifs.instantaction.{kind}.{subst}",
        };

        foreach (string path in candidates)
        {
            string localized = env.GetLocalized(path, true);
            if (!string.IsNullOrEmpty(localized) && localized != path)
            {
                PhxBF3LegacyContent.RegisterModeInfo(key, localized, null, null, "ConversionPack");
                return;
            }
        }
    }


    // ------------------------------------------------------------------
    // Maps
    // ------------------------------------------------------------------
    //
    // The pack names its maps itself, and which ids it picked is a property of
    // the user's install rather than something to hard-code: ids collide
    // between mods, and the pack has been repacked more than once. So a map is
    // "the pack's" when the pack's addon folder registered its script
    // (AddDownloadableContent -> PhxGame.GetAddonFolderForScript), and the
    // planet is read out of the id, which by SWBF convention leads with a
    // three-letter world abbreviation ("rhn1c_con" -> Rhen Var).
    //
    // Used for two things only, both of them fallbacks: a display name when
    // the pack ships no localized one, and the space-layer classification that
    // decides whether capital ships may hang over the map at all.

    class PhxCPWorld
    {
        public string Planet;
        public PhxBF3LegacyContent.PhxBF3Layer Layer;
    }

    static readonly Dictionary<string, PhxCPWorld> Worlds = new Dictionary<string, PhxCPWorld>(StringComparer.OrdinalIgnoreCase)
    {
        // --- Battlefront (2004) / Battlefront II worlds the pack converts ---
        { "bes", new PhxCPWorld { Planet = "Bespin",        Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "cor", new PhxCPWorld { Planet = "Coruscant",     Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "dag", new PhxCPWorld { Planet = "Dagobah",       Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "dea", new PhxCPWorld { Planet = "Death Star",    Layer = PhxBF3LegacyContent.PhxBF3Layer.Interior } },
        { "end", new PhxCPWorld { Planet = "Endor",         Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "fel", new PhxCPWorld { Planet = "Felucia",       Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "geo", new PhxCPWorld { Planet = "Geonosis",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "hot", new PhxCPWorld { Planet = "Hoth",          Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "kam", new PhxCPWorld { Planet = "Kamino",        Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "kas", new PhxCPWorld { Planet = "Kashyyyk",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "mus", new PhxCPWorld { Planet = "Mustafar",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "myg", new PhxCPWorld { Planet = "Mygeeto",       Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "nab", new PhxCPWorld { Planet = "Naboo",         Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "pol", new PhxCPWorld { Planet = "Polis Massa",   Layer = PhxBF3LegacyContent.PhxBF3Layer.Space } },
        { "rhn", new PhxCPWorld { Planet = "Rhen Var",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "rhe", new PhxCPWorld { Planet = "Rhen Var",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "tan", new PhxCPWorld { Planet = "Tantive IV",    Layer = PhxBF3LegacyContent.PhxBF3Layer.Interior } },
        { "tat", new PhxCPWorld { Planet = "Tatooine",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "uta", new PhxCPWorld { Planet = "Utapau",        Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "yav", new PhxCPWorld { Planet = "Yavin 4",       Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },

        // --- Knights of the Old Republic worlds the pack's third era plays on ---
        { "dan", new PhxCPWorld { Planet = "Dantooine",     Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "dxn", new PhxCPWorld { Planet = "Dxun",          Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "kor", new PhxCPWorld { Planet = "Korriban",      Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "leh", new PhxCPWorld { Planet = "Lehon",         Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "man", new PhxCPWorld { Planet = "Manaan",        Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "rak", new PhxCPWorld { Planet = "Rakata Prime",  Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "tar", new PhxCPWorld { Planet = "Taris",         Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
        { "tel", new PhxCPWorld { Planet = "Telos",         Layer = PhxBF3LegacyContent.PhxBF3Layer.Ground } },
    };

    // Space maps name themselves, in stock BF2 and in every mod that follows
    // it: "spa1g_ass", "spa2c_1flag".
    static readonly string[] SpaceMarkers = { "spa", "space" };

    /// <summary>
    /// True when the given mission script belongs to the Conversion Pack, as
    /// established by which addon folder registered it.
    /// </summary>
    public static bool OwnsMap(string mapScript)
    {
        if (!IsInstalled || string.IsNullOrEmpty(mapScript)) return false;

        // Mission list patterns and concrete script names reduce to the same
        // map id, which is what OwnedMapIds holds - see MapIdCandidates for why
        // there can be two readings of one name.
        foreach (string candidate in MapIdCandidates(mapScript))
        {
            if (OwnedMapIds.Contains(candidate)) return true;
        }

        // Before the ids are collected (a map booted directly into, without
        // passing through the menu), the registration itself still answers it.
        if (PhxGame.Instance == null) return false;
        string owner = PhxGame.Instance.GetAddonFolderForScript(mapScript);
        return owner != null && string.Equals(owner, FolderName, StringComparison.OrdinalIgnoreCase);
    }

    // Map ids the pack registered this session, keyed without the era letter
    // ("rhn1"). Filled by OnAddonScriptsExecuted.
    static readonly HashSet<string> OwnedMapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What is known about one of the pack's maps, or null when the script is
    /// not the pack's or the world cannot be told from its id.
    /// </summary>
    public static PhxCPMapInfo GetMapInfo(string mapScript)
    {
        if (!OwnsMap(mapScript)) return null;

        string id = GetMapId(mapScript);
        if (string.IsNullOrEmpty(id)) return null;

        if (MapOverrides.TryGetValue(id, out PhxCPMapInfo overridden))
        {
            return overridden;
        }

        string lower = id.ToLowerInvariant();
        foreach (string marker in SpaceMarkers)
        {
            if (lower.StartsWith(marker))
            {
                return new PhxCPMapInfo
                {
                    Id = id,
                    DisplayName = "Conversion Pack: Space",
                    Planet = "Deep Space",
                    Layer = PhxBF3LegacyContent.PhxBF3Layer.Space,
                };
            }
        }

        // Ids lead with a three-letter world, which is what makes reading the
        // world out of one possible at all. Anything shorter or unknown stays
        // unclassified rather than being guessed at.
        if (lower.Length < 3) return null;
        if (!Worlds.TryGetValue(lower.Substring(0, 3), out PhxCPWorld world)) return null;

        return new PhxCPMapInfo
        {
            Id = id,
            DisplayName = "Conversion Pack: " + world.Planet,
            Planet = world.Planet,
            Layer = world.Layer,
        };
    }

    /// <summary>Display name for a pack map with no localized name, or null.</summary>
    public static string GetMapDisplayName(string mapluafile)
    {
        PhxCPMapInfo info = GetMapInfo(mapluafile);
        return info?.DisplayName;
    }

    /// <summary>
    /// Every reading of the map id at the front of a script name or mission
    /// list pattern, most specific first.
    /// </summary>
    /// <remarks>
    /// A pattern spells the era letter out as "%s", so its id is unambiguous.
    /// A concrete name does not: "rhn1c_con" is "rhn1" + era 'c', but nothing
    /// in the string says whether the trailing letter is the era or part of an
    /// id that simply ends in a letter ("kas" + 'c' reads the same as "kasc").
    /// Rather than pick one, both readings are offered - they are only ever
    /// used to match against ids gathered the same way, so an id registered in
    /// one shape is still recognized in the other.
    /// </remarks>
    static IEnumerable<string> MapIdCandidates(string mapScript)
    {
        if (string.IsNullOrEmpty(mapScript)) yield break;

        string head = Head(mapScript);
        if (string.IsNullOrEmpty(head)) yield break;

        yield return head;

        if (head.Length > 2 && char.IsLetter(head[head.Length - 1]))
        {
            yield return head.Substring(0, head.Length - 1);
        }
    }

    static string Head(string mapScript)
    {
        int cut = mapScript.IndexOf('%');
        if (cut < 0) cut = mapScript.IndexOf('_');
        return cut < 0 ? mapScript : mapScript.Substring(0, cut);
    }

    /// <summary>
    /// The map id at the front of a mission script name or mission list
    /// pattern: "rhn1c_con" and "rhn1%s_%s" both yield "rhn1".
    /// </summary>
    /// <remarks>
    /// The trailing character before the first separator is the era letter the
    /// menu substitutes, so it is not part of the id. A pattern ("%s") makes
    /// that explicit; a concrete script name does not, so the last letter is
    /// dropped only when what precedes it still ends in a digit - the shape
    /// every stock and mod id has ("rhn1" + "c"), and the one case where
    /// dropping it cannot eat a real id character.
    /// </remarks>
    public static string GetMapId(string mapScript)
    {
        if (string.IsNullOrEmpty(mapScript)) return null;

        string head = Head(mapScript);
        if (string.IsNullOrEmpty(head)) return null;

        if (head.Length >= 2 && char.IsLetter(head[head.Length - 1]) &&
            char.IsDigit(head[head.Length - 2]))
        {
            head = head.Substring(0, head.Length - 1);
        }
        return head;
    }


    // ------------------------------------------------------------------
    // User overrides
    // ------------------------------------------------------------------
    //
    // The pack's own ids, era letter and mode names are properties of the
    // release the user installed, and repacks differ. Rather than require a
    // rebuild to correct one, everything this class supplies as a fallback can
    // be overridden from a file next to the runtime's config.

    const string OverridesFileName = "conversionpack.json";

    /// <summary>Where the user's corrections live (next to bf3legacy.json).</summary>
    public static string OverridesPath
    {
        get
        {
            string dir = Path.GetDirectoryName(PhxBF3.ConfigPath);
            return string.IsNullOrEmpty(dir)
                ? OverridesFileName
                : Path.Combine(dir, OverridesFileName);
        }
    }

    static readonly Dictionary<string, PhxCPMapInfo> MapOverrides =
        new Dictionary<string, PhxCPMapInfo>(StringComparer.OrdinalIgnoreCase);

    // Mode/era keys the user named explicitly, so nothing discovered later
    // silently replaces what they asked for.
    static readonly HashSet<string> UserNamedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static bool OverridesLoaded;

    static void LoadOverrides()
    {
        if (OverridesLoaded) return;
        OverridesLoaded = true;

        try
        {
            if (!File.Exists(OverridesPath))
            {
                WriteOverridesTemplate();
                return;
            }

            PhxCPOverrides data = JsonUtility.FromJson<PhxCPOverrides>(File.ReadAllText(OverridesPath));
            if (data == null) return;

            if (data.Modes != null)
            {
                foreach (PhxCPDescriptorOverride m in data.Modes)
                {
                    if (m == null || string.IsNullOrEmpty(m.Key)) continue;
                    PhxBF3LegacyContent.RegisterModeInfo(m.Key, m.Name, m.About, m.Icon,
                                                         "ConversionPack/user");
                    if (!string.IsNullOrEmpty(m.Name)) UserNamedKeys.Add(m.Key);
                }
            }

            if (data.Maps != null)
            {
                foreach (PhxCPMapOverride m in data.Maps)
                {
                    if (m == null || string.IsNullOrEmpty(m.Id)) continue;
                    MapOverrides[m.Id] = new PhxCPMapInfo
                    {
                        Id = m.Id,
                        DisplayName = string.IsNullOrEmpty(m.Name) ? m.Id : m.Name,
                        Planet = m.Planet,
                        Layer = ParseLayer(m.Layer),
                    };
                }
            }

            Debug.Log($"{LogTag} Read {OverridesPath}: " +
                      $"{(data.Modes == null ? 0 : data.Modes.Length)} mode/era and " +
                      $"{MapOverrides.Count} map override(s).");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"{LogTag} Could not read '{OverridesPath}': {e.Message}. " +
                             "Built-in descriptors are used instead.");
        }
    }

    static PhxBF3LegacyContent.PhxBF3Layer ParseLayer(string layer)
    {
        if (string.IsNullOrEmpty(layer)) return PhxBF3LegacyContent.PhxBF3Layer.Ground;
        if (layer.StartsWith("i", StringComparison.OrdinalIgnoreCase)) return PhxBF3LegacyContent.PhxBF3Layer.Interior;
        if (layer.StartsWith("s", StringComparison.OrdinalIgnoreCase)) return PhxBF3LegacyContent.PhxBF3Layer.Space;
        return PhxBF3LegacyContent.PhxBF3Layer.Ground;
    }

    /// <summary>
    /// Write an example file the first time the pack is seen, so correcting a
    /// name is editing an existing file rather than inventing a format.
    /// </summary>
    static void WriteOverridesTemplate()
    {
        try
        {
            // The entries are placeholders on purpose: they show the shape
            // without asserting anything about the user's install. A Key that
            // is neither 'era_*' nor 'mode_*' is ignored on load, and no map
            // has the id "example", so the file as written changes nothing
            // until it is edited.
            PhxCPOverrides template = new PhxCPOverrides
            {
                Comment = "Corrections for the Battlefront Conversion Pack, applied at startup. " +
                          "Modes/eras: Key is the mission list flag exactly as the map declares it " +
                          "('era_k', 'mode_xl'); Icon is a UI texture name, or empty for none. " +
                          "Maps: Id is the map id without the era letter ('rhn1', not 'rhn1c_con'); " +
                          "Layer is Ground, Space or Interior. The entries below are placeholders - " +
                          "edit them, or delete them to keep the built-in values. Unrecognized flags " +
                          "are logged at startup with the key to use here.",
                Modes = new[]
                {
                    new PhxCPDescriptorOverride { Key = "example_replace_with_era_or_mode_flag",
                                                  Name = "", About = "", Icon = "" },
                },
                Maps = new[]
                {
                    new PhxCPMapOverride { Id = "example", Name = "", Planet = "", Layer = "Ground" },
                },
            };

            File.WriteAllText(OverridesPath, JsonUtility.ToJson(template, true));
            Debug.Log($"{LogTag} Wrote an example override file to '{OverridesPath}'. " +
                      "Edit it to correct any map, mode or era name the pack does not supply itself.");
        }
        catch (Exception e)
        {
            // Read-only install directory, sandboxed player - not worth a
            // warning, the built-in table is what runs either way.
            Debug.Log($"{LogTag} No override file written ({e.Message}); built-in descriptors are used.");
        }
    }

    static string Named(string key, string fallback)
    {
        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }
}


[Serializable]
public class PhxCPDescriptorOverride
{
    public string Key;
    public string Name;
    public string About;
    public string Icon;
}

[Serializable]
public class PhxCPMapOverride
{
    public string Id;
    public string Name;
    public string Planet;
    public string Layer;   // Ground | Space | Interior
}

[Serializable]
public class PhxCPOverrides
{
    public string Comment;
    public PhxCPDescriptorOverride[] Modes;
    public PhxCPMapOverride[] Maps;
}
