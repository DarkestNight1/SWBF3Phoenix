using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Keeps a gameplay "practical" light - a command post holo projector, a room
/// fixture, a console glow - local to the thing it belongs to.
/// </summary>
/// <remarks>
/// A light authored for the 2005 renderer, or dropped into a prefab without
/// HDRP in mind, has three properties that together flood a level:
///
/// <list type="number">
/// <item><b>No shadows.</b> An unshadowed point light passes through every
/// wall inside its range. This is the one that actually looks broken: a
/// command post lights the corridor on the far side of the bulkhead it is
/// standing behind.</item>
/// <item><b>Range far larger than the space it is in.</b> Fifteen metres is a
/// whole hangar, not a projector pool.</item>
/// <item><b>A fully saturated colour.</b> A pure-green light emits in one
/// channel only, so everything it touches goes monochrome - which is why a
/// team-coloured light reads as a filter over the frame rather than as a light
/// in the world. Real coloured practicals are tinted, not primaries.</item>
/// </list>
///
/// Plus a fade distance of ten kilometres, so every post on the map is fully
/// evaluated from anywhere on it.
///
/// This applies one policy to all of them. It is deliberately a runtime
/// configurator rather than an edit to each prefab: the same light is created
/// from a prefab, from imported map data and from code, and prefab values
/// drift. Setting the policy where the light is used means it holds however
/// the light got there.
/// </remarks>
public static class BFLocalLightPolicy
{
    /// <summary>
    /// How far a practical is allowed to reach. Sized to light the object it
    /// belongs to and the ground around it, not the room.
    /// </summary>
    public const float DefaultRange = 7.5f;

    /// <summary>
    /// Camera distance at which the light stops contributing entirely.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than <see cref="ShadowFadeDistance"/>. Shadows are
    /// the expensive part and fade first in most setups - but if the light
    /// outlived its shadows it would start passing through walls again at
    /// exactly the distance where nobody would notice the cause. Fading the
    /// light out first means "no shadow" and "no light" arrive in that order.
    /// </remarks>
    public const float FadeDistance = 40f;

    public const float ShadowFadeDistance = 48f;

    /// <summary>
    /// Shadow map side length. Small on purpose: a point light is six faces,
    /// and a conquest map has six or more command posts. The shadow exists to
    /// stop light crossing a wall, not to resolve fine detail.
    /// </summary>
    public const int ShadowResolution = 256;

    /// <summary>
    /// How far a team colour is pulled toward white.
    /// </summary>
    /// <remarks>
    /// Team identity has to survive - a red post must read as red - but a
    /// primary-channel light turns everything it touches into a silhouette in
    /// that channel. Just under half way keeps the hue obvious and gives the
    /// other two channels enough to preserve material colour.
    /// </remarks>
    public const float ColorDesaturation = 0.45f;

    /// <summary>Volumetric contribution, so a practical does not fog its room.</summary>
    public const float VolumetricDimmer = 0.25f;

    /// <summary>
    /// Constrain a light to its own space. Returns the intensity it settled
    /// at, so callers that fade the light can scale from the right value
    /// rather than from whatever the prefab happened to hold.
    /// </summary>
    /// <param name="maxIntensity">
    /// Ceiling in the light's own unit. The authored value is kept when it is
    /// already below this - an artist dimming a light is a decision; an artist
    /// leaving it at a room-filling default is not.
    /// </param>
    public static float Apply(HDAdditionalLightData data, float range = DefaultRange,
                              float maxIntensity = 250f, bool castShadows = true)
    {
        if (data == null) return 0f;

        Light light = data.GetComponent<Light>();

        data.range = range;
        data.intensity = Mathf.Min(data.intensity, maxIntensity);

        data.fadeDistance = FadeDistance;
        data.shadowFadeDistance = ShadowFadeDistance;
        data.volumetricDimmer = VolumetricDimmer;

        // Range attenuation on: without it the light does not fall off toward
        // the edge of its range at all, so shrinking the range just produces a
        // hard-edged disc of full-brightness light.
        data.applyRangeAttenuation = true;

        if (castShadows && BFPresentationQuality.Tier >= BFQualityTier.Medium)
        {
            data.EnableShadows(true);
            data.SetShadowResolution(ShadowResolution);
            data.shadowUpdateMode = ShadowUpdateMode.OnEnable;
            data.shadowNearPlane = 0.15f;

            if (light != null)
            {
                light.shadows = LightShadows.Soft;
            }
        }
        else if (light != null)
        {
            // No shadow budget: the light must not be able to reach a wall it
            // cannot cast through. Half range is the honest trade - a dimmer,
            // tighter pool rather than a bleeding one.
            data.range = range * 0.5f;
            data.EnableShadows(false);
            light.shadows = LightShadows.None;
        }

        return data.intensity;
    }

    /// <summary>
    /// A team colour usable as light without turning the frame monochrome.
    /// </summary>
    public static Color TintForLight(Color teamColor)
    {
        Color tint = Color.Lerp(teamColor, Color.white, ColorDesaturation);
        tint.a = 1f;
        return tint;
    }
}
