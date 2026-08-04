using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Utils;

/// <summary>
/// The stock 'turret' ODF class - emplaced guns placed on nearly every map
/// (anti-infantry towers, AA emplacements, the E-Web style guns). These were
/// previously unregistered, so none of them spawned.
///
/// Structurally these are stationary vehicles with a single TURRETSECTION
/// seat, which is exactly what PhxVehicleTurret already models; the class is
/// therefore a thin sibling of PhxArmedBuilding (which handles the
/// BUILDINGSECTION variant).
///
/// Per the mod tools documentation AI does not need hint nodes to use these -
/// turrets are technically vehicles and the AI treats them as occupiable
/// positions, which our PhxBF3AIController vehicle-mount path already covers.
/// </summary>
public class PhxTurret : PhxVehicle
{
    public class ClassProperties : PhxVehicleProperties { }

    PhxVehicleTurret TurretSection;


    public override void Init()
    {
        base.Init();
        SetupEnterTrigger();

        ModelMapping.ConvexifyMeshColliders(false);

        CurHealth.Set(C.MaxHealth.Get());

        var EC = C.EntityClass;
        EC.GetAllProperties(out uint[] properties, out string[] values);

        Seats = new List<PhxSeat>();

        int i = 0;
        while (i < properties.Length)
        {
            // Most turret odfs declare a single TURRETSECTION; some reuse the
            // BUILDINGSECTION/TURRET1 form, so accept either.
            if (properties[i] == HashUtils.GetFNV("TURRETSECTION"))
            {
                TurretSection = new PhxVehicleTurret(this, 0);
                TurretSection.InitManual(EC, i, "TURRETSECTION", values[i]);
                Seats.Add(TurretSection);
                break;
            }

            if (properties[i] == HashUtils.GetFNV("BUILDINGSECTION") &&
                values[i].Equals("TURRET1", StringComparison.OrdinalIgnoreCase))
            {
                TurretSection = new PhxVehicleTurret(this, 0);
                TurretSection.InitManual(EC, i, "BUILDINGSECTION", "TURRET1");
                Seats.Add(TurretSection);
                break;
            }

            i++;
        }

        if (TurretSection == null)
        {
            Debug.LogWarning($"Turret class '{C.Name}' has no TURRETSECTION/BUILDINGSECTION!");
        }

        SetIgnoredCollidersOnAllWeapons();
    }

    public override Vector3 GetCameraPosition()
    {
        return TurretSection != null ? TurretSection.GetCameraPosition() : transform.position;
    }

    public override Quaternion GetCameraRotation()
    {
        return TurretSection != null ? TurretSection.GetCameraRotation() : transform.rotation;
    }

    public override void Tick(float deltaTime)
    {
        UnityEngine.Profiling.Profiler.BeginSample("Tick Turret");
        base.Tick(deltaTime);
        TurretSection?.Tick(deltaTime);
        UnityEngine.Profiling.Profiler.EndSample();
    }

    public override void TickPhysics(float deltaTime) { }
}
