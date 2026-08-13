using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Galactic Conquest state.
/// </summary>
/// <remarks>
/// <b>Structural scaffolding. Nothing calls into this yet and nothing should
/// until the shell UI exists.</b> <see cref="Enabled"/> is false and every
/// entry point returns without effect while it is.
///
/// GC is not a game mode in the sense conquest is - it is a meta-layer that
/// picks which map runs next and carries state between runs. That state is the
/// whole feature: a planet map with ownership, a fleet, credits earned per
/// victory and spent on bonuses that apply to the next battle. The battles
/// themselves are ordinary instant-action matches, which is why this can be
/// built without touching the match code.
///
/// What is deliberately NOT here: persistence to disk, the planet-map UI, and
/// any hook into PhxGame's map loading. Those are the parts that would change
/// runtime behaviour, and they belong with the shell work rather than with the
/// data model.
/// </remarks>
public static class BFGalacticConquest
{
    /// <summary>
    /// Master switch. While false every method here is a no-op, so the type
    /// can exist in a shipping build without being reachable.
    /// </summary>
    public static bool Enabled { get; private set; }

    public sealed class BFPlanet
    {
        public string Name;

        /// <summary>Map script this planet plays, e.g. "tat1c_con".</summary>
        public string MapLuaFile;

        /// <summary>Team that currently holds it; 0 for neutral.</summary>
        public int Owner;

        /// <summary>Planets reachable from this one, by name.</summary>
        public readonly List<string> Connections = new List<string>();

        /// <summary>Credits awarded for taking it.</summary>
        public int Value = 100;
    }

    public sealed class BFFaction
    {
        public int Team;
        public int Credits;

        /// <summary>Bonus ids bought and still owned.</summary>
        public readonly List<string> Bonuses = new List<string>();

        /// <summary>Planets held. Kept as names so it survives a rebuild.</summary>
        public readonly List<string> Holdings = new List<string>();
    }

    public sealed class BFCampaignState
    {
        public readonly Dictionary<string, BFPlanet> Planets =
            new Dictionary<string, BFPlanet>();

        public readonly Dictionary<int, BFFaction> Factions =
            new Dictionary<int, BFFaction>();

        public int Turn;

        /// <summary>Team whose move it is.</summary>
        public int ActiveTeam;
    }

    public static BFCampaignState State { get; private set; }

    /// <summary>
    /// Begin a campaign. Does nothing while <see cref="Enabled"/> is false.
    /// </summary>
    public static void Begin(BFCampaignState initial)
    {
        if (!Enabled) return;
        State = initial;
    }

    public static void Reset()
    {
        State = null;
    }

    /// <summary>
    /// Which planets <paramref name="team"/> could attack this turn: anything
    /// it does not own that borders something it does.
    /// </summary>
    public static List<BFPlanet> GetAttackable(int team)
    {
        var result = new List<BFPlanet>();
        if (!Enabled || State == null) return result;
        if (!State.Factions.TryGetValue(team, out BFFaction faction)) return result;

        foreach (string held in faction.Holdings)
        {
            if (!State.Planets.TryGetValue(held, out BFPlanet from)) continue;

            foreach (string neighbour in from.Connections)
            {
                if (!State.Planets.TryGetValue(neighbour, out BFPlanet to)) continue;
                if (to.Owner == team || result.Contains(to)) continue;
                result.Add(to);
            }
        }
        return result;
    }

    /// <summary>
    /// Apply the outcome of a battle. The caller owns actually running it.
    /// </summary>
    public static void ResolveBattle(string planetName, int winningTeam)
    {
        if (!Enabled || State == null) return;
        if (!State.Planets.TryGetValue(planetName, out BFPlanet planet)) return;

        int previousOwner = planet.Owner;
        planet.Owner = winningTeam;

        if (State.Factions.TryGetValue(previousOwner, out BFFaction loser))
        {
            loser.Holdings.Remove(planetName);
        }
        if (State.Factions.TryGetValue(winningTeam, out BFFaction winner))
        {
            if (!winner.Holdings.Contains(planetName)) winner.Holdings.Add(planetName);
            winner.Credits += planet.Value;
        }

        State.Turn++;
    }

    /// <summary>True when one team holds every planet.</summary>
    public static bool IsCampaignOver(out int winningTeam)
    {
        winningTeam = 0;
        if (!Enabled || State == null || State.Planets.Count == 0) return false;

        int first = 0;
        bool firstSet = false;
        foreach (var kv in State.Planets)
        {
            int owner = kv.Value.Owner;
            if (owner == 0) return false;
            if (!firstSet) { first = owner; firstSet = true; }
            else if (owner != first) return false;
        }

        winningTeam = first;
        return firstSet;
    }
}
