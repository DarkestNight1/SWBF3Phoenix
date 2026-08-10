using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Works out which command posts matter right now, and to whom.
/// </summary>
/// <remarks>
/// "Which post should I go to" is the question that decides whether a
/// Battlefront round looks like a battle or like twenty people walking to the
/// nearest flag. A competent player does not pick the nearest one; they pick
/// the one that is about to fall, or the one that cuts the enemy's spawn line,
/// or the one they can actually take with the people they have.
///
/// Each post is scored per team from things a team could plausibly know -
/// who holds it, who is standing on it, how far it is, how central it is to
/// the map's connectivity, and how badly the team is losing. The result is a
/// ranking that shifts through the round: early on, the middle matters; when
/// reinforcements run low, holding what you have matters more than taking
/// more.
/// </remarks>
public static class BFAIObjectiveEvaluator
{
    public struct PostAssessment
    {
        public PhxCommandpost Post;

        /// <summary>How much this team should want it, higher is more.</summary>
        public float Value;

        /// <summary>True when the team holds it and enemies are on it.</summary>
        public bool Threatened;

        /// <summary>Enemies known to be near it.</summary>
        public int EnemyPresence;

        public int AllyPresence;
    }

    static readonly List<PostAssessment> Assessments = new List<PostAssessment>();
    static readonly Collider[] Overlap = new Collider[64];

    /// <summary>Radius counted as "at" a post when reading presence.</summary>
    const float PresenceRadius = 25f;

    static float LastEvaluated = float.NegativeInfinity;
    static int LastTeam = -1;

    /// <summary>
    /// How often the picture is refreshed. Presence is an overlap query per
    /// post, so this is not free, and the tactical picture does not change
    /// meaningfully faster than this.
    /// </summary>
    const float RefreshInterval = 1.5f;

    /// <summary>
    /// Assessments for a team, best first. Cached briefly and shared - every
    /// soldier on a team wants the same answer.
    /// </summary>
    public static IReadOnlyList<PostAssessment> Evaluate(int team)
    {
        if (team == LastTeam && Time.time - LastEvaluated < RefreshInterval)
        {
            return Assessments;
        }

        LastTeam = team;
        LastEvaluated = Time.time;
        Assessments.Clear();

        PhxScene scene = PhxGame.GetScene();
        PhxCommandpost[] posts = scene?.GetCommandPosts();
        if (posts == null) return Assessments;

        PhxMatch match = PhxGame.GetMatch();
        float desperation = Desperation(match, team);

        // Map centre from the posts themselves - a post near the centroid of
        // all posts is a post that connects the map, which is what makes it
        // worth more than a corner.
        Vector3 centroid = Vector3.zero;
        int counted = 0;
        for (int i = 0; i < posts.Length; ++i)
        {
            if (posts[i] == null) continue;
            centroid += posts[i].transform.position;
            ++counted;
        }
        if (counted == 0) return Assessments;
        centroid /= counted;

        for (int i = 0; i < posts.Length; ++i)
        {
            PhxCommandpost post = posts[i];
            if (post == null) continue;

            Assessments.Add(Assess(post, team, centroid, desperation));
        }

        Assessments.Sort((a, b) => b.Value.CompareTo(a.Value));
        return Assessments;
    }

    /// <summary>
    /// How badly this team is losing, 0 comfortable to 1 desperate.
    /// A losing team should consolidate; a winning one can push.
    /// </summary>
    static float Desperation(PhxMatch match, int team)
    {
        if (match == null || team < 1 || team > PhxMatch.MAX_TEAMS) return 0f;

        int own = match.GetReinforcementCount(team);
        int best = 0;
        for (int t = 1; t <= PhxMatch.MAX_TEAMS; ++t)
        {
            if (t == team) continue;
            best = Mathf.Max(best, match.GetReinforcementCount(t));
        }
        if (best <= 0) return 0f;

        return Mathf.Clamp01(1f - (float)own / best);
    }

    static PostAssessment Assess(PhxCommandpost post, int team, Vector3 centroid, float desperation)
    {
        Vector3 position = post.transform.position;

        int allies = 0, enemies = 0;
        int count = Physics.OverlapSphereNonAlloc(position, PresenceRadius, Overlap,
                                                  PhxLayers.Soldier, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = Overlap[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.IsDead) continue;

            if (soldier.Team.Get() == team) ++allies;
            else if (soldier.Team.Get() > 0) ++enemies;
        }

        bool owned = post.Team == team;
        bool neutral = post.Team == 0;
        bool threatened = owned && enemies > allies;

        float value = 0f;

        // A post you do not hold is worth taking; a neutral one is cheapest.
        if (neutral) value += 1.0f;
        else if (!owned) value += 0.75f;

        // One you hold and are losing is the most urgent thing on the map -
        // it is worth more than any post you might take instead.
        if (threatened) value += 1.4f + Mathf.Min(enemies - allies, 4) * 0.15f;

        // Central posts connect the map. Losing the middle loses everything
        // behind it, which is why players fight over it out of proportion to
        // its count.
        float centrality = 1f - Mathf.Clamp01(Vector3.Distance(position, centroid) / 300f);
        value += centrality * 0.5f;

        // Losing teams consolidate rather than spread: hold what you have.
        if (owned) value += desperation * 0.8f;
        else value -= desperation * 0.4f;

        // Somewhere already crowded with your own side needs you less.
        if (allies > 4 && !threatened) value -= 0.35f;

        return new PostAssessment
        {
            Post = post,
            Value = value,
            Threatened = threatened,
            EnemyPresence = enemies,
            AllyPresence = allies,
        };
    }

    /// <summary>
    /// The post this soldier should go to: the highest-valued one, adjusted
    /// for how far away it is.
    /// </summary>
    /// <remarks>
    /// Distance is a divisor rather than a filter, so a soldier will cross a
    /// map for something genuinely important and will not cross it for a
    /// marginal gain - which is the behaviour that stops a whole team
    /// abandoning one flank because a post on the other one ticked over.
    /// </remarks>
    public static PhxCommandpost ChooseFor(Vector3 position, int team, out bool defending)
    {
        defending = false;

        IReadOnlyList<PostAssessment> assessments = Evaluate(team);
        if (assessments.Count == 0) return null;

        PhxCommandpost best = null;
        float bestScore = float.MinValue;
        bool bestDefending = false;

        for (int i = 0; i < assessments.Count; ++i)
        {
            PostAssessment a = assessments[i];
            if (a.Post == null) continue;

            float distance = Vector3.Distance(position, a.Post.transform.position);
            float score = a.Value / (1f + distance / 120f);

            if (score <= bestScore) continue;

            bestScore = score;
            best = a.Post;
            bestDefending = a.Post.Team == team;
        }

        defending = bestDefending;
        return best;
    }
}
