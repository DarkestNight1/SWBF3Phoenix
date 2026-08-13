using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Reports each map's authored atmosphere. Fog itself is owned by
/// <see cref="BFLightingDirector"/>.
/// </summary>
/// <remarks>
/// This used to own a Fog override on a priority-110 volume, reading the
/// map's authored FogColor and setting ConstantColor - the correct, faithful
/// behaviour. BFLightingDirector then ran at priority 120 and overrode
/// colorMode back to SkyColor, so the authored colour was read, logged, and
/// thrown away on every map. The log said the fog was right while the frame
/// showed a generic sky-tinted wash.
///
/// Two volumes writing one parameter is the bug, not the priority ordering.
/// The director now reads the authored colour itself, and this keeps only the
/// diagnostic - which is worth having, because it reports what the source
/// data said independently of what the renderer did with it.
/// </remarks>
public class PhxMapAtmosphere : MonoBehaviour
{
    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Apply;
        }
    }

    void Apply()
    {
        bool fogAuthored = SWBFSkyProperties.HasSkyInfo &&
                           SWBFSkyProperties.FogRange.y > SWBFSkyProperties.FogRange.x &&
                           SWBFSkyProperties.FogRange.y > 0f;

        Debug.Log(fogAuthored
            ? $"[BF3Legacy] Map authored fog: colour {SWBFSkyProperties.FogColor}, " +
              $"linear range {SWBFSkyProperties.FogRange} - applied by BFLightingDirector."
            : "[BF3Legacy] Map authored no fog.");

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

    /// <summary>
    /// Report the authored sun. BFLightingDirector applies it.
    /// </summary>
    /// <remarks>
    /// This used to set the sun's colour and rotation directly, and so did
    /// BFLightingDirector - as plain property writes on the Light, not volume
    /// overrides, so nothing arbitrated between them. Volume priority does not
    /// apply to a direct assignment: whichever OnMapLoaded handler ran last
    /// won, and that order follows which host component happened to be
    /// constructed first. The sun's colour on every map was decided by
    /// component construction order.
    ///
    /// The director now reads the authored values itself and prefers them over
    /// its profile, so there is one writer and the source data wins.
    /// </remarks>
    void ApplySun()
    {
        Debug.Log($"[BF3Legacy] Map authored sun: colour {SWBFSkyProperties.SunColor}, " +
                  $"angle {SWBFSkyProperties.SunAngle} - applied by BFLightingDirector.");
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded -= Apply;
        }
    }
}
