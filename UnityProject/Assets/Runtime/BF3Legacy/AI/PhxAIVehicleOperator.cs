using UnityEngine;

/// <summary>
/// Drives a vehicle on behalf of an AI soldier occupying one of its seats.
///
/// The engine already routes all vehicle control through the occupant's
/// PhxPawnController (PhxSeat.Tick reads MoveDirection / mouseX / mouseY /
/// Jump / ShootPrimary from it), so this operator does not touch vehicle
/// internals - it writes the same controller fields a human would produce:
///
///   Land (hover):  MoveDirection.y = drive, .x = strafe, mouseX = steer
///   Flyer:         Jump toggles takeoff/landing, MoveDirection.y = throttle,
///                  .x = roll, mouseX = yaw, mouseY = pitch
///
/// Behaviors:
///   - Land: drive to the objective, obstacle-deflect, reverse out of jams.
///   - Air:  take off, hold combat altitude, dogfight enemy flyers, and fly
///           strafing runs against enemy capital ship subsystems (engines
///           are the externally-attackable critical, so that's the priority
///           target until shields drop).
/// </summary>
public class PhxAIVehicleOperator
{
    public enum PhxVehicleRole { Ground, Air }

    readonly PhxPawnController Controller;
    readonly PhxVehicle Vehicle;
    public PhxSeat Seat { get; private set; }
    public PhxVehicleRole Role { get; private set; }

    // air state
    bool TakeoffRequested;
    float TakeoffCooldown;
    float AttackRunTimer;
    Vector3 AttackRunExit;
    bool InAttackRun;

    // ground state
    float StuckTimer;
    float ReverseTimer;
    Vector3 LastPosition;

    // shared
    float TargetRefreshTimer;
    Component CurrentTarget;

    public const float CombatAltitude = 60f;
    const float StrafeBreakDistance = 90f;

    static readonly Collider[] OverlapCache = new Collider[64];


    public PhxAIVehicleOperator(PhxPawnController controller, PhxVehicle vehicle, PhxSeat seat)
    {
        Controller = controller;
        Vehicle = vehicle;
        Seat = seat;
        Role = vehicle is PhxFlyer ? PhxVehicleRole.Air : PhxVehicleRole.Ground;
        LastPosition = vehicle.transform.position;
    }

    public bool IsAir => Role == PhxVehicleRole.Air;

    // Gunners only aim and shoot - the vehicle reads movement from the
    // driver seat's controller, so passengers must not fight it.
    public bool IsGunnerOnly => Seat != null && !Seat.IsDriverSeat;

    /// <summary>
    /// Drive toward a goal, engaging targets of opportunity.
    /// Returns true while the operator wants to stay in the vehicle.
    /// </summary>
    public bool Tick(float deltaTime, Vector3 goal, int team, PhxAISkillProfile skill)
    {
        if (Vehicle == null || Vehicle.IsDestroyed)
        {
            return false;
        }

        TargetRefreshTimer -= deltaTime;
        if (TargetRefreshTimer <= 0f)
        {
            TargetRefreshTimer = 0.6f;
            CurrentTarget = AcquireTarget(team);
        }

        if (IsGunnerOnly)
        {
            TickGunner(deltaTime, skill);
        }
        else if (IsAir)
        {
            TickAir(deltaTime, goal, team, skill);
        }
        else
        {
            TickGround(deltaTime, goal, skill);
        }
        return true;
    }

    // --------------------------------------------------------------- gunner

