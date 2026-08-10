using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The mission script's standing orders to the AI.
/// </summary>
/// <remarks>
/// These are not tuning knobs - they are how a map says what kind of battle it
/// is. A campaign level that calls <c>SetAttackingTeam(2)</c> and
/// <c>AllowAISpawn(1, false)</c> is describing a defensive scenario where one
/// side does not reinforce; with both calls empty it plays as a symmetric
/// skirmish, which is a different mission. Flight ceilings keep AI flyers out
/// of the skybox and above the terrain; the notify radius is how far a
/// commandeered vehicle's arrival propagates to nearby AI.
///
/// Every one of these was an empty stub. They are collected here rather than
/// scattered across the systems that read them because they share a lifetime -
/// all set during ScriptInit, all cleared on map change - and because a single
/// place makes it obvious which ones a given map actually used.
/// </remarks>
public static class PhxAIDirectives
{
    /// <summary>Per-team overrides, keyed by 1-based team index.</summary>
    sealed class TeamDirectives
    {
        public bool SpawnAllowed = true;
        public int? Difficulty;
        public float Aggressiveness = 1f;
    }

    static readonly Dictionary<int, TeamDirectives> Teams = new Dictionary<int, TeamDirectives>();
    static readonly HashSet<string> DisabledFlyerPaths =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far a vehicle's presence is announced to AI on foot, in metres.
    /// Drives whether nearby AI notice a transport they could board or a tank
    /// they should avoid. 0 means "not set", and consumers fall back.
    /// </summary>
    public static float VehicleNotifyRadius { get; private set; }

    /// <summary>Team the mission designates as attacker, or 0 for neither.</summary>
    public static int AttackingTeam { get; private set; }

    /// <summary>Flight envelope for AI flyers. Max of 0 means unconstrained.</summary>
    public static float MinFlyHeight { get; private set; }
    public static float MaxFlyHeight { get; private set; }

    /// <summary>Flight envelope applied to the player, which BF2 sets separately.</summary>
    public static float MinPlayerFlyHeight { get; private set; }
    public static float MaxPlayerFlyHeight { get; private set; }

    /// <summary>
    /// A map with dense cover, where AI should prefer shorter sight lines.
    /// </summary>
    public static bool DenseEnvironment { get; private set; }

    /// <summary>Jet troopers may jump without a confirmed landing spot.</summary>
    public static bool AllowBlindJetJumps { get; private set; }

    public static void Reset()
    {
        Teams.Clear();
        DisabledFlyerPaths.Clear();
        VehicleNotifyRadius = 0f;
        AttackingTeam = 0;
        MinFlyHeight = 0f;
        MaxFlyHeight = 0f;
        MinPlayerFlyHeight = 0f;
        MaxPlayerFlyHeight = 0f;
        DenseEnvironment = false;
        AllowBlindJetJumps = false;
    }

    static TeamDirectives Team(int teamIdx)
    {
        if (!Teams.TryGetValue(teamIdx, out TeamDirectives directives))
        {
            directives = new TeamDirectives();
            Teams.Add(teamIdx, directives);
        }
        return directives;
    }

    // ------------------------------------------------------------- setters

    public static void SetVehicleNotifyRadius(float radius)
    {
        VehicleNotifyRadius = Mathf.Max(0f, radius);
    }

    public static void SetAttackingTeam(int teamIdx)
    {
        AttackingTeam = teamIdx;
    }

    public static void SetSpawnAllowed(int teamIdx, bool allowed)
    {
        Team(teamIdx).SpawnAllowed = allowed;
    }

    /// <summary>
    /// Whether a team may put new AI into the world. False is how a mission
    /// scripts "hold this position, no help is coming".
    /// </summary>
    public static bool IsSpawnAllowed(int teamIdx)
    {
        return !Teams.TryGetValue(teamIdx, out TeamDirectives directives) || directives.SpawnAllowed;
    }

    /// <summary>
    /// Map BF2's difficulty names onto this project's tiers.
    /// </summary>
    /// <remarks>
    /// BF2 names only "medium" and "hard" in the calls we see; this fork has
    /// four tiers (Classic / Veteran / Elite / Legendary). "medium" reads as
    /// Veteran and "hard" as Elite, leaving the two extremes for the player's
    /// own setting - a mission asking for "hard" should not override someone
    /// who chose Legendary downwards, so the higher of the two wins.
    /// </remarks>
    public static void SetDifficulty(int teamIdx, string difficulty)
    {
        if (string.IsNullOrEmpty(difficulty)) return;

        int tier;
        switch (difficulty.ToLowerInvariant())
        {
            case "easy": tier = 0; break;
            case "medium": tier = 1; break;
            case "hard": tier = 2; break;
            case "elite":
            case "insane": tier = 3; break;
            default: return;
        }

        Team(teamIdx).Difficulty = tier;
    }

    /// <summary>Difficulty tier for a team: the mission's, or the player's setting.</summary>
    public static int GetDifficulty(int teamIdx)
    {
        int playerTier = PhxBF3.Config.AIDifficulty;
        if (Teams.TryGetValue(teamIdx, out TeamDirectives directives) && directives.Difficulty.HasValue)
        {
            return Mathf.Max(playerTier, directives.Difficulty.Value);
        }
        return playerTier;
    }

    public static void SetAggressiveness(int teamIdx, float aggressiveness)
    {
        Team(teamIdx).Aggressiveness = Mathf.Clamp(aggressiveness, 0f, 2f);
    }

    /// <summary>
    /// 1 is normal. Below 1 the team holds ground and fights defensively;
    /// above 1 it pushes. Scales how strongly AI weighs objectives against
    /// staying alive.
    /// </summary>
    public static float GetAggressiveness(int teamIdx)
    {
        return Teams.TryGetValue(teamIdx, out TeamDirectives directives) ? directives.Aggressiveness : 1f;
    }

    public static void SetFlyerPathEnabled(string pathName, bool enabled)
    {
        if (string.IsNullOrEmpty(pathName)) return;

        if (enabled) DisabledFlyerPaths.Remove(pathName);
        else DisabledFlyerPaths.Add(pathName);
    }

    /// <summary>
    /// Whether AI flyers may use an authored route. Missions switch these off
    /// for phases where, for instance, transports must not be landing.
    /// </summary>
    public static bool IsFlyerPathEnabled(string pathName)
    {
        return string.IsNullOrEmpty(pathName) || !DisabledFlyerPaths.Contains(pathName);
    }

    public static void SetFlyHeights(float min, float max)
    {
        MinFlyHeight = min;
        MaxFlyHeight = max;
    }

    public static void SetPlayerFlyHeights(float min, float max)
    {
        MinPlayerFlyHeight = min;
        MaxPlayerFlyHeight = max;
    }

    public static void SetDenseEnvironment(bool dense)
    {
        DenseEnvironment = dense;
    }

    public static void SetAllowBlindJetJumps(bool allow)
    {
        AllowBlindJetJumps = allow;
    }

    /// <summary>
    /// Clamp an altitude into the flight envelope. Returns the input unchanged
    /// when the mission set no ceiling, which is the common case.
    /// </summary>
    public static float ClampFlyHeight(float height, bool isPlayer)
    {
        float min = isPlayer ? MinPlayerFlyHeight : MinFlyHeight;
        float max = isPlayer ? MaxPlayerFlyHeight : MaxFlyHeight;

        if (max > 0f) height = Mathf.Min(height, max);
        if (min > 0f) height = Mathf.Max(height, min);
        return height;
    }
}
