using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Modernized soldier AI. Replaces the stub PhxSoldierAIController with a real
/// combat brain, parameterized by PhxAISkillProfile (Classic -> Legendary):
///
///  - target acquisition with line-of-sight, reaction time, burst discipline
///  - grenade / secondary weapon usage against clustered or mid-range targets
///  - strafing and cover (crouch) behavior in fights
///  - objective play: ATTACK command posts, DEFEND owned posts (director
///    assigned), and BOARD enemy capital ships once their shields drop -
///    muster, ride a (simulated) transport to the hangar, then sabotage the
///    critical systems and reactor on foot
///  - vehicle usage: mounts nearby free vehicles for long approaches, drives
///    toward the objective, dismounts on arrival
///  - stuck detection with active recovery (jump + sidestep + fresh approach
///    vector) so AI never grinds against geometry forever
/// </summary>
public class PhxBF3AIController : PhxAIController
{
    enum PhxAIState
    {
        SeekObjective,   // move to assigned/nearest capturable command post
        Defend,          // hold position around an owned command post
        Engage,          // fight a visible enemy
        Capture,         // stand in CP region until captured
        Board,           // muster + transport onto an enemy capital ship
        Sabotage,        // inside the ship: destroy critical systems / reactor
        ManTurret,       // walk to a friendly ship turret console and hold it
    }

    public PhxAISkillProfile Skill;

    // Set by PhxAIDirector for squad play
    public PhxCommandpost AssignedObjective;
    public PhxCommandpost DefendObjective;
    public PhxCapitalShip BoardTarget;
    public Vector3 FlankOffset;   // world-space detour offset when approaching contested areas

    PhxAIState State = PhxAIState.SeekObjective;

    // combat memory
    IPhxControlableInstance TargetPawn;
    PhxCapitalShipSubsystem TargetSubsystem;
    float ReactionTimer;
    float BurstTimer;
    float BurstPauseTimer;
    float StrafeDir = 1f;
    float StrafeTimer;
    float RetargetTimer;
    float GrenadeTimer = 5f;
    float GrenadePulse;

    // boarding
    float BoardMusterTimer;

    // vehicle operation (land + air), created when we occupy a seat
    PhxAIVehicleOperator VehicleOp;

    // hangar turret console we're heading for / holding
    PhxShipTurretStation ManningStation;
    float TurretScanTimer;

    // stuck recovery
    Vector3 LastPosition;
    float StuckTimer;
    float UnstickTimer;
    Vector3 UnstickDir;

    static readonly Collider[] OverlapCache = new Collider[128];

    // Director: don't reassign units that are mid-boarding-run
    public bool IsBusyBoarding => State == PhxAIState.Board || State == PhxAIState.Sabotage;


    public PhxBF3AIController()
    {
        Skill = PhxAIDirector.GetSkillProfile();
        PhxAIDirector.Register(this);
    }

    public override Vector3 GetAimPosition()
    {
        if (TargetSubsystem != null)
        {
            return TargetSubsystem.transform.position;
        }
        if (TargetPawn != null && TargetPawn.GetInstance() != null)
        {
            // center-mass aim with skill-based error cone
            Vector3 targetPos = TargetPawn.GetInstance().transform.position + Vector3.up * 1.2f;
            Vector3 dir = (targetPos - PawnPosition()).normalized;
            dir = ApplyAimError(dir, Skill.AimErrorDegrees);
            return PawnPosition() + dir * Vector3.Distance(PawnPosition(), targetPos);
        }
        return PawnPosition() + ViewDirection * 1000f;
    }

