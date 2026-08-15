using UnityEngine;

/// <summary>
/// Finds cover from the geometry, for the times the map does not provide any.
/// </summary>
/// <remarks>
/// Cover was entirely authored: <see cref="PhxHintNodes"/> within 35 metres,
/// scanned every three seconds. Where a level designer placed nodes that is
/// excellent information and it stays preferred - an authored node knows things
/// geometry does not, like which side of a wall the fight is expected to come
/// from. But away from those nodes there was no cover at all, and two things
/// followed.
///
/// The soldier stood in the open, which is the visible half. The other half is
/// worse: <c>CoverAvailable</c> was set to "this soldier holds no hint node",
/// not to "cover exists". So the scorer was told cover was reachable wherever a
/// soldier happened to be standing, frequently chose SeekCover on the strength
/// of it, found no node, and did nothing. A soldier deciding to take cover and
/// then standing still is worse than one that never considered it, and it is a
/// large part of why the AI reads as stupid rather than as merely simple.
///
/// So: a geometric search, used when the authored nodes have nothing. The test
/// for a piece of cover is the one a person makes without thinking about it -
/// crouched behind this, does the thing shooting at me have a line to my chest,
/// and if I stand up again do I have a line back. Cover that fails the first
/// test is not cover; cover that fails the second is a hole to hide in, which
/// is where a soldier goes to be useless for the rest of the match.
/// </remarks>
public static class BFAICover
{
    /// <summary>Chest height of a crouched soldier - what must be protected.</summary>
    const float CrouchedChest = 0.85f;

    /// <summary>Eye height standing - what must still see out.</summary>
    const float StandingEye = 1.6f;

    /// <summary>Where a threat's shots come from.</summary>
    const float ThreatEye = 1.5f;

    /// <summary>Directions sampled around the soldier.</summary>
    const int Bearings = 8;

    /// <summary>How far down a candidate is allowed to be from the search ring.</summary>
    const float GroundProbe = 4f;

    /// <summary>Rings sampled outward. Near cover is worth more than far cover.</summary>
    static readonly float[] Rings = { 3f, 6.5f, 11f };

    /// <summary>
    /// Best cover position against a threat, or false if there is none.
    /// </summary>
    /// <param name="from">Where the soldier is now.</param>
    /// <param name="threat">Where the shooting is coming from.</param>
    /// <param name="maxRange">Furthest the soldier is willing to move for it.</param>
    /// <remarks>
    /// Costs at most Bearings * Rings ground probes and twice that many line
    /// tests - about seventy against a soldier layer mask, once per scan rather
    /// than per frame. Callers stagger their own scans.
    /// </remarks>
    public static bool TryFind(Vector3 from, Vector3 threat, float maxRange, out Vector3 spot)
    {
        spot = from;

        int mask = PhxLayers.SoldierGround;
        Vector3 threatEye = threat + Vector3.up * ThreatEye;

        // Away from the threat, in the plane. Candidates behind the soldier are
        // worth more than candidates alongside, and much more than candidates
        // toward the person shooting.
        Vector3 away = from - threat;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
        away.Normalize();

        float bestScore = float.MinValue;
        bool found = false;

        for (int ring = 0; ring < Rings.Length; ++ring)
        {
            float radius = Rings[ring];
            if (radius > maxRange) break;

            for (int b = 0; b < Bearings; ++b)
            {
                float angle = (Mathf.PI * 2f / Bearings) * b;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                Vector3 candidate = from + offset;

                if (!Standable(candidate, mask, out Vector3 ground)) continue;
                if (!Reachable(from, ground, mask)) continue;

                Vector3 chest = ground + Vector3.up * CrouchedChest;
                Vector3 eye = ground + Vector3.up * StandingEye;

                // Crouched, the threat must NOT have a line. This is the whole
                // definition of the thing.
                if (!Physics.Linecast(chest, threatEye, mask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                // Standing, the soldier must have one. Otherwise this is a pit,
                // and a soldier who goes there stops contributing.
                bool canPeek = !Physics.Linecast(eye, threatEye, mask,
                                                 QueryTriggerInteraction.Ignore);

                float score = 1f;

                // Cover you can shoot from is worth far more than cover you
                // cannot, but a hole still beats standing in the open when
                // things are bad, so it scores rather than disqualifies.
                score += canPeek ? 1.2f : 0.15f;

                // Nearer is better - every metre is time spent in the open
                // getting to it.
                score += Mathf.Clamp01(1f - radius / maxRange) * 0.8f;

                // Prefer moving away from the threat over sideways, and
                // sideways over toward.
                float bearing = Vector3.Dot(offset.normalized, away);
                score += bearing * 0.5f;

                if (score <= bestScore) continue;

                bestScore = score;
                spot = ground;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Ground under a candidate, if there is any within reach.</summary>
    /// <remarks>
    /// Probed from above rather than trusted, or a candidate on the far side of
    /// a railing sits in mid-air over a drop and the soldier walks off a
    /// catwalk to reach it.
    /// </remarks>
    static bool Standable(Vector3 candidate, int mask, out Vector3 ground)
    {
        ground = candidate;

        Vector3 above = candidate + Vector3.up * 2f;
        if (!Physics.Raycast(above, Vector3.down, out RaycastHit hit,
                             2f + GroundProbe, mask, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        // Too steep to stand on is not cover, it is a wall face.
        if (Vector3.Angle(hit.normal, Vector3.up) > 50f) return false;

        ground = hit.point;
        return true;
    }

    /// <summary>Whether the soldier could plausibly walk there.</summary>
    /// <remarks>
    /// A chest-height line rather than a path query. This is not pathfinding -
    /// it only rejects the cover on the other side of the wall the soldier is
    /// already behind, which is the case that otherwise sends them walking into
    /// it and standing there.
    /// </remarks>
    static bool Reachable(Vector3 from, Vector3 to, int mask)
    {
        Vector3 a = from + Vector3.up * CrouchedChest;
        Vector3 b = to + Vector3.up * CrouchedChest;

        return !Physics.Linecast(a, b, mask, QueryTriggerInteraction.Ignore);
    }
}
