using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

using LibSWBF2.Enums;


public class PhxMissileClass : PhxOrdnanceClass
{
    public PhxProp<float> LightRadius = new PhxProp<float>(3f);
    public PhxProp<Color> LightColor = new PhxProp<Color>(Color.white);

    public PhxProp<float> MinSpeed = new PhxProp<float>(10f);
    public PhxProp<float> Acceleration = new PhxProp<float>(50f);
    public PhxProp<float> Rebound = new PhxProp<float>(0f);
    public PhxProp<float> TurnRate = new PhxProp<float>(0f);

    public PhxProp<float> Velocity = new PhxProp<float>(100f);

    public PhxProp<string> TrailEffect = new PhxProp<string>(null);
    public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);

    public PhxProp<PhxClass> ExplosionImpact = new PhxProp<PhxClass>(null);
    public PhxProp<PhxClass> ExplosionExpire = new PhxProp<PhxClass>(null);    
}



[RequireComponent(typeof(Rigidbody), typeof(Light))]
public class PhxMissile : PhxOrdnance, IPhxTickablePhysics
{
    // for heatseeking
    PhxInstance Target; 

    PhxMissileClass MissileClass;
   
    protected Rigidbody Body;

    /// <summary>Set once Init has run; OnEnable fires before it on a fresh pool object.</summary>
    bool InertiaApplied;

    /// <summary>
    /// Pin the inertia tensor so PhysX never derives one from the collider.
    /// </summary>
    /// <remarks>
    /// An explicit tensor is what stops PhysX computing its own, but the
    /// override does not survive the body being disabled - which is exactly
    /// what pooling does on every impact. Re-asserting it on enable is the
    /// difference between a projectile that recycles cleanly and one that
    /// logs a PhysX error every time it is reused.
    /// </remarks>
    void ApplyInertia()
    {
        if (Body == null) return;

        Body.centerOfMass = Vector3.zero;
        Body.inertiaTensor = Vector3.one;
        Body.inertiaTensorRotation = Quaternion.identity;
    }

    void OnEnable()
    {
        if (InertiaApplied) ApplyInertia();
    }
    Light Light;

    protected List<Collider> Colliders;
    protected List<Collider> IgnoredColliders;


    protected PhxEffect TrailEffect;

    protected float TimeAlive;


    public override void Init()
    {
        gameObject.layer = LayerMask.NameToLayer("OrdnanceAll");

        Body = GetComponent<Rigidbody>();
        InertiaApplied = true;
        Body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;

        Light = GetComponent<Light>();
        Light.type = LightType.Point;

        MissileClass = OrdnanceClass as PhxMissileClass;

        Light.color = MissileClass.LightColor;
        Light.range = MissileClass.LightRadius;
        Light.intensity = 3f;

        Body.useGravity = false;
        Body.linearDamping = 0f;

        // Not 1e-10.
        //
        // A missile is driven by its velocity, not by forces, so the mass only
        // has to be small enough that hitting something does not throw it
        // around. At 1e-10 the inertia tensor PhysX derives from it underflows
        // to zero, and every time the pool re-enables this body PhysX rejects
        // it with "Inertia tensor must be larger than zero in all
        // coordinates" - once per missile impact, for the whole match.
        Body.mass = 0.01f;
        Body.angularDamping = 0f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        SWBFModel Mapping = ModelLoader.Instance.GetModelMapping(gameObject, MissileClass.GeometryName.Get());
        if (Mapping != null)
        {
            Mapping.ConvexifyMeshColliders();
            Mapping.SetColliderMaskAll(ECollisionMaskFlags.Ordnance);
            Mapping.GameRole = SWBFGameRole.Ordnance;
            Mapping.SetColliderLayerFromMaskAll();
            Colliders = Mapping.GetCollidersByLayer(ECollisionMaskFlags.Ordnance); 
        }
        else 
        {
            Colliders = new List<Collider>();
            Colliders.Add(GetComponent<SphereCollider>());
        }

        // After the colliders, not before.
        //
        // Assigning inertiaTensor tells PhysX to stop deriving it, but adding
        // or enabling a collider makes it derive the body's mass properties
        // again and throw the explicit value away. This ran before the model's
        // colliders were built, so whatever the convexified ordnance mesh
        // happened to produce won - and for a missile that is a long thin
        // sliver, which degenerates. Applying last makes the explicit tensor
        // the final word, which is what OnEnable then restores on every reuse.
        ApplyInertia();

        TrailEffect = SCENE.EffectsManager.LendEffect(MissileClass.TrailEffect.Get());
        if (TrailEffect != null)
        {
            TrailEffect.SetLooping();
            TrailEffect.SetParent(transform);
            TrailEffect.SetLocalTransform(Vector3.zero, Quaternion.identity);
            TrailEffect.SetDynamic(true);
        } 
    }

