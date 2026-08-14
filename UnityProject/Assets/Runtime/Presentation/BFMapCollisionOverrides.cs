using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Corrections to collision that a map's own data gets wrong.
/// </summary>
/// <remarks>
/// Deliberately narrow, and deliberately not an import rule.
///
/// The case this exists for is Death Star II's doors. Measured, the map ships
/// six instances of DS2_FAKE_DOOR - a bare prop whose only meaningful property
/// is its geometry name - and the model behind it carries a 64-vertex collision
/// mesh. There is no door class anywhere in the map, no world animation, no
/// animation group, and no reference to a door from any of its scripts. So
/// nothing opens them, and players walk into a wall where the level obviously
/// intends a doorway.
///
/// It is tempting to fix that in the importer, and every version of that is
/// wrong. The collision mesh has no mask chunk, and LibSWBF2 reports an absent
/// mask as "All" - but absent is the NORMAL case, not a special one: 169 of 169
/// collision entries on Tatooine have no mask, 567 of 598 on the Death Star.
/// Treating "no mask" as anything other than solid would take the floor out
/// from under most of the game. A name-based rule is no better: no stock map
/// contains a single model with "fake" in its name, so the convention this mod
/// is using is its own and cannot be generalised from the shipped data.
///
/// What is left is what this is - a short list of map data that is known wrong,
/// carried in code so it ships with the build and is visible in review, and
/// applied at runtime so nothing about the import path changes. If it grows
/// past a screenful, that is the signal that it wanted to be a data file after
/// all.
/// </remarks>
public sealed class BFMapCollisionOverrides : MonoBehaviour
{
    /// <summary>An instance whose collision should be removed entirely.</summary>
    struct VisualOnly
    {
        public string ScriptPrefix;     // map, by mission-script prefix
        public string InstancePrefix;   // instance name, matched case-insensitively
        public string Why;
    }

    static readonly VisualOnly[] Overrides =
    {
        new VisualOnly
        {
            ScriptPrefix = "DS2",
            InstancePrefix = "ds2_fake_door",
            Why = "solid door with nothing in the map to open it - no door class, " +
                  "no animation and no script reference",
        },
    };

    void Start()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded += Apply;
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null) PhxGame.Instance.OnMapLoaded -= Apply;
    }

    void Apply()
    {
        string world = PhxGame.GetEnvironment()?.GetWorldName();
        if (string.IsNullOrEmpty(world)) return;

        for (int i = 0; i < Overrides.Length; ++i)
        {
            VisualOnly o = Overrides[i];
            if (!world.StartsWith(o.ScriptPrefix, System.StringComparison.OrdinalIgnoreCase)) continue;

            int cleared = ApplyVisualOnly(o.InstancePrefix);
            if (cleared > 0)
            {
                Debug.Log($"[BFPresentation] Collision override on '{world}': made {cleared} " +
                          $"collider(s) under '{o.InstancePrefix}*' non-blocking - {o.Why}.");
            }
        }
    }

    /// <summary>
    /// Strip collision from every instance whose name starts with the prefix.
    /// </summary>
    /// <remarks>
    /// Colliders are disabled rather than moved to another layer. A doorway
    /// should not stop a body OR a blaster bolt, and disabling says that once
    /// instead of depending on every row of the layer matrix agreeing with it.
    /// The renderers are untouched, so the door still looks like a door.
    /// </remarks>
    static int ApplyVisualOnly(string instancePrefix)
    {
        int cleared = 0;

        // Colliders rather than the scene root: these instances are nested
        // under their world layer, and inactive while the map is still
        // settling, which GameObject.Find would miss on both counts.
        Collider[] all = FindObjectsOfType<Collider>(true);
        for (int i = 0; i < all.Length; ++i)
        {
            Collider c = all[i];
            if (c == null || c.isTrigger) continue;

            if (!BelongsTo(c.transform, instancePrefix)) continue;

            c.enabled = false;
            ++cleared;
        }

        return cleared;
    }

    /// <summary>Whether this collider sits under an instance with that name.</summary>
    static bool BelongsTo(Transform t, string prefix)
    {
        // Walk up rather than testing the collider's own name: the collision
        // node is a child of the instance and is named after the collision
        // mesh, not after the placement.
        while (t != null)
        {
            if (t.name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return true;
            t = t.parent;
        }
        return false;
    }
}
