using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Extended mod support on top of PhxGame's addon discovery:
///
///  - enumerates GameData/addon with metadata (name, folder, addme.script, size)
///  - user-controllable load order via GameData/addon/modorder.txt
///    (one folder name per line, '#' comments, unlisted mods load afterwards
///    alphabetically; prefix a line with '!' to disable that mod)
///  - detects known large conversions, most importantly the
///    "Star Wars Battlefront III Legacy" mod (recovered Free Radical BF3
///    assets packaged as SWBF2 addon content), and flags it so BF3 Legacy
///    features (capital ships, vertical battlefront maps) can light up.
///
/// PhxGame remains the authority for actually mounting addon lvl files; this
/// manager only inspects the same folder and provides ordering/metadata. Mods
/// disabled here are hidden by renaming their addme.script to addme.script.off
/// (reversible, same trick the community uses manually).
/// </summary>
public static class PhxModManager
{
    public class PhxModInfo
    {
        public string FolderName;
        public string DisplayName;
        public PhxPath Path;
        public bool HasAddmeScript;
        public bool Enabled;
        public bool IsBF3Legacy;
        public bool IsConversionPack;
        public long SizeBytes;

        // Set when this folder is a recognized component of the BF3 Legacy
        // release (see PhxBF3LegacyContent), null for any other mod.
        public PhxBF3LegacyContent.PhxBF3ModComponent BF3Component;
    }

    static readonly List<PhxModInfo> Mods = new List<PhxModInfo>();

    public static IReadOnlyList<PhxModInfo> GetMods() => Mods;

    public static bool IsBF3LegacyInstalled { get; private set; }

    /// <summary>
    /// Whether the "Star Wars Battlefront Conversion Pack" is installed, and
    /// enabled. See <see cref="PhxConversionPackContent"/> for what that
    /// unlocks.
    /// </summary>
    public static bool IsConversionPackInstalled => PhxConversionPackContent.IsInstalled;

    /// <summary>Recognized BF3 Legacy pack components that are installed and enabled.</summary>
    public static IReadOnlyList<PhxBF3LegacyContent.PhxBF3ModComponent> BF3LegacyComponents => DetectedComponents;

    static readonly List<PhxBF3LegacyContent.PhxBF3ModComponent> DetectedComponents =
        new List<PhxBF3LegacyContent.PhxBF3ModComponent>();

    /// <summary>
    /// True when BF3 Legacy add-on components are installed without the main
    /// mod folder ("BF3") they all build on. Those add-ons reference sides and
    /// scripts the main mod ships, so their maps load into a broken state.
    /// </summary>
    public static bool BF3LegacyMissingBaseMod { get; private set; }

    // Folder name fragments identifying the BF3 Legacy mod family and other
    // recovered-BF3-content releases. Only a fallback: exact component names
    // come from PhxBF3LegacyContent.
    static readonly string[] BF3LegacyMarkers = { "bf3", "battlefront3", "battlefront iii", "legacy" };


    static bool ScanSucceeded;

    /// <summary>
    /// Scan once, lazily. Bootstrap runs before the game path may be known
    /// (or valid), so callers on the map-load path use this to make sure the
    /// mod list actually got built.
    /// </summary>
    public static void EnsureScanned()
    {
        if (!ScanSucceeded) Scan();
    }

