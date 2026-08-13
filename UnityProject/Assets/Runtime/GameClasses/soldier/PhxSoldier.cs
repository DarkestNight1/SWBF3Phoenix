using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Animations;
using LibSWBF2.Utils;
using LibSWBF2.Wrappers;
using System.Runtime.ExceptionServices;

public class PhxSoldier : PhxControlableInstance<PhxSoldier.ClassProperties>, ICraAnimated, IPhxTickable, IPhxTickablePhysics, IPhxDamageableInstance
{
    static PhxGame GAME => PhxGame.Instance;
    static PhxMatch MTC => PhxGame.GetMatch();
    static PhxScene SCENE => PhxGame.GetScene();
    static PhxCamera CAM => PhxGame.GetCamera();


    public class ClassProperties : PhxClass
    {
        public PhxProp<Texture2D> MapTexture = new PhxProp<Texture2D>(null);
        public PhxProp<float> MapScale = new PhxProp<float>(1.0f);
        public PhxProp<float> MapViewMin = new PhxProp<float>(1.0f);
        public PhxProp<float> MapViewMax = new PhxProp<float>(1.0f);
        public PhxProp<float> MapSpeedMin = new PhxProp<float>(1.0f);
        public PhxProp<float> MapSpeedMax = new PhxProp<float>(1.0f);

        public PhxProp<string> HealthType = new PhxProp<string>("person");
        public PhxProp<float>  MaxHealth = new PhxProp<float>(100.0f);

        // Default animation for soldier classes seems to be hardcoded to "human".
        // For example, there's no "AnimationName" anywhere in the odf hierarchy:
        //   rep_inf_ep3_rifleman -> rep_inf_default_rifleman -> rep_inf_default -> com_inf_default
        public PhxProp<string> AnimationName = new PhxProp<string>("human");
        public PhxProp<string> SkeletonName = new PhxProp<string>("human");

        public PhxProp<float> MaxSpeed = new PhxProp<float>(1.0f);
        public PhxProp<float> MaxStrafeSpeed = new PhxProp<float>(1.0f);
        public PhxProp<float> MaxTurnSpeed = new PhxProp<float>(1.0f);
        public PhxProp<float> JumpHeight = new PhxProp<float>(1.0f);
        public PhxProp<float> JumpForwardSpeedFactor = new PhxProp<float>(1.0f);
        public PhxProp<float> JumpStrafeSpeedFactor = new PhxProp<float>(1.0f);
        public PhxProp<float> RollSpeedFactor = new PhxProp<float>(1.0f);
        public PhxProp<float> Acceleration = new PhxProp<float>(1.0f);
        public PhxProp<float> SprintAccelerateTime = new PhxProp<float>(1.0f);

        public PhxMultiProp ControlSpeed = new PhxMultiProp(typeof(string), typeof(float), typeof(float), typeof(float));

        public PhxProp<float> EnergyBar = new PhxProp<float>(1.0f);
        public PhxProp<float> EnergyRestore = new PhxProp<float>(1.0f);
        public PhxProp<float> EnergyRestoreIdle = new PhxProp<float>(1.0f);
        public PhxProp<float> EnergyDrainSprint = new PhxProp<float>(1.0f);
        public PhxProp<float> EnergyMinSprint = new PhxProp<float>(1.0f);
        public PhxProp<float> EnergyCostJump = new PhxProp<float>(0.0f);
        public PhxProp<float> EnergyCostRoll = new PhxProp<float>(1.0f);

        public PhxProp<float> AimValue = new PhxProp<float>(1.0f);
        public PhxProp<float> AimFactorPostureSpecial = new PhxProp<float>(1.0f);
        public PhxProp<float> AimFactorPostureStand = new PhxProp<float>(1.0f);
        public PhxProp<float> AimFactorPostureCrouch = new PhxProp<float>(1.0f);
        public PhxProp<float> AimFactorPostureProne = new PhxProp<float>(1.0f);
        public PhxProp<float> AimFactorStrafe = new PhxProp<float>(0.0f);
        public PhxProp<float> AimFactorMove = new PhxProp<float>(1.0f);

        public PhxPropertySection Weapons = new PhxPropertySection(
            "WEAPONSECTION",
            ("WeaponName",    new PhxProp<string>(null)),
            ("WeaponAmmo",    new PhxProp<int>(0)),
            ("WeaponChannel", new PhxProp<int>(0))
        );

        public PhxProp<string> AISizeType = new PhxProp<string>("SOLDIER");
    }

    // Original SWBF2 Control States, see: com_inf_default.odf
    enum PhxControlState
    {
        Stand,
        Crouch,
        Prone,
        Sprint,
        Jet,
        Jump,
        Roll,
        Tumble,
    }

    enum PhxSoldierContext
    {
        Free,
        Pilot,
    }

    PhxSoldierContext Context = PhxSoldierContext.Free;


    // Vehicle related fields
    PhxSeat CurrentSeat;

    PhxPoser Poser;




    public PhxProp<float> CurHealth = new PhxProp<float>(100.0f);

    /// <summary>
    /// Sprint stamina, in the same units as the class's EnergyBar. The odf
    /// properties for this (EnergyBar, EnergyDrainSprint, EnergyRestore,
    /// EnergyRestoreIdle, EnergyMinSprint, EnergyCostJump, EnergyCostRoll)
    /// have always been parsed, but nothing consumed them - sprint was free
    /// and unlimited, and the HUD had no bar to draw.
    /// </summary>
    public float CurEnergy { get; private set; } = 1f;

    /// <summary>Full stamina for this class; 0 when the class declares none.</summary>
    public float MaxEnergy => C != null ? C.EnergyBar.Get() : 0f;

    /// <summary>Stamina as a 0..1 fraction, for the HUD. 1 when the class has no energy bar.</summary>
    public float EnergyFraction => MaxEnergy > 0f ? Mathf.Clamp01(CurEnergy / MaxEnergy) : 1f;

    /// <summary>Health as a 0..1 fraction, for the HUD.</summary>
    public float HealthFraction
    {
        get
        {
            float max = C != null ? C.MaxHealth.Get() : 0f;
            return max > 0f ? Mathf.Clamp01(CurHealth.Get() / max) : 0f;
        }
    }

    PhxHumanAnimator Animator;
    Rigidbody Body;

    // Important skeleton bones
    Transform HpWeapons;
    Transform Spine;
    Transform Neck;

    PhxControlState State;
    PhxControlState PrevState;

    // Physical raycast downwards
    bool Grounded;
    bool PrevGrounded;

    // How long to still be alerted after the last fire / hit
    const float AlertTime = 3f;
    float AlertTimer;

    // Count time while jumping/falling
    float FallTimer;

    // Minimum time we're considered falling when jumping
    const float JumpTime = 0.2f;
    float JumpTimer;

    // When > 0, we're currently landing
    float LandTimer;

    // Time it takes to turn left/right when idle (not walking)
    const float TurnTime = 0.2f;
    float TurnTimer;
    Quaternion TurnStart;

    Vector3 CurrSpeed;
    Quaternion LookRot;

    bool IsFixated => Body == null;


    bool bHasLookaroundIdleAnim = false;
    bool bHasCheckweaponIdleAnim = false;
    bool LastIdle = false;
    const float IdleTime = 10f;

    // <stance>, <thrustfactor> <strafefactor> <turnfactor>
    float[][] ControlValues;

