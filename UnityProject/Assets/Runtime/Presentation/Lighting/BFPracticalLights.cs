using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Hangs a practical light inside imported world geometry that shelters the
/// space under it, where the source data provides no light of its own.
/// </summary>
/// <remarks>
/// SWBF2 lit its interiors with a baked ambient term that had no notion of
/// occlusion, so a roof cost nothing: the floor under it received the same
/// ambient as open ground. HDRP's sky occlusion is real, and the result is
/// that anything with a lid over it goes to near-black - most visibly on
/// Kashyyyk, where the fight happens on covered platforms several stops below
/// open sky. <see cref="BFLightingDirector"/> already gives exposure room to
/// open up for that, but adaptation can only lift what the renderer actually
/// put there, and under a solid canopy that is almost nothing.
///
/// This is deliberately a small keyed table rather than a general "is this
/// object enclosed" test. Deriving shelter from geometry means measuring
/// occlusion per instance at load time, and getting it wrong in the permissive
/// direction puts a light inside every crate and rock on the map. The set of
/// models that actually roof a playable space is short, known, and per-map -
/// so it is written down.
///
/// The light is created through <see cref="PhxRuntimeAssets.CreatePointLight"/>
/// and then handed to <see cref="BFLocalLightPolicy"/>, which owns range, fade,
/// volumetric contribution and the shadow budget. Nothing here sets those
/// directly: a practical added by this pass has to live under the same rules as
/// one that arrived from a prefab or from the command post code, or it becomes
/// the next light that bleeds through a wall.
/// </remarks>
public static class BFPracticalLights
{
    /// <summary>How a sheltering model should be lit underneath.</summary>
    public struct Spec
    {
        /// <summary>Point light intensity, in lumen.</summary>
        public float IntensityLumen;

        public Color Color;

        /// <summary>
        /// How far below the underside of the lid the light hangs.
        /// </summary>
        /// <remarks>
        /// Measured from the underside of the topmost renderer, not from the
        /// model's peak and not from its base, because neither of those is
        /// reliably the ceiling of the space being lit. A roof model may or may
        /// not include its support posts: when it does, the base is the ground;
        /// when it does not, the base IS the underside and the space to light
        /// is entirely below the model's bounds. Dropping from the lid's own
        /// underside puts the light in open air under the canopy in both cases,
        /// which is also where a lantern would hang.
        ///
        /// A light placed at or above the underside is inside the roof mesh and
        /// lights nothing but the inside of the roof.
        /// </remarks>
        public float DropBelowLid;

        /// <summary>
        /// Range as a multiple of the model's larger horizontal extent, so one
        /// entry covers a small hut and a large platform without separate
        /// tuning. Clamped by <see cref="MinRange"/>/<see cref="MaxRange"/>.
        /// </summary>
        public float RangeFactor;
    }

    const float MinRange = 6f;
    const float MaxRange = 20f;

    /// <summary>
    /// Keyed by odf entity class name, lower case - the class, not the
    /// instance name, because a world file names most instances by number or
    /// not at all, and every placement of the same model wants the same light.
    /// </summary>
    static readonly Dictionary<string, Spec> ByEntityClass = new Dictionary<string, Spec>
    {
        // Kashyyyk village platforms. A solid wooden canopy over the walkable
        // deck, warm because the map's own light is warm and a neutral fill
        // under it reads as moonlight at midday.
        //
        // Verified against the shipped data rather than guessed: enumerating
        // kas2.lvl gives this name WITH a ".msh" suffix, at 4 instances. The
        // key here is the unsuffixed form and Lookup strips the extension
        // before matching - an exact-match table keyed on the bare string
        // against a suffixed name matches nothing at all, silently, which is
        // how this entry did nothing the first time it was written.
        ["kas2_bldg_platform_roof"] = new Spec
        {
            IntensityLumen = 2600f,
            Color = new Color(1f, 0.92f, 0.78f),
            DropBelowLid = 0.5f,
            RangeFactor = 1.8f,
        },

        // The Kashyyyk main doorway. Dimmer and tighter than the platform: a
        // door is a threshold rather than a room, and the job is to stop the
        // opening reading as a black rectangle, not to light what is behind
        // it. DropBelowLid puts it just inside the head of the frame.
        //
        // NOTE: unverified class name - see the report below. If this key is
        // wrong the entry is a silent no-op, which is exactly what the load
        // report exists to catch.
        ["door_main"] = new Spec
        {
            IntensityLumen = 1800f,
            Color = new Color(1f, 0.89f, 0.76f),
            DropBelowLid = 0.4f,
            RangeFactor = 2.2f,
        },
    };

    /// <summary>How many instances each entry actually matched this load.</summary>
    /// <remarks>
    /// A key that matches nothing is indistinguishable from a key that matches
    /// something unlit, because both produce no visible change - and the odf
    /// class names these are keyed on are not discoverable from the Unity side
    /// at all. Counting matches turns "the light did not appear" into either
    /// "0 instances, the name is wrong" or "12 instances, the name is right and
    /// the light needs tuning", which are different problems.
    /// </remarks>
    static readonly Dictionary<string, int> MatchCounts = new Dictionary<string, int>();

