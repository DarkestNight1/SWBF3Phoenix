using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The one place an interaction is reported, and the one place it is
/// dispatched from.
/// </summary>
/// <remarks>
/// Callers report what happened; the system decides who hears about it. That
/// indirection is what lets a new responder (a grass field, a snow buffer, a
/// water body) start reacting to every existing event without any of those
/// events knowing it exists - and, just as importantly, lets the whole thing
/// be budgeted centrally.
///
/// Budgeting is not optional at Battlefront scale. Sixty-four soldiers walking
/// generates several hundred footsteps a second before a shot is fired, so the
/// system drops interactions that are too far from the camera or too small to
/// see, and rate-limits repeats from the same instigator.
/// </remarks>
public static class BFSurfaceInteractionSystem
{
    static readonly List<BFInteractionReceiver> Receivers = new List<BFInteractionReceiver>();
    static readonly Dictionary<int, float> LastReported = new Dictionary<int, float>();

    /// <summary>
    /// Beyond this from the camera, interactions are dropped outright. Nothing
    /// they drive - deformation, decals, particles - is visible at that range.
    /// </summary>
    public static float MaxInteractionDistance = 120f;

    /// <summary>
    /// Beyond this fraction of the max distance, only strong interactions get
    /// through. A footstep at 90 m is not worth a render-texture write;
    /// an explosion is.
    /// </summary>
    const float DistantStrengthThreshold = 1.0f;
    const float DistantFraction = 0.5f;

    public static int ReceiverCount => Receivers.Count;

    /// <summary>Interactions accepted since the last counter reset. Diagnostics.</summary>
    public static int Accepted { get; private set; }

    /// <summary>Interactions dropped by budget. Diagnostics.</summary>
    public static int Dropped { get; private set; }

    public static void Reset()
    {
        Receivers.Clear();
        LastReported.Clear();
        Accepted = 0;
        Dropped = 0;
    }

    public static void Register(BFInteractionReceiver receiver)
    {
        if (receiver == null || Receivers.Contains(receiver)) return;
        Receivers.Add(receiver);
    }

    public static void Unregister(BFInteractionReceiver receiver)
    {
        Receivers.Remove(receiver);
    }

    /// <summary>
    /// Report an interaction with explicit parameters.
    /// </summary>
    public static void Report(in BFSurfaceInteraction interaction)
    {
        if (!PassesBudget(interaction))
        {
            ++Dropped;
            return;
        }
        ++Accepted;

        for (int i = 0; i < Receivers.Count; ++i)
        {
            BFInteractionReceiver receiver = Receivers[i];
            if (receiver == null) continue;
            if (!receiver.Accepts(interaction.Source)) continue;

            Bounds bounds = receiver.InteractionBounds;
            // Grown by the interaction radius so an explosion just outside a
            // receiver still reaches into it.
            bounds.Expand(interaction.Radius * 2f);
            if (!bounds.Contains(interaction.Position)) continue;

            receiver.OnInteraction(interaction);
        }
    }

    /// <summary>
    /// Report using the source's default strength and radius, scaled.
    /// The common path - callers rarely have a reason to override both.
    /// </summary>
    public static void Report(BFInteractionSource source, Vector3 position, Vector3 normal,
                              Vector3 direction, BFSurfaceType surfaceType,
                              float scale = 1f, GameObject instigator = null)
    {
        BFInteractionProfile.Defaults defaults = BFInteractionProfile.For(source);

        if (defaults.Cooldown > 0f && instigator != null &&
            !PassesCooldown(instigator.GetInstanceID(), defaults.Cooldown))
        {
            ++Dropped;
            return;
        }

        Report(new BFSurfaceInteraction(position, normal, direction,
                                        defaults.Strength * scale,
                                        defaults.Radius * Mathf.Max(0.25f, scale),
                                        source, surfaceType, instigator));
    }

    /// <summary>
    /// Report from a raycast hit, resolving the surface automatically. The
    /// shortest path from "something hit something" to a full response.
    /// </summary>
    public static void ReportHit(BFInteractionSource source, RaycastHit hit, Vector3 direction,
                                 float scale = 1f, GameObject instigator = null)
    {
        Report(source, hit.point, hit.normal, direction,
               BFSurfaceQuery.Resolve(hit), scale, instigator);
    }

    static bool PassesCooldown(int instigatorId, float cooldown)
    {
        float now = Time.time;
        if (LastReported.TryGetValue(instigatorId, out float last) && now - last < cooldown)
        {
            return false;
        }
        LastReported[instigatorId] = now;
        return true;
    }

    static bool PassesBudget(in BFSurfaceInteraction interaction)
    {
        Camera camera = BFWorldQuery.Viewer;
        if (camera == null) return true;    // no view yet: don't second-guess

        float distance = Vector3.Distance(camera.transform.position, interaction.Position);
        if (distance > MaxInteractionDistance) return false;

        if (distance > MaxInteractionDistance * DistantFraction &&
            interaction.Strength < DistantStrengthThreshold)
        {
            return false;
        }
        return true;
    }

    /// <summary>Drop cooldown entries for instigators that no longer exist.</summary>
    public static void Prune()
    {
        if (LastReported.Count < 512) return;

        float cutoff = Time.time - 5f;
        var stale = new List<int>();
        foreach (KeyValuePair<int, float> entry in LastReported)
        {
            if (entry.Value < cutoff) stale.Add(entry.Key);
        }
        for (int i = 0; i < stale.Count; ++i)
        {
            LastReported.Remove(stale[i]);
        }
    }
}
