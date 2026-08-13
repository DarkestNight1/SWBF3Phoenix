using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a squad knows about one region of the map.
/// </summary>
/// <remarks>
/// Regions are the level designer's own tactical annotation - they drew them
/// to say "this is the hangar", "this is the approach", "this is inside". The
/// importer reads them and the trigger system uses them, but the AI has never
/// looked at one. That is the difference between memory that is positional
/// ("I was shot at 41,0,207") and memory that is tactical ("the hangar is
/// dangerous"), and only the second survives the squad moving.
/// </remarks>
public struct BFRegionThreat
{
    public string RegionName;

    /// <summary>Rises on contact, decays with time. Unbounded above 0.</summary>
    public float Threat;

    /// <summary>When the threat was last reinforced, in Time.time.</summary>
    public float LastSeen;

    /// <summary>Which team the contact belonged to.</summary>
    public int HostileTeam;
}

/// <summary>Formation a squad holds while moving.</summary>
public enum BFSquadFormation
{
    /// <summary>No formation - everyone routes independently.</summary>
    None,
    /// <summary>Single file. Narrow ground, corridors, bridges.</summary>
    Column,
    /// <summary>Abreast. Open ground where everyone wants a firing line.</summary>
    Line,
    /// <summary>Spread around the leader. Default in the open.</summary>
    Wedge,
}

/// <summary>
/// A squad as an entity, rather than as a list of soldiers who happen to share
/// an objective.
/// </summary>
/// <remarks>
/// The director already groups soldiers by proximity and shared contacts and
/// hands them a flank assignment. What it does not have is anything that
/// persists: no leader, so there is nobody whose decision the others follow;
/// no formation, so a "squad" crossing open ground is four soldiers walking
/// the same line; and no memory, so the squad relearns the map every time it
/// respawns.
///
/// This is deliberately advisory. It computes a leader, a formation, a slot
/// offset per member and a threat picture, and exposes them. It does not drive
/// controllers directly - the existing state machine keeps ownership of
/// movement, and reads these as inputs. That keeps the working system working.
/// </remarks>
public sealed class BFSquad
{
    public readonly List<PhxBF3AIController> Members = new List<PhxBF3AIController>();

    /// <summary>
    /// Whose lead the squad follows. Recomputed as members die - a squad whose
    /// leader is a corpse is the most common way squad logic quietly stops.
    /// </summary>
    public PhxBF3AIController Leader { get; private set; }

    public BFSquadFormation Formation { get; private set; } = BFSquadFormation.Wedge;

    /// <summary>Which team this squad fights for. Set at creation.</summary>
    public int Team;

    /// <summary>
    /// Regions this squad believes are dangerous, worst first.
    /// </summary>
    /// <remarks>
    /// Stored per team rather than on the squad, because the director rebuilds
    /// its squad grouping from scratch on every replan. Memory that lived on
    /// the squad object would be discarded every few seconds, which is the
    /// same as having none. What a team has learned about a region outlives
    /// any particular grouping of soldiers.
    /// </remarks>
    public IReadOnlyList<BFRegionThreat> Threats => BFSquadSystem.ThreatsFor(Team);

    /// <summary>
    /// Pick the leader: the healthiest member with a live pawn, preferring the
    /// current one so leadership does not flicker between two equals.
    /// </summary>
    public void ElectLeader()
    {
        PhxBF3AIController best = null;
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < Members.Count; ++i)
        {
            PhxBF3AIController c = Members[i];
            if (c == null || !(c.Pawn is PhxSoldier s) || s == null) continue;

            float score = s.HealthFraction;
            // Hysteresis: the incumbent has to actually be worse to be replaced.
            if (ReferenceEquals(c, Leader)) score += 0.15f;

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        Leader = best;
    }

    /// <summary>
    /// Choose a formation from the ground the squad is standing on.
    /// </summary>
    /// <remarks>
    /// Indoors means a column, because a wedge in a corridor is four soldiers
    /// jammed in a doorway. This is the first thing in the AI that asks
    /// BFWorldQuery a question about the world rather than about a position.
    /// </remarks>
    public void ChooseFormation()
    {
        if (Leader == null || !(Leader.Pawn is PhxSoldier s) || s == null)
        {
            Formation = BFSquadFormation.None;
            return;
        }

        Vector3 at = s.transform.position;

        if (BFWorldQuery.IsIndoors(at))
        {
            Formation = BFSquadFormation.Column;
            return;
        }

        // A squad that is actively fighting wants a firing line; one that is
        // moving wants to be hard to catch with one grenade.
        Formation = Leader.HasVisibleTarget ? BFSquadFormation.Line : BFSquadFormation.Wedge;
    }

    /// <summary>
    /// Where member <paramref name="index"/> should sit relative to the leader,
    /// in the leader's own frame. Callers add this to the leader's position.
    /// </summary>
    public Vector3 SlotOffset(int index, float spacing = 4f)
    {
        if (index <= 0) return Vector3.zero;

        switch (Formation)
        {
            case BFSquadFormation.Column:
                return new Vector3(0f, 0f, -spacing * index);

            case BFSquadFormation.Line:
            {
                // Alternate sides so the line grows outward from the leader
                // rather than trailing off to one flank.
                int rank = (index + 1) / 2;
                float side = (index % 2 == 1) ? -1f : 1f;
                return new Vector3(side * spacing * rank, 0f, 0f);
            }

            case BFSquadFormation.Wedge:
            {
                int rank = (index + 1) / 2;
                float side = (index % 2 == 1) ? -1f : 1f;
                return new Vector3(side * spacing * rank, 0f, -spacing * rank);
            }

            default:
                return Vector3.zero;
        }
    }

