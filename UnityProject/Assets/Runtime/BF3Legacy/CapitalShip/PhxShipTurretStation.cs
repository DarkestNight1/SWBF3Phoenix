using UnityEngine;

/// <summary>
/// Manned anti-fighter turret console in the capital ship hangar (the BF2 /
/// Elite Squadron hangar turrets), driving an external gun on the hull.
///
/// Two ways to man it:
///  - PLAYER: walk up to the console and press E. The camera possesses the
///    external gun (PhxCamera.Track), mouse aims, left mouse fires, E exits
///    back to the soldier. A minimal on-screen prompt/reticle is drawn while
///    in range / possessed.
///  - AI / other soldiers: any friendly soldier standing at the console mans
///    the gun automatically and it engages enemy flyers on its own.
/// </summary>
public class PhxShipTurretStation : MonoBehaviour, IPhxTrackable
{
    public PhxCapitalShip Ship;
    public Vector3 ExternalGunLocalPos;
    public float UseRadius = 3f;
    public float GunRange = 250f;
    public float GunDamagePerSecond = 60f;
    public float GunShotDamage = 45f;
    public float GunShotDelay = 0.18f;

    public PhxSoldier Operator { get; private set; }
    public bool PlayerPossessed { get; private set; }

    GameObject ExternalGun;
    LineRenderer Tracer;
    Component Target;             // PhxSoldier or PhxVehicle
    float RetargetTimer;

    // player possession state
    IPhxControlableInstance PossessedPawn;
    PhxPawnController PossessedController;
    float GunYaw, GunPitch;
    float ShotTimer;
    bool PlayerInRange;

    static readonly Collider[] OverlapCache = new Collider[64];


    void Update()
    {
        if (Ship == null) return;

        bool shipAlive = Ship.State != PhxCapitalShip.PhxShipState.Dying &&
                         Ship.State != PhxCapitalShip.PhxShipState.Destroyed;

        UpdatePlayerPossession(shipAlive);

        if (PlayerPossessed)
        {
            TickPlayerControl();
            return;
        }

        UpdateOperator();

        bool active = Operator != null && shipAlive;
        if (!active)
        {
            if (Tracer != null) Tracer.enabled = false;
            return;
        }

        EnsureGun();
        TickAutoControl();
    }

    // ------------------------------------------------------ player possession

    void UpdatePlayerPossession(bool shipAlive)
    {
        PhxMatch match = PhxGame.GetMatch();
        PhxCamera cam = PhxGame.GetCamera();
        if (match == null || match.Player == null || cam == null) return;

        if (PlayerPossessed)
        {
            // exit: E pressed, soldier died, or the ship is going down
            bool wantExit = Input.GetKeyDown(KeyCode.E);
            bool mustExit = !shipAlive ||
                            (PossessedPawn is PhxSoldier s && s.IsDead);
            if (wantExit || mustExit)
            {
                PlayerPossessed = false;
                if (PossessedPawn != null && PossessedController != null)
                {
                    PossessedPawn.Assign(PossessedController);
                    cam.Follow(PossessedPawn);
                }
                PossessedPawn = null;
                PossessedController = null;
                if (Tracer != null) Tracer.enabled = false;
            }
            return;
        }

        // can the player grab this console?
        IPhxControlableInstance playerPawn = match.Player.Pawn;
        PlayerInRange = false;
        if (!shipAlive || playerPawn == null) return;
        if (!(playerPawn is PhxSoldier soldier) || soldier.IsDead) return;
        if (soldier.Team != Ship.Team) return;
        if ((soldier.transform.position - transform.position).magnitude > UseRadius) return;

        PlayerInRange = true;
        if (Input.GetKeyDown(KeyCode.E))
        {
            EnsureGun();
            PossessedPawn = playerPawn;
            PossessedController = playerPawn.GetController();

            // E is also the soldier's vehicle enter/exit key
            // (PhxPlayerController) - consume it so manning a turret next to
            // a vehicle doesn't also try to board that vehicle this frame.
            if (PossessedController != null)
            {
                PossessedController.Enter = false;
            }
            playerPawn.UnAssign();

            // start aiming outward from the hull
            Vector3 outward = ExternalGun.transform.position - Ship.transform.position;
            outward.y = 0f;
            GunYaw = Quaternion.LookRotation(outward.normalized).eulerAngles.y;
            GunPitch = 0f;

            PlayerPossessed = true;
            PhxGame.GetCamera().Track(this);
        }
    }

    void TickPlayerControl()
    {
        GunYaw += Input.GetAxis("Mouse X") * 2.5f;
        GunPitch = Mathf.Clamp(GunPitch - Input.GetAxis("Mouse Y") * 2.5f, -45f, 60f);

        ShotTimer -= Time.deltaTime;
        if (Input.GetMouseButton(0) && ShotTimer <= 0f)
        {
            ShotTimer = GunShotDelay;
            FirePlayerShot();
        }
        else if (Tracer != null && ShotTimer <= -0.1f)
        {
            Tracer.enabled = false;
        }
    }

