using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SWBF2 "melee" weapon class (lightsabers, wrist blades, staffs). The stock
/// game left this ODF class unimplemented in Phoenix; this implementation
/// performs an arc sweep in front of the wielder on each swing and applies
/// damage directly (no ordnance).
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
    }

    public override void Destroy() { }

    public bool Fire(PhxPawnController owner, Vector3 targetPos)
    {
        if (SwingTimer > 0f)
        {
            return false;
        }
        SwingTimer = C.ShotDelay;
        OwnerController = owner ?? OwnerController;

        if (Audio != null)
        {
            Audio.PlayOneShot(Audio.clip, 1.0f);
        }
        ShotCallback?.Invoke();

        // arc sweep in front of the wielder
        Vector3 origin = FirePoint.position;
        Vector3 forward = (targetPos - origin);
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.001f ? forward.normalized : FirePoint.forward;

        int ownerTeam = OwnerController != null ? OwnerController.Team : 0;
        float halfArc = C.DamageArc * 0.5f;

        int count = Physics.OverlapSphereNonAlloc(origin, C.LightSaberLength, OverlapCache);
        var alreadyHit = new HashSet<PhxInstance>();
        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (IgnoredColliders != null && IgnoredColliders.Contains(coll)) continue;

            PhxInstance instance = coll.GetComponentInParent<PhxInstance>();
            if (instance == null || instance == this || alreadyHit.Contains(instance)) continue;
            if (ownerTeam != 0 && instance.Team == ownerTeam) continue;

            Vector3 to = instance.transform.position - origin;
            to.y = 0f;
            if (to.sqrMagnitude > 0.001f && Vector3.Angle(forward, to.normalized) > halfArc) continue;

            alreadyHit.Add(instance);

            Vector3 hitPos = coll.ClosestPoint(origin + forward * C.LightSaberLength * 0.5f);
            if (instance is PhxSoldier soldier)
            {
                soldier.AddDamageFrom(C.MaxDamage, hitPos, isSaber: bIsSaber);
            }
            else if (instance is IPhxDamageableInstance damageable)
            {
                damageable.AddDamage(C.MaxDamage);
            }
        }
        return true;
    }

    public void Tick(float deltaTime)
    {
        if (SwingTimer > 0f)
        {
            SwingTimer -= deltaTime;
        }
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
    public bool IsFiring() => SwingTimer > 0f;

    // melee weapons never run dry
    public int GetMagazineSize() => 1;
    public int GetTotalAmmo() => 1;
    public int GetMagazineAmmo() => 1;
    public int GetAvailableAmmo() => 1;
    public float GetReloadTime() => 0f;
    public float GetReloadProgress() => 1f;
}
