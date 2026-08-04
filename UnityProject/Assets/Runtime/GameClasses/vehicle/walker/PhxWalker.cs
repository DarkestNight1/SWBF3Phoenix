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
/// Movement: SWBF2 walkers are animation-driven (leg cycles with foot
/// planting). Reproducing that needs the walker animation banks driven by
/// travel speed, which is a substantial piece of work on its own. Until then
/// this drives the body over the terrain - raycast ground-following with
/// tank-style steering - so walkers spawn, carry troops, aim and fire
/// correctly. Legs will visually slide rather than step.
///
/// TODO: drive the leg animation from distance travelled and add foot IK.
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

        // NOTE: AnimationName/GeometryName etc. come from PhxVehicleProperties.
        // Do NOT redeclare base props here - PhxInstance.InitInstance walks
        // fields reflectively and duplicate names break that scan.
    }

    PhxWalker.ClassProperties W;
    PhxSeat DriverSeat;
    Rigidbody Body;

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
        float targetSpeed = drive * W.MaxSpeed;
        CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, targetSpeed, W.Acceleration * deltaTime);

        // tank steering
        if (Mathf.Abs(steer) > 0.001f)
        {
            float turn = steer * W.MaxTurnSpeed * 30f * deltaTime;
            Body.MoveRotation(Body.rotation * Quaternion.Euler(0f, turn, 0f));
        }

        Vector3 next = Body.position + transform.forward * (CurrentSpeed * deltaTime);

        // ground following: keep the body a fixed height above the surface and
        // align to the slope, so walkers climb terrain instead of clipping it
        Vector3 probeOrigin = next + Vector3.up * (W.WalkerHeight + 10f);
        if (Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit,
                            W.WalkerHeight + 40f, GroundMask, QueryTriggerInteraction.Ignore))
        {
            next.y = hit.point.y + W.WalkerHeight;

            Quaternion slope = Quaternion.FromToRotation(transform.up, hit.normal) * Body.rotation;
            Body.MoveRotation(Quaternion.Slerp(Body.rotation, slope, deltaTime * 2f));
        }

        Body.MovePosition(next);
    }
}
