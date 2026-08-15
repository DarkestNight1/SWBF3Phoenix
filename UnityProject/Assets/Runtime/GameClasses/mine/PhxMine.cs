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

        // Three explosions, not one. Measured from the stock classes, which
        // declare ExplosionTrigger / ExplosionExpire / ExplosionDeath and
        // never declare ExplosionName - so the single ExplosionName this used
        // to bind was a silent null on every mine in the game, and mines went
        // off producing nothing at all. That is exactly the PhxProp failure
        // mode: a name that does not match binds quietly and reads as zero.

        /// <summary>Explosion when it goes off the way it is meant to.</summary>
        public PhxProp<PhxClass> ExplosionTrigger = new PhxProp<PhxClass>(null);

        /// <summary>Explosion when its lifespan runs out. Usually a smaller "destroyed" one.</summary>
        public PhxProp<PhxClass> ExplosionExpire = new PhxProp<PhxClass>(null);

        /// <summary>Explosion when it is shot off a wall.</summary>
        public PhxProp<PhxClass> ExplosionDeath = new PhxProp<PhxClass>(null);

        /// <summary>Legacy single-explosion spelling, kept for mods that use it.</summary>
        public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);

        /// <summary>
        /// Distance an enemy must come within to set it off. 0 means it has no
        /// proximity trigger at all.
        /// </summary>
        /// <remarks>
        /// Defaulted to 3 m, which is a trigger the data never asked for: the
        /// stock landmine declares 1.0 and a detpack declares nothing, because
        /// a detpack is not proximity-triggered. Anything that wants a radius
        /// states one.
        /// </remarks>
        public PhxProp<float> TriggerRadius = new PhxProp<float>(0f);

        /// <summary>Goes off on being touched as well as on proximity.</summary>
        public PhxProp<bool> TriggerContact = new PhxProp<bool>(false);

        /// <summary>Seconds it stays before removing itself; 0 means it stays.</summary>
        /// <remarks>
        /// LifeSpan is the spelling the odfs use - LifeTime bound nothing, so
        /// no mine has ever expired. Stock mines and detpacks both declare 60.
        /// </remarks>
        public PhxProp<float> LifeSpan = new PhxProp<float>(0f);


        /// <summary>Delay after placement before it can trigger at all.</summary>
        public PhxProp<float> ArmedTime = new PhxProp<float>(1.5f);

        /// <summary>Health, so mines can be shot off a doorway.</summary>
        public PhxProp<float> MaxHealth = new PhxProp<float>(10f);

        /// <summary>Effect shown while armed and waiting.</summary>
        public PhxProp<string> ArmedEffect = new PhxProp<string>(null);

        /// <summary>Blinking light and its colour, the "there is a mine here" cue.</summary>
        public PhxProp<string> TrailEffect = new PhxProp<string>(null);
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
        LifeTimer = C.LifeSpan.Get();

        string armedEffect = string.IsNullOrEmpty(C.ArmedEffect.Get()) ? C.TrailEffect.Get() : C.ArmedEffect.Get();
        if (!string.IsNullOrEmpty(armedEffect))
        {
            SCENE?.EffectsManager.PlayEffectOnce(armedEffect, transform.position, transform.rotation);
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

        if (C.LifeSpan.Get() > 0f)
        {
            LifeTimer -= deltaTime;
            if (LifeTimer <= 0f)
            {
                // Expiry is its own ending with its own explosion - the stock
                // classes name a "destroyed" one for it, distinct from the
                // full blast. Which is right: an expired charge fizzles, it
                // does not level the room it was left in.
                Detonate(C.ExplosionExpire.Get() as PhxExplosionClass);
                return;
            }
        }

        // No radius and no contact trigger means it is not a proximity mine at
        // all - a detpack waits to be set off by whoever placed it. Skipping
        // the overlap test also keeps a pile of detpacks from costing a sphere
        // query each per frame.
        if (C.TriggerRadius.Get() <= 0f && !C.TriggerContact) return;

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
            if (PhxDamage.IsFriendly(myTeam, inst)) continue;

            return inst;
        }
        return null;
    }

    /// <summary>Set it off, using the explosion for the way it ended.</summary>
    public void Detonate(PhxExplosionClass explosion = null)
    {
        if (Detonated) return;
        Detonated = true;

        // ExplosionName is the legacy spelling and no stock class declares it,
        // so it is the last fallback rather than the only option.
        if (explosion == null)
        {
            explosion = (C.ExplosionTrigger.Get() ?? C.ExplosionName.Get()) as PhxExplosionClass;
        }

        PhxExplosionManager.AddExplosion(Owner, explosion, transform.position, transform.rotation);

        int? objIdx = SCENE?.GetInstanceIndex(this);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);

        PhxDestructionRegistry.NotifyDestroyed(this);
        SCENE?.DestroyInstance(this);
    }

    /// <summary>
    /// Nothing to restore: a detonated mine destroys its own instance.
    /// </summary>
    /// <remarks>
    /// Detonate calls DestroyInstance, so by the time anything could ask for a
    /// restore the object is gone and the pointer a script held is stale.
    /// Re-arming would mean spawning a fresh mine, which is a different thing
    /// with a different owner - so this reports rather than silently doing
    /// nothing, and a mission that wants a minefield back lays one.
    /// </remarks>
    public void Restore()
    {
        if (!Detonated) return;

        Debug.LogWarning($"[Lua] RespawnObject on mine '{name}', which was destroyed " +
                         "when it detonated. Spawn a new mine instead.");
    }

    public void AddDamage(float damage, PhxPawnController instigator = null)
    {
        if (Detonated) return;

        CurHealth.Set(CurHealth.Get() - damage);
        if (CurHealth.Get() <= 0f)
        {
            // Shooting a mine sets it off where it lies - which is how mines
            // are cleared, and why clearing one from close range is a bad idea.
            // ExplosionDeath is the smaller blast the data names for being
            // shot off, not the full trigger explosion - clearing a mine at
            // range should not be as lethal as stepping on it.
            Detonate((C.ExplosionDeath.Get() ?? C.ExplosionTrigger.Get()) as PhxExplosionClass);
        }
    }
}
