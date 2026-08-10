using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Install simplification: auto-detects a Star Wars Battlefront II (2005)
/// installation in the usual Steam / GOG / retail locations so most users
/// never have to type a game path. A path counts as valid when
/// GameData/data/_lvl_pc/common.lvl exists.
///
/// Steam library folders beyond the default are discovered by parsing
/// steamapps/libraryfolders.vdf (line-based, no full VDF parser needed).
/// </summary>
public static class PhxGamePathDetector
{
    const string LvlProbe = "GameData/data/_lvl_pc/common.lvl";
    const string SteamAppFolder = "steamapps/common/Star Wars Battlefront II";

    // Installers disagree on the folder name: Steam uses roman numerals, GOG
    // ships "Star Wars - Battlefront 2", and retail/manual copies use whatever
    // the user typed.
    static readonly string[] InstallFolderNames =
    {
        "Star Wars Battlefront II",
        "Star Wars - Battlefront II",
        "Star Wars Battlefront 2",
        "Star Wars - Battlefront 2",
        "STAR WARS Battlefront II",
        "Battlefront II",
        "SWBF2",
    };

    // Folders people actually keep games in, relative to each root below.
    static readonly string[] GameContainerFolders =
    {
        "GOG Games",
        "GOG Galaxy/Games",
        "Games",
        "LucasArts",
    };

    /// <summary>
    /// Directory the build lives in: the folder containing the executable in a
    /// player, the Unity project root in the editor.
    /// </summary>
    public static string GetInstallRoot()
    {
        // Player:  <install>/Phoenix_Data  ->  <install>
        // Editor:  <project>/Assets        ->  <project>
        try
        {
            return Directory.GetParent(Application.dataPath)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Self-contained install: the build sits inside the Battlefront II folder
    /// (typically &lt;BF2&gt;/Phoenix/Phoenix.exe), so the game is simply "up
    /// from here". Walking a few levels also covers dropping the build straight
    /// into the BF2 root or one folder deeper.
    ///
    /// Returns null in the editor and for builds kept outside the game folder,
    /// which then fall through to the configured path and the usual scan.
    /// </summary>
    public static string TryDetectPortable()
    {
        string dir = GetInstallRoot();
        for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); ++i)
        {
            if (IsValidGamePath(dir))
            {
                return dir.Replace('\\', '/');
            }
            try
            {
                dir = Directory.GetParent(dir)?.FullName;
            }
            catch
            {
                break;
            }
        }
        return null;
    }

    public static string TryDetect()
    {
        // an explicit choice (first-run setup panel) always wins
        string configured = PhxBF3.Config != null ? PhxBF3.Config.GamePathOverride : null;
        if (IsValidGamePath(configured))
        {
            return configured.Replace('\\', '/');
        }

        // then a self-contained install: we're sitting inside the game folder
        string portable = TryDetectPortable();
        if (portable != null)
        {
            return portable;
        }

        foreach (string candidate in EnumerateCandidates())
        {
            if (IsValidGamePath(candidate))
            {
                return candidate.Replace('\\', '/');
            }
        }
        return null;
    }

    /// <summary>Locations probed by auto-detection, for the setup UI to show.</summary>
    public static System.Collections.Generic.List<string> GetProbedPaths()
    {
        var list = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string c in EnumerateCandidates())
        {
            string normalized = c.Replace('\\', '/');
            if (!seen.Add(normalized)) continue;

            list.Add(normalized);
            if (list.Count >= 40) break;
        }
        return list;
    }

    public static bool IsValidGamePath(string path)
    {
        return !string.IsNullOrEmpty(path) &&
               Directory.Exists(path) &&
               File.Exists(Path.Combine(path, "GameData", "data", "_lvl_pc", "common.lvl"));
    }

    static IEnumerable<string> EnumerateCandidates()
    {
        // --- Steam default + extra library folders ---
        foreach (string steamRoot in SteamRoots())
        {
            yield return Path.Combine(steamRoot, SteamAppFolder);

            string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (string library in ParseLibraryFolders(vdf))
                {
                    yield return Path.Combine(library, SteamAppFolder);
                    foreach (string name in InstallFolderNames)
                    {
                        yield return Path.Combine(library, "steamapps", "common", name);
                    }
                }
            }
        }

        // --- GOG / retail / manual copies ---
        // Cross every plausible root with every known folder name, then look
        // one level inside game-library folders so an unexpected name (a GOG
        // install the user renamed) is still found.
        foreach (string root in SearchRoots())
        {
            if (string.IsNullOrEmpty(root)) continue;

            foreach (string name in InstallFolderNames)
            {
                yield return Path.Combine(root, name);
            }

            foreach (string container in GameContainerFolders)
            {
                string dir = Path.Combine(root, container.Replace('/', Path.DirectorySeparatorChar));
                foreach (string name in InstallFolderNames)
                {
                    yield return Path.Combine(dir, name);
                }
                foreach (string child in SafeEnumerateDirectories(dir))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// Roots worth searching: the user's own folders (games do end up on the
    /// desktop), the Program Files pair, and every fixed drive's root.
    /// </summary>
    static IEnumerable<string> SearchRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return home;
            yield return Path.Combine(home, "Desktop");
            yield return Path.Combine(home, "Documents");
            yield return Path.Combine(home, "Downloads");
        }

        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (string drive in FixedDriveRoots())
        {
            yield return drive;
        }
    }

    static IEnumerable<string> FixedDriveRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo d in drives)
        {
            string name = null;
            try
            {
                // IsReady throws on a disconnected network drive
                if (d.DriveType == DriveType.Fixed && d.IsReady) name = d.RootDirectory.FullName;
            }
            catch
            {
                name = null;
            }
            if (name != null) yield return name;
        }
    }

    static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        string[] children;
        try
        {
            if (!Directory.Exists(dir)) return new string[0];
            children = Directory.GetDirectories(dir);
        }
        catch
        {
            return new string[0];
        }
        return children;
    }

    static IEnumerable<string> SteamRoots()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor:
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "Steam");
                yield return "C:/Steam";
                yield return "D:/Steam";
                yield return "D:/SteamLibrary";
                break;

            case RuntimePlatform.LinuxPlayer:
            case RuntimePlatform.LinuxEditor:
                string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                yield return Path.Combine(home, ".steam", "steam");
                yield return Path.Combine(home, ".local", "share", "Steam");
                break;

            case RuntimePlatform.OSXPlayer:
            case RuntimePlatform.OSXEditor:
                string macHome = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                yield return Path.Combine(macHome, "Library", "Application Support", "Steam");
                break;
        }
    }

    static IEnumerable<string> ParseLibraryFolders(string vdfPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(vdfPath);
        }
        catch
        {
            yield break;
        }

        foreach (string line in lines)
        {
            // lines look like:   "path"   "D:\\SteamLibrary"
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"")) continue;

            int firstQuote = trimmed.IndexOf('"', 6);
            int lastQuote = trimmed.LastIndexOf('"');
            if (firstQuote < 0 || lastQuote <= firstQuote) continue;

            string path = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1)
                                 .Replace("\\\\", "/").Replace('\\', '/');
            if (!string.IsNullOrEmpty(path))
            {
                yield return path;
            }
        }
    }
}
