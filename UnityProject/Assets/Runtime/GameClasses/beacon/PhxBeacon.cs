using UnityEngine;

/// <summary>
/// A placed beacon: orbital-strike markers, recon beacons, the objects
/// campaign scripts drop to mark a spot.
/// </summary>
/// <remarks>
/// The <c>beacon</c> base class was unregistered, so these never ticked, never
/// expired, and - the part that matters for missions - never fired the object
/// events their scripts wait on. A beacon whose script never hears that it
/// went off leaves the objective it belongs to permanently incomplete.
///
/// What a beacon actually *does* is script-driven: it is a named object a
/// mission reacts to. This class owns the parts that are not script-specific -
/// lifetime, its effect, team ownership, being destructible - and raises the
/// events at the right moments.
/// </remarks>
public class PhxBeacon : PhxInstance<PhxBeacon.ClassProperties>,
                          IPhxTickable, IPhxDamageableInstance, IPhxDestructible
{
    static PhxScene SCENE => PhxGame.GetScene();

    public class ClassProperties : PhxClass
    {
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        /// <summary>Effect played for as long as the beacon is active.</summary>
        public PhxProp<string> BeaconEffect = new PhxProp<string>(null);

        /// <summary>Seconds the beacon stays active; 0 means indefinitely.</summary>
        public PhxProp<float> BeaconDuration = new PhxProp<float>(0f);

        public PhxProp<float> MaxHealth = new PhxProp<float>(50f);

        /// <summary>Explosion when the beacon is destroyed, if it has one.</summary>
        public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);
    }

    public PhxProp<float> CurHealth = new PhxProp<float>(50f);

    /// <summary>Controller credited for whatever the beacon calls in.</summary>
    public PhxPawnController Owner;

    /// <summary>Raised once when the beacon's duration runs out.</summary>
    public System.Action<PhxBeacon> OnExpired;

    PhxEffect ActiveEffect;
    float Timer;
    bool Finished;

    public override void Init()
    {
        Rigidbody body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;

        gameObject.layer = LayerMask.NameToLayer("BuildingAll");

        CurHealth.Set(C.MaxHealth.Get());
        Timer = C.BeaconDuration.Get();

        string effect = C.BeaconEffect.Get();
        if (!string.IsNullOrEmpty(effect) && SCENE != null)
        {
            ActiveEffect = SCENE.EffectsManager.LendEffect(effect);
            if (ActiveEffect != null)
            {
                ActiveEffect.SetParent(transform);
                ActiveEffect.SetLooping(true);
                ActiveEffect.Play();
            }
        }

        PhxDestructionRegistry.Register(this);
    }

    public override void Destroy()
    {
        StopEffect();
        PhxDestructionRegistry.Unregister(this);
    }

    // ------------------------------------------------------ IPhxDestructible
    public PhxDestructibleKind DestructibleKind => PhxDestructibleKind.MissionObject;
    public GameObject GetGameObject() => gameObject;
    public string GetDestructibleName() => name;
    public int GetTeam() => Team.Get();
    public float GetHealth() => CurHealth.Get();
    public float GetMaxHealth() => C == null ? 0f : C.MaxHealth.Get();
    public bool IsDestroyed => Finished;

    public void Tick(float deltaTime)
    {
        if (Finished || C.BeaconDuration.Get() <= 0f) return;

        Timer -= deltaTime;
        if (Timer > 0f) return;

        Finished = true;
        StopEffect();
        OnExpired?.Invoke(this);

        int? objIdx = SCENE?.GetInstanceIndex(this);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);

        PhxDestructionRegistry.NotifyDestroyed(this);
        SCENE?.DestroyInstance(this);
    }

    /// <summary>
    /// Nothing to restore: a finished beacon destroys its own instance.
    /// </summary>
    /// <remarks>
    /// A beacon is a timed call-in, not a fixture - it expires or is shot down
    /// and DestroyInstance follows either way, so there is no object left to
    /// bring back. A mission that wants another one calls it in again.
    /// </remarks>
    public void Restore()
    {
        if (!Finished) return;

        Debug.LogWarning($"[Lua] RespawnObject on beacon '{name}', which no longer " +
                         "exists. Call in a new beacon instead.");
    }

    /// <summary>Repair. See <see cref="IPhxDestructible.AddHealth"/>.</summary>
    /// <remarks>Nothing to repair once it is gone; the instance is destroyed with it.</remarks>
    public float AddHealth(float amount)
    {
        if (amount <= 0f || Finished) return 0f;

        float before = CurHealth.Get();
        float max = C != null ? C.MaxHealth.Get() : before;
        CurHealth.Set(Mathf.Min(before + amount, max));
        return CurHealth.Get() - before;
    }

    public void AddDamage(float damage, PhxPawnController instigator = null)
    {
        if (Finished) return;

        CurHealth.Set(CurHealth.Get() - damage);
        if (CurHealth.Get() > 0f) return;

        // Shot down before it fired: the objective it served does NOT complete,
        // so the expiry callback deliberately does not run here.
        Finished = true;
        StopEffect();

        PhxExplosionManager.AddExplosion(instigator, C.ExplosionName.Get() as PhxExplosionClass,
                                         transform.position, transform.rotation);

        int? objIdx = SCENE?.GetInstanceIndex(this);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillTeam, Team.Get(), objIdx);

        PhxDestructionRegistry.NotifyDestroyed(this);
        SCENE?.DestroyInstance(this);
    }

    void StopEffect()
    {
        if (ActiveEffect == null) return;

        ActiveEffect.SetLooping(false);
        ActiveEffect.Stop();
        ActiveEffect = null;
    }
}
