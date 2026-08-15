using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>One budget, what the tier would have allowed, and where it came from.</summary>
/// <remarks>
/// The source is the whole point. When a map runs at the resolution floor the
/// question is always "which number did that, and was it the tier or the map's
/// own profile?" - and a budget without its provenance sends you back to
/// reading BFPresentationQuality to find out.
/// </remarks>
[Serializable]
public class BFBudgetEntry
{
    public string name;
    public float value;
    public float tierValue;
    public string source;      // "tier" | "map" | "min(tier,map)"
}

[Serializable]
public class BFRenderGeometryStats
{
    public int renderers;
    public int skinned;
    public int shadowCasters;
    public int uniqueMaterials;
    public long triangles;
    public long shadowCasterTriangles;
    public int alphaTestedMaterials;
    public int doubleSidedMaterials;
    public int clothPieces;
    public int authoredLodGroups;
}

[Serializable]
public class BFRenderLightStats
{
    public int total;
    public int directional;
    public int point;
    public int spot;
    public int shadowedDirectional;
    public int shadowedPunctual;
    public int volumetricAffecting;
}

[Serializable]
public class BFRenderProbeStats
{
    public int reflectionPlaced;
    public int reflectionBudget;
    public int reflectionCacheSize;
}

[Serializable]
public class BFRenderResolutionStats
{
    public int screenW;
    public int screenH;
    public int targetW;
    public int targetH;
    public bool dynamicResolutionEnabled;
    public bool dynamicResHardware;
    public float dynamicResMinPercent;
    public float dynamicResMapFloorPercent;
}

[Serializable]
public class BFRenderTierStats
{
    public string name;
    public int ordinal;

    /// <summary>"config" if a config file was read, "default" otherwise.</summary>
    /// <remarks>
    /// Says where the VALUE came from, not that anyone chose it - a config
    /// written out on first run contains the compiled default. Its purpose is
    /// narrower than it looks: it catches the case where presentation
    /// bootstrapped before the config was loaded and silently used defaults.
    /// </remarks>
    public string source;

    /// <summary>
    /// The raw config value before clamping. Differs from ordinal when someone
    /// has written a tier outside the valid range and is silently getting a
    /// different one.
    /// </summary>
    public int requested;
}

[Serializable]
public class BFRenderFeatureStats
{
    public bool volumetrics;
    public bool contactShadows;
    public bool ssr;
    public float ssrMinSmoothness;
    public bool ssgi;
    public bool derivedMaterialMaps;
    // HDRP 17 split the single shadowFilteringQuality into three - punctual,
    // directional and area. Both of the ones this game actually pays for are
    // reported rather than collapsing them back into one number: PCSS is
    // enabled per category now, so "shadow filtering 2" was ambiguous the
    // moment the field was split.
    public int shadowFilteringQualityDirectional;
    public int shadowFilteringQualityPunctual;
    public bool ssgiSupportedByAsset;

    /// <summary>
    /// LOD distance multiplier. Read against geometry.authoredLodGroups: a
    /// high bias with many authored LOD groups means the map is paying for
    /// detail meshes the artists expected to have been swapped out.
    /// </summary>
    public float lodBias;

    /// <summary>Distance past which decals stop rendering, in metres.</summary>
    public float decalDrawDistance;
}

/// <summary>Filled on map exit, not on load - see BFRenderBudgetReport.</summary>
[Serializable]
public class BFRenderMeasuredStats
{
    public float gpuFrameMsMedian;
    public float gpuFrameMsP95;
    public float resolutionPercentMedian;
    public float sampleSeconds;
    public string timingSource;    // "FrameTimingManager" | "wallClock" | "none"
}

[Serializable]
public class BFRenderBudgetData
{
    public string generatedUtc;
    public string worldName;
    public string profileName;

    public BFRenderGeometryStats geometry = new BFRenderGeometryStats();
    public BFRenderLightStats lights = new BFRenderLightStats();
    public BFRenderProbeStats probes = new BFRenderProbeStats();
    public BFRenderResolutionStats resolution = new BFRenderResolutionStats();
    public BFRenderTierStats tier = new BFRenderTierStats();
    public BFRenderFeatureStats features = new BFRenderFeatureStats();
    public BFRenderMeasuredStats measured = new BFRenderMeasuredStats();

