using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The BF3 "Vertical Battlefront" for EVERY map - stock SWBF2, BF3 Legacy mod
/// maps and other addons alike. When a map finishes loading, this manager:
///
///  1. Decides whether the space layer is IN ATMOSPHERE (capital ships loom
///     low over the battlefield, Elite Squadron style - used on planetary
///     surface maps) or in ORBIT/DEEP SPACE (space maps, vacuum worlds).
///  2. Spawns one destructible capital ship per team above the battlefield,
///     complete with the walkable ES/BF3 interior (hangar, corridors,
///     critical systems, reactor, escape pods, autoguns).
///  3. Adds a transition band: flyers climbing through it get a callout and
///     continue seamlessly into the space layer (same scene, no loading -
///     exactly Free Radical's pitch).
///
/// Interior maps (Tantive IV, Death Star) get no space layer - there is no
/// sky to fly into.
///
/// Attached to the BF3Legacy host object when Config.VerticalBattlefront is on.
/// </summary>
public class PhxVerticalBattlefront : MonoBehaviour
{
    public const float AtmosphereShipAltitude = 450f;   // ES: ships loom over the battle
    public const float OrbitShipAltitude = 900f;
    public const float ShipLateralOffset = 350f;

    enum PhxSpaceLayerKind { None, Atmosphere, Orbit }

    PhxScene ActiveScene;
    readonly List<PhxCapitalShip> SpawnedShips = new List<PhxCapitalShip>();
    GameObject TransitionBand;

    // --- per-planet knowledge for stock SWBF2 maps (mapluafile prefixes) ---
    // Interior maps get no layer; vacuum/space worlds put ships in orbit;
    // everything else flies them in-atmosphere, Elite Squadron style.
    static readonly Dictionary<string, PhxSpaceLayerKind> StockMapKinds = new Dictionary<string, PhxSpaceLayerKind>
    {
        { "cor", PhxSpaceLayerKind.Atmosphere },  // Coruscant
        { "dag", PhxSpaceLayerKind.Atmosphere },  // Dagobah
        { "end", PhxSpaceLayerKind.Atmosphere },  // Endor
        { "fel", PhxSpaceLayerKind.Atmosphere },  // Felucia
        { "geo", PhxSpaceLayerKind.Atmosphere },  // Geonosis
        { "hot", PhxSpaceLayerKind.Atmosphere },  // Hoth
        { "kam", PhxSpaceLayerKind.Atmosphere },  // Kamino
        { "kas", PhxSpaceLayerKind.Atmosphere },  // Kashyyyk
        { "mus", PhxSpaceLayerKind.Atmosphere },  // Mustafar
        { "myg", PhxSpaceLayerKind.Atmosphere },  // Mygeeto
        { "nab", PhxSpaceLayerKind.Atmosphere },  // Naboo
        { "uta", PhxSpaceLayerKind.Atmosphere },  // Utapau
        { "yav", PhxSpaceLayerKind.Atmosphere },  // Yavin 4
        { "tat", PhxSpaceLayerKind.Atmosphere },  // Tatooine
        { "pol", PhxSpaceLayerKind.Orbit },       // Polis Massa (vacuum)
        { "spa", PhxSpaceLayerKind.Orbit },       // space assault maps
        { "tan", PhxSpaceLayerKind.None },        // Tantive IV (interior)
        { "dea", PhxSpaceLayerKind.None },        // Death Star (interior)
    };


