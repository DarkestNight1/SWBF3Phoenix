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

        // Base-game parity rather than a BF3 extra, so it is not behind a
        // feature toggle: BF2 always had a scoreboard on Tab.
        Host.AddComponent<PhxScoreboard>();

        // Also parity, not an enhancement: every stock map authors its own
        // fog/sun in the .sky config, which the importer now surfaces.
        Host.AddComponent<PhxMapAtmosphere>();

        // Hand the importer the smoothness scale before anything loads. The
        // importer can't reach into BF3Legacy config itself (it's a separate,
        // engine-agnostic assembly), so the runtime pushes the value in.
        MaterialLoader.SmoothnessScale = Config.SmoothnessScale;
        MaterialLoader.NormalMapStrength = Config.NormalMapStrength;
        TextureLoader.CompressWorldTextures = Config.CompressTextures;

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

    const string ConfigFileName = "bf3legacy.json";

    /// <summary>
    /// Where the config lives.
    ///
    /// A self-contained install (the build dropped inside the Battlefront II
    /// folder) keeps its config next to the executable, so the whole thing can
    /// be copied, moved or deleted as one folder and leaves nothing behind.
    /// Otherwise it falls back to Unity's persistent data path, next to the
    /// player log - which is also what the editor uses.
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            string portable = PortableConfigPath;
            if (portable != null && (File.Exists(portable) || IsWritableDir(Path.GetDirectoryName(portable))))
            {
                return portable;
            }
            return Path.Combine(Application.persistentDataPath, ConfigFileName);
        }
    }

    /// <summary>Config path for a self-contained install, or null in the editor.</summary>
    static string PortableConfigPath
    {
        get
        {
            // The editor's "install root" is the Unity project folder; writing
            // config into the repo would be wrong, so portable mode is
            // player-only.
            if (Application.isEditor) return null;

            string root = PhxGamePathDetector.GetInstallRoot();
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, ConfigFileName);
        }
    }

    /// <summary>
    /// A build under Program Files can't write next to itself. Probe rather
    /// than guess, so the fallback to persistentDataPath is automatic.
    /// </summary>
    static bool IsWritableDir(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        string probe = Path.Combine(dir, ".phx_write_test");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>
    /// Exposure bias in EV applied on top of auto-exposure, for maps the
    /// metering reads badly. Positive brightens, negative darkens; high-albedo
    /// levels (Mygeeto's snow, Polis Massa's white interiors) are the ones that
    /// clip, so start around -1.5 and work down. 0 leaves auto-exposure alone.
    /// </summary>
    public float ExposureCompensation = 0f;

    /// <summary>Bloom strength. The 2005 renderer over-bloomed; 0 disables.</summary>
    public float BloomIntensity = 0.15f;

    /// <summary>
    /// Multiplies every soldier's ODF JumpHeight. Apex scales linearly with
    /// this (launch velocity by its square root), so 1.25 is about a quarter
    /// higher, not a quarter faster. 1.0 is stock BF2.
    /// </summary>
    public float JumpHeightScale = 1.25f;
    public bool HighResolutionMode = true;
    public bool VerticalBattlefront = true;   // seamless ground <-> space transitions
    public bool DynamicWeather = true;        // per-map precipitation, storms, day/night
    public bool ProceduralAnimation = true;   // modern lean/recoil layered on stock anims

    /// <summary>
    /// Units per team, player included, overriding whatever the mission script
    /// asked for. BF2's stock scripts field roughly 8-16 a side; 32 gives the
    /// 32v32 battles the engine can now afford. 0 keeps each map's own value.
    /// </summary>
    public int TeamSize = 32;

    /// <summary>
    /// Reinforcements per team, overriding the mission script's own count.
    /// Stock scripts ask for ~150, which was tuned for BF2's much smaller
    /// squads: at the 32v32 of <see cref="TeamSize"/> every death still costs
    /// one ticket, so the same 150 drains several times faster and rounds end
    /// almost immediately. 0 keeps each map's own value.
    /// </summary>
    public int Reinforcements = 400;

    /// <summary>
    /// AI difficulty: 0 Recruit, 1 Normal, 2 Hard, 3 Elite.
    /// </summary>
    /// <remarks>
    /// Difficulty changes decision quality, not just accuracy - reaction time,
    /// positioning, target selection, objective prioritisation and how wide a
    /// band of "good enough" choices a soldier draws from (see
    /// <see cref="BFAIDecision"/> and <see cref="BFAimProfile"/>). No tier has
    /// perfect aim, including this one.
    ///
    /// Defaults to Hard until there is a difficulty selector on the map/mode
    /// screen to set it from.
    /// </remarks>
    public int AIDifficulty = 2;

    // Gore: 0 = off, 1 = sparks only (cauterized, no detach), 2 = full dismemberment
    public int GoreLevel = 2;

    /// <summary>
    /// Presentation quality tier: 0 Low, 1 Medium, 2 High, 3 Ultra.
    /// Drives every budget in the presentation layer - decals, impact lights,
    /// probes, terrain deformation resolution, interaction distance, and which
    /// HDRP features are on at all.
    /// </summary>
    public int PresentationQuality = 2;

    /// <summary>
    /// Derive normal and occlusion maps from the stock diffuse textures.
    /// </summary>
    /// <remarks>
    /// A separate switch from the quality tier because its cost is paid at
    /// load rather than per frame - one pass over every texture in the level -
    /// and because it is the one part of the presentation layer that changes
    /// how the original art reads. Turning it off costs nothing that was in
    /// the source data.
    /// </remarks>
    public bool DerivedMaterialMaps = true;

    // Graphics
    public int TargetWidth = 3840;
    public int TargetHeight = 2160;
    public bool UseDynamicResolution = true;
    public float RenderScale = 1.0f;
    public bool RayTracedEffects = false;     // only honored on capable hardware

    // Extra fidelity pass: TAA, motion blur, DoF, PBR sky, reflection probe,
    // GPU instancing, mip bias
    public bool GraphicsEnhancements = true;

    /// <summary>
    /// Scales the smoothness imported from each material's authored specular
    /// exponent. 1 uses the converted value as-is; lower it if surfaces read
    /// too glossy, 0 restores the old fully-matte look.
    /// </summary>
    public float SmoothnessScale = 1.0f;

    /// <summary>
    /// Strength of the bump maps BF2 authored (the BumpMap material flag and
    /// second texture slot). 0 disables them.
    /// </summary>
    public float NormalMapStrength = 1.0f;

    /// <summary>
    /// Re-compress imported world textures to DXT on upload. The source is
    /// already DXT in the .lvl but arrives uncompressed, so this reclaims
    /// several times their VRAM. Turn off to compare quality.
    /// </summary>
    public bool CompressTextures = true;

    // Runtime texture upscaling of the original game's textures
    public bool UpscaleTextures = true;
    public int UpscaleFactor = 2;             // 2 or 4
    public float UpscaleSharpness = 0.5f;     // 0..1
    public int UpscaleBudgetMB = 1536;        // cap on added VRAM

    // Music and voice-over from the mission scripts' audio API
    public bool MusicEnabled = true;
    public float MusicVolume = 0.6f;
    public float VOVolume = 1.0f;
}