    // First array index is whether:
    // - 0 : Primary Weapon
    // - 1 : Secondary Weapon
    IPhxWeapon[][] Weapons = new IPhxWeapon[2][];

    /// <summary>
    /// Classes this unit carries that are placed rather than fired - mines,
    /// beacons, deployable terminals. Named in a WEAPONSECTION like a weapon,
    /// but they become objects in the world, not something held.
    /// </summary>
    public readonly List<PhxClass> Deployables = new List<PhxClass>();

    /// <summary>The first deployable whose root base class matches, or null.</summary>
    public PhxClass GetDeployable(string rootBaseClassName)
    {
        for (int i = 0; i < Deployables.Count; ++i)
        {
            EntityClass root = ClassLoader.GetRootClass(Deployables[i].EntityClass);
            if (root != null && string.Equals(root.BaseClassName, rootBaseClassName,
                                              StringComparison.OrdinalIgnoreCase))
            {
                return Deployables[i];
            }
        }
        return null;
    }
    int[] WeaponIdx = new int[2] { -1, -1 };


    public override void Init()
    {
        gameObject.layer = LayerMask.NameToLayer("SoldierAll");

        // Start at full health and stamina. CurHealth is an instance property
        // that stock odfs never set, so it kept its 100 default while MaxHealth
        // came from the class - a 200-health unit would have read as half dead
        // on the health bar from the moment it spawned.
        CurHealth.Set(C.MaxHealth.Get());
        CurEnergy = MaxEnergy;

        ViewConstraint.x = 45f;

        // TODO: base turn speed in degreees/sec really 45?
        MaxTurnSpeed.y = 45f * C.MaxTurnSpeed;

        //CurrDir = transform.rotation;
        //TargetDir = CurrDir;

        PhxControlState[] states = (PhxControlState[])Enum.GetValues(typeof(PhxControlState));
        ControlValues = new float[states.Length][];
        for (int i = 0; i < states.Length; ++i)
        {
            ControlValues[i] = GetControlSpeed(states[i]);
        }

        HpWeapons = UnityUtils.FindChildTransform(transform, "hp_weapons"); //transform.Find("dummyroot/bone_root/bone_a_spine/bone_b_spine/bone_ribcage/bone_r_clavicle/bone_r_upperarm/bone_r_forearm/bone_r_hand/hp_weapons");
        Neck = UnityUtils.FindChildTransform(transform, "bone_neck"); //transform.Find("dummyroot/bone_root/bone_a_spine/bone_b_spine/bone_ribcage/bone_neck");
        Spine = transform.Find("dummyroot/bone_root/bone_a_spine");
        if (Spine == null)
        {
            Spine = UnityUtils.FindChildTransform(transform, "bone_b_spine");
        }
        Debug.Assert(HpWeapons != null);
        Debug.Assert(Neck != null);
        Debug.Assert(Spine != null);

        ConfigureBody();

        CapsuleCollider coll = gameObject.AddComponent<CapsuleCollider>();
        coll.height = 1.9f;
        coll.radius = 0.4f;
        coll.center = new Vector3(0f, 0.9f, 0f);

        // Grounded defaults to false and is only otherwise set by
        // UpdatePhysics's own CheckSphere, which hasn't run yet - so every
        // spawn, even standing on solid authored ground, saw UpdateState's
        // very first tick read "not grounded" and force a one-frame flash
        // into the falling animation before physics caught up and corrected
        // it. transform.position is already the final spawn placement by the
        // time Init runs (PhxScene sets it before adding this component), so
        // seed Grounded here with the same check UpdatePhysics uses instead
        // of leaving it at the C# default.
        Grounded = Physics.CheckSphere(transform.position, 0.4f, PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore);


        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        // Weapons
        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        var weapons = new List<IPhxWeapon>[2]
        {
            new List<IPhxWeapon>(),
            new List<IPhxWeapon>()
        };

        HashSet<string> weaponAnimBanks = new HashSet<string>();

        foreach (Dictionary<string, IPhxPropRef> section in C.Weapons)
        {
            int channel = 0;
            if (section.TryGetValue("WeaponChannel", out IPhxPropRef chVal))
            {
                PhxProp<int> weapCh = (PhxProp<int>)chVal;
                channel = weapCh;
            }
            Debug.Assert(channel >= 0 && channel < 2);

            if (section.TryGetValue("WeaponName", out IPhxPropRef nameVal))
            {
                PhxProp<string> weapCh = (PhxProp<string>)nameVal;
                PhxClass weapClass = SCENE.GetClass(weapCh);
                if (weapClass != null)
                {
                    PhxProp<int> medalProp = weapClass.P.Get<PhxProp<int>>("MedalsTypeToUnlock");
                    if (medalProp != null && medalProp != 0)
                    {
                        // Skip medal/award weapons for now
                        continue;
                    }

                    // Only actually build it if the class is something that can
                    // be held and fired.
                    //
                    // A WEAPONSECTION can name a class whose runtime type is
                    // not a weapon at all - a deployable, most obviously a
                    // mine. Instantiating one of those here puts a live,
                    // armed object on the soldier's weapon hardpoint: the
                    // `as IPhxWeapon` yields null so nothing ever equips or
                    // frees it, and a mine in particular then sits in the
                    // carrier's hand looking for someone to blow up. Ask
                    // first, and let the deployable path own those classes.
                    Type weapType = PhxClassRegister.GetPhxInstanceType(
                        ClassLoader.GetRootClass(weapClass.EntityClass)?.BaseClassName);
                    if (weapType != null && !typeof(IPhxWeapon).IsAssignableFrom(weapType))
                    {
                        Deployables.Add(weapClass);
                        continue;
                    }

                    IPhxWeapon weap = SCENE.CreateInstance(weapClass, false, HpWeapons) as IPhxWeapon;
                    if (weap != null)
                    {
                        weap.SetIgnoredColliders(new List<Collider>() {gameObject.GetComponent<CapsuleCollider>()});

                        string weapAnimName = weap.GetAnimBankName();
                        if (!string.IsNullOrEmpty(weapAnimName))
                        {
                            weaponAnimBanks.Add(weapAnimName);
                        }

                        weapons[channel].Add(weap);

                        // init weapon as inactive
                        weap.GetInstance().gameObject.SetActive(false);
                        weap.OnShot(() => FireAnimation(channel == 0));
                        weap.OnReload(Reload);
                    }
                    else
                    {
                        Debug.LogWarning($"Instantiation of weapon class '{weapCh}' failed!");
                    }
                }
                else
                {
                    Debug.LogWarning($"Cannot find weapon class '{weapCh}'!");
                }
            }

            // TODO: weapon ammo
        }

        Weapons[0] = weapons[0].Count == 0 ? new IPhxWeapon[1] { null } : weapons[0].ToArray();
        Weapons[1] = weapons[1].Count == 0 ? new IPhxWeapon[1] { null } : weapons[1].ToArray();


        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        // Animation
        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        string[] weapAnimBanks = new string[weaponAnimBanks.Count];
        weaponAnimBanks.CopyTo(weapAnimBanks);
        // Pose the rig at the skeleton's authored rest pose before any clip
        // plays. Animations key only a subset of joints, and they are authored
        // against this pose rather than the one baked into the model's bone
        // hierarchy - the two disagree by over a unit at the pelvis and nearly
        // 180 degrees at the upper arms, so without this the rig is in two
        // spaces at once. Done here rather than per clip because Cra bakes
        // FrameCount x BoneCount into one fixed buffer.
        PhxAnimationLoader.ApplyBindPose(transform, C.SkeletonName.Get());

        Animator = new PhxHumanAnimator(transform, weapAnimBanks, C.SkeletonName.Get());


        // this needs to happen after the Animator is initialized, since swicthing
        // will weapons will most likely cause an animation bank change aswell
        NextWeapon(0);
        NextWeapon(1);

        PhxHeroLoadout.Apply(this, Weapons);

        // Presentation only: turns movement into surface interactions
        // (footprints, puffs, ripples). Costs nothing on a map whose surfaces
        // do not react.
        gameObject.AddComponent<BFFootstepEmitter>();
    }

