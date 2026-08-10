#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

/// <summary>
/// Removes the remaining manual steps between "cloned the repo" and "pressed
/// Play in PhxMainScene".
///
/// Two things used to be on the user:
///   1. Import the HDRP "Particle System Shader Samples" via the Package
///      Manager (README step 8). Without it, effect materials fall back to
///      magenta - and nothing tells you why.
///   2. Type the Battlefront II install path into the Game object.
///
/// The path is handled by PhxGamePathDetector plus the installer scripts
/// (which write GamePathOverride into bf3legacy.json). This class covers the
/// sample import, and prints a one-line status on load so a broken setup is
/// obvious in the Console instead of at runtime.
/// </summary>
[InitializeOnLoad]
public static class PhxFirstTimeEditorSetup
{
    const string HDRPPackage = "com.unity.render-pipelines.high-definition";
    const string SampleName = "Particle System Shader Samples";
    const string SamplesRoot = "Assets/Samples/High Definition RP";

    // Report once per editor session, not on every domain reload.
    const string ReportedKey = "Phx_SetupReported";

    static PhxFirstTimeEditorSetup()
    {
        // Package manager state isn't reliable during static construction.
        EditorApplication.delayCall += Run;
    }

    static void Run()
    {
        EnsureParticleSamples();

        if (SessionState.GetBool(ReportedKey, false)) return;
        SessionState.SetBool(ReportedKey, true);
        ReportSetup();
    }

    static bool SamplesImported()
    {
        if (!Directory.Exists(SamplesRoot)) return false;

        // Assets/Samples/High Definition RP/<version>/<sample name>
        foreach (string versionDir in Directory.GetDirectories(SamplesRoot))
        {
            if (Directory.Exists(Path.Combine(versionDir, SampleName))) return true;
        }
        return false;
    }

    static void EnsureParticleSamples()
    {
        if (SamplesImported()) return;

        try
        {
            // An empty version asks for the samples of whatever HDRP version
            // this project actually resolved.
            Sample sample = Sample.FindByPackage(HDRPPackage, string.Empty)
                                  .FirstOrDefault(s => s.displayName == SampleName);

            if (sample.displayName != SampleName)
            {
                Debug.LogWarning($"[Phx Setup] Could not find the HDRP sample '{SampleName}'. " +
                                 "Import it by hand: Window > Package Manager > High Definition RP > Samples.");
                return;
            }

            if (sample.isImported) return;

            if (sample.Import(Sample.ImportOptions.HideImportWindow))
            {
                Debug.Log($"[Phx Setup] Imported HDRP sample '{SampleName}' - " +
                          "effect materials will resolve correctly.");
            }
            else
            {
                Debug.LogWarning($"[Phx Setup] Import of '{SampleName}' failed. " +
                                 "Import it by hand: Window > Package Manager > High Definition RP > Samples.");
            }
        }
        catch (System.Exception e)
        {
            // Never let setup convenience break opening the project.
            Debug.LogWarning($"[Phx Setup] Could not auto-import '{SampleName}' ({e.Message}). " +
                             "Import it by hand: Window > Package Manager > High Definition RP > Samples.");
        }
    }

    static void ReportSetup()
    {
        PhxBF3.LoadConfig();

        string gamePath = PhxGamePathDetector.TryDetect();
        if (string.IsNullOrEmpty(gamePath))
        {
            Debug.LogWarning("[Phx Setup] No Battlefront II install found. Run " +
                             "'Tools/install_mod.ps1' (or install_mod.sh --setup) to point the " +
                             "project at your copy, or set it in the in-game setup panel.");
            return;
        }

        string addon = Path.Combine(gamePath, "GameData", "addon");
        int mods = Directory.Exists(addon) ? Directory.GetDirectories(addon).Length : 0;

        Debug.Log($"[Phx Setup] Battlefront II: {gamePath} ({mods} addon(s) installed). " +
                  "Open Runtime/Scenes/PhxMainScene and press Play.");
    }
}
#endif
