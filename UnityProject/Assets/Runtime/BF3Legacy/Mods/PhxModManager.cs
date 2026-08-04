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
        public long SizeBytes;
    }

    static readonly List<PhxModInfo> Mods = new List<PhxModInfo>();

    public static IReadOnlyList<PhxModInfo> GetMods() => Mods;

    public static bool IsBF3LegacyInstalled { get; private set; }

    // Folder name fragments identifying the BF3 Legacy mod family and other
    // recovered-BF3-content releases.
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
        IsBF3LegacyInstalled = false;

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
            bool scriptOn = File.Exists(System.IO.Path.Combine(dir, "addme.script"));
            bool scriptOff = File.Exists(System.IO.Path.Combine(dir, "addme.script.off"));

            PhxModInfo mod = new PhxModInfo
            {
                FolderName = folder,
                DisplayName = folder,
                Path = new PhxPath(dir),
                HasAddmeScript = scriptOn || scriptOff,
                Enabled = scriptOn && !disabled.Contains(folder.ToLowerInvariant()),
                IsBF3Legacy = IsBF3LegacyFolder(folder),
                SizeBytes = 0, // filled lazily; full recursive size is slow on big mods
            };
            Mods.Add(mod);

            if (mod.IsBF3Legacy && mod.Enabled)
            {
                IsBF3LegacyInstalled = true;
            }
        }

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
                  (IsBF3LegacyInstalled ? ", Battlefront III Legacy detected!" : ""));
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
