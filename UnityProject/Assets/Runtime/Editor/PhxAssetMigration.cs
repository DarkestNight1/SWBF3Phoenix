#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Force every serialized asset to be rewritten with the current engine's
/// serialization, running each type's own migration on the way through.
/// </summary>
/// <remarks>
/// Needed because an engine upgrade does NOT rewrite assets by itself. HDRP
/// migrates its pipeline asset when the asset is loaded - see
/// HDRenderPipelineAsset.Migration.cs - but the migrated result only reaches
/// disk if something marks it dirty and saves. A batchmode import with -quit
/// does neither, so the file on disk keeps the old schema while the running
/// editor holds the new one. The two disagree silently, and the disagreement
/// survives into source control.
///
/// That matters here more than usual: the pipeline asset carries the entire
/// per-map render budget (shadow atlas, probe cache, dynamic resolution
/// bounds, shadow filtering quality), and HDRP 17 restructured several of
/// those fields - reflectionProbeCacheSize became a texture atlas plus an
/// on-screen cap, and shadowFilteringQuality split into punctual, directional
/// and area. Left unmigrated, the values silently fall back to defaults.
///
/// Run with:
///   Unity.exe -batchmode -quit -projectPath &lt;project&gt; \
///             -executeMethod PhxAssetMigration.ReserializeAll
/// </remarks>
public static class PhxAssetMigration
{
    [MenuItem("Phoenix/Upgrade/Reserialize All Assets")]
    public static void ReserializeAll()
    {
        string[] all = AssetDatabase.FindAssets("", new[] { "Assets" });
        var paths = new string[all.Length];
        for (int i = 0; i < all.Length; ++i)
        {
            paths[i] = AssetDatabase.GUIDToAssetPath(all[i]);
        }

        Debug.Log($"[PhxUpgrade] Reserializing {paths.Length} asset(s) with " +
                  $"{Application.unityVersion} serialization.");

        // ForceReserializeAssets loads each asset with the CURRENT engine,
        // which is what triggers the per-type migration, and writes it back.
        AssetDatabase.ForceReserializeAssets(paths, ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata);
        AssetDatabase.SaveAssets();

        Debug.Log("[PhxUpgrade] Reserialize complete.");
    }
}
#endif
