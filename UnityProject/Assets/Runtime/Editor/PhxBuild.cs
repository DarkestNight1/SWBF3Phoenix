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
            Debug.Log($"[Phx Build] Succeeded: {s.totalSize / (1024 * 1024)} MB in {s.totalTime}");
        }
        else
        {
            Debug.LogError($"[Phx Build] {s.result}: {s.totalErrors} error(s)");
        }
        return report;
    }

    static string[] GetEnabledScenes()
    {
        var list = new List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled && !string.IsNullOrEmpty(scene.path)) list.Add(scene.path);
        }
        return list.ToArray();
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
