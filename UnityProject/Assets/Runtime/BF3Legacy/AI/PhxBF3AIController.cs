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

    /// <summary>
    /// This soldier's job inside its squad, pushed by BFSquadSystem when the
    /// squad's state changes. Read per frame in combat, so it is stored rather
    /// than looked up - SquadOf is a scan over every squad.
    /// </summary>
    public BFSquadRole SquadRole = BFSquadRole.None;

    PhxAIState State = PhxAIState.SeekObjective;

    // combat memory
    IPhxControlableInstance TargetPawn;

    /// <summary>
    /// Whether this soldier currently has a live target. Exposed for the squad
    /// layer, which picks a formation from whether the squad is fighting or
    /// moving; goes through GetInstance() so a destroyed pawn reads as false.
    /// </summary>
    public bool HasVisibleTarget => TargetPawn != null && TargetPawn.GetInstance() != null;

    /// <summary>Team of the current target, or 0 when there isn't one.</summary>
    public int GetTargetTeam()
    {
        // Team is a PhxProp<int>, so read it through Get() rather than relying
        // on an implicit conversion unifying with a literal in a ternary.
        PhxInstance inst = TargetPawn?.GetInstance();
        if (inst == null) return 0;
        return inst.Team.Get();
    }

    /// <summary>Where this soldier's current target is, if it has one.</summary>
    public bool TryGetTargetPosition(out Vector3 position)
    {
        PhxInstance inst = TargetPawn?.GetInstance();
        if (inst == null)
        {
            position = Vector3.zero;
            return false;
        }
        position = inst.transform.position;
        return true;
    }
    PhxCapitalShipSubsystem TargetSubsystem;
    float ReactionTimer;
    float BurstTimer;
    float BurstPauseTimer;
    float StrafeDir = 1f;
    float StrafeTimer;
    float RetargetTimer;
    float DistractionTimer;
    float GrenadeTimer = 5f;
    float GrenadePulse;

    // boarding
    float BoardMusterTimer;

    // vehicle operation (land + air), created when we occupy a seat
    PhxAIVehicleOperator VehicleOp;

    // hangar turret console we're heading for / holding
    PhxShipTurretStation ManningStation;
    float TurretScanTimer;

    // --- BF2 navigation data ---
    // Route through the map's authored planning graph, replacing straight-line
    // movement. Recomputed when the goal moves or we finish the path.
    readonly List<Vector3> NavPath = new List<Vector3>();
    int NavIndex;
    Vector3 NavGoal = Vector3.positiveInfinity;
    float NavRepathTimer;

    // Which connectivity-graph / barrier size class this unit uses. The mod
    // tools define AISizeType in the odf exactly for this; it defaults to
    // SOLDIER when absent.
    PhxNavSize CachedNavSize = PhxNavSize.Soldier;
    object CachedNavSizePawn;

    PhxNavSize NavSize
    {
        get
        {
            // recompute when we're assigned a different pawn (respawn as a
            // different class changes the size category)
            if (!ReferenceEquals(CachedNavSizePawn, Pawn))
            {
                CachedNavSizePawn = Pawn;
                CachedNavSize = (Pawn is PhxSoldier soldier && soldier.IsInit)
                    ? PhxNavGraph.SizeFromAIType(soldier.C.AISizeType)
                    : PhxNavSize.Soldier;
            }
            return CachedNavSize;
        }
    }

    /// <summary>
    /// What this unit may do to cross an arc.
    /// </summary>
    /// <remarks>
    /// Jump only, for infantry: nothing here has a working jetpack, so routing
    /// a soldier over a JETJUMP arc gives them a route whose next leg they can
    /// never complete - they walk to the lip of the gap and stop. Excluding
    /// those arcs makes them take the long way round, which is a route they
    /// can finish. Restore JetJump here the moment jet troopers can actually
    /// jet, and the arcs the designers authored for them start being used.
    /// </remarks>
    PhxNavCapabilities NavCapabilities => PhxNavCapabilities.Infantry;

    // Tactical hint node we've claimed (cover / snipe position)
    PhxHintNode ClaimedHint;
    float HintScanTimer;

    // Below this the goal is close enough to walk at directly; above it, route
    // through the planning graph. Small on purpose - see MoveTowards.
    const float NavGraphMinDistance = 4f;

    // How long we tolerate making no progress before assuming the current route
    // is blocked and asking for a new one.
    const float NavRepathStuckTime = 0.6f;

    // stuck recovery
    Vector3 LastPosition;
    float StuckTimer;
    float UnstickTimer;
    Vector3 UnstickDir;

    // Target scans write here. Sized for a 64v64 scrum: OverlapSphereNonAlloc
    // silently stops at the array bound, so an undersized buffer means AI stop
    // seeing enemies exactly when the fight is thickest. The query is masked to
    // soldiers (below), which is what makes a buffer this size sufficient -
    // unmasked it filled with scenery long before it found people.
    static readonly Collider[] OverlapCache = new Collider[512];

    // Director: don't reassign units that are mid-boarding-run
    public bool IsBusyBoarding => State == PhxAIState.Board || State == PhxAIState.Sabotage;


    public PhxBF3AIController()
    {
        Skill = PhxAIDirector.GetSkillProfile();
        Aim = new BFAimState(BFAimProfile.ForDifficulty(PhxBF3.Config.AIDifficulty).WithJitter());
        PhxAIDirector.Register(this);
    }

    /// <summary>
    /// Clear per-life state before this controller possesses a fresh pawn.
    /// Controllers are reused across respawns (see PhxMatch.RespawnAI) so the
    /// scoreboard survives, but the dead soldier's combat/goal memory must not.
    /// </summary>
    public void ResetForRespawn()
    {
        // A recycled controller must not inherit the role its previous life had;
        // the director re-publishes on the next replan.
        SquadRole = BFSquadRole.None;
        // Re-read the skill profile: this controller was constructed before its
        // team was known (and possibly before the mission called
        // SetAIDifficulty at all), so its first profile could not account for
        // either. Respawn is when the team is settled.
        Skill = PhxAIDirector.GetSkillProfile(Team);
        Aim = new BFAimState(BFAimProfile.ForDifficulty(PhxAIDirectives.GetDifficulty(Team)).WithJitter());

        State = PhxAIState.SeekObjective;
        TargetPawn = null;
        TargetSubsystem = null;
        SeekPost = null;
        ReleaseHint();   // frees the node's occupancy slot, not just our ref
        ReleasePatrol();
        ReleaseMineNode();
        MinesLaid = 0;
        ManningStation = null;
        BoardTarget = null;
        VehicleOp = null;
        NavPath.Clear();
        NavGoal = Vector3.positiveInfinity;
        NavIndex = 0;
        NavRepathTimer = 0f;
        StuckTimer = 0f;
        UnstickTimer = 0f;
        ReactionTimer = 0f;
        MoveDirection = Vector2.zero;
        Jump = false;
        Crouch = false;
        ShootPrimary = false;
        ShootSecondary = false;

        // The previous life's sightings die with it - a fresh body should not
        // come back already knowing where its killer was standing.
        HasContact = false;
        LastContactTime = float.NegativeInfinity;

        // Fresh body, full health: nothing to retreat from.
        Retreating = false;
        WeaponSwitchTimer = 0f;
    }

    /// <summary>
    /// Where this soldier is actually pointing.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="BFAimState"/> rather than applying a flat error
    /// cone. The difference is that error here has causes with memory: it is
    /// large the instant a target is acquired and settles, grows with how fast
    /// the target is crossing the view, grows with range beyond the soldier's
    /// competence, and walks upward through a burst before resetting. A single
    /// cone cannot produce any of those, and they are what separate an
    /// opponent from a turret.
    /// </remarks>
    public override Vector3 GetAimPosition()
    {
        if (TargetSubsystem != null)
        {
            return TargetSubsystem.transform.position;
        }

        PhxInstance target = TargetPawn?.GetInstance();
        if (target != null)
        {
            Vector3 eye = PawnPosition() + Vector3.up * 1.6f;
            Vector3 targetPos = target.transform.position + Vector3.up * 1.2f;

            // Velocity for leading. Read off the body where there is one; a
            // target with no rigidbody is treated as stationary, which is the
            // conservative reading.
            Rigidbody body = target.GetComponent<Rigidbody>();
            Vector3 velocity = body != null && !body.isKinematic ? body.linearVelocity : Vector3.zero;

            return Aim.Aim(eye, TargetPawn, targetPos, velocity, Time.deltaTime);
        }
        return PawnPosition() + ViewDirection * 1000f;
    }

    /// <summary>This soldier's shooting, as a competence rather than a constant.</summary>
    public BFAimState Aim { get; private set; }

    public override void Tick(float deltaTime)
    {
        // Note: intentionally NOT calling PhxAIController.Tick - it overwrites
        // ViewDirection from the legacy Target field.
        // Pawn is an interface reference, so `== null` is plain reference
        // equality and does not detect a destroyed Unity object - go through
        // GetInstance() so Unity's null check applies, otherwise every call
        // below throws once the soldier is gone.
        if (Pawn == null || Pawn.GetInstance() == null)
        {
            return;
        }

        Team = Pawn.GetInstance().Team;
        Reload = false;
        ShootSecondary = false;
        Jump = false;

        // Weapon-change requests are ONE-SHOT inputs: PhxSoldier acts on them
        // every tick they are set and never clears them itself (see
        // PhxPlayerController, which clears them explicitly for the same
        // reason). Leaving them latched made the soldier call NextWeapon every
        // single tick - the weapon was deactivated and reactivated
        // continuously, so it was almost never past its reload progress check
        // and the fire branch became unreachable. That is why AI stopped
        // shooting entirely.
        NextPrimaryWeapon = false;
        NextSecondaryWeapon = false;
        // Per-frame default: states that want a crouch (combat cover, hint
        // nodes) re-assert it every Tick. Without this an AI that fought once
        // crawled to every later objective, and a crouched pawn can't perform
        // the unstick jump (Jump only stands it up).
        Crouch = false;

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

        // Resupply pre-empts every state, not just combat.
        //
        // TickSelfPreservation was called from TickEngage alone, so a soldier
        // only ever looked for a droid while it had a target. One that took
        // fire crossing open ground, broke line of sight and then marched on to
        // its objective at a tenth health never sought help at all - and since
        // dropping below the retreat threshold usually means the fight went
        // badly, that is exactly the soldier most likely to be out of contact.
        //
        // Sited here rather than inside each Tick so it cannot be forgotten by
        // a state added later, and after the state selection above so the state
        // it returns to is already correct when it finishes.
        if (State != PhxAIState.Board && State != PhxAIState.ManTurret &&
            TickSelfPreservation())
        {
            return;
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

        // Guard BEFORE stuck detection: a guard veto zeroes MoveDirection, and
        // stuck detection must see that as "doesn't want to move". The other
        // order judged every vetoed frame as "stuck" and produced a perpetual
        // ~2.5s plant-and-jump loop at the first ledge/slope.
        ApplyLedgeGuard();
        TickStuckRecovery(deltaTime);

        TickDecision(deltaTime);
        TickActionAnimation();
    }

    // Last intent published. Only a change is worth acting on.
    BFAIAction LastAnimAction = (BFAIAction)(-1);
    bool HaveAnimAction;

    /// <summary>What this soldier has most recently decided to do.</summary>
    public BFAIAction CurrentAction => HaveAnimAction ? LastAnimAction : BFAIAction.HoldPosition;

    /// <summary>
    /// Publish what this soldier is doing as a <see cref="BFAIAction"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT play a clip. PhxSoldier writes animator layer 0
    /// every frame from its locomotion state, so anything else writing layer 0
    /// is either overwritten within a frame or wins briefly and stutters. An
    /// earlier version of this called BFAIActionAnimation.Play here and did
    /// exactly that.
    ///
    /// The vocabulary is still worth publishing: it is what a HUD, the squad
    /// layer or the director can read to know intent without reaching into a
    /// private state machine. Turning it back into animation needs layer
    /// arbitration - somebody has to own layer 0 and take requests - which is a
    /// change to PhxSoldier's locomotion code, not a flag.
    /// </remarks>

    // How often the scored decision runs. It reads a dozen fields and walks a
    // short candidate list, so it is cheap - but not per-frame-per-soldier
    // cheap at 128 units, and the answer does not change that fast anyway.
    // Set by the decision/role logic in Engage and read by the movement and
    // cover code below, so the two stay in one place rather than each rolling
    // their own dice.
    bool EngageHoldGround;
    bool WantsCover;

    // Geometric cover, for where the map authored no hint nodes. Cached rather
    // than searched per decision: the search is about seventy line tests and
    // the decision runs several times a second.
    Vector3 CoverSpot;
    bool CoverSpotValid;
    bool HoldingCoverSpot;
    float CoverRecheckTimer;

    float DecisionTimer;
    BFAIAction Decision = BFAIAction.Attack;

    /// <summary>The action the scored chooser last picked for this soldier.</summary>
    public BFAIAction CurrentDecision => Decision;

    /// <summary>
    /// Run BFAIDecision over this soldier's situation.
    /// </summary>
    /// <remarks>
    /// The chooser has existed, fully written, with nothing ever calling it -
    /// scoring ten actions and applying difficulty as a band of good-enough
    /// options rather than always taking the best. This is the producer.
    ///
    /// It advises rather than replaces. The concrete state machine (seek,
    /// defend, engage, capture, board, sabotage, turret) works and owns
    /// movement; what it lacked was any variation in HOW a soldier fights once
    /// it is in contact. So the decision is consumed inside Engage, where the
    /// difference between pressing, taking cover, flanking and backing off is
    /// exactly the choice this scores - and where getting it wrong costs a
    /// firefight rather than a whole objective.
    /// </remarks>
    void TickDecision(float deltaTime)
    {
        DecisionTimer -= deltaTime;
        if (DecisionTimer > 0f) return;

        // Jittered so a squad that spawned together does not re-decide in
        // lockstep for the rest of the match.
        DecisionTimer = Random.Range(0.6f, 1.1f);

        if (!(Pawn is PhxSoldier self) || self == null) return;

        Vector3 pos = PawnPosition();

        BFAISituation s = default;
        s.Position = pos;
        s.Team = Team;
        s.HealthFraction = self.HealthFraction;

        IPhxWeapon weapon = self.GetPrimaryWeapon();
        int mag = weapon != null ? weapon.GetMagazineSize() : 0;
        s.AmmoFraction = mag > 0 ? Mathf.Clamp01(weapon.GetMagazineAmmo() / (float)mag) : 1f;

        s.HasVisibleEnemy = HasVisibleTarget;
        s.HasRememberedEnemy = HasFreshContact;
        s.DistanceToEnemy = TryGetTargetPosition(out Vector3 tp)
            ? Vector3.Distance(pos, tp)
            : (HasFreshContact ? Vector3.Distance(pos, LastKnownEnemyPosition) : float.MaxValue);

        CountNearby(pos, out int enemies, out int allies);
        s.NearbyEnemies = enemies;
        s.NearbyAllies = allies;

        s.LocalDanger = PhxAIDanger.Sample(pos, Team);

        PhxCommandpost objective = GetObjective() ?? DefendObjective;
        s.DistanceToObjective = objective != null
            ? Vector3.Distance(pos, objective.transform.position)
            : float.MaxValue;
        // "Contested" here means capture is partway through - somebody is
        // working on it, whoever that is.
        s.ObjectiveThreatened = objective != null && objective.GetCaptureProgress() > 0.01f;

        s.InCover = ClaimedHint != null || HoldingCoverSpot;

        // "Is there cover" and not "am I currently standing at a hint node".
        //
        // This used to be the latter, which meant the scorer was told cover was
        // reachable wherever a soldier happened to be - so SeekCover would win
        // on the strength of a promise nothing could keep, the hint scan would
        // find no node, and the soldier would stand still having just decided
        // to take cover. CoverSpotValid is a position that was actually found,
        // by the authored nodes or by BFAICover, so the answer is now true only
        // when there is somewhere to go.
        s.CoverAvailable = !s.InCover && Skill.CoverUsage > 0f && CoverSpotValid;

        Decision = BFAIDecision.Choose(s, (int)PhxBF3.Config.AIDifficulty,
                                       PhxAIDirectives.GetAggressiveness(Team));
    }

    /// <summary>
    /// Enemies and allies this soldier can plausibly account for.
    /// </summary>
    /// <remarks>
    /// Reuses the shared overlap buffer and the soldier layer mask, the same
    /// way target acquisition does - an unmasked query fills with scenery long
    /// before it finds people.
    /// </remarks>
    void CountNearby(Vector3 pos, out int enemies, out int allies)
    {
        enemies = 0;
        allies = 0;

        const float awareness = 30f;
        int count = Physics.OverlapSphereNonAlloc(pos, awareness, OverlapCache,
                                                  PhxLayers.Soldier,
                                                  QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; ++i)
        {
            PhxSoldier other = OverlapCache[i] != null
                ? OverlapCache[i].GetComponentInParent<PhxSoldier>()
                : null;
            if (other == null || other.IsDead) continue;

            if (other.Team.Get() == Team) ++allies;
            else ++enemies;
        }

        // We counted ourselves among the allies.
        if (allies > 0) --allies;
    }

    void TickActionAnimation()
    {
        if (!(Pawn is PhxSoldier soldier) || soldier.IsInVehicle) return;

        BFAIAction action = BFAIActionAnimation.FromControllerState(
            State.ToString(),
            HasVisibleTarget,
            Reload,
            soldier.HealthFraction < 0.3f,
            Crouch);

        if (HaveAnimAction && action == LastAnimAction) return;

        LastAnimAction = action;
        HaveAnimAction = true;

        // Only the actions locomotion cannot already express are worth playing.
        // Running to an objective looks like running whether the intent is
        // Advance, Pursue or Reinforce, and overriding those would replace a
        // speed-matched locomotion clip with a fixed one. Reload and SeekCover
        // are postures the movement state has no way to show.
        if (action != BFAIAction.Reload && action != BFAIAction.SeekCover) return;

        BFAIActionAnimation.Play(soldier, action,
                                 soldier.GetAnimationBankPrefix(),
                                 soldier.GetWeaponPosture());
    }

    // Maximum drop an AI will willingly walk into. Anything deeper is treated
    // as a pit rather than a step.
    const float MaxSafeDrop = 4f;

    // How far ahead to test. Roughly one stride, so the veto lands before the
    // pawn's centre crosses the edge.
    const float LedgeProbeAhead = 1.2f;

    /// <summary>
    /// Refuse a movement command that would step off a ledge.
    ///
    /// The navigation data is a graph of hubs and connections; it says where
    /// the AI may path, but nothing in it describes the drop between two
    /// levels of a multi-storey interior. On maps built from stacked platforms
    /// (Death Star II above all) the AI would happily walk straight off an edge
    /// toward a goal below and die, bleeding reinforcements all round.
    ///
    /// This is deliberately a veto on the final movement rather than a change
    /// to pathing: it cannot make the AI smarter about routes, it only stops
    /// them walking into a fall no player would take.
    /// </summary>
    void ApplyLedgeGuard()
    {
        if (MoveDirection.sqrMagnitude < 0.0001f) return;

        Transform pawnTf = PawnTransform();
        if (pawnTf == null) return;

        // MoveDirection is (strafe, forward) relative to the yaw of
        // ViewDirection - that is how PhxSoldier interprets it - NOT the pawn
        // transform, whose facing can be up to 180 degrees off while strafing.
        Vector3 flatView = ViewDirection;
        flatView.y = 0f;
        if (flatView.sqrMagnitude < 1e-4f) return;
        Quaternion look = Quaternion.LookRotation(flatView.normalized);
        Vector3 world = look * new Vector3(MoveDirection.x, 0f, MoveDirection.y);
        if (world.sqrMagnitude < 0.0001f) return;
        world.Normalize();

        // A wall directly ahead means there is no ledge to walk off - let the
        // steering whiskers and stuck recovery deal with it. Without this the
        // drop probe starts inside the wall's mesh and reports a phantom pit.
        //
        // SoldierGround, not ~0. A mask of everything counts the ordnance-only
        // and vehicle-only collision meshes the importer splits onto their own
        // layers, which a soldier walks straight through - so a bolt-blocking
        // pane in front of the AI read as a wall and suppressed the guard
        // entirely. The soldier layer is already absent from this mask, which
        // is what used to need the PhxSoldier exclusion below it.
        Vector3 chest = PawnPosition() + Vector3.up * 1.0f;
        if (Physics.Raycast(chest, world, LedgeProbeAhead,
                            PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        // Drop probe from above head height: starting only 0.5m up put the
        // origin underneath single-sided terrain on any slope over ~23 degrees,
        // where a downward ray exits through backfaces without a hit and the
        // guard froze the AI on every hill. A spherecast also tolerates thin
        // triangulation gaps a ray would slip through.
        //
        // SoldierGround here for the more serious version of the same reason.
        // With a mask of everything, an ordnance-only or vehicle-only collision
        // mesh under the drop answered "there is ground to land on" - and a
        // soldier does not collide with either, so the guard cleared the AI to
        // step off and it fell straight through the thing that had reassured
        // it. That is the walking-off-the-map case: not a missing guard, a
        // guard asking a question whose answer did not apply to soldiers.
        Vector3 probe = PawnPosition() + world * LedgeProbeAhead + Vector3.up * 1.8f;
        if (Physics.SphereCast(probe, 0.3f, Vector3.down, out _,
                               1.8f + MaxSafeDrop, PhxLayers.SoldierGround,
                               QueryTriggerInteraction.Ignore))
        {
            return;     // there is ground to land on
        }

        // Nothing underfoot ahead. Stop rather than reverse: reversing fights
        // the stuck-recovery logic, and a halted AI still turns and shoots.
        MoveDirection = Vector2.zero;
    }

    Transform PawnTransform()
    {
        return Pawn?.GetInstance() != null ? Pawn.GetInstance().transform : null;
    }

    // ---------------------------------------------------------------- states

    void TickSeekObjective(float deltaTime)
    {
        ShootPrimary = false;

        // ObjectiveFocus decides whether a visible enemy derails the march to
        // the objective. Close threats always win - nobody strolls past a
        // blaster at 15m. The roll happens in AcquireTarget (0.4s cadence).
        if (TargetPawn != null && TargetPawn.GetInstance() != null)
        {
            float threatDist = Vector3.Distance(PawnPosition(), TargetPawn.GetInstance().transform.position);
            if (EngageCommitted || threatDist < 15f)
            {
                ReactionTimer = Skill.ReactionTime;
                State = PhxAIState.Engage;
                return;
            }
        }
        else if (HasFreshContact && EngageCommitted)
        {
            // Nothing in sight, but we saw or heard something recently. Go and
            // look. Engage handles the no-target case by moving to the last
            // known position and dropping the contact once it arrives, so this
            // terminates instead of looping.
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

        // Switch to Capture only when the CP's trigger region has actually
        // registered us (it owns CapturePost), or we are practically on top of
        // it. Stopping at a fixed 8m parked squads just outside small capture
        // regions forever - capture progress is driven purely by the trigger.
        if (CapturePost == cp || dist < 2f)
        {
            SeekPost = cp;
            State = PhxAIState.Capture;
            return;
        }
        SeekPost = cp;

        MoveTowards(goal);
    }

    // The post this AI is trying to take. Distinct from CapturePost, which is
    // owned by the command post's trigger region (set on enter, cleared on
    // exit) and must not be written by the state machine.
    PhxCommandpost SeekPost;

    void TickDefend(float deltaTime)
    {
        ShootPrimary = false;

        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime * 0.7f;   // defenders are ready
            ReleasePatrol();
            ReleaseMineNode();
            State = PhxAIState.Engage;
            return;
        }

        if (DefendObjective == null || DefendObjective.Team != Team)
        {
            // lost the post (or it got taken) - retake it
            AssignedObjective = DefendObjective;
            DefendObjective = null;
            ReleasePatrol();
            State = PhxAIState.SeekObjective;
            return;
        }

        Vector3 anchor = DefendObjective.transform.position;
        float dist = Vector3.Distance(PawnPosition(), anchor);
        if (dist > 25f)
        {
            MoveTowards(anchor);
            return;
        }

        // An engineer holding a position mines the approaches first. MINE
        // nodes are where the designers wanted a minefield, so this is a
        // defence the level was built to have and previously never got.
        if (TickMineLaying(deltaTime, anchor)) return;

        // Walk the designers' patrol route where there is one. PATROL nodes
        // are the authored answer to "where should someone holding this
        // position walk", and they are placed along the approaches that
        // actually matter - which is the difference between a garrison that
        // looks posted and one that mills around the flag.
        if (TickPatrol(deltaTime, anchor)) return;

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

    // --- PATROL hint nodes ---

    /// <summary>How far from the defended post patrol nodes are considered.</summary>
    const float PatrolSearchRadius = 60f;

    /// <summary>Seconds spent watching at a patrol node before moving on.</summary>
    const float PatrolDwellSeconds = 4f;

    PhxHintNode PatrolNode;
    float PatrolDwellTimer;

    /// <summary>
    /// Move between authored patrol positions around <paramref name="anchor"/>.
    /// Returns false when the map has none nearby, so the caller falls back to
    /// its own loitering.
    /// </summary>
    bool TickPatrol(float deltaTime, Vector3 anchor)
    {
        if (PatrolNode == null)
        {
            PatrolNode = PhxHintNodes.NextPatrolNode(null, anchor, PatrolSearchRadius);
            if (PatrolNode == null) return false;

            // Claimed for as long as the walk plus the dwell could take, so two
            // defenders don't converge on the same corner.
            if (!PhxHintNodes.TryOccupy(PatrolNode, this, 30f))
            {
                PatrolNode = null;
                return false;
            }
            PatrolDwellTimer = 0f;
        }

        float toNode = Vector3.Distance(PawnPosition(), PatrolNode.Position);
        if (toNode > 2.5f)
        {
            MoveTowards(PatrolNode.Position, sprint: false);
            return true;
        }

        // Arrived: face the way the designer pointed the node and hold.
        MoveDirection = Vector2.zero;
        ViewDirection = PatrolNode.Facing;
        Crouch = PhxHintNodes.StanceIsLow(PatrolNode.PrimaryStance);

        PatrolDwellTimer += deltaTime;
        if (PatrolDwellTimer < PatrolDwellSeconds) return true;

        PhxHintNode next = PhxHintNodes.NextPatrolNode(PatrolNode, anchor, PatrolSearchRadius);
        PhxHintNodes.Release(PatrolNode, this);
        PatrolNode = null;

        if (next != null && PhxHintNodes.TryOccupy(next, this, 30f))
        {
            PatrolNode = next;
            PatrolDwellTimer = 0f;
        }
        return true;
    }

    void ReleasePatrol()
    {
        if (PatrolNode == null) return;

        PhxHintNodes.Release(PatrolNode, this);
        PatrolNode = null;
        PatrolDwellTimer = 0f;
    }

    // --- MINE hint nodes ---

    /// <summary>How far from the defended post an engineer will go to mine.</summary>
    const float MineSearchRadius = 50f;

    /// <summary>Seconds spent planting once at the node.</summary>
    const float MinePlantSeconds = 2f;

    /// <summary>Most mines one AI lays per life, so a post isn't carpeted.</summary>
    const int MaxMinesPerLife = 3;

    PhxHintNode MineNode;
    float MinePlantTimer;
    int MinesLaid;

    /// <summary>
    /// Lay mines at authored MINE positions while defending. Returns false
    /// when this unit carries no mine, has laid its quota, or the area has no
    /// MINE nodes - which is every unit on most maps.
    /// </summary>
    bool TickMineLaying(float deltaTime, Vector3 anchor)
    {
        if (MinesLaid >= MaxMinesPerLife) return false;

        PhxSoldier soldier = Pawn as PhxSoldier;
        PhxClass mineClass = soldier?.GetDeployable("mine");
        if (mineClass == null) return false;

        if (MineNode == null)
        {
            MineNode = PhxHintNodes.FindNearest(PawnPosition(), PhxHintType.Mine, MineSearchRadius);
            if (MineNode == null) return false;

            // Held for the walk plus the plant. A node stays claimed after the
            // mine goes down so a second engineer stacks their mine somewhere
            // else instead of on top of it.
            if (!PhxHintNodes.TryOccupy(MineNode, this, 120f))
            {
                MineNode = null;
                return false;
            }
            MinePlantTimer = 0f;
        }

        float toNode = Vector3.Distance(PawnPosition(), MineNode.Position);
        if (toNode > 2f)
        {
            MoveTowards(MineNode.Position, sprint: false);
            return true;
        }

        MoveDirection = Vector2.zero;
        Crouch = true;
        ViewDirection = MineNode.Facing;

        MinePlantTimer += deltaTime;
        if (MinePlantTimer < MinePlantSeconds) return true;

        PlaceMine(mineClass, MineNode);

        // The node keeps its claim (see above); we simply stop holding a
        // reference to it and look for another next time round.
        MineNode = null;
        MinePlantTimer = 0f;
        return true;
    }

    void PlaceMine(PhxClass mineClass, PhxHintNode node)
    {
        PhxScene scene = PhxGame.GetScene();
        if (scene == null) return;

        PhxInstance placed = scene.CreateInstance(
            mineClass, $"{mineClass.Name}_{node.Name}_{MinesLaid}",
            node.Position, node.Rotation);

        if (placed == null) return;

        placed.Team.Set(Team);
        if (placed is PhxMine mine)
        {
            mine.Owner = this;
        }
        ++MinesLaid;
    }

    void ReleaseMineNode()
    {
        if (MineNode == null) return;

        PhxHintNodes.Release(MineNode, this);
        MineNode = null;
        MinePlantTimer = 0f;
    }

    // --- Self-preservation (AI Stage 5) ---

    // Where we're falling back to while hurt, so the destination doesn't
    // re-roll every frame and leave the soldier jittering between two posts.
    Vector3 RetreatGoal;
    bool Retreating;

    /// <summary>Health fraction below which this AI breaks off to recover.</summary>
    const float RetreatHealthFraction = 0.25f;

    /// <summary>And the fraction at which it is willing to fight again.</summary>
    const float RecoveredHealthFraction = 0.6f;

    /// <summary>
    /// Break off and recover when badly hurt, heading for a resupply droid or
    /// a friendly post.
    /// </summary>
    /// <remarks>
    /// Returns true when it has taken control of this tick. Soldiers that fight
    /// to the death at 5 health read as mindless; more importantly, the
    /// resupply droids exist and nothing ever used them.
    /// </remarks>
    bool TickSelfPreservation()
    {
        PhxSoldier soldier = Pawn as PhxSoldier;
        if (soldier == null || !soldier.IsInit) return false;

        float health = soldier.HealthFraction;
        float ammo = AmmoFraction(soldier);

        bool hurt = health <= RetreatHealthFraction;
        bool dry = ammo <= ResupplyAmmoFraction;

        if (Retreating)
        {
            // Patched up, or restocked: back to the fight. Both conditions have
            // to clear, or a soldier who went for ammo walks away the instant
            // its health ticks up from a passing medic droid.
            bool recovered = health >= RecoveredHealthFraction || !NeededHealth;
            bool restocked = ammo >= RecoveredAmmoFraction || !NeededAmmo;

            if (recovered && restocked)
            {
                Retreating = false;
                NeededHealth = false;
                NeededAmmo = false;
                return false;
            }
        }
        else
        {
            // Ammo counts as much as health here. A soldier with a full bar and
            // an empty magazine contributes nothing to a firefight, and until
            // now nothing sent it to a droid - AmmoFraction was computed for
            // the decision scorer and then never acted on.
            if (!hurt && !dry) return false;

            Retreating = true;
            NeededHealth = hurt;
            NeededAmmo = dry;
            RetreatGoal = FindRecoveryPoint(wantHealth: hurt, wantAmmo: dry);
        }

        // Nowhere to go - better to keep fighting than stand still.
        if (RetreatGoal == Vector3.positiveInfinity)
        {
            Retreating = false;
            return false;
        }

        // Keep facing the threat while withdrawing, and keep shooting if we
        // can still see them - a retreat is not a surrender.
        if (TargetPawn != null && TargetPawn.GetInstance() != null)
        {
            ViewDirection = (TargetPawn.GetInstance().transform.position + Vector3.up * 1.2f
                             - PawnPosition()).normalized;
        }

        MoveTowards(RetreatGoal);
        return true;
    }

    // Resupply droids don't move and don't get built mid-round, so find them
    // once per map instead of per retreat. Scanning every scene instance (which
    // is thousands of objects) for each hurt soldier was fine at 16 units and a
    // frame-time cliff at 128.
    static readonly List<PhxPowerupstation> ResupplyStations = new List<PhxPowerupstation>();
    static PhxScene ResupplyStationsScene;

    static List<PhxPowerupstation> GetResupplyStations(PhxScene scene)
    {
        if (scene == null) return ResupplyStations;

        // Rebuild when the map changed; reference compare is enough because
        // PhxEnvironment creates exactly one PhxScene per load.
        if (!ReferenceEquals(scene, ResupplyStationsScene))
        {
            ResupplyStationsScene = scene;
            ResupplyStations.Clear();

            int count = scene.GetInstanceCount();
            for (int i = 0; i < count; ++i)
            {
                if (scene.GetInstance(i) is PhxPowerupstation station)
                {
                    ResupplyStations.Add(station);
                }
            }
        }
        return ResupplyStations;
    }

    /// <summary>
    /// Nearest resupply droid, or failing that a friendly command post.
    /// </summary>
    /// <summary>Rounds below which a soldier goes looking for a droid.</summary>
    const float ResupplyAmmoFraction = 0.2f;

    /// <summary>Rounds at which it is worth going back to the fight.</summary>
    const float RecoveredAmmoFraction = 0.7f;

    /// <summary>What sent this soldier away, so arrival can be judged.</summary>
    bool NeededHealth;
    bool NeededAmmo;

    /// <summary>Magazine fill of the primary weapon, 1 when it has no magazine.</summary>
    static float AmmoFraction(PhxSoldier soldier)
    {
        IPhxWeapon weapon = soldier.GetPrimaryWeapon();
        if (weapon == null) return 1f;

        int mag = weapon.GetMagazineSize();
        if (mag <= 0) return 1f;

        // Total ammo, not the loaded magazine - a soldier mid-reload is not
        // out of ammunition, and sending it across the map would be absurd.
        return Mathf.Clamp01(weapon.GetTotalAmmo() / (float)mag);
    }

    Vector3 FindRecoveryPoint(bool wantHealth, bool wantAmmo)
    {
        Vector3 self = PawnPosition();
        Vector3 best = Vector3.positiveInfinity;
        float bestDist = float.MaxValue;
        PhxScene scene = PhxGame.GetScene();

        // Droids first, but only ones that supply what is actually missing.
        //
        // This used to take the nearest station of any kind. A station's odf
        // states SoldierHealth and SoldierAmmo independently and either may be
        // zero, so a bleeding soldier could walk to the closest droid, stand in
        // an ammo-only resupply field, and be repaired by nothing.
        foreach (PhxPowerupstation station in GetResupplyStations(scene))
        {
            if (station == null || !station.IsInit) continue;

            bool heals = station.C.SoldierHealth.Get() > 0f;
            bool restocks = station.C.SoldierAmmo.Get() > 0f;

            if (wantHealth && !heals) continue;
            if (wantAmmo && !restocks && !wantHealth) continue;

            float d = Vector3.SqrMagnitude(station.transform.position - self);
            if (d < bestDist)
            {
                bestDist = d;
                best = station.transform.position;
            }
        }

        if (best != Vector3.positiveInfinity) return best;

        // Otherwise fall back on ground we hold.
        PhxCommandpost[] posts = scene?.GetCommandPosts();
        if (posts == null) return Vector3.positiveInfinity;

        for (int i = 0; i < posts.Length; ++i)
        {
            if (posts[i] == null || posts[i].Team != Team) continue;

            float d = Vector3.SqrMagnitude(posts[i].transform.position - self);
            if (d < bestDist)
            {
                bestDist = d;
                best = posts[i].transform.position;
            }
        }

        return best;
    }

    void TickEngage(float deltaTime)
    {
        // Self-preservation pre-empts every state from Tick now, so the
        // combat-only call that used to sit here is gone - keeping it would run
        // the check twice per frame for a soldier already in a firefight.

        if (TargetPawn == null || TargetPawn.GetInstance() == null ||
            (TargetPawn is PhxSoldier deadCheck && deadCheck.IsDead))
        {
            bool wasKilled = TargetPawn is PhxSoldier killed && killed.IsDead;
            TargetPawn = null;
            ShootPrimary = false;

            // A dead enemy is resolved; a vanished one is not. If we still hold
            // a fresh contact - last seen behind that corner, or heard firing -
            // press to it instead of shrugging and walking back to the
            // objective the instant line of sight breaks.
            if (!wasKilled && HasFreshContact)
            {
                float toContact = Vector3.Distance(PawnPosition(), LastKnownEnemyPosition);

                // Face where they were, and keep the position under fire while
                // closing. Suppression is most of what makes a firefight feel
                // like one: an enemy who stops shooting the instant you duck
                // behind cover reads as a target dummy, not an opponent.
                ViewDirection = (LastKnownEnemyPosition + Vector3.up * 1.2f - PawnPosition()).normalized;
                ShootPrimary = toContact < Skill.DetectionRange &&
                               Random.value < Skill.StrafeAggression;

                MoveTowards(LastKnownEnemyPosition, false);

                // Arrived and still nothing: the trail is cold, stop chasing.
                if (toContact < 3f)
                {
                    HasContact = false;
                    ShootPrimary = false;
                }
                return;
            }

            HasContact = false;
            ReleaseHint();          // don't camp a cover node with no enemy
            State = ReturnState();
            return;
        }

        Vector3 targetPos = TargetPawn.GetInstance().transform.position;
        RememberContact(targetPos);
        ViewDirection = (targetPos + Vector3.up * 1.2f - PawnPosition()).normalized;

        // reaction delay before opening fire
        if (ReactionTimer > 0f)
        {
            ReactionTimer -= deltaTime;
            ShootPrimary = false;
            return;
        }

        // Target-switch distraction: a soldier under fire from two directions
        // does not calmly finish the target it started on. Human attention is
        // not exclusive, and never modelling that is one of the things that
        // makes AI read as scripted. Rolled once a second so it does not scale
        // with framerate.
        DistractionTimer -= deltaTime;
        if (DistractionTimer <= 0f)
        {
            DistractionTimer = 1f;
            if (Random.value < Aim.Profile.TargetSwitchChancePerSecond)
            {
                RetargetTimer = 0f;      // forces reacquisition next scan
            }
        }

        float dist = Vector3.Distance(PawnPosition(), targetPos);

        // What the scored chooser and the squad want from this soldier.
        //
        // Both are advisory: they bias how the fight is taken, not whether it
        // is. A Base element holds where it is and keeps firing so the enemy
        // stays looking at it; a Maneuver element breaks the direct approach
        // and comes from an angle. Without the split, four soldiers with the
        // same target all walk at it.
        BFSquadRole role = SquadRole;

        if (role == BFSquadRole.Base)
        {
            // Hold. Suppression is the job, so keep shooting even at ranges a
            // lone soldier would close.
            EngageHoldGround = true;
        }
        else if (role == BFSquadRole.Maneuver && dist > 12f)
        {
            // Break the straight line. Reusing FlankOffset keeps this on the
            // one path the movement code already understands rather than
            // adding a second notion of "where I am going".
            if (FlankOffset == Vector3.zero)
            {
                Vector3 toTarget = targetPos - PawnPosition();
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 1f)
                {
                    Vector3 side = Vector3.Cross(toTarget.normalized, Vector3.up);
                    FlankOffset = side * (Random.value < 0.5f ? -18f : 18f);
                }
            }
            EngageHoldGround = false;
        }
        else
        {
            EngageHoldGround = false;
        }

        // The chooser's verdict, applied where it changes how the fight goes.
        switch (Decision)
        {
            case BFAIAction.SeekCover:
                // Bias toward claiming cover; the hint-node scan below reads
                // this rather than rolling purely on skill.
                WantsCover = true;
                break;

            case BFAIAction.Retreat:
                // Self-preservation already owns actually disengaging - this
                // only stops us pressing forward while it decides.
                EngageHoldGround = true;
                WantsCover = true;
                break;

            case BFAIAction.Flank:
                if (role == BFSquadRole.None && dist > 12f && FlankOffset == Vector3.zero)
                {
                    Vector3 toTarget = targetPos - PawnPosition();
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude > 1f)
                    {
                        Vector3 side = Vector3.Cross(toTarget.normalized, Vector3.up);
                        FlankOffset = side * (Random.value < 0.5f ? -15f : 15f);
                    }
                }
                WantsCover = false;
                break;

            default:
                WantsCover = false;
                break;
        }

        // grenade / secondary weapon: mid-range targets, on cooldown, skill-gated
        if (GrenadePulse > 0f)
        {
            GrenadePulse -= deltaTime;
            ShootSecondary = true;
        }
        else if (GrenadeTimer <= 0f && dist > 8f && dist < 32f)
        {
            // Decide once a second, not per frame - the old per-frame roll
            // scaled with framerate.
            GrenadeDecisionTimer -= deltaTime;
            if (GrenadeDecisionTimer <= 0f)
            {
                GrenadeDecisionTimer = 1f;
                if (Random.value < Skill.StrafeAggression * 0.5f)
                {
                    GrenadeTimer = Random.Range(8f, 16f);
                    GrenadePulse = 0.15f;
                }
            }
        }

        // Swap off a weapon we can't fire before worrying about burst discipline
        // - an AI standing in the open dry-firing an empty rifle it will never
        // reload is worse than one that simply pulls its sidearm.
        TickWeaponSelection(dist);

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

        // Tactical positioning from the map's authored hint nodes: designers
        // marked where cover and firing lanes actually are, so prefer those
        // over the generic strafe. Skill gates how often AI bothers.
        HintScanTimer -= deltaTime;
        if (HintScanTimer <= 0f)
        {
            // Scanned faster when the chooser has actually asked for cover.
            // Three seconds is fine as an idle habit and far too slow as a
            // response to being shot at - it is most of the time a soldier
            // spends deciding to move and then not having moved.
            HintScanTimer = WantsCover ? 1.2f : 3f;

            // WantsCover is the scored chooser asking for cover; without it this
            // was a pure skill roll that ignored how the fight was actually going.
            if (ClaimedHint == null && (WantsCover || Random.value < Skill.CoverUsage))
            {
                // marksmen at range look for a snipe post, everyone else cover
                PhxHintType want = dist > 45f ? PhxHintType.Snipe : PhxHintType.Cover;
                PhxHintNode candidate = PhxHintNodes.FindNearest(
                    PawnPosition(), want, 35f, facingToward: targetPos);

                if (candidate != null && PhxHintNodes.TryOccupy(candidate, this, 20f))
                {
                    ClaimedHint = candidate;
                    CoverSpotValid = false;
                }
                else
                {
                    // Nothing authored within reach. Away from a designer's
                    // nodes the map is not empty of cover, only of labels for
                    // it, so look at the geometry instead. Authored first
                    // always: a node knows which side of the wall the fight is
                    // expected to come from, and the search below only knows
                    // where the shooting is coming from right now.
                    CoverSpotValid = BFAICover.TryFind(PawnPosition(), targetPos,
                                                       maxRange: 12f, out CoverSpot);
                }
            }
        }

        if (ClaimedHint != null)
        {
            float toHint = Vector3.Distance(PawnPosition(), ClaimedHint.Position);
            if (toHint > 2.5f)
            {
                // Move into the authored position, still facing the enemy.
                // Through the graph, not straight at it: a cover node is very
                // often on the far side of the wall it provides cover from.
                MoveTowards(ClaimedHint.Position, sprint: toHint > 15f);
                Crouch = false;
                return;
            }

            // In position: hold it and adopt the stance the level designer
            // authored on the node. This used to read a "Posture" string that
            // does not exist in the data, so it always fell back to "crouch at
            // any cover node" - now the node's own PrimaryStance decides.
            MoveDirection = Vector2.zero;
            Crouch = PhxHintNodes.StanceIsLow(ClaimedHint.PrimaryStance) ||
                     ClaimedHint.Type == PhxHintType.Cover;
            return;
        }

        // Unauthored cover, found from the geometry. Same shape as the hint
        // node above - move in, then hold and stay low - but the position came
        // from a search rather than from a designer, so it is re-checked as the
        // fight moves instead of being held until released.
        if (CoverSpotValid)
        {
            float toSpot = Vector3.Distance(PawnPosition(), CoverSpot);
            if (toSpot > 1.5f)
            {
                MoveTowards(CoverSpot, sprint: toSpot > 10f);
                Crouch = false;
                HoldingCoverSpot = false;
                return;
            }

            // Arrived. Crouched is the posture the spot was tested for - it was
            // chosen because a crouched chest is protected there, and standing
            // up in it protects nothing.
            MoveDirection = Vector2.zero;
            Crouch = true;
            HoldingCoverSpot = true;

            // Stop holding it once it stops being cover. The search ran against
            // where the threat was then; a target that has moved around the
            // side turns the spot back into open ground, and a soldier crouched
            // in the open behind nothing is the exact behaviour this set out to
            // remove.
            CoverRecheckTimer -= deltaTime;
            if (CoverRecheckTimer <= 0f)
            {
                CoverRecheckTimer = 1.5f;

                Vector3 chest = PawnPosition() + Vector3.up * 0.85f;
                Vector3 threatEye = targetPos + Vector3.up * 1.5f;
                bool stillProtected = Physics.Linecast(chest, threatEye,
                                                       PhxLayers.SoldierGround,
                                                       QueryTriggerInteraction.Ignore);
                if (!stillProtected)
                {
                    CoverSpotValid = false;
                    HoldingCoverSpot = false;
                }
            }
            return;
        }

        HoldingCoverSpot = false;

        // combat movement: strafe and use cover based on skill. All decisions
        // roll on the strafe timer, never per frame - a per-frame roll made
        // high-skill AI flicker between "strafe" and "stand" many times a
        // second, snapping the body 180 degrees on each toggle.
        StrafeTimer -= deltaTime;
        if (StrafeTimer <= 0f)
        {
            StrafeTimer = Random.Range(0.8f, 1.8f);
            StrafeDir = Random.value < 0.5f ? -1f : 1f;
            StrafeActive = Random.value < Skill.StrafeAggression;
            CombatCrouch = Random.value < Skill.CoverUsage;
        }
        Crouch = CombatCrouch;

        Vector2 move = Vector2.zero;
        if (StrafeActive)
        {
            move.x = StrafeDir;
            // keep a touch of forward intent so the soldier's strafe-invert
            // branch (which flips the body's facing) never engages mid-fight
            move.y = 0.1f;
        }
        // A base element does not close - holding the enemy's attention only
        // works from where it already has an angle. Everyone else advances as
        // before.
        if (dist > 25f && !EngageHoldGround) move.y = 1f;   // close in
        else if (dist < 8f) move.y = -0.7f; // back off
        MoveDirection = move;
    }

    bool StrafeActive;
    bool CombatCrouch;
    float GrenadeDecisionTimer;

    void TickCapture(float deltaTime)
    {
        ShootPrimary = false;

        // Capturing is the point: only break off for threats that are actually
        // on top of the post, not any enemy in detection range.
        if (TargetPawn != null && TargetPawn.GetInstance() != null &&
            Vector3.Distance(PawnPosition(), TargetPawn.GetInstance().transform.position) < 25f)
        {
            State = PhxAIState.Engage;
            return;
        }

        PhxCommandpost cp = SeekPost;
        if (cp == null || cp.Team == Team)
        {
            // captured (or lost the reference) - find the next objective
            SeekPost = null;
            AssignedObjective = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        // Hold still only while the CP's trigger region actually has us; until
        // then (or if we got knocked out of it) keep walking onto the post.
        if (CapturePost == cp)
        {
            MoveDirection = Vector2.zero;
        }
        else
        {
            // Through the graph. Interior posts are routinely reached by a
            // corridor rather than a straight line, and steering directly at
            // one put the whole squad against the outside of the building.
            MoveTowards(cp.transform.position, sprint: false);
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

        if (BoardTarget.HangarEntrance == null)
        {
            // Nowhere authored to land. Boarding is not possible on this ship,
            // and pretending otherwise is what the teleport used to do.
            BoardTarget = null;
            State = PhxAIState.SeekObjective;
            return;
        }

        if (!(Pawn is PhxSoldier soldier))
        {
            State = PhxAIState.SeekObjective;
            return;
        }

        Vector3 hangar = BoardTarget.HangarEntrance.position;

        // Inside, on foot: the run is over.
        if (!soldier.IsInVehicle &&
            Vector3.Distance(PawnPosition(), hangar) < HangarArrivalRadius)
        {
            ReleaseVehicle();
            Debug.Log($"[BF3Legacy] AI boarding party reached {BoardTarget.ShipName}");
            State = PhxAIState.Sabotage;
            return;
        }

        if (soldier.IsInVehicle)
        {
            TickBoardingFlight(deltaTime, soldier, hangar);
            return;
        }

        TickBoardingEmbark(deltaTime, hangar);
    }

    /// <summary>Radius of the hangar mouth that counts as "aboard".</summary>
    const float HangarArrivalRadius = 12f;

    /// <summary>How close the transport gets before the squad steps off.</summary>
    const float DisembarkRadius = 18f;

    /// <summary>How far a boarding party will walk to find a ride.</summary>
    const float TransportSearchRadius = 120f;

    /// <summary>
    /// Aboard a transport, flying to the target ship's hangar.
    /// </summary>
    /// <remarks>
    /// The pilot flies to the authored LAND position nearest the hangar rather
    /// than the hangar mouth itself - that is exactly what LAND hint nodes are
    /// for, and a hangar entrance transform is a doorway, not a place to put a
    /// gunship down. Passengers ride; only the pilot steers.
    /// </remarks>
    void TickBoardingFlight(float deltaTime, PhxSoldier soldier, Vector3 hangar)
    {
        PhxSeat seat = soldier.GetCurrentSeat();
        PhxVehicle vehicle = seat?.Owner as PhxVehicle;

        if (seat == null || vehicle == null || vehicle.IsDestroyed)
        {
            // Shot down or thrown out mid-run. Continue on foot rather than
            // giving up: the party may already be close.
            ReleaseVehicle();
            return;
        }

        if (VehicleOp == null || VehicleOp.Seat != seat)
        {
            VehicleOp = new PhxAIVehicleOperator(this, vehicle, seat);
        }

        Vector3 landingSpot = VehicleOp.ResolveLandingSpot(hangar);
        float toHangar = Vector3.Distance(vehicle.transform.position, hangar);

        if (toHangar < DisembarkRadius)
        {
            // Close enough: everyone off. The pilot leaves too - the objective
            // is inside the ship, not in the seat.
            ReleaseVehicle();
            MoveDirection = Vector2.zero;
            Enter = true;                 // the seat handles the dismount
            return;
        }

        VehicleOp.Tick(deltaTime, landingSpot, Team, Skill);
    }

    /// <summary>
    /// On foot, looking for a ride to the enemy ship.
    /// </summary>
    /// <remarks>
    /// A boarding party with no transport waits at the muster point instead of
    /// walking into space. That is a real outcome - a team with no flyers left
    /// cannot board - and it is the outcome the teleport hid.
    /// </remarks>
    void TickBoardingEmbark(float deltaTime, Vector3 hangar)
    {
        PhxVehicle transport = FindNearbyFreeVehicle(TransportSearchRadius, flyersOnly: true);
        if (transport != null)
        {
            float toTransport = Vector3.Distance(PawnPosition(), transport.transform.position);
            if (toTransport > 4f)
            {
                MoveTowards(transport.transform.position);
            }
            else
            {
                MoveDirection = Vector2.zero;
                Enter = true;             // PhxSoldier takes the nearest free seat
            }
            return;
        }

        BoardMusterTimer -= deltaTime;
        if (BoardMusterTimer > 0f)
        {
            MoveDirection = Vector2.zero;
            ViewDirection = (hangar - PawnPosition()).normalized;
            return;
        }

        // Waited out the muster with nothing to fly. Go do something useful and
        // let the director hand out the boarding job again when a transport
        // exists.
        BoardMusterTimer = 15f;
        BoardTarget = null;
        State = PhxAIState.SeekObjective;
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

    void ReleaseHint()
    {
        if (ClaimedHint != null)
        {
            PhxHintNodes.Release(ClaimedHint, this);
            ClaimedHint = null;
        }
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

    PhxVehicle FindNearbyFreeVehicle(float radius, bool flyersOnly = false)
    {
        int count = Physics.OverlapSphereNonAlloc(PawnPosition(), radius, OverlapCache);
        PhxVehicle best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < count; ++i)
        {
            PhxVehicle v = OverlapCache[i].GetComponentInParent<PhxVehicle>();
            if (v == null || !v.HasAvailableSeat()) continue;
            if (flyersOnly && !(v is PhxFlyer)) continue;
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

        // Landing/turn recovery suppresses movement on the soldier side while
        // we keep commanding it; counting those frozen frames as "stuck" made
        // every unstick jump feed the next one.
        if (soldier.IsMovementLocked)
        {
            StuckTimer = 0f;
            LastPosition = PawnPosition();
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

    // Public so squadmates can measure radio range to each other.
    public Vector3 PawnPosition()
    {
        return Pawn.GetInstance().transform.position;
    }

    /// <summary>
    /// Move toward a distant goal using the map's authored planning graph.
    /// Falls back to direct steering when the map has no graph (or no route),
    /// so behaviour degrades gracefully rather than stalling.
    /// </summary>
    void MoveTowards(Vector3 goal, bool sprint = true)
    {
        float goalDist = Vector3.Distance(PawnPosition(), goal);

        // Use the authored graph for anything but the last few metres.
        //
        // This used to require a 15m goal, which meant every approach inside
        // that radius reverted to straight-line steering - and 15m is exactly
        // the range at which a soldier is threading doorways, corners and
        // props rather than crossing open ground. That is where AI looked like
        // they didn't understand the map: they pathed beautifully across it,
        // then walked into the last wall between them and the objective. The
        // graph is loaded and cheap to query; use it until we are genuinely
        // on top of the goal.
        if (PhxNavGraph.Instance.IsLoaded && goalDist > NavGraphMinDistance)
        {
            NavRepathTimer -= Time.deltaTime;
            bool goalMoved = (goal - NavGoal).sqrMagnitude > 25f;

            // Re-path when we are demonstrably not getting anywhere, not only
            // when the goal moves. Stuck recovery nudges the body free, but
            // without this the AI resumed following the same blocked route
            // straight back into whatever stopped it.
            bool blocked = StuckTimer > NavRepathStuckTime;

            if (goalMoved || blocked || (NavPath.Count == 0 && NavRepathTimer <= 0f))
            {
                NavRepathTimer = 2f;   // don't re-path every frame on failure
                NavGoal = goal;
                NavIndex = 0;

                bool routed = PhxNavGraph.Instance.FindPath(PawnPosition(), goal, NavSize, NavPath,
                                                            NavCapabilities);
                ReportNavAttempt(routed);
            }

            // String-pulling. A* returns hub centres, and following them
            // literally makes a soldier dogleg to the middle of every node on
            // the way - the "walking the graph" look, worst in the open where
            // the detour is longest and least necessary. Skipping to the
            // furthest waypoint we can actually see straightens the route
            // without discarding it: anything not in line of sight is still
            // walked hub by hub.
            SmoothNavPath();

            if (NavIndex < NavPath.Count)
            {
                Vector3 waypoint = NavPath[NavIndex];
                Vector3 toWaypoint = waypoint - PawnPosition();
                toWaypoint.y = 0f;

                if (toWaypoint.magnitude < 4f)
                {
                    // reached this hub, advance
                    if (++NavIndex >= NavPath.Count)
                    {
                        NavPath.Clear();
                        // force an immediate repath next call - otherwise the
                        // stale NavGoal suppresses it for up to 2s and the AI
                        // direct-steers blindly in the meantime
                        NavGoal = Vector3.positiveInfinity;
                        NavRepathTimer = 0f;
                    }
                }
                else
                {
                    SteerDirect(waypoint, sprint);
                    return;
                }
            }
        }

        SteerDirect(goal, sprint);
    }

    // How often the planning graph actually produces a route.
    //
    // "AI walk into walls" has two completely different causes that look
    // identical from the outside: the graph isn't returning routes (so
    // everyone direct-steers), or it is and the routes are bad. Guessing
    // between them has cost several rounds, so measure it: one line every 15s
    // with the success rate, rather than a per-call log that would flood at
    // 128 units.
    static int NavAttempts;
    static int NavSuccesses;
    static float NavReportTime;


    // How far ahead string-pulling looks. Bounded because each candidate costs
    // a cast, and because skipping too far on a long path starts cutting
    // corners the graph included for a reason.
    const int MaxSmoothLookahead = 4;

    // Checked at walking pace, not per frame: the answer changes only as fast
    // as the soldier moves, and 128 units each casting several capsules every
    // frame is not affordable.
    float SmoothTimer;

    /// <summary>
    /// Advance <c>NavIndex</c> past any waypoints we have a clear walk to.
    /// </summary>
    void SmoothNavPath()
    {
        SmoothTimer -= Time.deltaTime;
        if (SmoothTimer > 0f) return;
        SmoothTimer = 0.25f;

        if (NavPath.Count == 0 || NavIndex >= NavPath.Count - 1) return;

        Vector3 self = PawnPosition();
        int limit = Mathf.Min(NavIndex + MaxSmoothLookahead, NavPath.Count - 1);

        // Walk back from the furthest candidate: the first reachable one is the
        // best one, which makes every earlier check unnecessary.
        for (int i = limit; i > NavIndex; --i)
        {
            if (HasClearWalk(self, NavPath[i]))
            {
                NavIndex = i;
                return;
            }
        }
    }

    /// <summary>
    /// Whether a soldier-sized body can travel between two points unobstructed.
    /// </summary>
    /// <remarks>
    /// A sphere cast rather than a ray, so the clearance test matches the body
    /// that has to fit through it - a ray finds a gap between two crates that
    /// the soldier then wedges itself in.
    ///
    /// SoldierGround for the same reason the whiskers use it: the default layers
    /// include ordnance-only and vehicle-only collision meshes a soldier walks
    /// straight through, and treating those as walls would make string-pulling
    /// refuse every shortcut that actually exists. Soldiers are excluded
    /// deliberately - a friendly on the line is a moving obstacle, not a reason
    /// to keep the dogleg.
    /// </remarks>
    bool HasClearWalk(Vector3 from, Vector3 to)
    {
        Vector3 a = from + Vector3.up * 0.9f;
        Vector3 b = to + Vector3.up * 0.9f;

        Vector3 delta = b - a;
        float dist = delta.magnitude;
        if (dist < 0.01f) return true;

        // Slightly under the soldier's own radius so a doorway exactly wide
        // enough still reads as passable.
        const float probeRadius = 0.35f;

        return !Physics.SphereCast(a, probeRadius, delta / dist, out _, dist,
                                   PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore);
    }

    static void ReportNavAttempt(bool routed)
    {
        NavAttempts++;
        if (routed) NavSuccesses++;

        if (Time.time < NavReportTime) return;
        NavReportTime = Time.time + 15f;

        if (NavAttempts == 0) return;
        Debug.Log($"[AI nav] {NavSuccesses}/{NavAttempts} path requests routed " +
                  $"({(100f * NavSuccesses / NavAttempts):F0}%). " +
                  $"Graph loaded: {PhxNavGraph.Instance.IsLoaded}. " +
                  "A low rate means AI are direct-steering because the planning " +
                  "graph gave them nothing, not because the routes are poor.");

        NavAttempts = 0;
        NavSuccesses = 0;
    }

    /// <summary>Direct steering with obstacle whiskers (fallback / final approach).</summary>
    void SteerDirect(Vector3 goal, bool sprint = true)
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
        // walking into them (cheap alternative to full navmesh pathing).
        // Other soldiers count as obstacles too - skipping them entirely (as
        // this used to) jammed whole squads into each other at spawn and
        // chokepoints until stuck recovery had everyone jumping in unison.
        // They only get a short whisker so distant friendlies don't deflect us.
        // Whiskers must test what a SOLDIER can actually walk into. Unmasked,
        // these used Unity's default raycast layers, which include the
        // ordnance-only and vehicle-only collision meshes BF2 ships (see
        // PhxLayers.SoldierGround) - surfaces a soldier passes straight
        // through. So AI swerved around phantom walls and, worse, both side
        // whiskers frequently reported "blocked" on geometry that wasn't
        // there, dropping them into the push-and-hope branch below right next
        // to a real wall.
        //
        // SoldierGround excludes the soldier layer itself, so friendlies get
        // their own short-range test rather than being conflated with terrain.
        int obstacleMask = PhxLayers.SoldierGround | PhxLayers.Soldier;

        Vector3 eye = PawnPosition() + Vector3.up * 1.0f;
        if (Physics.Raycast(eye, dir, out RaycastHit hit, 4f, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            bool blockerIsSoldier = hit.collider.GetComponentInParent<PhxSoldier>() != null;
            if (!blockerIsSoldier || hit.distance < 2f)
            {
                Vector3 left = Quaternion.Euler(0f, -40f, 0f) * dir;
                Vector3 right = Quaternion.Euler(0f, 40f, 0f) * dir;
                bool leftClear = !Physics.Raycast(eye, left, 4f, obstacleMask, QueryTriggerInteraction.Ignore);
                bool rightClear = !Physics.Raycast(eye, right, 4f, obstacleMask, QueryTriggerInteraction.Ignore);

                // Wider probes for when both 40-degree whiskers are blocked -
                // a wall met at a shallow angle blocks both, and the old code
                // then just kept walking into it.
                if (!leftClear && !rightClear)
                {
                    Vector3 hardLeft = Quaternion.Euler(0f, -80f, 0f) * dir;
                    Vector3 hardRight = Quaternion.Euler(0f, 80f, 0f) * dir;
                    bool hardLeftClear = !Physics.Raycast(eye, hardLeft, 4f, obstacleMask, QueryTriggerInteraction.Ignore);
                    bool hardRightClear = !Physics.Raycast(eye, hardRight, 4f, obstacleMask, QueryTriggerInteraction.Ignore);

                    if (hardLeftClear || hardRightClear)
                    {
                        // Slide along the wall rather than grinding into it.
                        left = hardLeft;
                        right = hardRight;
                        leftClear = hardLeftClear;
                        rightClear = hardRightClear;
                    }
                }

                if (leftClear && !rightClear) dir = left;
                else if (rightClear && !leftClear) dir = right;
                else if (leftClear && rightClear)
                {
                    if (blockerIsSoldier)
                    {
                        // step around the blocker, away from its side of us
                        Vector3 toBlocker = hit.point - PawnPosition();
                        float side = Vector3.Dot(toBlocker, Vector3.Cross(Vector3.up, dir));
                        dir = side > 0f ? left : right;
                    }
                    else
                    {
                        dir = Random.value < 0.5f ? left : right;
                    }
                }
                // neither clear: keep pushing, stuck recovery will kick in
            }
        }

        ViewDirection = dir;
        MoveDirection = new Vector2(0f, 1f);

        // Stop asking once winded. The soldier would refuse anyway, but holding
        // the request down means the decision gets made in two places and the
        // AI spends its whole approach fighting its own stamina bar.
        bool winded = Pawn is PhxSoldier s && s.IsInit && s.IsWinded;
        Sprint = sprint && !winded && toGoal.magnitude > 40f;
    }

    // --- Perception memory (AI Stage 1) ---
    //
    // Where we last actually perceived an enemy, and when. Without this,
    // AcquireTarget's opening "TargetPawn = null" meant one frame of broken
    // line of sight erased the enemy completely: step behind a crate and the
    // whole squad forgets you exist and walks back to its objective. Keeping a
    // decaying memory is what makes them push to where you *were*, which reads
    // as intelligence far more than any amount of aim tuning.
    public Vector3 LastKnownEnemyPosition { get; private set; }
    float LastContactTime = float.NegativeInfinity;
    bool HasContact;

    /// <summary>Seconds a lost contact stays worth chasing.</summary>
    float ContactMemoryDuration => Mathf.Max(3f, Skill.ReactionTime * 8f + 4f);

    /// <summary>A remembered contact we can still act on.</summary>
    public bool HasFreshContact => HasContact && Time.time - LastContactTime <= ContactMemoryDuration;

    void RememberContact(Vector3 position)
    {
        LastKnownEnemyPosition = position;
        LastContactTime = Time.time;
        HasContact = true;
    }

    /// <summary>
    /// Accept a contact called in by a squadmate.
    /// </summary>
    /// <remarks>
    /// Stage 2. Without this each soldier has to rediscover the enemy
    /// personally, so a squad walks past its own dying members one at a time.
    /// A radioed contact is deliberately weaker than a first-hand one: it does
    /// not overwrite a fresher sighting of our own.
    /// </remarks>
    public void ReceiveContactReport(Vector3 position, float reportedAt)
    {
        if (HasContact && LastContactTime >= reportedAt) return;

        LastKnownEnemyPosition = position;
        LastContactTime = reportedAt;
        HasContact = true;
    }

    /// <summary>Squadmates, assigned by the director. Never null.</summary>
    public readonly List<PhxBF3AIController> Squad = new List<PhxBF3AIController>();

    /// <summary>How far a contact call carries.</summary>
    const float RadioRange = 70f;

    void BroadcastContact(Vector3 position)
    {
        float now = Time.time;
        for (int i = 0; i < Squad.Count; ++i)
        {
            PhxBF3AIController mate = Squad[i];
            if (mate == null || ReferenceEquals(mate, this)) continue;
            if (mate.Pawn == null || mate.Pawn.GetInstance() == null) continue;

            if (Vector3.Distance(PawnPosition(), mate.PawnPosition()) <= RadioRange)
            {
                mate.ReceiveContactReport(position, now);
            }
        }
    }

    void AcquireTarget()
    {
        IPhxControlableInstance previous = TargetPawn;
        TargetPawn = null;

        // Masked to the soldier layer: unmasked this returned every collider in
        // range - terrain chunks, props, ordnance - and filled the buffer with
        // scenery before it reached the people we were looking for.
        int count = Physics.OverlapSphereNonAlloc(PawnPosition(), Skill.DetectionRange,
                                                  OverlapCache, PhxLayers.Soldier,
                                                  QueryTriggerInteraction.Ignore);
        float bestScore = float.MinValue;

        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.IsDead) continue;
            if (soldier.Team == Team || soldier.Team == 0) continue;
            if (ReferenceEquals(soldier, Pawn)) continue;

            if (!HasLineOfSight(soldier.transform.position + Vector3.up * 1.2f)) continue;

            float score = ScoreThreat(soldier);
            if (score > bestScore)
            {
                bestScore = score;
                TargetPawn = soldier;
            }
        }

        if (TargetPawn != null)
        {
            Vector3 seenAt = TargetPawn.GetInstance().transform.position;
            RememberContact(seenAt);

            // Call it in. One squadmate seeing you should bring the squad, not
            // just the one who happened to have line of sight.
            BroadcastContact(seenAt);

            if (State == PhxAIState.SeekObjective)
            {
                ReactionTimer = Skill.ReactionTime;
            }

            // One roll per NEW target: does this AI let the sighting distract
            // it from the objective? Skill.ObjectiveFocus was authored per
            // difficulty tier but never consulted before. Re-rolling while
            // already fighting the same enemy made them flip-flop mid-fight.
            if (!ReferenceEquals(previous, TargetPawn))
            {
                EngageCommitted = Random.value > Skill.ObjectiveFocus;
            }
            return;
        }

        // Nothing visible. Before falling back to the objective, listen: a
        // shot from behind cover is a contact even with no line of sight.
        if (PhxAIPerception.TryGetLoudest(PawnPosition(), Team, out PhxAIPerception.Noise noise))
        {
            RememberContact(noise.Position);
        }
    }

    // Cycling a weapon takes one tick (the pawn consumes the request), so rate
    // limit it - otherwise a soldier with two empty guns flips between them
    // every frame instead of shooting.
    float WeaponSwitchTimer;

    /// <summary>
    /// Keep a usable weapon in hand.
    /// </summary>
    /// <remarks>
    /// The AI previously only ever fired whatever weapon happened to be
    /// selected at spawn, so a class whose first slot ran dry stopped being a
    /// threat for the rest of its life. Cycling the primary channel when the
    /// current weapon is empty is what "use any weapons they have" actually
    /// requires - the soldier already owns the slots, nothing was choosing
    /// between them.
    /// </remarks>
    void TickWeaponSelection(float distanceToTarget)
    {
        WeaponSwitchTimer -= Time.deltaTime;
        if (WeaponSwitchTimer > 0f) return;

        IPhxWeapon primary = Pawn.GetPrimaryWeapon();

        // Current weapon has nothing left anywhere: try the next.
        bool unusable = primary == null ||
                        (primary.GetTotalAmmo() <= 0 && primary.GetMagazineAmmo() <= 0);
        if (!unusable) return;

        // Cycling only helps if some OTHER slot holds a weapon we could fire.
        // Several stock classes reference award/dispenser weapons that aren't in
        // the loaded side lvls, leaving null slots - without this check such a
        // soldier requests a weapon change forever and never does anything else.
        if (!(Pawn is PhxSoldier soldier) || !HasAnotherUsableWeapon(soldier))
        {
            ReportWeaponless();
            WeaponSwitchTimer = 5f;   // stop asking; nothing is going to change
            return;
        }

        NextPrimaryWeapon = true;
        WeaponSwitchTimer = 0.5f;
    }

    static bool HasAnotherUsableWeapon(PhxSoldier soldier)
    {
        int equipped = soldier.GetEquippedWeaponIdx(0);
        int count = soldier.GetWeaponCount(0);

        for (int i = 0; i < count; ++i)
        {
            if (i == equipped) continue;

            IPhxWeapon w = soldier.GetWeapon(0, i);
            if (w != null && (w.GetTotalAmmo() > 0 || w.GetMagazineAmmo() > 0))
            {
                return true;
            }
        }
        return false;
    }

    // Once per class, not per soldier - a whole team of the same broken class
    // would otherwise bury the console.
    static readonly HashSet<string> ReportedWeaponless = new HashSet<string>();

    /// <summary>
    /// Forget which classes have already been reported. Called on map change:
    /// a warning suppressed on one map must not stay suppressed on the next.
    /// </summary>
    public static void ResetDiagnostics()
    {
        ReportedWeaponless.Clear();
    }

    void ReportWeaponless()
    {
        PhxInstance inst = Pawn?.GetInstance();
        PhxClass cl = inst != null ? inst.GetClassRef() : null;
        string className = cl != null ? cl.Name : (inst != null ? inst.name : "<unknown>");

        if (!ReportedWeaponless.Add(className)) return;

        Debug.LogWarning($"[AI] Class '{className}' has no usable weapon in any primary slot - " +
                         "these units cannot fight. Usually the weapon odfs live in a side lvl " +
                         "sub-file that never got mounted (see the 'Cannot find weapon class' warnings).");
    }

    /// <summary>
    /// How dangerous a candidate is to us right now. Higher wins.
    /// </summary>
    /// <remarks>
    /// Previously this was simply "nearest visible", which makes AI walk past
    /// the soldier actively shooting them to engage someone marginally closer.
    /// Proximity still dominates - it is the best single predictor of threat -
    /// but someone firing at us outranks a slightly nearer bystander.
    /// </remarks>
    float ScoreThreat(PhxSoldier candidate)
    {
        Vector3 toUs = PawnPosition() - candidate.transform.position;
        float dist = toUs.magnitude;

        // Falls off with distance rather than a hard nearest-wins comparison.
        float score = Skill.DetectionRange - dist;

        PhxPawnController controller = candidate.GetController();
        if (controller != null && controller.ShootPrimary)
        {
            score += 15f;

            // Actually pointed at us, not just firing somewhere.
            if (dist > 0.01f && Vector3.Dot(controller.ViewDirection.normalized, toUs.normalized) > 0.9f)
            {
                score += 25f;
            }
        }

        return score;
    }

    bool EngageCommitted;

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
