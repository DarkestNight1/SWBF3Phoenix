using UnityEngine;

/// <summary>
/// Decides whether a map's authored skydome or HDRP's procedural sky is the
/// thing the player is actually looking at.
/// </summary>
/// <remarks>
/// The importer builds the dome geometry from <c>DomeInfo</c> and the lighting
/// director puts a physically based sky behind it. Both then render. On Hoth
/// that is roughly right - the dome is a thin haze band and the atmosphere
/// does the work. On Tatooine it is wrong: the dome *is* the sky, a painted
/// one, and a simulated atmosphere behind it either shows through where the
/// dome does not reach or fights it where it does. Worse, ambient light gets
/// derived from an atmosphere the artist never intended, so the whole map is
/// lit for a planet it is not on.
///
/// The two were never reconciled because nothing measured the dome. This does:
/// a dome with real geometry and real coverage is treated as authoritative,
/// the procedural atmosphere is stood down to a flat gradient that matches the
/// map's own authored fog colour, and ambient follows the dome rather than a
/// simulation of a sky the player cannot see.
/// </remarks>
public static class BFSkydomeReconciler
{
    /// <summary>What the last evaluated map decided, for the import report.</summary>
    public static string LastDecision { get; private set; } = "not evaluated";

    /// <summary>True when the authored dome is carrying the sky.</summary>
    public static bool DomeIsSky { get; private set; }

    /// <summary>Renderers found under the dome root on the last evaluation.</summary>
    public static int DomeRendererCount { get; private set; }

    public static void Reset()
    {
        LastDecision = "not evaluated";
        DomeIsSky = false;
        DomeRendererCount = 0;
    }

    /// <summary>
    /// A dome has to clear a bar before it is allowed to override the
    /// atmosphere. A single small band - which several maps ship as a horizon
    /// detail rather than a sky - should not stand down a correct physical sky.
    /// </summary>
    const int MinDomeRenderers = 1;
    const float MinDomeRadius = 200f;

    /// <summary>
    /// Evaluate the loaded scene's dome and return the sky kind the lighting
    /// director should use, or null to leave the profile's own choice alone.
    /// </summary>
    public static BFEnvironmentLightingProfile.BFSkyKind? Evaluate(
        BFEnvironmentLightingProfile profile)
    {
        Reset();
        if (profile == null) return null;

        // Space maps are decided by the profile and nothing about a dome
        // should change that - a starfield backdrop is not an atmosphere.
        if (profile.Sky == BFEnvironmentLightingProfile.BFSkyKind.Space)
        {
            LastDecision = "space profile - dome ignored";
            return null;
        }

        GameObject skyRoot = GameObject.Find("Skydome");
        if (skyRoot == null)
        {
            LastDecision = "no Skydome in scene - keeping procedural sky";
            return null;
        }

        Transform dome = skyRoot.transform.Find("Dome");
        if (dome == null)
        {
            LastDecision = "Skydome has no Dome child - keeping procedural sky";
            return null;
        }

        // Measure rather than assume. A dome that imported to nothing is the
        // normal case for interior-only maps and must not stand anything down.
        Renderer[] renderers = dome.GetComponentsInChildren<Renderer>(true);
        DomeRendererCount = renderers.Length;
        if (renderers.Length < MinDomeRenderers)
        {
            LastDecision = "dome imported no geometry - keeping procedural sky";
            return null;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; ++i)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        float radius = Mathf.Max(bounds.extents.x, bounds.extents.z);
        if (radius < MinDomeRadius)
        {
            LastDecision = $"dome too small to be the sky ({radius:F0}m) - keeping procedural sky";
            return null;
        }

        DomeIsSky = true;
        LastDecision = $"authored dome is the sky ({renderers.Length} renderer(s), {radius:F0}m)";
        return BFEnvironmentLightingProfile.BFSkyKind.AuthoredDome;
    }

    /// <summary>
    /// Ambient basis for a dome-lit map.
    /// </summary>
    /// <remarks>
    /// Sampling the dome's textures would mean readable copies of every sky
    /// texture in the level. The authored fog colour is the cheaper and more
    /// faithful answer: BF2 artists set it to match the horizon their dome
    /// paints, precisely so distant geometry blends into it. If the map
    /// authored no fog, fall back to the profile's own tint.
    /// </remarks>
    public static Color GetDomeAmbient(BFEnvironmentLightingProfile profile)
    {
        if (SWBFSkyProperties.HasSkyInfo)
        {
            return SWBFSkyProperties.FogColor;
        }
        return profile != null ? profile.AtmosphereTint : Color.grey;
    }
}
