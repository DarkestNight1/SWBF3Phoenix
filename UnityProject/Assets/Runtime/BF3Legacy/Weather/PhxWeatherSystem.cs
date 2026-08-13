using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-map weather, time-of-day and atmospheric particles for every map -
/// stock, BF3 greybox and addon/mod maps. All effects are generated at
/// runtime (no assets): camera-following particle emitters for snow / rain /
/// ash / dust / spores, fog tuning, lightning storms, and a day/dusk/night
/// pass over the map's directional lights.
///
/// Profiles map SWBF2 planet prefixes to sensible weather: Hoth snows, Kamino
/// storms, Mustafar rains embers, Felucia drifts spores, Dagobah sits in fog.
///
/// This applies to BF3 Legacy and addon maps only. Stock maps ship finished
/// lighting and sky data, and overriding it is how a bright daytime Coruscant
/// became a dark rainy one - see Apply.
/// </summary>
public class PhxWeatherSystem : MonoBehaviour
{
    public enum PhxPrecipitation { None, Rain, Snow, Ash, Dust, Spores, Mist }
    public enum PhxTimeOfDay { Auto, Day, Dusk, Night }

    class PhxWeatherProfile
    {
        public PhxPrecipitation Precipitation = PhxPrecipitation.None;
        public float Intensity = 1f;            // emission scale
        public bool Storm;                      // lightning + thunder flashes
        public float FogDensityScale = 1f;      // relative fog thickening
        public PhxTimeOfDay ForcedTime = PhxTimeOfDay.Auto;
        public Color ParticleTint = Color.white;
    }

