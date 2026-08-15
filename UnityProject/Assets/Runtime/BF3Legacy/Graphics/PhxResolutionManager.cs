using UnityEngine;

/// <summary>
/// 4K graphics upgrade: applies the configured target resolution, maximum
/// texture quality and filtering, and (optionally) dynamic resolution scaling
/// to hold frame rate at 4K.
///
/// SWBF2's assets are loaded at their native resolution by LibSWBF2; rendering
/// them through HDRP at 3840x2160 with full anisotropic filtering and no
/// texture mip-biasing already gives the "remaster" look; texture *upscaling*
/// (e.g. pre-upscaled texture packs via the mod system) plugs in on top of this.
///
/// Attached to the persistent BF3Legacy host by PhxBF3.Bootstrap() when
/// Config.HighResolutionMode is enabled.
/// </summary>
public class PhxResolutionManager : MonoBehaviour
{
    void Start()
    {
        PhxBF3Config cfg = PhxBF3.Config;

        // Only switch if the display can actually do it
        Resolution best = GetBestResolution(cfg.TargetWidth, cfg.TargetHeight);
        Screen.SetResolution(best.width, best.height, FullScreenMode.FullScreenWindow);

        // Full-resolution textures, best filtering
        QualitySettings.masterTextureLimit = 0;
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        // lodBias is set by BFPresentationQuality.Apply, per tier, so that it
        // stays with the other budgets rather than being a constant here that
        // silently overrides them. It was 2f, which cancelled most of the value
        // of the stock low-detail meshes.

        // Frame pacing. Without this the project runs every quality tier at
        // vSyncCount 0, and an uncapped borderless-fullscreen window presents
        // out of step with the compositor - judder that reads as a low frame
        // rate even when the frame rate is high.
        // Physics rate, set here because it is the same concern as frame
        // pacing: how often the world advances against how often it is shown.
        // Left at Unity's 50 Hz default, a soldier's position steps fifty
        // times a second under a display running at two or three times that,
        // and the camera pinned to that position judders while the animation
        // riding on top of it stays smooth.
        if (cfg.PhysicsRateHz > 0)
        {
            float step = 1f / Mathf.Clamp(cfg.PhysicsRateHz, 30, 240);
            Time.fixedDeltaTime = step;

            // Keep the catch-up ceiling proportional. maximumDeltaTime is how
            // much simulation one frame may be asked to make up, and leaving
            // it at the default while shortening the step lets a single long
            // frame queue a dozen physics ticks - which presents as a lurch
            // rather than the stutter it was meant to prevent.
            Time.maximumDeltaTime = Mathf.Max(step * 6f, 0.1f);

            Debug.Log($"[BF3Legacy] Physics rate {cfg.PhysicsRateHz} Hz " +
                      $"(step {step * 1000f:F1} ms).");
        }

        QualitySettings.vSyncCount = cfg.VSync ? 1 : 0;
        Application.targetFrameRate = cfg.VSync ? -1
                                                : (cfg.TargetFrameRate > 0 ? cfg.TargetFrameRate : -1);

        Debug.Log($"[BF3Legacy] Resolution set to {best.width}x{best.height} " +
                  $"(requested {cfg.TargetWidth}x{cfg.TargetHeight})");
    }

    static Resolution GetBestResolution(int targetWidth, int targetHeight)
    {
        Resolution[] all = Screen.resolutions;
        Resolution best = Screen.currentResolution;

        // pick the largest supported resolution not exceeding the target
        int bestPixels = 0;
        foreach (Resolution r in all)
        {
            int pixels = r.width * r.height;
            if (r.width <= targetWidth && r.height <= targetHeight && pixels > bestPixels)
            {
                bestPixels = pixels;
                best = r;
            }
        }
        return best;
    }
}
