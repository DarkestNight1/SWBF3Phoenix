using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the mission script's map markers onto a PhxUIMap.
///
/// Scripts drop markers constantly (MapAddRegionMarker / MapAddEntityMarker
/// and friends) to point players at the current objective; PhxMatch has always
/// recorded them, but nothing rendered them, so objective-driven modes gave the
/// player no direction at all.
///
/// Added in code by PhxUIMap, so it works on both the HUD minimap and the
/// character-select map without a prefab change.
/// </summary>
public class PhxUIMapMarkers : MonoBehaviour
{
    static PhxScene SCENE => PhxGame.GetScene();
    static PhxMatch MATCH => PhxGame.GetMatch();

    // Markers move with their entities, but not fast enough to justify a
    // per-frame instance scan.
    const float RefreshInterval = 0.5f;

    PhxUIMap Map;
    RectTransform MapRect;
    Texture2D DefaultIcon;

    readonly List<RawImage> Pool = new List<RawImage>();
    readonly List<(Vector3 world, int team, string icon)> Placements =
        new List<(Vector3, int, string)>();

    float RefreshTimer;

    void Awake()
    {
        Map = GetComponent<PhxUIMap>();
        MapRect = transform as RectTransform;
    }

    void Start()
    {
        // hud_objective_icon is the stock objective marker; fall back to the
        // command post icon, which every map ships.
        DefaultIcon = TextureLoader.Instance.ImportUITexture("hud_objective_icon", false)
                   ?? TextureLoader.Instance.ImportUITexture("hud_flag_icon", false);
    }

    void Update()
    {
        RefreshTimer -= Time.deltaTime;
        if (RefreshTimer > 0f) return;
        RefreshTimer = RefreshInterval;

        if (Map == null || MapRect == null || MATCH == null) return;

        CollectPlacements();
        Draw();
    }

    void CollectPlacements()
    {
        Placements.Clear();

        PhxScene scene = SCENE;
        if (scene == null) return;

        IReadOnlyList<PhxMatch.PhxMapMarker> markers = MATCH.GetMapMarkers();
        for (int i = 0; i < markers.Count; ++i)
        {
            PhxMatch.PhxMapMarker marker = markers[i];

            // Markers addressed to one team are only that team's business.
            if (marker.TeamIdx != 0 && marker.TeamIdx != MATCH.Player.Team) continue;

            if (marker.Kind == "region")
            {
                PhxRegion region = scene.GetRegion(marker.Name);
                if (region != null)
                {
                    Placements.Add((region.transform.position, marker.TeamIdx, marker.IconName));
                }
            }
            else if (marker.Kind == "class")
            {
                // One icon per live instance of the class, so markers track
                // movers (vehicles, carried objectives) rather than a spawn point.
                CollectClassInstances(scene, marker);
            }
        }
    }

    void CollectClassInstances(PhxScene scene, PhxMatch.PhxMapMarker marker)
    {
        int instanceCount = scene.GetInstanceCount();
        for (int i = 0; i < instanceCount; ++i)
        {
            PhxInstance inst = scene.GetInstance(i);
            if (inst == null) continue;

            PhxClass cl = inst.GetClassRef();
            if (cl == null || !string.Equals(cl.Name, marker.Name, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Placements.Add((inst.transform.position, marker.TeamIdx, marker.IconName));
        }
    }

    void Draw()
    {
        Rect rect = MapRect.rect;

        for (int i = 0; i < Placements.Count; ++i)
        {
            (Vector3 world, int team, string iconName) = Placements[i];

            // Same world -> map mapping the command post buttons use.
            Vector2 worldXZ = new Vector2(world.x, world.z);
            Vector2 mapPos = ((worldXZ + Map.MapOffset) / Map.Zoom) + new Vector2(0.5f, 0.5f);

            RawImage icon = GetPoolItem(i);
            icon.gameObject.SetActive(true);
            icon.texture = ResolveIcon(iconName);
            icon.color = team != 0 ? MATCH.GetTeamColor(team) : Color.white;

            RectTransform t = (RectTransform)icon.transform;
            t.anchoredPosition = new Vector2(mapPos.x * rect.width, mapPos.y * rect.height);
        }

        for (int i = Placements.Count; i < Pool.Count; ++i)
        {
            Pool[i].gameObject.SetActive(false);
        }
    }

    readonly Dictionary<string, Texture2D> IconCache = new Dictionary<string, Texture2D>();

    Texture2D ResolveIcon(string iconName)
    {
        if (string.IsNullOrEmpty(iconName)) return DefaultIcon;

        if (!IconCache.TryGetValue(iconName, out Texture2D tex))
        {
            tex = TextureLoader.Instance.ImportUITexture(iconName, false) ?? DefaultIcon;
            IconCache[iconName] = tex;
        }
        return tex;
    }

    RawImage GetPoolItem(int idx)
    {
        while (Pool.Count <= idx)
        {
            GameObject obj = new GameObject("MapMarker", typeof(RectTransform));
            obj.transform.SetParent(transform, false);

            RectTransform t = (RectTransform)obj.transform;
            t.anchorMin = Vector2.zero;
            t.anchorMax = Vector2.zero;
            t.pivot = new Vector2(0.5f, 0.5f);
            t.sizeDelta = new Vector2(16f, 16f);

            RawImage img = obj.AddComponent<RawImage>();
            img.raycastTarget = false;
            Pool.Add(img);
        }
        return Pool[idx];
    }
}
