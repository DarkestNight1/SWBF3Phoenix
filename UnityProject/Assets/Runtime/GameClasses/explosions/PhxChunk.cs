using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One piece of authored wreckage thrown off a destroyed object.
/// </summary>
/// <remarks>
/// SWBF2 vehicles ship their own break-up geometry: a CHUNKSECTION per piece
/// naming a model (<c>ChunkGeometryName</c>) or a node of the vehicle's own
/// model (<c>ChunkNodeName</c>), how fast to throw it, how much lift to give
/// it, what trail and smoke to drag behind it, what to play where it hits the
/// ground, and how many ground hits it survives before it settles.
///
/// All of that was already parsed into <c>PhxVehicleProperties.ChunkSection</c>
/// and then discarded - this file existed but was commented out in its
/// entirety. A destroyed tank produced a generic puff instead of the authored
/// hull sections the artists built, which is a large part of why vehicle
/// deaths read as placeholder.
/// </remarks>
[RequireComponent(typeof(Rigidbody))]
public class PhxChunk : MonoBehaviour
{
    static PhxScene SCENE => PhxGame.GetScene();

    /// <summary>
    /// How long a settled chunk stays before it is removed. Wreckage that
    /// never disappears turns a long match into a physics scene, and BF2
    /// itself fades chunks out.
    /// </summary>
    const float SettledLifetime = 20f;

    Rigidbody Body;

    int TerrainCollisionsAllowed = 1;
    int TerrainCollisionsSeen;

    string TerrainEffect;
    PhxEffect TrailEffect;
    PhxEffect SmokeEffect;

    float Bounciness;
    float Stickiness;
    float SettleTimer;
    bool Settled;

    void Awake()
    {
        Body = GetComponent<Rigidbody>();
    }

    /// <summary>
    /// Configure from one parsed CHUNKSECTION and launch.
    /// </summary>
    /// <param name="section">The section's properties, by odf name.</param>
    /// <param name="wreckCentre">
    /// World position of the object that died, which is what the chunk is
    /// thrown away from.
    /// </param>
    /// <param name="inheritedVelocity">
    /// Velocity of the object that died, so a chunk off a moving vehicle keeps
    /// travelling instead of dropping straight down.
    /// </param>
    public void Init(Dictionary<string, IPhxPropRef> section, Vector3 wreckCentre, Vector3 inheritedVelocity)
    {
        TerrainCollisionsAllowed = Mathf.Max(1, ReadInt(section, "ChunkTerrainCollisions", 1));
        TerrainEffect = ReadString(section, "ChunkTerrainEffect", null);
        Bounciness = ReadFloat(section, "ChunkBounciness", 0f);
        Stickiness = ReadFloat(section, "ChunkStickiness", 0f);

        float speed = ReadFloat(section, "ChunkSpeed", 1f);
        float upFactor = ReadFloat(section, "ChunkUpFactor", 0.5f);

        Body.isKinematic = false;
        Body.useGravity = true;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Thrown outward from the wreck with the authored share of lift.
        // Direction is the chunk's own offset from the object's origin, which
        // is what makes a break-up spread rather than fountain: pieces on the
        // left go left. A piece exactly on the origin gets a random lateral
        // direction so it does not go straight up alone.
        Vector3 outward = transform.position - wreckCentre;
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.01f)
        {
            outward = new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
        }
        Vector3 launch = (outward.normalized + Vector3.up * upFactor).normalized * speed;
        Body.velocity = inheritedVelocity + launch;

        // ChunkOmega is authored as a per-axis spin rate. Its parsed form is a
        // multi-value property whose exact units are not documented, so it is
        // applied as a direct angular velocity and a random tumble is used
        // when it is absent - a chunk with no spin at all reads as a dropped
        // box rather than debris.
        Vector3 omega = ReadVector(section, "ChunkOmega");
        Body.angularVelocity = omega == Vector3.zero
            ? Random.insideUnitSphere * 6f
            : omega;

        AttachEffect(ReadString(section, "ChunkTrailEffect", null), transform, ref TrailEffect);

