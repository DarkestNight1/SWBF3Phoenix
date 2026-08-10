using System.Collections.Generic;
using UnityEngine;

/// <summary>What caused an interaction with the world.</summary>
public enum BFInteractionSource
{
    Unknown = 0,
    Footstep,
    BlasterImpact,
    Explosion,
    Vehicle,
    Projectile,
    CharacterMovement,
    WaterEntry,
    Weather,
    Landing,
}

/// <summary>
/// One thing touching the world, described in terms every responder can use.
/// </summary>
/// <remarks>
/// The unifying idea of the presentation layer: a boot on snow, a bolt on
/// metal, a grenade in mud and rain on a hangar deck are all the same shape of
/// event - something arrived somewhere, from a direction, with some force,
/// over some area - and what differs is the surface's answer, not the caller's
/// question.
///
/// A struct so that a firefight's worth of these costs no allocations.
/// </remarks>
public readonly struct BFSurfaceInteraction
{
    public readonly Vector3 Position;

    /// <summary>Surface normal at the contact point.</summary>
    public readonly Vector3 Normal;

    /// <summary>Travel direction of whatever arrived, normalized.</summary>
    public readonly Vector3 Direction;

    /// <summary>Relative magnitude, 1 being a typical instance of its source.</summary>
    public readonly float Strength;

    /// <summary>Area affected, in metres.</summary>
    public readonly float Radius;

    public readonly BFInteractionSource Source;
    public readonly BFSurfaceType SurfaceType;

    /// <summary>What caused it, when there is an object to credit. May be null.</summary>
    public readonly GameObject Instigator;

    public BFSurfaceInteraction(Vector3 position, Vector3 normal, Vector3 direction,
                                float strength, float radius,
                                BFInteractionSource source, BFSurfaceType surfaceType,
                                GameObject instigator = null)
    {
        Position = position;
        Normal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
        Direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : -Normal;
        Strength = strength;
        Radius = radius;
        Source = source;
        SurfaceType = surfaceType;
        Instigator = instigator;
    }

    public BFSurfaceProfile Profile => BFSurfaceProfile.Get(SurfaceType);

    /// <summary>
    /// How glancing the arrival was, 0 head-on and 1 parallel to the surface.
    /// Sparks fly along the surface at grazing angles and away from it at
    /// steep ones, so most responders need this.
    /// </summary>
    public float Grazing => 1f - Mathf.Abs(Vector3.Dot(Direction, Normal));

    /// <summary>
    /// Direction debris should leave in: the incoming direction reflected off
    /// the surface, which is what makes sparks read as coming off the hit
    /// rather than out of it.
    /// </summary>
    public Vector3 Deflection => Vector3.Reflect(Direction, Normal);
}

/// <summary>
/// Something that wants to be told about interactions near it - a terrain
/// deformation buffer, a water body, a grass field.
/// </summary>
public interface BFInteractionReceiver
{
    /// <summary>World-space bounds this receiver cares about.</summary>
    Bounds InteractionBounds { get; }

    /// <summary>Which sources it responds to; others are not delivered.</summary>
    bool Accepts(BFInteractionSource source);

    void OnInteraction(in BFSurfaceInteraction interaction);
}

/// <summary>
/// Per-source tuning: how strong a given kind of event is by default, and how
/// far it reaches.
/// </summary>
/// <remarks>
/// Callers should not have to know that a footstep is "0.35 strength over
/// 0.3 metres" - they know they took a step. This turns the event they know
/// about into the numbers responders need, in one place, so the relative
/// weight of a footstep against an explosion is a single readable table
/// rather than an emergent property of a dozen call sites.
/// </remarks>
public static class BFInteractionProfile
{
    public struct Defaults
    {
        public float Strength;
        public float Radius;

        /// <summary>Minimum seconds between two of these from one instigator.</summary>
        public float Cooldown;
    }

    static readonly Dictionary<BFInteractionSource, Defaults> Table =
        new Dictionary<BFInteractionSource, Defaults>
    {
        { BFInteractionSource.Footstep,          new Defaults { Strength = 0.35f, Radius = 0.30f, Cooldown = 0.15f } },
        { BFInteractionSource.CharacterMovement, new Defaults { Strength = 0.15f, Radius = 0.60f, Cooldown = 0.25f } },
        { BFInteractionSource.Landing,           new Defaults { Strength = 1.10f, Radius = 0.80f, Cooldown = 0.20f } },
        { BFInteractionSource.BlasterImpact,     new Defaults { Strength = 0.60f, Radius = 0.35f, Cooldown = 0f } },
        { BFInteractionSource.Projectile,        new Defaults { Strength = 0.90f, Radius = 0.60f, Cooldown = 0f } },
        { BFInteractionSource.Explosion,         new Defaults { Strength = 3.00f, Radius = 4.00f, Cooldown = 0f } },
        { BFInteractionSource.Vehicle,           new Defaults { Strength = 1.50f, Radius = 1.60f, Cooldown = 0.10f } },
        { BFInteractionSource.WaterEntry,        new Defaults { Strength = 1.00f, Radius = 1.20f, Cooldown = 0.10f } },
        { BFInteractionSource.Weather,           new Defaults { Strength = 0.05f, Radius = 0.10f, Cooldown = 0f } },
    };

    public static Defaults For(BFInteractionSource source)
    {
        return Table.TryGetValue(source, out Defaults defaults)
            ? defaults
            : new Defaults { Strength = 1f, Radius = 1f };
    }
}
