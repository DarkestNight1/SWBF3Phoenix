#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
    #define UNIX
#endif

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

using LibLog = LibSWBF2.Logging.Logger;
using LibLogEntry = LibSWBF2.Logging.LoggerEntry;
using ELibLogType = LibSWBF2.Logging.ELogType;

#if UNIX
using System.IO;
#endif

// This is the Entry point for everything. In other words, this is ROOT.
public class PhxGame : MonoBehaviour
{
    public static PhxGame Instance { get; private set; } = null;


    public PhxPath GamePath => new PhxPath(Settings.GamePathString);


    [Header("Settings")]
    public PhxSettings Settings;

    [Header("References")]
    public PhxLoadscreen      InitScreenPrefab;
    public PhxLoadscreen      LoadScreenPrefab;
    public PhxMainMenu        MainMenuPrefab;
    public PhxPauseMenu       PauseMenuPrefab;
    public PhxCharacterSelect CharacterSelectPrefab;
    public Transform          CharSelectTransform;
    public Volume             CharSelectPPVolume;
    public AudioMixerGroup    UIAudioMixer;
    public PhxCamera          Camera;
    public PhysicsMaterial    GroundPhyMat;
    public PhxHUD             HUDPrefab;
    public PhxBolt            BoltPrefab;
    public PhxBeam            BeamPrefab;

    [Header("For non-Windows Users")]
    public string MissionListPath = Application.platform == RuntimePlatform.WindowsEditor ? "" : "path/to/missionlist.lua";

    // This will only fire for maps, NOT for the main menu!
    public Action OnMapLoaded;
    public Action<Type> OnRemoveMenu;

    public PhxPath AddonPath { get; private set; }
    public PhxPath StdLVLPC { get; private set; }

    // mapluafile of the currently entered map (e.g. "cor1c_con"), null in main menu.
    // Used by BF3 Legacy to decide per-map vertical battlefront setup.
    public string CurrentMapScript { get; private set; }

    /// <summary>
    /// Whether the current map came from an addon rather than the stock game.
    /// </summary>
    /// <remarks>
    /// The presentation layer needs this to keep a promise the project is
    /// built on: stock maps look like the stock game. Enhancements that invent
    /// content rather than interpret it - weather, a forced time of day, a
    /// space layer over a ground map - belong to the BF3 Legacy pack and its
    /// own maps, and must not rewrite what a stock .lgt authored.
    /// </remarks>
    public bool CurrentMapIsAddon { get; private set; }
    public string VersionString { get; private set; }
    public int VersionMajor { get; private set; }
    public int VersionMinor { get; private set; }
    public int VersionPatch { get; private set; }

    // Used only when registering addons
    PhxPath CurrentAddonFolder;

    PhxLoadscreen    CurrentLS;
    PhxMenuInterface CurrentMenu;

    // ring buffer
    AudioSource[] UIAudio = new AudioSource[5];
    byte UIAudioHead = 0;

    PhxEnvironment Env;
    Dictionary<string, string> RegisteredAddons = new Dictionary<string, string>();
    
    // Maps addons to their root folders
    Dictionary<string, PhxPath> AddonRoots = new Dictionary<string, PhxPath>();

    // Maps each registered mission script to the addon folder that owns it.
    //
    // AddonRoots alone is not enough: several BF3 Legacy components call
    // AddDownloadableContent with the SAME addon name but ship different
    // scripts under it (both BF3 and BF3Cato-Hunt register "CN3"), so keying
    // the folder by addon name lets the last addme.script win and sends map
    // loads to a folder that does not contain their script.
    Dictionary<string, PhxPath> ScriptRoots = new Dictionary<string, PhxPath>();

    bool bInitMainMenu;
    string UnitySceneName = null;

    // contains mapluafile strings, e.g. cor1c_con
    List<string> MapRotation = new List<string>();
    int MapRotationIdx = -1;



    // Do not call Destroy on destruction in Editor!
    // For some reason, when switching between Edit and Play mode,
    // Unity will create multiple instances of this, without calling
    // 'Awake' and then destroy those instances again immediately...
#if !UNITY_EDITOR
    ~PhxGame()
    {
        Debug.Log("PhxGame destructor called");
        Destroy();
    }
#endif

