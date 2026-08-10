using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Very short-lived point lights for the moment something bright happens.
/// </summary>
/// <remarks>
/// A blaster bolt striking a bulkhead throws light on the wall around it for
/// perhaps a tenth of a second. That flash is most of what sells the hit - and
/// it is also the single easiest way to destroy performance, because the naive
/// implementation creates a GameObject with a Light on it per impact and lets
/// Unity clean up. Sixty-four soldiers firing gives hundreds of those a second.
///
/// So: a fixed pool, allocated once, with a hard budget from
/// <see cref="BFPresentationQuality"/>. When every light is in use the next
/// impact steals the one closest to expiring rather than allocating - a flash
/// that ends 30 ms early is invisible, and an unbounded light count is not.
///
/// Shadows are never enabled on these. A shadow-casting light costs a shadow
/// map render, and nothing in a 0.1 s flash is worth that.
/// </remarks>
public sealed class BFImpactLightPool : MonoBehaviour
{
    public static BFImpactLightPool Instance { get; private set; }

    sealed class PooledLight
    {
        public GameObject Object;
        public Light Light;
        public HDAdditionalLightData Data;
        public float ExpiresAt;
        public float Lifetime;
        public float PeakIntensity;
        public bool InUse;
    }

    readonly List<PooledLight> Pool = new List<PooledLight>();
    Transform Root;

    void Awake()
    {
        Instance = this;
        Root = new GameObject("BFImpactLights").transform;
        Root.SetParent(transform, false);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        float now = Time.time;

        for (int i = 0; i < Pool.Count; ++i)
        {
            PooledLight light = Pool[i];
            if (!light.InUse) continue;

            float remaining = light.ExpiresAt - now;
            if (remaining <= 0f)
            {
                Release(light);
                continue;
            }

            // Fade out over the life rather than blinking off. The eye reads a
            // hard cut as a rendering glitch and a decay as a spark dying.
            float t = Mathf.Clamp01(remaining / Mathf.Max(0.001f, light.Lifetime));
            light.Data.intensity = light.PeakIntensity * t * t;
        }
    }

    /// <summary>
    /// Flash a light at a point.
    /// </summary>
    /// <param name="intensity">Peak, in lumens.</param>
    /// <param name="lifetime">Seconds. Impacts want ~0.06, explosions ~0.4.</param>
    public static void Flash(Vector3 position, Color color, float intensity, float range, float lifetime)
    {
        if (!BFPresentationQuality.ImpactLights || Instance == null) return;

        Instance.FlashInternal(position, color, intensity, range, lifetime);
    }

    void FlashInternal(Vector3 position, Color color, float intensity, float range, float lifetime)
    {
        PooledLight light = Acquire();
        if (light == null) return;

        light.Object.transform.position = position;
        light.Object.SetActive(true);
        light.Light.color = color;
        light.Data.range = range;
        light.PeakIntensity = intensity;
        light.Data.intensity = intensity;
        light.Lifetime = lifetime;
        light.ExpiresAt = Time.time + lifetime;
        light.InUse = true;
    }

    PooledLight Acquire()
    {
        int budget = BFPresentationQuality.ImpactLightBudget;
        if (budget <= 0) return null;

        for (int i = 0; i < Pool.Count; ++i)
        {
            if (!Pool[i].InUse) return Pool[i];
        }

        if (Pool.Count < budget)
        {
            PooledLight created = Create();
            Pool.Add(created);
            return created;
        }

        // Budget spent: take the one with the least life left. Cutting a flash
        // short is invisible; exceeding the light budget is not.
        PooledLight oldest = null;
        float earliest = float.MaxValue;
        for (int i = 0; i < Pool.Count; ++i)
        {
            if (Pool[i].ExpiresAt >= earliest) continue;
            earliest = Pool[i].ExpiresAt;
            oldest = Pool[i];
        }
        return oldest;
    }

    PooledLight Create()
    {
        var obj = new GameObject("ImpactLight");
        obj.transform.SetParent(Root, false);
        obj.SetActive(false);

        Light light = obj.AddComponent<Light>();
        light.type = LightType.Point;
        light.shadows = LightShadows.None;

        HDAdditionalLightData data = obj.AddComponent<HDAdditionalLightData>();
        data.EnableColorTemperature(false);
        data.affectsVolumetric = true;

        return new PooledLight { Object = obj, Light = light, Data = data };
    }

    void Release(PooledLight light)
    {
        light.InUse = false;
        light.Object.SetActive(false);
    }

    /// <summary>Extinguish everything - map change.</summary>
    public static void Clear()
    {
        if (Instance == null) return;

        for (int i = 0; i < Instance.Pool.Count; ++i)
        {
            Instance.Release(Instance.Pool[i]);
        }
    }
}
