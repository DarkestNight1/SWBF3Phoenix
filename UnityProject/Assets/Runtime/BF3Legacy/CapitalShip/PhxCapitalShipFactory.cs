using UnityEngine;

/// <summary>
/// Spawns a complete greybox capital ship: hollow hull with the full walkable
/// Elite Squadron / BF3 interior (PhxShipInterior), external engine block, and
/// the destruction sequence pre-configured for atmosphere or deep space.
///
/// Shared by the BF3 map greybox builder and the vertical battlefront manager
/// (which puts ships over stock SWBF2 and BF3 Legacy maps at runtime).
/// </summary>
public static class PhxCapitalShipFactory
{
    public static PhxCapitalShip Spawn(int team, string shipName, Vector3 position,
                                       Vector3 facingTarget, bool inAtmosphere, Transform parent = null)
    {
        GameObject shipGo = new GameObject($"CapitalShip_Team{team}");
        if (parent != null) shipGo.transform.SetParent(parent, true);
        shipGo.transform.position = position;

        Vector3 look = facingTarget - position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            shipGo.transform.rotation = Quaternion.LookRotation(look.normalized);
        }

        PhxCapitalShip ship = shipGo.AddComponent<PhxCapitalShip>();
        ship.ShipName = shipName;
        ship.Team = team;

        // hull + full interior playspace (hangar, corridors, systems, reactor, bridge)
        PhxShipInterior.Build(ship);

        // external engine block at the stern - the one critical system
        // attackable from outside (strafe runs on the engines)
        GameObject engines = GameObject.CreatePrimitive(PrimitiveType.Cube);
        engines.name = "Engines";
        engines.transform.SetParent(shipGo.transform, false);
        engines.transform.localPosition = new Vector3(0f, 0f, PhxShipInterior.SternZ - 8f);
        engines.transform.localScale = new Vector3(PhxShipInterior.HullWidth * 0.7f, PhxShipInterior.HullHeight * 0.7f, 12f);
        PhxRuntimeAssets.Tint(engines, new Color(0.4f, 0.5f, 0.9f));
        PhxCapitalShipSubsystem engineSys = engines.AddComponent<PhxCapitalShipSubsystem>();
        engineSys.Type = PhxCapitalShipSubsystem.PhxSubsystemType.Engines;
        engineSys.IsCritical = true;
        engineSys.IsInternal = false;
        engineSys.MaxHealth = 3000f;
        ship.RegisterSubsystem(engineSys);

        PhxRuntimeAssets.CreatePointLight(engines, new Color(0.5f, 0.6f, 1f), 60f, 60000f);

        // destruction environment
        PhxCapitalShipDestruction seq = shipGo.GetComponent<PhxCapitalShipDestruction>();
        if (seq == null) seq = shipGo.AddComponent<PhxCapitalShipDestruction>();
        seq.InAtmosphere = inAtmosphere;

        return ship;
    }
}
