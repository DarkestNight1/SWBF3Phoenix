using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Global AI orchestration. Where PhxBF3AIController is a single soldier's
/// brain, the director is the battlefield commander:
///
///  - hands out skill profiles per difficulty tier (Classic/Veteran/Elite/Legendary)
///  - groups AI into squads and assigns squads to command post objectives
///  - designates flanking squads so attacks come from multiple directions
///  - in space/vertical-battlefront scenarios, tasks AI with capital ship
///    objectives once shields are down (board and sabotage)
///
/// Attached to the persistent BF3Legacy host object by PhxBF3.Bootstrap().
/// </summary>
public class PhxAIDirector : MonoBehaviour
{
    const int SquadSize = 4;
    const float ReplanInterval = 10f;

    float ReplanTimer;

    class PhxSquad
    {
        public readonly List<PhxBF3AIController> Members = new List<PhxBF3AIController>();
        public PhxCommandpost Objective;
        public bool IsFlanking;
    }

    readonly List<PhxSquad> Squads = new List<PhxSquad>();

    // All BF3 AI controllers currently alive, registered on spawn
    static readonly List<PhxBF3AIController> Controllers = new List<PhxBF3AIController>();

    public static void Register(PhxBF3AIController controller)
    {
        if (!Controllers.Contains(controller)) Controllers.Add(controller);
    }

    public static void Unregister(PhxBF3AIController controller)
    {
        Controllers.Remove(controller);
    }

    public static PhxAISkillProfile GetSkillProfile()
    {
        return PhxAISkillProfile.ForDifficulty(PhxBF3.Config.AIDifficulty).WithJitter();
    }


    void Update()
    {
        ReplanTimer -= Time.deltaTime;
        if (ReplanTimer > 0f) return;
        ReplanTimer = ReplanInterval;

        Replan();
    }

    void Replan()
    {
        Controllers.RemoveAll(c => c == null || c.Pawn == null);
        if (Controllers.Count == 0)
        {
            Squads.Clear();
            return;
        }

        PhxScene scene = PhxGame.GetScene();
        PhxCommandpost[] posts = scene?.GetCommandPosts();
        if (posts == null || posts.Length == 0) return;

        // rebuild squads per team
        Squads.Clear();
        Dictionary<int, List<PhxBF3AIController>> perTeam = new Dictionary<int, List<PhxBF3AIController>>();
        foreach (PhxBF3AIController c in Controllers)
        {
            int team = c.Pawn.GetInstance().Team;
            if (!perTeam.TryGetValue(team, out List<PhxBF3AIController> list))
            {
                perTeam[team] = list = new List<PhxBF3AIController>();
            }
            list.Add(c);
        }

        foreach (KeyValuePair<int, List<PhxBF3AIController>> kv in perTeam)
        {
            AssignTeamSquads(kv.Key, kv.Value, posts);
        }
    }

    void AssignTeamSquads(int team, List<PhxBF3AIController> members, PhxCommandpost[] posts)
    {
        // capturable objectives for this team
        List<PhxCommandpost> targets = new List<PhxCommandpost>();
        foreach (PhxCommandpost cp in posts)
        {
            if (cp.Team != team) targets.Add(cp);
        }
        if (targets.Count == 0) return;

        int squadCount = Mathf.Max(1, members.Count / SquadSize);
        for (int s = 0; s < squadCount; ++s)
        {
            PhxSquad squad = new PhxSquad();
            for (int m = s; m < members.Count; m += squadCount)
            {
                squad.Members.Add(members[m]);
            }

            // spread squads over objectives; extra squads flank
            squad.Objective = targets[s % targets.Count];
            squad.IsFlanking = Random.value < GetSquadSkill(squad).FlankTendency;

            Vector3 flankOffset = Vector3.zero;
            if (squad.IsFlanking)
            {
                // detour perpendicular to the approach direction
                Vector3 approach = squad.Objective.transform.position -
                                   squad.Members[0].Pawn.GetInstance().transform.position;
                Vector3 side = Vector3.Cross(approach.normalized, Vector3.up);
                flankOffset = side * (Random.value < 0.5f ? -1f : 1f) * Random.Range(25f, 60f);
            }

            foreach (PhxBF3AIController member in squad.Members)
            {
                member.AssignedObjective = squad.Objective;
                member.FlankOffset = flankOffset;
            }
            Squads.Add(squad);
        }
    }

    PhxAISkillProfile GetSquadSkill(PhxSquad squad)
    {
        return squad.Members.Count > 0 ? squad.Members[0].Skill
                                       : PhxAISkillProfile.ForDifficulty(PhxBF3.Config.AIDifficulty);
    }
}