    public override void Destroy()
    {
        
    }

    public override void Fixate()
    {
        Destroy(Body);
        Body = null;
        Grounded = true;

        Destroy(GetComponent<CapsuleCollider>());
    }

    public override IPhxWeapon GetPrimaryWeapon()
    {
        return GetEquippedWeapon(0);
    }

    /// <summary>
    /// The weapon currently selected on a channel, or null. Slots can be empty
    /// (missing weapon odf) and the index can still be -1 before the first
    /// NextWeapon call, so every read of Weapons[c][i] has to go through here.
    /// </summary>
    IPhxWeapon GetEquippedWeapon(int channel)
    {
        if (channel < 0 || channel >= Weapons.Length) return null;
        IPhxWeapon[] slots = Weapons[channel];
        if (slots == null) return null;
        int idx = WeaponIdx[channel];
        if (idx < 0 || idx >= slots.Length) return null;
        return slots[idx];
    }

    /// <summary>
    /// The secondary channel's weapon currently selected - grenades, detpacks,
    /// mines and the other "item" slots BF2 draws along the bottom of the HUD.
    /// </summary>
    public IPhxWeapon GetSecondaryWeapon()
    {
        return GetEquippedWeapon(1);
    }

    /// <summary>How many slots a channel has (0 = primary, 1 = secondary).</summary>
    public int GetWeaponCount(int channel)
    {
        if (channel < 0 || channel >= Weapons.Length) return 0;
        return Weapons[channel]?.Length ?? 0;
    }

    /// <summary>A specific slot on a channel, or null if it's empty.</summary>
    public IPhxWeapon GetWeapon(int channel, int idx)
    {
        if (channel < 0 || channel >= Weapons.Length) return null;
        IPhxWeapon[] slots = Weapons[channel];
        if (slots == null || idx < 0 || idx >= slots.Length) return null;
        return slots[idx];
    }

    /// <summary>Index of the selected slot on a channel, or -1 if none is.</summary>
    public int GetEquippedWeaponIdx(int channel)
    {
        if (channel < 0 || channel >= WeaponIdx.Length) return -1;
        return WeaponIdx[channel];
    }

    public bool IsDead { get; private set; }

    // True while landing/turn recovery suppresses movement input. AI stuck
    // detection must not count these frames as "trying to move but stuck".
    public bool IsMovementLocked => LandTimer > 0f || TurnTimer > 0f;

    // Whether we're currently piloting a vehicle seat (used by AI)
    public bool IsInVehicle => Context == PhxSoldierContext.Pilot;

    // The seat we currently occupy (null when on foot)
    public PhxSeat GetCurrentSeat() => Context == PhxSoldierContext.Pilot ? CurrentSeat : null;

    public void AddHealth(float amount)
    {
        if (IsDead)
        {
            return;
        }

        if (amount < 0)
        {
            // we got hit! alert!
            AlertTimer = AlertTime;
        }

        float health = CurHealth + amount;
        if (health <= 0f)
        {
            health = 0;
            CurHealth.Set(0f);
            Die(transform.position, false);
            return;
        }
        CurHealth.Set(Mathf.Min(health, C.MaxHealth));
    }

    // IPhxDamageableInstance - projectile impacts etc.
    public void AddDamage(float damage)
    {
        AddDamageFrom(damage, transform.position, false);
    }

    // Who last damaged us, for kill credit. BF2 credits the last attacker
    // rather than whoever did the most damage, so a single reference is all
    // the scoreboard needs.
    PhxPawnController LastAttacker;

    // Damage with hit context, so lethal saber hits can dismember (BF3 Legacy)
    public void AddDamageFrom(float damage, Vector3 hitPos, bool isSaber, PhxPawnController instigator = null)
    {
        if (IsDead)
        {
            return;
        }

        if (instigator != null)
        {
            LastAttacker = instigator;
        }

        // Scripts watch named objects for damage (scripted set pieces, escort
        // objectives). Keyed by instance name, lower case, as registered.
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectDamageName, name.ToLower(),
                                         SCENE?.GetInstanceIndex(this), damage);

        float health = CurHealth - damage;
        bool fatal = health <= 0f;

        ReportToPlayerHUD(instigator, hitPos, fatal);

