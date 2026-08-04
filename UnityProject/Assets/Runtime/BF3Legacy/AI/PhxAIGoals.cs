using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The script-declared AI goals every mission lua sets up, which the runtime
/// previously discarded (AddAIGoal was an empty stub returning null).
///
/// Per the mod tools documentation:
///   - AddAIGoal(team, goalName, weight[, target[, flag]]) returns a handle.
///   - "Weight is a relative weight for the goal - since you can specify
///     multiple goals for a team, a goal with weight 2 will get twice as many
///     units as a goal with weight 1."
///   - Conquest and Deathmatch goals need no target (they work it out
///     themselves); Defend, Destroy and CTF goals must name what to protect
///     or attack.
///
/// This is what decides how a team splits its forces, so honoring it replaces
/// the AI director's previously hardcoded attack/defend ratio with the mix the
/// map author actually asked for.
/// </summary>
public enum PhxAIGoalType
{
    Conquest,
    Deathmatch,
    Defend,
    Destroy,
    CTF,
    Unknown,
}

public class PhxAIGoal
{
    public int Handle;
    public int Team;
    public PhxAIGoalType Type;
    public float Weight = 1f;
    public string TargetName;     // region / object name for Defend/Destroy/CTF
    public int FlagPtr;
}

public static class PhxAIGoals
{
    static readonly List<PhxAIGoal> Goals = new List<PhxAIGoal>();
    static int NextHandle = 1;

    public static IReadOnlyList<PhxAIGoal> All => Goals;

    public static void Reset()
    {
        Goals.Clear();
        NextHandle = 1;
    }

    public static int Add(int team, string goalName, float weight, string target = null, int flagPtr = 0)
    {
        PhxAIGoal goal = new PhxAIGoal
        {
            Handle = NextHandle++,
            Team = team,
            Type = ParseType(goalName),
            // a non-positive weight would silently remove the goal from the
            // allocation; treat it as the documented default of 1
            Weight = weight > 0f ? weight : 1f,
            TargetName = target,
            FlagPtr = flagPtr,
        };
        Goals.Add(goal);

        Debug.Log($"[BF3Legacy] AI goal for team {team}: {goal.Type} " +
                  $"(weight {goal.Weight}{(target != null ? ", target " + target : "")})");
        return goal.Handle;
    }

    public static void Delete(int handle)
    {
        Goals.RemoveAll(g => g.Handle == handle);
    }

    public static void ClearTeam(int team)
    {
        Goals.RemoveAll(g => g.Team == team);
    }

    public static List<PhxAIGoal> GetForTeam(int team)
    {
        List<PhxAIGoal> result = new List<PhxAIGoal>();
        foreach (PhxAIGoal g in Goals)
        {
            if (g.Team == team) result.Add(g);
        }
        return result;
    }

    /// <summary>
    /// Split a unit count across a team's goals in proportion to weight, per
    /// the documented "weight 2 gets twice as many units as weight 1" rule.
    /// Returns null when the team declared no goals (caller keeps its default
    /// behaviour).
    /// </summary>
    public static Dictionary<PhxAIGoal, int> Allocate(int team, int unitCount)
    {
        List<PhxAIGoal> goals = GetForTeam(team);
        if (goals.Count == 0 || unitCount <= 0) return null;

        float totalWeight = 0f;
        foreach (PhxAIGoal g in goals) totalWeight += g.Weight;
        if (totalWeight <= 0f) return null;

        Dictionary<PhxAIGoal, int> allocation = new Dictionary<PhxAIGoal, int>();
        int assigned = 0;

        for (int i = 0; i < goals.Count; ++i)
        {
            int share = (i == goals.Count - 1)
                ? unitCount - assigned                      // last goal soaks the remainder
                : Mathf.FloorToInt(unitCount * (goals[i].Weight / totalWeight));

            share = Mathf.Max(share, 0);
            allocation[goals[i]] = share;
            assigned += share;
        }
        return allocation;
    }

    static PhxAIGoalType ParseType(string goalName)
    {
        if (string.IsNullOrEmpty(goalName)) return PhxAIGoalType.Unknown;
        switch (goalName.ToLowerInvariant())
        {
            case "conquest": return PhxAIGoalType.Conquest;
            case "deathmatch": return PhxAIGoalType.Deathmatch;
            case "defend": return PhxAIGoalType.Defend;
            case "destroy": return PhxAIGoalType.Destroy;
            case "ctfoffense":
            case "ctfdefense":
            case "ctf": return PhxAIGoalType.CTF;
            default:
                Debug.LogWarning($"[BF3Legacy] Unknown AI goal type '{goalName}'");
                return PhxAIGoalType.Unknown;
        }
    }
}
