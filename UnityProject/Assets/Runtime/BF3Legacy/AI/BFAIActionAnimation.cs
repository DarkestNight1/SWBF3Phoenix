using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns a decided <see cref="BFAIAction"/> into a stock animation.
/// </summary>
/// <remarks>
/// The AI layer names an intent - "seek cover", "regroup", "reload" - and this
/// is the only place that knows which clip that corresponds to. Without the
/// separation the decision code would have to spell out clip names, and every
/// mod that renames a bank would break the AI rather than just its animation.
///
/// Nothing here is authoritative about movement: the controller still drives
/// position, aim and fire. This decides only what the body looks like it is
/// doing, and it stays silent whenever the soldier is already busy with
/// something the gameplay layer owns (dying, in a vehicle, mid-combo).
///
/// Every action lists several candidate clips. Stock banks vary by species and
/// by weapon, and a bank that has none of the candidates simply gets no
/// override - the soldier keeps its locomotion animation, which is the same
/// thing that happened before this existed.
/// </remarks>
public static class BFAIActionAnimation
{
    // Ordered by preference. First one present in the bank wins.
    static readonly Dictionary<BFAIAction, string[]> Clips = new Dictionary<BFAIAction, string[]>
    {
        { BFAIAction.HoldPosition, new[] { "standalert_idle_emote", "stand_idle_emote" } },
        { BFAIAction.Advance,      new[] { "standalert_runforward", "stand_runforward" } },
        { BFAIAction.Attack,       new[] { "standalert_runforward", "stand_runforward" } },
        { BFAIAction.Defend,       new[] { "crouchalert_idle_emote", "standalert_idle_emote" } },
        { BFAIAction.Capture,      new[] { "standalert_idle_emote", "stand_idle_emote" } },
        { BFAIAction.Reinforce,    new[] { "sprint_full", "sprint", "stand_runforward" } },
        { BFAIAction.Flank,        new[] { "sprint_full", "sprint", "standalert_runforward" } },
        { BFAIAction.Retreat,      new[] { "sprint_full", "sprint", "stand_runforward" } },
        { BFAIAction.SeekCover,    new[] { "crouchalert_runforward", "crouch_runforward" } },
        { BFAIAction.Pursue,       new[] { "standalert_runforward", "stand_runforward" } },
        { BFAIAction.Regroup,      new[] { "stand_runforward", "standalert_runforward" } },
        { BFAIAction.AssistAlly,   new[] { "stand_runforward", "standalert_runforward" } },
        { BFAIAction.Reload,       new[] { "stand_reload_full", "stand_reload" } },
    };

    /// <summary>
    /// Candidate clip names for an action, most preferred first. Exposed so a
    /// diagnostic can report which of them a given bank actually has.
    /// </summary>
    public static IReadOnlyList<string> CandidatesFor(BFAIAction action)
    {
        return Clips.TryGetValue(action, out string[] c) ? c : System.Array.Empty<string>();
    }

    /// <summary>
    /// Ask the soldier to look like it is doing <paramref name="action"/>.
    /// </summary>
    /// <param name="bankPrefix">
    /// The soldier's animation vocabulary - "human", "gam", "wok". Combined
    /// with the weapon posture to form the clip name the bank actually holds.
    /// </param>
    /// <returns>true when a clip was found and started.</returns>
    public static bool Play(PhxSoldier soldier, BFAIAction action, string bankPrefix,
                            string posture, float holdSeconds = 1.5f)
    {
        if (soldier == null || string.IsNullOrEmpty(bankPrefix)) return false;
        if (!Clips.TryGetValue(action, out string[] candidates)) return false;

        // Stock clip names are "<species>_<posture>_<motion>", e.g.
        // "human_rifle_standalert_runforward". The posture is the weapon class
        // the soldier is holding, which the caller knows and this does not.
        string post = string.IsNullOrEmpty(posture) ? "rifle" : posture;

        for (int i = 0; i < candidates.Length; ++i)
        {
            string clip = bankPrefix + "_" + post + "_" + candidates[i];
            // Pass no bank: bankPrefix is a clip-name prefix ("human"), NOT an
            // animation bank. The animator resolves against its own real banks
            // (human_0..human_4, human_sabre).
            // Through the arbitrator, not straight at the animator: PhxSoldier
            // writes layer 0 every frame from locomotion, and it has to agree
            // to yield. It refuses while airborne, landing or turning in place.
            if (soldier.RequestAnimOverride(null, clip, holdSeconds))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The action vocabulary equivalent of the controller's own state.
    /// </summary>
    /// <remarks>
    /// The BF3 controller runs a concrete state machine (seek / defend /
    /// engage / capture / board / sabotage / man turret) that predates
    /// <see cref="BFAIAction"/> and works. Rather than replace it, this
    /// translates it into the shared vocabulary so the animation layer - and
    /// anything else that wants to observe intent - has one thing to read.
    ///
    /// <paramref name="reloading"/> and <paramref name="hurt"/> override the
    /// state because they say more about what the body is doing than the
    /// objective does.
    /// </remarks>
    public static BFAIAction FromControllerState(string stateName, bool hasTarget, bool reloading, bool hurt, bool crouching)
    {
        if (reloading) return BFAIAction.Reload;
        if (hurt) return BFAIAction.Retreat;

        switch (stateName)
        {
            case "Engage":
                return crouching ? BFAIAction.SeekCover : BFAIAction.Attack;
            case "Defend":
                return BFAIAction.Defend;
            case "Capture":
                return BFAIAction.Capture;
            case "Board":
            case "Sabotage":
                return BFAIAction.Advance;
            case "ManTurret":
                return BFAIAction.HoldPosition;
            case "SeekObjective":
            default:
                return hasTarget ? BFAIAction.Pursue : BFAIAction.Advance;
        }
    }
}
