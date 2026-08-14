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
