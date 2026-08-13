#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds a SELF-CONTAINED Phoenix player into the Battlefront II folder.
///
/// The result is a single folder inside the game install:
///
///     Star Wars - Battlefront 2/
///         BattlefrontII.exe        <- the original game, untouched
///         GameData/                <- shared data + addon mods
///         Phoenix/
///             Phoenix.exe          <- this build
///             Phoenix_Data/ ...
///             bf3legacy.json       <- written on first run
///
/// Nothing outside that folder is touched and nothing is written to the
/// registry or AppData: PhxGamePathDetector.TryDetectPortable walks up from
/// the executable and finds GameData right there, so the build needs no
/// configuration at all. Deleting the Phoenix folder uninstalls it completely.
///
/// Usage:
///   - Editor:    Phoenix > Build Self-Contained Player...
///   - Batchmode: -executeMethod PhxBuild.BuildFromCommandLine -phxOutput "<dir>"
///     (see BuildPhoenix.bat, which finds the game and calls this)
/// </summary>
public static class PhxBuild
{
    public const string ExeName = "Phoenix.exe";
    const string SubFolderName = "Phoenix";

    [MenuItem("Phoenix/Build Self-Contained Player...")]
    static void BuildInteractive()
    {
        string gameDir = PhxGamePathDetector.TryDetect();
        string startDir = string.IsNullOrEmpty(gameDir) ? "" : gameDir;

        string chosen = EditorUtility.SaveFolderPanel(
            "Choose your Battlefront II folder (the one containing GameData)",
            startDir, "");

        if (string.IsNullOrEmpty(chosen)) return;

        if (!PhxGamePathDetector.IsValidGamePath(chosen))
        {
            EditorUtility.DisplayDialog("Phoenix",
                $"'{chosen}' doesn't look like a Battlefront II install.\n\n" +
                "Expected GameData/data/_lvl_pc/common.lvl inside it.", "OK");
            return;
        }

        string outDir = Path.Combine(chosen, SubFolderName);
        BuildReport report = Build(outDir);

        bool ok = report != null && report.summary.result == BuildResult.Succeeded;
        EditorUtility.DisplayDialog("Phoenix",
            ok ? $"Built into:\n{outDir}\n\nRun {ExeName} there, or use the shortcut in the game folder."
               : "Build failed - see the Console for details.", "OK");
    }

    /// <summary>Batchmode entry point. Reads -phxOutput from the command line.</summary>
    public static void BuildFromCommandLine()
    {
        string outDir = GetArg("-phxOutput");
        if (string.IsNullOrEmpty(outDir))
        {
            Debug.LogError("[Phx Build] No -phxOutput <dir> given.");
            EditorApplication.Exit(2);
            return;
        }

        BuildReport report = Build(outDir);
        bool ok = report != null && report.summary.result == BuildResult.Succeeded;
        EditorApplication.Exit(ok ? 0 : 1);
    }

