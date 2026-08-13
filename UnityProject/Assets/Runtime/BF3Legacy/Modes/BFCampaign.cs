using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Campaign progress, save slots and unlockables.
/// </summary>
/// <remarks>
/// The campaign itself is not implemented here, and deliberately so. BF2 ships
/// its whole campaign in Lua - <c>ifs_campaign_menu</c>, <c>campaign_data</c>,
/// the briefing and battle-card screens, the mission sequence - and Phoenix
/// already runs that Lua. What the game expects from the engine underneath is a
/// small set of callbacks: remember how far the player has got, save and load
/// named campaign states, track unlockables, and enter a mission.
///
/// None of those sixteen callbacks existed, which is why the campaign has never
/// been reachable. This is the state they read and write; the callbacks
/// themselves live in PhxLuaAPI and forward here.
///
/// Nothing in Phoenix routes the player into the campaign shell yet, so in
/// practice this stays dormant: the callbacks only run when campaign Lua calls
/// them. They are implemented unconditionally rather than gated, because a
/// missing callback logs an error and can leave the shell script wedged - a
/// callback that answers correctly and is never called is the safer inert
/// state.
/// </remarks>
public static class BFCampaign
{
    // ------------------------------------------------------------------ data

    /// <summary>
    /// One progress or unlockable entry.
    /// </summary>
    /// <remarks>
    /// A list of pairs rather than a Dictionary because JsonUtility - which is
    /// what the rest of the BF3 Legacy config uses - cannot serialize
    /// dictionaries. Lookup volume here is a few dozen entries at menu
    /// transitions, so the linear scan costs nothing worth avoiding.
    /// </remarks>
    [Serializable]
    public class BFEntry
    {
        public string Key;
        public float Value;
    }

    [Serializable]
    public class BFCampaignSave
    {
        /// <summary>Slot name, as shown by ScriptCB_GetSavedCampaignList.</summary>
        public string Name = "";

        /// <summary>Single-player progress, keyed the way the script keys it.</summary>
        public List<BFEntry> Progress = new List<BFEntry>();

        /// <summary>Unlockables the player has earned.</summary>
        public List<string> Unlocked = new List<string>();

        /// <summary>Whether the player is inside the tutorial.</summary>
        public bool InTrainingMission;

        /// <summary>Mission names the shell last set, in order.</summary>
        public List<string> MissionNames = new List<string>();

        /// <summary>Queued mission setup, as saved by ScriptCB_SaveMissionSetup.</summary>
        public List<string> MissionQueue = new List<string>();

        /// <summary>Unix seconds, so a save list can be ordered by recency.</summary>
        public long SavedAtUtc;
    }

    [Serializable]
    class BFCampaignFile
    {
        public BFCampaignSave Current = new BFCampaignSave();
        public List<BFCampaignSave> Slots = new List<BFCampaignSave>();
    }

    /// <summary>
    /// How many missions the shell may queue. Stock asks via
    /// ScriptCB_GetMaxMissionQueue and sizes its own UI from the answer.
    /// </summary>
    public const int MaxMissionQueue = 16;

    static BFCampaignFile File_;

    static BFCampaignFile Data
    {
        get
        {
            if (File_ == null) Load();
            return File_;
        }
    }

    /// <summary>The state being played right now.</summary>
    public static BFCampaignSave Current => Data.Current;

    // ----------------------------------------------------------- persistence

    const string FileName = "bf3campaign.json";

    /// <summary>
    /// Beside the BF3 Legacy config, so a self-contained install keeps its
    /// campaign progress inside the folder you can delete to uninstall.
    /// </summary>
    public static string SavePath
    {
        get
        {
            string dir = Path.GetDirectoryName(PhxBF3.ConfigPath);
            return string.IsNullOrEmpty(dir)
                ? Path.Combine(Application.persistentDataPath, FileName)
                : Path.Combine(dir, FileName);
        }
    }

    public static void Load()
    {
        File_ = new BFCampaignFile();

        try
        {
            string path = SavePath;
            if (!System.IO.File.Exists(path)) return;

            BFCampaignFile loaded = JsonUtility.FromJson<BFCampaignFile>(
                System.IO.File.ReadAllText(path));

            // A truncated or hand-edited file deserializes to something with
            // null members rather than throwing, so check before trusting it.
            if (loaded != null && loaded.Current != null)
            {
                if (loaded.Slots == null) loaded.Slots = new List<BFCampaignSave>();
                File_ = loaded;
            }
        }
        catch (Exception e)
        {
            // Losing campaign progress is bad; refusing to start because of it
            // is worse. Report and continue from empty.
            Debug.LogWarning($"[BFCampaign] Could not read '{SavePath}': {e.Message}. " +
                             "Starting from empty campaign progress.");
            File_ = new BFCampaignFile();
        }
    }

