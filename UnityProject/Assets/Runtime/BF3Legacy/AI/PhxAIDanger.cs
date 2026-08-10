using UnityEngine;

/// <summary>
/// Where soldiers keep dying, so the AI can stop walking into it.
/// </summary>
/// <remarks>
/// Stage 4. Squads previously picked a flank direction with
/// <c>Random.value &lt; 0.5f</c>, which is as likely to route them through the
/// kill zone as around it. Deaths are the cheapest available signal for "this
/// ground is dangerous" - no map analysis, no authored data, and it adapts to
/// how the round is actually going.
///
/// Deliberately coarse: a decaying ring of recent death sites, queried by
/// proximity. A real influence map would be better and is not worth the cost
/// here, where the only consumers are a flank-side choice and a retreat check.
/// </remarks>
public static class PhxAIDanger
{
    struct Death
    {
        public Vector3 Position;
        public int Team;      // who died there - danger is team-relative
        public float Time;
    }

    const int Capacity = 48;
    static readonly Death[] Events = new Death[Capacity];
    static int Next;

    /// <summary>How long a death keeps marking ground as dangerous.</summary>
    const float Lifetime = 25f;

    /// <summary>Radius a single death makes dangerous.</summary>
    const float Spread = 18f;

    public static void ReportDeath(Vector3 position, int team)
    {
        Events[Next] = new Death { Position = position, Team = team, Time = Time.time };
        Next = (Next + 1) % Capacity;

        // Also make the planning graph itself avoid the spot. Sampling danger
        // at a destination only tells a unit whether to go there; costing the
        // arcs is what changes the way it takes to get there, which is the
        // difference between "don't attack that post" and "attack it from the
        // other side".
        //
        // The cost is comparable to a short detour rather than a prohibitive
        // one: routes through recent kill sites should lose to reasonable
        // alternatives and still win over walking the length of the map.
        PhxNavGraph.Instance.AddDynamicCost(position, Spread, 40f, Lifetime);
    }

    /// <summary>
    /// How dangerous <paramref name="position"/> is for <paramref name="team"/>.
    /// 0 is clear; higher is worse. Only our own side's deaths count - the
    /// enemy dying somewhere makes it safer, not more dangerous.
    /// </summary>
    public static float Sample(Vector3 position, int team)
    {
        float now = Time.time;
        float danger = 0f;

        for (int i = 0; i < Capacity; ++i)
        {
            Death d = Events[i];
            if (d.Team != team) continue;

            float age = now - d.Time;
            if (age < 0f || age > Lifetime) continue;

            float dist = Vector3.Distance(position, d.Position);
            if (dist > Spread) continue;

            // Closer and more recent counts for more.
            danger += (1f - dist / Spread) * (1f - age / Lifetime);
        }

        return danger;
    }

    public static void Reset()
    {
        for (int i = 0; i < Capacity; ++i)
        {
            Events[i] = default;
        }
        Next = 0;
        PhxNavGraph.Instance.ClearDynamicCosts();
    }
}
