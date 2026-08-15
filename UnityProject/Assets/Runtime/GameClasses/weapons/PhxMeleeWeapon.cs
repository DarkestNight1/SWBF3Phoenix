using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SWBF2 "melee" weapon class (lightsabers, wrist blades, staffs).
///
/// Where the odf names a <c>ComboAnimationBank</c> and that combo exists in
/// the mounted data, swings run through the authored move set
/// (<see cref="PhxComboRunner"/>): each move commits the wielder for its
/// duration, damage lands only inside the authored window with that window's
/// own reach and push, and a further press only chains if it arrives while a
/// transition is open.
///
/// Everything else - and any hero whose combo file is missing - keeps the
/// single arc sweep with a cooldown, which is what all melee did before.
///
/// Lethal saber hits route through PhxSoldier.AddDamageFrom with the saber
/// flag, which triggers BF3 Legacy dismemberment on kill.
/// </summary>
public class PhxMeleeWeapon : PhxInstance<PhxMeleeWeapon.ClassProperties>, IPhxWeapon, IPhxTickable
{
    protected PhxScene Scene => PhxGame.GetScene();

    public class ClassProperties : PhxClass
    {
        public PhxProp<string> AnimationBank = new PhxProp<string>("melee");
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        // Swing timing
        public PhxProp<float> ShotDelay = new PhxProp<float>(0.7f);

        // Damage per swing. SWBF2 melee ODFs vary in how they specify damage;
        // MaxDamage covers the common case, default one-shots basic infantry.
        public PhxProp<float> MaxDamage = new PhxProp<float>(300f);

        // Same per-health-type scaling every other damage source uses
        // (-1 = not set in the odf; see PhxDamage.Resolve)
        public PhxProp<float> PersonScale = new PhxProp<float>(-1f);
        public PhxProp<float> AnimalScale = new PhxProp<float>(-1f);
        public PhxProp<float> DroidScale = new PhxProp<float>(-1f);
        public PhxProp<float> VehicleScale = new PhxProp<float>(-1f);
        public PhxProp<float> BuildingScale = new PhxProp<float>(-1f);
        public PhxProp<float> HealthScale = new PhxProp<float>(-1f);
        public PhxProp<float> ArmorScale = new PhxProp<float>(-1f);

        public PhxDamageScales GetDamageScales()
        {
            return PhxDamage.Resolve(PersonScale, AnimalScale, DroidScale,
                                     VehicleScale, BuildingScale,
                                     HealthScale, ArmorScale);
        }

        // Reach and sweep arc of the swing
        public PhxProp<float> LightSaberLength = new PhxProp<float>(3.0f);
        public PhxProp<float> DamageArc = new PhxProp<float>(120f);

        // Further saber odf properties per the BF2 mod tools docs; populated
        // from the odf when present (blade visuals / combo system TODO)
        public PhxProp<float> LightSaberWidth = new PhxProp<float>(0.1f);
        public PhxProp<string> LightSaberTexture = new PhxProp<string>(null);
        public PhxProp<string> ComboAnimationBank = new PhxProp<string>(null);

        // Anything with "saber" in its class marks a lightsaber; can also be
        // forced via odf (used for dismemberment + deflect visuals)
        public PhxProp<bool> IsLightSaber = new PhxProp<bool>(false);

        public PhxProp<string> FireSound = new PhxProp<string>(null);
    }

    Action ShotCallback;
    Action ReloadCallback;
    AudioSource Audio;

    PhxPawnController OwnerController;
    List<Collider> IgnoredColliders;
    Transform FirePoint;

    float SwingTimer;
    bool bIsSaber;

    PhxComboRunner Combo;

    // Direction the current combo attack sweeps. Captured when the swing is
    // requested rather than when the window fires, because the wielder keeps
    // turning during a committed move and the swing should land where it was
    // aimed.
    Vector3 SwingDirection = Vector3.forward;

    static readonly Collider[] OverlapCache = new Collider[32];


    public override void Init()
    {
        if (C.FireSound.Get() != null)
        {
            AudioClip clip = SoundLoader.Instance.LoadSound(C.FireSound.Get());
            if (clip != null)
            {
                Audio = gameObject.AddComponent<AudioSource>();
                Audio.playOnAwake = false;
                Audio.spatialBlend = 1.0f;
                Audio.rolloffMode = AudioRolloffMode.Linear;
                Audio.minDistance = 2.0f;
                Audio.maxDistance = 30.0f;
                Audio.loop = false;
                Audio.clip = clip;
            }
        }

        FirePoint = transform;

        bIsSaber = C.IsLightSaber ||
                   (C.Name != null && C.Name.ToLowerInvariant().Contains("saber")) ||
                   name.ToLowerInvariant().Contains("saber");

        PhxComboDefinition combo = PhxComboLoader.Load(C.ComboAnimationBank.Get());
        if (combo != null)
        {
            Combo = new PhxComboRunner(combo);
            Combo.OnAttackWindow += ApplyComboAttack;
            Combo.OnMoveStarted += PlayComboAnimation;
        }
    }

    public override void Destroy() { }

