using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Campaign mission sequencing.
/// </summary>
/// <remarks>
/// <b>Structural scaffolding. Inert until <see cref="Enabled"/> is set, which
/// nothing does yet.</b>
///
/// Unlike Galactic Conquest, campaign needs very little new gameplay: the
/// stock campaign maps (<c>yav1g_c</c>, <c>cor1c_c</c>, <c>kam1c_c</c> and the
/// rest) already contain their objectives, their scripted events and their
/// voice-over cues, and Phoenix already runs mission Lua. What is missing is
/// the layer above - which mission comes next, what the player has unlocked,
/// and how a mission reports that it ended in success rather than simply
/// ending.
///
/// So this models the sequence and the completion record, and deliberately
/// stops short of the two things that would change runtime behaviour: loading
/// the next map, and rendering the briefing between them.
/// </remarks>
public static class BFCampaign
{
    /// <summary>While false, every entry point here is a no-op.</summary>
    public static bool Enabled { get; private set; }

    public sealed class BFMission
    {
        /// <summary>Map script, e.g. "yav1g_c".</summary>
        public string MapLuaFile;

        /// <summary>Shown on the briefing screen. A localisation key.</summary>
        public string TitleKey;

        /// <summary>Missions that must be complete before this unlocks.</summary>
        public readonly List<string> Prerequisites = new List<string>();

        /// <summary>Era this mission belongs to - clone wars or civil war.</summary>
        public string Era;
    }

    public sealed class BFCampaignProgress
    {
        /// <summary>MapLuaFile of every mission finished successfully.</summary>
        public readonly HashSet<string> Completed = new HashSet<string>();

        /// <summary>The mission currently loaded, if any.</summary>
        public string Active;
    }

    static readonly List<BFMission> Missions = new List<BFMission>();

    public static BFCampaignProgress Progress { get; private set; } = new BFCampaignProgress();

    public static IReadOnlyList<BFMission> AllMissions => Missions;

    public static void Reset()
    {
        Missions.Clear();
        Progress = new BFCampaignProgress();
    }

    /// <summary>
    /// Register the mission list. Intended to be filled from the game's own
    /// mission data rather than hardcoded, so addon campaigns work too.
    /// </summary>
    public static void Define(IEnumerable<BFMission> missions)
    {
        Missions.Clear();
        if (missions == null) return;
        Missions.AddRange(missions);
    }

    /// <summary>
    /// Missions the player can start now: not already done, and with every
    /// prerequisite met.
    /// </summary>
    public static List<BFMission> GetAvailable()
    {
        var available = new List<BFMission>();
        if (!Enabled) return available;

        foreach (BFMission m in Missions)
        {
            if (m == null || string.IsNullOrEmpty(m.MapLuaFile)) continue;
            if (Progress.Completed.Contains(m.MapLuaFile)) continue;

            bool unlocked = true;
            foreach (string prereq in m.Prerequisites)
            {
                if (!Progress.Completed.Contains(prereq)) { unlocked = false; break; }
            }
            if (unlocked) available.Add(m);
        }
        return available;
    }

    /// <summary>
    /// Record that the active mission was completed. The caller decides what
    /// "completed" means - campaign objectives live in the mission's own Lua.
    /// </summary>
    public static void CompleteActive()
    {
        if (!Enabled || string.IsNullOrEmpty(Progress.Active)) return;

        Progress.Completed.Add(Progress.Active);
        Progress.Active = null;
    }

    /// <summary>
    /// The next mission in sequence, or null when the campaign is finished.
    /// </summary>
    public static BFMission GetNext()
    {
        List<BFMission> available = GetAvailable();
        return available.Count > 0 ? available[0] : null;
    }
}
