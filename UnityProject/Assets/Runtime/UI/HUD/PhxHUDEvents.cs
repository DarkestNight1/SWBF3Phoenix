using System;
using UnityEngine;

/// <summary>
/// Combat notifications the HUD needs but has no way to observe on its own.
///
/// Damage is resolved deep inside the ordnance/soldier code, which knows who
/// fired and who was hit but nothing about the UI. Polling can't recover this:
/// a hit is an instant, and by the next frame the only trace left is a health
/// value that may have changed for several reasons at once. So the damage path
/// reports the two things the player's HUD reacts to, and anything interested
/// subscribes.
///
/// Static because the HUD is created and destroyed with each map while the
/// damage path is not, and because there is only ever one local player.
/// Subscribers must unsubscribe in OnDestroy or they leak across map loads.
/// </summary>
public static class PhxHUDEvents
{
    /// <summary>
    /// The local player hit something. The flag is true when the hit killed it,
    /// which BF2 marks differently from an ordinary hit.
    /// </summary>
    public static event Action<bool> OnLocalPlayerDealtDamage;

    /// <summary>
    /// The local player took damage. Carries the world position the damage came
    /// from, so the HUD can point the indicator at it.
    /// </summary>
    public static event Action<Vector3> OnLocalPlayerDamaged;

    public static void ReportDealtDamage(bool fatal)
    {
        OnLocalPlayerDealtDamage?.Invoke(fatal);
    }

    public static void ReportDamageTaken(Vector3 sourcePosition)
    {
        OnLocalPlayerDamaged?.Invoke(sourcePosition);
    }

    /// <summary>
    /// Drop every subscriber. Called on map teardown: a HUD from the previous
    /// map that failed to unsubscribe would otherwise keep receiving events
    /// and touching destroyed objects.
    /// </summary>
    public static void Reset()
    {
        OnLocalPlayerDealtDamage = null;
        OnLocalPlayerDamaged = null;
    }
}