    /// <summary>
    /// Record that a hostile was seen at a position, against whatever regions
    /// contain it.
    /// </summary>
    public void NoteContact(Vector3 where, int hostileTeam)
    {
        BFSquadSystem.NoteContact(Team, where, hostileTeam);
    }

    /// <summary>How dangerous the squad believes a position is. 0 when unknown.</summary>
    public float ThreatAt(Vector3 position)
    {
        return BFSquadSystem.ThreatAt(Team, position);
    }
}

/// <summary>
/// Per-match squad bookkeeping. Ticked by the director.
/// </summary>
public static class BFSquadSystem
{
    static readonly List<BFSquad> Squads = new List<BFSquad>();

    // Region threat per team, surviving squad regrouping. See BFSquad.Threats.
    static readonly Dictionary<int, List<BFRegionThreat>> TeamThreats =
        new Dictionary<int, List<BFRegionThreat>>();

    static readonly List<BFRegionThreat> NoThreats = new List<BFRegionThreat>();

    /// <summary>Threat decays to nothing over roughly this long without contact.</summary>
    public const float ThreatHalfLife = 20f;

    public static IReadOnlyList<BFSquad> All => Squads;

    /// <summary>Statics outlive a match; the director resets this on load.</summary>
    public static void Reset()
    {
        Squads.Clear();
        TeamThreats.Clear();
    }

    /// <summary>
    /// Drop the squad grouping but keep what each team has learned. Called
    /// when the director regroups, which it does every few seconds - wiping
    /// the threat picture here would make the memory useless.
    /// </summary>
    public static void ClearSquads()
    {
        Squads.Clear();
    }

    public static BFSquad Create(int team)
    {
        BFSquad s = new BFSquad { Team = team };
        Squads.Add(s);
        return s;
    }

    public static IReadOnlyList<BFRegionThreat> ThreatsFor(int team)
    {
        return TeamThreats.TryGetValue(team, out List<BFRegionThreat> list) ? list : NoThreats;
    }

    /// <summary>
    /// Record that team <paramref name="team"/> saw a hostile at a position,
    /// against whatever authored regions contain it.
    /// </summary>
    public static void NoteContact(int team, Vector3 where, int hostileTeam)
    {
        IReadOnlyList<PhxRegion> regions = BFWorldQuery.RegionsAt(where);
        if (regions == null || regions.Count == 0) return;

        if (!TeamThreats.TryGetValue(team, out List<BFRegionThreat> list))
        {
            TeamThreats[team] = list = new List<BFRegionThreat>();
        }

        for (int i = 0; i < regions.Count; ++i)
        {
            if (regions[i] == null) continue;
            AddThreat(list, regions[i].name, hostileTeam, 1f);
        }
    }

    static void AddThreat(List<BFRegionThreat> list, string region, int hostileTeam, float amount)
    {
        for (int i = 0; i < list.Count; ++i)
        {
            if (list[i].RegionName != region) continue;

            BFRegionThreat t = list[i];
            t.Threat += amount;
            t.LastSeen = Time.time;
            t.HostileTeam = hostileTeam;
            list[i] = t;
            return;
        }

        list.Add(new BFRegionThreat
        {
            RegionName = region,
            Threat = amount,
            LastSeen = Time.time,
            HostileTeam = hostileTeam,
        });
    }

    /// <summary>How dangerous team <paramref name="team"/> believes a position is.</summary>
    public static float ThreatAt(int team, Vector3 position)
    {
        if (!TeamThreats.TryGetValue(team, out List<BFRegionThreat> list) || list.Count == 0)
        {
            return 0f;
        }

        IReadOnlyList<PhxRegion> regions = BFWorldQuery.RegionsAt(position);
        if (regions == null || regions.Count == 0) return 0f;

        float worst = 0f;
        for (int i = 0; i < regions.Count; ++i)
        {
            if (regions[i] == null) continue;
            for (int j = 0; j < list.Count; ++j)
            {
                if (list[j].RegionName == regions[i].name)
                {
                    worst = Mathf.Max(worst, list[j].Threat);
                }
            }
        }
        return worst;
    }

    /// <summary>
    /// Age every team's threat picture. Without this a region stays dangerous
    /// forever on the strength of one contact ten minutes ago.
    /// </summary>
    static void DecayThreats()
    {
        float now = Time.time;
        foreach (var kv in TeamThreats)
        {
            List<BFRegionThreat> list = kv.Value;
            for (int i = list.Count - 1; i >= 0; --i)
            {
                BFRegionThreat t = list[i];
                float age = now - t.LastSeen;
                t.Threat *= Mathf.Pow(0.5f, age / ThreatHalfLife);

                if (t.Threat < 0.05f) list.RemoveAt(i);
                else list[i] = t;
            }
        }
    }

    /// <summary>
    /// Refresh leadership, formation and threat decay for every squad.
    /// Cheap enough to run at a low rate; the director owns the cadence.
    /// </summary>
    public static void Tick()
    {
        for (int i = Squads.Count - 1; i >= 0; --i)
        {
            BFSquad s = Squads[i];

            s.Members.RemoveAll(m => m == null || m.Pawn == null || m.Pawn.GetInstance() == null);
            if (s.Members.Count == 0)
            {
                Squads.RemoveAt(i);
                continue;
            }

            s.ElectLeader();
            s.ChooseFormation();
        }

        DecayThreats();
    }

    /// <summary>The squad a controller belongs to, or null.</summary>
    public static BFSquad SquadOf(PhxBF3AIController controller)
    {
        if (controller == null) return null;
        for (int i = 0; i < Squads.Count; ++i)
        {
            if (Squads[i].Members.Contains(controller)) return Squads[i];
        }
        return null;
    }
}
