using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2;
using LibSWBF2.Enums;
using LibSWBF2.Wrappers;


/*
 * Phases of PhxEnvironment:
 *     
 * 1. Init                          Environment is created, with base LVLs scheduled and ready to load. Base LVLs are:
 *                                      - core.lvl
 *                                      - shell.lvl
 *                                      - common.lvl
 *                                      - mission.lvl
 * 2. Loading Base                  Loading scheduled base LVLs
 * 3. Executing Main                There's always a LUA script responsible for environment setup that will be executed in this phase.
 *                                  Usually, there's an optional main function within the main script that will called aswell, if specified.
 *                                  The main LUA script is expected to call ReadDataFile on multiple LVL files. These LVL files will be scheduled.
 * 4. Loading World                 All LVLs that have been scheduled during phase 3 will be loaded in this phase
 * 5. Create Scene                  In this phase, scene conversion of the imported world LVL takes place. 
 * 6. Loaded                        Final state. Everything is done.
 * 
 */


public class PhxEnvironment
{
    public struct LVL
    {
        public SWBF2Handle Handle;
        public Level Level;

        // For Debug
        public PhxPath DisplayPath;
        public bool bIsAddon;
    }

    public enum EnvStage
    {
        Init,        
        LoadingBase, 
        ExecuteMain, 
        LoadingWorld,
        CreateScene, 
        Loaded       
    }


    public List<LVL> Loading = new List<LVL>();
    public List<LVL> Loaded  = new List<LVL>();

    public bool              IsLoaded => Stage == EnvStage.Loaded;
    public Action<Texture2D> OnLoadscreenLoaded;
    public Action            OnExecuteMain;
    public Action            OnLoaded;          // Same frame load is done
    public Action            OnPostLoad;        // one frame AFTER load is done

    public PhxPath GameDataPath { get; private set; }
    public PhxPath AddonDataPath { get; private set; }
    public EnvStage Stage { get; private set; }

    bool CanSchedule => Stage == EnvStage.Init || Stage == EnvStage.ExecuteMain;
    bool CanExecute  => Stage == EnvStage.ExecuteMain || Stage == EnvStage.CreateScene || Stage == EnvStage.Loaded;

    PhxLuaRuntime  LuaRT;
    SWBF2Handle LoadscreenHandle;

    Container EnvCon;
    Level     WorldLevel;     // points to level inside 'Loaded'
    Level     LoadscreenLVL;  // points to level inside 'Loaded'

    string InitScriptName;
    string InitFunctionName;
    string PostLoadFunctionName;

    PhxScene RTScene;
    PhxMatch Match;
    PhxTimerDB Timers;

    List<Localization> Localizations = new List<Localization>();
    Dictionary<string, List<Localization>> LocalizationLookup = new Dictionary<string, List<Localization>>();

    // To prevent loading an lvl more than once.
    // The path here always describes the relative 2-leaf lvl path
    Dictionary<PhxPath, SWBF2Handle> PathToHandle = new Dictionary<PhxPath, SWBF2Handle>();

    bool FirePostLoadEvent;


    PhxEnvironment(PhxPath dataPath, PhxPath addonPath)
    {
        GameDataPath = dataPath;
        AddonDataPath = addonPath;
        Stage = EnvStage.Init;
        WorldLevel = null;

        LuaRT = new PhxLuaRuntime();
        EnvCon = new Container();

        Loader.SetGlobalContainer(EnvCon);
    }

    ~PhxEnvironment()
    {
        Destroy();
    }

    public void Destroy()
    {
        WorldLevel = null;
        Match.Destroy();
        Match = null;
        Timers = null;
        RTScene.Destroy();
        RTScene = null;
        OnLoadscreenLoaded = null;
        OnExecuteMain = null;
        OnLoaded = null;
        LuaRT?.Close();
        EnvCon?.Delete();
        EnvCon = null;
        PathToHandle.Clear();
        Debug.Log("PhxEnvironment destroyed");
    }

    public PhxScene GetScene()
    {
        return RTScene;
    }

