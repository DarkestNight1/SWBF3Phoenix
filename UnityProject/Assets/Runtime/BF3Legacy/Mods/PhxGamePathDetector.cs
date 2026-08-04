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

    public static string TryDetect()
    {
        // an explicit choice (first-run setup panel) always wins
        string configured = PhxBF3.Config != null ? PhxBF3.Config.GamePathOverride : null;
        if (IsValidGamePath(configured))
        {
            return configured.Replace('\\', '/');
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
        foreach (string c in EnumerateCandidates())
        {
            list.Add(c.Replace('\\', '/'));
            if (list.Count >= 12) break;
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
                }
            }
        }

        // --- GOG / retail (Windows) ---
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string root in new[] { pf86, pf, "C:/GOG Games", "D:/GOG Games" })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "GOG Galaxy", "Games", "Star Wars - Battlefront II");
            yield return Path.Combine(root, "Star Wars - Battlefront II");
            yield return Path.Combine(root, "LucasArts", "Star Wars Battlefront II");
        }
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