    void FirePlayerShot()
    {
        Vector3 origin = ExternalGun.transform.position;
        Vector3 dir = GetAimRotation() * Vector3.forward;

        Vector3 hitPoint = origin + dir * GunRange;
        if (Physics.Raycast(origin + dir * 6f, dir, out RaycastHit hit, GunRange))
        {
            hitPoint = hit.point;

            PhxSoldier soldier = hit.collider.GetComponentInParent<PhxSoldier>();
            if (soldier != null)
            {
                // credit whoever is manning the turret
                soldier.AddDamageFrom(GunShotDamage, hit.point, isSaber: false,
                                      instigator: Operator?.GetController());
            }
            else
            {
                IPhxDamageableInstance damageable = hit.collider.GetComponentInParent<IPhxDamageableInstance>();
                damageable?.AddDamage(GunShotDamage);
            }
        }

        Tracer.enabled = true;
        Tracer.SetPosition(0, origin);
        Tracer.SetPosition(1, hitPoint);
    }

    Quaternion GetAimRotation()
    {
        return Quaternion.Euler(GunPitch, GunYaw, 0f);
    }

    // IPhxTrackable - camera rides just behind/above the gun
    public Vector3 GetCameraPosition()
    {
        Quaternion rot = GetAimRotation();
        return ExternalGun.transform.position - rot * Vector3.forward * 6f + Vector3.up * 2.5f;
    }

    public Quaternion GetCameraRotation()
    {
        return GetAimRotation();
    }

    // --------------------------------------------------------------- UI hint

    void OnGUI()
    {
        if (PlayerPossessed)
        {
            // minimal reticle + exit hint
            Rect mid = new Rect(Screen.width / 2f - 4f, Screen.height / 2f - 4f, 8f, 8f);
            GUI.Label(mid, "+");
            GUI.Label(new Rect(20f, Screen.height - 40f, 400f, 30f),
                "Hangar turret - [Mouse] aim, [LMB] fire, [E] exit");
        }
        else if (PlayerInRange)
        {
            GUI.Label(new Rect(Screen.width / 2f - 100f, Screen.height * 0.6f, 260f, 30f),
                "[E] Man hangar defense turret");
        }
    }

    // ------------------------------------------------------ AI / auto control

    float OperatorScanTimer;

    void UpdateOperator()
    {
        // occupied while a living friendly soldier stands at the console
        if (Operator != null &&
            (Operator.IsDead || (Operator.transform.position - transform.position).magnitude > UseRadius))
        {
            Operator = null;
        }
        if (Operator != null) return;

        // finding a new operator is a physics query - don't run it every
        // frame on every station in the level
        OperatorScanTimer -= Time.deltaTime;
        if (OperatorScanTimer > 0f) return;
        OperatorScanTimer = 0.5f;

        int count = Physics.OverlapSphereNonAlloc(transform.position, UseRadius, OverlapCache);
        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier != null && !soldier.IsDead && soldier.Team == Ship.Team)
            {
                Operator = soldier;
                return;
            }
        }
    }

    void TickAutoControl()
    {
        RetargetTimer -= Time.deltaTime;
        if (RetargetTimer <= 0f)
        {
            RetargetTimer = 0.5f;
            AcquireTarget();
        }

        if (Target == null)
        {
            Tracer.enabled = false;
            return;
        }

        Vector3 aim = Target.transform.position;
        Tracer.enabled = true;
        Tracer.SetPosition(0, ExternalGun.transform.position);
        Tracer.SetPosition(1, aim);

        float dmg = GunDamagePerSecond * Time.deltaTime;
        if (Target is PhxSoldier soldier)
        {
            soldier.AddDamageFrom(dmg, ExternalGun.transform.position, isSaber: false,
                                  instigator: Operator?.GetController());
        }
        else if (Target is IPhxDamageableInstance dmgable)
        {
            dmgable.AddDamage(dmg);
        }
    }

    void EnsureGun()
    {
        if (ExternalGun != null) return;

        ExternalGun = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ExternalGun.name = "HangarDefenseGun";
        ExternalGun.transform.SetParent(Ship.transform, false);
        ExternalGun.transform.localPosition = ExternalGunLocalPos;
        ExternalGun.transform.localScale = new Vector3(2f, 3f, 2f);
        PhxRuntimeAssets.Tint(ExternalGun, new Color(0.6f, 0.62f, 0.68f));

        Tracer = ExternalGun.AddComponent<LineRenderer>();
        Tracer.startWidth = 0.25f;
        Tracer.endWidth = 0.25f;
        Tracer.material = PhxRuntimeAssets.CreateLineMaterial(new Color(0.4f, 1f, 0.4f));
        Tracer.startColor = new Color(0.4f, 1f, 0.4f);
        Tracer.endColor = new Color(0.4f, 1f, 0.4f, 0.2f);
        Tracer.enabled = false;
    }

    void AcquireTarget()
    {
        Target = null;
        Vector3 origin = ExternalGun.transform.position;
        int count = Physics.OverlapSphereNonAlloc(origin, GunRange, OverlapCache);
        float best = float.MaxValue;

        for (int i = 0; i < count; ++i)
        {
            // prefer enemy flyers, fall back to enemy soldiers outside the ship
            PhxVehicle vehicle = OverlapCache[i].GetComponentInParent<PhxVehicle>();
            if (vehicle != null && vehicle.Team != Ship.Team && vehicle.Team != 0)
            {
                float d = (vehicle.transform.position - origin).sqrMagnitude;
                if (d < best) { best = d; Target = vehicle; }
                continue;
            }
            if (Target == null)
            {
                PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
                if (soldier != null && !soldier.IsDead && soldier.Team != Ship.Team && soldier.Team != 0)
                {
                    Target = soldier;
                }
            }
        }
    }
}
