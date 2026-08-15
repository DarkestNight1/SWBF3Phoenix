using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class PhxMainMenu : PhxMenuInterface
{
    static PhxEnvironment ENV { get { return PhxGame.GetEnvironment(); } }
    static PhxLuaRuntime RT { get { return PhxGame.GetLuaRuntime(); } }

    struct SubIcon
    {
        public string Sub;
        public Texture2D Icon;
    }

    [Header("References")]
    public PhxListBox LstMaps;
    public PhxListBox LstModes;
    public PhxListBox LstEras;
    public PhxListBox LstRotation;
    public Button BtnAdd;
    public Button BtnRemove;
    public Button BtnRemoveAll;
    public Button BtnStart;
    public Button BtnQuit;

    List<string>  MapLuaFiles = new List<string>();
    List<SubIcon> ModeSubs = new List<SubIcon>();
    List<SubIcon> EraSubs = new List<SubIcon>();
    List<string>  RotationLuaFiles = new List<string>();

    // Raw mission list entry per map. Stock 'missionlist_ExpandModelist' only
    // reports modes and eras the stock shell knows about, so mod-defined ones
    // (BF3 Legacy's Orbital Assault, its two BF3 eras) are dropped and the map
    // shows up unplayable. Keeping the entry lets us read its flags directly.
    List<PhxLuaRuntime.Table> MapEntries = new List<PhxLuaRuntime.Table>();

    // These are just for convenience, so the user doesn't
    // have to re-check his last checked modes and eras
    HashSet<string> LastCheckedModes = new HashSet<string>();
    HashSet<string> LastCheckedEras = new HashSet<string>();


    public override void Clear()
    {

    }

    void OnMapSelectionChanged(int newIdx)
    {
        string mapluafile = MapLuaFiles[newIdx];

        LstModes.Clear();
        LstEras.Clear();
        ModeSubs.Clear();
        EraSubs.Clear();

        HashSet<string> stockModeKeys = new HashSet<string>();
        HashSet<string> stockEraKeys = new HashSet<string>();

        object[] res = RT.CallLuaFunction("missionlist_ExpandModelist", 1, mapluafile);
        PhxLuaRuntime.Table modes = res != null && res.Length > 0 ? res[0] as PhxLuaRuntime.Table : null;
        if (modes != null)
        {
            foreach (KeyValuePair<object, object> entry in modes)
            {
                PhxLuaRuntime.Table mode = entry.Value as PhxLuaRuntime.Table;
                string modeNamePath = mode.Get<string>("showstr");
                if (mode.Get("bIsWildcard") == null)
                {
                    Texture2D icon = TextureLoader.Instance.ImportUITexture(mode.Get<string>("icon"));
                    string key = mode.Get<string>("key");
                    stockModeKeys.Add(key);

                    AddModeItem(ENV.GetLocalized(modeNamePath), key, mode.Get<string>("subst"), icon);
                }
            }
        }

        res = RT.CallLuaFunction("missionlist_ExpandEralist", 1, mapluafile);
        PhxLuaRuntime.Table eras = res != null && res.Length > 0 ? res[0] as PhxLuaRuntime.Table : null;
        if (eras != null)
        {
            foreach (KeyValuePair<object, object> entry in eras)
            {
                PhxLuaRuntime.Table era = entry.Value as PhxLuaRuntime.Table;
                string eraNamePath = era.Get<string>("showstr");
                if (era.Get("bIsWildcard") == null)
                {
                    Texture2D icon = TextureLoader.Instance.ImportUITexture(era.Get<string>("icon2"));
                    string key = era.Get<string>("key");
                    stockEraKeys.Add(key);

                    AddEraItem(ENV.GetLocalized(eraNamePath), key, era.Get<string>("subst"), icon);
                }
            }
        }

        AddModdedSubs(newIdx, stockModeKeys, stockEraKeys);
    }

    /// <summary>
    /// Fill in modes and eras the map declares but the stock shell doesn't
    /// recognise. Without this every BF3 Legacy map lists zero playable
    /// combinations, because its eras ('x', 'y') and its Orbital Assault mode
    /// are defined by the mod, not by Battlefront II.
    /// </summary>
    void AddModdedSubs(int mapIdx, HashSet<string> stockModeKeys, HashSet<string> stockEraKeys)
    {
        if (mapIdx < 0 || mapIdx >= MapEntries.Count) return;

        PhxLuaRuntime.Table mapEntry = MapEntries[mapIdx];

        foreach (PhxBF3LegacyContent.PhxBF3ModeInfo mode in PhxBF3LegacyContent.ExpandModes(mapEntry))
        {
            if (stockModeKeys.Contains(mode.Key) || HasSub(ModeSubs, mode.Subst)) continue;
            AddModeItem(mode.DisplayName, mode.Key, mode.Subst, LoadIcon(mode.Icon));
        }

        foreach (PhxBF3LegacyContent.PhxBF3ModeInfo era in PhxBF3LegacyContent.ExpandEras(mapEntry))
        {
            if (stockEraKeys.Contains(era.Key) || HasSub(EraSubs, era.Subst)) continue;
            AddEraItem(era.DisplayName, era.Key, era.Subst, LoadIcon(era.Icon));
        }
    }

    // The stock expansion keys entries the same way we do, but that is a
    // convention rather than a guarantee - matching on what actually gets
    // substituted into the map script name catches a duplicate either way.
    static bool HasSub(List<SubIcon> subs, string sub)
    {
        foreach (SubIcon s in subs)
        {
            if (string.Equals(s.Sub, sub, System.StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // A mod-declared mode may name no icon at all, and a named one may not
    // exist in any loaded lvl - either way the item just shows without one.
    static Texture2D LoadIcon(string name)
    {
        return string.IsNullOrEmpty(name) ? null : TextureLoader.Instance.ImportUITexture(name);
    }

    void AddModeItem(string label, string key, string subst, Texture2D icon)
    {
        PhxListBoxItem item = LstModes.AddItem(label);
        item.SetIcon(icon);
        item.SetChecked(LastCheckedModes.Contains(key));
        item.OnCheckChanged += (bool check) =>
        {
            if (check)
            {
                LastCheckedModes.Add(key);
            }
            else
            {
                LastCheckedModes.Remove(key);
            }
        };

        ModeSubs.Add(new SubIcon { Sub = subst, Icon = icon });
    }

    void AddEraItem(string label, string key, string subst, Texture2D icon)
    {
        PhxListBoxItem item = LstEras.AddItem(label);
        item.SetIcon(icon);
        item.SetChecked(LastCheckedEras.Contains(key));
        item.OnCheckChanged += (bool check) =>
        {
            if (check)
            {
                LastCheckedEras.Add(key);
            }
            else
            {
                LastCheckedEras.Remove(key);
            }
        };

        EraSubs.Add(new SubIcon { Sub = subst, Icon = icon });
    }

    void AddMap()
    {
        if (LstMaps.CurrentSelection < 0)
        {
            return;
        }

        string mapluafile = MapLuaFiles[LstMaps.CurrentSelection];
        int[] modeIndices = LstModes.GetCheckedIndices();
        int[] eraIndices  = LstEras.GetCheckedIndices();

        for (int i = 0; i < modeIndices.Length; ++i)
        {
            SubIcon modeSub = ModeSubs[modeIndices[i]];

            for (int j = 0; j < eraIndices.Length; j++)
            {
                SubIcon eraSub = EraSubs[eraIndices[j]];

                string mapName = ENV.GetLocalizedMapName(mapluafile);
                PhxListBoxItem item = LstRotation.AddItem(mapName);
                item.SetIcon(modeSub.Icon);
                item.SetIcon2(eraSub.Icon);

                string mapScript = PhxHelpers.Format(mapluafile, eraSub.Sub, modeSub.Sub);
                RotationLuaFiles.Add(mapScript);
            }
        }
    }

    void RemoveSelectionFromRotation()
    {
        // TODO
    }

    void ClearRotation()
    {
        RotationLuaFiles.Clear();
        LstRotation.Clear();
    }

    void StartRotation()
    {
        PhxGame.Instance.AddToMapRotation(RotationLuaFiles);
        PhxGame.Instance.NextMap();
    }

    void Quit()
    {
        Application.Quit();
    }

    void Start()
    {
        Debug.Assert(LstMaps      != null);
        Debug.Assert(LstModes     != null);
        Debug.Assert(LstEras      != null);
        Debug.Assert(LstRotation  != null);
        Debug.Assert(BtnAdd       != null);
        Debug.Assert(BtnRemove    != null);
        Debug.Assert(BtnRemoveAll != null);
        Debug.Assert(BtnStart     != null);
        Debug.Assert(BtnQuit      != null);

        LstMaps.OnSelect += OnMapSelectionChanged;

        BtnAdd.onClick.AddListener(AddMap);
        BtnRemove.onClick.AddListener(RemoveSelectionFromRotation);
        BtnRemoveAll.onClick.AddListener(ClearRotation);
        BtnStart.onClick.AddListener(StartRotation);
        BtnQuit.onClick.AddListener(Quit);

        // The shell's data - including any addon core.lvl - is mounted by now,
        // so a mod that ships its own names for the eras and modes it invented
        // gets to use them instead of our fallbacks.
        PhxConversionPackContent.RefreshLocalizedNames();

        bool bForMP = false;
        RT.CallLuaFunction("missionlist_ExpandMaplist", 0, bForMP);
        PhxLuaRuntime.Table spMissions = RT.GetTable("missionselect_listbox_contents");

        foreach (KeyValuePair<object, object> entry in spMissions)
        {
            PhxLuaRuntime.Table map = entry.Value as PhxLuaRuntime.Table;
            string mapluafile = map.Get<string>("mapluafile");
            bool bIsModLevel  = map.Get<bool>("isModLevel");
            string mapName    = ENV.GetLocalizedMapName(mapluafile);

            // Mod maps often ship no localized name; fall back to what the mod
            // content tables know rather than showing a raw key.
            if (string.IsNullOrEmpty(mapName))
            {
                PhxBF3LegacyContent.PhxBF3MapInfo info = PhxBF3LegacyContent.GetMapInfo(mapluafile);
                mapName = info != null ? info.DisplayName
                                       : (PhxConversionPackContent.GetMapDisplayName(mapluafile) ?? mapluafile);
            }

            LstMaps.AddItem(mapName, bIsModLevel);
            MapLuaFiles.Add(mapluafile);
            MapEntries.Add(map);
        }
    }
}
