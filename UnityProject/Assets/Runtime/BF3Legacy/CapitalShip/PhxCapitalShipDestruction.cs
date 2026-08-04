using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Staged capital ship breakup, recreating the Battlefront III / Elite Squadron
/// destruction spectacle:
///
///   Phase 1 - Chain reaction: explosions ripple along the hull from the reactor
///             outwards, secondary fires ignite, the ship's lights flicker out.
///   Phase 2 - Structural failure: the ship lists (pitch/roll drift), then the
///             hull fractures - child objects tagged as break sections detach
///             with physics and drift apart.
///   Phase 3 - Final detonation: reactor flash, shockwave, remaining geometry
///             despawns or plummets (in-atmosphere maps) / drifts (space).
///
/// Purely self-contained: uses the ship's existing child geometry, no assets
/// from the original games. Explosion visuals go through Unity primitives +
/// lights, and can later be swapped for loaded SWBF2 effects via PhxEffects.
/// </summary>
public class PhxCapitalShipDestruction : MonoBehaviour
{
    [Header("Timing")]
    public float ChainReactionDuration = 12f;
    public float ListingDuration = 8f;
    public float FinalFlashDuration = 2.5f;

    [Header("Breakup")]
    // Children whose name contains one of these markers detach as debris chunks.
    // Ship builders can also explicitly add PhxShipBreakSection components.
    public string[] BreakSectionNameMarkers = { "_break", "_section", "_hull" };
    public float DebrisImpulse = 60f;
    public float DebrisTorque = 15f;

    [Header("Environment")]
    // If true, wreck falls towards the planet below (in-atmosphere battles like
    // BF3's Coruscant); otherwise it drifts in space.
    public bool InAtmosphere = false;
    public float WreckFallAcceleration = 9f;

    PhxCapitalShip Ship;
    Action OnComplete;


    public void Play(PhxCapitalShip ship, Action onComplete)
    {
        Ship = ship;
        OnComplete = onComplete;
        StartCoroutine(RunSequence());
    }

    IEnumerator RunSequence()
    {
        Bounds hullBounds = ComputeHullBounds();

        // ---- Phase 1: chain reaction ----
        float t = 0f;
        while (t < ChainReactionDuration)
        {
            // Explosions travel from the core outwards over time
            float spread = Mathf.Lerp(0.1f, 1f, t / ChainReactionDuration);
            Vector3 pos = hullBounds.center + Vector3.Scale(
                UnityEngine.Random.insideUnitSphere * spread, hullBounds.extents);

            SpawnExplosion(pos, UnityEngine.Random.Range(6f, 18f) * spread + 4f);

            t += UnityEngine.Random.Range(0.25f, 0.7f);
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.25f, 0.7f));
        }

        // ---- Phase 2: listing + hull fracture ----
        List<Rigidbody> debris = DetachBreakSections();

        Vector3 listAxis = UnityEngine.Random.onUnitSphere;
        float t2 = 0f;
        while (t2 < ListingDuration)
        {
            float dt = Time.deltaTime;
            t2 += dt;

            // slow, accelerating list
            transform.Rotate(listAxis, dt * Mathf.Lerp(0.5f, 4f, t2 / ListingDuration), Space.World);
            if (InAtmosphere)
            {
                transform.position += Vector3.down * (0.5f * WreckFallAcceleration * t2 * dt);
            }

            if (UnityEngine.Random.value < dt * 2f)
            {
                Vector3 pos = hullBounds.center + Vector3.Scale(
                    UnityEngine.Random.insideUnitSphere, hullBounds.extents);
                SpawnExplosion(pos, UnityEngine.Random.Range(8f, 20f));
            }
            yield return null;
        }

        // ---- Phase 3: final detonation ----
        SpawnExplosion(transform.TransformPoint(hullBounds.center - transform.position), hullBounds.extents.magnitude * 1.5f);
        PhxShipShockwave.Spawn(transform.position, hullBounds.extents.magnitude * 3f, FinalFlashDuration);

        yield return new WaitForSeconds(FinalFlashDuration);

        // Hide remaining hull, leave debris drifting for a while, then clean up
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            r.enabled = false;
        }
        foreach (Collider c in GetComponentsInChildren<Collider>())
        {
            c.enabled = false;
        }
        foreach (Rigidbody rb in debris)
        {
            if (rb != null) GameObject.Destroy(rb.gameObject, 20f);
        }

        OnComplete?.Invoke();
    }

    Bounds ComputeHullBounds()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return new Bounds(transform.position, Vector3.one * 100f);
        }
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; ++i)
        {
            b.Encapsulate(renderers[i].bounds);
        }
        return b;
    }

    List<Rigidbody> DetachBreakSections()
    {
        List<Rigidbody> result = new List<Rigidbody>();

        List<Transform> sections = new List<Transform>();
        foreach (Transform child in GetComponentsInChildren<Transform>())
        {
            if (child == transform) continue;
            if (child.GetComponent<PhxShipBreakSection>() != null)
            {
                sections.Add(child);
                continue;
            }
            string lower = child.name.ToLowerInvariant();
            foreach (string marker in BreakSectionNameMarkers)
            {
                if (lower.Contains(marker))
                {
                    sections.Add(child);
                    break;
                }
            }
        }

        foreach (Transform section in sections)
        {
            section.SetParent(null, true);
            Rigidbody rb = section.gameObject.GetComponent<Rigidbody>();
            if (rb == null) rb = section.gameObject.AddComponent<Rigidbody>();
            rb.mass = 1000f;
            rb.useGravity = InAtmosphere;
            rb.AddForce(UnityEngine.Random.onUnitSphere * DebrisImpulse, ForceMode.VelocityChange);
            rb.AddTorque(UnityEngine.Random.onUnitSphere * DebrisTorque, ForceMode.VelocityChange);
            result.Add(rb);
        }
        return result;
    }

    void SpawnExplosion(Vector3 worldPos, float radius)
    {
        PhxShipExplosionFlash.Spawn(worldPos, radius);
    }
}

