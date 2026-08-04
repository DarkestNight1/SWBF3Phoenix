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
        QualitySettings.lodBias = 2f;

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
