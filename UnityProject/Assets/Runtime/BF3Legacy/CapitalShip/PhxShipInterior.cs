using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds the walkable interior playspace of a capital ship, laid out to match
/// what is documented of Elite Squadron's and Free Radical BF3's boarding runs:
///
///   - Docking bay / hangar at the bow, entered through a shielded mouth once
///     the ship's shields are down. Health/ammo droid stations and manned
///     anti-fighter turret alcoves line the hangar (BF2/ES heritage).
///   - Behind it a junction lobby with ESCAPE POD bays port and starboard -
///     BF3's design let you blow the reactor and watch the destruction from a
///     pod; ES had you "rush back to the docking bay or an escape pod".
///   - Twin narrow corridors run aft (ES's camera-hostile hallways), guarded
///     by ceiling AUTOGUNS (BF3's interior defense, which replaced manned
///     interior turrets during development).
///   - Between the corridors: the critical systems rooms - shield generator,
///     auto-turret mainframe, life support (BF2 space assault heritage,
///     gating the reactor).
///   - Aft: the large REACTOR CHAMBER with the reactor core column - the kill
///     objective ("keep throwing grenades and shooting at it until the ship
///     is destroyed").
///   - Sternmost: the BRIDGE (BF3 leaked build: land in the hangar, fight
///     your way to the bridge), placed as the deepest compartment.
///
/// Geometry is greybox plates; outer shell plates are tagged as break
/// sections so the destruction sequence tears the hull apart. All dimensions
/// in meters, ship-local space: +Z = bow, deck floor at y = -8.
/// </summary>
public static class PhxShipInterior
{
    // Hull envelope
    public const float HullWidth = 64f;
    public const float HullHeight = 22f;
    public const float BowZ = 135f;
    public const float SternZ = -175f;
    const float FloorY = -8f;
    const float CeilY = 8f;
    const float Wall = 1f;   // plate thickness

    // Hangar mouth (opening in the bow face)
    const float MouthWidth = 30f;
    const float MouthHeight = 12f;

