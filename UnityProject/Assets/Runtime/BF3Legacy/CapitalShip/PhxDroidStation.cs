using UnityEngine;

/// <summary>
/// Health/ammo droid station in a capital ship hangar (BF2/ES heritage - see
/// PhxShipInterior's docs). A lightweight greybox equivalent of the ODF-driven
/// PhxPowerupstation: no imported model or ODF properties, just a periodic
/// proximity check that resupplies whichever soldier is standing on it.
///
/// Not team-restricted, matching PhxEscapePod's precedent for these greybox
/// stations ("any team - stealing an enemy pod is a legitimate escape").
/// </summary>
public class PhxDroidStation : MonoBehaviour
{
    public bool IsHealthDroid = true;   // false = ammo droid
    public float Radius = 2.5f;

    /// <summary>Health per tick, for a health droid.</summary>
    public float AmountPerTick = 10f;

    /// <summary>
    /// Magazines per tick, for an ammo droid.
    /// </summary>
    /// <remarks>
    /// Separate from AmountPerTick because the two are not in the same unit -
    /// health is absolute HP and ammo is clips, the same split the stock
    /// powerupstation odfs use (soldierhealth 25.0 against soldierammo 1.0).
    /// Feeding the health figure to AddAmmo asked for ten magazines a second.
    /// </remarks>
    public float MagazinesPerTick = 0.5f;
    public float TickInterval = 1f;

    static readonly Collider[] OverlapCache = new Collider[16];

    float TickTimer;

    void Update()
    {
        TickTimer -= Time.deltaTime;
        if (TickTimer > 0f) return;
        TickTimer = TickInterval;

        int count = Physics.OverlapSphereNonAlloc(transform.position, Radius, OverlapCache);
        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.IsDead) continue;

            if (IsHealthDroid)
            {
                soldier.AddHealth(AmountPerTick);
            }
            else
            {
                soldier.AddAmmo(MagazinesPerTick);
            }
        }
    }
}
