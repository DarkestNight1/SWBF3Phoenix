using UnityEngine;

/// <summary>
/// A placed proximity mine.
/// </summary>
/// <remarks>
/// The <c>mine</c> base class was not registered, so every mine on every map -
/// engineer-placed minefields, the authored ones the level designers scatter
/// around vehicle routes, and the MINE hint nodes that tell AI engineers where
/// to lay them - parsed correctly and then became inert scenery. Nothing
/// triggered, nothing exploded, nothing took damage.
///
/// Detonation is a radius test rather than a trigger volume: a mine's odf
/// carries its own model collision, and adding a trigger to imported geometry
/// disturbs physics for everything that touches it. The same reasoning
/// <see cref="PhxFlag"/> gives.
/// </remarks>
public class PhxMine : PhxInstance<PhxMine.ClassProperties>,
                        IPhxTickable, IPhxDamageableInstance, IPhxDestructible
{
    static PhxScene SCENE => PhxGame.GetScene();

    public class ClassProperties : PhxClass
    {
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        /// <summary>Explosion odf set off on trigger or on being shot.</summary>
        public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);

        /// <summary>Distance an enemy must come within to set it off.</summary>
        public PhxProp<float> TriggerRadius = new PhxProp<float>(3.0f);

        /// <summary>Delay after placement before it can trigger at all.</summary>
        public PhxProp<float> ArmedTime = new PhxProp<float>(1.5f);

        /// <summary>Seconds before it removes itself; 0 means it stays.</summary>
        public PhxProp<float> LifeTime = new PhxProp<float>(0f);

        /// <summary>Health, so mines can be shot off a doorway.</summary>
        public PhxProp<float> MaxHealth = new PhxProp<float>(10f);

        /// <summary>Effect shown while armed and waiting.</summary>
        public PhxProp<string> ArmedEffect = new PhxProp<string>(null);
    }

    public PhxProp<float> CurHealth = new PhxProp<float>(10f);

    /// <summary>
    /// Controller credited with anything the mine kills. Set by whatever
    /// places it; an authored map mine has none and scores to nobody.
    /// </summary>
    public PhxPawnController Owner;

    float ArmTimer;
    float LifeTimer;
    bool Detonated;

    // Reused so a minefield doesn't allocate a collider array per mine per
    // frame.
    static readonly Collider[] OverlapCache = new Collider[32];

    public override void Init()
    {
        Rigidbody body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;

        gameObject.layer = LayerMask.NameToLayer("BuildingAll");

        CurHealth.Set(C.MaxHealth.Get());
        ArmTimer = C.ArmedTime.Get();
        LifeTimer = C.LifeTime.Get();

        if (!string.IsNullOrEmpty(C.ArmedEffect.Get()))
        {
            SCENE?.EffectsManager.PlayEffectOnce(C.ArmedEffect.Get(), transform.position, transform.rotation);
        }

        PhxDestructionRegistry.Register(this);
    }

    public override void Destroy()
    {
        PhxDestructionRegistry.Unregister(this);
    }

    // ------------------------------------------------------ IPhxDestructible
    public PhxDestructibleKind DestructibleKind => PhxDestructibleKind.MissionObject;
    public GameObject GetGameObject() => gameObject;
    public string GetDestructibleName() => name;
    public int GetTeam() => Team.Get();
    public float GetHealth() => CurHealth.Get();
    public float GetMaxHealth() => C == null ? 0f : C.MaxHealth.Get();
    public bool IsDestroyed => Detonated;

    public void Tick(float deltaTime)
    {
        if (Detonated) return;

        if (ArmTimer > 0f)
        {
            ArmTimer -= deltaTime;
            return;
        }

        if (C.LifeTime.Get() > 0f)
        {
            LifeTimer -= deltaTime;
            if (LifeTimer <= 0f)
            {
                // Expiry is not a detonation: an expired mine is picked back
                // up, it does not blow up under whoever placed it.
                SCENE?.DestroyInstance(this);
                Detonated = true;
                return;
            }
        }

        if (FindVictim() != null)
        {
            Detonate();
        }
    }

    /// <summary>
    /// Nearest hostile pawn inside the trigger radius, or null.
    /// </summary>
    /// <remarks>
    /// Team 0 is "no team" and covers unowned map mines, which are hostile to
    /// everyone - that is what makes an authored minefield a hazard rather
    /// than decoration.
    /// </remarks>
    PhxInstance FindVictim()
    {
        int myTeam = Team.Get();
        int count = Physics.OverlapSphereNonAlloc(transform.position, C.TriggerRadius.Get(),
                                                  OverlapCache, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (coll == null) continue;

            PhxInstance inst = coll.GetComponentInParent<PhxInstance>();
            if (inst == null || inst == this) continue;

            // Only things that can be driven or walked set a mine off; props
            // and buildings do not.
            if (!(inst is IPhxControlableInstance)) continue;

            if (inst is PhxSoldier soldier && soldier.IsDead) continue;
            if (myTeam > 0 && inst.Team.Get() == myTeam) continue;

            return inst;
        }
        return null;
    }

    public void Detonate()
    {
        if (Detonated) return;
        Detonated = true;

        PhxExplosionManager.AddExplosion(Owner, C.ExplosionName.Get() as PhxExplosionClass,
                                         transform.position, transform.rotation);

        int? objIdx = SCENE?.GetInstanceIndex(this);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);

        PhxDestructionRegistry.NotifyDestroyed(this);
        SCENE?.DestroyInstance(this);
    }

    public void AddDamage(float damage)
    {
        if (Detonated) return;

        CurHealth.Set(CurHealth.Get() - damage);
        if (CurHealth.Get() <= 0f)
        {
            // Shooting a mine sets it off where it lies - which is how mines
            // are cleared, and why clearing one from close range is a bad idea.
            Detonate();
        }
    }
}