    static readonly Dictionary<string, PhxWeatherProfile> Profiles = new Dictionary<string, PhxWeatherProfile>
    {
        { "hot", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Snow, Intensity = 1.6f, ParticleTint = Color.white, ForcedTime = PhxTimeOfDay.Day } },
        { "kam", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Rain, Intensity = 2.2f, Storm = true, FogDensityScale = 1.6f, ForcedTime = PhxTimeOfDay.Dusk, ParticleTint = new Color(0.75f, 0.85f, 1f) } },
        { "dag", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Mist, Intensity = 1.2f, FogDensityScale = 2.5f, ForcedTime = PhxTimeOfDay.Dusk, ParticleTint = new Color(0.75f, 0.8f, 0.7f) } },
        { "yav", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Mist, Intensity = 0.7f, FogDensityScale = 1.3f, ParticleTint = new Color(0.8f, 0.9f, 0.75f) } },
        { "end", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Mist, Intensity = 0.5f, FogDensityScale = 1.2f, ParticleTint = new Color(0.8f, 0.85f, 0.8f) } },
        { "geo", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Dust, Intensity = 1.4f, FogDensityScale = 1.4f, ForcedTime = PhxTimeOfDay.Day, ParticleTint = new Color(0.85f, 0.6f, 0.4f) } },
        { "tat", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Dust, Intensity = 0.5f, ForcedTime = PhxTimeOfDay.Day, ParticleTint = new Color(0.9f, 0.8f, 0.6f) } },
        { "mus", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Ash, Intensity = 1.3f, FogDensityScale = 1.5f, ForcedTime = PhxTimeOfDay.Night, ParticleTint = new Color(1f, 0.5f, 0.2f) } },
        { "cor", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Rain, Intensity = 0.6f, ForcedTime = PhxTimeOfDay.Night, ParticleTint = new Color(0.8f, 0.85f, 1f) } },
        { "fel", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Spores, Intensity = 0.9f, FogDensityScale = 1.5f, ForcedTime = PhxTimeOfDay.Dusk, ParticleTint = new Color(0.9f, 0.75f, 1f) } },
        { "myg", new PhxWeatherProfile { Precipitation = PhxPrecipitation.Snow, Intensity = 0.8f, ForcedTime = PhxTimeOfDay.Dusk, ParticleTint = new Color(0.9f, 0.95f, 1f) } },
        { "kas", new PhxWeatherProfile { Precipitation = PhxPrecipitation.None, FogDensityScale = 1.1f } },
        { "nab", new PhxWeatherProfile { Precipitation = PhxPrecipitation.None } },
        { "pol", new PhxWeatherProfile { Precipitation = PhxPrecipitation.None, ForcedTime = PhxTimeOfDay.Night } },
        { "spa", new PhxWeatherProfile { Precipitation = PhxPrecipitation.None, ForcedTime = PhxTimeOfDay.Night } },
    };

    PhxScene ActiveScene;
    GameObject WeatherRoot;
    ParticleSystem Emitter;
    Coroutine StormRoutine;


    void Update()
    {
        PhxScene scene = PhxGame.GetScene();
        if (scene == ActiveScene)
        {
            // keep the emitter riding on the camera
            if (Emitter != null && Camera.main != null)
            {
                Emitter.transform.position = Camera.main.transform.position + Vector3.up * 25f;
            }
            return;
        }

        ActiveScene = scene;
        if (StormRoutine != null) { StopCoroutine(StormRoutine); StormRoutine = null; }
        if (WeatherRoot != null) Destroy(WeatherRoot);
        Emitter = null;
        if (scene == null) return;

        string mapScript = PhxGame.Instance != null ? PhxGame.Instance.CurrentMapScript : null;
        if (string.IsNullOrEmpty(mapScript)) return;

        Apply(mapScript.ToLowerInvariant());
    }

    void Apply(string mapScript)
    {
        // A stock map is presented exactly as it was authored.
        //
        // This system invents content rather than interpreting it: the profile
        // table decided Coruscant is a rainy night city, and ApplyTimeOfDay
        // then rescales the map's own directional lights to match. On stock
        // cor1 that turned a bright daytime plaza into a dark one and put rain
        // on it - the map's .lgt said otherwise, and the .lgt is the source of
        // truth. That is the whole "stock data, modern interpretation"
        // promise: HDRP may light the authored sun better, it may not decide
        // the sun has set.
        //
        // BF3 Legacy's own maps are a different case. They ship greybox
        // levels with no finished sky, and the pack's look is what they are
        // for - so weather stays enabled there.
        if (PhxGame.Instance != null && !PhxGame.Instance.CurrentMapIsAddon)
        {
            // Cleared, not just skipped: this is static and survives the map
            // change, so a stock map loaded after a BF3 night map would
            // otherwise inherit its time of day.
            ActiveTimeOfDay = PhxTimeOfDay.Auto;

            Debug.Log($"[BF3Legacy] '{mapScript}' is a stock map; weather and time of day " +
                      "left as authored.");
            return;
        }

        PhxWeatherProfile profile = null;
        foreach (KeyValuePair<string, PhxWeatherProfile> kv in Profiles)
        {
            if (mapScript.StartsWith(kv.Key) || mapScript.Contains("_" + kv.Key))
            {
                profile = kv.Value;
                break;
            }
        }
        profile = profile ?? new PhxWeatherProfile();   // unknown maps: clear weather

        WeatherRoot = new GameObject("BF3Weather");
        WeatherRoot.transform.SetParent(transform, false);

        // ---- time of day ----
        // Auto means "leave the lighting alone", not "pick one".
        //
        // This used to derive the time of day from the hash of the map name -
        // Coruscant was night because "cor1c_con".GetHashCode() % 5 happened
        // to be 1. An arbitrary function of a string is not a lighting
        // decision, and a map with no profile entry has nothing to say about
        // its time of day, so the authored lighting stands.
        PhxTimeOfDay tod = profile.ForcedTime;
        ActiveTimeOfDay = tod;

        // ---- precipitation ----
        if (profile.Precipitation != PhxPrecipitation.None)
        {
            Emitter = BuildEmitter(profile);
        }

        // ---- storm ----
        if (profile.Storm)
        {
            StormRoutine = StartCoroutine(LightningStorm());
        }

        Debug.Log($"[BF3Legacy] Weather for '{mapScript}': {profile.Precipitation} " +
                  $"x{profile.Intensity}, {tod}{(profile.Storm ? ", storm" : "")}");
    }

    /// <summary>
    /// The time of day this map asked for. Read by BFLightingDirector, which
    /// is the only thing that writes the sun.
    /// </summary>
    /// <remarks>
    /// Published rather than applied. This used to walk every directional
    /// light and rescale its intensity and colour in place - a fifth writer of
    /// the sun, on top of the importer, the director, PhxMapAtmosphere and the
    /// profile. Direct property writes are not arbitrated by volume priority,
    /// so whichever ran last won, and that followed component construction
    /// order rather than any intent.
    ///
    /// Worse, the scaling was in place with no baseline: intensity was
    /// multiplied by 0.12 for night wherever it happened to be at that moment,
    /// so the result depended on whether the director had already applied its
    /// own scale. On the BF3 Legacy Coruscant map that produced a sun at a
    /// fraction of its authored strength, pushed 70% toward blue - a dark,
    /// cold, glossy city that neither the map nor the profile asked for.
    ///
    /// Time of day is a lighting decision, so it belongs to the component that
    /// owns lighting. This states the request; the director resolves it once,
    /// against the authored baseline it already tracks.
    /// </remarks>
    public static PhxTimeOfDay ActiveTimeOfDay { get; private set; } = PhxTimeOfDay.Auto;

    /// <summary>Sun intensity scale and colour shift for a time of day.</summary>
    public static void GetTimeOfDayGrade(out float intensityScale, out Color tint, out float tintWeight)
    {
        switch (ActiveTimeOfDay)
        {
            case PhxTimeOfDay.Dusk:
                intensityScale = 0.55f;
                tint = new Color(1f, 0.6f, 0.35f);
                tintWeight = 0.5f;
                return;
            case PhxTimeOfDay.Night:
                intensityScale = 0.12f;
                tint = new Color(0.55f, 0.65f, 1f);
                tintWeight = 0.7f;
                return;
            default:
                intensityScale = 1f;
                tint = Color.white;
                tintWeight = 0f;
                return;
        }
    }

    ParticleSystem BuildEmitter(PhxWeatherProfile profile)
    {
        GameObject go = new GameObject("Precipitation");
        go.transform.SetParent(WeatherRoot.transform, false);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = ps.main;
        ParticleSystem.EmissionModule emission = ps.emission;
        ParticleSystem.ShapeModule shape = ps.shape;
        ParticleSystem.VelocityOverLifetimeModule vel = ps.velocityOverLifetime;

        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = profile.ParticleTint;
        main.maxParticles = 6000;

        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(120f, 1f, 120f);

        vel.enabled = true;

        switch (profile.Precipitation)
        {
            case PhxPrecipitation.Rain:
                main.startSpeed = 0f;
                main.startLifetime = 1.6f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
                main.gravityModifier = 0f;
                emission.rateOverTime = 2500f * profile.Intensity;
                vel.y = new ParticleSystem.MinMaxCurve(-38f, -30f);
                vel.x = new ParticleSystem.MinMaxCurve(-2f, 2f);
                // stretched droplets
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 6f;
                break;

            case PhxPrecipitation.Snow:
                main.startSpeed = 0f;
                main.startLifetime = 8f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.1f);
                emission.rateOverTime = 900f * profile.Intensity;
                vel.y = new ParticleSystem.MinMaxCurve(-4f, -2f);
                vel.x = new ParticleSystem.MinMaxCurve(-3f, 3f);   // wind drift
                vel.z = new ParticleSystem.MinMaxCurve(-3f, 3f);
                break;

            case PhxPrecipitation.Ash:
                main.startSpeed = 0f;
                main.startLifetime = 10f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.12f);
                emission.rateOverTime = 350f * profile.Intensity;
                vel.y = new ParticleSystem.MinMaxCurve(-2f, -0.5f);
                vel.x = new ParticleSystem.MinMaxCurve(-2f, 2f);
                vel.z = new ParticleSystem.MinMaxCurve(-2f, 2f);
                break;

            case PhxPrecipitation.Dust:
                main.startSpeed = 0f;
                main.startLifetime = 6f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
                emission.rateOverTime = 120f * profile.Intensity;
                vel.x = new ParticleSystem.MinMaxCurve(4f, 10f);    // sideways haze
                vel.y = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
                shape.scale = new Vector3(160f, 30f, 160f);
                break;

            case PhxPrecipitation.Spores:
            case PhxPrecipitation.Mist:
                main.startSpeed = 0f;
                main.startLifetime = 12f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.6f);
                emission.rateOverTime = 150f * profile.Intensity;
                vel.y = new ParticleSystem.MinMaxCurve(-0.3f, 0.6f);
                vel.x = new ParticleSystem.MinMaxCurve(-1f, 1f);
                vel.z = new ParticleSystem.MinMaxCurve(-1f, 1f);
                shape.scale = new Vector3(120f, 20f, 120f);
                break;
        }

        // simple soft particle material
        ParticleSystemRenderer psr = go.GetComponent<ParticleSystemRenderer>();
        if (psr.sharedMaterial == null || psr.sharedMaterial.shader == null)
        {
            psr.material = PhxRuntimeAssets.CreateLineMaterial(profile.ParticleTint);
        }
        return ps;
    }

    IEnumerator LightningStorm()
    {
        GameObject flashGo = new GameObject("LightningFlash");
        flashGo.transform.SetParent(WeatherRoot.transform, false);
        Light flash = PhxRuntimeAssets.CreateDirectionalLight(
            flashGo, new Color(0.85f, 0.9f, 1f), 0f, castShadows: false);

        while (true)
        {
            yield return new WaitForSeconds(Random.Range(6f, 20f));

            // double-strike flicker (Lux - briefly outshines the sun)
            for (int i = 0; i < Random.Range(1, 3); ++i)
            {
                PhxRuntimeAssets.SetIntensity(flash, Random.Range(60000f, 130000f), directional: true);
                yield return new WaitForSeconds(0.08f);
                PhxRuntimeAssets.SetIntensity(flash, 12000f, directional: true);
                yield return new WaitForSeconds(0.06f);
            }
            PhxRuntimeAssets.SetIntensity(flash, 0f, directional: true);
        }
    }
}
