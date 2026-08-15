using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Battlefield noise the AI can hear.
/// </summary>
/// <remarks>
/// Until now the only way an AI learned about an enemy was an unobstructed line
/// of sight, which makes them deaf: you could empty a magazine at a squad from
/// behind a crate and none of them would react until one happened to see you.
/// BF2's AI turns toward gunfire, so weapons and explosions publish a noise
/// event here and controllers sample it.
///
/// Events are kept in a small ring rather than dispatched through callbacks:
/// AI polls on its own retarget cadence (a few times a second), so pushing to
/// every controller on every shot would do far more work than it's worth in a
/// firefight with dozens of participants.
///
/// Static because the emitters (weapons, explosions) have no reference to the
/// AI layer, and there is only one battlefield.
/// </remarks>
public static class PhxAIPerception
{
    public struct Noise
    {
        public Vector3 Position;
        public int Team;         // who made it, so listeners ignore their own side
        public float Loudness;   // metres it carries
        public float Time;       // Time.time when it happened
    }

    // Ring buffer: noise is worthless once it's stale, so old entries are
    // overwritten rather than collected and swept.
    const int Capacity = 64;
    static readonly Noise[] Events = new Noise[Capacity];
    static int Next;

    /// <summary>How long a noise stays audible to a listener that hasn't sampled it yet.</summary>
    public const float Lifetime = 3f;

    /// <summary>Roughly how far a rifle shot carries. Config-driven.</summary>
    public static float GunshotLoudness => PhxBF3.Config.GunshotHearingRange;

    /// <summary>Explosions carry considerably further.</summary>
    public static float ExplosionLoudness => PhxBF3.Config.ExplosionHearingRange;

    public static void Report(Vector3 position, int team, float loudness)
    {
        Events[Next] = new Noise
        {
            Position = position,
            Team = team,
            Loudness = loudness,
            Time = Time.time,
        };
        Next = (Next + 1) % Capacity;
    }

    /// <summary>
    /// The most recent audible noise from someone other than <paramref name="listenerTeam"/>,
    /// or false if there is nothing worth reacting to.
    /// </summary>
    public static bool TryGetLoudest(Vector3 listenerPosition, int listenerTeam, out Noise result)
    {
        result = default;
        float now = Time.time;
        bool found = false;
        float bestScore = float.MinValue;

        for (int i = 0; i < Capacity; ++i)
        {
            Noise n = Events[i];
            if (n.Loudness <= 0f) continue;               // never written
            if (now - n.Time > Lifetime) continue;        // stale
            if (n.Team == listenerTeam) continue;         // our own guns

            float dist = Vector3.Distance(listenerPosition, n.Position);
            if (dist > n.Loudness) continue;              // out of earshot

            // Closer and louder wins; ties broken toward the more recent event.
            float score = (n.Loudness - dist) - (now - n.Time) * 10f;
            if (score > bestScore)
            {
                bestScore = score;
                result = n;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Drop everything. Called on map teardown.</summary>
    public static void Reset()
    {
        for (int i = 0; i < Capacity; ++i)
        {
            Events[i] = default;
        }
        Next = 0;
    }
}
