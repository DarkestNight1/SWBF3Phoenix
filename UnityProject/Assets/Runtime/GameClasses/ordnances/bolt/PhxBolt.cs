using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;


public class PhxBoltClass : PhxOrdnanceClass
{
    public PhxProp<Texture2D> LaserTexture = new PhxProp<Texture2D>(null);
    public PhxProp<Color> LaserGlowColor = new PhxProp<Color>(Color.red);
    public PhxProp<Color> LightColor = new PhxProp<Color>(Color.red);
    public PhxProp<float> LightRadius = new PhxProp<float>(1f);
    public PhxProp<float> LaserLength = new PhxProp<float>(1f);
    public PhxProp<float> LaserWidth = new PhxProp<float>(.05f);
    public PhxProp<float> Velocity = new PhxProp<float>(1f);

    public PhxProp<string> ImpactEffectStatic = new PhxProp<string>(null);
    public PhxProp<string> ImpactEffectRigid = new PhxProp<string>(null);
    public PhxProp<string> ImpactEffectSoft = new PhxProp<string>(null);
    public PhxProp<string> ImpactEffectTerrain = new PhxProp<string>(null);
    public PhxProp<string> ImpactEffectWater = new PhxProp<string>(null);
    public PhxProp<string> ImpactEffectShield = new PhxProp<string>(null);

    public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);
}


[RequireComponent(typeof(LineRenderer), typeof(Rigidbody), typeof(Light))]
public class PhxBolt : PhxOrdnance
{

    PhxBoltClass BoltClass;

    public BoxCollider Coll { get; private set; }
    public Action<PhxBolt, Collision> OnHit;

    float EmissionIntensity = Mathf.Pow(2f, 25f);

    Rigidbody Body;

    Light Light;
    HDAdditionalLightData HDLightData;

    LineRenderer Renderer;



    public override void Setup(IPhxWeapon Originator, Vector3 Pos, Quaternion Rot)
    {
        gameObject.SetActive(true);

        // Remember the weapon that fired this, which is where the kill credit
        // comes from at impact.
        //
        // This was never assigned. The comment further down claimed it was
        // "set in Setup()" and it was not - the only write in the file is in
        // TryDeflect, when a saber takes the bolt over. So the instigator
        // lookup at impact was always null, and fixing the weapon end alone
        // would have changed nothing.
        //
        // It also closes a pooling leak: with no assignment here, a bolt that
        // had once been deflected kept the saber as its owner into every later
        // reuse, since Destroy() clears only the ignored colliders.
        OwnerWeapon = Originator;

        //Originator.GetFirePoint(out Vector3 Pos, out Quaternion Rot);
        
        Body.transform.position = Pos;
        Body.transform.rotation = Rot;
        Body.velocity = Body.transform.forward * BoltClass.Velocity;

        // A bolt must not hit the shooter it just left, so the firer's own
        // colliders are ignored - but bolts are POOLED, and Physics.IgnoreCollision
        // pairs persist on the collider until they are explicitly cleared.
        // Leaving them set meant every recycled bolt accumulated permanent
        // ignore pairs with everyone who had ever fired it: after a minute of
        // combat a large share of bolts passed harmlessly through the player
        // and through AI, so shots stopped registering as kills in both
        // directions. Release the previous set before taking a new one.
        ClearIgnoredColliders();

        List<Collider> ignored = Originator.GetIgnoredColliders();
        for (int i = 0; i < ignored.Count; ++i)
        {
            Collider c = ignored[i];
            if (c == null) continue;
            Physics.IgnoreCollision(c, Coll, true);
            IgnoredColliders.Add(c);
        }
    }

    // Colliders this bolt is currently ignoring, so the pairs can be undone
    // when it is recycled.
    readonly List<Collider> IgnoredColliders = new List<Collider>();

    void ClearIgnoredColliders()
    {
        for (int i = 0; i < IgnoredColliders.Count; ++i)
        {
            Collider c = IgnoredColliders[i];
            // The shooter may have died and been destroyed since we fired; a
            // destroyed collider has no pair left to undo.
            if (c == null || Coll == null) continue;
            Physics.IgnoreCollision(c, Coll, false);
        }
        IgnoredColliders.Clear();
    }



    public override void Init()
    {
        Coll = GetComponent<BoxCollider>();
        Body = GetComponent<Rigidbody>();
        Light = GetComponent<Light>();
        HDLightData = GetComponent<HDAdditionalLightData>();
        Renderer = GetComponent<LineRenderer>();

        gameObject.layer = LayerMask.NameToLayer("OrdnanceAll");

        BoltClass = OrdnanceClass as PhxBoltClass;

        HDLightData.color = BoltClass.LightColor;

        Renderer.startWidth = BoltClass.LaserWidth / 2f;
        Renderer.endWidth = BoltClass.LaserWidth / 2f;
        Renderer.SetPosition(1, new Vector3(0f, 0f, BoltClass.LaserLength * 2f));

        Renderer.material.SetTexture("_UnlitColorMap", BoltClass.LaserTexture);
        Renderer.material.SetTexture("_EmissiveColorMap", BoltClass.LaserTexture);
        Renderer.material.SetColor("_EmissiveColor", BoltClass.LightColor.Get() * EmissionIntensity); 
    }

    public override void Destroy()
    {
        ClearIgnoredColliders();
    }