    public static PhxLuaRuntime GetLuaRuntime()
    {
        PhxEnvironment env = GetEnvironment();
        return env == null ? null : env.GetLuaRuntime();
    }

    public static PhxEnvironment GetEnvironment()
    {
        return Instance == null ? null : Instance.Env;
    }

    public static PhxCamera GetCamera()
    {
        return Instance == null ? null : Instance.Camera;
    }

    public static PhxScene GetScene()
    {
        PhxEnvironment env = GetEnvironment();
        return env == null ? null : env.GetScene();
    }

    public static PhxMatch GetMatch()
    {
        PhxEnvironment env = GetEnvironment();
        return env == null ? null : env.GetMatch();
    }

    public static PhxTimerDB GetTimerDB()
    {
        PhxEnvironment env = GetEnvironment();
        return env == null ? null : env.GetTimerDB();
    }

    public void Destroy()
    {
        Env?.Destroy();
        Env = null;
        Instance = null;
        Debug.Log("PhxGame destroyed");
    }

    public void AddToMapRotation(List<string> mapScripts)
    {
        MapRotation.AddRange(mapScripts);
    }

    public void AddToMapRotation(string mapScript)
    {
        MapRotation.Add(mapScript);
    }

    public void NextMap()
    {
        if (MapRotation.Count == 0)
        {
            return;
        }

        if (++MapRotationIdx >= MapRotation.Count)
        {
            MapRotationIdx = 0;
        }

        EnterSWBF2Map(MapRotation[MapRotationIdx]);
    }

    public void RegisterAddonScript(string scriptName, string addonName)
    {
        if (RegisteredAddons.TryGetValue(scriptName.ToLower(), out string addNm))
        {
            Debug.LogWarningFormat("Addon script '{0}' already registered to '{1}'!", scriptName, addNm);
        }
        else 
        {
            // CurrentAddonFolder is set in OnMainMenuExecution
            AddonRoots[addonName] = CurrentAddonFolder;
            ScriptRoots[scriptName.ToLower()] = CurrentAddonFolder;
            RegisteredAddons.Add(scriptName.ToLower(), addonName);
        }
    }

    /// <summary>
    /// Let the command line pick the boot map, overriding the settings asset.
    /// </summary>
    /// <remarks>
    /// Adapted from BF2GameExt by PrismaticFlower (MIT), which patches the
    /// retail 2005 executable - see https://github.com/PrismaticFlower/BF2GameExt
    /// and the credits in docs/BF2Compatibility.md.
    ///
    /// Almost nothing in that project ports to Phoenix, because almost all of
    /// it lifts hard limits and fixes crashes inside an engine Phoenix
    /// replaces rather than hooks: a 500-mission cap, heap exhaustion, sky
    /// object and sound layer overflows, a screenshot key. None of those exist
    /// here to fix.
    ///
    /// Its last patch is the exception, and it is a real feature rather than a
    /// workaround: repairing DLC mission list initialisation so the game can be
    /// launched straight into a mod map from the command line. Phoenix already
    /// had the destination - PhxBootMode.SWBF2Map with BootSWBF2Map - and no
    /// way to reach it except by editing the settings asset and re-entering
    /// play mode. This is that missing half.
    ///
    ///     Phoenix.exe -map kas2c_con
    ///
    /// Worth having beyond parity: every diagnostic pass on this project so far
    /// has meant loading one specific map repeatedly through two menus.
    /// </remarks>
    void ApplyCommandLineBoot()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        if (args == null) return;

