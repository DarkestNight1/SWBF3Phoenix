using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Applies each map's authored atmosphere - fog color/range from SkyInfo,
/// sun key/back light from SunInfo, dome ambient - parsed by the world
/// importer into SWBFSkyProperties. This is the per-map fog PhxModernLighting
/// deliberately does not provide globally: a fixed fog density is meaningless
/// across an open battlefield, a ship interior and a vacuum space map alike.
///
/// Attached to the persistent BF3Legacy host by PhxBF3.Bootstrap().
/// </summary>
public class PhxMapAtmosphere : MonoBehaviour
{
    Volume Volume;
    VolumeProfile Profile;
    Fog Fog;

    void Start()
    {
        Profile = ScriptableObject.CreateInstance<VolumeProfile>();
        Profile.name = "BF3LegacyMapAtmosphere";
        Fog = Profile.Add<Fog>(true);
        Fog.enabled.Override(false);

        Volume = gameObject.AddComponent<Volume>();
        Volume.isGlobal = true;
        // Above PhxModernLighting (100): map-authored atmosphere wins over
        // the generic modernization defaults.
        Volume.priority = 110f;
        Volume.profile = Profile;

        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Apply;
        }
    }

    void Apply()
    {
        if (Fog == null) return;

        bool fogAuthored = SWBFSkyProperties.HasSkyInfo &&
                           SWBFSkyProperties.FogRange.y > SWBFSkyProperties.FogRange.x &&
                           SWBFSkyProperties.FogRange.y > 0f;
        if (fogAuthored)
        {
            Fog.enabled.Override(true);
            Fog.color.Override(SWBFSkyProperties.FogColor);
            Fog.colorMode.Override(FogColorMode.ConstantColor);
            // BF2's linear fog start/end mapped onto HDRP's height fog: use the
            // far distance as the attenuation distance so full fog lands where
            // the original renderer reached it.
            Fog.meanFreePath.Override(Mathf.Max(20f, SWBFSkyProperties.FogRange.y));
            Fog.baseHeight.Override(0f);
            Fog.maximumHeight.Override(400f);
            Debug.Log($"[BF3Legacy] Map fog: color {SWBFSkyProperties.FogColor}, range {SWBFSkyProperties.FogRange}");
        }
        else
        {
            Fog.enabled.Override(false);
        }

        if (SWBFSkyProperties.HasSunInfo)
        {
            ApplySun();
        }

        if (SWBFSkyProperties.HasDomeAmbient && RenderSettings.ambientMode == AmbientMode.Trilight)
        {
            // The dome ambient is the sky contribution the original renderer
            // added on top of the .lgt ambient; fold it into the sky term.
            RenderSettings.ambientSkyColor = Color.Lerp(
                RenderSettings.ambientSkyColor, SWBFSkyProperties.DomeAmbient, 0.5f);
        }
    }

    void ApplySun()
    {
        // Find the map's shadow-casting directional light (the importer only
        // enables shadows on the sun) and align it with the authored angle.
        Light sun = null;
        foreach (Light l in FindObjectsOfType<Light>())
        {
            if (l.type != LightType.Directional) continue;
            if (sun == null || l.shadows != LightShadows.None)
            {
                sun = l;
                if (l.shadows != LightShadows.None) break;
            }
        }
        if (sun == null) return;

        Vector2 angle = SWBFSkyProperties.SunAngle;
        // .sky Angle(azimuth, elevation): elevation is negative-down in the
        // authored data, Unity pitches the light down with positive X.
        sun.transform.rotation = Quaternion.Euler(-angle.y, angle.x, 0f);

        if (SWBFSkyProperties.SunColor.maxColorComponent > 0f)
        {
            sun.color = SWBFSkyProperties.SunColor;
        }
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded -= Apply;
        }
        if (Profile != null)
        {
            ScriptableObject.Destroy(Profile);
        }
    }
}
