using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// When a team may field its hero, and what happens to that hero afterwards.
/// </summary>
/// <remarks>
/// <c>SetHeroClass</c> already stored an odf per team, and nothing ever
/// consulted it - a map could name Darth Vader and never produce one, because
/// availability is a rule set and the two Lua calls that turn those rules on
/// (<c>EnableSPScriptedHeroes</c>, <c>EnableSPHeroRules</c>) were empty.
///
/// BF2 has two distinct modes and they are not variations of each other:
///
/// <list type="bullet">
/// <item><b>Hero rules</b> (multiplayer / instant action): the hero unlocks
/// when the team has earned enough, one player has them at a time, and losing
/// them costs the team the slot until it is earned again.</item>
/// <item><b>Scripted heroes</b> (campaign): the mission decides. The hero is
/// available when the script says so and not before, regardless of
/// score.</item>
/// </list>
///
/// Both are represented here so a map that calls neither behaves as it always
/// did: no hero, which is correct for the maps that never set a hero class.
/// </remarks>
public static class PhxHeroRules
{
    /// <summary>
    /// Points a team must accumulate before its hero unlocks under hero rules.
    /// The stock value is not published; this is in the range stock matches
    /// reach around the time heroes appear, and is one constant so it can be
    /// corrected in one place.
    /// </summary>
    const int DefaultUnlockPoints = 25;

    sealed class TeamState
    {
        public bool Unlocked;
        public bool Spawned;
        public int UnlockPoints = DefaultUnlockPoints;

        /// <summary>Set once the hero has been used and lost.</summary>
        public bool Consumed;
    }

    static readonly Dictionary<int, TeamState> Teams = new Dictionary<int, TeamState>();

    /// <summary>Score-driven hero availability (EnableSPHeroRules).</summary>
    public static bool HeroRulesEnabled { get; private set; }

    /// <summary>Mission-driven hero availability (EnableSPScriptedHeroes).</summary>
    public static bool ScriptedHeroesEnabled { get; private set; }

    public static void Reset()
    {
        Teams.Clear();
        HeroRulesEnabled = false;
        ScriptedHeroesEnabled = false;
    }

    public static void EnableHeroRules()
    {
        HeroRulesEnabled = true;
        Debug.Log("[Phoenix] Hero rules enabled - heroes unlock on team score.");
    }

    public static void EnableScriptedHeroes()
    {
        ScriptedHeroesEnabled = true;
        Debug.Log("[Phoenix] Scripted heroes enabled - the mission controls hero availability.");
    }

    static TeamState State(int team)
    {
        if (!Teams.TryGetValue(team, out TeamState state))
        {
            state = new TeamState();
            Teams.Add(team, state);
        }
        return state;
    }

    /// <summary>Points this team still needs; 0 once unlocked.</summary>
    public static int PointsToUnlock(int team, int currentPoints)
    {
        TeamState state = State(team);
        if (state.Unlocked) return 0;
        return Mathf.Max(0, state.UnlockPoints - currentPoints);
    }

    /// <summary>Override the unlock threshold for one team.</summary>
    public static void SetUnlockPoints(int team, int points)
    {
        State(team).UnlockPoints = Mathf.Max(0, points);
    }

    /// <summary>
    /// A mission granting or revoking its hero directly. Only meaningful with
    /// scripted heroes; under hero rules the score decides.
    /// </summary>
    public static void SetScriptedAvailable(int team, bool available)
    {
        TeamState state = State(team);
        state.Unlocked = available;
        if (!available)
        {
            state.Spawned = false;
        }
    }

    /// <summary>Called as team score changes, to unlock under hero rules.</summary>
    public static void NotifyTeamPoints(int team, int points)
    {
        if (!HeroRulesEnabled) return;

        TeamState state = State(team);
        if (state.Unlocked || state.Consumed) return;
        if (points < state.UnlockPoints) return;

        state.Unlocked = true;
        Debug.Log($"[Phoenix] Team {team} has earned its hero.");
    }

    /// <summary>
    /// Whether a member of this team may spawn as the hero right now.
    /// </summary>
    /// <remarks>
    /// With neither mode enabled this is false: a map that sets a hero class
    /// but never turns on a rule set is a map whose scripts never intended a
    /// hero to appear on their own, and spawning one would change how that map
    /// plays.
    /// </remarks>
    public static bool CanSpawnHero(int team)
    {
        // Testing override. Deliberately the ONLY thing it changes: the hero
        // still comes from the map's own hero class, still occupies the team's
        // single slot, and NotifyHeroLost still frees it - so what is being
        // tested is the real hero, reached early, rather than a different code
        // path that only exists in development.
        //
        // Everything below this line is the shipping rule set and is untouched:
        // points accumulate, unlock at the authored threshold, one hero per
        // team, slot spent on death.
        if (PhxBF3.Config.AlwaysAllowHeroes)
        {
            return !State(team).Spawned;
        }

        if (!HeroRulesEnabled && !ScriptedHeroesEnabled) return false;

        TeamState state = State(team);
        return state.Unlocked && !state.Spawned;
    }

    /// <summary>Claim the team's single hero slot.</summary>
    public static bool TryClaimHero(int team)
    {
        if (!CanSpawnHero(team)) return false;

        State(team).Spawned = true;
        return true;
    }

    /// <summary>
    /// The hero died or left. Under hero rules the slot is spent and must be
    /// earned again; scripted heroes stay available until the mission says
    /// otherwise, because a campaign hero is usually the player.
    /// </summary>
    public static void NotifyHeroLost(int team)
    {
        TeamState state = State(team);
        state.Spawned = false;

        if (HeroRulesEnabled)
        {
            state.Unlocked = false;
            state.Consumed = false;
        }
    }
}