        for (int i = 0; i < args.Length - 1; ++i)
        {
            // "+map" as well as "-map": the stock game and its launchers use
            // the plus form, and someone coming from those will reach for it.
            if (!string.Equals(args[i], "-map", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(args[i], "+map", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string script = args[i + 1];
            if (string.IsNullOrWhiteSpace(script)) continue;

            Settings.BootMode = PhxBootMode.SWBF2Map;
            Settings.BootSWBF2Map = script;

            Debug.Log($"[Phx] Command line requested boot map '{script}' - " +
                      "skipping the main menu.");
            return;
        }
    }

    public void EnterMainMenu(bool bInit = false)
    {
        Debug.Assert(Env == null || Env.IsLoaded);

        MapRotation.Clear();
        MapRotationIdx = -1;
        CurrentMapScript = null;

        bInitMainMenu = bInit;
        ShowLoadscreen(bInit);
        RemoveMenu(false);

        if (UnitySceneName != null)
        {
            SceneManager.UnloadSceneAsync(UnitySceneName);
            UnitySceneName = null;
        }

        Env?.Destroy();
        Env = PhxEnvironment.Create(StdLVLPC);

        if (!bInit)
        {
            Env.ScheduleRel("load/gal_con.lvl");
        }

        RegisteredAddons.Clear();
        ScriptRoots.Clear();
        AddonRoots.Clear();
        ExploreAddons();

        Env.OnExecuteMain += OnMainMenuExecution;
        Env.OnLoaded += OnMainMenuLoaded;
        Env.Run("missionlist");
    }

    public void EnterSWBF2Map(string mapScript)
    {
        Debug.Assert(Env == null || Env.IsLoaded);

        CurrentMapScript = mapScript;
        ShowLoadscreen();
        RemoveMenu(false);

        PhxPath addonPath = null;
        // Resolve through the per-script map, not the addon name: components
        // share addon names (BF3 and BF3Cato-Hunt both register "CN3"), so
        // keying by name mounts whichever folder registered last and the
        // script is then missing from it.
        //
        // Exact match only. An earlier version normalized the era letter
        // ("dea1c_con" -> registered "dea1x_con") on the theory that addons
        // register template eras while their shells launch concrete ones -
        // but that's wrong: era-x/y/g addon content is a DIFFERENT map that
        // merely shares a name prefix with the stock era-c/g map ("dea1x_con"
        // exists only in BF3Era's mission.lvl; "dea1c_con" exists only in
        // stock mission.lvl). Normalizing sent stock map selections to the
        // wrong addon's data path, and ScheduleRelFallback mounts addon data
        // INSTEAD OF stock, so the stock script the player actually picked
        // was never loaded: "Couldn't find script 'dea1c_con'!". Addon maps
        // never needed this - each addon registers its own exact script name
        // via AddDownloadableContent and its shell requests that same name,
        // so the plain lookup below already succeeds for them.
        string scriptKey = mapScript.ToLower();
        CurrentMapIsAddon = ScriptRoots.ContainsKey(scriptKey);
        if (ScriptRoots.TryGetValue(scriptKey, out PhxPath scriptRoot))
        {
            RegisteredAddons.TryGetValue(scriptKey, out string addonName);
            addonPath = AddonPath / scriptRoot / "data/_lvl_pc";

            // A wrong path here silently degrades to stock-only data, which
            // surfaces much later as "Couldn't find script '<map>'".
            if (!addonPath.Exists())
            {
                Debug.LogError($"[BF3Legacy] Addon data path for '{mapScript}' does not exist: " +
                               $"'{addonPath}' (addon '{addonName}', root '{scriptRoot}')");
            }
        }
        else
        {
            // Not an addon script, so only stock data gets mounted. That is the
            // correct and expected path for every stock map, so this is
            // information rather than a problem - if the script genuinely is
            // missing, PhxEnvironment.Execute reports it as an error.
            Debug.Log($"[BF3Legacy] '{mapScript}' is not an addon script; mounting stock game data " +
                      $"({RegisteredAddons.Count} addon script(s) registered).");
        }

        // Unload previous Unity Scene (if any)
        if (UnitySceneName != null)
        {
            SceneManager.UnloadSceneAsync(UnitySceneName);
            UnitySceneName = null;
        }

        Env?.Destroy();
        Env = PhxEnvironment.Create(StdLVLPC, addonPath);

        // Each environment gets a fresh Lua state, so the mod compatibility
        // helpers have to be reinstalled for mission scripts that use them.
        PhxBF3LegacyCompat.Install(Env.GetLuaRuntime());

        Env.ScheduleRel("load/common.lvl");
        Env.OnLoadscreenLoaded += OnLoadscreenTextureLoaded;
        Env.OnPostLoad += OnEnvLoaded;
        Env.Run(mapScript, "ScriptInit", "ScriptPostLoad");
    }

    public void EnterUnityScene(string sceneName)
    {
        Debug.Assert(Env == null || Env.IsLoaded);

        ShowLoadscreen();
        RemoveMenu(false);

        if (UnitySceneName != null)
        {
            SceneManager.UnloadSceneAsync(UnitySceneName);
            UnitySceneName = null;
        }

        SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        UnitySceneName = sceneName;

        SceneManager.sceneLoaded += (Scene scene, LoadSceneMode mode) =>
        {
            PhxUnityScript sceneInit = FindObjectOfType<PhxUnityScript>();

            // for some reason, this event fires twice...
            // and only the second time, the script is found
            if (sceneInit == null)
            {
                return;
            }

            Env?.Destroy();
            Env = PhxEnvironment.Create(StdLVLPC);
            Env.ScheduleRel("load/common.lvl");

            Env.OnLoadscreenLoaded += OnLoadscreenTextureLoaded;
            Env.OnPostLoad += OnEnvLoaded;

            Env.OnExecuteMain += sceneInit.ScriptInit;
            Env.OnPostLoad += sceneInit.ScriptPostLoad;

            Env.Run(null);
        };
    }

    public T ShowMenu<T>(T prefab) where T : PhxMenuInterface
    {
        if (CurrentMenu != null)
        {
            RemoveMenu(false);
        }
        CurrentMenu = Instantiate(prefab.gameObject).GetComponent<PhxMenuInterface>();
        return (T)CurrentMenu;
    }

    public void RemoveMenu()
    {
        RemoveMenu(true);
    }

    public bool IsMenuActive(PhxMenuInterface prefab)
    {
        Debug.Assert(prefab != null);
        return CurrentMenu == null ? false : CurrentMenu.GetType() == prefab.GetType();
    }

    void RemoveMenu(bool bInvokeEvent)
    {
        if (CurrentMenu != null)
        {
            Type menuType = CurrentMenu.GetType();
            CurrentMenu.Clear();
            Destroy(CurrentMenu.gameObject);
            CurrentMenu = null;
            if (bInvokeEvent)
            {
                OnRemoveMenu?.Invoke(menuType);
            }
        }
    }

    public void PlayUISound(AudioClip sound, float pitch = 1.0f)
    {
        UIAudio[UIAudioHead].clip = sound;
        UIAudio[UIAudioHead].pitch = pitch;
        UIAudio[UIAudioHead].Play();

        UIAudioHead++;
        if (UIAudioHead >= UIAudio.Length)
        {
            UIAudioHead = 0;
        }
    }

    void ShowLoadscreen(bool bInitScreen = false)
    {
        Debug.Assert(CurrentLS == null);
        CurrentLS = Instantiate(bInitScreen ? InitScreenPrefab : LoadScreenPrefab);
    }

    void OnLoadscreenTextureLoaded(Texture2D loadscreenTexture)
    {
        CurrentLS.SetLoadImage(loadscreenTexture);
    }

    void RemoveLoadscreen()
    {
        Debug.Assert(CurrentLS != null);
        CurrentLS.FadeOut();
        CurrentLS = null;
    }

    void Init()
    {
        Debug.Log("PhxGame Init");
        Debug.Assert(Instance == null);

        Instance = this;
        WorldLoader.UseHDRP = true;
        MaterialLoader.UseHDRP = true;
        EffectsLoader.UseHDRP = true;

        // BF3 Legacy install simplification: if no (valid) path is configured,
        // try to auto-detect a BF2 install in common Steam/GOG locations.
        if (!PhxGamePathDetector.IsValidGamePath(Settings.GamePathString))
        {
            string detected = PhxGamePathDetector.TryDetect();
            if (detected != null)
            {
                Debug.Log($"[BF3Legacy] Auto-detected BF2 installation: {detected}");
                Settings.GamePathString = detected;
            }
        }

        AddonPath = GamePath / "GameData/addon";
        StdLVLPC = GamePath / "GameData/data/_lvl_pc";

        if (GamePath.IsFile()              ||
            !GamePath.Exists()             ||
            !CheckStdLVLExistence("common.lvl")  ||
            !CheckStdLVLExistence("core.lvl")    ||
            !CheckStdLVLExistence("ingame.lvl")  ||
            !CheckStdLVLExistence("inshell.lvl") ||
            !CheckStdLVLExistence("mission.lvl") ||
            !CheckStdLVLExistence("shell.lvl"))
        {
            Debug.LogErrorFormat("Invalid game path '{0}!'", GamePath);
            return;
        }

        ApplyCommandLineBoot();

        if (Settings.BootMode == PhxBootMode.MainMenu)
        {
            EnterMainMenu(true);
        }
        else if (Settings.BootMode == PhxBootMode.SWBF2Map)
        {
            Debug.Assert(!string.IsNullOrEmpty(Settings.BootSWBF2Map));
            EnterSWBF2Map(Settings.BootSWBF2Map);
        }
        else if (Settings.BootMode == PhxBootMode.UnityScene)
        {
            Debug.Assert(!string.IsNullOrEmpty(Settings.BootUnityScene));
            EnterUnityScene(Settings.BootUnityScene);
        }
    }

    void ExploreAddons()
    {
        if (!AddonPath.Exists()) return;

        string[] addons = System.IO.Directory.GetDirectories(AddonPath);
        
        foreach (PhxPath addon in addons)
        {
            PhxPath addme = addon / "addme.script";
            if (addme.Exists() && addme.IsFile())
            {
                Env.ScheduleAbs(addme);
            }
        }
    }

    void OnMainMenuExecution()
    {
        Debug.Assert(CurrentLS != null);

        if (bInitMainMenu)
        {
            CurrentLS.SetLoadImage(TextureLoader.Instance.ImportUITexture("_LOCALIZE_english_bootlegal"));
        }
        else
        {
            CurrentLS.SetLoadImage(TextureLoader.Instance.ImportUITexture("gal_con"));
        }

        // Addons built against the SWBF2 UI Remaster (the whole BF3 Legacy pack)
        // call helpers the stock shell doesn't have. Define them before any
        // addme.script runs, or those scripts abort and their maps never
        // reach the mission list.
        PhxBF3LegacyCompat.Install(Env.GetLuaRuntime());

        foreach (var lvl in Env.Loaded)
        {
            if (lvl.DisplayPath.GetLeaf() == "addme.script")
            {
                var addme = lvl.Level.Get<LibSWBF2.Wrappers.Script>("addme");
                if (addme == null)
                {
                    Debug.LogWarningFormat("Seems like '{0}' has no 'addme' script chunk!", lvl.DisplayPath);
                    continue;
                }

                CurrentAddonFolder = lvl.DisplayPath - "addme.script";
                Env.Execute(addme);
            }
        }
    }

    void OnMainMenuLoaded()
    {
        RemoveLoadscreen();
        ShowMenu(MainMenuPrefab);
    }

    void OnEnvLoaded()
    {
        RemoveLoadscreen();
        Env.GetMatch().StartMatch();
        OnMapLoaded?.Invoke();

        // Headroom on the animation clip pool. Running it dry makes soldiers
        // silently lose their clips, and nothing in the resulting errors says
        // so - see PhxAnimationLoader.ReportClipPoolUsage.
        PhxAnimationLoader.ReportClipPoolUsage(CurrentMapScript);
    }

    bool CheckStdLVLExistence(string lvlName)
    {
        PhxPath p = StdLVLPC / lvlName;
        bool bExists = p.Exists();
        if (!bExists)
        {
            Debug.LogError($"Could not find '{p}'!");
        }
        return bExists;
    }

    void Awake()
    {
        VersionString = Application.version;
        string[] subStr = VersionString.Split('.');
        Debug.Assert(subStr.Length == 3);
        VersionMajor = int.Parse(subStr[0]);
        VersionMinor = int.Parse(subStr[1]);
        VersionPatch = int.Parse(subStr[2]);

        LibLog.SetLogLevel(ELibLogType.Warning);

        Debug.Assert(InitScreenPrefab     != null);
        Debug.Assert(LoadScreenPrefab     != null);
        Debug.Assert(MainMenuPrefab       != null);
        Debug.Assert(PauseMenuPrefab      != null);
        Debug.Assert(CharSelectTransform  != null);
        Debug.Assert(CharSelectPPVolume   != null);
        Debug.Assert(UIAudioMixer         != null);
        Debug.Assert(Camera               != null);
        Debug.Assert(GroundPhyMat         != null);
        Debug.Assert(HUDPrefab            != null);
        Debug.Assert(BoltPrefab           != null);
        Debug.Assert(BeamPrefab           != null);

        for (int i = 0; i < UIAudio.Length; ++i)
        {
            GameObject audioObj = new GameObject(string.Format("UIAudio{0}", i));
            audioObj.transform.SetParent(transform);
            UIAudio[i] = audioObj.AddComponent<AudioSource>();
            UIAudio[i].outputAudioMixerGroup = UIAudioMixer;
        }

        
#if UNIX
        if (PlayerPrefs.GetInt("PhoenixUnixRename") == 0)
        {
            // For Unix, ensure all folder- and file names are all lower case, starting from 'GameData'
            void LowerCaseRecursive(PhxPath p)
            {
                string[] files = Directory.GetFiles(p);
                for (int i = 0; i < files.Length; ++i)
                {
                    string fileName = new PhxPath(files[i]).GetLeaf();
                    if (fileName.ToLower() != fileName)
                    {
                        if (!File.Exists(p / fileName.ToLower()))
                        {
                            File.Move(p / fileName, p / fileName.ToLower());
                            Debug.Log($"Renamed '{p / fileName}' to '{p / fileName.ToLower()}'");
                        }
                    }
                }

                string[] subDirs = Directory.GetDirectories(p);
                for (int i = 0; i < subDirs.Length; ++i)
                {
                    string dirName = new PhxPath(subDirs[i]).GetLeaf();
                    if (dirName.ToLower() != dirName)
                    {
                        if (!Directory.Exists(p / dirName.ToLower()))
                        {
                            Directory.Move(p / dirName, p / dirName.ToLower());
                            Debug.Log($"Renamed '{p / dirName}' to '{p / dirName.ToLower()}'");
                        }
                    }

                    LowerCaseRecursive(p / dirName.ToLower());
                }
            }
            LowerCaseRecursive(GamePath / "GameData");
            PlayerPrefs.SetInt("PhoenixUnixRename", 1);
        }

#endif

        Init();
    }

    void Start()
    {
        //PhxProjectiles.Instance.InitProjectileMeshes();
    }

    void Update()
    {
        // TEMPORARY DIAGNOSTIC - remove with the rest of the PhxDamage.Trace
        // instrumentation. F9 toggles the damage trace in-game so the log can
        // be armed for one burst of fire rather than spamming the whole match.
        if (Input.GetKeyDown(KeyCode.F9))
        {
            PhxDamage.Trace = !PhxDamage.Trace;
            Debug.Log($"[PhxDamage] trace {(PhxDamage.Trace ? "ON" : "OFF")}");
        }

        Env?.Tick(Time.deltaTime);

        while (LibLog.GetNextLog(out LibLogEntry entry))
        {
            switch (entry.Level)
            {
                case ELibLogType.Info:
                    Debug.Log($"[LibSWBF2] {entry}");
                    break;
                case ELibLogType.Warning:
                    Debug.LogWarning($"[LibSWBF2] {entry}");
                    break;

                case ELibLogType.Error:
                    Debug.LogError($"[LibSWBF2] {entry}");
                    break;
            }
        }    
    }

    void FixedUpdate()
    {
        Env?.TickPhysics(Time.fixedDeltaTime);
    }
}