    public List<BFBudgetEntry> budgets = new List<BFBudgetEntry>();
    public List<string> warnings = new List<string>();
}

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
/// printed once at load and written as JSON beside the import validation
/// reports, so "performance is poor" can be attached to a number and two
/// commits can be diffed against each other.
///
/// It measures nothing per frame and costs one scene walk per map.
/// </remarks>
public sealed class BFRenderBudgetReport : MonoBehaviour
{
    /// <summary>The most recent report, so map exit can re-emit it with timings.</summary>
    public static BFRenderBudgetData Last { get; private set; }

    void Start()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded += BeginReport;
        }
    }

    void OnDestroy()
    {
        if (PhxGame.Instance != null)
        {
            PhxGame.Instance.OnMapLoaded -= BeginReport;
        }
    }

    void BeginReport()
    {
        StopAllCoroutines();
        StartCoroutine(ReportWhenSettled());
    }

    /// <summary>
    /// Wait for the systems whose output this measures to finish producing it.
    /// </summary>
    /// <remarks>
    /// Reflection probes are placed a frame apart, so a report that runs inside
    /// the OnMapLoaded dispatch counted zero of them on every map ever
    /// recorded. Waiting is not optional: a budget report that reads its own
    /// subjects before they exist is worse than none, because it looks precise.
    /// </remarks>
    IEnumerator ReportWhenSettled()
    {
        BFReflectionProbeManager probes = BFReflectionProbeManager.Instance;

        // Bounded: a manager that never completes must not stall the report
        // forever, and one probe per frame over a generous budget is fast.
        for (int i = 0; i < 600 && probes != null && !probes.PlacementComplete; ++i)
        {
            yield return null;
        }

        yield return new WaitForEndOfFrame();

        Report();
    }

    static readonly List<Material> MaterialScratch = new List<Material>();

    void Report()
    {
        var data = new BFRenderBudgetData
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            worldName = PhxGame.GetEnvironment()?.GetWorldName() ?? string.Empty,
            profileName = BFLightingDirector.Active?.Name ?? "Default",
        };

        CollectGeometry(data);
        CollectLights(data);
        CollectProbes(data);
        CollectSettings(data);

        Last = data;
        MeasurementsApplied = false;
        Emit(data);
    }

    void CollectGeometry(BFRenderBudgetData data)
    {
        Renderer[] renderers = FindObjectsOfType<Renderer>();
        BFRenderGeometryStats g = data.geometry;
        g.renderers = renderers.Length;

        // One set, not two: every id added to the count set was also added to
        // the "distinct materials" set, so they were always identical.
        var materials = new HashSet<int>();

        for (int i = 0; i < renderers.Length; ++i)
        {
            Renderer renderer = renderers[i];
            if (renderer is SkinnedMeshRenderer) ++g.skinned;

            bool casts = renderer.shadowCastingMode != ShadowCastingMode.Off;
            if (casts) ++g.shadowCasters;

            // Into a reused list rather than the sharedMaterials property,
            // which hands back a fresh array per renderer.
            renderer.GetSharedMaterials(MaterialScratch);
            for (int m = 0; m < MaterialScratch.Count; ++m)
            {
                Material mat = MaterialScratch[m];
                if (mat == null) continue;

                // Counted per distinct material, not per renderer using it.
                if (!materials.Add(mat.GetInstanceID())) continue;

                if (mat.IsKeywordEnabled("_ALPHATEST_ON")) ++g.alphaTestedMaterials;
                if (mat.HasProperty("_DoubleSidedEnable") &&
                    mat.GetFloat("_DoubleSidedEnable") > 0.5f) ++g.doubleSidedMaterials;
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
            long tris = 0;
            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
            {
                tris += (long)mesh.GetIndexCount(sub) / 3;
            }

            g.triangles += tris;

            // What the cascades actually redraw, which is the number that
            // decides shadow cost - not the scene's total.
            if (casts) g.shadowCasterTriangles += tris;
        }

        g.uniqueMaterials = materials.Count;
        g.clothPieces = BFClothImporter.PiecesAttached;
        g.authoredLodGroups = ModelLoader.LODGroupsBuilt;

        // The number that most often explains a bad frame on this content.
        // Every renderer is at least one draw call unless something batches or
        // instances it away.
        if (g.renderers > 4000)
        {
            data.warnings.Add($"{g.renderers} renderers - draw calls are the likely bottleneck, " +
                              "not shading. Check static batching actually ran.");
        }
    }

    void CollectLights(BFRenderBudgetData data)
    {
        Light[] lights = FindObjectsOfType<Light>();
        BFRenderLightStats l = data.lights;
        l.total = lights.Length;

        for (int i = 0; i < lights.Length; ++i)
        {
            Light light = lights[i];
            bool casts = light.shadows != LightShadows.None;

            switch (light.type)
            {
                case LightType.Directional:
                    ++l.directional;
                    if (casts) ++l.shadowedDirectional;
                    break;
                case LightType.Point:
                    ++l.point;
                    if (casts) ++l.shadowedPunctual;
                    break;
                case LightType.Spot:
                    ++l.spot;
                    if (casts) ++l.shadowedPunctual;
                    break;
            }

            HDAdditionalLightData hd = light.GetComponent<HDAdditionalLightData>();
            if (hd != null && hd.affectsVolumetric) ++l.volumetricAffecting;
        }

        // A shadowed point light is six shadow renders. This is the single
        // easiest way to accidentally cost a great deal.
        if (l.shadowedPunctual > 0)
        {
            data.warnings.Add($"{l.shadowedPunctual} shadow-casting point/spot light(s) - " +
                              "each is up to six shadow renders.");
        }

        // More than one *casting* directional is the error condition; a map is
        // free to author several so long as only one casts.
        if (l.shadowedDirectional > 1)
        {
            data.warnings.Add($"{l.shadowedDirectional} directional lights casting; HDRP shadows " +
                              "only one and will report a cascade atlas failure every frame.");
        }
    }

    void CollectProbes(BFRenderBudgetData data)
    {
        BFReflectionProbeManager mgr = BFReflectionProbeManager.Instance;
        data.probes.reflectionPlaced = mgr != null ? mgr.Placed : 0;
        data.probes.reflectionBudget = BFPresentationQuality.ReflectionProbeBudget;

        HDRenderPipelineAsset hdrp = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        if (hdrp != null)
        {
            // HDRP 17 replaced reflectionProbeCacheSize - a count of probes the
            // cache held - with a texture ATLAS (reflectionProbeTexCacheSize,
            // a resolution) plus a separate cap on how many cube reflections
            // may be live at once. The old field still exists but is internal
            // and only read by the asset migration.
            //
            // maxCubeReflectionOnScreen is the one that still answers the
            // question this warning asks, because it is still a count of
            // probes. Comparing a probe budget against an atlas resolution
            // would be comparing two different units.
            data.probes.reflectionCacheSize =
                hdrp.currentPlatformRenderPipelineSettings.lightLoopSettings.maxCubeReflectionOnScreen;
        }

        // A budget larger than the cache guarantees the cache thrashes every
        // frame, and nothing else in the engine will say so.
        if (data.probes.reflectionCacheSize > 0 &&
            data.probes.reflectionBudget > data.probes.reflectionCacheSize)
        {
            data.warnings.Add($"reflection probe budget {data.probes.reflectionBudget} exceeds the " +
                              $"pipeline's limit of {data.probes.reflectionCacheSize} cube " +
                              "reflections on screen - probes past that are dropped, not blended.");
        }
    }

    void CollectSettings(BFRenderBudgetData data)
    {
        BFRenderResolutionStats r = data.resolution;
        r.screenW = Screen.width;
        r.screenH = Screen.height;
        r.targetW = PhxBF3.Config.TargetWidth;
        r.targetH = PhxBF3.Config.TargetHeight;

        HDRenderPipelineAsset hdrp = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        if (hdrp != null)
        {
            RenderPipelineSettings s = hdrp.currentPlatformRenderPipelineSettings;

            r.dynamicResolutionEnabled = s.dynamicResolutionSettings.enabled;
            r.dynamicResHardware = s.dynamicResolutionSettings.dynResType == DynamicResolutionType.Hardware;
            r.dynamicResMinPercent = s.dynamicResolutionSettings.minPercentage;
            r.dynamicResMapFloorPercent = BFDynamicResolution.MapFloorPercent;

            data.features.shadowFilteringQualityDirectional =
                (int)s.hdShadowInitParams.directionalShadowFilteringQuality;
            data.features.shadowFilteringQualityPunctual =
                (int)s.hdShadowInitParams.punctualShadowFilteringQuality;
            data.features.ssgiSupportedByAsset = s.supportSSGI;
            data.features.decalDrawDistance = s.decalSettings.drawDistance;
        }

        data.tier.name = BFPresentationQuality.Tier.ToString();
        data.tier.ordinal = (int)BFPresentationQuality.Tier;
        data.tier.source = PhxBF3.ConfigLoadedFromDisk ? "config" : "default";
        data.tier.requested = PhxBF3.Config.PresentationQuality;

        if (data.tier.requested != data.tier.ordinal)
        {
            data.warnings.Add($"PresentationQuality {data.tier.requested} is outside the valid " +
                              $"range and was clamped to {data.tier.ordinal} ({data.tier.name}).");
        }

        BFRenderFeatureStats f = data.features;
        f.volumetrics = BFPresentationQuality.Volumetrics;
        f.contactShadows = BFPresentationQuality.ContactShadows;
        f.ssr = BFPresentationQuality.ScreenSpaceReflections;
        f.ssgi = BFPresentationQuality.ScreenSpaceGlobalIllumination;
        f.derivedMaterialMaps = BFPresentationQuality.DerivedMaterialMaps;
        f.ssrMinSmoothness = BFLightingDirector.Active?.ReflectionMinSmoothness ?? 0f;
        f.lodBias = QualitySettings.lodBias;

        // Authored LOD meshes only pay off if the bias lets them engage. A map
        // with plenty of them and a bias above 1 is rendering detail its own
        // artists expected to have been swapped out by that distance.
        if (data.geometry.authoredLodGroups > 10 && f.lodBias > 1.25f)
        {
            data.warnings.Add($"{data.geometry.authoredLodGroups} authored LOD group(s) against a " +
                              $"LOD bias of {f.lodBias} - the stock low-detail meshes will rarely " +
                              "engage.");
        }

        // Budgets carry the tier's own value alongside the effective one, so
        // the reduce-only rule can be checked rather than assumed.
        AddBudget(data, "decals", BFPresentationQuality.DecalBudget,
                  BFPresentationQuality.TierValueOf("decals"));
        AddBudget(data, "reflectionProbes", BFPresentationQuality.ReflectionProbeBudget,
                  BFPresentationQuality.TierValueOf("reflectionProbes"));
        AddBudget(data, "sunShadowResolution", BFPresentationQuality.SunShadowResolution,
                  BFPresentationQuality.TierValueOf("sunShadowResolution"));
        AddBudget(data, "shadowCascades", BFPresentationQuality.ShadowCascades,
                  BFPresentationQuality.TierValueOf("shadowCascades"));
        AddBudget(data, "shadowCastingPunctual", BFPresentationQuality.ShadowCastingPunctualBudget,
                  BFPresentationQuality.TierValueOf("shadowCastingPunctual"));

        // Bigger is cheaper for this one, so the violation test is inverted -
        // AddBudget handles that by name.
        AddBudget(data, "minShadowCasterRadius", BFPresentationQuality.MinShadowCasterRadius,
                  BFPresentationQuality.TierMinShadowCasterRadius);

        // No per-map cap on these yet; recorded so the schema is stable.
        AddBudget(data, "impactLights", BFPresentationQuality.ImpactLightBudget, BFPresentationQuality.ImpactLightBudget);
        AddBudget(data, "interactionDistance", BFPresentationQuality.InteractionDistance, BFPresentationQuality.InteractionDistance);
        AddBudget(data, "terrainDeformationResolution", BFPresentationQuality.TerrainDeformationResolution, BFPresentationQuality.TerrainDeformationResolution);

        float shadowDistance = (BFLightingDirector.Active?.ShadowDistance ?? 0f)
                             * BFPresentationQuality.ShadowDistanceScale;
        AddBudget(data, "shadowDistanceMetres", shadowDistance, shadowDistance);

        long pixels = (long)Screen.width * Screen.height;
        if (pixels > 3_000_000 && f.ssr)
        {
            data.warnings.Add("4K-class resolution with screen-space effects on. " +
                              "PhxBF3Config.TargetWidth/Height and PresentationQuality are the dials.");
        }

        if (!r.dynamicResolutionEnabled && PhxBF3.Config.UseDynamicResolution)
        {
            data.warnings.Add("config asks for dynamic resolution but the pipeline asset has it " +
                              "disabled - the request does nothing.");
        }
    }

    /// <summary>
    /// Record a budget. Phase-0 callers pass the same number twice; once maps
    /// carry their own caps, effective and tier values diverge and the source
    /// says which won.
    /// </summary>
    static void AddBudget(BFRenderBudgetData data, string name, float value, float tierValue)
    {
        // One budget runs the other way: a larger minimum caster radius
        // discards more shadow casters, so bigger is cheaper there.
        bool biggerIsCheaper = name == "minShadowCasterRadius";

        bool sameAsTier = Mathf.Approximately(value, tierValue);
        string source = sameAsTier
            ? "tier"
            : (biggerIsCheaper ? "max(tier,map)" : "min(tier,map)");

        data.budgets.Add(new BFBudgetEntry
        {
            name = name,
            value = value,
            tierValue = tierValue,
            source = source,
        });

        // The invariant the whole per-map design rests on: a map may lower a
        // cost, never raise one. Cheaper to assert here than to discover it
        // from a frame time six maps later.
        bool raised = biggerIsCheaper ? value < tierValue : value > tierValue;
        if (raised && !sameAsTier)
        {
            data.warnings.Add($"budget '{name}' ({value}) is more expensive than its tier value " +
                              $"({tierValue}) - a map profile raised a cost, which the precedence " +
                              "rule forbids.");
        }
    }

    static void Emit(BFRenderBudgetData data)
    {
        Debug.Log(ToSummary(data));

        try
        {
            string fileName = string.IsNullOrEmpty(data.worldName)
                ? "bf2-render-budget.json"
                : $"bf2-render-budget-{Sanitize(data.worldName)}.json";
            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, JsonUtility.ToJson(data, true));
            Debug.Log($"[BFPerf] render budget written to {path}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFPerf] could not write render budget report: {e.Message}");
        }
    }

    /// <summary>
    /// Re-emit the current report with the timings gathered while it was
    /// played. Load-time counts do not say whether a map was affordable.
    /// </summary>
    /// <summary>Guards against appending the same warnings twice to one report.</summary>
    /// <remarks>
    /// Import can run more than once for a map - a retried load is the obvious
    /// case - and each run re-emits the previous map's report on the way past.
    /// </remarks>
    static bool MeasurementsApplied;

    public static void EmitWithMeasurements(BFRenderMeasuredStats measured)
    {
        if (Last == null || MeasurementsApplied) return;
        MeasurementsApplied = true;

        Last.measured = measured ?? new BFRenderMeasuredStats();

        // Say so in the file, not only once in the console.
        //
        // vsync quantises wall-clock frame time to the refresh interval, which
        // is exactly the signal dynamic resolution needs and exactly what it
        // destroys. Without GPU timings the numbers below are the refresh rate
        // reported back, and any conclusion drawn from them about what a map
        // costs is worthless - which is not obvious from a plausible-looking
        // median sitting next to every other real measurement in the file.
        if (Last.measured.timingSource == "wallClock")
        {
            Last.warnings.Add("GPU timings unavailable; frame cost was measured from wall clock, " +
                              "which vsync quantises. Treat gpuFrameMs* as unreliable and do not " +
                              "tune budgets from them.");
        }
        else if (Last.measured.timingSource == "none")
        {
            Last.warnings.Add("No frame timings were captured for this map - it was probably left " +
                              "before the sampler ran.");
        }

        Emit(Last);
    }

    static string ToSummary(BFRenderBudgetData d)
    {
        var sb = new StringBuilder();
        sb.Append("[BFPerf] Render budget for '").Append(d.worldName)
          .Append("' (profile ").Append(d.profileName).AppendLine("):");

        sb.Append("  renderers      ").Append(d.geometry.renderers)
          .Append("  (skinned ").Append(d.geometry.skinned)
          .Append(", shadow casters ").Append(d.geometry.shadowCasters).AppendLine(")");
        sb.Append("  unique mats    ").Append(d.geometry.uniqueMaterials)
          .Append("  (alpha-tested ").Append(d.geometry.alphaTestedMaterials)
          .Append(", double-sided ").Append(d.geometry.doubleSidedMaterials).AppendLine(")");
        sb.Append("  triangles      ").Append(d.geometry.triangles / 1000)
          .Append("k  (shadow casters ").Append(d.geometry.shadowCasterTriangles / 1000).AppendLine("k)");
        sb.Append("  cloth pieces   ").Append(d.geometry.clothPieces)
          .AppendLine(" authored CLTH piece(s) attached");
        sb.Append("  authored LODs  ").Append(d.geometry.authoredLodGroups)
          .AppendLine(" model(s) with a stock low-detail mesh");

        sb.Append("  lights         ").Append(d.lights.total)
          .Append("  (dir ").Append(d.lights.directional)
          .Append(", point ").Append(d.lights.point)
          .Append(", spot ").Append(d.lights.spot)
          .Append("; casting ").Append(d.lights.shadowedDirectional + d.lights.shadowedPunctual)
          .Append(", volumetric ").Append(d.lights.volumetricAffecting).AppendLine(")");

        sb.Append("  probes         ").Append(d.probes.reflectionPlaced)
          .Append(" placed of budget ").Append(d.probes.reflectionBudget)
          .Append(", cache ").Append(d.probes.reflectionCacheSize).AppendLine();

        sb.Append("  resolution     ").Append(d.resolution.screenW).Append('x').Append(d.resolution.screenH)
          .Append("  (target ").Append(d.resolution.targetW).Append('x').Append(d.resolution.targetH)
          .Append(", dynamic ").Append(d.resolution.dynamicResolutionEnabled)
          .Append(d.resolution.dynamicResHardware ? "/hardware" : "/software").AppendLine(")");

        sb.Append("  quality tier   ").Append(d.tier.name)
          .Append(" (from ").Append(d.tier.source).Append(')')
          .Append("  shadows ").Append((int)Budget(d, "sunShadowResolution"))
          .Append(" x").Append((int)Budget(d, "shadowCascades"))
          .Append(" cascades, decals ").Append((int)Budget(d, "decals"))
          .Append(", impact lights ").Append((int)Budget(d, "impactLights"))
          .AppendLine();

        sb.Append("  features       ")
          .Append("volumetrics ").Append(d.features.volumetrics)
          .Append(", contact shadows ").Append(d.features.contactShadows)
          .Append(", SSR ").Append(d.features.ssr)
          .Append(", SSGI ").Append(d.features.ssgi)
          .Append(d.features.ssgiSupportedByAsset ? "" : " (unsupported by asset)")
          .Append(", shadow filtering dir ").Append(d.features.shadowFilteringQualityDirectional)
          .Append("/punct ").Append(d.features.shadowFilteringQualityPunctual)
          .Append(", derived maps ").Append(d.features.derivedMaterialMaps)
          .AppendLine();

        for (int i = 0; i < d.warnings.Count; ++i)
        {
            sb.Append("  ^ ").AppendLine(d.warnings[i]);
        }

        return sb.ToString();
    }

    static float Budget(BFRenderBudgetData d, string name)
    {
        for (int i = 0; i < d.budgets.Count; ++i)
        {
            if (d.budgets[i].name == name) return d.budgets[i].value;
        }
        return 0f;
    }

    static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }
        return sb.ToString();
    }
}