/// <summary>Marks a child of a capital ship as a detachable debris chunk.</summary>
public class PhxShipBreakSection : MonoBehaviour
{
}

/// <summary>
/// Minimal self-contained explosion visual: expanding emissive sphere + point
/// light, fading out. Placeholder until wired to loaded SWBF2 explosion classes.
/// </summary>
public class PhxShipExplosionFlash : MonoBehaviour
{
    float Radius;
    float Life;
    float MaxLife = 1.2f;
    Material Mat;
    Light Glow;

    public static void Spawn(Vector3 pos, float radius)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        GameObject.Destroy(go.GetComponent<Collider>());
        go.name = "ShipExplosion";
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * 0.1f;

        PhxShipExplosionFlash fx = go.AddComponent<PhxShipExplosionFlash>();
        fx.Radius = radius;

        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(1f, 0.6f, 0.2f);
        l.range = radius * 4f;
        l.intensity = 8000f;
        fx.Glow = l;
    }

    void Awake()
    {
        Renderer r = GetComponent<Renderer>();
        Mat = r.material;
        Mat.color = new Color(1f, 0.55f, 0.15f, 1f);
    }

    void Update()
    {
        Life += Time.deltaTime;
        float f = Mathf.Clamp01(Life / MaxLife);

        transform.localScale = Vector3.one * Mathf.Lerp(0.1f, Radius, Mathf.Sqrt(f));
        if (Mat != null)
        {
            Color c = Color.Lerp(new Color(1f, 0.55f, 0.15f), new Color(0.2f, 0.05f, 0.02f), f);
            c.a = 1f - f;
            Mat.color = c;
        }
        if (Glow != null)
        {
            Glow.intensity = Mathf.Lerp(8000f, 0f, f);
        }
        if (Life >= MaxLife)
        {
            GameObject.Destroy(gameObject);
        }
    }
}

/// <summary>Expanding shockwave ring used for the final reactor detonation.</summary>
public class PhxShipShockwave : MonoBehaviour
{
    float MaxRadius;
    float Duration;
    float Life;

    public static void Spawn(Vector3 pos, float maxRadius, float duration)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        GameObject.Destroy(go.GetComponent<Collider>());
        go.name = "ShipShockwave";
        go.transform.position = pos;

        PhxShipShockwave wave = go.AddComponent<PhxShipShockwave>();
        wave.MaxRadius = maxRadius;
        wave.Duration = Mathf.Max(duration, 0.1f);

        Renderer r = go.GetComponent<Renderer>();
        r.material.color = new Color(0.6f, 0.8f, 1f, 0.6f);
    }

    void Update()
    {
        Life += Time.deltaTime;
        float f = Mathf.Clamp01(Life / Duration);
        transform.localScale = Vector3.one * Mathf.Lerp(1f, MaxRadius, f);

        Renderer r = GetComponent<Renderer>();
        Color c = r.material.color;
        c.a = 0.6f * (1f - f);
        r.material.color = c;

        if (f >= 1f)
        {
            GameObject.Destroy(gameObject);
        }
    }
}
