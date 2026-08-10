using System.Collections.Generic;
using UnityEngine;

/// <summary>What a soldier has decided to do about its situation.</summary>
public enum BFAIAction
{
    HoldPosition,
    Advance,
    Attack,
    Defend,
    Capture,
    Reinforce,
    Flank,
    Retreat,
    SeekCover,
    Pursue,
    Regroup,
    AssistAlly,
    Reload,
}

/// <summary>
/// Everything the soldier knows when it decides. Deliberately only what it
/// could plausibly know.
/// </summary>
public struct BFAISituation
{
    public Vector3 Position;
    public int Team;

    /// <summary>0 dead to 1 unhurt.</summary>
    public float HealthFraction;

    /// <summary>Rounds left as a fraction of a magazine.</summary>
    public float AmmoFraction;

    /// <summary>Is there a target this soldier can currently see?</summary>
    public bool HasVisibleEnemy;

    /// <summary>A contact it knows about but cannot currently see.</summary>
    public bool HasRememberedEnemy;

    public float DistanceToEnemy;

    /// <summary>Enemies it is aware of within a short radius.</summary>
    public int NearbyEnemies;

    /// <summary>Friends within supporting distance.</summary>
    public int NearbyAllies;

    /// <summary>How dangerous this ground has been lately, from PhxAIDanger.</summary>
    public float LocalDanger;

    /// <summary>Is the objective it cares about currently contested?</summary>
    public bool ObjectiveThreatened;

    public float DistanceToObjective;

    /// <summary>Does this soldier currently hold usable cover?</summary>
    public bool InCover;

    /// <summary>Is a better piece of cover reachable?</summary>
    public bool CoverAvailable;
}

/// <summary>
/// Turns a situation into an action, by scoring every option rather than
/// running down a fixed chain of ifs.
/// </summary>
/// <remarks>
/// The behaviour this replaces is "enemy detected, shoot enemy". The behaviour
/// it produces is closer to how a person actually plays: notice the situation,
/// weigh several things you could do about it, and pick one - where the
/// weighing accounts for whether you can win the fight, whether the objective
/// needs you more, whether you are about to die, and whether there is a better
/// place to be standing.
///
/// Scoring rather than branching matters for two reasons. It makes the
/// trade-offs explicit and adjustable in one place instead of scattered
/// through a state machine. And it gives somewhere natural for controlled
/// imperfection to live: the chosen action is drawn from among the good ones
/// rather than always the single best, with the spread set by difficulty. That
/// is what makes an AI take an unnecessary fight or chase someone it should
/// have let go - the things human players do constantly and scripted AI never
/// does.
/// </remarks>
public static class BFAIDecision
{
    /// <summary>
    /// How far below the best score an option can be and still be chosen,
    /// per difficulty. Poor decision-making is not random behaviour; it is a
    /// wider band of "good enough".
    /// </summary>
    static float DecisionSpread(int difficulty)
    {
        switch (difficulty)
        {
            case 0: return 0.45f;   // recruits pick the wrong reasonable option often
            case 1: return 0.25f;
            case 2: return 0.12f;
            default: return 0.06f;  // elites rarely, but not never
        }
    }

    /// <summary>Health below which a soldier starts thinking about leaving.</summary>
    const float ShakenHealth = 0.35f;

    /// <summary>Reusable scratch, so a decision allocates nothing.</summary>
    static readonly List<(BFAIAction Action, float Score)> Scratch =
        new List<(BFAIAction, float)>(16);

