using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// One body of water, taken over from whatever the map imported as water.
/// </summary>
/// <remarks>
/// The map already has water: a plane with a scrolling texture, placed by the
/// level designer, in the right shape and the right position. That placement
/// is stock data and is kept. What this replaces is only how it is shaded and
/// how it behaves - depth-based colour, layered normals, a reflection, and a
/// surface that answers when something enters it.
/// </remarks>
public sealed class BFWaterSurface : MonoBehaviour, BFInteractionReceiver
{
    /// <summary>Height of the still surface, in world space.</summary>
    public float SurfaceHeight => transform.position.y;

    /// <summary>Colour at depth. Shallow water tends toward the bed's colour.</summary>
    public Color DeepColor = new Color(0.02f, 0.10f, 0.14f);
    public Color ShallowColor = new Color(0.18f, 0.38f, 0.40f);

    /// <summary>Metres over which the water reaches full deep colour.</summary>
    public float DepthFalloff = 6f;

    /// <summary>Whether this body is worth a planar reflection.</summary>
    public bool Important;

    Renderer SurfaceRenderer;
    PlanarReflectionProbe Reflection;
    BFWaterRippleSimulation Ripples;
    Bounds WorldBounds;

    public Bounds InteractionBounds => WorldBounds;

    public bool Accepts(BFInteractionSource source)
    {
        switch (source)
        {
            case BFInteractionSource.WaterEntry:
            case BFInteractionSource.Footstep:
            case BFInteractionSource.Landing:
            case BFInteractionSource.BlasterImpact:
            case BFInteractionSource.Projectile:
            case BFInteractionSource.Explosion:
            case BFInteractionSource.Vehicle:
            case BFInteractionSource.Weather:
                return true;
            default:
                return false;
        }
    }

    void Awake()
    {
        SurfaceRenderer = GetComponentInChildren<Renderer>();
        WorldBounds = SurfaceRenderer != null
            ? SurfaceRenderer.bounds
            : new Bounds(transform.position, Vector3.one * 50f);

        Ripples = gameObject.AddComponent<BFWaterRippleSimulation>();
        Ripples.Initialize(WorldBounds);
    }

    void OnEnable() => BFSurfaceInteractionSystem.Register(this);
    void OnDisable() => BFSurfaceInteractionSystem.Unregister(this);

    void Start()
    {
        ApplyShading();
        ApplyReflection();
    }

    void ApplyShading()
    {
        if (SurfaceRenderer == null) return;

        Material material = SurfaceRenderer.material;
        if (material == null) return;

        // Depth colouring and smoothness only. The scrolling texture the map
        // authored stays as the base - it is what makes each planet's water
        // look like that planet's water.
        if (material.HasProperty("_BaseColor"))
        {
            // Alpha carried over from whatever the material already had, not
            // taken from ShallowColor. A body built by BFMapWater has been put
            // into HDRP's transparent pass with an alpha chosen to let the bed
            // show through, and writing an opaque colour straight over it turns
            // the surface back into a sheet of wet concrete.
            Color shallow = ShallowColor;
            if (material.HasProperty("_BaseColor"))
            {
                shallow.a = material.GetColor("_BaseColor").a;
            }
            material.SetColor("_BaseColor", shallow);
        }
        if (material.HasProperty("_Smoothness"))
        {
            material.SetFloat("_Smoothness", 0.96f);
        }
        if (material.HasProperty("_Metallic"))
        {
            material.SetFloat("_Metallic", 0f);
        }

        // Published for anything sampling water depth (shoreline foam, the
        // underwater volume, splash sizing).
        Shader.SetGlobalFloat("_BFWaterHeight", SurfaceHeight);
        Shader.SetGlobalColor("_BFWaterDeepColor", DeepColor);
        Shader.SetGlobalColor("_BFWaterShallowColor", ShallowColor);
        Shader.SetGlobalFloat("_BFWaterDepthFalloff", DepthFalloff);
    }