    public static void Build(PhxCapitalShip ship)
    {
        GameObject root = new GameObject("Interior");
        root.transform.SetParent(ship.transform, false);
        Transform t = root.transform;

        float midY = (FloorY + CeilY) * 0.5f;   // 0
        float innerH = CeilY - FloorY;          // 16
        float halfW = HullWidth * 0.5f;         // 32
        float lengthZ = BowZ - SternZ;          // 310
        float midZ = (BowZ + SternZ) * 0.5f;

        // ================= outer shell (break sections) =================
        Shell(t, "hull_floor",   new Vector3(0f, FloorY - Wall, midZ), new Vector3(HullWidth, Wall * 2f, lengthZ));
        Shell(t, "hull_roof",    new Vector3(0f, CeilY + Wall, midZ),  new Vector3(HullWidth, Wall * 2f, lengthZ));
        Shell(t, "hull_port",    new Vector3(-halfW, midY, midZ),      new Vector3(Wall * 2f, HullHeight, lengthZ));
        Shell(t, "hull_stbd",    new Vector3(halfW, midY, midZ),       new Vector3(Wall * 2f, HullHeight, lengthZ));
        Shell(t, "hull_stern",   new Vector3(0f, midY, SternZ),        new Vector3(HullWidth, HullHeight, Wall * 2f));

        // bow face with hangar mouth opening: 4 plates around a MouthWidth x MouthHeight hole
        float sideW = (HullWidth - MouthWidth) * 0.5f;
        Shell(t, "hull_bow_port", new Vector3(-(MouthWidth + sideW) * 0.5f, midY, BowZ), new Vector3(sideW, HullHeight, Wall * 2f));
        Shell(t, "hull_bow_stbd", new Vector3((MouthWidth + sideW) * 0.5f, midY, BowZ),  new Vector3(sideW, HullHeight, Wall * 2f));
        float mouthTopH = CeilY - (FloorY + MouthHeight);
        Shell(t, "hull_bow_top",  new Vector3(0f, FloorY + MouthHeight + mouthTopH * 0.5f, BowZ), new Vector3(MouthWidth, mouthTopH, Wall * 2f));

        // hangar shield: translucent barrier in the mouth, dropped with the shields
        GameObject shield = GameObject.CreatePrimitive(PrimitiveType.Cube);
        shield.name = "HangarShield";
        shield.transform.SetParent(t, false);
        shield.transform.localPosition = new Vector3(0f, FloorY + MouthHeight * 0.5f, BowZ);
        shield.transform.localScale = new Vector3(MouthWidth, MouthHeight, 0.5f);
        Renderer sr = shield.GetComponent<Renderer>();
        sr.material.color = new Color(0.4f, 0.7f, 1f, 0.35f);
        ship.HangarShieldVisual = shield;

        // ================= hangar (z 60..135) =================
        // rear wall separating hangar from junction, door gap centered
        WallX(t, "hangar_rear", 60f, doorHalfWidth: 5f);
        RoomLight(t, new Vector3(0f, CeilY - 1f, 100f), 40f);

        // manned anti-fighter turret stations in the side alcoves
        TurretStation(ship, t, new Vector3(-halfW + 4f, FloorY, 110f), lookRight: false);
        TurretStation(ship, t, new Vector3(halfW - 4f, FloorY, 110f), lookRight: true);

        // health / ammo droid stations at the hangar rear (ES/BF2 heritage)
        DroidStation(t, "health_droid", new Vector3(-10f, FloorY, 64f), new Color(0.9f, 0.25f, 0.2f));
        DroidStation(t, "ammo_droid", new Vector3(10f, FloorY, 64f), new Color(0.9f, 0.8f, 0.2f));

        // defender spawn pad
        SpawnPad(ship, t, new Vector3(0f, FloorY, 70f));

        // hangar entrance marker for AI boarding / landing guidance
        GameObject hangarEntrance = new GameObject("HangarEntrance");
        hangarEntrance.transform.SetParent(t, false);
        hangarEntrance.transform.localPosition = new Vector3(0f, FloorY + 3f, BowZ - 10f);
        ship.HangarEntrance = hangarEntrance.transform;

        // ================= junction + escape pods (z 40..60) =================
        WallX(t, "junction_rear", 40f, doorHalfWidth: 5f);
        RoomLight(t, new Vector3(0f, CeilY - 1f, 50f), 25f);

        EscapePodBay(ship, t, new Vector3(-halfW + 5f, FloorY, 45f));
        EscapePodBay(ship, t, new Vector3(-halfW + 5f, FloorY, 55f));
        EscapePodBay(ship, t, new Vector3(halfW - 5f, FloorY, 45f));
        EscapePodBay(ship, t, new Vector3(halfW - 5f, FloorY, 55f));

        // ================= twin corridors (z -60..40), center systems rooms =================
        // corridor outer bounds are the hull sides; inner walls at x = +-14
        // with door gaps into the three center rooms
        WallZ(t, "corr_port_inner", -14f, -60f, 40f, doorsAtZ: new float[] { 20f, -15f, -50f });
        WallZ(t, "corr_stbd_inner", 14f, -60f, 40f, doorsAtZ: new float[] { 20f, -15f, -50f });

        // center room dividers
        WallX(t, "sys_div_1", 0f, doorHalfWidth: 2.5f, xFrom: -14f, xTo: 14f);
        WallX(t, "sys_div_2", -35f, doorHalfWidth: 2.5f, xFrom: -14f, xTo: 14f);

        // critical systems (gate the reactor, BF2 space-assault style)
        SubsystemConsole(ship, t, "ShieldGenerator", PhxCapitalShipSubsystem.PhxSubsystemType.ShieldGenerator,
            new Vector3(0f, FloorY + 2f, 20f), new Color(0.3f, 0.7f, 1f));
        SubsystemConsole(ship, t, "AutoTurretMainframe", PhxCapitalShipSubsystem.PhxSubsystemType.AutoTurretMainframe,
            new Vector3(0f, FloorY + 2f, -17f), new Color(1f, 0.6f, 0.2f));
        SubsystemConsole(ship, t, "LifeSupport", PhxCapitalShipSubsystem.PhxSubsystemType.LifeSupport,
            new Vector3(0f, FloorY + 2f, -52f), new Color(0.4f, 1f, 0.5f));

        // interior defense autoguns covering the corridors (BF3's autogun)
        Autogun(ship, t, new Vector3(-23f, CeilY - 1f, 10f));
        Autogun(ship, t, new Vector3(23f, CeilY - 1f, 10f));
        Autogun(ship, t, new Vector3(-23f, CeilY - 1f, -45f));
        Autogun(ship, t, new Vector3(23f, CeilY - 1f, -45f));

        RoomLight(t, new Vector3(-23f, CeilY - 1f, -10f), 20f);
        RoomLight(t, new Vector3(23f, CeilY - 1f, -10f), 20f);
        RoomLight(t, new Vector3(0f, CeilY - 1f, -10f), 18f);

        // ================= reactor chamber (z -130..-60) =================
        WallX(t, "reactor_fore", -60f, doorHalfWidth: 4f);
        WallX(t, "reactor_rear", -130f, doorHalfWidth: 3f);
        RoomLight(t, new Vector3(0f, CeilY - 1f, -95f), 35f);

        // the reactor core column - the kill objective
        GameObject core = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        core.name = "MainReactor";
        core.transform.SetParent(t, false);
        core.transform.localPosition = new Vector3(0f, midY, -95f);
        core.transform.localScale = new Vector3(8f, innerH * 0.5f, 8f);
        core.GetComponent<Renderer>().material.color = new Color(0.5f, 0.9f, 1f);
        PhxCapitalShipSubsystem reactor = core.AddComponent<PhxCapitalShipSubsystem>();
        reactor.Type = PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor;
        reactor.IsCritical = true;
        reactor.IsInternal = true;
        reactor.MaxHealth = 4000f;
        ship.RegisterSubsystem(reactor);

        Light coreGlow = core.AddComponent<Light>();
        coreGlow.type = LightType.Point;
        coreGlow.color = new Color(0.5f, 0.9f, 1f);
        coreGlow.range = 30f;
        coreGlow.intensity = 600f;

        // second defender spawn covering the reactor approach
        SpawnPad(ship, t, new Vector3(0f, FloorY, -70f));

        // ================= bridge (z -175..-130), deepest compartment =================
        RoomLight(t, new Vector3(0f, CeilY - 1f, -152f), 25f);
        SubsystemConsole(ship, t, "Bridge", PhxCapitalShipSubsystem.PhxSubsystemType.Bridge,
            new Vector3(0f, FloorY + 2f, -160f), new Color(0.8f, 0.8f, 1f), critical: false);
    }

