using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Utils;

/// <summary>
/// The stock 'walker' / 'commandwalker' ODF class - AT-ST, AT-AT, AT-TE,
/// spider droid, hailfire. Previously unregistered, so none of these spawned
/// on any map at all.
///
/// Walker odfs are structured like other vehicles: a WALKERSECTION per seat
/// (body + turrets), each with its own weapon systems, which PhxSeat and
/// PhxVehicleTurret already handle - PhxSeat's section scan was already
/// aware of WALKERSECTION.
///
/// Movement is animation-driven, as the original is: the leg cycle plays at
/// the rate the machine is covering ground, the feet are placed on what is
/// actually under them, and the hull rides on the feet rather than on a single
/// probe under its centre. That behaviour lives in
/// <see cref="PhxWalkerLocomotion"/>; this class owns driving and steering and
/// hands it the resulting ground speed.
///
/// A walker whose bank has no recognisable leg-cycle clip still drives - it
/// falls back to a centre probe, which is what every walker did before.
/// </summary>
public class PhxWalker : PhxVehicle
{
    public class ClassProperties : PhxVehicleProperties
    {
        public PhxProp<float> MaxSpeed = new PhxProp<float>(4f);
        public PhxProp<float> MaxTurnSpeed = new PhxProp<float>(0.8f);
        public PhxProp<float> Acceleration = new PhxProp<float>(2f);

        // Height the body rides above the ground (legs' length)
        public PhxProp<float> WalkerHeight = new PhxProp<float>(4f);

        // Kicked up where a foot lands. Absent from most walker odfs, in which
        // case footfalls are silent-and-clean rather than wrong.
        public PhxProp<string> FootstepEffect = new PhxProp<string>("");

        // NOTE: AnimationName/GeometryName etc. come from PhxVehicleProperties.
        // Do NOT redeclare base props here - PhxInstance.InitInstance walks
        // fields reflectively and duplicate names break that scan.
    }

    PhxWalker.ClassProperties W;
    PhxSeat DriverSeat;
    Rigidbody Body;
    PhxWalkerLocomotion Legs;

    float CurrentSpeed;

    // ground probe mask: terrain + buildings, matching PhxFlyer's usage
    LayerMask GroundMask = (1 << 11) | (1 << 12) | (1 << 13) | (1 << 14) | (1 << 15);


    public override void Init()
    {
        base.Init();
        SetupEnterTrigger();

        W = C as PhxWalker.ClassProperties;
        CurHealth.Set(C.MaxHealth.Get());

        ModelMapping.ConvexifyMeshColliders(false);

        Body = gameObject.AddComponent<Rigidbody>();
        Body.mass = 4000f;
        Body.useGravity = false;      // ground-following handles altitude
        Body.isKinematic = true;
        Body.interpolation = RigidbodyInterpolation.Interpolate;

        var EC = C.EntityClass;
        EC.GetAllProperties(out uint[] properties, out string[] values);

        Seats = new List<PhxSeat>();

        int i = 0;
        int turretIndex = 1;
        while (i < properties.Length)
        {
            if (properties[i] == HashUtils.GetFNV("WALKERSECTION"))
            {
                // "BODY" is the driver; every other section is a turret seat
                if (values[i].Equals("BODY", StringComparison.OrdinalIgnoreCase))
                {
                    if (DriverSeat == null)
                    {
                        PhxVehicleTurret bodySeat = new PhxVehicleTurret(this, 0);
                        bodySeat.InitManual(EC, i, "WALKERSECTION", values[i]);
                        DriverSeat = bodySeat;
                        Seats.Add(bodySeat);
                    }
                }
                else
                {
                    PhxVehicleTurret turret = new PhxVehicleTurret(this, turretIndex++);
                    turret.InitManual(EC, i, "WALKERSECTION", values[i]);
                    Seats.Add(turret);
                }
            }
            i++;
        }

        if (Seats.Count == 0)
        {
            Debug.LogWarning($"Walker class '{C.Name}' has no WALKERSECTION!");
        }

        SetIgnoredCollidersOnAllWeapons();

        Legs = new PhxWalkerLocomotion(transform, C.AnimationName.Get(), W.WalkerHeight.Get(), GroundMask);
        Legs.OnFootPlanted += OnFootPlanted;

        Debug.Log($"[Phoenix] Walker '{C.Name}': {Legs.FootCount} foot node(s), " +
                  $"leg animation {(Legs.IsAnimated ? "active" : "unavailable")}.");
    }

