using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Fill light under structures that the map lights from outside and never from
/// within.
/// </summary>
/// <remarks>
/// Phoenix builds every map at runtime from the original .lvl data, so there is
/// no baked global illumination anywhere in the game and there cannot be - a
/// bake needs a scene on disk to bake. Direct sun plus the sky ambient probe is
/// the entire lighting model, and SSGI only propagates what is already on
/// screen.
///
/// Outdoors that is fine. Under a solid roof it is not: the sun is correctly
/// blocked, no bounce arrives to replace it, and what should read as shade
/// reads as a black hole. Kashyyyk is the worst case in the game - a dim green
/// ambient to begin with, and platforms whose roofs are the only cover on the
/// map.
///
/// This adds the light that bounce would have provided, at the few places
/// measured to need it.
///
/// WHY NOT JUST DISABLE THE ROOF'S SHADOW. It is cheaper and it is wrong. The
/// sun would shine through a solid roof onto the platform floor, the roof's
/// shadow would disappear from the terrain outside, and on Kashyyyk - which
/// runs VolumetricLightingMultiplier 1.5 - there would be god rays streaming
/// through solid timber. Trading "too dark" for "lit from inside a roof" is not
/// a fix, and the artefacts are harder to explain than the darkness.
///
/// Deliberately a short list in code, for the same reasons
/// <see cref="BFMapCollisionOverrides"/> is: it ships with the build, it is
/// visible in review, and it changes nothing about the import path. If it grows
/// past a screenful it wanted to be a data file.
/// </remarks>
public sealed class BFMapFillLights : MonoBehaviour
{
    /// <summary>A structure whose underside needs light the map never gives it.</summary>
    struct FillUnder
    {
        public string ScriptPrefix;     // map, by mission-script prefix
        public string InstancePrefix;   // instance name, matched case-insensitively
        public Color Tint;
        public float Lumen;
        public string Why;
    }

    static readonly FillUnder[] Overrides =
    {
        new FillUnder
        {
            ScriptPrefix = "kas",
            // Verified against the shipped data, not guessed: enumerating
            // kas2.lvl gives the class as "kas2_bldg_platform_roof.msh" - WITH
            // the .msh suffix - at 4 instances. Matching is StartsWith, so this
            // prefix covers the suffixed name; an exact-match table keyed on the
            // unsuffixed string silently matches nothing, which is how this went
            // unnoticed the first time.
            InstancePrefix = "kas2_bldg_platform_roof",
            // Warm, because the light this stands in for would have bounced off
            // wooden decking. A neutral fill under a timber roof reads as
            // moonlight and looks stranger than the darkness did.
            Tint = new Color(1f, 0.92f, 0.78f),
            Lumen = 2600f,
            Why = "solid roof, no bounce light in the engine to fill the shade beneath it",
        },
    };

    /// <summary>Lights created for the current map, cleared on the next load.</summary>
    readonly System.Collections.Generic.List<GameObject> Spawned =
        new System.Collections.Generic.List<GameObject>();

    void Start()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded += Apply;
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= Apply;
        Clear();
    }

    void Clear()
    {
        for (int i = 0; i < Spawned.Count; ++i)
        {
            if (Spawned[i] != null) Destroy(Spawned[i]);
        }
        Spawned.Clear();
    }

    void Apply()
    {
        Clear();

        string world = PhxGame.GetEnvironment()?.GetWorldName();
        if (string.IsNullOrEmpty(world)) return;

        // Fill light is a fidelity feature, not a correctness one - a machine on
        // the bottom tier is already dropping local lights it was asked for, and
        // should not be handed more.
        if (!BFPresentationQuality.ImpactLights) return;

        for (int i = 0; i < Overrides.Length; ++i)
        {
            FillUnder o = Overrides[i];
            if (!world.StartsWith(o.ScriptPrefix, System.StringComparison.OrdinalIgnoreCase)) continue;

            int lit = ApplyFill(o);
            if (lit > 0)
            {
                Debug.Log($"[BFPresentation] Fill light on '{world}': lit {lit} structure(s) " +
                          $"under '{o.InstancePrefix}*' - {o.Why}.");
            }
        }
    }

    /// <summary>
    /// One light per matching structure, hung under its own bounds.
    /// </summary>
    /// <remarks>
    /// Placed and sized from the renderer bounds rather than from a hand-typed
    /// offset, because the same roof prop is instanced at several scales and
    /// rotations across the map - a fixed height that centres one of them sits
    /// above the roof of another and lights the sky instead.
    /// </remarks>
    int ApplyFill(FillUnder o)
    {
        int lit = 0;

        Renderer[] all = FindObjectsByType<Renderer>(FindObjectsInactive.Include,
                                                     FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; ++i)
        {
            Renderer r = all[i];
            if (r == null) continue;
            if (!BelongsTo(r.transform, o.InstancePrefix)) continue;

            Bounds b = r.bounds;
            if (b.size.sqrMagnitude <= 0.001f) continue;

            // Just under the roof's own underside, at its centre. A light placed
            // AT the underside is inside the geometry and lights nothing.
            Vector3 pos = new Vector3(b.center.x, b.min.y - 0.5f, b.center.z);

            GameObject host = new GameObject($"BFFill_{r.name}");
            host.transform.SetParent(r.transform, false);
            host.transform.position = pos;

            // Range covers the footprint, so a wide platform is not lit by a
            // bright dot in the middle of an otherwise black deck.
            float range = Mathf.Max(b.extents.x, b.extents.z) * 2f + 4f;

            Light light = PhxRuntimeAssets.CreatePointLight(host, o.Tint, range, o.Lumen);

            HDAdditionalLightData hd = host.GetComponent<HDAdditionalLightData>();
            if (hd != null)
            {
                // Volumetrics OFF. This light is standing in for bounce, and
                // bounce does not make fog glow - on Kashyyyk, where the
                // volumetric multiplier is 1.5, leaving it on hangs a visible
                // ball of haze under every roof.
                hd.affectsVolumetric = false;
                hd.EnableShadows(false);
            }

            Spawned.Add(host);
            ++lit;
        }

        return lit;
    }

    /// <summary>Whether this renderer sits under an instance with that name.</summary>
    /// <remarks>
    /// Walks up rather than testing the renderer's own name, which is the mesh
    /// segment's, not the placement's - the same reason
    /// <see cref="BFMapCollisionOverrides"/> does.
    /// </remarks>
    static bool BelongsTo(Transform t, string prefix)
    {
        while (t != null)
        {
            if (t.name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return true;
            t = t.parent;
        }
        return false;
    }
}