    /// <summary>
    /// A planar reflection, but only where it earns its cost.
    /// </summary>
    /// <remarks>
    /// A planar reflection re-renders the scene. On a map with a dozen puddles
    /// that is a dozen extra scene renders, which is not a trade worth making
    /// for a puddle. Large or important bodies get one; everything else falls
    /// back to screen-space reflections and the reflection probes, which are
    /// already paid for.
    /// </remarks>
    void ApplyReflection()
    {
        if (!BFPresentationQuality.PlanarWaterReflections) return;

        float area = WorldBounds.size.x * WorldBounds.size.z;
        if (!Important && area < 2500f) return;      // smaller than 50 m square

        var obj = new GameObject("BFWaterReflection");
        obj.transform.SetParent(transform, false);
        obj.transform.position = new Vector3(WorldBounds.center.x, SurfaceHeight, WorldBounds.center.z);
        obj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        Reflection = obj.AddComponent<PlanarReflectionProbe>();
        Reflection.influenceVolume.shape = InfluenceShape.Box;
        Reflection.influenceVolume.boxSize = new Vector3(WorldBounds.size.x, 40f, WorldBounds.size.z);
        Reflection.mode = ProbeSettings.Mode.Realtime;
        Reflection.realtimeMode = ProbeSettings.RealtimeMode.OnEnable;
    }

    public void OnInteraction(in BFSurfaceInteraction interaction)
    {
        // Only things at or below the surface disturb it.
        if (interaction.Position.y > SurfaceHeight + 1.5f) return;

        Ripples?.AddRipple(interaction.Position, interaction.Radius, interaction.Strength);

        // A splash is worth a visible effect for anything larger than rain.
        if (interaction.Strength < 0.3f) return;

        PhxScene scene = PhxGame.GetScene();
        if (scene == null) return;

        Vector3 splashAt = new Vector3(interaction.Position.x, SurfaceHeight, interaction.Position.z);
        scene.EffectsManager.PlayEffectOnce("com_sfx_watersplash", splashAt,
                                            Quaternion.LookRotation(Vector3.up));
    }

    /// <summary>Whether a world position is under this body's surface.</summary>
    public bool Contains(Vector3 worldPosition)
    {
        if (worldPosition.y > SurfaceHeight) return false;

        return worldPosition.x >= WorldBounds.min.x && worldPosition.x <= WorldBounds.max.x &&
               worldPosition.z >= WorldBounds.min.z && worldPosition.z <= WorldBounds.max.z;
    }

    public float DepthAt(Vector3 worldPosition) => Mathf.Max(0f, SurfaceHeight - worldPosition.y);
}

/// <summary>
/// Expanding rings from everything that touches the water.
/// </summary>
/// <remarks>
/// A fixed ring buffer rather than a grid solve. What sells water being
/// disturbed is a ring leaving the point of entry at a believable speed and
/// fading - not an accurate wave equation - and a ring buffer of a few dozen
/// entries costs nothing and cannot grow without bound during a firefight over
/// a lake.
/// </remarks>
public sealed class BFWaterRippleSimulation : MonoBehaviour
{
    struct Ripple
    {
        public Vector3 Origin;
        public float StartTime;
        public float Strength;
        public float MaxRadius;
    }

    const int Capacity = 48;
    readonly Ripple[] Ripples = new Ripple[Capacity];
    int Next;

    /// <summary>How fast a ring travels outward, m/s.</summary>
    const float RippleSpeed = 2.5f;

    /// <summary>How long a ring lives.</summary>
    const float RippleLifetime = 3f;

    Bounds Area;

    public void Initialize(Bounds area)
    {
        Area = area;
    }

    public void AddRipple(Vector3 origin, float radius, float strength)
    {
        Ripples[Next] = new Ripple
        {
            Origin = origin,
            StartTime = Time.time,
            Strength = strength,
            MaxRadius = Mathf.Max(1f, radius * 4f),
        };
        Next = (Next + 1) % Capacity;
    }