    void OnFootPlanted(Vector3 position)
    {
        string effect = W?.FootstepEffect.Get();
        if (string.IsNullOrEmpty(effect) || SCENE == null) return;

        SCENE.EffectsManager.PlayEffectOnce(effect, position, Quaternion.identity);
    }

    public override Vector3 GetCameraPosition()
    {
        return Seats.Count > 0 ? Seats[0].GetCameraPosition() : transform.position;
    }

    public override Quaternion GetCameraRotation()
    {
        return Seats.Count > 0 ? Seats[0].GetCameraRotation() : transform.rotation;
    }

    public override void Tick(float deltaTime)
    {
        UnityEngine.Profiling.Profiler.BeginSample("Tick Walker");
        base.Tick(deltaTime);

        foreach (PhxSeat seat in Seats)
        {
            seat.Tick(deltaTime);
        }

        // Foot placement has to run here, not in TickPhysics: PhxScene ticks
        // the animation system first and instances afterwards precisely so
        // that instances can adjust the pose it produced. Correcting the feet
        // in the physics tick would write into a skeleton the next animation
        // update overwrites, and nothing would ever be visible.
        Legs?.TickFeet(deltaTime);

        UnityEngine.Profiling.Profiler.EndSample();
    }

    public override void TickPhysics(float deltaTime)
    {
        UnityEngine.Profiling.Profiler.BeginSample("Tick Walker Physics");
        UpdateMovement(deltaTime);
        UnityEngine.Profiling.Profiler.EndSample();
    }

    void UpdateMovement(float deltaTime)
    {
        if (W == null || DriverSeat == null || Body == null) return;

        PhxPawnController controller = DriverSeat.GetController();
        float drive = 0f;
        float steer = 0f;

        if (controller != null)
        {
            drive = controller.MoveDirection.y;
            steer = controller.mouseX;
        }

        // accelerate toward the commanded speed
        // Damage to the legs slows the machine. This is the whole point of
        // zoned damage on a walker: an AT-ST with a shattered leg should
        // become a slow target rather than dying at the same rate everywhere.
        float mobility = MobilityFactor;

        float targetSpeed = drive * W.MaxSpeed * mobility;
        CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, targetSpeed, W.Acceleration * mobility * deltaTime);

        // tank steering
        if (Mathf.Abs(steer) > 0.001f)
        {
            float turn = steer * W.MaxTurnSpeed * 30f * deltaTime;
            Body.MoveRotation(Body.rotation * Quaternion.Euler(0f, turn, 0f));
        }

        // The legs cycle at whatever rate covers the ground actually passing
        // beneath them, so speeding up or reversing changes the gait rather
        // than the slide rate.
        Legs?.SetGroundSpeed(CurrentSpeed);

        Vector3 next = Body.position + transform.forward * (CurrentSpeed * deltaTime);

        // Where the hull sits: on the feet when the legs found ground, and on
        // a single centre probe otherwise (a walker with no recognisable leg
        // animation, or one striding over a gap).
        float supportHeight = next.y;
        Vector3 supportNormal = Vector3.up;
        bool supported = Legs != null && Legs.GetBodySupport(out supportHeight, out supportNormal);

        if (!supported)
        {
            Vector3 probeOrigin = next + Vector3.up * (W.WalkerHeight + 10f);
            if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit,
                                W.WalkerHeight + 40f, GroundMask, QueryTriggerInteraction.Ignore))
            {
                supportHeight = hit.point.y;
                supportNormal = hit.normal;
                supported = true;
            }
            else
            {
                supportHeight = next.y;
                supportNormal = Vector3.up;
            }
        }

        if (supported)
        {
            // Lerped rather than snapped: the support height moves with every
            // footfall, and following it exactly makes the hull bob one step
            // per step. A walker's mass is the point - it should lag its legs.
            next.y = Mathf.Lerp(Body.position.y, supportHeight + W.WalkerHeight, deltaTime * 3f);

            Quaternion slope = Quaternion.FromToRotation(transform.up, supportNormal) * Body.rotation;
            Body.MoveRotation(Quaternion.Slerp(Body.rotation, slope, deltaTime * 2f));
        }

        Body.MovePosition(next);
    }
}
