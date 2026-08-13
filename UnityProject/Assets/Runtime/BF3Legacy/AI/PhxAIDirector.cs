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

        // Noise and kill sites from the previous map would otherwise still be
        // "audible"/dangerous to the first AI to spawn on the next one.
        PhxAIPerception.Reset();
        PhxAIDanger.Reset();

        // Per-class warnings are suppressed after their first report. Statics
        // outlive a match, so without clearing this a problem reported on one
        // map stays silent for the rest of the session - including on maps
        // where it is a different problem.
        PhxBF3AIController.ResetDiagnostics();

        // Region names are per map, so last map's threat picture is not merely
        // stale here - it is about regions that no longer exist.
        BFSquadSystem.Reset();
    }

    public static PhxAISkillProfile GetSkillProfile()
    {
        return GetSkillProfile(0);
    }

    /// <summary>
    /// Skill for a unit of <paramref name="team"/>, taking the mission's
    /// SetAIDifficulty for that team into account as well as the player's own
    /// setting, and tightening detection on maps the script marked as dense.
    /// </summary>
    public static PhxAISkillProfile GetSkillProfile(int team)
    {
        PhxAISkillProfile profile =
            PhxAISkillProfile.ForDifficulty(PhxAIDirectives.GetDifficulty(team)).WithJitter();

        if (PhxAIDirectives.DenseEnvironment)
        {
            // On a map the mission calls dense, sight lines are short and a
            // detection range tuned for open ground means AI reacting to
            // things it cannot actually see through.
            profile.DetectionRange *= 0.6f;
        }

        // Aggressiveness moves the balance between pursuing objectives and
        // staying alive, which is the one axis a mission can dial without
        // making AI better or worse at shooting.
        float aggression = PhxAIDirectives.GetAggressiveness(team);
        if (!Mathf.Approximately(aggression, 1f))
        {
            profile.ObjectiveFocus = Mathf.Clamp01(profile.ObjectiveFocus * aggression);
            profile.CoverUsage = Mathf.Clamp01(profile.CoverUsage / Mathf.Max(0.25f, aggression));
        }

        return profile;
    }


    // Ownership of every post, as of the last plan. A post changing hands is
    // the event that invalidates a plan, so it is worth reacting to
    // immediately rather than up to ReplanInterval seconds later.
    int LastOwnershipSignature;

    // Ownership is polled rather than event-hooked: posts are created and
    // destroyed with the map, so subscribing would mean managing that
    // lifetime for a check this cheap.
    const float OwnershipPollInterval = 0.5f;
    float OwnershipPollTimer;

    void Update()
    {
        ReplanTimer -= Time.deltaTime;

        // Retire kill-site costs whose danger has aged out. Cheap - it scans a
        // handful of sources and only rebuilds arc costs when one actually
        // expires - and it has to happen somewhere with a frame, which the
        // graph itself does not have.
        PhxNavGraph.Instance.TickDynamicCosts();

        OwnershipPollTimer -= Time.deltaTime;
        bool postChangedHands = false;
        if (OwnershipPollTimer <= 0f)
        {
            OwnershipPollTimer = OwnershipPollInterval;

            int signature = ComputeOwnershipSignature();
            postChangedHands = signature != LastOwnershipSignature;
            LastOwnershipSignature = signature;
        }

        // A captured or lost post changes what every squad should be doing:
        // attackers need a new target, defenders a new post to hold. Waiting
        // out the rest of the interval is what made AI keep marching on ground
        // they already owned.
        if (ReplanTimer > 0f && !postChangedHands) return;

        ReplanTimer = ReplanInterval;
        Replan();
    }


    /// <summary>
    /// Give each squad member its own approach offset, from the formation
    /// BFSquadSystem chose.
    /// </summary>
    /// <remarks>
    /// This is what stops a squad moving as one body. The offsets are in the
    /// leader's frame at assignment time rather than continuously maintained -
    /// a rigid formation marched through a doorway looks worse than a loose
    /// one, and the planning graph already handles the routing. The point is
    /// that four soldiers approaching the same command post approach four
    /// slightly different points, so they spread across the objective instead
    /// of stacking on it.
    ///
    /// Additive with FlankOffset: the flank decides which way the squad swings
    /// wide, the slot decides where each member sits within it.
    /// </remarks>
    static void AssignFormationSlots(BFSquad squad)
    {
        if (squad == null || squad.Members.Count <= 1) return;

        squad.ElectLeader();
        squad.ChooseFormation();

        // The leader's facing is the frame the offsets are expressed in. Before
        // anyone has moved that is whichever way they spawned, which is good
        // enough - the offsets only need to differ from each other.
        Vector3 forward = Vector3.forward;
        if (squad.Leader != null && squad.Leader.Pawn?.GetInstance() != null)
        {
            Transform lt = squad.Leader.Pawn.GetInstance().transform;
            forward = lt.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
        }

        Quaternion frame = Quaternion.LookRotation(forward, Vector3.up);

        for (int i = 0; i < squad.Members.Count; ++i)
        {
            PhxBF3AIController member = squad.Members[i];
            if (member == null) continue;

            Vector3 slot = frame * squad.SlotOffset(i);
            member.FlankOffset += slot;
        }
    }

    static int ComputeOwnershipSignature()
    {
        PhxCommandpost[] posts = PhxGame.GetScene()?.GetCommandPosts();
        if (posts == null) return 0;

        int hash = 17;
        for (int i = 0; i < posts.Length; ++i)
        {
            hash = hash * 31 + (posts[i] != null ? posts[i].Team.Get() : -1);
        }
        return hash;
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
        BFSquadSystem.ClearSquads();
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

        // Elect leaders, choose formations and age the threat picture. Runs on
        // the replan cadence rather than per frame: none of it changes faster
        // than the grouping it describes.
        BFSquadSystem.Tick();

        // Feed the region memory from what the squads can currently see. This
        // is the point where an authored region stops being level-designer
        // annotation the AI ignores and becomes something it reasons about.
        foreach (PhxBF3AIController c in Controllers)
        {
            if (c == null || !c.HasVisibleTarget) continue;
            if (!c.TryGetTargetPosition(out Vector3 seenAt)) continue;

            PhxInstance self = c.Pawn?.GetInstance();
            if (self == null) continue;

            BFSquadSystem.NoteContact(self.Team.Get(), seenAt, c.GetTargetTeam());
        }
    }

    void AssignTeamSquads(int team, List<PhxBF3AIController> members, PhxCommandpost[] posts)
    {
        // Split the map into capturable targets and posts we own, and order
        // each list by how much it actually matters right now rather than by
        // whatever order the importer produced.
        //
        // This is the difference between a team that spreads evenly over every
        // flag and a team that reads the round: the evaluator weighs who is
        // standing on what, how central a post is to the map, and whether the
        // team is far enough behind that it should be consolidating instead of
        // pushing. Squads are handed objectives off the top of these lists, so
        // the first squads get the posts that matter most.
        List<PhxCommandpost> targets = new List<PhxCommandpost>();
        List<PhxCommandpost> owned = new List<PhxCommandpost>();

        IReadOnlyList<BFAIObjectiveEvaluator.PostAssessment> assessed =
            BFAIObjectiveEvaluator.Evaluate(team);

        if (assessed.Count > 0)
        {
            for (int i = 0; i < assessed.Count; ++i)
            {
                PhxCommandpost cp = assessed[i].Post;
                if (cp == null) continue;

                if (cp.Team != team) targets.Add(cp);
                else owned.Add(cp);
            }
        }
        else
        {
            foreach (PhxCommandpost cp in posts)
            {
                if (cp.Team != team) targets.Add(cp);
                else owned.Add(cp);
            }
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

        // How many squads defend vs attack. If the mission script declared AI
        // goals, honour their relative weights ("a goal with weight 2 gets
        // twice as many units as weight 1"); otherwise fall back to our own
        // ~1/3 defensive split.
        int defendSquads;
        Dictionary<PhxAIGoal, int> allocation = PhxAIGoals.Allocate(team, squadCount);
        if (allocation != null)
        {
            int defendShare = 0;
            foreach (KeyValuePair<PhxAIGoal, int> kv in allocation)
            {
                // Defend (and CTF defence) hold ground; Conquest/Destroy/
                // Deathmatch push forward.
                if (kv.Key.Type == PhxAIGoalType.Defend)
                {
                    defendShare += kv.Value;
                }
            }
            defendSquads = owned.Count > 0 ? Mathf.Min(defendShare, squadCount) : 0;
        }
        else
        {
            defendSquads = owned.Count > 0 ? Mathf.Max(squadCount / 3, squadCount > 1 ? 1 : 0) : 0;
        }

        bool sendBoarders = boardable != null && squadCount > 1;

        // Squads must be SPATIALLY coherent. This previously assigned members by
        // index stride (m += squadCount), which scatters a "squad" across the
        // whole map - they could never support each other, share a contact
        // inside radio range, or arrive anywhere together, which makes every
        // squad-level behaviour below meaningless. Group by proximity instead:
        // repeatedly take the furthest-forward unassigned soldier as a seed and
        // give it its nearest unassigned neighbours.
        List<List<PhxBF3AIController>> grouped = GroupByProximity(members, squadCount);

        for (int s = 0; s < squadCount; ++s)
        {
            PhxSquad squad = new PhxSquad();
            squad.Members.AddRange(grouped[s]);
            Squads.Add(squad);

            // Mirror the grouping into BFSquadSystem, which is what owns
            // leader, formation and the team's region threat picture. Kept as
            // a parallel view rather than a replacement: this director's own
            // squad logic works, and the tactical layer is advisory on top of
            // it rather than a rewrite of it.
            BFSquad tactical = BFSquadSystem.Create(team);
            tactical.Members.AddRange(grouped[s]);

            // Let members talk to each other (Stage 2 contact reports).
            foreach (PhxBF3AIController member in squad.Members)
            {
                member.Squad.Clear();
                member.Squad.AddRange(squad.Members);
            }

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
                // Detour perpendicular to the approach direction. WHICH side is
                // chosen by the danger map rather than a coin flip: a random
                // side is as likely to route the squad through the kill zone
                // that has been eating their team as around it.
                Vector3 from = squad.Members[0].Pawn.GetInstance().transform.position;
                Vector3 approach = squad.Objective.transform.position - from;
                Vector3 side = Vector3.Cross(approach.normalized, Vector3.up);
                float reach = Random.Range(25f, 60f);

                Vector3 leftProbe = from + approach * 0.5f - side * reach;
                Vector3 rightProbe = from + approach * 0.5f + side * reach;

                float leftDanger = PhxAIDanger.Sample(leftProbe, team);
                float rightDanger = PhxAIDanger.Sample(rightProbe, team);

                // Break ties randomly so squads don't all file down one side.
                float sign = leftDanger < rightDanger ? -1f
                           : rightDanger < leftDanger ? 1f
                           : (Random.value < 0.5f ? -1f : 1f);

                flankOffset = side * sign * reach;
            }

            foreach (PhxBF3AIController member in squad.Members)
            {
                member.AssignedObjective = squad.Objective;
                member.DefendObjective = null;
                member.BoardTarget = null;
                member.FlankOffset = flankOffset;
            }

            // AFTER the flank assignment, which uses "=" and would otherwise
            // wipe the slots. The flank decides which way the squad swings
            // wide; the slot decides where each member sits within it.
            //
            // Without this every member carries the SAME offset and routes to
            // the SAME point through the SAME hubs, so the squad computes one
            // identical path and walks it in a clump. That is the "troops
            // travel in packs" behaviour - not a pathfinding failure, an
            // absence of per-member destinations.
            AssignFormationSlots(tactical);
        }
    }

    /// <summary>
    /// Split <paramref name="members"/> into <paramref name="squadCount"/>
    /// groups of soldiers who are actually near each other.
    /// </summary>
    /// <remarks>
    /// Greedy nearest-neighbour clustering: pick an unassigned seed, then pull
    /// in the closest remaining soldiers until the squad is full. Not optimal
    /// clustering, but it runs once every replan over a few dozen units and
    /// only needs to beat "arbitrary index stride", which it does comfortably.
    /// </remarks>
    static List<List<PhxBF3AIController>> GroupByProximity(List<PhxBF3AIController> members, int squadCount)
    {
        List<List<PhxBF3AIController>> groups = new List<List<PhxBF3AIController>>(squadCount);
        List<PhxBF3AIController> pool = new List<PhxBF3AIController>(members);

        // Ceiling division, so no member is left over when it doesn't divide.
        int perSquad = Mathf.Max(1, Mathf.CeilToInt(members.Count / (float)squadCount));

        for (int s = 0; s < squadCount; ++s)
        {
            List<PhxBF3AIController> group = new List<PhxBF3AIController>();
            groups.Add(group);

            if (pool.Count == 0) continue;

            // Seed as far from the squads already formed as possible.
            //
            // Taking pool[0] made every squad's seed arbitrary, and at spawn -
            // when a whole team is stacked on one command post - every squad
            // then formed around nearly the same point. Farthest-point seeding
            // gives each squad a distinct centre even when everyone starts on
            // top of each other, so squads diverge immediately instead of
            // travelling as one body and separating later, if at all.
            int seedIdx = 0;
            if (s > 0)
            {
                float bestDist = -1f;
                for (int i = 0; i < pool.Count; ++i)
                {
                    Vector3 p = pool[i].PawnPosition();

                    // Distance to the nearest existing seed; maximise it.
                    float nearestSeed = float.MaxValue;
                    for (int g = 0; g < groups.Count - 1; ++g)
                    {
                        if (groups[g].Count == 0) continue;
                        float d = Vector3.SqrMagnitude(groups[g][0].PawnPosition() - p);
                        if (d < nearestSeed) nearestSeed = d;
                    }

                    if (nearestSeed > bestDist)
                    {
                        bestDist = nearestSeed;
                        seedIdx = i;
                    }
                }
            }

            PhxBF3AIController seed = pool[seedIdx];
            pool.RemoveAt(seedIdx);
            Vector3 origin = seed.PawnPosition();

            // Last squad takes everything remaining rather than stranding
            // anyone squadless.
            int want = (s == squadCount - 1) ? int.MaxValue : perSquad;

            while (group.Count < want && pool.Count > 0)
            {
                int nearest = 0;
                float nearestDist = float.MaxValue;
                for (int i = 0; i < pool.Count; ++i)
                {
                    float d = Vector3.SqrMagnitude(pool[i].PawnPosition() - origin);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearest = i;
                    }
                }
                group.Add(pool[nearest]);
                pool.RemoveAt(nearest);
            }
        }

        return groups;
    }

    PhxAISkillProfile GetSquadSkill(PhxSquad squad)
    {
        return squad.Members.Count > 0 ? squad.Members[0].Skill
                                       : PhxAISkillProfile.ForDifficulty(PhxBF3.Config.AIDifficulty);
    }
}