    /// <summary>
    /// Choose. <paramref name="aggression"/> comes from the mission's
    /// SetTeamAggressiveness; 1 is normal.
    /// </summary>
    public static BFAIAction Choose(in BFAISituation s, int difficulty, float aggression = 1f)
    {
        Scratch.Clear();

        float outnumbered = s.NearbyEnemies - s.NearbyAllies;
        bool canWinFight = CanWinFight(s, outnumbered);

        // --- fighting ---
        if (s.HasVisibleEnemy)
        {
            float attack = 0.6f + 0.25f * aggression;
            if (canWinFight) attack += 0.35f;
            if (s.HealthFraction < ShakenHealth) attack -= 0.45f;
            if (s.AmmoFraction < 0.15f) attack -= 0.5f;
            if (s.InCover) attack += 0.15f;
            Scratch.Add((BFAIAction.Attack, attack));

            // Flanking is only worth it when the fight is not already won and
            // there is someone else to hold attention while you move.
            float flank = 0.3f + 0.2f * aggression;
            if (!canWinFight && s.NearbyAllies > 0) flank += 0.35f;
            if (s.DistanceToEnemy < 12f) flank -= 0.4f;   // too close to break off
            Scratch.Add((BFAIAction.Flank, flank));

            if (s.CoverAvailable && !s.InCover)
            {
                float cover = 0.45f;
                if (s.HealthFraction < 0.6f) cover += 0.35f;
                if (outnumbered > 0f) cover += 0.25f;
                cover -= 0.15f * aggression;
                Scratch.Add((BFAIAction.SeekCover, cover));
            }
        }
        else if (s.HasRememberedEnemy)
        {
            // Someone was here. Pressing to a last-known position is what makes
            // breaking line of sight feel like a reprieve rather than an escape.
            float pursue = 0.45f + 0.2f * aggression;
            if (s.HealthFraction < ShakenHealth) pursue -= 0.4f;
            if (s.NearbyAllies == 0) pursue -= 0.15f;
            Scratch.Add((BFAIAction.Pursue, pursue));
        }

        // --- self-preservation ---
        if (s.HealthFraction < ShakenHealth)
        {
            float retreat = 0.55f + (ShakenHealth - s.HealthFraction) * 1.5f;
            if (outnumbered > 0f) retreat += 0.3f;
            retreat -= 0.25f * aggression;
            // Retreating out of a fight you are winning is worse than staying.
            if (canWinFight) retreat -= 0.3f;
            Scratch.Add((BFAIAction.Retreat, retreat));
        }

        if (s.AmmoFraction < 0.2f)
        {
            float reload = 0.5f + (0.2f - s.AmmoFraction) * 2f;
            if (s.DistanceToEnemy < 8f && s.HasVisibleEnemy) reload -= 0.35f;
            Scratch.Add((BFAIAction.Reload, reload));
        }

        // --- objectives ---
        if (s.ObjectiveThreatened)
        {
            float defend = 0.65f;
            defend += Mathf.Clamp01(1f - s.DistanceToObjective / 80f) * 0.4f;
            if (!s.HasVisibleEnemy) defend += 0.2f;
            Scratch.Add((BFAIAction.Defend, defend));
        }

        float capture = 0.5f * aggression;
        capture += Mathf.Clamp01(1f - s.DistanceToObjective / 150f) * 0.35f;
        if (s.HasVisibleEnemy) capture -= 0.3f;      // deal with the shooting first
        if (s.LocalDanger > 1f) capture -= 0.25f;
        Scratch.Add((BFAIAction.Capture, capture));

        float advance = 0.4f * aggression;
        if (!s.HasVisibleEnemy && !s.ObjectiveThreatened) advance += 0.2f;
        Scratch.Add((BFAIAction.Advance, advance));

        // --- cohesion ---
        if (s.NearbyAllies == 0 && (s.HasVisibleEnemy || s.LocalDanger > 0.5f))
        {
            Scratch.Add((BFAIAction.Regroup, 0.4f + s.LocalDanger * 0.2f));
        }

        return Pick(difficulty);
    }

    /// <summary>
    /// The judgement at the centre of "should I take this fight".
    /// </summary>
    /// <remarks>
    /// Not a simulation - a person's snap read. Am I healthy, do I have ammo,
    /// am I outnumbered, am I in cover, is this a range I shoot well at. The
    /// answer being wrong sometimes is correct behaviour.
    /// </remarks>
    static bool CanWinFight(in BFAISituation s, float outnumbered)
    {
        float odds = 0f;
        odds += (s.HealthFraction - 0.5f) * 1.2f;
        odds += (s.AmmoFraction - 0.3f) * 0.6f;
        odds -= outnumbered * 0.5f;
        if (s.InCover) odds += 0.4f;
        if (s.DistanceToEnemy > 60f) odds -= 0.2f;    // long range favours nobody
        odds -= s.LocalDanger * 0.25f;

        return odds > 0f;
    }

    /// <summary>
    /// Pick from the scored options, allowing worse ones through in proportion
    /// to how poor this soldier's judgement is.
    /// </summary>
    static BFAIAction Pick(int difficulty)
    {
        if (Scratch.Count == 0) return BFAIAction.HoldPosition;

        float best = float.MinValue;
        for (int i = 0; i < Scratch.Count; ++i)
        {
            if (Scratch[i].Score > best) best = Scratch[i].Score;
        }

        float threshold = best - DecisionSpread(difficulty);

        // Weighted draw among everything above the threshold, so a slightly
        // worse option is picked occasionally rather than never or half the
        // time.
        float total = 0f;
        for (int i = 0; i < Scratch.Count; ++i)
        {
            if (Scratch[i].Score >= threshold) total += Scratch[i].Score - threshold + 0.01f;
        }
        if (total <= 0f) return BFAIAction.HoldPosition;

        float roll = Random.value * total;
        for (int i = 0; i < Scratch.Count; ++i)
        {
            if (Scratch[i].Score < threshold) continue;

            roll -= Scratch[i].Score - threshold + 0.01f;
            if (roll <= 0f) return Scratch[i].Action;
        }
        return Scratch[Scratch.Count - 1].Action;
    }
}
