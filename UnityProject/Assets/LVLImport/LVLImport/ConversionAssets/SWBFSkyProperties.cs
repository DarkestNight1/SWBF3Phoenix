using UnityEngine;

/// <summary>
/// Per-map atmosphere data parsed from the world's Skydome config (.sky at
/// munge time): fog, sun/backlight and dome ambient. The loader only records
/// it here; applying it (HDRP fog volume, light tuning) is runtime policy and
/// lives in the BF3Legacy layer. Reset and refilled on every world import.
/// </summary>
public static class SWBFSkyProperties
{
    public static bool HasSkyInfo;
    public static Color FogColor;
    public static Vector2 FogRange;          // near, far (0,0 = no fog authored)
    public static Vector2 FarSceneRange;     // PC() FarSceneRange: far scene start, range

    public static bool HasSunInfo;
    public static Vector2 SunAngle;          // azimuth, elevation (degrees)
    public static Color SunColor;
    public static Vector2 SunBackAngle;
    public static Color SunBackColor;

    public static bool HasDomeAmbient;
    public static Color DomeAmbient;

    public static void Reset()
    {
        HasSkyInfo = false;
        HasSunInfo = false;
        HasDomeAmbient = false;
        FogColor = Color.clear;
        FogRange = Vector2.zero;
        FarSceneRange = Vector2.zero;
        SunAngle = Vector2.zero;
        SunColor = Color.clear;
        SunBackAngle = Vector2.zero;
        SunBackColor = Color.clear;
        DomeAmbient = Color.clear;
    }
}

/// <summary>
/// Keeps a skydome centered on the camera, scaled by the authored
/// MovementScale (~0.995 in stock data: the dome very nearly follows the
/// camera, leaving a hint of parallax). Without this the dome was parented to
/// the world at a fixed spot - a finite object the player could walk toward.
/// </summary>
public class SWBFSkydomeFollow : MonoBehaviour
{
    public float MovementScale = 0.995f;
    public Vector3 BasePosition;

    void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        transform.position = BasePosition + cam.transform.position * MovementScale;
    }
}