    void Update()
    {
        PhxScene scene = PhxGame.GetScene();
        if (scene == ActiveScene) return;

        // scene changed: reset and (maybe) build the space layer
        ActiveScene = scene;
        SpawnedShips.Clear();
        TransitionBand = null;
        if (scene == null) return;

        // bootstrap may have run before the game path was known
        PhxModManager.EnsureScanned();

        string mapScript = PhxGame.Instance != null ? PhxGame.Instance.CurrentMapScript : null;
        if (string.IsNullOrEmpty(mapScript)) return;

        PhxSpaceLayerKind kind = ClassifyMap(mapScript.ToLowerInvariant());
        if (kind == PhxSpaceLayerKind.None)
        {
            Debug.Log($"[BF3Legacy] '{mapScript}': interior map, no space layer");
            return;
        }

        // If the map drives its own capital ships through the stock space
        // assault system, those are real authored ships - don't also spawn
        // greybox stand-ins on top of them.
        if (PhxSpaceAssault.Enabled)
        {
            Debug.Log($"[BF3Legacy] '{mapScript}': using the map's own space assault " +
                      "capital ships, greybox layer skipped");
            return;
        }

        BuildSpaceLayer(scene, mapScript, kind);
    }

    PhxSpaceLayerKind ClassifyMap(string mapScript)
    {
        // BF3 greybox maps handle their own ships via PhxBF3MapBuilder
        if (mapScript.StartsWith("bf3_")) return PhxSpaceLayerKind.None;

        foreach (KeyValuePair<string, PhxSpaceLayerKind> kv in StockMapKinds)
        {
            if (mapScript.StartsWith(kv.Key)) return kv.Value;
        }

        // Unknown map (addon/mod content, incl. BF3 Legacy): if it looks like
        // a space map put ships in orbit, otherwise assume a planet surface.
        if (mapScript.Contains("space") || mapScript.Contains("spa"))
        {
            return PhxSpaceLayerKind.Orbit;
        }
        return PhxSpaceLayerKind.Atmosphere;
    }

    void BuildSpaceLayer(PhxScene scene, string mapScript, PhxSpaceLayerKind kind)
    {
        // center the layer over the action: average of all command posts
        Vector3 center = Vector3.zero;
        PhxCommandpost[] posts = scene.GetCommandPosts();
        if (posts != null && posts.Length > 0)
        {
            foreach (PhxCommandpost cp in posts) center += cp.transform.position;
            center /= posts.Length;
        }

        bool inAtmosphere = kind == PhxSpaceLayerKind.Atmosphere;
        float altitude = inAtmosphere ? AtmosphereShipAltitude : OrbitShipAltitude;

        Vector3 posTeam1 = center + new Vector3(-ShipLateralOffset, altitude, 0f);
        Vector3 posTeam2 = center + new Vector3(ShipLateralOffset, altitude, 0f);

        SpawnedShips.Add(PhxCapitalShipFactory.Spawn(1, "Capital Ship (Team 1)",
            posTeam1, posTeam2, inAtmosphere));
        SpawnedShips.Add(PhxCapitalShipFactory.Spawn(2, "Capital Ship (Team 2)",
            posTeam2, posTeam1, inAtmosphere));

        // transition band: flyers crossing this altitude are "leaving the battlefield"
        TransitionBand = new GameObject("SpaceTransitionBand");
        TransitionBand.transform.position = center + Vector3.up * (altitude * 0.55f);
        BoxCollider trigger = TransitionBand.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(4000f, 30f, 4000f);
        TransitionBand.AddComponent<PhxSpaceTransitionBand>();

        Debug.Log($"[BF3Legacy] '{mapScript}': vertical battlefront active, " +
                  $"capital ships {(inAtmosphere ? "in atmosphere" : "in orbit")} at {altitude}m");
    }
}

/// <summary>
/// Marks the seam between the ground war and the space war. Crossing is
/// seamless (same scene); this only announces the transition and gives a hook
/// for later polish (sky fade, music shift, HUD "entering space" callout).
/// </summary>
public class PhxSpaceTransitionBand : MonoBehaviour
{
    void OnTriggerEnter(Collider other)
    {
        PhxVehicle vehicle = other.GetComponentInParent<PhxVehicle>();
        if (vehicle != null)
        {
            Debug.Log($"[BF3Legacy] {vehicle.name} crossing the ground/space transition");
        }
    }
}
