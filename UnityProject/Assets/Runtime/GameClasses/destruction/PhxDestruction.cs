using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What kind of thing a destructible is, for consumers that care about the
/// distinction without wanting to know the concrete type.
/// </summary>
public enum PhxDestructibleKind
{
    Vehicle,
    Building,
    ShipSubsystem,
    MissionObject,
}

/// <summary>
/// Anything with health that can be destroyed and that the rest of the game
/// wants to ask about.
/// </summary>
/// <remarks>
/// Before this, "how damaged is that thing" had a different answer per type -
/// <c>PhxVehicle.CurHealth</c>, <c>PhxDestructableBuilding.CurHealth</c>,
/// <c>PhxCapitalShipSubsystem.CurHealth</c> - each private to its own file and
/// each with its own destruction path. Every consumer that needed the state
/// (HUD damage readouts, mission objectives, scoring, AI target selection)
/// therefore had to know every concrete type, and the BF3 capital-ship layer
/// ended up additive and separate rather than a case of the same model.
///
/// The interface is deliberately read-only apart from damage: destruction
/// stays the owner's decision, since only the owner knows what its death
/// involves (ejecting crew, swapping to a destroyed mesh, ungating a reactor).
/// </remarks>
public interface IPhxDestructible
{
    PhxDestructibleKind DestructibleKind { get; }

    /// <summary>The scene object this is attached to. Never null.</summary>
    GameObject GetGameObject();

    /// <summary>Human-facing name, which is what scripts address it by.</summary>
    string GetDestructibleName();

    int GetTeam();
    float GetHealth();
    float GetMaxHealth();
    bool IsDestroyed { get; }

    void AddDamage(float damage);
}

/// <summary>
/// Every destructible currently in the scene, and the one place to be told
/// when one dies.
/// </summary>
/// <remarks>
/// Registry rather than a scene search: HUD, objectives and AI all want this
/// list every frame or on every kill, and <c>FindObjectsOfType</c> per
/// consumer per frame is not an option at map scale.
/// </remarks>
public static class PhxDestructionRegistry
{
    static readonly List<IPhxDestructible> Live = new List<IPhxDestructible>();

    /// <summary>Raised after something is destroyed, whatever kind it was.</summary>
    public static event Action<IPhxDestructible> OnAnyDestroyed;

    public static IReadOnlyList<IPhxDestructible> All => Live;

    public static void Reset()
    {
        Live.Clear();
        OnAnyDestroyed = null;
    }

    public static void Register(IPhxDestructible destructible)
    {
        if (destructible == null || Live.Contains(destructible)) return;
        Live.Add(destructible);
    }

    public static void Unregister(IPhxDestructible destructible)
    {
        if (destructible == null) return;
        Live.Remove(destructible);
    }

    /// <summary>Owners call this once, after their own death handling.</summary>
    public static void NotifyDestroyed(IPhxDestructible destructible)
    {
        if (destructible == null) return;

        Live.Remove(destructible);
        OnAnyDestroyed?.Invoke(destructible);
    }

    /// <summary>
    /// Nearest live destructible of a kind that is hostile to
    /// <paramref name="team"/>, for AI target selection and objective HUD.
    /// </summary>
    public static IPhxDestructible FindNearestHostile(Vector3 from, int team,
                                                      PhxDestructibleKind kind, float maxRange)
    {
        IPhxDestructible best = null;
        float bestDist = maxRange * maxRange;

        for (int i = 0; i < Live.Count; ++i)
        {
            IPhxDestructible candidate = Live[i];
            if (candidate == null || candidate.IsDestroyed) continue;
            if (candidate.DestructibleKind != kind) continue;

            int otherTeam = candidate.GetTeam();
            if (team > 0 && otherTeam == team) continue;

            GameObject obj = candidate.GetGameObject();
            if (obj == null) continue;

            float d = (obj.transform.position - from).sqrMagnitude;
            if (d >= bestDist) continue;

            bestDist = d;
            best = candidate;
        }
        return best;
    }
}