    public bool Fire(PhxPawnController owner, Vector3 targetPos)
    {
        OwnerController = owner ?? OwnerController;

        Vector3 origin = FirePoint.position;
        Vector3 forward = targetPos - origin;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.001f ? forward.normalized : FirePoint.forward;

        if (Combo != null)
        {
            // The combo owns the timing; a press either starts a move, chains
            // into one, or is buffered, and only the first two are a swing.
            SwingDirection = forward;
            if (!Combo.Press(PhxComboInput.Attack)) return false;

            PlaySwingFeedback();
            return true;
        }

        if (SwingTimer > 0f)
        {
            return false;
        }
        SwingTimer = C.ShotDelay;
        PlaySwingFeedback();

        Sweep(origin, forward, C.LightSaberLength, C.DamageArc, C.MaxDamage, 0f);
        return true;
    }

    void PlaySwingFeedback()
    {
        if (Audio != null)
        {
            Audio.PlayOneShot(Audio.clip, 1.0f);
        }
        ShotCallback?.Invoke();
    }

    /// <summary>One authored attack window landing, with its own parameters.</summary>
    void ApplyComboAttack(PhxComboAttack attack)
    {
        Sweep(FirePoint.position, SwingDirection, attack.Reach, attack.Arc,
              attack.Damage, attack.Push);
    }

    /// <summary>
    /// Put the move the combo runner just entered on the wielder's rig.
    /// </summary>
    /// <remarks>
    /// The state machine and the animation data have both been present all
    /// along and were never connected: the runner knew the move, the move
    /// knew its clip name, and nothing played it. Failure here is deliberately
    /// quiet - a hero whose bank is missing one clip should keep fighting with
    /// the timing intact rather than stop mid-combo.
    /// </remarks>
    void PlayComboAnimation(PhxComboMove move)
    {
        if (move == null || string.IsNullOrEmpty(move.AnimationName)) return;

        PhxSoldier wielder = OwnerController?.Pawn as PhxSoldier;
        if (wielder == null) return;

        wielder.PlayOverrideAnim(C.ComboAnimationBank.Get(), move.AnimationName);
    }

    /// <summary>
    /// Damage everything hostile within reach and inside the arc, once each.
    /// </summary>
    void Sweep(Vector3 origin, Vector3 forward, float reach, float arc, float damage, float push)
    {
        if (reach <= 0f) return;

        int ownerTeam = OwnerController != null ? OwnerController.Team : 0;
        float halfArc = arc * 0.5f;

        int count = Physics.OverlapSphereNonAlloc(origin, reach, OverlapCache);
        var alreadyHit = new HashSet<PhxInstance>();
        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (coll == null) continue;
            if (IgnoredColliders != null && IgnoredColliders.Contains(coll)) continue;

            PhxInstance instance = coll.GetComponentInParent<PhxInstance>();
            if (instance == null || instance == this || alreadyHit.Contains(instance)) continue;
            if (PhxDamage.IsFriendly(ownerTeam, instance)) continue;

            Vector3 to = instance.transform.position - origin;
            to.y = 0f;
            if (to.sqrMagnitude > 0.001f && Vector3.Angle(forward, to.normalized) > halfArc) continue;

            alreadyHit.Add(instance);

            Vector3 hitPos = coll.ClosestPoint(origin + forward * reach * 0.5f);

            // scaled by the target's HealthType, like all other damage
            PhxDamage.ApplyToCollider(coll, damage, C.GetDamageScales(),
                                      hitPos, isSaber: bIsSaber,
                                      instigator: OwnerController);

            if (push <= 0f) continue;

            Rigidbody body = coll.attachedRigidbody;
            if (body != null && !body.isKinematic)
            {
                body.AddForce((to.normalized + Vector3.up * 0.2f).normalized * push,
                              ForceMode.VelocityChange);
            }
        }
    }

    public void Tick(float deltaTime)
    {
        if (SwingTimer > 0f)
        {
            SwingTimer -= deltaTime;
        }
        Combo?.Tick(deltaTime);
    }

    // ---- IPhxWeapon boilerplate ----

    public PhxInstance GetInstance() => this;
    public void Reload() { }
    public void OnShot(Action callback) { ShotCallback += callback; }
    public void OnReload(Action callback) { ReloadCallback += callback; }
    public string GetAnimBankName() => C.AnimationBank.Get();

    public void SetFirePoint(Transform fp) { FirePoint = fp; }
    public Transform GetFirePoint() => FirePoint;
    public void GetFirePoint(out Vector3 pos, out Quaternion rot)
    {
        pos = FirePoint.position;
        rot = FirePoint.rotation;
    }

    public void SetIgnoredColliders(List<Collider> colliders) { IgnoredColliders = colliders; }
    public List<Collider> GetIgnoredColliders() => IgnoredColliders ?? new List<Collider>();

    public PhxPawnController GetOwnerController() => OwnerController;
    public bool IsFiring() => SwingTimer > 0f || (Combo != null && Combo.IsBusy);

    /// <summary>The move currently being performed, or null. For animation.</summary>
    public PhxComboMove GetCurrentComboMove() => Combo?.CurrentMove;

    /// <summary>Break out of a combo - death, being knocked down, dropping the weapon.</summary>
    public void InterruptCombo() => Combo?.Interrupt();

    // melee weapons never run dry
    public int GetMagazineSize() => 1;
    public int GetTotalAmmo() => 1;
    public int GetMagazineAmmo() => 1;
    public int GetAvailableAmmo() => 1;
    public float GetReloadTime() => 0f;
    public float GetReloadProgress() => 1f;
    public void AddAmmo(float magazines) { }

    // A saber has no optics.
    public float[] GetZoomLevels() => System.Array.Empty<float>();
    public float GetZoomRate() => 0f;
    public bool HasSniperScope() => false;
}