    public static void Save()
    {
        try
        {
            System.IO.File.WriteAllText(SavePath, JsonUtility.ToJson(Data, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFCampaign] Could not write '{SavePath}': {e.Message}");
        }
    }

    // -------------------------------------------------------------- progress

    /// <summary>
    /// Progress value for a key, or 0 when the player has not reached it.
    /// </summary>
    public static float GetProgress(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0f;

        List<BFEntry> list = Current.Progress;
        for (int i = 0; i < list.Count; ++i)
        {
            if (string.Equals(list[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return list[i].Value;
            }
        }
        return 0f;
    }

    /// <summary>
    /// Record progress. Never moves backwards: the campaign scripts set
    /// progress on entering a mission as well as on finishing it, so taking the
    /// lower of two writes would un-complete a mission the player replayed.
    /// </summary>
    public static void SetProgress(string key, float value)
    {
        if (string.IsNullOrEmpty(key)) return;

        List<BFEntry> list = Current.Progress;
        for (int i = 0; i < list.Count; ++i)
        {
            if (string.Equals(list[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (value > list[i].Value) list[i].Value = value;
                return;
            }
        }
        list.Add(new BFEntry { Key = key, Value = value });
    }

    // ----------------------------------------------------------- unlockables

    public static bool IsUnlocked(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        List<string> list = Current.Unlocked;
        for (int i = 0; i < list.Count; ++i)
        {
            if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static void Unlock(string name)
    {
        if (string.IsNullOrEmpty(name) || IsUnlocked(name)) return;

        Current.Unlocked.Add(name);
        Debug.Log($"[BFCampaign] Unlocked '{name}'.");
    }

    // ------------------------------------------------------------ save slots

    /// <summary>Names of every saved campaign, most recent first.</summary>
    public static List<string> GetSavedNames()
    {
        var names = new List<string>();
        List<BFCampaignSave> slots = Data.Slots;

        // Copy before sorting: callers get a list they can hold, and the stored
        // order stays whatever it was.
        var ordered = new List<BFCampaignSave>(slots);
        ordered.Sort((a, b) => b.SavedAtUtc.CompareTo(a.SavedAtUtc));

        for (int i = 0; i < ordered.Count; ++i) names.Add(ordered[i].Name);
        return names;
    }

    public static bool HasSavedState => Data.Slots.Count > 0;

    /// <summary>
    /// Copy the live state into a named slot, replacing one of that name.
    /// </summary>
    public static void SaveState(string name)
    {
        if (string.IsNullOrEmpty(name)) name = "campaign";

        BFCampaignSave copy = Clone(Current);
        copy.Name = name;
        copy.SavedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        List<BFCampaignSave> slots = Data.Slots;
        for (int i = 0; i < slots.Count; ++i)
        {
            if (string.Equals(slots[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                slots[i] = copy;
                Save();
                return;
            }
        }

        slots.Add(copy);
        Save();
    }

    /// <summary>Make a named slot live. False when there is no such slot.</summary>
    public static bool LoadState(string name)
    {
        List<BFCampaignSave> slots = Data.Slots;

        // No name means "the most recent one", which is what a Continue button
        // wants and what the stock shell asks for after a crash.
        if (string.IsNullOrEmpty(name))
        {
            if (slots.Count == 0) return false;

            BFCampaignSave newest = slots[0];
            for (int i = 1; i < slots.Count; ++i)
            {
                if (slots[i].SavedAtUtc > newest.SavedAtUtc) newest = slots[i];
            }
            Data.Current = Clone(newest);
            return true;
        }

        for (int i = 0; i < slots.Count; ++i)
        {
            if (string.Equals(slots[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Data.Current = Clone(slots[i]);
                return true;
            }
        }
        return false;
    }

    /// <summary>Wipe the live state. Saved slots are left alone.</summary>
    public static void ClearState()
    {
        Data.Current = new BFCampaignSave();
        Save();
    }

    static BFCampaignSave Clone(BFCampaignSave src)
    {
        // Round-tripping through JsonUtility is how the rest of this project
        // copies serializable state, and it cannot share a list by reference
        // the way a field-by-field copy invites.
        return JsonUtility.FromJson<BFCampaignSave>(JsonUtility.ToJson(src));
    }

    // --------------------------------------------------------- mission setup

    public static void ClearMissionSetup()
    {
        Current.MissionQueue.Clear();
    }

    public static void QueueMission(string mapLuaFile)
    {
        if (string.IsNullOrEmpty(mapLuaFile)) return;
        if (Current.MissionQueue.Count >= MaxMissionQueue)
        {
            Debug.LogWarning($"[BFCampaign] Mission queue is full ({MaxMissionQueue}); " +
                             $"'{mapLuaFile}' not queued.");
            return;
        }
        Current.MissionQueue.Add(mapLuaFile);
    }

    public static void SetMissionNames(IEnumerable<string> names)
    {
        Current.MissionNames.Clear();
        if (names == null) return;
        foreach (string n in names)
        {
            if (!string.IsNullOrEmpty(n)) Current.MissionNames.Add(n);
        }
    }

    // -------------------------------------------------------------- training

    public static bool InTrainingMission
    {
        get => Current.InTrainingMission;
        set => Current.InTrainingMission = value;
    }

    // ------------------------------------------------------- mission sequence

    /// <summary>
    /// Campaign missions available from the loaded mission list.
    /// </summary>
    /// <remarks>
    /// Derived from the game's own data rather than a hardcoded table, so an
    /// addon campaign works without changes here. BF2 names campaign scripts
    /// <c>&lt;planet&gt;&lt;n&gt;&lt;era&gt;_c</c> - yav1g_c, cor1c_c, kam1c_c -
    /// where the trailing <c>_c</c> is the mode, exactly as <c>_con</c> is
    /// conquest and <c>_ctf</c> is capture the flag.
    /// </remarks>
    public static bool IsCampaignScript(string mapLuaFile)
    {
        return !string.IsNullOrEmpty(mapLuaFile) &&
               mapLuaFile.EndsWith("_c", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a mission counts as finished, by the progress the scripts wrote.
    /// </summary>
    public static bool IsComplete(string mapLuaFile)
    {
        return !string.IsNullOrEmpty(mapLuaFile) && GetProgress(mapLuaFile) > 0f;
    }

    /// <summary>
    /// Mark a mission complete. Called when a campaign match is won, so the
    /// next one unlocks.
    /// </summary>
    public static void MarkComplete(string mapLuaFile)
    {
        if (string.IsNullOrEmpty(mapLuaFile)) return;

        SetProgress(mapLuaFile, 1f);
        Save();
        Debug.Log($"[BFCampaign] Mission '{mapLuaFile}' complete.");
    }
}
