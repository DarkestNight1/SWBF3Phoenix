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

        EnsureConfigLoaded();

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
        MaterialLoader.AlphaCutoff = Config.AlphaCutoff;
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
            // The default upscale budget assumes a card with room to spare. On
            // 8GB hardware it competes with the screen-space history buffers
            // and the reflection probe cache for the same VRAM, and losing
            // that fight costs far more than sharper textures win.
            if (SystemInfo.graphicsMemorySize > 0 &&
                SystemInfo.graphicsMemorySize < 10000 &&
                Config.UpscaleBudgetMB > 768)
            {
                Debug.Log($"[BF3Legacy] {SystemInfo.graphicsMemorySize}MB of video memory; " +
                          $"capping the texture upscale budget at 768MB (config asked for " +
                          $"{Config.UpscaleBudgetMB}MB).");
                Config.UpscaleBudgetMB = 768;
            }

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

    static bool ConfigLoaded;

    /// <summary>
    /// Load the config once, from whichever bootstrap runs first.
    /// </summary>
    /// <remarks>
    /// PhxBF3.Bootstrap and BFPresentation.Bootstrap are both
    /// RuntimeInitializeOnLoadMethod(AfterSceneLoad), and Unity does not define
    /// their relative order. BFPresentation reads PresentationQuality and
    /// ModernLighting, so when it won the race it read the compiled defaults
    /// and the user's bf3legacy.json was silently ignored - a quality tier that
    /// works on one launch and not the next.
    ///
    /// Separate from LoadConfig so that an explicit reload (the editor setup
    /// window does one) still forces a re-read.
    /// </remarks>
    public static void EnsureConfigLoaded()
    {
        if (ConfigLoaded) return;
        LoadConfig();
    }

    /// <summary>Whether the config on disk was read, as opposed to defaults.</summary>
    public static bool ConfigLoadedFromDisk { get; private set; }

    public static void LoadConfig()
    {
        ConfigLoaded = true;
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config = JsonUtility.FromJson<PhxBF3Config>(File.ReadAllText(ConfigPath));
                ConfigLoadedFromDisk = true;
            }
            else
            {
                SaveConfig();
                ConfigLoadedFromDisk = false;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BF3Legacy] Failed to load config, using defaults: {e.Message}");
            Config = new PhxBF3Config();
            ConfigLoadedFromDisk = false;
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
    //
    // 1440p, not 4K. PhxResolutionManager picks the largest supported mode at
    // or below the target, so a 3840x2160 default silently opts every 4K
    // display into rendering at 4K - including the mid-range hardware this
    // project is tuned for, where the screen-space stack cannot afford it.
    // Anyone with the headroom can raise it; nobody should be opted in by a
    // number they never saw.
    public int TargetWidth = 2560;
    public int TargetHeight = 1440;
    public bool UseDynamicResolution = true;

    /// <summary>
    /// Sync presentation to the display. On by default.
    /// </summary>
    /// <remarks>
    /// Every quality tier shipped with vSyncCount 0. Uncapped presentation
    /// into a borderless fullscreen window judders regardless of how high the
    /// frame rate is, because frames finish out of step with the compositor -
    /// which reads as choppiness even on hardware with headroom to spare.
    /// </remarks>
    public bool VSync = true;

    /// <summary>Frame cap when VSync is off. 0 leaves it uncapped.</summary>
    public int TargetFrameRate = 0;

    /// <summary>
    /// Fixed render scale as a fraction of native, or 1.0 to let dynamic
    /// resolution decide.
    /// </summary>
    /// <remarks>
    /// Anything other than 1.0 pins the resolution there and takes the
    /// automatic scaler out of the loop - the manual override for someone who
    /// would rather choose a constant image than have it move.
    ///
    /// This had no consumer at all until dynamic resolution was wired up; it
    /// was a knob in a config file that did nothing, which is how the same
    /// class of bug got into the graphics stack in the first place.
    /// </remarks>
    public float RenderScale = 1.0f;

    /// <summary>
    /// Default floor for dynamic resolution, as a percentage of native.
    /// </summary>
    /// <remarks>
    /// Used by maps that do not state a floor of their own. A map profile that
    /// does state one replaces this, in both directions - interiors hold a
    /// higher floor because they run SSGI, space allows a lower one because its
    /// geometry upscales cleanly. The pipeline asset's minPercentage is the
    /// absolute backstop beneath all of them.
    /// </remarks>
    /// <remarks continued>
    /// Raised from 65 to 80 after the softness was reported twice. 65% of
    /// 1440p is 936p being upscaled to fill the screen, which is a visible
    /// loss on exactly the high-frequency detail a character model is made of
    /// - and DRS spends most of its time at the floor, not at the ceiling, so
    /// the floor is what the game normally looks like rather than a worst
    /// case. 80% is 1152p, which upscales cleanly.
    ///
    /// This trades frames for sharpness. Turn it back down if the frame rate
    /// suffers; the resident scale is logged, so it is checkable rather than a
    /// matter of opinion.
    /// </remarks>
    public float MinDynamicResolutionPercent = 80f;

    /// <summary>
    /// Motion blur strength, 0 to 1. 0 disables it.
    /// </summary>
    /// <remarks>
    /// Low on purpose. This is a third-person game, so the player character
    /// animates in screen space every frame while the world behind it does
    /// not - motion blur reads that as motion and softens the one thing the
    /// player is always looking at. It was 0.35, which is a reasonable number
    /// for a first-person camera and too much here.
    /// </remarks>
    public float MotionBlurIntensity = 0.12f;

    /// <summary>
    /// How aggressively TAA discards history where motion vectors disagree,
    /// 0 to 1.
    /// </summary>
    /// <remarks>
    /// HDRP defaults this to 0, meaning it never rejects. Static geometry
    /// reprojects perfectly and stays crisp; a skinned character deforms every
    /// frame so its history never quite matches, and the accumulated result
    /// ghosts. Raising this trades a little temporal stability on characters
    /// for them actually being sharp. Turn it down if edges start to shimmer.
    /// </remarks>
    public float TemporalMotionRejection = 0.55f;

    /// <summary>
    /// Fraction of the frame budget held back as headroom, so the scaler aims
    /// under the refresh interval rather than exactly at it.
    /// </summary>
    public float DynamicResolutionHeadroom = 0.15f;

    // RayTracedEffects was removed. The pipeline asset has supportRayTracing
    // off with no ray tracing resources assigned, so it could never have done
    // anything, and the target hardware could not afford it if it did.

    // Extra fidelity pass: TAA, motion blur, vignette, GPU instancing
    public bool GraphicsEnhancements = true;

    /// <summary>
    /// Simulated camera-lens artifacts: chromatic aberration and film grain.
    /// </summary>
    /// <remarks>
    /// Off, because these are the one part of the modern stack that makes the
    /// image worse rather than better here. They do not improve how the map is
    /// lit or shaded - they add colour fringing to every high-contrast edge and
    /// noise over the whole frame, both of which read as rendering faults
    /// against stock art that never had them. Everything else in the stack
    /// (AO, SSR, volumetrics, bloom, TAA) interprets the map's own data and
    /// stays on.
    /// </remarks>
    public bool LensSimulation = false;

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
    /// Alpha threshold for cutout materials - fences, grates, foliage cards.
    /// </summary>
    /// <remarks>
    /// Below HDRP's 0.5 default because 2005 alpha maps were drawn for a
    /// hardware alpha test against soft, often dithered edges; clipping at the
    /// midpoint eats the outer pixels of a leaf or a chain link. Raise it if
    /// foliage looks fringed, lower it if it looks chewed.
    /// </remarks>
    public float AlphaCutoff = 0.35f;

    /// <summary>
    /// Make the team hero selectable from the spawn screen immediately.
    /// </summary>
    /// <remarks>
    /// A testing switch, off by default. It removes the unlock requirement and
    /// nothing else - the hero is still the map's own, still limited to one per
    /// team, and the slot is still spent when they die. Shipping behaviour is
    /// the stock rule set: earn the points, unlock, spawn once.
    ///
    /// Kept as config rather than a build define so a hero can be checked in a
    /// player build, which is where the interesting problems are.
    /// </remarks>
    public bool AlwaysAllowHeroes = false;

    /// <summary>
    /// Aim reticle size, in units of a 1920x1080 canvas. 0 keeps the default.
    /// </summary>
    /// <remarks>
    /// The shipped default is 56, roughly 5% of screen height, which is where
    /// shooter reticles normally sit. Raise it if the ammo arcs around the ring
    /// are hard to read at your resolution; much below about 40 and they stop
    /// being legible at all.
    /// </remarks>
    public float CrosshairSize = 0f;

    /// <summary>
    /// How far a rifle shot carries to AI ears, in metres. 0 makes them deaf
    /// to gunfire.
    /// </summary>
    /// <remarks>
    /// Tunable because it is the single loudest knob in the AI. Gunfire
    /// reporting had no effect for as long as weapons failed to record who
    /// fired them, so 60 m is an untested default rather than a measured one -
    /// the AI has only now started hearing anything at all. Too high and every
    /// firefight drags the whole map toward it, abandoning objectives; too low
    /// and they stand with their backs to a shooter. Turn it down first if
    /// bots start mobbing noise.
    /// </remarks>
    public float GunshotHearingRange = 60f;

    /// <summary>How far an explosion carries to AI ears, in metres.</summary>
    public float ExplosionHearingRange = 120f;

    /// <summary>
    /// Allow direct fire to damage your own team.
    /// </summary>
    /// <remarks>
    /// Off by default. Gunfire was unattributed until kills started being
    /// credited, so friendly fire has never actually been reachable in this
    /// project - turning it on with 32v32 and AI that have no line-of-fire
    /// check means being shot in the back by your own side constantly.
    /// Explosions and mines ignore this: a grenade at your feet killing you is
    /// correct, and always was.
    /// </remarks>
    public bool FriendlyFire = false;

    /// <summary>
    /// How quickly the view settles into and out of zoom. Higher is snappier.
    /// </summary>
    public float ZoomTransitionSpeed = 14f;

    /// <summary>
    /// How much zoom slows turning, 0 to 1.
    /// </summary>
    /// <remarks>
    /// 1 makes turn rate fully proportional to magnification, so an 8x scope
    /// turns an eighth as fast - correct for aiming and unpleasant if you need
    /// to react. 0 leaves turn rate alone. Applied against the soldier's own
    /// MaxTurnSpeed, which is rewritten every tick, so it has to be reapplied
    /// there rather than set once.
    /// </remarks>
    public float ZoomTurnSlowdown = 0.75f;

    /// <summary>
    /// Re-compress imported world textures to DXT on upload.
    /// </summary>
    /// <remarks>
    /// Off, because it costs image quality twice over.
    ///
    /// The source in the .lvl is already DXT, which is a lossy block codec.
    /// LibSWBF2 hands it back as RGBA - decompressed - and this then
    /// compressed it to DXT again. Re-quantising blocks that were already
    /// quantised is a second generation of loss, and it lands hardest on the
    /// fine detail DXT handles worst: insignia, panel lines, faces. That is
    /// why characters read soft and slightly mushy up close.
    ///
    /// The memory it reclaimed was real but is not where this project's VRAM
    /// goes. The pipeline asset currently allocates a 4096x4096 decal atlas, a
    /// 4096x4096 shadow atlas for area lights in a game that has none, and 64
    /// uncompressed reflection probes - each larger than the whole saving.
    ///
    /// The right fix is to upload the source's own DXT blocks untouched,
    /// which is both lossless relative to the original and the same memory as
    /// re-compressing. It needs LibSWBF2's ETextureFormat exported through the
    /// C API - it exists natively, in Types/Enums.h, but no wrapper reaches it.
    /// </remarks>
    public bool CompressTextures = false;

    // Runtime texture upscaling of the original game's textures
    public bool UpscaleTextures = true;
    public int UpscaleFactor = 2;             // 2 or 4
    public float UpscaleSharpness = 0.5f;     // 0..1
    public int UpscaleBudgetMB = 1536;        // cap on added VRAM

    // Music and voice-over from the mission scripts' audio API
    public bool MusicEnabled = true;
    public float MusicVolume = 0.6f;
    public float VOVolume = 1.0f;

    // Positional ambience placed in the world (machinery, shield hums, water).
    public bool AmbienceEnabled = true;
    public float AmbienceVolume = 0.7f;
}
