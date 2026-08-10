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
    public float AmountPerTick = 10f;
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
                soldier.AddAmmo(AmountPerTick);
            }
        }
    }
}