    /// <summary>
    /// Vertical displacement at a position from every live ring. For shaders
    /// via a uniform, and for anything that wants to float on the result.
    /// </summary>
    public float SampleHeight(Vector3 worldPosition)
    {
        float now = Time.time;
        float sum = 0f;

        for (int i = 0; i < Capacity; ++i)
        {
            Ripple r = Ripples[i];
            if (r.Strength <= 0f) continue;

            float age = now - r.StartTime;
            if (age < 0f || age > RippleLifetime) continue;

            float ringRadius = age * RippleSpeed;
            if (ringRadius > r.MaxRadius) continue;

            float distance = Vector2.Distance(
                new Vector2(worldPosition.x, worldPosition.z),
                new Vector2(r.Origin.x, r.Origin.z));

            // Contribution is a narrow band around the ring's current radius,
            // decaying with both age and distance travelled.
            float band = Mathf.Abs(distance - ringRadius);
            if (band > 1.2f) continue;

            float falloff = (1f - age / RippleLifetime) * (1f - band / 1.2f);
            sum += Mathf.Sin(band * 6f) * falloff * r.Strength * 0.08f;
        }
        return sum;
    }
}

/// <summary>
/// Underwater fog and post-processing while the camera is submerged.
/// </summary>
public sealed class BFUnderwaterVolume : MonoBehaviour
{
    public static bool CameraSubmerged { get; private set; }

    UnityEngine.Rendering.Volume Volume;
    UnityEngine.Rendering.VolumeProfile Profile;
    Fog Fog;
    ColorAdjustments Color;
    BFWaterSurface PolledWater;

    void Start()
    {
        Profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
        Profile.name = "BFUnderwater";
        Fog = Profile.Add<Fog>(true);
        Color = Profile.Add<ColorAdjustments>(true);

        Volume = gameObject.AddComponent<UnityEngine.Rendering.Volume>();
        Volume.isGlobal = true;
        // Above everything else: being underwater overrides the environment.
        Volume.priority = 200f;
        Volume.profile = Profile;
        Volume.weight = 0f;
    }

    float PollTimer;

    void Update()
    {
        Camera camera = BFWorldQuery.Viewer;
        if (camera == null || Volume == null) return;

        // Whether the camera is submerged is polled rather than tested every
        // frame: BodyAt walks every water body on the map, and a map with no
        // water at all still paid for the walk sixty times a second. The eased
        // weight below is what actually needs the frame.
        PollTimer -= Time.deltaTime;
        if (PollTimer <= 0f)
        {
            PollTimer = 0.2f;
            PolledWater = BFWaterSystem.BodyAt(camera.transform.position);
        }

        BFWaterSurface water = PolledWater;
        bool submerged = water != null;

        if (submerged != CameraSubmerged)
        {
            CameraSubmerged = submerged;
            if (submerged) Configure(water);
        }

        // Eased rather than snapped: breaking the surface is a transition, and
        // a hard cut reads as a rendering error.
        Volume.weight = Mathf.MoveTowards(Volume.weight, submerged ? 1f : 0f, Time.deltaTime * 4f);
    }

    void Configure(BFWaterSurface water)
    {
        Fog.enabled.Override(true);
        Fog.colorMode.Override(FogColorMode.ConstantColor);
        Fog.color.Override(water.DeepColor);
        Fog.meanFreePath.Override(12f);
        Fog.baseHeight.Override(-1000f);
        Fog.maximumHeight.Override(water.SurfaceHeight);

        Color.active = true;
        Color.saturation.Override(-25f);
        Color.colorFilter.Override(new Color(0.65f, 0.85f, 1f));
    }
}

/// <summary>
/// Finds the map's water and gives each body a <see cref="BFWaterSurface"/>.
/// </summary>
/// <remarks>
/// Discovery is by surface type, through the same query everything else uses:
/// a renderer whose material or texture the artists named for water is water.
/// That keeps water detection consistent with footsteps and impacts, so a
/// puddle a soldier splashes through is the same object the shading treats as
/// water.
/// </remarks>
public sealed class BFWaterSystem : MonoBehaviour
{
    public static BFWaterSystem Instance { get; private set; }

