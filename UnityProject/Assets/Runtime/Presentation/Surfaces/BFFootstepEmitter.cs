using UnityEngine;

/// <summary>
/// Turns a walking soldier into footstep interactions.
/// </summary>
/// <remarks>
/// Attached automatically to every soldier. Footsteps are emitted from
/// distance travelled rather than from an animation event, because the imported
/// animation clips carry no event track - there is nowhere in the stock data
/// for "this frame is a footfall" to live. Stride distance is a far better
/// approximation than a timer: it stays correct when the soldier speeds up,
/// slows down, or is pushed.
///
/// This is the highest-frequency interaction in the game by a wide margin -
/// sixty-four soldiers walking is several hundred footfalls a second - so it
/// reports through <see cref="BFSurfaceInteractionSystem"/> and lets the
/// distance and cooldown budget there decide what survives, rather than each
/// soldier deciding for itself.
/// </remarks>
[RequireComponent(typeof(PhxSoldier))]
public sealed class BFFootstepEmitter : MonoBehaviour
{
    /// <summary>Metres between footfalls at a walk.</summary>
    const float StrideLength = 1.5f;

    /// <summary>Fall speed above which a landing counts as an impact.</summary>
    const float HardLandingSpeed = 6f;

    PhxSoldier Soldier;
    Vector3 LastPosition;
    float DistanceSinceStep;
    bool WasGrounded = true;
    float PreviousFallSpeed;

    void Awake()
    {
        Soldier = GetComponent<PhxSoldier>();
        LastPosition = transform.position;
    }

    /// <summary>
    /// Metres of travel that must accumulate before the ground is probed
    /// again.
    /// </summary>
    /// <remarks>
    /// The first version raycast every soldier every frame, which is sixty-
    /// four casts a frame before a shot is fired - for an effect that can only
    /// produce something once per stride. The probe now happens when the
    /// soldier has actually covered ground, plus whenever it is airborne
    /// (where the landing has to be caught the frame it happens).
    /// </remarks>
    const float ProbeInterval = 0.4f;

    float DistanceSinceProbe;
    RaycastHit LastGroundHit;
    bool LastGrounded = true;

    void Update()
    {
        if (Soldier == null || Soldier.IsDead) return;

        Vector3 position = transform.position;
        Vector3 delta = position - LastPosition;
        LastPosition = position;

        float fallSpeed = -delta.y / Mathf.Max(Time.deltaTime, 0.0001f);

        DistanceSinceProbe += delta.magnitude;

        // Airborne is probed every frame: the landing is a single-frame event
        // and missing it loses the impression entirely. On the ground, the
        // last result stands until the soldier has moved far enough for it to
        // possibly have changed.
        bool grounded;
        RaycastHit hit;
        if (!LastGrounded || DistanceSinceProbe >= ProbeInterval)
        {
            DistanceSinceProbe = 0f;
            grounded = Physics.Raycast(position + Vector3.up * 0.4f, Vector3.down,
                                       out hit, 1.2f,
                                       PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore);
            LastGroundHit = hit;
            LastGrounded = grounded;
        }
        else
        {
            grounded = LastGrounded;
            hit = LastGroundHit;
        }

        if (!grounded)
        {
            WasGrounded = false;
            PreviousFallSpeed = fallSpeed;
            DistanceSinceStep = 0f;
            return;
        }

        BFSurfaceType surface = BFSurfaceQuery.Resolve(hit.collider, hit.point);

        if (!WasGrounded)
        {
            WasGrounded = true;
            DistanceSinceStep = 0f;

            // A hard landing is a different event from a step: deeper, wider,
            // and worth a mark where a footprint would not be.
            if (PreviousFallSpeed > HardLandingSpeed)
            {
                float force = Mathf.Clamp(PreviousFallSpeed / HardLandingSpeed, 1f, 3f);
                BFSurfaceInteractionSystem.Report(BFInteractionSource.Landing,
                                                  hit.point, hit.normal, Vector3.down,
                                                  surface, force, gameObject);
            }
            return;
        }

        // Horizontal travel only - a soldier riding a lift is not walking.
        delta.y = 0f;
        DistanceSinceStep += delta.magnitude;
        if (DistanceSinceStep < StrideLength) return;

        DistanceSinceStep = 0f;

        // Sprinting lands harder and leaves more behind.
        float scale = Soldier.IsSprinting ? 1.5f : 1f;
        BFImpactResponse.PlayFootstep(hit.point, hit.normal, surface, scale, gameObject);
    }
}