    // ---------------------------------------------------------------- helpers

    static GameObject Plate(Transform parent, string name, Vector3 center, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().material.color = new Color(0.45f, 0.47f, 0.52f);
        return go;
    }

    static void Shell(Transform parent, string name, Vector3 center, Vector3 size)
    {
        Plate(parent, name, center, size).AddComponent<PhxShipBreakSection>();
    }

    // Interior wall across the ship (X axis) with a centered door gap
    static void WallX(Transform parent, string name, float z, float doorHalfWidth,
                      float xFrom = -HullWidth * 0.5f, float xTo = HullWidth * 0.5f)
    {
        float y = (FloorY + CeilY) * 0.5f;
        float h = CeilY - FloorY;
        float leftW = (-doorHalfWidth) - xFrom;
        float rightW = xTo - doorHalfWidth;
        if (leftW > 0.1f)
            Plate(parent, name + "_l", new Vector3(xFrom + leftW * 0.5f, y, z), new Vector3(leftW, h, Wall));
        if (rightW > 0.1f)
            Plate(parent, name + "_r", new Vector3(doorHalfWidth + rightW * 0.5f, y, z), new Vector3(rightW, h, Wall));
    }

    // Interior wall along the ship (Z axis) with door gaps at given z positions
    static void WallZ(Transform parent, string name, float x, float zFrom, float zTo, float[] doorsAtZ)
    {
        const float doorHalf = 2.5f;
        float y = (FloorY + CeilY) * 0.5f;
        float h = CeilY - FloorY;

        List<float> cuts = new List<float> { zFrom };
        foreach (float d in doorsAtZ) { cuts.Add(d - doorHalf); cuts.Add(d + doorHalf); }
        cuts.Add(zTo);
        cuts.Sort();

        // solid segments are between even/odd cut pairs
        for (int i = 0; i + 1 < cuts.Count; i += 2)
        {
            float segLen = cuts[i + 1] - cuts[i];
            if (segLen < 0.1f) continue;
            Plate(parent, $"{name}_{i / 2}", new Vector3(x, y, cuts[i] + segLen * 0.5f), new Vector3(Wall, h, segLen));
        }
    }