    static readonly List<BFWaterSurface> Bodies = new List<BFWaterSurface>();

    public static IReadOnlyList<BFWaterSurface> Surfaces => Bodies;

    void Awake()
    {
        Instance = this;
        gameObject.AddComponent<BFUnderwaterVolume>();
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= Discover;
        if (Instance == this) Instance = null;
        Bodies.Clear();
    }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Discover;
        }
    }

    void Discover()
    {
        Bodies.Clear();

        Renderer[] renderers = FindObjectsOfType<Renderer>();
        for (int i = 0; i < renderers.Length; ++i)
        {
            Renderer renderer = renderers[i];
            if (renderer == null || renderer.GetComponent<BFWaterSurface>() != null) continue;

            if (!IsWater(renderer)) continue;

            BFWaterSurface surface = renderer.gameObject.AddComponent<BFWaterSurface>();

            // The largest body on a map is the one worth a planar reflection.
            surface.Important = renderer.bounds.size.x * renderer.bounds.size.z > 10000f;
            Bodies.Add(surface);
        }

        if (Bodies.Count > 0)
        {
            Debug.Log($"[BFPresentation] {Bodies.Count} water surface(s) taken over.");
        }
    }

    static bool IsWater(Renderer renderer)
    {
        if (BFSurfaceQuery.FromKeyword(renderer.name) == BFSurfaceType.Water) return true;

        Material material = renderer.sharedMaterial;
        if (material == null) return false;
        if (BFSurfaceQuery.FromKeyword(material.name) == BFSurfaceType.Water) return true;

        // Ask before reading either one. `mainTexture` is a property lookup for
        // "_MainTex" under the hood and Unity logs an error per call when the
        // shader has no such property - which the hologram shader does not, so
        // scanning a map with holograms in it filled the console with errors
        // from a function that was only ever asking a question.
        Texture texture = null;
        if (material.HasProperty("_BaseColorMap"))
        {
            texture = material.GetTexture("_BaseColorMap");
        }
        else if (material.HasProperty("_MainTex"))
        {
            texture = material.mainTexture;
        }

        return texture != null && BFSurfaceQuery.FromKeyword(texture.name) == BFSurfaceType.Water;
    }

    /// <summary>
    /// Take over a surface that arrived after discovery had already run.
    /// </summary>
    /// <remarks>
    /// Discovery is a scan at map load, which is the right shape for water the
    /// map brought with it and the wrong shape for water built during that same
    /// load - <see cref="BFMapWater"/> constructs a surface for maps whose water
    /// lives in terrain data this project cannot read, and by then the scan has
    /// been and gone. Rather than rescan on a timer for something that happens
    /// once, the builder says so.
    ///
    /// Idempotent: a surface that already carries a <see cref="BFWaterSurface"/>
    /// is one this system has, and adopting it twice would give one body two
    /// ripple simulations and two reflection probes.
    /// </remarks>
    public static void Adopt(GameObject surface)
    {
        if (surface == null) return;

        Renderer renderer = surface.GetComponent<Renderer>();
        if (renderer == null) return;
        if (surface.GetComponent<BFWaterSurface>() != null) return;

        BFWaterSurface body = surface.AddComponent<BFWaterSurface>();
        body.Important = renderer.bounds.size.x * renderer.bounds.size.z > 10000f;
        Bodies.Add(body);

        Debug.Log("[BFPresentation] 1 water surface adopted after discovery.");
    }

    /// <summary>The body containing a position, or null.</summary>
    public static BFWaterSurface BodyAt(Vector3 worldPosition)
    {
        for (int i = 0; i < Bodies.Count; ++i)
        {
            if (Bodies[i] != null && Bodies[i].Contains(worldPosition)) return Bodies[i];
        }
        return null;
    }
}