    /// <summary>
    /// Names of every texture in the mounted levels, optionally filtered to
    /// those containing one of <paramref name="substrings"/> (case-insensitive).
    /// </summary>
    /// <remarks>
    /// The HUD wants the original game's artwork, but the texture names are
    /// data, not documentation - guessing them one at a time across test runs
    /// is slow and unreliable. This lets the HUD report what is actually
    /// available once, so the real names can be used directly.
    /// </remarks>
    public List<string> GetLoadedTextureNames(params string[] substrings)
    {
        List<string> names = new List<string>();
        foreach (LVL lvl in Loaded)
        {
            if (lvl.Level == null) continue;

            LibSWBF2.Wrappers.Texture[] textures;
            try
            {
                textures = lvl.Level.Get<LibSWBF2.Wrappers.Texture>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not enumerate textures of '{lvl.DisplayPath}': {e.Message}");
                continue;
            }
            if (textures == null) continue;

            foreach (LibSWBF2.Wrappers.Texture tex in textures)
            {
                string texName = tex?.Name;
                if (string.IsNullOrEmpty(texName)) continue;

                if (substrings != null && substrings.Length > 0)
                {
                    bool matched = false;
                    for (int i = 0; i < substrings.Length && !matched; ++i)
                    {
                        matched = texName.IndexOf(substrings[i], StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    if (!matched) continue;
                }

                names.Add(texName);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public PhxMatch GetMatch()
    {
        return Match;
    }

    public PhxTimerDB GetTimerDB()
    {
        return Timers;
    }

    public static PhxEnvironment Create(PhxPath lvlGameDataPath, PhxPath lvlAddonDataPath = null, bool initMatch=true)
    {
        if (!lvlGameDataPath.Exists())
        {
            Debug.LogError($"Given environment path '{lvlGameDataPath}' doesn't exist!");
            return null;
        }

        bool bIsAddon = lvlAddonDataPath != null;
        if (bIsAddon && !lvlAddonDataPath.Exists())
        {
            Debug.LogError($"Given environment path '{lvlGameDataPath}' doesn't exist!");
            return null;
        }

        PhxAnimationLoader.ClearDB();
        PhxLuaEvents.Clear();

        PhxEnvironment rt = new PhxEnvironment(lvlGameDataPath, lvlAddonDataPath);
        rt.ScheduleRelFallback("core.lvl");
        rt.ScheduleRelFallback("shell.lvl");
        rt.ScheduleRelFallback("common.lvl");
        rt.ScheduleRelFallback("mission.lvl", null, alsoStock: true);
        rt.ScheduleRel("sound/common.bnk");

        // Sound lives in per-area banks under sound/, and only common.bnk was
        // ever mounted - so every lookup outside it failed ("failed to find
        // sound queried with: 0x790E02B5"), taking vehicle engines, weapon
        // reports and music with it. The per-map bank is mounted in Run() once
        // the map is known.
        //
        // sound/global.lvl is deliberately NOT mounted: parsing it crashes
        // LibSWBF2 outright - the process dies inside Level::FromFile with no
        // catchable exception, so there is no way to guard it from here. It is
        // the only bank that does this; Tools/TexRepro confirms the other 20
        // (including every per-planet bank) parse cleanly. Restore this line
        // once the parser bug is fixed.

        // TODO: Remove
        //rt.ScheduleAbs(rt.AddonDataPath / "ingame.lvl");

        rt.RTScene = new PhxScene(rt, rt.EnvCon);
        rt.Match = initMatch ? new PhxMatch() : null;
        rt.Timers = new PhxTimerDB();

        PhxAnimationLoader.Con = rt.EnvCon;

        return rt;
    }

    public PhxLuaRuntime GetLuaRuntime()
    {
        return LuaRT;
    }

    public T Find<T>(string name) where T : NativeWrapper, new()
    {
        return EnvCon.Get<T>(name);
    }

    /// <summary>
    /// A config chunk (effect, combo, music, sound, ...) from the mounted
    /// levels, or null. Exposed because the container itself is private and
    /// runtime systems outside the importers need to read their own chunk
    /// types - the alternative is each of them holding its own Container
    /// reference and going stale on map change.
    /// </summary>
    public Config FindConfig(EConfigType type, string name)
    {
        if (EnvCon == null || string.IsNullOrEmpty(name)) return null;
        return EnvCon.FindConfig(type, name);
    }

    public bool Execute(string scriptName)
    {
        Debug.Assert(CanExecute);
        
        Script script = EnvCon.Get<Script>(scriptName);
        if (script == null)
        {
            script = EnvCon.Get<Script>(scriptName.ToLower());
        }

        if (script == null)
        {
            Debug.LogError($"Couldn't find script '{scriptName}'!");
            return false;
        }

        if (!script.IsValid())
        {
            Debug.LogError($"Script '{scriptName}' found but invalid!");
            return false;
        }

        return Execute(script);
    }

    public bool Execute(Script script)
    {
        Debug.Assert(CanExecute);
        if (script == null || !script.IsValid())
        {
            Debug.LogError($"Given script '{script.Name}' is NULl or invalid!");
            return false;
        }

        if (!script.GetData(out IntPtr luaBin, out uint size))
        {
            Debug.LogError($"Couldn't grab lua binary code from script '{script.Name}'!");
            return false;
        }

        // Still have no idea why missionlist fails on Linux/Mac.  I'll have to dig into the compilation
        // warnings produced when compiling the Lua lib.
        if (script.Name == "missionlist" && PhxGame.Instance.MissionListPath != "")
        {
            return LuaRT.ExecuteFile(PhxGame.Instance.MissionListPath);
        }
        else 
        {
            return LuaRT.Execute(luaBin, size, script.Name);
        }
    }

    public void Run(string initScript, string initFn = null, string postLoadFn = null)
    {
        Debug.Assert(Stage == EnvStage.Init);
        Loader.ResetAllLoaders();

        InitScriptName = initScript;
        InitFunctionName = initFn;
        PostLoadFunctionName = postLoadFn;

        // Map scripts are named <3-letter planet><era><mode>, e.g. geo1c_con,
        // and their sound bank is sound/<planet>.lvl - sound/geo.lvl here.
        // Non-map scripts such as "missionlist" have no bank, which
        // ScheduleSoundBank treats as normal rather than as an error.
        if (!string.IsNullOrEmpty(initScript) && initScript.Length >= 3)
        {
            ScheduleSoundBank(initScript.Substring(0, 3));
        }

        LoadscreenHandle = ScheduleRel(GetLoadscreenPath());
        EnvCon.LoadLevels();

        Stage = EnvStage.LoadingBase;
    }

    public float GetLoadingProgress()
    {
        float stageContribution = 1.0f / 2.0f;
        float stageProgress = Stage >= EnvStage.LoadingWorld ? stageContribution : 0.0f;

        return stageProgress + stageContribution * EnvCon.GetOverallProgress();
    }

    /// <summary>
    /// Name of the mission script this environment is running (e.g.
    /// "geo1c_con"), which is what scripts mean by the world filename.
    /// </summary>
    public string GetWorldName()
    {
        return InitScriptName ?? "";
    }

    public Level GetWorldLevel()
    {
        return WorldLevel;
    }

    public SWBF2Handle ScheduleAbs(PhxPath absPath, string[] subLVLs = null)
    {
        if (!Schedule(absPath, out SWBF2Handle handle, subLVLs))
        {
            Debug.LogErrorFormat("Couldn't schedule '{0}'! File not found!", absPath);
        }
        return handle;
    }

    public SWBF2Handle ScheduleRel(PhxPath relPath, string[] subLVLs = null, bool bAddon = false)
    {
        if (bAddon && !AddonDataPath.Exists())
        {
            Debug.LogError($"ScheduleRel '{relPath}' from Addon, but AddonDataPath is NULL!");
            return new SWBF2Handle(ushort.MaxValue);
        }

        // Relative paths are always lower case in consideration of Unix file systems.
        // Also see PhxGame::Awake()
        relPath = relPath.ToString().ToLower();
        PhxPath dataPath = bAddon ? AddonDataPath : GameDataPath;

        SWBF2Handle handle;
        if (Schedule(dataPath / relPath, out handle, subLVLs))
        {
            return handle;
        }

        // The requested path does not exist. Mod scripts routinely ask for
        // files by a path their own package does not ship - the BF3 Legacy
        // scripts request addon/bf3/data/_lvl_pc/core/shell.lvl, and that Core
        // folder exists but holds no shell.lvl, while stock ships one at
        // data/_lvl_pc/shell.lvl and sibling addons ship their own. The real
        // game resolves this because its data is a single merged tree; we have
        // to search for it. Retry by file name against the addon root and then
        // stock, which is the same precedence ScheduleRelFallback already uses.
        if (ScheduleByFileName(relPath, subLVLs, out handle))
        {
            return handle;
        }

        Debug.LogError($"Couldn't schedule '{relPath}'! File not found!");
        return handle;
    }

    /// <summary>
    /// Mount the sound bank sound/&lt;name&gt;.lvl if it exists, addon copy first.
    ///
    /// Absence is not an error: only some names have a bank (there is no
    /// sound/mis.lvl for "missionlist"), and mods routinely ship a subset. A
    /// missing bank is reported at Log level so a genuinely silent map can
    /// still be traced, without filling the console on every load.
    /// </summary>
    public void ScheduleSoundBank(string name)
    {
        if (string.IsNullOrEmpty(name)) return;

        PhxPath relPath = new PhxPath("sound") / (name.ToLower() + ".lvl");

        if (AddonDataPath != null)
        {
            PhxPath addonBank = AddonDataPath / relPath;
            if (addonBank.Exists() && addonBank.IsFile())
            {
                ScheduleRel(relPath, null, true);
                return;
            }
        }

        PhxPath stockBank = GameDataPath / relPath;
        if (stockBank.Exists() && stockBank.IsFile())
        {
            ScheduleRel(relPath, null, false);
            return;
        }

        Debug.Log($"No sound bank '{relPath}' - sounds for '{name}' will fall back to the shared banks.");
    }

    /// <summary>
    /// Last-resort lookup for a level by its file name alone, ignoring the
    /// directory the caller asked for. Addon data wins over stock, matching
    /// <see cref="ScheduleRelFallback"/>. Returns false if no copy exists.
    /// </summary>
    bool ScheduleByFileName(PhxPath relPath, string[] subLVLs, out SWBF2Handle handle)
    {
        handle = new SWBF2Handle(ushort.MaxValue);

        string fileName = relPath.ToString().Replace('\\', '/');
        int slash = fileName.LastIndexOf('/');
        if (slash >= 0)
        {
            fileName = fileName.Substring(slash + 1);
        }
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        // Only the two data roots are searched - both already point at a
        // _lvl_pc directory. Subdirectories are deliberately NOT searched:
        // sound/shell.lvl is a sound bank that shares its name with the shell
        // UI level, so a recursive search would happily resolve a request for
        // the UI to a bank of sound effects. Failing loudly beats loading the
        // wrong file under the right name.
        List<PhxPath> candidates = new List<PhxPath>();
        if (AddonDataPath != null)
        {
            candidates.Add(AddonDataPath / fileName);
        }
        candidates.Add(GameDataPath / fileName);

        for (int i = 0; i < candidates.Count; ++i)
        {
            PhxPath candidate = candidates[i];
            if (!candidate.Exists() || !candidate.IsFile())
            {
                continue;
            }

            if (Schedule(candidate, out handle, subLVLs))
            {
                Debug.Log($"'{relPath}' not found; resolved to '{candidate}' by file name.");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Addon copy if present, otherwise stock. With <paramref name="alsoStock"/>,
    /// mount stock alongside an addon copy too (addon first, so a same-named
    /// addon chunk still wins) - some addons ship a file that only carries
    /// their own subset (e.g. an era-specific addon's mission.lvl has only
    /// that era's scripts), and without the stock copy every other script
    /// sharing that file becomes unreachable: "Couldn't find script '...'".
    /// </summary>
    public SWBF2Handle ScheduleRelFallback(PhxPath relPath, string[] subLVLs = null, bool alsoStock = false)
    {
        relPath = relPath.ToString().ToLower();
        if (AddonDataPath != null)
        {
            PhxPath path = (AddonDataPath / relPath);
            if (path.Exists() && path.IsFile())
            {
                SWBF2Handle addonHandle = ScheduleRel(relPath, subLVLs, true);

                if (alsoStock)
                {
                    PhxPath stockPath = GameDataPath / relPath;
                    if (stockPath.Exists() && stockPath.IsFile())
                    {
                        ScheduleRel(relPath, subLVLs, false);
                    }
                }

                return addonHandle;
            }
        }
        return ScheduleRel(relPath, subLVLs, false);
    }

    public float GetProgress(SWBF2Handle handle)
    {
        return EnvCon.GetProgress(handle);
    }

    public void Tick(float deltaTime)
    {
        // This needs to come first
        if (FirePostLoadEvent)
        {
            OnPostLoad?.Invoke();
            FirePostLoadEvent = false;
        }

        for (int i = 0; i < Loading.Count; ++i)
        {
            ELoadStatus status = EnvCon.GetStatus(Loading[i].Handle);

            if (status == ELoadStatus.Loaded)
            {
                LVL scheduled = Loading[i];
                
                var lvl = EnvCon.GetLevel(Loading[i].Handle);
                Debug.Assert(lvl != null);
                scheduled.Level = lvl;

                if (lvl.IsWorldLevel)
                {
                    if (WorldLevel != null)
                    {
                        Debug.LogErrorFormat("Encounterred another world lvl '{0}' in environment! Previously found world lvl: '{1}'", lvl.Name, WorldLevel.Name);
                    }
                    else
                    {
                        WorldLevel = lvl;
                    }
                }

                // grab lvl localizations, if any
                Localizations.AddRange(lvl.Get<Localization>());
                

                SoundLoader.Instance.InitializeSoundProperties(lvl);


                Loaded.Add(scheduled);
                Loading.RemoveAt(i);
                break; // do not further iterate altered list
            }
            else if (status == ELoadStatus.Failed)
            {
                Debug.LogErrorFormat("Loading '{0}' failed!", Loading[i].DisplayPath);

                Loading.RemoveAt(i);
                break; // do not further iterate altered list
            }    
        }

        if (Stage == EnvStage.LoadingBase)
        {
            if (LoadscreenLVL == null)
            {
                LoadscreenLVL = EnvCon.GetLevel(LoadscreenHandle);
                if (LoadscreenLVL != null)
                {
                    var textures = LoadscreenLVL.Get<LibSWBF2.Wrappers.Texture>();
                    int texIdx = UnityEngine.Random.Range(0, textures.Length - 1);
                    OnLoadscreenLoaded?.Invoke(TextureLoader.Instance.ImportUITexture(textures[texIdx].Name));
                }
            }

            if (EnvCon.IsDone() && Loading.Count == 0)
            {
                Stage = EnvStage.ExecuteMain;
                RunMain();
            }
        }

        if (Stage == EnvStage.LoadingWorld && EnvCon.IsDone() && Loading.Count == 0)
        {
            for (int i = 0; i < Localizations.Count; ++i)
            {
                string locName = Localizations[i].Name;
                if (LocalizationLookup.TryGetValue(locName, out var loc))
                {
                    loc.Add(Localizations[i]);
                }
                else
                {
                    LocalizationLookup.Add(locName, new List<LibSWBF2.Wrappers.Localization> { Localizations[i] });
                }
            }

            // apply queued Lua calls like "AddUnitClass"
            Match?.ApplySchedule();

            Stage = EnvStage.CreateScene;
            CreateScene();
        }

        Timers?.Tick(deltaTime);
        Match?.Tick(deltaTime);
        RTScene.Tick(deltaTime);
    }

    public void TickPhysics(float deltaTime)
    {
        RTScene.TickPhysics(deltaTime);
    }

    public string GetLocalized(string localizedPath, bool bReturnNullIfNotFound=false)
    {
        if (GetLocalized(PhxGame.Instance.Settings.Language, localizedPath, out string localizedUnicode))
        {
            return localizedUnicode;
        }
        if (GetLocalized("english", localizedPath, out localizedUnicode))
        {
            return localizedUnicode;
        }
        return bReturnNullIfNotFound ? null : localizedPath;
    }

    bool GetLocalized(string language, string localizedPath, out string localizedUnicode)
    {
        localizedUnicode = localizedPath;

        List<LibSWBF2.Wrappers.Localization> locs;
        if (!LocalizationLookup.TryGetValue(language, out locs))
        {
            return false;
        }

        for (int i = 0; i < locs.Count; ++i)
        {
            if (locs[i].GetLocalizedWideString(localizedPath, out localizedUnicode))
            {
                return true;
            }
        }
        return false;
    }

    public string GetLocalizedMapName(string mapluafile)
    {
        object[] res = LuaRT.CallLuaFunction("missionlist_GetLocalizedMapName", 2, false, true, mapluafile);
        string mapName = res[0] as string;
        return mapName;
    }

    bool Schedule(PhxPath absPath, out SWBF2Handle handle, string[] subLVLs = null)
    {
        Debug.Assert(CanSchedule);

        if (PathToHandle.TryGetValue(absPath, out handle))
        {
            return true;
        }

        if (absPath.Exists() && absPath.IsFile())
        {
            handle = EnvCon.AddLevel(absPath, subLVLs);

            // Index any cloth this file carries while its path is in hand.
            //
            // CLTH has no handler in LibSWBF2 at all, so it never arrives
            // through EnvCon and has to be read from the file directly. This
            // is the one place every .lvl passes through with an absolute
            // path, which makes it the only place that can. Keyed by owning
            // model, so ModelLoader can look cloth up later without knowing
            // which file it came from.
            BFClothImporter.Scan(absPath.ToString());

            Loading.Add(new LVL
            {
                Handle = handle,
                DisplayPath = absPath.GetLeaf(2),
                bIsAddon = absPath.Contains("/addon/")
            });

            PathToHandle.Add(absPath, handle);
            return true;
        }

        handle = new SWBF2Handle(ushort.MaxValue);
        return false;
    }

    PhxPath GetLoadscreenPath()
    {
        // First, try grab loadscreen for standard maps
        if (!string.IsNullOrEmpty(InitScriptName))
        {
            PhxPath loadscreenLVL = new PhxPath("load") / (InitScriptName.Substring(0, 4) + ".lvl");
            if ((GameDataPath / loadscreenLVL).Exists() || (AddonDataPath != null && (AddonDataPath / loadscreenLVL).Exists()))
            {
                return loadscreenLVL;
            } 
        }
        return "load/common.lvl";
    }

    void RunMain()
    {
        Debug.Assert(Stage == EnvStage.ExecuteMain);

        // 1 - execute the main script
        if (!string.IsNullOrEmpty(InitScriptName) && !Execute(InitScriptName))
        {
            Debug.LogErrorFormat("Executing lua main script '{0}' failed!", InitScriptName);
            return;
        }
        OnExecuteMain?.Invoke();

        // 1b - ScriptPreInit, if the script defines one.
        //
        // BF2 calls three entry points, not two. Space mission scripts
        // (spa1g_c, spa1g_ass, ...) do a meaningful part of their setup in
        // ScriptPreInit - it was never invoked, so those maps reached
        // ScriptInit already half-configured. Optional by design: most ground
        // maps don't define it, and a missing function is not an error here.
        // Probed rather than called-and-let-fail: the call path reports a
        // missing function as an error, which is wrong for an entry point
        // that is optional by design.
        if (LuaRT.CallLuaFunctionIfPresent("ScriptPreInit", 0))
        {
            Debug.Log("[Lua] ScriptPreInit executed.");
        }

        // 2 - execute the main function -> will call ReadDataFile multiple times
        if (!string.IsNullOrEmpty(InitFunctionName) && LuaRT.CallLuaFunction(InitFunctionName, 0) == null)
        {
            Debug.LogErrorFormat("Executing lua main function '{0}' failed!", InitFunctionName);
            return;
        }

        // 3 - load the (via ReadDataFile) scheduled lvl files
        EnvCon.LoadLevels();
        Stage = EnvStage.LoadingWorld;
    }

    void CreateScene()
    {
        Debug.Assert(Stage == EnvStage.CreateScene);

        // WorldLevel will be null for Main Menu
        if (WorldLevel != null)
        {
            RTScene.Import(WorldLevel.Get<LibSWBF2.Wrappers.World>());
        }

        // 4 - execute post load function AFTER scene has been created
        if (!string.IsNullOrEmpty(PostLoadFunctionName) && LuaRT.CallLuaFunction(PostLoadFunctionName, 0) == null)
        {
            Debug.LogErrorFormat("Executing lua post load function '{0}' failed!", PostLoadFunctionName);
        }

        Stage = EnvStage.Loaded;
        OnLoaded?.Invoke();

        FirePostLoadEvent = true;
    }
}