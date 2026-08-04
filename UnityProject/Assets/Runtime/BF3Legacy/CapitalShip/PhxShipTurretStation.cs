using UnityEngine;

/// <summary>
/// Manned anti-fighter turret console in the capital ship hangar (the BF2 /
/// Elite Squadron hangar turrets). A soldier of the ship's team standing at
/// the console mans the linked external gun mounted on the hull; while manned
/// it engages enemy flyers and (as fallback) enemy soldiers outside.
///
/// The external gun is created on first use. Full seat/camera possession is a
/// TODO (needs PhxSeat integration); manning currently drives the gun
/// automatically while the console is occupied, which already matters
/// tactically: an occupied hangar keeps its air defense alive.
/// </summary>
public class PhxShipTurretStation : MonoBehaviour
{
    public PhxCapitalShip Ship;
    public Vector3 ExternalGunLocalPos;
    public float UseRadius = 3f;
    public float GunRange = 250f;
    public float GunDamagePerSecond = 60f;

    public PhxSoldier Operator { get; private set; }

    GameObject ExternalGun;
    LineRenderer Tracer;
    Component Target;             // PhxSoldier or PhxVehicle-ish component
    float RetargetTimer;

    static readonly Collider[] OverlapCache = new Collider[64];


    void Update()
    {
        if (Ship == null) return;

        UpdateOperator();

        bool active = Operator != null &&
                      Ship.State != PhxCapitalShip.PhxShipState.Dying &&
                      Ship.State != PhxCapitalShip.PhxShipState.Destroyed;
        if (!active)
        {
            if (Tracer != null) Tracer.enabled = false;
            return;
        }

        EnsureGun();

        RetargetTimer -= Time.deltaTime;
        if (RetargetTimer <= 0f)
        {
            RetargetTimer = 0.5f;
            AcquireTarget();
        }

        if (Target == null)
        {
            Tracer.enabled = false;
            return;
        }

        Vector3 aim = Target.transform.position;
        Tracer.enabled = true;
        Tracer.SetPosition(0, ExternalGun.transform.position);
        Tracer.SetPosition(1, aim);

        float dmg = GunDamagePerSecond * Time.deltaTime;
        if (Target is PhxSoldier soldier)
        {
            soldier.AddDamageFrom(dmg, ExternalGun.transform.position, isSaber: false);
        }
        else if (Target is IPhxDamageableInstance dmgable)
        {
            dmgable.AddDamage(dmg);
        }
    }

    void UpdateOperator()
    {
        // occupied while a living friendly soldier stands at the console
        if (Operator != null &&
            (Operator.IsDead || (Operator.transform.position - transform.position).magnitude > UseRadius))
        {
            Operator = null;
        }
        if (Operator != null) return;

        int count = Physics.OverlapSphereNonAlloc(transform.position, UseRadius, OverlapCache);
        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier != null && !soldier.IsDead && soldier.Team == Ship.Team)
            {
                Operator = soldier;
                return;
            }
        }
    }

    void EnsureGun()
    {
        if (ExternalGun != null) return;

        ExternalGun = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ExternalGun.name = "HangarDefenseGun";
        ExternalGun.transform.SetParent(Ship.transform, false);
        ExternalGun.transform.localPosition = ExternalGunLocalPos;
        ExternalGun.transform.localScale = new Vector3(2f, 3f, 2f);
        ExternalGun.GetComponent<Renderer>().material.color = new Color(0.6f, 0.62f, 0.68f);

        Tracer = ExternalGun.AddComponent<LineRenderer>();
        Tracer.startWidth = 0.25f;
        Tracer.endWidth = 0.25f;
        Tracer.material = new Material(Shader.Find("Sprites/Default"));
        Tracer.startColor = new Color(0.4f, 1f, 0.4f);
        Tracer.endColor = new Color(0.4f, 1f, 0.4f, 0.2f);
        Tracer.enabled = false;
    }

    void AcquireTarget()
    {
        Target = null;
        Vector3 origin = ExternalGun.transform.position;
        int count = Physics.OverlapSphereNonAlloc(origin, GunRange, OverlapCache);
        float best = float.MaxValue;

        for (int i = 0; i < count; ++i)
        {
            // prefer enemy flyers, fall back to enemy soldiers outside the ship
            PhxVehicle vehicle = OverlapCache[i].GetComponentInParent<PhxVehicle>();
            if (vehicle != null && vehicle.Team != Ship.Team && vehicle.Team != 0)
            {
                float d = (vehicle.transform.position - origin).sqrMagnitude;
                if (d < best) { best = d; Target = vehicle; }
                continue;
            }
            if (Target == null)
            {
                PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
                if (soldier != null && !soldier.IsDead && soldier.Team != Ship.Team && soldier.Team != 0)
                {
                    Target = soldier;
                }
            }
        }
    }
}