    public override void Setup(IPhxWeapon OriginatorWeapon, Vector3 Position, Quaternion Rotation)
    {
        OwnerWeapon = OriginatorWeapon;
        Owner = OwnerWeapon.GetOwnerController();

        // Can be null of course
        // Target = OwnerWeapon.GetLockedTarget();

        gameObject.SetActive(true);

        //OwnerWeapon.GetFirePoint(out Vector3 Pos, out Quaternion Rot);
        transform.position = Position;
        transform.rotation = Rotation;

        Body.linearVelocity = transform.forward * MissileClass.MinSpeed.Get();

        // Will need to unignore these in Release, but how to check
        // if they still exist?  Points to per-weapon pools
        // so this can be done easily when weapon is reused or detached, etc
        IgnoredColliders = OriginatorWeapon.GetIgnoredColliders();
        foreach (Collider IgnoredCollider in IgnoredColliders)
        {
            foreach (Collider MissileCollider in Colliders)
            {
                //Debug.LogFormat("Ignoring collider objects: {0}, {1}", IgnoredCollider.gameObject.name, MissileCollider.gameObject.name);
                Physics.IgnoreCollision(MissileCollider, IgnoredCollider);
            }                   
        }

        TrailEffect?.Play();

        TimeAlive = 0f;
    }

    public override void Destroy()
    {
        //IgnoredColliders.Clear();

        Owner = null;
        Target = null;

        TrailEffect?.Stop();
    }

    /// <summary>
    /// Advance the fuse. Returns true if it expired and the ordnance is gone.
    /// </summary>
    /// <remarks>
    /// Separate from TickPhysics so a subclass that has stopped simulating can
    /// still keep its clock running - a grenade stuck to a wall must not stop
    /// counting down just because it stopped moving.
    /// </remarks>
    protected bool TickLifespan(float deltaTime)
    {
        TimeAlive += deltaTime;
        if (TimeAlive <= MissileClass.LifeSpan) return false;

        OnLifespanExpired();
        return true;
    }

    /// <summary>
    /// What to do when the ordnance runs out of life without hitting anything.
    /// </summary>
    /// <remarks>
    /// Default is to vanish, which is right for a missile or shell that flew
    /// past its target: the shot missed and nothing should happen where it
    /// happens to run out. A grenade is the opposite case - the fuse IS the
    /// weapon - so PhxSticky overrides this.
    /// </remarks>
    protected virtual void OnLifespanExpired()
    {
        ParentPool.Free(this);
    }

    public virtual void TickPhysics(float deltaTime)
    {
        if (TickLifespan(deltaTime))
        {
            return;
        }

        // Don't think missiles actually use gravity, shells do though
        // Body.AddForce(9.8f * MissileClass.Gravity * Vector3.down, ForceMode.Acceleration);

        if (Vector3.Magnitude(Body.linearVelocity) > MissileClass.Velocity)
        {
            Body.linearVelocity = MissileClass.Velocity * Vector3.Normalize(Body.linearVelocity);
        }
        else 
        {
            Body.AddForce(MissileClass.Acceleration * transform.forward, ForceMode.Acceleration);
        }

        if (Target != null)
        {
            // Handle Turn towards Target
        }
    }


    /// <summary>
    /// Unity's collision message. Deliberately the only one in the hierarchy.
    /// </summary>
    /// <remarks>
    /// A subclass wanting different impact behaviour overrides OnImpact rather
    /// than declaring its own OnCollisionEnter. Unity dispatches these by name
    /// through the type, so a `new` method in a subclass leaves it ambiguous
    /// which of the two the engine will call - and for a grenade the wrong
    /// answer is "the base one", which detonates it against the first surface
    /// it touches instead of on its fuse.
    /// </remarks>
    void OnCollisionEnter(Collision coll)
    {
        if (!gameObject.activeSelf) return;
        OnImpact(coll);
    }

    protected virtual void OnImpact(Collision coll)
    {
        ContactPoint Contact = coll.GetContact(0);

        /*
        EXPLOSION

        TODO: Figure out what explosion param to use when...
        */

        PhxClass Exp = MissileClass.ExplosionImpact.Get();
        if (Exp == null)
        {
            Exp = MissileClass.ExplosionName.Get();
        }

        // Owner is the controller that fired this, set in Setup. Passing null
        // here threw the kill credit away on every rocket and shell in the
        // game while the field sat populated on the same object.
        PhxExplosionManager.AddExplosion(Owner, Exp as PhxExplosionClass, Contact.point, Quaternion.identity);

        ParentPool.Free(this);
    }
}