    static BuildReport Build(string outDir)
    {
        if (!Preflight()) return null;

        string[] scenes = GetEnabledScenes();
        if (scenes.Length == 0)
        {
            Debug.LogError("[Phx Build] No enabled scenes in Build Settings.");
            return null;
        }

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Phx Build] Cannot create '{outDir}': {e.Message}");
            return null;
        }

        // IL2CPP needs a module that plain "Windows Build Support" doesn't
        // include; Mono is the safe default and is what this project ships with.
        if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) == ScriptingImplementation.IL2CPP)
        {
            Debug.Log("[Phx Build] Scripting backend is IL2CPP - make sure the IL2CPP module is installed.");
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(outDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        Debug.Log($"[Phx Build] Building {scenes.Length} scene(s) -> {options.locationPathName}");
        BuildReport report = BuildPipeline.BuildPlayer(options);

        BuildSummary s = report.summary;
        if (s.result == BuildResult.Succeeded)
        {
            CopyLookupTable(outDir);
            Debug.Log($"[Phx Build] Succeeded: {s.totalSize / (1024 * 1024)} MB in {s.totalTime}");
        }
        else
        {
            Debug.LogError($"[Phx Build] {s.result}: {s.totalErrors} error(s)");
        }
        return report;
    }

    /// <summary>
    /// Scenes that belong in a shipped player.
    /// </summary>
    /// <remarks>
    /// Test scenes are excluded here rather than only in Build Settings,
    /// because Build Settings is a checkbox anyone can flip while debugging and
    /// then forget. PhxVehicleTesting was enabled and would have shipped.
    /// </remarks>

    /// <summary>
    /// Put lookup.csv next to the native library in the built player.
    /// </summary>
    /// <remarks>
    /// LibSWBF2 resolves its FNV name table relative to its own module, and
    /// Unity copies native plugins into Phoenix_Data/Plugins/x86_64 - but a
    /// .csv in Assets/Lib is not a plugin, so nothing carries it across. Without
    /// this the table comes up empty in a shipped build and every hash-named
    /// object stays a number instead of resolving to a name. It fails silently,
    /// which is why it needs to be handled here rather than noticed later.
    /// </remarks>
    static void CopyLookupTable(string outDir)
    {
        const string source = "Assets/Lib/lookup.csv";
        if (!File.Exists(source))
        {
            Debug.LogWarning($"[Phx Build] '{source}' is missing - hashed names will not " +
                             "resolve in this build. Copy it from LibSWBF2/lookup.csv.");
            return;
        }

        // Mirror wherever Unity actually put the plugin rather than assuming
        // the folder layout, which differs by target and Unity version.
        string dataDir = Path.Combine(outDir, Path.GetFileNameWithoutExtension(ExeName) + "_Data");
        string pluginRoot = Path.Combine(dataDir, "Plugins");
        if (!Directory.Exists(pluginRoot))
        {
            Debug.LogWarning($"[Phx Build] No plugin folder at '{pluginRoot}'; " +
                             "lookup.csv not copied.");
            return;
        }

        int copied = 0;
        foreach (string dll in Directory.GetFiles(pluginRoot, "LibSWBF2.dll", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(Path.GetDirectoryName(dll), "lookup.csv");
            try
            {
                File.Copy(source, dest, true);
                ++copied;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Phx Build] Could not copy lookup.csv to '{dest}': {e.Message}");
            }
        }

        if (copied == 0)
        {
            Debug.LogWarning("[Phx Build] LibSWBF2.dll was not found under the plugin folder; " +
                             "lookup.csv not copied.");
        }
        else
        {
            Debug.Log($"[Phx Build] lookup.csv copied beside {copied} native library instance(s).");
        }
    }

    static string[] GetEnabledScenes()
    {
        var list = new List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (!scene.enabled || string.IsNullOrEmpty(scene.path)) continue;

            string path = scene.path.Replace(System.IO.Path.DirectorySeparatorChar, (char)47);
            if (path.IndexOf("/Testing/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Debug.Log($"[Phx Build] Skipping test scene '{scene.path}'.");
                continue;
            }

            list.Add(scene.path);
        }
        return list.ToArray();
    }

    /// <summary>
    /// Things that make a build silently wrong rather than failing it.
    /// </summary>
    /// <remarks>
    /// Both of these produce a player that launches and then misbehaves in a
    /// way that looks like a runtime bug: missing particle shaders render
    /// magenta, and a missing native library throws DllNotFoundException on
    /// first asset load. Catching them here costs nothing and saves a 20-minute
    /// build plus the debugging that follows.
    /// </remarks>
    static bool Preflight()
    {
        bool ok = true;

        // HDRP "Particle System Shader Samples" is imported by
        // PhxFirstTimeEditorSetup on editor load, and that import is
        // asynchronous. A headless build can therefore start before it lands.
        const string samplesRoot = "Assets/Samples/High Definition RP";
        bool haveSamples = false;
        if (Directory.Exists(samplesRoot))
        {
            foreach (string versionDir in Directory.GetDirectories(samplesRoot))
            {
                if (Directory.Exists(Path.Combine(versionDir, "Particle System Shader Samples")))
                {
                    haveSamples = true;
                    break;
                }
            }
        }
        if (!haveSamples)
        {
            Debug.LogError("[Phx Build] HDRP 'Particle System Shader Samples' are not imported. " +
                           "Effect materials will render magenta. Open the project in the editor " +
                           "once (PhxFirstTimeEditorSetup imports them) and build again.");
            ok = false;
        }

        // The managed wrapper is useless without its native library, and the
        // failure only shows up when something first reads a .lvl.
        string[] natives = { "LibSWBF2.dll", "LibSWBF2.NET.dll", "lua50-swbf2-x64.dll" };
        foreach (string native in natives)
        {
            string p = Path.Combine("Assets/Lib", native);
            if (!File.Exists(p))
            {
                Debug.LogError($"[Phx Build] Missing native library '{p}'. " +
                               "Run BuildAndCopyLibsWin.bat first.");
                ok = false;
            }
        }

        return ok;
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; ++i)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
#endif