    /// <summary>
    /// Turret gunner in a passenger seat: no driving input, just traverse
    /// onto the best target and fire within the seat's arc limits.
    /// </summary>
    void TickGunner(float deltaTime, PhxAISkillProfile skill)
    {
        Controller.MoveDirection = Vector2.zero;

        if (CurrentTarget == null)
        {
            Seat.AimOverride = null;
            Controller.ShootPrimary = false;
            Controller.mouseX = 0f;
            Controller.mouseY = 0f;
            return;
        }

        Vector3 targetPos = CurrentTarget.transform.position;

        // aim error scaled by skill so low tiers miss more
        if (skill.AimErrorDegrees > 0f)
        {
            float spread = skill.AimErrorDegrees * 0.3f;
            targetPos += Random.insideUnitSphere * spread;
        }
        Seat.AimOverride = targetPos;

        // drive the seat's own pitch/yaw accumulators toward the target so
        // the turret model actually traverses (aimers follow these)
        Transform root = Vehicle.transform;
        Vector3 toTarget = targetPos - root.position;

        Vector3 flatFwd = root.forward; flatFwd.y = 0f;
        Vector3 flatTo = toTarget; flatTo.y = 0f;
        float yawError = (flatFwd.sqrMagnitude > 0.001f && flatTo.sqrMagnitude > 0.001f)
            ? Vector3.SignedAngle(flatFwd.normalized, flatTo.normalized, Vector3.up)
            : 0f;
        float pitchError = Vector3.SignedAngle(root.forward, toTarget.normalized, root.right);

        Controller.mouseX = Mathf.Clamp(yawError / 30f, -1f, 1f);
        Controller.mouseY = Mathf.Clamp(-pitchError / 30f, -1f, 1f);

        // fire once roughly on target and in range
        float range = toTarget.magnitude;
        Controller.ShootPrimary = range < 400f && Mathf.Abs(yawError) < 20f;
    }

    // ------------------------------------------------------------------ air

    void TickAir(float deltaTime, Vector3 goal, int team, PhxAISkillProfile skill)
    {
        Transform t = Vehicle.transform;

        // 1. get airborne (Jump toggles Grounded -> TakingOff on PhxFlyer)
        TakeoffCooldown -= deltaTime;
        if (!TakeoffRequested && TakeoffCooldown <= 0f)
        {
            Controller.Jump = true;
            TakeoffRequested = true;
            TakeoffCooldown = 3f;
            return;
        }

        // 2. pick where we're flying
        Vector3 flyTo;
        PhxCapitalShipSubsystem shipTarget = CurrentTarget as PhxCapitalShipSubsystem;

        if (shipTarget != null)
        {
            flyTo = TickCapitalShipAttackRun(deltaTime, shipTarget, skill);
        }
        else if (CurrentTarget != null)
        {
            // dogfight / ground attack: lead slightly and close in
            flyTo = CurrentTarget.transform.position;
            float d = Vector3.Distance(t.position, flyTo);
            if (d < 40f)
            {
                // overshoot to avoid ramming, then come around
                flyTo = t.position + t.forward * 120f + Vector3.up * 20f;
            }
        }
        else
        {
            // no target: cruise to the objective at combat altitude
            flyTo = goal + Vector3.up * CombatAltitude;
        }

        SteerFlyer(flyTo, deltaTime);

        // 3. shoot when the nose is on target
        bool onTarget = false;
        if (CurrentTarget != null)
        {
            Vector3 toTarget = (CurrentTarget.transform.position - t.position);
            float range = toTarget.magnitude;
            onTarget = range < 400f && Vector3.Angle(t.forward, toTarget.normalized) < 8f;

            // AI gunners in this seat aim directly (bypasses camera-based aim)
            Seat.AimOverride = CurrentTarget.transform.position;
        }
        else
        {
            Seat.AimOverride = null;
        }
        Controller.ShootPrimary = onTarget;
    }

    /// <summary>
    /// Classic strafing run: line up on the subsystem, dive in firing, break
    /// off at close range, climb out, come around for another pass.
    /// </summary>
    Vector3 TickCapitalShipAttackRun(float deltaTime, PhxCapitalShipSubsystem target, PhxAISkillProfile skill)
    {
        Transform t = Vehicle.transform;
        Vector3 targetPos = target.transform.position;
        float dist = Vector3.Distance(t.position, targetPos);

        if (InAttackRun)
        {
            AttackRunTimer -= deltaTime;
            if (AttackRunTimer <= 0f)
            {
                InAttackRun = false;
            }
            return AttackRunExit;
        }

        if (dist < StrafeBreakDistance)
        {
            // break off: climb away past the hull, then re-attack
            InAttackRun = true;
            AttackRunTimer = 3.5f;
            Vector3 away = (t.position - targetPos).normalized;
            AttackRunExit = t.position + t.forward * 150f + (away + Vector3.up) * 120f;
            return AttackRunExit;
        }

        return targetPos;
    }