    /// <summary>
    /// Hand the bolt to a saber wielder if the thing it hit is one and it
    /// chooses to take it. A deflected bolt keeps flying on a new heading with
    /// the deflector credited for anything it goes on to kill.
    /// </summary>
    bool TryDeflect(Collision coll, PhxPawnController instigator)
    {
        PhxSaberDeflect deflector = PhxSaberDeflect.Find(coll.collider);
        if (deflector == null) return false;

        Vector3 direction = Body.velocity.sqrMagnitude > 0.0001f
            ? Body.velocity.normalized
            : transform.forward;

        Vector3 shooterPosition = instigator?.Pawn?.GetInstance() != null
            ? instigator.Pawn.GetInstance().transform.position
            : transform.position - direction * 20f;

        Vector3? deflected = deflector.TryDeflect(transform.position, direction, shooterPosition);
        if (!deflected.HasValue) return false;

        // Reassign ownership: a returned bolt is the deflector's shot now, so
        // the kill and the friendly-fire check both follow the blade.
        PhxSoldier wielder = deflector.GetComponentInParent<PhxSoldier>();
        IPhxWeapon saber = wielder?.GetPrimaryWeapon();
        if (saber != null)
        {
            OwnerWeapon = saber;
        }

        Body.transform.rotation = Quaternion.LookRotation(deflected.Value);
        Body.velocity = deflected.Value * BoltClass.Velocity;

        SCENE.EffectsManager.PlayEffectOnce(BoltClass.ImpactEffectShield.Get(),
                                            transform.position,
                                            Quaternion.LookRotation(deflected.Value));
        return true;
    }

    void OnCollisionEnter(Collision coll)
    {
        // Damage is the ordnance odf's MaxDamage scaled by the scale matching
        // the TARGET'S HealthType (person/animal/droid/vehicle/building) -
        // not by what C# type it happens to be. See PhxDamage.
        ContactPoint contact = coll.GetContact(0);

        // The firing controller, so the kill is credited to whoever pulled the
        // trigger. A weapon with no controller - a map turret firing on its
        // own - leaves this null, which ReportKill treats as unattributed.
        PhxPawnController instigator = OwnerWeapon?.GetOwnerController();

        // A saber wielder gets first refusal on the bolt. Asked here rather
        // than anywhere in the hero code because this is the one place a bolt
        // and the thing it is about to hit are both known - and a deflected
        // bolt must not have damaged anything on the way past.
        if (TryDeflect(coll, instigator))
        {
            return;
        }

        // Own team, and friendly fire off: the bolt still stops here and still
        // marks the wall, it just does no damage. Passing through would be
        // worse - you would shoot your squadmates' cover away from behind them.
        if (PhxDamage.BlocksDirectFire(instigator, coll.collider))
        {
            ParentPool.Free(this);
            return;
        }

        PhxDamage.ApplyToCollider(coll.collider, BoltClass.MaxDamage,
                                  BoltClass.GetDamageScales(), contact.point,
                                  isSaber: false, instigator: instigator);

        if (gameObject.activeSelf)
        {
            OnHit?.Invoke(this, coll);

            ContactPoint Point = coll.GetContact(0);

            Vector3 Pos = Point.point;
            Quaternion Rot = Quaternion.LookRotation(Point.normal, Vector3.up);

            SCENE.EffectsManager.PlayEffectOnce(BoltClass.ImpactEffectStatic.Get(), Pos, Rot);

            PhxExplosionManager.AddExplosion(instigator, BoltClass.ExplosionName.Get() as PhxExplosionClass, Pos, Rot);
            if (coll.gameObject.layer == LayerMask.NameToLayer("TerrainAll"))
            {
                SCENE.EffectsManager.PlayEffectOnce(BoltClass.ImpactEffectTerrain.Get(), Point.point, Quaternion.identity);
            }
            else if (coll.gameObject.layer == LayerMask.NameToLayer("BuildingAll") ||
                    coll.gameObject.layer == LayerMask.NameToLayer("BuildingOrdnance"))
            {
                SCENE.EffectsManager.PlayEffectOnce(BoltClass.ImpactEffectStatic.Get(), Point.point, Quaternion.identity);
            }
            else if (coll.gameObject.layer == LayerMask.NameToLayer("SoldierAll"))
            {
                SCENE.EffectsManager.PlayEffectOnce(BoltClass.ImpactEffectSoft.Get(), Point.point, Quaternion.identity);
            }
            else 
            {
                SCENE.EffectsManager.PlayEffectOnce(BoltClass.ImpactEffectRigid.Get(), Point.point, Quaternion.identity);                
            }


            // Surface response on top of the ordnance's own authored effects:
            // the flash that lights the wall around the hit, the scorch mark,
            // and whatever the surface itself does about being hit (snow
            // displacement, water ripples). The odf's effect above stays the
            // artistic answer - this adds only what the 2005 format had no way
            // to express.
            BFImpactResponse.Play(Point.point, Point.normal,
                                  Body.velocity.sqrMagnitude > 0.0001f
                                      ? Body.velocity.normalized
                                      : transform.forward,
                                  BFSurfaceQuery.Resolve(coll.collider, Point.point),
                                  scale: 1f, instigator: gameObject,
                                  playSurfaceParticles: false,
                                  segment: BFSegmentIdentity.Of(coll.collider));

            ParentPool.Free(this);
        }
    }
}
