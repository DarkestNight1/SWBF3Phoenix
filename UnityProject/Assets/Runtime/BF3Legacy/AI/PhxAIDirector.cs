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

    /// <summary>Drop every controller (called on map change).</summary>
    public static void ResetAll()
    {
        Controllers.Clear();
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

    /// <summary>
    /// True once this controller's pawn is gone. NOTE: Pawn is an *interface*
    /// reference, so `Pawn == null` uses plain reference equality and does
    /// NOT catch a destroyed Unity object. Going through GetInstance() gets
    /// us a MonoBehaviour, where Unity's overloaded null check applies -
    /// without this, controllers from previous maps never get pruned.
    /// </summary>
    static bool IsStale(PhxBF3AIController c)
    {
        if (c == null || c.Pawn == null) return true;
        return c.Pawn.GetInstance() == null;
    }

    void Replan()
    {
        Controllers.RemoveAll(IsStale);
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
        // split the map into capturable targets and posts we own (to defend)
        List<PhxCommandpost> targets = new List<PhxCommandpost>();
        List<PhxCommandpost> owned = new List<PhxCommandpost>();
        foreach (PhxCommandpost cp in posts)
        {
            if (cp.Team != team) targets.Add(cp);
            else owned.Add(cp);
        }

        // units already on a boarding run keep their orders - reassigning
        // them would teleport-yank them out of the ship
        int alreadyBoarding = 0;
        members = members.FindAll(m =>
        {
            if (m.IsBusyBoarding) { alreadyBoarding++; return false; }
            return true;
        });
        if (members.Count == 0) return;

        // boardable enemy capital ship? task a boarding party (one at a time)
        PhxCapitalShip boardable = null;
        if (alreadyBoarding == 0)
        {
            foreach (PhxCapitalShip ship in PhxCapitalShip.GetAll())
            {
                if (ship.Team != team && ship.CanBeBoarded())
                {
                    boardable = ship;
                    break;
                }
            }
        }

        int squadCount = Mathf.Max(1, members.Count / SquadSize);

        // Squad role split: ~1/3 defend owned posts (if any), one squad boards
        // a vulnerable capital ship, the rest attack. Attackers always get at
        // least one squad when there's anything to take.
        int defendSquads = owned.Count > 0 ? Mathf.Max(squadCount / 3, squadCount > 1 ? 1 : 0) : 0;
        bool sendBoarders = boardable != null && squadCount > 1;

        for (int s = 0; s < squadCount; ++s)
        {
            PhxSquad squad = new PhxSquad();
            for (int m = s; m < members.Count; m += squadCount)
            {
                squad.Members.Add(members[m]);
            }
            Squads.Add(squad);

            // role assignment by squad index: boarders first, then defenders,
            // remainder attacks
            if (sendBoarders && s == 0)
            {
                foreach (PhxBF3AIController member in squad.Members)
                {
                    member.BoardTarget = boardable;
                    member.AssignedObjective = null;
                    member.DefendObjective = null;
                    member.FlankOffset = Vector3.zero;
                }
                continue;
            }

            int roleIdx = sendBoarders ? s - 1 : s;
            if (roleIdx < defendSquads && owned.Count > 0)
            {
                PhxCommandpost post = owned[roleIdx % owned.Count];
                foreach (PhxBF3AIController member in squad.Members)
                {
                    member.DefendObjective = post;
                    member.AssignedObjective = null;
                    member.BoardTarget = null;
                    member.FlankOffset = Vector3.zero;
                }
                continue;
            }

            if (targets.Count == 0)
            {
                // nothing left to take - everyone defends
                if (owned.Count > 0)
                {
                    PhxCommandpost post = owned[s % owned.Count];
                    foreach (PhxBF3AIController member in squad.Members)
                    {
                        member.DefendObjective = post;
                        member.AssignedObjective = null;
                    }
                }
                continue;
            }

            squad.Objective = targets[roleIdx % targets.Count];
            squad.IsFlanking = Random.value < GetSquadSkill(squad).FlankTendency;

            Vector3 flankOffset = Vector3.zero;
            if (squad.IsFlanking && squad.Members.Count > 0 && squad.Members[0].Pawn != null)
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
                member.DefendObjective = null;
                member.BoardTarget = null;
                member.FlankOffset = flankOffset;
            }
        }
    }

    PhxAISkillProfile GetSquadSkill(PhxSquad squad)
    {
        return squad.Members.Count > 0 ? squad.Members[0].Skill
                                       : PhxAISkillProfile.ForDifficulty(PhxBF3.Config.AIDifficulty);
    }
}
