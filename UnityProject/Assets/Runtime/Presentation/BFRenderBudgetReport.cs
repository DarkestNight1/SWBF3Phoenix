using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Reports what the renderer is actually being asked to do, once per map.
/// </summary>
/// <remarks>
/// Every performance decision in this project so far has been made by
/// reasoning about the code rather than by measuring, because there is no
/// profiler in the loop. That produces plausible fixes for the wrong problem:
/// a shadow cascade cap is worthless if the real cost is ten thousand draw
/// calls, and neither shows up in a screenshot.
///
/// This is the cheap substitute - the counts that actually determine cost,
/// printed once at load, so "performance is poor" can be attached to a number.
/// It measures nothing per frame and costs one scene walk per map.
/// </remarks>
public sealed class BFRenderBudgetReport : MonoBehaviour
{
    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += Report;
        }
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded -= Report;
        }
    }

    void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[BFPerf] Render budget for this map:");

        ReportGeometry(sb);
        ReportLights(sb);
        ReportSettings(sb);

        Debug.Log(sb.ToString());
    }

    static readonly List<Material> MaterialScratch = new List<Material>();

    void ReportGeometry(StringBuilder sb)
    {
        Renderer[] renderers = FindObjectsOfType<Renderer>();

        int skinned = 0;
        int shadowCasters = 0;
        long triangles = 0;
        var materials = new HashSet<int>();

        for (int i = 0; i < renderers.Length; ++i)
        {
            Renderer renderer = renderers[i];
            if (renderer is SkinnedMeshRenderer) ++skinned;
            if (renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) ++shadowCasters;

            // Into a reused list rather than the sharedMaterials property,
            // which hands back a fresh array per renderer.
            renderer.GetSharedMaterials(MaterialScratch);
            for (int m = 0; m < MaterialScratch.Count; ++m)
            {
                if (MaterialScratch[m] != null) materials.Add(MaterialScratch[m].GetInstanceID());
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh
                                       : (renderer as SkinnedMeshRenderer)?.sharedMesh;
            if (mesh == null) continue;

            // GetIndexCount, not the triangles array. Reading .triangles
            // copies the whole index buffer out of the mesh - about a megabyte
            // and a half of garbage across a map, to produce a single number -
            // and it throws outright on any mesh the importer marked
            // non-readable, which would make this report fail on exactly the
            // content it exists to measure.
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
            {
                triangles += (long)mesh.GetIndexCount(sub) / 3;
            }
        }

        sb.Append("  renderers      ").Append(renderers.Length)
          .Append("  (skinned ").Append(skinned)
          .Append(", shadow casters ").Append(shadowCasters).AppendLine(")");
        sb.Append("  unique mats    ").Append(materials.Count).AppendLine();
        sb.Append("  triangles      ").Append(triangles / 1000).AppendLine("k");
        sb.Append("  cloth pieces   ").Append(BFClothImporter.PiecesAttached)
          .AppendLine(" authored CLTH piece(s) attached");
        sb.Append("  authored LODs  ").Append(ModelLoader.LODGroupsBuilt)
          .AppendLine(" model(s) with a stock low-detail mesh");

        // The number that most often explains a bad frame on this content.
        // Every renderer is at least one draw call unless something batches or
        // instances it away.
        if (renderers.Length > 4000)
        {
            sb.AppendLine("  ^ high renderer count - draw calls are the likely bottleneck, " +
                          "not shading. Check static batching actually ran.");
        }
    }

    void ReportLights(StringBuilder sb)
    {
        Light[] lights = FindObjectsOfType<Light>();

        int directional = 0, point = 0, spot = 0;
        int shadowed = 0, shadowedPunctual = 0;

        for (int i = 0; i < lights.Length; ++i)
        {
            Light light = lights[i];
            bool casts = light.shadows != LightShadows.None;
            if (casts) ++shadowed;

            switch (light.type)
            {
                case LightType.Directional: ++directional; break;
                case LightType.Point: ++point; if (casts) ++shadowedPunctual; break;
                case LightType.Spot: ++spot; if (casts) ++shadowedPunctual; break;
            }
        }

        sb.Append("  lights         ").Append(lights.Length)
          .Append("  (dir ").Append(directional)
          .Append(", point ").Append(point)
          .Append(", spot ").Append(spot)
          .Append("; casting ").Append(shadowed).AppendLine(")");

        // A shadowed point light is six shadow renders. This is the single
        // easiest way to accidentally cost a great deal.
        if (shadowedPunctual > 0)
        {
            sb.Append("  ^ ").Append(shadowedPunctual)
              .AppendLine(" shadow-casting point/spot light(s) - each is up to six shadow renders.");
        }
        // Count how many directionals actually cast, not how many exist. A map
        // is free to author several; only more than one *casting* is the error
        // condition, and the previous wording reported a healthy scene as a
        // problem.
        int shadowedDirectional = 0;
        for (int i = 0; i < lights.Length; ++i)
        {
            if (lights[i].type == LightType.Directional && lights[i].shadows != LightShadows.None)
            {
                ++shadowedDirectional;
            }
        }
        if (shadowedDirectional > 1)
        {
            sb.Append("  ^ ").Append(shadowedDirectional)
              .AppendLine(" directional lights casting; HDRP shadows only one and will report a "
                          + "cascade atlas failure every frame.");
        }
    }

    void ReportSettings(StringBuilder sb)
    {
        sb.Append("  resolution     ").Append(Screen.width).Append('x').Append(Screen.height)
          .Append("  (target ").Append(PhxBF3.Config.TargetWidth).Append('x')
          .Append(PhxBF3.Config.TargetHeight).AppendLine(")");

        sb.Append("  quality tier   ").Append(BFPresentationQuality.Tier)
          .Append("  shadows ").Append(BFPresentationQuality.SunShadowResolution)
          .Append(" x").Append(BFPresentationQuality.ShadowCascades)
          .Append(" cascades, decals ").Append(BFPresentationQuality.DecalBudget)
          .Append(", impact lights ").Append(BFPresentationQuality.ImpactLightBudget)
          .AppendLine();

        sb.Append("  features       ")
          .Append("volumetrics ").Append(BFPresentationQuality.Volumetrics)
          .Append(", contact shadows ").Append(BFPresentationQuality.ContactShadows)
          .Append(", SSR ").Append(BFPresentationQuality.ScreenSpaceReflections)
          .Append(", SSGI ").Append(BFPresentationQuality.ScreenSpaceGlobalIllumination)
          .Append(", derived maps ").Append(BFPresentationQuality.DerivedMaterialMaps)
          .AppendLine();

        // 4K with the full screen-space stack is a large ask, and the default
        // config requests it without saying so anywhere the player would look.
        long pixels = (long)Screen.width * Screen.height;
        if (pixels > 3_000_000 && BFPresentationQuality.ScreenSpaceReflections)
        {
            sb.AppendLine("  ^ 4K-class resolution with screen-space effects on. " +
                          "PhxBF3Config.TargetWidth/Height and PresentationQuality are the dials.");
        }
    }
}