        if (fatal)
        {
            CurHealth.Set(0f);
            Die(hitPos, isSaber);
        }
        else
        {
            AddHealth(-damage);

            // Flinch, on the upper body only, so movement and fire continue.
            Animator.PlayHitReaction(PhxHumanAnimator.ImpactDirFrom(transform, hitPos));
        }
    }

    /// <summary>
    /// Raise the local player's hit marker / damage indicator when this hit
    /// involves them, in either direction. Kept here because this is the one
    /// place that knows both ends of the exchange.
    /// </summary>
    void ReportToPlayerHUD(PhxPawnController instigator, Vector3 hitPos, bool fatal)
    {
        PhxPlayerController player = MTC?.Player;
        if (player == null) return;

        if (instigator != null && ReferenceEquals(instigator, player))
        {
            PhxHUDEvents.ReportDealtDamage(fatal);
        }

        // We're the one being hit. Prefer the attacker's own position for the
        // direction indicator - the impact point is on our own body and would
        // point nowhere useful.
        if (ReferenceEquals(Controller, player))
        {
            Vector3 from = hitPos;
            PhxInstance attacker = instigator?.Pawn?.GetInstance();
            if (attacker != null)
            {
                from = attacker.transform.position;
            }
            PhxHUDEvents.ReportDamageTaken(from);
        }
    }

    void Die(Vector3 hitPos, bool isSaber)
    {
        if (IsDead)
        {
            return;
        }
        IsDead = true;

        // A death spends one of the victim team's reinforcements, which is
        // conquest's actual losing condition - nothing was decrementing them
        // before, so a match could never be lost. The instigator now comes
        // from the ordnance's owning weapon, so the kill is credited to the
        // individual who fired rather than guessed from the team.
        MTC?.ReportKill(LastAttacker, Controller, Team);

        // Mission scripts hook these to drive mode logic (hunt counters,
        // scripted events, campaign objectives). Instance indices are the
        // "character" handles scripts pass around, same convention as
        // OnFinishCapture. Fired before UnAssign, while Team is still ours.
        int? victimIdx = SCENE?.GetInstanceIndex(this);
        PhxInstance killerInst = LastAttacker?.Pawn?.GetInstance();
        int? killerIdx = killerInst != null ? SCENE?.GetInstanceIndex(killerInst) : null;
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnCharacterDeath, victimIdx, killerIdx);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnCharacterDeathTeam, Team.Get(), victimIdx, killerIdx);

        if (isSaber)
        {
            PhxDismemberment.TrySever(gameObject, hitPos);
        }

        // Whether the human player died has to be read before UnAssign clears
        // the link, and decides if we hand back to the spawn menu below.
        bool wasPlayer = Controller is PhxPlayerController;

        // Capture the killer before UnAssign clears LastAttacker's link.
        PhxInstance killerForCam = LastAttacker?.Pawn?.GetInstance();

        // Release the controller. Tick/TickPhysics both early-out on IsDead, so
        // the body is inert from here on regardless of what still references it.
        UnAssign();

        // Play the death animation that matches where the shot came from.
        // These are BF2's own clips (human_rifle_stand_death_*), so a soldier
        // shot in the back falls forward, as in the original.
        // Animator is a struct, so there is no null to test - PlayDeath guards
        // on its own state array, which covers a default-constructed animator.
        Animator.PlayDeath(PhxHumanAnimator.ImpactDirFrom(transform, hitPos));

        // The corpse keeps its collider and gravity for a moment so it settles
        // onto the ground as the animation plays out, rather than freezing
        // mid-stride. Freezing happens in RemoveCorpse once it has landed.
        StartCoroutine(RemoveCorpse());

        if (wasPlayer)
        {
            // Get the camera off the corpse. It was left in Follow on a pawn
            // that had just been unassigned, so the view froze on a dead
            // soldier's last aim direction - which reads as the game hanging
            // rather than as dying.
            CAM?.Death(transform, killerForCam != null ? killerForCam.transform : null);

            StartCoroutine(ReturnPlayerToSpawnMenu());
        }
    }

    System.Collections.IEnumerator RemoveCorpse()
    {
        // Let it fall for a beat, then stop simulating it so 30+ corpses don't
        // stay in the physics broadphase for the rest of the round.
        yield return new WaitForSeconds(2f);

        if (Body != null)
        {
            // Order matters: Unity warns "Kinematic body only supports
            // Speculative Continuous collision detection" if a body is switched
            // to kinematic while still set to a sweep-based mode.
            Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            Body.isKinematic = true;
        }
        CapsuleCollider coll = GetComponent<CapsuleCollider>();
        if (coll != null)
        {
            coll.enabled = false;
        }

        yield return new WaitForSeconds(13f);
        SCENE?.DestroyInstance(this);
    }

    /// <summary>
    /// BF2 shows the corpse briefly, then drops the player back to the class
    /// select screen. Nothing did this before: PhxMatch.KillPlayer was only
    /// ever reached from the pause menu, so dying in combat left the camera
    /// stuck on a body with no way to respawn.
    /// </summary>
    System.Collections.IEnumerator ReturnPlayerToSpawnMenu()
    {
        yield return new WaitForSeconds(DeathCamSeconds);
        MTC?.KillPlayer();
    }

    const float DeathCamSeconds = 2.5f;

    public void AddAmmo(float amount)
    {
        // TODO
    }

    public void NextWeapon(int channel)
    {
        Debug.Assert(channel >= 0 && channel < 2);

        if (WeaponIdx[channel] >= 0 && Weapons[channel][WeaponIdx[channel]] != null)
        {
            Weapons[channel][WeaponIdx[channel]].GetInstance().gameObject.SetActive(false);
        }
        if (++WeaponIdx[channel] >= Weapons[channel].Length)
        {
            WeaponIdx[channel] = 0;
        }
        if (Weapons[channel][WeaponIdx[channel]] != null)
        {
            Weapons[channel][WeaponIdx[channel]].GetInstance().gameObject.SetActive(true);
            Animator.SetAnimBank(Weapons[channel][WeaponIdx[channel]].GetAnimBankName());
        }
        else
        {
            Debug.LogWarning($"Encountered NULL weapon at channel {channel} and weapon index {WeaponIdx[channel]}!");
        }
    }

    // Shared rigidbody setup for spawn (Init) and vehicle exit (SetFree). The 80kg mass
    // matters: depenetration against other 80kg soldiers must not launch this body.
    void ConfigureBody()
    {
        Body = gameObject.AddComponent<Rigidbody>();
        Body.mass = 80f;
        Body.drag = 0.2f;
        Body.angularDrag = 10f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        Body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }


    // --- animation layer 0 arbitration --------------------------------------
    //
    // Locomotion writes layer 0 every frame from the movement state. Anything
    // else that wants the full body - an AI action, a scripted pose - has to be
    // able to hold it for a while without being overwritten on the next frame,
    // and has to give it back. Without an owner, two writers just fight: the
    // first version of the AI action layer set layer 0 directly and either lost
    // within a frame or won briefly and stuttered.
    //
    // The rule is deliberately simple. Locomotion yields while an override is
    // held, unless the soldier is doing something locomotion must win - falling,
    // landing, turning in place, dying - because those read as broken far more
    // obviously than a missed idle animation does.

    float OverrideAnimTimer;
    string OverrideAnimName;

    /// <summary>Is a full-body override currently holding layer 0?</summary>
    public bool HasAnimOverride => OverrideAnimTimer > 0f;

    /// <summary>
    /// Take layer 0 for <paramref name="holdSeconds"/>, playing a clip from an
    /// arbitrary bank.
    /// </summary>
    /// <remarks>
    /// Returns false when the clip is not in any of the soldier's banks, so the
    /// caller can fall back rather than assume it succeeded. Requests are also
    /// refused outright while locomotion owns the body, which keeps the "who
    /// wins" question in one place instead of at every call site.
    /// </remarks>
    public bool RequestAnimOverride(string bankName, string animName, float holdSeconds)
    {
        if (!CanYieldLocomotion()) return false;

        // Re-requesting the clip that is already playing just extends it;
        // restarting would hold it on frame zero for as long as the caller
        // keeps asking.
        if (HasAnimOverride && OverrideAnimName == animName)
        {
            OverrideAnimTimer = Mathf.Max(OverrideAnimTimer, holdSeconds);
            return true;
        }

        if (!Animator.PlayOverrideAnim(bankName, animName)) return false;

        OverrideAnimName = animName;
        OverrideAnimTimer = Mathf.Max(0.05f, holdSeconds);
        return true;
    }

    /// <summary>Hand layer 0 back to locomotion immediately.</summary>
    public void ReleaseAnimOverride()
    {
        OverrideAnimTimer = 0f;
        OverrideAnimName = null;
    }

    /// <summary>
    /// States where locomotion must keep the body regardless of what anyone
    /// else wants. Airborne and turn-in-place both drive position or facing, so
    /// replacing their animation desynchronises what the soldier looks like
    /// from what it is doing.
    /// </summary>
    bool CanYieldLocomotion()
    {
        if (!Grounded) return false;
        if (TurnTimer > 0f || LandTimer > 0f) return false;
        if (State == PhxControlState.Jump || State == PhxControlState.Roll ||
            State == PhxControlState.Tumble) return false;
        return true;
    }

    void TickAnimOverride(float deltaTime)
    {
        if (OverrideAnimTimer <= 0f) return;

        OverrideAnimTimer -= deltaTime;

        // Give the body back the moment locomotion needs it, rather than
        // waiting out the hold and animating a jump as a reload.
        if (!CanYieldLocomotion()) ReleaseAnimOverride();
    }

    public override void PlayIntroAnim()
    {
        Animator.PlayIntroAnim();
    }

    /// <summary>
    /// Play a clip from an arbitrary bank - the hook the combo state machine
    /// uses to put a saber move on screen. Returns false when the bank has no
    /// such clip, so the caller can leave locomotion running rather than
    /// stalling the hero on a move that never plays.
    /// </summary>
    public bool PlayOverrideAnim(string bankName, string animName)
    {
        return Animator.PlayOverrideAnim(bankName, animName);
    }

    /// <summary>
    /// The species half of a stock clip name - "human", "gam", "wok". Comes
    /// from the odf's AnimationName.
    /// </summary>
    public string GetAnimationBankPrefix()
    {
        return C != null ? C.AnimationName.Get() : "human";
    }

    /// <summary>
    /// The weapon-posture half of a stock clip name - "rifle", "pistol",
    /// "bazooka". Falls back to rifle, which every humanoid bank has.
    /// </summary>
    public string GetWeaponPosture()
    {
        IPhxWeapon weapon = GetPrimaryWeapon();
        string bank = weapon?.GetAnimBankName();
        return string.IsNullOrEmpty(bank) ? "rifle" : bank;
    }

    void FireAnimation(bool primary)
    {
        Animator.Anim.SetState(1, Animator.StandShootPrimary);
        Animator.Anim.RestartState(1);
    }

    void Reload()
    {
        IPhxWeapon weap = GetEquippedWeapon(0);
        if (weap != null)
        {
            Animator.Anim.SetState(1, Animator.StandReload);
            Animator.Anim.RestartState(1);
            float animTime = Animator.Anim.GetCurrentState(1).GetDuration();
            float reloadTime = weap.GetReloadTime();

            // Both divisions can go wrong, and both ways are reachable.
            //
            // A missing reload clip gives animTime 0, so reloadTime/0 is
            // infinity and 1/infinity is 0 - a playback speed of zero, which
            // freezes the reload animation permanently rather than skipping it.
            // A weapon with no declared reload time gives 1/0 the other way and
            // feeds infinity into Cra. Clips do go missing here; the console
            // reports them by name.
            if (animTime > 0.001f && reloadTime > 0.001f)
            {
                Animator.Anim.SetPlaybackSpeed(1, Animator.StandReload, animTime / reloadTime);
            }
        }
    }

    // Undoes SetPilot; called by vehicles/turrets when ejecting soldiers
    public void SetFree(Vector3 position)
    {
        Context = PhxSoldierContext.Free;
        CurrentSeat = null;

        ConfigureBody();

        GetComponent<SkinnedMeshRenderer>().enabled = true;
        GetComponent<CapsuleCollider>().enabled = true;

        transform.parent = null;
        transform.position = position;

        Poser = null;

        Controller.Enter = false;


        if (WeaponIdx[0] >= 0 && Weapons[0][WeaponIdx[0]] != null)
        {
            Weapons[0][WeaponIdx[0]].GetInstance().gameObject.SetActive(true);
        }
    }


    public void SetPilot(PhxSeat section)
    {
        Context = PhxSoldierContext.Pilot;
        CurrentSeat = section;

        if (Body != null)
        {
            Destroy(Body);
            Body = null;
        }

        GetComponent<CapsuleCollider>().enabled = false;


        if (section.PilotPosition == null)
        {
            GetComponent<SkinnedMeshRenderer>().enabled = false;
        }
        else
        {
            GetComponent<SkinnedMeshRenderer>().enabled = true;

            transform.parent = section.PilotPosition;
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            if (CurrentSeat.PilotAnimationType != PilotAnimationType.None)
            {
                bool isStatic = CurrentSeat.PilotAnimationType == PilotAnimationType.StaticPose;
                string animName = isStatic ? CurrentSeat.PilotAnimation : CurrentSeat.Pilot9Pose;
                
                Poser = new PhxPoser("human_4", "human_" + animName, transform, isStatic);   
            }    
        }

        if (WeaponIdx[0] >= 0 && Weapons[0][WeaponIdx[0]] != null)
        {
            Weapons[0][WeaponIdx[0]].GetInstance().gameObject.SetActive(false);
        }
    }


    // see: com_inf_default
    float[] GetControlSpeed(PhxControlState state)
    {
        foreach (object[] values in C.ControlSpeed.Values)
        {
            string controlName = values[0] as string;
            if (!string.IsNullOrEmpty(controlName) && controlName == state.ToString().ToLowerInvariant())
            {
                return new float[3]
                {
                    (float)values[1],
                    (float)values[2],
                    (float)values[3],
                };
            }
        }
        Debug.LogError($"Cannot find control state '{state}'!");
        return null;
    }

    public void Tick(float deltaTime)
    {
        if (IsDead)
        {
            return;
        }
        TickAnimOverride(deltaTime);
        Profiler.BeginSample("Tick Soldier");
        UpdateState(deltaTime);
        Profiler.EndSample();
    }

    public void TickPhysics(float deltaTime)
    {
        if (IsDead)
        {
            return;
        }
        Profiler.BeginSample("Tick Soldier Physics");
        UpdatePhysics(deltaTime);
        Profiler.EndSample();
    }


    void UpdatePose(float deltaTime)
    {
        Vector4 Input = new Vector4(Controller.MoveDirection.x, Controller.MoveDirection.y, Controller.mouseX, Controller.mouseY);

        if (Poser != null && CurrentSeat != null)
        {
            float blend = 2f * deltaTime;

            if (CurrentSeat.PilotAnimationType == PilotAnimationType.NinePose)
            {
                if (Vector4.Magnitude(Input) < .001f)
                {
                    Poser.SetState(PhxNinePoseState.Idle, blend);
                    return;
                }

                if (Input.x > .01f)
                {
                    Poser.SetState(PhxNinePoseState.StrafeRight, blend);           
                }

                if (Input.x < -.01f)
                {
                    Poser.SetState(PhxNinePoseState.StrafeLeft, blend);            
                }

                if (Input.y < 0f) 
                {
                    if (Input.z > .01f)
                    {
                        Poser.SetState(PhxNinePoseState.BackwardsTurnLeft, blend);            
                    }
                    else if (Input.z < -.01f)
                    {
                        Poser.SetState(PhxNinePoseState.BackwardsTurnRight, blend);            
                    }
                    else
                    {
                        Poser.SetState(PhxNinePoseState.Backwards, blend);            
                    }
                }
                else
                {
                    if (Input.z > .01f)
                    {
                        Poser.SetState(PhxNinePoseState.ForwardTurnLeft, blend);            
                    }
                    else if (Input.z < -.01f)
                    {
                        Poser.SetState(PhxNinePoseState.ForwardTurnRight, blend);            
                    }
                    else
                    {
                        Poser.SetState(PhxNinePoseState.Forward, blend);            
                    }
                }
            }
            else if (CurrentSeat.PilotAnimationType == PilotAnimationType.FivePose)
            {
                if (Mathf.Abs(Input.z) + Mathf.Abs(Input.w) < .001f)
                {
                    Poser.SetState(PhxFivePoseState.Idle, blend);
                    return;
                }

                if (Input.z > .01f)
                {
                Poser.SetState(PhxFivePoseState.TurnRight, blend);            
                }
                else
                {
                    Poser.SetState(PhxFivePoseState.TurnLeft, blend);            
                }

                if (Input.w > .01f)
                {
                    Poser.SetState(PhxFivePoseState.TurnDown, blend);            
                }
                else
                {
                    Poser.SetState(PhxFivePoseState.TurnUp, blend);            
                }
            }
            else if (CurrentSeat.PilotAnimationType == PilotAnimationType.StaticPose)
            {
                Poser.SetState();
            }
            else 
            {
                // Not sure what happens if PilotPosition is defined but PilotAnimation/Pilot9Pose are missing...
            }
        }
    }




    // Set when a sprint drains the bar, cleared only once enough has come back.
    bool Winded;

    /// <summary>
    /// Fraction of the bar that must return before a winded soldier can sprint
    /// again.
    /// </summary>
    /// <remarks>
    /// Hysteresis, and it is not cosmetic. EnergyMinSprint is typically a tiny
    /// floor, so gating on that alone let a soldier who ran the bar to zero
    /// re-enter Sprint a frame or two later, empty it again, and repeat -
    /// flipping between the sprint and run animations several times a second.
    /// A human taps sprint and never notices; the AI holds it down permanently,
    /// which is why their animation looked broken and the player's didn't.
    /// </remarks>
    const float RecoveredEnergyFraction = 0.35f;

    /// <summary>
    /// True when there is enough stamina left to break into a sprint.
    /// EnergyMinSprint is the odf's own floor; Winded adds the recovery
    /// requirement on top of it.
    /// </summary>
    bool CanStartSprint => MaxEnergy <= 0f || (!Winded && CurEnergy >= C.EnergyMinSprint.Get());

    /// <summary>True once a sprint has drained the bar and must end.</summary>
    bool IsEnergySpent => MaxEnergy > 0f && CurEnergy <= 0f;

    /// <summary>True while too winded to sprint - the AI stops asking.</summary>
    public bool IsWinded => Winded;

    /// <summary>True while actually sprinting, for anything that reacts to it.</summary>
    public bool IsSprinting => State == PhxControlState.Sprint;

    /// <summary>
    /// Spend stamina on something other than sprinting - a force power, a
    /// saber deflection. Returns false and spends nothing when the bar cannot
    /// cover it, so the caller can refuse the whole action rather than
    /// performing a free one.
    /// </summary>
    /// <remarks>
    /// Classes with no EnergyBar always succeed. That covers every non-hero
    /// unit, which is exactly right: these costs are a hero resource, and a
    /// class that declares no bar should not be silently unable to act.
    /// </remarks>
    public bool TrySpendEnergy(float amount)
    {
        if (amount <= 0f) return true;

        float max = MaxEnergy;
        if (max <= 0f) return true;
        if (CurEnergy < amount) return false;

        CurEnergy -= amount;
        if (CurEnergy <= 0f)
        {
            Winded = true;
        }
        return true;
    }

    /// <summary>
    /// Drain stamina while sprinting, restore it otherwise. Classes with no
    /// EnergyBar (most vehicles-as-soldiers and some award units) opt out
    /// entirely, keeping the old unlimited behaviour for them.
    /// </summary>
    void TickEnergy(float deltaTime)
    {
        float max = MaxEnergy;
        if (max <= 0f) return;

        if (State == PhxControlState.Sprint)
        {
            CurEnergy -= C.EnergyDrainSprint.Get() * deltaTime;
        }
        else
        {
            // BF2 splits recovery in two: a faster rate when standing still,
            // a slower one while otherwise moving.
            bool idle = Controller == null || Controller.MoveDirection.sqrMagnitude < 0.01f;
            float restore = idle ? C.EnergyRestoreIdle.Get() : C.EnergyRestore.Get();
            CurEnergy += restore * deltaTime;
        }

        CurEnergy = Mathf.Clamp(CurEnergy, 0f, max);

        // Latch on empty, release only after a real recovery - see the note on
        // RecoveredEnergyFraction.
        if (CurEnergy <= 0f)
        {
            Winded = true;
        }
        else if (Winded && CurEnergy >= max * RecoveredEnergyFraction)
        {
            Winded = false;
        }
    }

    void UpdateState(float deltaTime)
    {
        if (Context == PhxSoldierContext.Pilot && Controller != null)
        {
            Animator.SetActive(false);
            UpdatePose(deltaTime);
            return;
        }

        AnimationCorrection();

        AlertTimer = Mathf.Max(AlertTimer - deltaTime, 0f);

        TickEnergy(deltaTime);

        if (Controller == null)
        {
            return;
        }

        // Will work into specific control schema.  Not sure when you can't enter vehicles...
        if (Controller.Enter && Context == PhxSoldierContext.Free)
        {
            PhxVehicle ClosestVehicle = null;
            float ClosestDist = float.MaxValue;
            
            Collider[] PossibleVehicles = Physics.OverlapSphere(transform.position, 5.0f, ~0, QueryTriggerInteraction.Collide);
            foreach (Collider PossibleVehicle in PossibleVehicles)
            {
                GameObject CollidedObj = PossibleVehicle.gameObject;
                if (PossibleVehicle.attachedRigidbody != null)
                {
                    CollidedObj = PossibleVehicle.attachedRigidbody.gameObject;
                }

                PhxVehicle Vehicle = CollidedObj.GetComponent<PhxVehicle>();
                if (Vehicle != null && Vehicle.HasAvailableSeat())
                {
                    float Dist = Vector3.Magnitude(transform.position - Vehicle.transform.position);
                    if (Dist < ClosestDist)
                    {
                        ClosestDist = Dist;
                        ClosestVehicle = Vehicle;
                    }
                }
            }

            if (ClosestVehicle != null)
            {
                CurrentSeat = ClosestVehicle.TryEnterVehicle(this);
                if (CurrentSeat != null)
                {
                    SetPilot(CurrentSeat);
                    Controller.Enter = false;
                    return;
                }
            }
            
        }

        // Flattening the view direction collapses to a zero vector whenever the
        // controller is looking straight up or down - an AI aiming at a target
        // directly overhead, or at its own feet. LookRotation answers that with
        // a zero quaternion, which Rigidbody.MoveRotation then rejects outright
        // ("Rotation quaternions must be unit length") and the soldier stops
        // turning. Keep facing where we already face instead.
        Vector3 lookWalkForward = Controller.ViewDirection;
        lookWalkForward.y = 0f;
        if (lookWalkForward.sqrMagnitude > 1e-6f)
        {
            LookRot = Quaternion.LookRotation(lookWalkForward);
        }

        LandTimer = Mathf.Max(LandTimer - deltaTime, 0f);
        TurnTimer = Mathf.Max(TurnTimer - deltaTime, 0f);

        if (LandTimer == 0f)
        {
            // Stand - Crouch - Sprint
            if (State == PhxControlState.Stand || State == PhxControlState.Crouch || State == PhxControlState.Sprint)
            {
                float accStep = C.Acceleration * deltaTime;
                float thrustFactor = ControlValues[(int)State][0];
                float strafeFactor = ControlValues[(int)State][1];
                float turnFactor = ControlValues[(int)State][2];

                Vector3 moveDirLocal = new Vector3(Controller.MoveDirection.x * turnFactor, 0f, Controller.MoveDirection.y);
                Vector3 moveDirWorld = LookRot * moveDirLocal;

                // TODO: base turn speed in degreees/sec really 45?
                MaxTurnSpeed.y = 45f * C.MaxTurnSpeed * turnFactor;

                if (moveDirLocal.magnitude == 0f)
                {
                    CurrSpeed *= 0.1f * deltaTime;

                    if (TurnTimer > 0f)
                    {
                        LookRot = Quaternion.Slerp(LookRot, TurnStart, TurnTimer / TurnTime);
                    }
                    else
                    {
                        //float rotDiff = Quaternion.Angle(transform.rotation, lookRot);
                        float rotDiff = Mathf.DeltaAngle(transform.rotation.eulerAngles.y, LookRot.eulerAngles.y);
                        if (rotDiff < -40f || rotDiff > 60f)
                        {
                            TurnTimer = TurnTime;
                            TurnStart = transform.rotation;
                            Animator.Anim.SetState(0, rotDiff < 0f ? Animator.TurnLeft : Animator.TurnRight);
                            Animator.Anim.SetState(1, CraSettings.STATE_NONE);
                            Animator.Anim.RestartState(0);
                        }

                        LookRot = transform.rotation;
                    }
                }
                else
                {
                    CurrSpeed += moveDirWorld * accStep;

                    float maxSpeed = moveDirLocal.z < 0.2f ? C.MaxStrafeSpeed : C.MaxSpeed;
                    float forwardFactor = moveDirLocal.z < 0.2f ? strafeFactor : thrustFactor;
                    CurrSpeed = Vector3.ClampMagnitude(CurrSpeed, maxSpeed * forwardFactor);

                    if (moveDirLocal.z <= 0f)
                    {
                        // invert look direction when strafing left/right
                        moveDirWorld = -moveDirWorld;
                    }

                    // Same guard as the view-direction LookRotation above, which
                    // was fixed while this one was not. Quaternion.LookRotation
                    // of a degenerate vector yields a zero quaternion, and once
                    // LookRot holds one it never recovers - every subsequent
                    // frame multiplies through it, so the soldier stops turning
                    // and MoveRotation rejects it forever after. That is the
                    // "Rotation quaternions must be unit length" spam.
                    if (moveDirWorld.sqrMagnitude > 1e-6f)
                    {
                        LookRot = Quaternion.LookRotation(moveDirWorld);
                    }
                }

                // Locomotion yields layer 0 while an override holds it. Without
                // this the two writers fight every frame - see
                // RequestAnimOverride.
                if (TurnTimer == 0f && !HasAnimOverride)
                {
                    // ---------------------------------------------------------------------------------------------
                    // Forward
                    // ---------------------------------------------------------------------------------------------
                    float walk = Mathf.Clamp01(Controller.MoveDirection.magnitude);
                    if (Controller.MoveDirection.y <= 0f)
                    {
                        // invert animation direction for strafing (left/right)
                        walk = -walk;
                    }

                    if (State == PhxControlState.Sprint)
                    {
                        Animator.Anim.SetState(0, Animator.StandSprint);
                    }
                    else if (walk > 0.2f && walk <= 0.75f)
                    {
                        Animator.Anim.SetState(0, AlertTimer > 0f ? Animator.StandAlertWalk : Animator.StandWalk);
                        Animator.Anim.SetPlaybackSpeed(0, Animator.StandWalk, walk / 0.75f);
                    }
                    else if (walk > 0.75f)
                    {
                        Animator.Anim.SetState(0, AlertTimer > 0f ? Animator.StandAlertRun : Animator.StandRun);
                        Animator.Anim.SetPlaybackSpeed(0, Animator.StandRun, walk);
                    }
                    else if (walk < -0.2f)
                    {
                        Animator.Anim.SetState(0, AlertTimer > 0f ? Animator.StandAlertBackward : Animator.StandBackward);
                        Animator.Anim.SetPlaybackSpeed(0, Animator.StandBackward, -walk);
                    }
                    else
                    {
                        Animator.Anim.SetState(0, AlertTimer > 0f ? Animator.StandAlertIdle : Animator.StandIdle);
                    }
                    // ---------------------------------------------------------------------------------------------
                }
            }

            // Stand - Crouch
            if (State == PhxControlState.Stand || State == PhxControlState.Crouch)
            {
                State = Controller.Crouch ? PhxControlState.Crouch : PhxControlState.Stand;

                // ---------------------------------------------------------------------------------------------
                // Idle
                // ---------------------------------------------------------------------------------------------
                if (Controller.IdleTime >= IdleTime)
                {
                    if (bHasLookaroundIdleAnim && !bHasCheckweaponIdleAnim)
                    {
                        //Anim.SetTrigger(IdleNames[0]);
                    }
                    else if (!bHasLookaroundIdleAnim && bHasCheckweaponIdleAnim)
                    {
                        //Anim.SetTrigger(IdleNames[1]);
                    }
                    else if (bHasLookaroundIdleAnim && bHasCheckweaponIdleAnim)
                    {
                        //Anim.SetTrigger(IdleNames[UnityEngine.Random.Range(0, 1)]);
                    }
                    Controller.ResetIdleTime();
                }
                if (!Controller.IsIdle && LastIdle)
                {
                    //Anim.SetTrigger("UnIdle");
                }
                // ---------------------------------------------------------------------------------------------


                // ---------------------------------------------------------------------------------------------
                // Shooting
                // ---------------------------------------------------------------------------------------------
                // Channels can legitimately hold no weapon: several stock
                // classes reference award/dispenser weapons that aren't in the
                // loaded side lvls ("Cannot find weapon class ..."), which
                // leaves a null slot. Reaching through it threw a
                // NullReferenceException every frame an AI held secondary fire.
                IPhxWeapon primary = GetEquippedWeapon(0);
                IPhxWeapon secondary = GetEquippedWeapon(1);

                if (primary != null && primary.GetReloadProgress() == 1f)
                {
                    if (Controller.ShootPrimary)
                    {
                        // only fire when not currently turning
                        //Weap.Fire = TurnTimer <= 0f;

                        primary.Fire(Controller, Controller.GetAimPosition());
                        AlertTimer = AlertTime;
                    }
                    else if (Controller.ShootSecondary && secondary != null)
                    {
                        secondary.Fire(Controller, Controller.GetAimPosition());
                        AlertTimer = AlertTime;
                    }
                    else if (Controller.Reload)
                    {
                        primary.Reload();
                        //Anim.SetTrigger("Reload");
                    }
                }
                // ---------------------------------------------------------------------------------------------
                if (Controller.NextPrimaryWeapon)
                {
                    NextWeapon(0);
                }
                if (Controller.NextSecondaryWeapon)
                {
                    NextWeapon(1);
                }
            }

            // Stand - Sprint
            if (State == PhxControlState.Stand || State == PhxControlState.Sprint)
            {
                // ---------------------------------------------------------------------------------------------
                // Jumping
                // ---------------------------------------------------------------------------------------------
                if (Controller.Jump)
                {
                    State = PhxControlState.Jump;
                    JumpTimer = JumpTime;

                    Animator.Anim.SetState(0, Animator.Jump);
                    Animator.Anim.SetState(1, CraSettings.STATE_NONE);
                }
                // ---------------------------------------------------------------------------------------------
            }

            // Stand
            if (State == PhxControlState.Stand)
            {
                IPhxWeapon sprintWeap = GetEquippedWeapon(0);
                if (Controller.MoveDirection.y > 0.2f && Controller.Sprint && CanStartSprint &&
                    (sprintWeap == null || sprintWeap.GetReloadProgress() == 1f))
                {
                    State = PhxControlState.Sprint;
                }
            }

            // Crouch
            if (State == PhxControlState.Crouch)
            {
                if (Controller.Jump)
                {
                    State = PhxControlState.Stand;
                }

                // TODO: verify
                else if (Controller.MoveDirection.y > 0.8f && Controller.Sprint && CanStartSprint)
                {
                    State = PhxControlState.Sprint;
                }
            }

            // Sprint
            if (State == PhxControlState.Sprint)
            {
                // Running the bar dry drops you out of the sprint, same as
                // letting go of the key.
                if (Controller.MoveDirection.y < 0.8f || !Controller.Sprint || IsEnergySpent)
                {
                    State = PhxControlState.Stand;
                }
            }
        }


        // Handle falling / jumping
        if (State != PhxControlState.Jump && !Grounded)
        {
            State = PhxControlState.Jump;
            Animator.Anim.SetState(0, Animator.Fall);
            Animator.Anim.SetState(1, CraSettings.STATE_NONE);
        }


        //Grounded = Physics.CheckSphere(transform.position, 0.4f, PhxGameRuntime.PlayerMask, QueryTriggerInteraction.Ignore);

        // Jump
        if (State == PhxControlState.Jump)
        {
            FallTimer += deltaTime;
            JumpTimer -= deltaTime;

            if (Grounded && JumpTimer < 0f)
            {
                State = PhxControlState.Stand;

                if (FallTimer > 1.5f)
                {
                    LandTimer = 0.9f;
                    Animator.Anim.SetState(0, Animator.LandHard);
                    Animator.Anim.SetState(1, CraSettings.STATE_NONE);
                    //Debug.Log($"Land HARD {FallTimer}");
                }
                else if (FallTimer > 1.2f || Controller.MoveDirection.magnitude < 0.1f)
                {
                    LandTimer = 0.6f;
                    Animator.Anim.SetState(0, Animator.LandSoft);
                    Animator.Anim.SetState(1, CraSettings.STATE_NONE);
                    //Debug.Log($"Land Soft {FallTimer}");
                }
                else
                {
                    LandTimer = 0.05f;
                    Animator.Anim.SetState(0, Animator.LandSoft);
                    Animator.Anim.SetState(1, CraSettings.STATE_NONE);
                    //Debug.Log($"Land very Soft {FallTimer}");
                }

                FallTimer = 0f;
            }
        }

        //Anim.SetBool("Alert", AlertTimer > 0f);
        LastIdle = Controller.IsIdle;
    }

    void UpdatePhysics(float deltaTime)
    {
        if (Context == PhxSoldierContext.Pilot) return;

        if (IsFixated)
        {
            transform.rotation = LookRot;
            return;
        }

        // An earlier version of this check temporarily switched our own layer
        // to "Ignore Raycast" so a self-overlap wouldn't register as ground -
        // the capsule's bottom hemisphere (radius 0.4, centered 0.35m above
        // this exact point) overlaps this exact sphere query, so without
        // exclusion it always finds itself. That trick didn't work:
        // Physics.CheckSphere's no-mask overload defaults to
        // Physics.AllLayers, not Physics.DefaultRaycastLayers like Raycast
        // does, so "Ignore Raycast" was never actually excluded - the check
        // could self-detect (or detect a teammate standing close by) as
        // ground, or simply be unreliable, either of which reads as
        // "grounded" when there is nothing solid underneath: the falling
        // animation flashes and the character drops through, since the only
        // thing that lets gravity move a soldier at all is the not-grounded
        // Jump state - the Stand/Crouch/Sprint states below pin position via
        // MovePosition every tick regardless of gravity. An explicit mask,
        // the same one SettleOnGround already uses to find a spawn point,
        // removes the ambiguity and also correctly excludes other soldiers.
        Grounded = Physics.CheckSphere(transform.position, 0.4f, PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore);

        if ((PrevState == PhxControlState.Stand || PrevState == PhxControlState.Sprint) && State == PhxControlState.Jump)
        {
            if (JumpTimer > 0f)
            {
                // Intentional jump. Scaling JumpHeight rather than the launch
                // velocity keeps the ODF value meaning what it says - a height
                // in metres - so the relative feel of each class is preserved.
                float jumpHeight = C.JumpHeight * Mathf.Max(0.1f, PhxBF3.Config.JumpHeightScale);
                Body.AddForce(Vector3.up * Mathf.Sqrt(jumpHeight * -2f * Physics.gravity.y) + CurrSpeed, ForceMode.VelocityChange);
            }
            else
            {
                // Falling, (from a cliff or whatnot) / no intentional jump
                Body.AddForce(CurrSpeed, ForceMode.VelocityChange);
            }
        }
        else if ((State == PhxControlState.Stand || State == PhxControlState.Crouch || State == PhxControlState.Sprint) && LandTimer == 0f)
        {
            Body.MovePosition(Body.position + CurrSpeed * deltaTime);

            // Last line of defence before PhysX. Guarding every producer of
            // LookRot is the real fix, but a single unguarded path anywhere -
            // now or later - turns into an error every physics tick for every
            // soldier, and the message names neither the soldier nor the
            // cause. Normalising here keeps one bad frame from becoming
            // permanent, and reports it once instead of forever.
            Body.MoveRotation(SafeRotation(LookRot));
        }

        PrevState = State;
    }

    /// <summary>
    /// A rotation PhysX will accept, and a one-shot report when one was not.
    /// </summary>
    /// <remarks>
    /// A quaternion reaches here degenerate (all zeros, from a LookRotation on
    /// a zero vector) or non-finite (NaN propagated out of a velocity). PhysX
    /// rejects both and logs per call - which is per soldier per physics tick,
    /// so a single bad value from one unit buries the console and hides
    /// everything else.
    ///
    /// Logged once per soldier so the underlying cause is still visible
    /// without the flood.
    /// </remarks>
    bool ReportedBadRotation;

    Quaternion SafeRotation(Quaternion rotation)
    {
        float lengthSqr = rotation.x * rotation.x + rotation.y * rotation.y +
                          rotation.z * rotation.z + rotation.w * rotation.w;

        // NaN fails every comparison, so this catches non-finite too.
        if (lengthSqr > 1e-6f && lengthSqr < 1e6f)
        {
            return Quaternion.Normalize(rotation);
        }

        if (!ReportedBadRotation)
        {
            ReportedBadRotation = true;
            Debug.LogWarning($"[Phoenix] '{name}' produced a non-unit rotation " +
                             $"({rotation.x}, {rotation.y}, {rotation.z}, {rotation.w}); " +
                             "falling back to its current facing. Reported once per soldier.");
        }
        return transform.rotation;
    }

    public Vector3 RotAlt1 = new Vector3(7f, -78f, -130f);
    public Vector3 RotAlt2 = new Vector3(0f, -50f, -75f);
    public Vector3 RotAlt3 = new Vector3(0f, -68f, -81f);
    public Vector3 RotAlt4 = new Vector3(0f, -53f, -77f);

    void AnimationCorrection()
    {
        if (Context == PhxSoldierContext.Pilot) return;

        if (Controller == null/* || FallTimer > 0f || TurnTimer > 0f*/)
        {
            return;
        }

        if (State == PhxControlState.Stand || State == PhxControlState.Crouch)
        {
            if (Animator.Anim.GetCurrentStateIdx(1) == Animator.StandShootPrimary)
            {
                Spine.rotation = Quaternion.LookRotation(Controller.ViewDirection) * Quaternion.Euler(RotAlt4);
            }
            else if (AlertTimer > 0f)
            {
                if (Controller.MoveDirection.magnitude > 0.1f)
                {
                    Spine.rotation = Quaternion.LookRotation(Controller.ViewDirection) * Quaternion.Euler(RotAlt3);
                }
                else
                {
                    Spine.rotation = Quaternion.LookRotation(Controller.ViewDirection) * Quaternion.Euler(RotAlt2);
                }
            }
            else
            {
                Neck.rotation = Quaternion.LookRotation(Controller.ViewDirection) * Quaternion.Euler(RotAlt1);
            }
        }
    }

    public CraAnimator GetAnimator()
    {
        return Animator.Anim;
    }
}