    public override void Tick(float deltaTime)
    {
        // Note: intentionally NOT calling PhxAIController.Tick - it overwrites
        // ViewDirection from the legacy Target field.
        if (Pawn == null)
        {
            return;
        }

        Team = Pawn.GetInstance().Team;
        Reload = false;
        ShootSecondary = false;
        Jump = false;

        GrenadeTimer -= deltaTime;

        // While in a vehicle the operator owns all control inputs - the
        // on-foot state machine must not fight it for the same fields.
        if (Pawn is PhxSoldier mounted && mounted.IsInVehicle)
        {
            PhxCommandpost vehGoal = GetObjective() ?? DefendObjective;
            Vector3 goalPos = vehGoal != null ? vehGoal.transform.position : PawnPosition();
            TryUseVehicle(goalPos, Vector3.Distance(PawnPosition(), goalPos));
            return;
        }
        if (VehicleOp != null)
        {
            // we left the vehicle (ejected, destroyed, killed)
            ReleaseVehicle();
        }

        // periodic (re)targeting - full scans are too costly per frame
        RetargetTimer -= deltaTime;
        if (RetargetTimer <= 0f)
        {
            RetargetTimer = 0.4f;
            AcquireTarget();
        }

        // director-driven state promotion
        if (BoardTarget != null && State != PhxAIState.Board && State != PhxAIState.Sabotage &&
            State != PhxAIState.Engage && BoardTarget.CanBeBoarded())
        {
            State = PhxAIState.Board;
            BoardMusterTimer = Random.Range(2f, 5f);
        }
        else if (DefendObjective != null && State == PhxAIState.SeekObjective && AssignedObjective == null)
        {
            State = PhxAIState.Defend;
        }

        // defenders aboard a threatened friendly ship man its hangar turrets
        TurretScanTimer -= deltaTime;
        if (TurretScanTimer <= 0f &&
            (State == PhxAIState.Defend || State == PhxAIState.SeekObjective))
        {
            TurretScanTimer = 4f;
            PhxShipTurretStation station = FindFreeTurretStation();
            if (station != null)
            {
                ManningStation = station;
                State = PhxAIState.ManTurret;
            }
        }

        switch (State)
        {
            case PhxAIState.SeekObjective: TickSeekObjective(deltaTime); break;
            case PhxAIState.Defend: TickDefend(deltaTime); break;
            case PhxAIState.Engage: TickEngage(deltaTime); break;
            case PhxAIState.Capture: TickCapture(deltaTime); break;
            case PhxAIState.Board: TickBoard(deltaTime); break;
            case PhxAIState.Sabotage: TickSabotage(deltaTime); break;
            case PhxAIState.ManTurret: TickManTurret(deltaTime); break;
        }

        TickStuckRecovery(deltaTime);
    }

    // ---------------------------------------------------------------- states

    void TickSeekObjective(float deltaTime)
    {
        ShootPrimary = false;

        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime;
            State = PhxAIState.Engage;
            return;
        }

        PhxCommandpost cp = GetObjective();
        if (cp == null)
        {
            MoveDirection = Vector2.zero;
            return;
        }

        Vector3 goal = cp.transform.position;
        float dist = Vector3.Distance(PawnPosition(), goal);

        // long approach? grab a ride
        if (TryUseVehicle(goal, dist))
        {
            return;
        }

        // flanking: approach via a lateral detour until close
        if (FlankOffset != Vector3.zero && dist > 30f)
        {
            goal += FlankOffset;
        }

        if (dist < 8f)
        {
            CapturePost = cp;
            State = PhxAIState.Capture;
            MoveDirection = Vector2.zero;
            return;
        }