        string smokeNodeName = ReadString(section, "ChunkSmokeNodeName", null);
        Transform smokeNode = string.IsNullOrEmpty(smokeNodeName)
            ? transform
            : (UnityUtils.FindChildTransform(transform, smokeNodeName) ?? transform);
        AttachEffect(ReadString(section, "ChunkSmokeEffect", null), smokeNode, ref SmokeEffect);
    }

    void AttachEffect(string effectName, Transform parent, ref PhxEffect slot)
    {
        if (string.IsNullOrEmpty(effectName) || SCENE == null) return;

        PhxEffect effect = SCENE.EffectsManager.LendEffect(effectName);
        if (effect == null) return;

        effect.SetParent(parent);
        effect.SetLooping(true);
        effect.Play();
        slot = effect;
    }

    void Update()
    {
        if (!Settled) return;

        SettleTimer -= Time.deltaTime;
        if (SettleTimer <= 0f)
        {
            Release();
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        if (Settled) return;

        bool hitTerrain = collision.gameObject.layer == LayerMask.NameToLayer("TerrainAll");
        if (!hitTerrain) return;

        ++TerrainCollisionsSeen;

        if (!string.IsNullOrEmpty(TerrainEffect) && collision.contactCount > 0 && SCENE != null)
        {
            ContactPoint point = collision.GetContact(0);
            SCENE.EffectsManager.PlayEffectOnce(TerrainEffect, point.point,
                                                Quaternion.LookRotation(point.normal, Vector3.up));
        }

        if (TerrainCollisionsSeen < TerrainCollisionsAllowed)
        {
            // Authored bounce, applied along the contact normal. Stickiness is
            // the opposite control and damps what is left.
            if (collision.contactCount > 0 && Bounciness > 0f)
            {
                Body.velocity += collision.GetContact(0).normal * Bounciness;
            }
            if (Stickiness > 0f)
            {
                Body.velocity *= Mathf.Clamp01(1f - Stickiness);
            }
            return;
        }

        Settle();
    }

    /// <summary>
    /// Stop simulating: the chunk has taken all the ground hits it was
    /// authored to take and now lies where it landed.
    /// </summary>
    void Settle()
    {
        Settled = true;
        SettleTimer = SettledLifetime;

        // Kinematic rather than destroying the Rigidbody: a wreck field of
        // dozens of chunks per vehicle is a real solver cost, and none of them
        // need to keep moving once they have come to rest.
        Body.velocity = Vector3.zero;
        Body.angularVelocity = Vector3.zero;
        Body.isKinematic = true;

        StopEffect(ref TrailEffect);
    }

    void Release()
    {
        StopEffect(ref TrailEffect);
        StopEffect(ref SmokeEffect);
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        StopEffect(ref TrailEffect);
        StopEffect(ref SmokeEffect);
    }

    static void StopEffect(ref PhxEffect effect)
    {
        if (effect == null) return;

        effect.SetLooping(false);
        effect.Stop();
        effect = null;
    }

    // ------------------------------------------------------- property access

    static string ReadString(Dictionary<string, IPhxPropRef> section, string name, string fallback)
    {
        if (section != null && section.TryGetValue(name, out IPhxPropRef prop) && prop is PhxProp<string> s)
        {
            string value = s.Get();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        return fallback;
    }

    static float ReadFloat(Dictionary<string, IPhxPropRef> section, string name, float fallback)
    {
        if (section != null && section.TryGetValue(name, out IPhxPropRef prop) && prop is PhxProp<float> f)
        {
            return f.Get();
        }
        return fallback;
    }

    static int ReadInt(Dictionary<string, IPhxPropRef> section, string name, int fallback)
    {
        if (section != null && section.TryGetValue(name, out IPhxPropRef prop))
        {
            if (prop is PhxProp<int> i) return i.Get();
            if (prop is PhxProp<float> f) return Mathf.RoundToInt(f.Get());
        }
        return fallback;
    }

    static Vector3 ReadVector(Dictionary<string, IPhxPropRef> section, string name)
    {
        if (section == null || !section.TryGetValue(name, out IPhxPropRef prop)) return Vector3.zero;
        if (!(prop is PhxMultiProp multi) || multi.Values.Count == 0) return Vector3.zero;

        object[] values = multi.Values[0];
        if (values.Length < 3) return Vector3.zero;

        return new Vector3(AsFloat(values[0]), AsFloat(values[1]), AsFloat(values[2]));
    }

    static float AsFloat(object value)
    {
        if (value is float f) return f;
        if (value is int i) return i;
        if (value is string s && float.TryParse(s, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture,
                                                out float parsed))
        {
            return parsed;
        }
        return 0f;
    }
}
