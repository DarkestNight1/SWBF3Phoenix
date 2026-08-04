using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Central entry point and configuration for the BF3 Legacy feature set.
///
/// "BF3 Legacy" refers both to the feature set of Free Radical's cancelled
/// Star Wars Battlefront III (and its salvaged release, Elite Squadron) that
/// this project re-implements, and to compatibility with the community
/// "Star Wars Battlefront III Legacy" mod (ModDB), which ships recovered
/// BF3 assets as standard SWBF2 addon content loadable by this runtime.
///
/// Feature pillars:
///  - Capital ship destruction (Elite Squadron style: shields -> hangar -> reactor -> breakup)
///  - Lightsaber dismemberment
///  - Modernized AI (squads, flanking, difficulty tiers)
///  - Modern lighting / 4K graphics (HDRP volume overrides)
///  - Extended mod support (load order, mod detection, BF3 Legacy compatibility)
///  - Recreations of documented BF3 maps (greybox generators, see Maps/)
/// </summary>
public static class PhxBF3
{
    public const string Version = "0.1.0";

    public static PhxBF3Config Config { get; private set; } = new PhxBF3Config();

    // Runtime host object carrying the BF3 Legacy MonoBehaviours
    public static GameObject Host { get; private set; }

    public static bool IsInitialized => Host != null;


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (Host != null) return;

        LoadConfig();

        Host = new GameObject("BF3Legacy");
        GameObject.DontDestroyOnLoad(Host);

        // Always present: guides the user through setup if no BF2 install is
        // found, instead of failing to a black screen.
        Host.AddComponent<PhxFirstRunSetup>();

        if (Config.EnhancedAI)
        {
            Host.AddComponent<PhxAIDirector>();
        }
        if (Config.ModernLighting)
        {
            Host.AddComponent<PhxModernLighting>();
        }
        if (Config.HighResolutionMode)
        {
            Host.AddComponent<PhxResolutionManager>();
        }
        if (Config.VerticalBattlefront)
        {
            Host.AddComponent<PhxVerticalBattlefront>();
        }
        if (Config.DynamicWeather)
        {
            Host.AddComponent<PhxWeatherSystem>();
        }
        if (Config.ProceduralAnimation)
        {
            Host.AddComponent<PhxProceduralMotionManager>();
        }
        if (Config.GraphicsEnhancements)
        {
            Host.AddComponent<PhxGraphicsEnhancer>();
        }
        if (Config.UpscaleTextures)
        {
            Host.AddComponent<PhxTextureUpscaler>();
        }

        // Mods are also re-scanned on map load (PhxModManager.EnsureScanned),
        // in case PhxGame wasn't ready at bootstrap time.
        PhxModManager.Scan();

        Debug.Log($"[BF3Legacy] Initialized v{Version} " +
                  $"(AI: {Config.EnhancedAI}, Lighting: {Config.ModernLighting}, " +
                  $"4K: {Config.HighResolutionMode}, Dismemberment: {Config.Dismemberment}, " +
                  $"CapitalShips: {Config.CapitalShipDestruction})");
    }

    /// <summary>
    /// Config lives next to the save games so users can edit it without
    /// touching game files: %AppData%/../LocalLow/.../bf3legacy.json
    /// </summary>
    public static string ConfigPath => Path.Combine(Application.persistentDataPath, "bf3legacy.json");

    public static void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config = JsonUtility.FromJson<PhxBF3Config>(File.ReadAllText(ConfigPath));
            }
            else
            {
                SaveConfig();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Failed to load config, using defaults: {e.Message}");
            Config = new PhxBF3Config();
        }
    }

    public static void SaveConfig()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(Config, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Failed to save config: {e.Message}");
        }
    }
}

[Serializable]
public class PhxBF3Config
{
    // Path to the user's Star Wars Battlefront II (2005) install. Empty means
    // "auto-detect". Set by the first-run setup panel so users never have to
    // edit the Unity scene or hand-edit settings.
    public string GamePathOverride = "";

    // Feature toggles
    public bool CapitalShipDestruction = true;
    public bool Dismemberment = true;
    public bool EnhancedAI = true;
    public bool ModernLighting = true;
    public bool HighResolutionMode = true;
    public bool VerticalBattlefront = true;   // seamless ground <-> space transitions
    public bool DynamicWeather = true;        // per-map precipitation, storms, day/night
    public bool ProceduralAnimation = true;   // modern lean/recoil layered on stock anims

    // AI difficulty: 0 = Classic (vanilla-ish), 1 = Veteran, 2 = Elite, 3 = Legendary
    public int AIDifficulty = 2;

    // Gore: 0 = off, 1 = sparks only (cauterized, no detach), 2 = full dismemberment
    public int GoreLevel = 2;

    // Graphics
    public int TargetWidth = 3840;
    public int TargetHeight = 2160;
    public bool UseDynamicResolution = true;
    public float RenderScale = 1.0f;
    public bool RayTracedEffects = false;     // only honored on capable hardware

    // Extra fidelity pass: TAA, motion blur, DoF, PBR sky, reflection probe,
    // GPU instancing, mip bias
    public bool GraphicsEnhancements = true;

    // Runtime texture upscaling of the original game's textures
    public bool UpscaleTextures = true;
    public int UpscaleFactor = 2;             // 2 or 4
    public float UpscaleSharpness = 0.5f;     // 0..1
    public int UpscaleBudgetMB = 1536;        // cap on added VRAM
}
