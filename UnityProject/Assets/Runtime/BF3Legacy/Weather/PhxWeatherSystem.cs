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
/// Time of day is deterministic per map name (so Coruscant is reliably a
/// night city) unless the config forces one.
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
        PhxTimeOfDay tod = profile.ForcedTime;
        if (tod == PhxTimeOfDay.Auto)
        {
            // deterministic per map name so revisits look the same
            int hash = Mathf.Abs(mapScript.GetHashCode());
            tod = (hash % 5) switch
            {
                0 => PhxTimeOfDay.Dusk,
                1 => PhxTimeOfDay.Night,
                _ => PhxTimeOfDay.Day,
            };
        }
        ApplyTimeOfDay(tod);

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

    void ApplyTimeOfDay(PhxTimeOfDay tod)
    {
        // scale the map's directional lights; night gets a cool moonlight tint
        foreach (Light light in FindObjectsOfType<Light>())
        {
            if (light.type != LightType.Directional) continue;

            // NOTE: must go through the helper - under HDRP the effective
            // intensity lives on HDAdditionalLightData, not Light.intensity.
            switch (tod)
            {
                case PhxTimeOfDay.Dusk:
                    PhxRuntimeAssets.ScaleIntensity(light, 0.55f);
                    light.color = Color.Lerp(light.color, new Color(1f, 0.6f, 0.35f), 0.5f);
                    Vector3 e = light.transform.eulerAngles;
                    light.transform.rotation = Quaternion.Euler(Mathf.Min(e.x, 18f), e.y, e.z);
                    break;
                case PhxTimeOfDay.Night:
                    PhxRuntimeAssets.ScaleIntensity(light, 0.12f);
                    light.color = Color.Lerp(light.color, new Color(0.55f, 0.65f, 1f), 0.7f);
                    break;
            }
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
