using UnityEngine;

/// <summary>
/// BF3's interior defense "autogun": a ceiling-mounted automated gun guarding
/// the capital ship's corridors (Free Radical replaced manned interior turrets
/// with these during development). Targets enemies of the ship's team within
/// range and line of sight.
///
/// All autoguns go offline when the ship's AutoTurretMainframe subsystem is
/// sabotaged - the BF2-heritage reason to hit that room first.
/// </summary>
public class PhxShipAutogun : MonoBehaviour
{
    public PhxCapitalShip Ship;
    public float Range = 35f;
    public float DamagePerSecond = 25f;
    public float RetargetInterval = 0.5f;

    PhxSoldier Target;
    float RetargetTimer;
    LineRenderer Beam;

    static readonly Collider[] OverlapCache = new Collider[64];


    void Start()
    {
        Beam = gameObject.AddComponent<LineRenderer>();
        Beam.startWidth = 0.08f;
        Beam.endWidth = 0.08f;
        Beam.material = new Material(Shader.Find("Sprites/Default"));
        Beam.startColor = new Color(1f, 0.3f, 0.2f);
        Beam.endColor = new Color(1f, 0.3f, 0.2f, 0.2f);
        Beam.enabled = false;
    }

    void Update()
    {
        if (Ship == null || !Ship.AutoTurretsOnline ||
            Ship.State == PhxCapitalShip.PhxShipState.Dying ||
            Ship.State == PhxCapitalShip.PhxShipState.Destroyed)
        {
            Beam.enabled = false;
            return;
        }

        RetargetTimer -= Time.deltaTime;
        if (RetargetTimer <= 0f)
        {
            RetargetTimer = RetargetInterval;
            AcquireTarget();
        }

        if (Target == null)
        {
            Beam.enabled = false;
            return;
        }

        Vector3 aim = Target.transform.position + Vector3.up * 1.2f;
        if (!HasLineOfSight(aim))
        {
            Target = null;
            Beam.enabled = false;
            return;
        }

        // sustained fire beam
        Beam.enabled = true;
        Beam.SetPosition(0, transform.position);
        Beam.SetPosition(1, aim);
        Target.AddDamageFrom(DamagePerSecond * Time.deltaTime, transform.position, isSaber: false);
    }

    void AcquireTarget()
    {
        Target = null;
        int count = Physics.OverlapSphereNonAlloc(transform.position, Range, OverlapCache);
        float best = float.MaxValue;
        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.Team == Ship.Team || soldier.Team == 0 || soldier.IsDead) continue;

            float d = (soldier.transform.position - transform.position).sqrMagnitude;
            if (d < best && HasLineOfSight(soldier.transform.position + Vector3.up * 1.2f))
            {
                best = d;
                Target = soldier;
            }
        }
    }

    bool HasLineOfSight(Vector3 point)
    {
        Vector3 dir = point - transform.position;
        if (Physics.Raycast(transform.position, dir.normalized, out RaycastHit hit, dir.magnitude))
        {
            return hit.collider.GetComponentInParent<PhxSoldier>() != null;
        }
        return true;
    }
}