    void SteerFlyer(Vector3 flyTo, float deltaTime)
    {
        Transform t = Vehicle.transform;
        Vector3 toGoal = flyTo - t.position;
        if (toGoal.sqrMagnitude < 1f)
        {
            Controller.MoveDirection = new Vector2(0f, 1f);
            return;
        }
        Vector3 dir = toGoal.normalized;

        // yaw: signed heading error on the horizontal plane
        Vector3 flatFwd = t.forward; flatFwd.y = 0f;
        Vector3 flatDir = dir; flatDir.y = 0f;
        float yawError = (flatFwd.sqrMagnitude > 0.001f && flatDir.sqrMagnitude > 0.001f)
            ? Vector3.SignedAngle(flatFwd.normalized, flatDir.normalized, Vector3.up)
            : 0f;

        // pitch: elevation difference relative to our own facing
        float pitchError = Vector3.SignedAngle(t.forward, dir, t.right);

        // PhxFlyer scales these by PitchRate/TurnRate * 16 * deltaTime, so
        // feed normalized -1..1 "stick" values
        Controller.mouseX = Mathf.Clamp(yawError / 45f, -1f, 1f);
        Controller.mouseY = Mathf.Clamp(-pitchError / 45f, -1f, 1f);

        // bank into the turn (MoveDirection.x is roll on flyers)
        float roll = Mathf.Clamp(yawError / 60f, -1f, 1f);

        // throttle: full unless we're about to overshoot a close waypoint
        float throttle = toGoal.magnitude < 60f ? 0f : 1f;
        Controller.MoveDirection = new Vector2(roll, throttle);

        // terrain avoidance: if something is dead ahead or we're low, climb
        if (Physics.Raycast(t.position, t.forward, 120f) || t.position.y < 25f)
        {
            Controller.mouseY = -1f;   // nose up
        }
    }

    // --------------------------------------------------------------- ground

    void TickGround(float deltaTime, Vector3 goal, PhxAISkillProfile skill)
    {
        Transform t = Vehicle.transform;

        // reversing out of a jam
        if (ReverseTimer > 0f)
        {
            ReverseTimer -= deltaTime;
            Controller.MoveDirection = new Vector2(0f, -1f);
            Controller.mouseX = 0.6f;   // turn while backing up
            return;
        }

        Vector3 toGoal = goal - t.position;
        toGoal.y = 0f;
        float dist = toGoal.magnitude;
        Vector3 dir = dist > 0.001f ? toGoal.normalized : t.forward;

        // obstacle deflection: probe ahead and to both front corners
        Vector3 probe = t.position + Vector3.up * 1.0f;
        bool blockedAhead = Physics.Raycast(probe, t.forward, 8f);
        if (blockedAhead)
        {
            bool leftClear = !Physics.Raycast(probe, Quaternion.Euler(0f, -35f, 0f) * t.forward, 8f);
            bool rightClear = !Physics.Raycast(probe, Quaternion.Euler(0f, 35f, 0f) * t.forward, 8f);
            if (leftClear) dir = Quaternion.Euler(0f, -35f, 0f) * t.forward;
            else if (rightClear) dir = Quaternion.Euler(0f, 35f, 0f) * t.forward;
        }

        // steer toward dir
        Vector3 flatFwd = t.forward; flatFwd.y = 0f;
        float yawError = (flatFwd.sqrMagnitude > 0.001f)
            ? Vector3.SignedAngle(flatFwd.normalized, dir, Vector3.up)
            : 0f;
        Controller.mouseX = Mathf.Clamp(yawError / 40f, -1f, 1f);

        // don't drive at full speed into a sharp turn
        float throttle = Mathf.Abs(yawError) > 70f ? 0.4f : 1f;
        if (dist < 12f) throttle = 0f;
        Controller.MoveDirection = new Vector2(0f, throttle);

        // stuck detection -> reverse
        if (throttle > 0.1f)
        {
            if ((t.position - LastPosition).magnitude < 0.15f)
            {
                StuckTimer += deltaTime;
                if (StuckTimer > 1.2f)
                {
                    ReverseTimer = 1.5f;
                    StuckTimer = 0f;
                }
            }
            else
            {
                StuckTimer = 0f;
            }
        }
        LastPosition = t.position;

        // fire on targets of opportunity
        if (CurrentTarget != null)
        {
            Vector3 targetPos = CurrentTarget.transform.position;
            Seat.AimOverride = targetPos;
            Vector3 toTarget = targetPos - t.position;
            Controller.ShootPrimary = toTarget.magnitude < 250f &&
                                      Vector3.Angle(t.forward, toTarget.normalized) < 45f;
        }
        else
        {
            Seat.AimOverride = null;
            Controller.ShootPrimary = false;
        }
    }