    public static void Scan()
    {
        Mods.Clear();
        DetectedComponents.Clear();
        IsBF3LegacyInstalled = false;
        BF3LegacyMissingBaseMod = false;
        PhxConversionPackContent.BeginScan();

        PhxGame game = PhxGame.Instance;
        if (game == null || game.AddonPath == null || !game.AddonPath.Exists())
        {
            Debug.Log("[BF3Legacy] No addon folder found, mod scan skipped");
            return;
        }
        ScanSucceeded = true;

        string addonDir = game.AddonPath;
        List<string> loadOrder = ReadLoadOrder(addonDir, out HashSet<string> disabled);

        foreach (string dir in Directory.GetDirectories(addonDir))
        {
            string folder = new DirectoryInfo(dir).Name;

            // Case-insensitively, as PhxGame's addon discovery now looks for
            // them: a mod shipping "AddMe.script" is loaded and executed on a
            // case-sensitive file system, so it must not be listed as disabled
            // here.
            bool scriptOn = HasFile(dir, "addme.script");
            bool scriptOff = HasFile(dir, "addme.script.off");

            PhxBF3LegacyContent.PhxBF3ModComponent component = PhxBF3LegacyContent.GetComponent(folder);

            bool enabled = scriptOn && !disabled.Contains(folder.ToLowerInvariant());
            bool isConversionPack = PhxConversionPackContent.Recognize(folder, new PhxPath(dir), enabled);

            PhxModInfo mod = new PhxModInfo
            {
                FolderName = folder,
                DisplayName = component != null ? component.DisplayName
                                                : (isConversionPack ? "Battlefront Conversion Pack" : folder),
                Path = new PhxPath(dir),
                HasAddmeScript = scriptOn || scriptOff,
                Enabled = enabled,
                IsBF3Legacy = component != null || IsBF3LegacyFolder(folder),
                IsConversionPack = isConversionPack,
                BF3Component = component,
                SizeBytes = 0, // filled lazily; full recursive size is slow on big mods
            };
            Mods.Add(mod);

            if (mod.IsBF3Legacy && mod.Enabled)
            {
                IsBF3LegacyInstalled = true;
            }
            if (component != null && mod.Enabled)
            {
                DetectedComponents.Add(component);
            }
        }

        // Registers the Conversion Pack's mode/era descriptors and reports any
        // problem with the install (missing side patches, missing mission.lvl).
        PhxConversionPackContent.FinishScan();

        // Every add-on component (Cato Hunt, Extended Engagements, MoreMaps, ...)
        // is built on top of the main "BF3" folder's sides and scripts.
        BF3LegacyMissingBaseMod =
            DetectedComponents.Count > 0 &&
            !DetectedComponents.Exists(c => c.FolderName == "BF3");

        // sort: explicit order first, then alphabetical
        Mods.Sort((a, b) =>
        {
            int ia = loadOrder.IndexOf(a.FolderName.ToLowerInvariant());
            int ib = loadOrder.IndexOf(b.FolderName.ToLowerInvariant());
            if (ia < 0 && ib < 0) return string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase);
            if (ia < 0) return 1;
            if (ib < 0) return -1;
            return ia.CompareTo(ib);
        });

        Debug.Log($"[BF3Legacy] Mod scan: {Mods.Count} addon(s) found" +
                  (IsBF3LegacyInstalled ? ", Battlefront III Legacy detected!" : "") +
                  (IsConversionPackInstalled ? ", Battlefront Conversion Pack detected!" : ""));

        if (DetectedComponents.Count > 0)
        {
            Debug.Log($"[BF3Legacy] Battlefront III Legacy {PhxBF3LegacyContent.PackVersion} pack: " +
                      $"{DetectedComponents.Count}/{PhxBF3LegacyContent.Components.Count} components enabled");
            foreach (PhxBF3LegacyContent.PhxBF3ModComponent c in DetectedComponents)
            {
                Debug.Log($"[BF3Legacy]   - {c.DisplayName} ({c.Author})");
            }
        }
        if (BF3LegacyMissingBaseMod)
        {
            Debug.LogWarning("[BF3Legacy] BF3 Legacy add-ons are installed without the main 'BF3' " +
                             "folder they depend on. Their maps will load with missing sides and " +
                             "scripts - install the full pack into GameData/addon.");
        }
    }

    static bool HasFile(string dir, string fileName)
    {
        PhxPath path = (new PhxPath(dir) / fileName).OnDisk();
        return path.Exists() && path.IsFile();
    }

    static bool IsBF3LegacyFolder(string folder)
    {
        string lower = folder.ToLowerInvariant();
        return BF3LegacyMarkers.Any(m => lower.Contains(m));
    }

    static List<string> ReadLoadOrder(string addonDir, out HashSet<string> disabled)
    {
        disabled = new HashSet<string>();
        List<string> order = new List<string>();

        string orderFile = System.IO.Path.Combine(addonDir, "modorder.txt");
        if (!File.Exists(orderFile)) return order;

        try
        {
            foreach (string rawLine in File.ReadAllLines(orderFile))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.StartsWith("!"))
                {
                    disabled.Add(line.Substring(1).Trim().ToLowerInvariant());
                }
                else
                {
                    order.Add(line.ToLowerInvariant());
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Failed to read modorder.txt: {e.Message}");
        }
        return order;
    }

    /// <summary>Enable/disable a mod by renaming its addme.script (reversible).</summary>
    public static bool SetModEnabled(PhxModInfo mod, bool enabled)
    {
        try
        {
            string on = mod.Path / "addme.script";
            string off = mod.Path / "addme.script.off";

            if (enabled && File.Exists(off) && !File.Exists(on))
            {
                File.Move(off, on);
            }
            else if (!enabled && File.Exists(on) && !File.Exists(off))
            {
                File.Move(on, off);
            }
            mod.Enabled = enabled;
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Could not toggle mod '{mod.FolderName}': {e.Message}");
            return false;
        }
    }
}
