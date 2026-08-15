using UnityEngine;

/// <summary>
/// A thrown explosive: bounces, may stick, and detonates on a fuse.
/// </summary>
/// <remarks>
/// Grenades were mapped straight onto PhxShellClass, which is an artillery
/// shell - it explodes on the first thing it touches. So a thermal detonator
/// hitting the floor at the thrower's feet went off there, and one that missed
/// simply vanished. What a grenade does is bounce, settle, and blow up a moment
/// later, and every parameter for that is in the odf and was being ignored.
///
/// Measured from com_weap_inf_thermaldetonator_ord:
///
///   lifespan 0.6   velocity 25   gravity 1.25   rebound 0.2   friction 2.0
///   stickvehicle 1  stickbuilding 1
///   stickperson 0   stickanimal 0  stickdroid 0  stickterrain 0  stickbuildingdead 0
///   impacteffect com_sfx_explosion_lg
///   collisionothersound com_weap_inf_grenade_bounce
///
/// Which reads: it sticks to hardware but not to people or the ground, it keeps
/// a fifth of its speed off a surface, it is slowed hard by contact, and it
/// detonates on its own clock rather than on impact.
/// </remarks>
public class PhxStickyClass : PhxShellClass
{
    /// <summary>Fraction of speed kept when it bounces.</summary>
    public PhxProp<float> Rebound = new PhxProp<float>(0.2f);

    /// <summary>How hard contact slows it. Higher settles sooner.</summary>
    public PhxProp<float> Friction = new PhxProp<float>(2f);

    public PhxProp<bool> StickPerson = new PhxProp<bool>(false);
    public PhxProp<bool> StickAnimal = new PhxProp<bool>(false);
    public PhxProp<bool> StickDroid = new PhxProp<bool>(false);
    public PhxProp<bool> StickVehicle = new PhxProp<bool>(true);
    public PhxProp<bool> StickBuilding = new PhxProp<bool>(true);
    public PhxProp<bool> StickBuildingDead = new PhxProp<bool>(false);
    public PhxProp<bool> StickTerrain = new PhxProp<bool>(false);

    /// <summary>Played when it strikes something without sticking.</summary>
    public PhxProp<string> CollisionOtherSound = new PhxProp<string>("");
}

public class PhxSticky : PhxShell
{
    PhxStickyClass StickyClass;

    /// <summary>Set once it has attached; it stops simulating from then on.</summary>
    bool Stuck;

    AudioSource BounceAudio;

    public override void Init()
    {
        base.Init();
        StickyClass = OrdnanceClass as PhxStickyClass;
    }

    public override void Setup(IPhxWeapon OriginatorWeapon, Vector3 Position, Quaternion Rotation)
    {
        base.Setup(OriginatorWeapon, Position, Rotation);

        // Pooled: every field that survived the previous throw has to be put
        // back, or the second grenade a player throws arrives already stuck.
        Stuck = false;
        Body.isKinematic = false;
        transform.SetParent(null, true);
    }

    public override void TickPhysics(float deltaTime)
    {
        if (Stuck) return;      // attached: no gravity, no drag, no motion

        base.TickPhysics(deltaTime);

        // Contact friction, applied as damping rather than through a physic
        // material so it matches the odf's single scalar and does not depend on
        // what the surface happens to be made of.
        if (StickyClass != null && StickyClass.Friction > 0f && Body.velocity.sqrMagnitude > 0.0001f)
        {
            Body.velocity = Vector3.MoveTowards(Body.velocity, Vector3.zero,
                                                StickyClass.Friction * deltaTime);
        }
    }

    /// <summary>
    /// Bounce or stick - but do not detonate. The fuse owns that.
    /// </summary>
    /// <remarks>
    /// PhxMissile explodes in OnCollisionEnter, which is right for a shell and
    /// wrong for a grenade, so this deliberately does not call up to it. The
    /// base class already counts TimeAlive against LifeSpan every tick and
    /// detonates there, which is exactly the fuse behaviour wanted.
    /// </remarks>
    new void OnCollisionEnter(Collision collision)
    {
        if (Stuck || StickyClass == null) return;

        if (ShouldStickTo(collision.collider))
        {
            Stick(collision);
            return;
        }

        // Keep a fraction of the incoming speed, reflected about the surface.
        ContactPoint contact = collision.GetContact(0);
        Vector3 reflected = Vector3.Reflect(Body.velocity, contact.normal);
        Body.velocity = reflected * Mathf.Clamp01(StickyClass.Rebound);

        PlayBounceSound();
    }

    /// <summary>Whether the thing it hit is a kind this ordnance adheres to.</summary>
    /// <remarks>
    /// Decided by layer, which is what the importer actually assigns, rather
    /// than by looking for game classes on the collider - a building's collision
    /// node carries no component of its own.
    /// </remarks>
    bool ShouldStickTo(Collider other)
    {
        if (other == null) return false;

        int layer = other.gameObject.layer;

        if (layer == LayerMask.NameToLayer("SoldierAll"))
        {
            return StickyClass.StickPerson || StickyClass.StickDroid || StickyClass.StickAnimal;
        }
        if (layer == LayerMask.NameToLayer("TerrainAll"))
        {
            return StickyClass.StickTerrain;
        }
        if (layer == LayerMask.NameToLayer("VehicleAll") ||
            layer == LayerMask.NameToLayer("VehicleOrdnance") ||
            layer == LayerMask.NameToLayer("VehicleSoldier"))
        {
            return StickyClass.StickVehicle;
        }
        if (layer == LayerMask.NameToLayer("BuildingAll") ||
            layer == LayerMask.NameToLayer("BuildingOrdnance") ||
            layer == LayerMask.NameToLayer("BuildingSoldier"))
        {
            return StickyClass.StickBuilding;
        }

        return false;
    }

    void Stick(Collision collision)
    {
        Stuck = true;
        Body.velocity = Vector3.zero;
        Body.angularVelocity = Vector3.zero;

        // Kinematic rather than frozen constraints, so it rides a vehicle that
        // drives off with it rather than hanging in the air where it landed.
        Body.isKinematic = true;
        transform.SetParent(collision.collider.transform, true);
    }

    void PlayBounceSound()
    {
        string sound = StickyClass.CollisionOtherSound.Get();
        if (string.IsNullOrEmpty(sound) || SoundLoader.Instance == null) return;

        if (BounceAudio == null)
        {
            BounceAudio = gameObject.AddComponent<AudioSource>();
            BounceAudio.playOnAwake = false;
            BounceAudio.spatialBlend = 1f;
            BounceAudio.rolloffMode = AudioRolloffMode.Logarithmic;
            BounceAudio.minDistance = 3f;
            BounceAudio.maxDistance = 60f;
            BounceAudio.clip = SoundLoader.Instance.LoadSound(sound);
        }

        if (BounceAudio.clip != null)
        {
            BounceAudio.Play();
        }
    }
}