    // -------------------------------------------------------------- targets

    Component AcquireTarget(int team)
    {
        Transform t = Vehicle.transform;
        Component best = null;
        float bestScore = float.MaxValue;

        // Air units prioritize enemy capital ships: engines are the one
        // critical system attackable from outside, so hit those first.
        if (IsAir)
        {
            foreach (PhxCapitalShip ship in PhxCapitalShip.GetAll())
            {
                if (ship.Team == team) continue;
                if (ship.State == PhxCapitalShip.PhxShipState.Dying ||
                    ship.State == PhxCapitalShip.PhxShipState.Destroyed) continue;

                foreach (PhxCapitalShipSubsystem sys in ship.GetComponentsInChildren<PhxCapitalShipSubsystem>())
                {
                    if (!sys.IsAlive || sys.IsInternal) continue;

                    float d = Vector3.Distance(t.position, sys.transform.position);
                    if (d > 1500f) continue;
                    // strong preference for ship targets over infantry
                    float score = d * 0.25f;
                    if (score < bestScore) { bestScore = score; best = sys; }
                }

                // shields still up? shooting the hull still drains them
                if (best == null && ship.GetShieldPercent() > 0f)
                {
                    float d = Vector3.Distance(t.position, ship.transform.position);
                    if (d < 1500f) { bestScore = d * 0.3f; best = ship; }
                }
            }
        }

        // nearby enemy vehicles / soldiers
        int count = Physics.OverlapSphereNonAlloc(t.position, IsAir ? 500f : 250f, OverlapCache);
        for (int i = 0; i < count; ++i)
        {
            PhxVehicle v = OverlapCache[i].GetComponentInParent<PhxVehicle>();
            if (v != null && v != Vehicle && !v.IsDestroyed && v.Team != team && v.Team != 0)
            {
                float score = Vector3.Distance(t.position, v.transform.position) * 0.5f;
                if (score < bestScore) { bestScore = score; best = v; }
                continue;
            }

            PhxSoldier s = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (s != null && !s.IsDead && s.Team != team && s.Team != 0)
            {
                float score = Vector3.Distance(t.position, s.transform.position);
                if (score < bestScore) { bestScore = score; best = s; }
            }
        }

        return best;
    }

    /// <summary>Clear controller state when the AI leaves the vehicle.</summary>
    public void Release()
    {
        if (Seat != null) Seat.AimOverride = null;
        if (Controller != null)
        {
            Controller.ShootPrimary = false;
            Controller.MoveDirection = Vector2.zero;
            Controller.mouseX = 0f;
            Controller.mouseY = 0f;
        }
    }
}