    static void RoomLight(Transform parent, Vector3 pos, float range)
    {
        GameObject go = new GameObject("RoomLight");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = range;
        l.intensity = 300f;
        l.color = new Color(0.9f, 0.95f, 1f);
    }

    static void SubsystemConsole(PhxCapitalShip ship, Transform parent, string name,
        PhxCapitalShipSubsystem.PhxSubsystemType type, Vector3 pos, Color color, bool critical = true)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(4f, 4f, 4f);
        go.GetComponent<Renderer>().material.color = color;

        PhxCapitalShipSubsystem sys = go.AddComponent<PhxCapitalShipSubsystem>();
        sys.Type = type;
        sys.IsCritical = critical;
        sys.IsInternal = true;
        sys.MaxHealth = 2000f;
        ship.RegisterSubsystem(sys);

        Light glow = go.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = color;
        glow.range = 10f;
        glow.intensity = 150f;
    }

    static void TurretStation(PhxCapitalShip ship, Transform parent, Vector3 pos, bool lookRight)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "TurretStation";
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos + Vector3.up * 1f;
        go.transform.localScale = new Vector3(2f, 1f, 2f);
        go.GetComponent<Renderer>().material.color = new Color(0.7f, 0.7f, 0.75f);

        PhxShipTurretStation station = go.AddComponent<PhxShipTurretStation>();
        station.Ship = ship;
        // the external gun this console controls, mounted on the hull outside
        station.ExternalGunLocalPos = new Vector3(lookRight ? HullWidth * 0.5f + 3f : -HullWidth * 0.5f - 3f, CeilY + 4f, 100f);
    }

    static void EscapePodBay(PhxCapitalShip ship, Transform parent, Vector3 pos)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "EscapePod";
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos + Vector3.up * 2f;
        go.transform.localScale = new Vector3(2.5f, 2f, 2.5f);
        go.GetComponent<Renderer>().material.color = new Color(0.75f, 0.72f, 0.65f);

        PhxEscapePod pod = go.AddComponent<PhxEscapePod>();
        pod.Ship = ship;
    }

    static void Autogun(PhxCapitalShip ship, Transform parent, Vector3 pos)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Autogun";
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = Vector3.one * 1.5f;
        go.GetComponent<Renderer>().material.color = new Color(0.8f, 0.3f, 0.25f);

        PhxShipAutogun gun = go.AddComponent<PhxShipAutogun>();
        gun.Ship = ship;
    }

    static void SpawnPad(PhxCapitalShip ship, Transform parent, Vector3 pos)
    {
        GameObject go = new GameObject("DefenderSpawn");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos + Vector3.up * 0.5f;
        ship.DefenderSpawns.Add(go.transform);
    }
}
