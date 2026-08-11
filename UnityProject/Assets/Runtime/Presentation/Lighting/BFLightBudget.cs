using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Keeps the cost of a map's own lights proportional to what they contribute.
/// </summary>
/// <remarks>
/// Measured on Coruscant: 322 renderers, 130k triangles, 36 lights - a scene
/// whose geometry is trivial for any modern GPU and whose lighting is not.
/// Two things dominate, and both are per-light rather than per-triangle:
///
/// <list type="number">
/// <item><b>Shadow-casting punctual lights.</b> A point light is six shadow
/// renders, and each render walks the scene's 273 shadow casters. Six of them
/// - the command post projectors - is thirty-six such passes. Cheap geometry
/// does not make that cheap.</item>
/// <item><b>Volumetric contribution.</b> HDRP evaluates every light that
/// affects volumetrics for every froxel in the camera volume. Thirty-four
/// point lights with the default <c>affectsVolumetric</c> is thirty-four
/// evaluations per froxel, whether or not the light is anywhere near one.</item>
/// </list>
///
/// So the budget is expressed in lights, not in objects: only the few nearest
/// punctual lights cast shadows, only lights that plausibly light visible fog
/// contribute to it, and the rest keep their illumination and lose the parts
/// nobody can point at in a screenshot.
/// </remarks>
public sealed class BFLightBudget : MonoBehaviour
{
    public static BFLightBudget Instance { get; private set; }

    sealed class Managed
    {
        public Light Light;
        public HDAdditionalLightData Data;

        /// <summary>Whether the importer or a system wanted shadows here.</summary>
        public bool WantsShadows;
    }

    readonly List<Managed> Punctual = new List<Managed>();
    float RescanTimer;
    float RebalanceTimer;

    /// <summary>How often the light list is rebuilt from the scene.</summary>
    const float RescanInterval = 5f;

    /// <summary>How often shadow permissions are reassigned by distance.</summary>
    const float RebalanceInterval = 0.5f;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Rescan;
        }
    }

    void Rescan()
    {
        Punctual.Clear();

        Light[] lights = FindObjectsOfType<Light>();
        for (int i = 0; i < lights.Length; ++i)
        {
            Light light = lights[i];
            if (light.type == LightType.Directional) continue;

            HDAdditionalLightData data = light.GetComponent<HDAdditionalLightData>();
            if (data == null) continue;

            Punctual.Add(new Managed
            {
                Light = light,
                Data = data,
                WantsShadows = light.shadows != LightShadows.None,
            });

            ApplyVolumetricPolicy(data, light);
        }

        Debug.Log($"[BFPerf] Light budget managing {Punctual.Count} punctual light(s); " +
                  $"at most {BFPresentationQuality.ShadowCastingPunctualBudget} may cast shadows.");

        Rebalance();
    }

    /// <summary>
    /// Decide whether a light contributes to volumetric fog.
    /// </summary>
    /// <remarks>
    /// Small local lights are the ones that cost the most for the least: a
    /// 7 m lamp illuminates a handful of froxels but is still evaluated
    /// against all of them. Large lights and anything the presentation layer
    /// placed deliberately keep their contribution, because those are the ones
    /// that produce visible shafts and glow.
    /// </remarks>
    static void ApplyVolumetricPolicy(HDAdditionalLightData data, Light light)
    {
        bool worthIt = data.range >= 15f;
        data.affectsVolumetric = worthIt && BFPresentationQuality.Volumetrics;
    }

    void Update()
    {
        RescanTimer -= Time.deltaTime;
        if (RescanTimer <= 0f)
        {
            RescanTimer = RescanInterval;
            PruneDestroyed();
        }

        RebalanceTimer -= Time.deltaTime;
        if (RebalanceTimer <= 0f)
        {
            RebalanceTimer = RebalanceInterval;
            Rebalance();
        }
    }

    void PruneDestroyed()
    {
        for (int i = Punctual.Count - 1; i >= 0; --i)
        {
            if (Punctual[i].Light == null) Punctual.RemoveAt(i);
        }
    }

    /// <summary>
    /// Give the shadow budget to the nearest lights that wanted it.
    /// </summary>
    /// <remarks>
    /// Nearest rather than brightest: a shadow exists to stop light crossing
    /// geometry the viewer can see, and the viewer can only see that nearby.
    /// A command post light three hundred metres away casting six shadow maps
    /// is paying full price for something that occupies a few pixels.
    /// </remarks>
    void Rebalance()
    {
        if (Punctual.Count == 0) return;

        Camera viewer = BFWorldQuery.Viewer;
        Vector3 eye = viewer != null ? viewer.transform.position : Vector3.zero;

        int budget = BFPresentationQuality.ShadowCastingPunctualBudget;

        // Partial selection rather than a sort: the list is small and this
        // runs twice a second, but allocating a sorted copy each time would
        // still be pure waste.
        for (int i = 0; i < Punctual.Count; ++i)
        {
            Managed managed = Punctual[i];
            if (managed.Light == null || !managed.WantsShadows) continue;

            float distance = Vector3.Distance(eye, managed.Light.transform.position);

            int nearer = 0;
            for (int j = 0; j < Punctual.Count; ++j)
            {
                if (j == i) continue;

                Managed other = Punctual[j];
                if (other.Light == null || !other.WantsShadows) continue;

                if (Vector3.Distance(eye, other.Light.transform.position) < distance) ++nearer;
            }

            bool allowed = nearer < budget;
            if ((managed.Light.shadows != LightShadows.None) == allowed) continue;

            managed.Light.shadows = allowed ? LightShadows.Soft : LightShadows.None;
            managed.Data.EnableShadows(allowed);
        }
    }

    /// <summary>
    /// Register a light created after the map loaded - a command post
    /// projector, a room fixture - so it takes part in the budget.
    /// </summary>
    public static void Register(HDAdditionalLightData data, bool wantsShadows)
    {
        if (Instance == null || data == null) return;

        Light light = data.GetComponent<Light>();
        if (light == null || light.type == LightType.Directional) return;

        for (int i = 0; i < Instance.Punctual.Count; ++i)
        {
            if (ReferenceEquals(Instance.Punctual[i].Data, data)) return;
        }

        Instance.Punctual.Add(new Managed { Light = light, Data = data, WantsShadows = wantsShadows });
        ApplyVolumetricPolicy(data, light);
    }
}
