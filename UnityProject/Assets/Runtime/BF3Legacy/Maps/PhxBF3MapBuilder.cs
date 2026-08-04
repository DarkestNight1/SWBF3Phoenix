using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns a PhxBF3MapDatabase entry into a playable greybox recreation:
/// ground plane, landmark volumes, command post markers, sun/sky lighting from
/// the map definition, and - for vertical battlefront maps - a space layer
/// with one destructible capital ship per team plus an optional ground ion
/// cannon (Elite Squadron flow).
///
/// Intent: these greyboxes make the BF3 layouts playable and testable NOW,
/// and act as blockouts to be replaced landmark-by-landmark with real art or
/// with the BF3 Legacy mod's own level files once installed (the builder skips
/// itself if the addon pipeline already provides the map).
///
/// Usage: PhxBF3MapBuilder.Build("bf3_coruscant") or add the component to a
/// GameObject and set MapId in the inspector.
/// </summary>
public class PhxBF3MapBuilder : MonoBehaviour
{
    public string MapId = "bf3_coruscant";
    public bool BuildOnStart = false;

    public const float SpaceLayerAltitude = 900f;

    GameObject Root;

    void Start()
    {
        if (BuildOnStart)
        {
            BuildInternal(PhxBF3MapDatabase.Get(MapId));
        }
    }

    public static GameObject Build(string mapId)
    {
        PhxBF3MapDatabase.PhxBF3MapDef def = PhxBF3MapDatabase.Get(mapId);
        if (def == null)
        {
            Debug.LogError($"[BF3Legacy] Unknown BF3 map id '{mapId}'");
            return null;
        }

        GameObject host = new GameObject($"BF3Map_{def.Id}");
        PhxBF3MapBuilder builder = host.AddComponent<PhxBF3MapBuilder>();
        builder.MapId = mapId;
        builder.BuildInternal(def);
        return host;
    }

    void BuildInternal(PhxBF3MapDatabase.PhxBF3MapDef def)
    {
        if (def == null) return;
        if (Root != null) Destroy(Root);

        Root = new GameObject(def.DisplayName);
        Root.transform.SetParent(transform, false);

        float half = def.MapSize * 0.5f;

        // ---- ground ----
        bool isSpaceOnly = def.Planet == "Deep Space";
        if (!isSpaceOnly)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(Root.transform, false);
            // Unity plane is 10x10m at scale 1
            ground.transform.localScale = new Vector3(def.MapSize / 10f, 1f, def.MapSize / 10f);
            Tint(ground, def.GroundColor);
        }

        // ---- landmarks ----
        foreach (PhxBF3MapDatabase.PhxLandmark lm in def.Landmarks)
        {
            GameObject go = CreateLandmark(lm);
            go.transform.SetParent(Root.transform, false);
            Vector3 pos = Vector3.Scale(lm.Position, new Vector3(half, def.MapSize, half));
            // ground-standing shapes sit on their base
            pos.y += lm.Size.y * 0.5f;
            go.transform.localPosition = pos;
            Tint(go, def.GroundColor * 1.15f);
        }

        // ---- command posts ----
        foreach (PhxBF3MapDatabase.PhxCommandPostDef cp in def.CommandPosts)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = cp.Name;
            marker.transform.SetParent(Root.transform, false);
            marker.transform.localPosition = Vector3.Scale(cp.Position, new Vector3(half, def.MapSize, half)) + Vector3.up * 1.5f;
            marker.transform.localScale = new Vector3(1.2f, 1.5f, 1.2f);
            Tint(marker, TeamColor(cp.StartingTeam));

            PhxRuntimeAssets.CreatePointLight(marker, TeamColor(cp.StartingTeam), 12f, 3000f);
        }

        // ---- lighting ----
        BuildSun(def);

        // ---- vertical battlefront: space layer ----
        if (def.GroundToSpace && PhxBF3.Config.VerticalBattlefront)
        {
            float altitude = def.InAtmosphereSpaceLayer ? SpaceLayerAltitude * 0.5f : SpaceLayerAltitude;
            BuildCapitalShip(def, team: 1, new Vector3(-half * 0.6f, altitude, 0f));
            BuildCapitalShip(def, team: 2, new Vector3(half * 0.6f, altitude, 0f));
        }

        // ---- ion cannon ----
        if (def.HasIonCannon)
        {
            GameObject cannon = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cannon.name = "IonCannon";
            cannon.transform.SetParent(Root.transform, false);
            cannon.transform.localPosition = new Vector3(0f, 9f, -half * 0.65f);
            cannon.transform.localScale = new Vector3(8f, 9f, 8f);
            Tint(cannon, new Color(0.6f, 0.7f, 0.8f));

            GameObject muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(cannon.transform, false);
            muzzle.transform.localPosition = Vector3.up * 1.1f;

            PhxIonCannon ion = cannon.AddComponent<PhxIonCannon>();
            ion.Muzzle = muzzle.transform;
        }

        Debug.Log($"[BF3Legacy] Built greybox '{def.DisplayName}' " +
                  $"({def.CommandPosts.Count} CPs, {def.Landmarks.Count} landmarks, " +
                  $"groundToSpace: {def.GroundToSpace}) - source: {def.Source}");
    }

    GameObject CreateLandmark(PhxBF3MapDatabase.PhxLandmark lm)
    {
        GameObject go;
        switch (lm.Shape)
        {
            case PhxBF3MapDatabase.PhxLandmarkShape.Cylinder:
            case PhxBF3MapDatabase.PhxLandmarkShape.Tower:
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                // Unity cylinder is 2m high at scale 1
                go.transform.localScale = new Vector3(lm.Size.x, lm.Size.y * 0.5f, lm.Size.z);
                break;
            default:
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.localScale = lm.Size;
                break;
        }
        go.name = lm.Name;
        return go;
    }

    void BuildSun(PhxBF3MapDatabase.PhxBF3MapDef def)
    {
        GameObject sun = new GameObject("Sun");
        sun.transform.SetParent(Root.transform, false);
        sun.transform.rotation = Quaternion.Euler(def.SunDirection);

        // SunIntensity is authored as a relative multiplier; HDRP directional
        // lights are in Lux (full daylight ~100k)
        PhxRuntimeAssets.CreateDirectionalLight(sun, def.SkyTint,
                                                def.SunIntensity * 70000f, castShadows: true);
    }

    void BuildCapitalShip(PhxBF3MapDatabase.PhxBF3MapDef def, int team, Vector3 position)
    {
        PhxCapitalShipFactory.Spawn(
            team,
            team == 1 ? "Alliance Flagship" : "Imperial Flagship",
            position: Root.transform.TransformPoint(position),
            facingTarget: Root.transform.position,
            inAtmosphere: def.InAtmosphereSpaceLayer,
            parent: Root.transform);
    }

    static void Tint(GameObject go, Color color)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r != null)
        {
            PhxRuntimeAssets.SetColor(r.material, color);
        }
    }

    static Color TeamColor(int team)
    {
        switch (team)
        {
            case 1: return new Color(0.3f, 0.55f, 1f);
            case 2: return new Color(1f, 0.3f, 0.25f);
            default: return Color.white;
        }
    }
}