        MoveTowards(goal);
    }

    void TickDefend(float deltaTime)
    {
        ShootPrimary = false;

        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime * 0.7f;   // defenders are ready
            State = PhxAIState.Engage;
            return;
        }

        if (DefendObjective == null || DefendObjective.Team != Team)
        {
            // lost the post (or it got taken) - retake it
            AssignedObjective = DefendObjective;
            DefendObjective = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        // patrol a loose ring around the post
        Vector3 anchor = DefendObjective.transform.position;
        float dist = Vector3.Distance(PawnPosition(), anchor);
        if (dist > 25f)
        {
            MoveTowards(anchor);
        }
        else
        {
            StrafeTimer -= deltaTime;
            if (StrafeTimer <= 0f)
            {
                StrafeTimer = Random.Range(2f, 4f);
                StrafeDir = Random.value < 0.5f ? -1f : 1f;
                // face a random outward direction to watch approaches
                Vector3 outward = (PawnPosition() - anchor).normalized;
                ViewDirection = (outward + new Vector3(Random.Range(-0.6f, 0.6f), 0f, Random.Range(-0.6f, 0.6f))).normalized;
            }
            MoveDirection = dist < 10f ? new Vector2(StrafeDir * 0.4f, 0f) : Vector2.zero;
        }
    }

    void TickEngage(float deltaTime)
    {
        if (TargetPawn == null || TargetPawn.GetInstance() == null ||
            (TargetPawn is PhxSoldier deadCheck && deadCheck.IsDead))
        {
            TargetPawn = null;
            ShootPrimary = false;
            State = ReturnState();
            return;
        }

        Vector3 targetPos = TargetPawn.GetInstance().transform.position;
        ViewDirection = (targetPos + Vector3.up * 1.2f - PawnPosition()).normalized;

        // reaction delay before opening fire
        if (ReactionTimer > 0f)
        {
            ReactionTimer -= deltaTime;
            ShootPrimary = false;
            return;
        }

        float dist = Vector3.Distance(PawnPosition(), targetPos);

        // grenade / secondary weapon: mid-range targets, on cooldown, skill-gated
        if (GrenadePulse > 0f)
        {
            GrenadePulse -= deltaTime;
            ShootSecondary = true;
        }
        else if (GrenadeTimer <= 0f && dist > 8f && dist < 32f &&
                 Random.value < Skill.StrafeAggression * 0.5f * deltaTime * 10f)
        {
            GrenadeTimer = Random.Range(8f, 16f);
            GrenadePulse = 0.15f;
        }

        // burst fire discipline
        if (BurstPauseTimer > 0f)
        {
            BurstPauseTimer -= deltaTime;
            ShootPrimary = false;
        }
        else
        {
            BurstTimer += deltaTime;
            ShootPrimary = true;
            if (BurstTimer >= Skill.BurstLength)
            {
                BurstTimer = 0f;
                BurstPauseTimer = Skill.BurstPause;

                // consider reloading in the pause
                IPhxWeapon weapon = Pawn.GetPrimaryWeapon();
                if (weapon != null && weapon.GetMagazineAmmo() <= weapon.GetMagazineSize() / 4)
                {
                    Reload = true;
                }
            }
        }

        // combat movement: strafe and use cover based on skill
        StrafeTimer -= deltaTime;
        if (StrafeTimer <= 0f)
        {
            StrafeTimer = Random.Range(0.8f, 1.8f);
            StrafeDir = Random.value < 0.5f ? -1f : 1f;
            Crouch = Random.value < Skill.CoverUsage;
        }

        Vector2 move = Vector2.zero;
        if (Random.value < Skill.StrafeAggression)
        {
            move.x = StrafeDir;
        }
        if (dist > 25f) move.y = 1f;        // close in
        else if (dist < 8f) move.y = -0.7f; // back off
        MoveDirection = move;
    }

    void TickCapture(float deltaTime)
    {
        ShootPrimary = false;
        MoveDirection = Vector2.zero;

        if (TargetPawn != null)
        {
            State = PhxAIState.Engage;
            return;
        }

        PhxCommandpost cp = CapturePost;
        if (cp == null || cp.Team == Team)
        {
            // captured (or lost the reference) - find the next objective
            CapturePost = null;
            AssignedObjective = null;
            State = PhxAIState.SeekObjective;
        }
    }

    void TickBoard(float deltaTime)
    {
        ShootPrimary = false;

        if (BoardTarget == null || !BoardTarget.CanBeBoarded())
        {
            BoardTarget = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime;
            State = PhxAIState.Engage;
            return;
        }

        // Muster, then ride the (simulated) boarding transport to the hangar.
        // TODO: replace the teleport with actual AI-piloted transports once
        // flyer AI can land in hangars.
        BoardMusterTimer -= deltaTime;
        MoveDirection = Vector2.zero;
        if (BoardMusterTimer <= 0f && BoardTarget.HangarEntrance != null)
        {
            Vector3 dropPoint = BoardTarget.HangarEntrance.position;
            Pawn.GetInstance().transform.position = dropPoint;
            Debug.Log($"[BF3Legacy] AI boarding party inserted into {BoardTarget.ShipName}");
            State = PhxAIState.Sabotage;
        }
    }

    void TickSabotage(float deltaTime)
    {
        if (BoardTarget == null ||
            BoardTarget.State == PhxCapitalShip.PhxShipState.Dying ||
            BoardTarget.State == PhxCapitalShip.PhxShipState.Destroyed)
        {
            // job done - use an escape pod if one is close, else fight on
            TargetSubsystem = null;
            BoardTarget = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime;
            TargetSubsystem = null;
            State = PhxAIState.Engage;
            return;
        }

        // pick the next system to destroy: criticals first, then the reactor
        if (TargetSubsystem == null || !TargetSubsystem.IsAlive)
        {
            TargetSubsystem = PickSabotageTarget();
            if (TargetSubsystem == null)
            {
                MoveDirection = Vector2.zero;
                ShootPrimary = false;
                return;
            }
        }

        Vector3 sysPos = TargetSubsystem.transform.position;
        float dist = Vector3.Distance(PawnPosition(), sysPos);
        ViewDirection = (sysPos - PawnPosition()).normalized;

        if (dist > 10f)
        {
            ShootPrimary = false;
            MoveTowards(sysPos, sprint: false);
        }
        else
        {
            MoveDirection = Vector2.zero;
            ShootPrimary = true;   // pour fire into the system
        }
    }

    /// <summary>
    /// Hold a hangar turret console. The station itself does the shooting
    /// (PhxShipTurretStation mans automatically for whoever stands there);
    /// the AI's job is to get there and stay put while it's useful.
    /// </summary>
    void TickManTurret(float deltaTime)
    {
        ShootPrimary = false;

        if (ManningStation == null || ManningStation.Ship == null ||
            ManningStation.Ship.State == PhxCapitalShip.PhxShipState.Dying ||
            ManningStation.Ship.State == PhxCapitalShip.PhxShipState.Destroyed)
        {
            ManningStation = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        // taken by someone else? find other work
        if (ManningStation.Operator != null && !ReferenceEquals(ManningStation.Operator, Pawn))
        {
            ManningStation = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        // a boarder in our face outranks the turret
        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime * 0.7f;
            State = PhxAIState.Engage;
            return;
        }

        float dist = Vector3.Distance(PawnPosition(), ManningStation.transform.position);
        if (dist > 1.8f)
        {
            MoveTowards(ManningStation.transform.position, sprint: dist > 20f);
        }
        else
        {
            // in position - the station takes over from here
            MoveDirection = Vector2.zero;
            Vector3 outward = ManningStation.transform.position - ManningStation.Ship.transform.position;
            outward.y = 0f;
            if (outward.sqrMagnitude > 0.01f) ViewDirection = outward.normalized;
        }
    }

    /// <summary>
    /// Look for an unmanned friendly hangar turret worth taking. Only while
    /// our own ship is actually threatened (shields down = enemies inbound).
    /// </summary>
    PhxShipTurretStation FindFreeTurretStation()
    {
        PhxCapitalShip ship = PhxCapitalShip.GetShipOfTeam(Team);
        if (ship == null || !ship.CanBeBoarded()) return null;

        PhxShipTurretStation best = null;
        float bestDist = 120f;   // only worth walking this far
        foreach (PhxShipTurretStation station in ship.GetComponentsInChildren<PhxShipTurretStation>())
        {
            if (station.Operator != null || station.PlayerPossessed) continue;

            float d = Vector3.Distance(PawnPosition(), station.transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = station;
            }
        }
        return best;
    }

    PhxCapitalShipSubsystem PickSabotageTarget()
    {
        PhxCapitalShipSubsystem best = null;
        float bestDist = float.MaxValue;
        bool reactorExposed = BoardTarget.State == PhxCapitalShip.PhxShipState.ReactorExposed;

        foreach (PhxCapitalShipSubsystem sys in BoardTarget.GetComponentsInChildren<PhxCapitalShipSubsystem>())
        {
            if (!sys.IsAlive || !sys.IsInternal) continue;

            bool isReactor = sys.Type == PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor;
            if (isReactor && !reactorExposed) continue;      // still shielded
            if (!isReactor && reactorExposed) continue;      // go straight for the kill

            float d = (sys.transform.position - PawnPosition()).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = sys;
            }
        }
        return best;
    }

    // ------------------------------------------------------------- vehicles

    bool TryUseVehicle(Vector3 goal, float distToGoal)
    {
        if (!(Pawn is PhxSoldier soldier)) return false;

        if (soldier.IsInVehicle)
        {
            PhxSeat seat = soldier.GetCurrentSeat();
            PhxVehicle vehicle = seat?.Owner as PhxVehicle;

            if (seat == null || vehicle == null || vehicle.IsDestroyed)
            {
                ReleaseVehicle();
                return false;
            }

            if (VehicleOp == null || VehicleOp.Seat != seat)
            {
                VehicleOp = new PhxAIVehicleOperator(this, vehicle, seat);
            }

            // Gunners ride along; pilots/drivers dismount once they've
            // delivered the squad (air units keep fighting instead).
            if (!VehicleOp.IsGunnerOnly && !VehicleOp.IsAir && distToGoal < 25f)
            {
                ReleaseVehicle();
                Enter = true;   // eject (handled by the seat)
                return true;
            }

            VehicleOp.Tick(Time.deltaTime, goal, Team, Skill);
            return true;
        }

        // on foot: worth mounting up? (long approach, or an aircraft is free
        // and there's a capital ship to kill)
        bool wantsAircraft = HasBoardableOrKillableShip();
        if (distToGoal < 80f && !wantsAircraft) return false;

        PhxVehicle free = FindNearbyFreeVehicle(wantsAircraft ? 60f : 20f);
        if (free == null) return false;

        float d = Vector3.Distance(PawnPosition(), free.transform.position);
        if (d > 4f)
        {
            MoveTowards(free.transform.position);
        }
        else
        {
            MoveDirection = Vector2.zero;
            Enter = true;   // PhxSoldier picks the closest available seat
        }
        return true;
    }

    void ReleaseVehicle()
    {
        VehicleOp?.Release();
        VehicleOp = null;
    }

    bool HasBoardableOrKillableShip()
    {
        foreach (PhxCapitalShip ship in PhxCapitalShip.GetAll())
        {
            if (ship.Team != Team &&
                ship.State != PhxCapitalShip.PhxShipState.Dying &&
                ship.State != PhxCapitalShip.PhxShipState.Destroyed)
            {
                return true;
            }
        }
        return false;
    }

    PhxVehicle FindNearbyFreeVehicle(float radius)
    {
        int count = Physics.OverlapSphereNonAlloc(PawnPosition(), radius, OverlapCache);
        PhxVehicle best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; ++i)
        {
            PhxVehicle v = OverlapCache[i].GetComponentInParent<PhxVehicle>();
            if (v == null || !v.HasAvailableSeat()) continue;
            int vTeam = v.Team;
            if (vTeam != 0 && vTeam != Team) continue;

            float d = (v.transform.position - PawnPosition()).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = v;
            }
        }
        return best;
    }

    // ------------------------------------------------------- stuck recovery

    void TickStuckRecovery(float deltaTime)
    {
        if (!(Pawn is PhxSoldier soldier) || soldier.IsInVehicle)
        {
            StuckTimer = 0f;
            return;
        }

        // active unstick maneuver: sidestep + jump for a moment
        if (UnstickTimer > 0f)
        {
            UnstickTimer -= deltaTime;
            ViewDirection = UnstickDir;
            MoveDirection = new Vector2(0f, 1f);
            if (UnstickTimer > 0.6f)
            {
                Jump = true;
            }
            return;
        }

        bool wantsToMove = MoveDirection.sqrMagnitude > 0.1f;
        if (!wantsToMove)
        {
            StuckTimer = 0f;
            LastPosition = PawnPosition();
            return;
        }

        if ((PawnPosition() - LastPosition).magnitude > 0.35f)
        {
            StuckTimer = 0f;
            LastPosition = PawnPosition();
            return;
        }

        StuckTimer += deltaTime;
        if (StuckTimer > 1.5f)
        {
            // pick a fresh direction: perpendicular to where we tried to go,
            // random side, with some randomization to break symmetric traps
            Vector3 tried = ViewDirection;
            tried.y = 0f;
            Vector3 side = Vector3.Cross(tried.normalized, Vector3.up) * (Random.value < 0.5f ? 1f : -1f);
            UnstickDir = (side + tried.normalized * -0.3f + Random.insideUnitSphere * 0.2f);
            UnstickDir.y = 0f;
            UnstickDir = UnstickDir.normalized;
            UnstickTimer = 1.0f;
            StuckTimer = 0f;

            // also refresh the flank offset so we don't retrace the same path
            if (FlankOffset != Vector3.zero)
            {
                FlankOffset = Quaternion.Euler(0f, Random.Range(-60f, 60f), 0f) * FlankOffset;
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    PhxAIState ReturnState()
    {
        if (BoardTarget != null && BoardTarget.CanBeBoarded()) return PhxAIState.Sabotage;
        if (DefendObjective != null && DefendObjective.Team == Team) return PhxAIState.Defend;
        return PhxAIState.SeekObjective;
    }

    Vector3 PawnPosition()
    {
        return Pawn.GetInstance().transform.position;
    }

    void MoveTowards(Vector3 goal, bool sprint = true)
    {
        Vector3 toGoal = goal - PawnPosition();
        toGoal.y = 0f;
        if (toGoal.sqrMagnitude < 0.01f)
        {
            MoveDirection = Vector2.zero;
            return;
        }

        Vector3 dir = toGoal.normalized;

        // whisker steering: probe ahead, deflect around obstacles instead of
        // walking into them (cheap alternative to full navmesh pathing)
        Vector3 eye = PawnPosition() + Vector3.up * 1.0f;
        if (Physics.Raycast(eye, dir, out RaycastHit hit, 4f) &&
            hit.collider.GetComponentInParent<PhxSoldier>() == null)
        {
            Vector3 left = Quaternion.Euler(0f, -40f, 0f) * dir;
            Vector3 right = Quaternion.Euler(0f, 40f, 0f) * dir;
            bool leftClear = !Physics.Raycast(eye, left, 4f);
            bool rightClear = !Physics.Raycast(eye, right, 4f);
            if (leftClear && !rightClear) dir = left;
            else if (rightClear && !leftClear) dir = right;
            else if (leftClear && rightClear) dir = Random.value < 0.5f ? left : right;
            // neither clear: keep pushing, stuck recovery will kick in
        }

        ViewDirection = dir;
        MoveDirection = new Vector2(0f, 1f);
        Sprint = sprint && toGoal.magnitude > 40f;
    }

    void AcquireTarget()
    {
        TargetPawn = null;

        int count = Physics.OverlapSphereNonAlloc(PawnPosition(), Skill.DetectionRange, OverlapCache);
        float bestDist = float.MaxValue;

        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.IsDead) continue;
            if (soldier.Team == Team || soldier.Team == 0) continue;
            if (ReferenceEquals(soldier, Pawn)) continue;

            float d = Vector3.Distance(PawnPosition(), soldier.transform.position);
            if (d >= bestDist) continue;

            if (HasLineOfSight(soldier.transform.position + Vector3.up * 1.2f))
            {
                bestDist = d;
                TargetPawn = soldier;
            }
        }

        if (TargetPawn != null && State == PhxAIState.SeekObjective)
        {
            ReactionTimer = Skill.ReactionTime;
        }
    }

    bool HasLineOfSight(Vector3 targetPoint)
    {
        Vector3 eye = PawnPosition() + Vector3.up * 1.6f;
        Vector3 dir = targetPoint - eye;
        if (Physics.Raycast(eye, dir.normalized, out RaycastHit hit, dir.magnitude))
        {
            // if we hit something that's not (part of) the target, sight is blocked
            return hit.collider.GetComponentInParent<PhxSoldier>() != null;
        }
        return true;
    }

    PhxCommandpost GetObjective()
    {
        if (AssignedObjective != null && AssignedObjective.Team != Team)
        {
            return AssignedObjective;
        }

        // fallback: nearest enemy/neutral command post
        PhxCommandpost[] posts = PhxGame.GetScene()?.GetCommandPosts();
        if (posts == null) return null;

        PhxCommandpost best = null;
        float bestDist = float.MaxValue;
        foreach (PhxCommandpost cp in posts)
        {
            if (cp.Team == Team) continue;
            float d = Vector3.Distance(PawnPosition(), cp.transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = cp;
            }
        }
        return best;
    }

    static Vector3 ApplyAimError(Vector3 dir, float errorDegrees)
    {
        if (errorDegrees <= 0f) return dir;
        Quaternion error = Quaternion.Euler(
            Random.Range(-errorDegrees, errorDegrees),
            Random.Range(-errorDegrees, errorDegrees),
            0f);
        return error * dir;
    }
}