    /// <summary>
    /// Find the entry for an imported name, tolerating the forms the same
    /// asset arrives under.
    /// </summary>
    /// <remarks>
    /// The name that reaches here is whatever the world file named the entity
    /// class, and that is not one consistent spelling. The Kashyyyk roof
    /// enumerates out of kas2.lvl as "kas2_bldg_platform_roof.msh"; other
    /// placements of the same asset arrive unsuffixed, and geometry names
    /// carry a segment suffix on top of that. So: lower-case, drop a trailing
    /// .msh/.odf, then try an exact hit before falling back to a prefix match.
    ///
    /// The prefix fallback is last and not first. It is what makes a suffixed
    /// or segmented name resolve, but it will also match a longer, unrelated
    /// class that happens to begin with a key, so an exact hit must win when
    /// there is one.
    /// </remarks>
    static bool Lookup(string entityClassName, out string key, out Spec spec)
    {
        string name = entityClassName.ToLowerInvariant();

        if (name.EndsWith(".msh")) name = name.Substring(0, name.Length - 4);
        else if (name.EndsWith(".odf")) name = name.Substring(0, name.Length - 4);

        if (ByEntityClass.TryGetValue(name, out spec))
        {
            key = name;
            return true;
        }

        foreach (KeyValuePair<string, Spec> entry in ByEntityClass)
        {
            if (name.StartsWith(entry.Key))
            {
                key = entry.Key;
                spec = entry.Value;
                return true;
            }
        }

        key = null;
        spec = default;
        return false;
    }

    /// <summary>
    /// Add the practical for this entity class, if it has one. Safe to call
    /// for every imported instance; classes with no entry cost a dictionary
    /// miss.
    /// </summary>
    public static void TryAttach(GameObject instance, string entityClassName)
    {
        if (instance == null || string.IsNullOrEmpty(entityClassName)) return;

        // Fill light is a fidelity feature, not a correctness one. A machine on
        // the bottom tier is already dropping local lights the map asked for by
        // name, and should not then be handed lights the map never asked for.
        if (!BFPresentationQuality.ImpactLights) return;

        if (!Lookup(entityClassName, out string key, out Spec spec))
        {
            return;
        }

        MatchCounts.TryGetValue(key, out int seen);
        MatchCounts[key] = seen + 1;

        // Bounds come from the renderers rather than the colliders: the
        // collision mesh of a roof is frequently a simplified box that extends
        // to the ground, which would put the light at the wrong height.
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; ++i)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        // The lid is the topmost renderer - the canopy itself. Anything below
        // it is post, rail or deck, and its own underside is the ceiling of
        // the space that needs the light.
        Renderer lid = renderers[0];
        for (int i = 1; i < renderers.Length; ++i)
        {
            if (renderers[i].bounds.max.y > lid.bounds.max.y) lid = renderers[i];
        }

        float y = lid.bounds.min.y - spec.DropBelowLid;

        float footprint = Mathf.Max(bounds.extents.x, bounds.extents.z);
        float range = Mathf.Clamp(footprint * spec.RangeFactor, MinRange, MaxRange);

        GameObject go = new GameObject("PracticalLight");
        go.transform.SetParent(instance.transform, false);
        // Set in world space, then let the transform work out the local offset.
        // The instance is reparented under the world root after this runs, and
        // a local offset survives that where a world position would not.
        go.transform.position = new Vector3(bounds.center.x, y, bounds.center.z);

        Light light = PhxRuntimeAssets.CreatePointLight(go, spec.Color, range,
                                                       spec.IntensityLumen,
                                                       castShadows: true);
        if (light == null) return;

        HDAdditionalLightData data = go.GetComponent<HDAdditionalLightData>();
        if (data == null) return;

        // maxIntensity is the value we just asked for, not the policy default.
        // Apply's ceiling is expressed in the light's own unit and its default
        // is sized for the command post projectors, which are authored an order
        // of magnitude dimmer than a room light in lumen - passing it through
        // unchanged would clamp this to a glow.
        BFLocalLightPolicy.Apply(data, range, maxIntensity: spec.IntensityLumen,
                                 castShadows: true);
    }

    /// <summary>Clear the per-load match tally. Call before importing a world.</summary>
    public static void ResetReport()
    {
        MatchCounts.Clear();
    }

    /// <summary>
    /// One line per configured entry, after the world is in: how many
    /// instances it lit. A zero means the odf class name is wrong for this
    /// map and the entry did nothing.
    /// </summary>
    public static void Report()
    {
        foreach (KeyValuePair<string, Spec> entry in ByEntityClass)
        {
            MatchCounts.TryGetValue(entry.Key, out int count);
            if (count > 0)
            {
                Debug.Log($"[BFPracticalLights] '{entry.Key}': lit {count} instance(s).");
            }
            else
            {
                Debug.LogWarning($"[BFPracticalLights] '{entry.Key}': matched NO instances " +
                                 "on this map - either the odf class name is wrong, or this " +
                                 "map does not use it.");
            }
        }
    }
}
