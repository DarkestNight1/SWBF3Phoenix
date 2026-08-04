using System.Collections.Generic;
using UnityEngine;

using LibSWBF2.Utils;



public class PhxExplosionClass : PhxClass 
{
    public PhxProp<float> Damage = new PhxProp<float>(400f);
    public PhxProp<float> DamageRadiusInner = new PhxProp<float>(1f);
    public PhxProp<float> DamageRadiusOuter = new PhxProp<float>(3f);

    public PhxProp<float> Push = new PhxProp<float>(10f);
    public PhxProp<float> PushRadiusInner = new PhxProp<float>(4f);
    public PhxProp<float> PushRadiusOuter = new PhxProp<float>(4f);

    public PhxProp<float> Shake = new PhxProp<float>(.5f);
    public PhxProp<float> ShakeLength = new PhxProp<float>(.6f);
    public PhxProp<float> ShakeRadiusInner = new PhxProp<float>(8f);
    public PhxProp<float> ShakeRadiusOuter = new PhxProp<float>(12f);

    public PhxProp<string> Effect = new PhxProp<string>(null);
    public PhxProp<string> WaterEffect = new PhxProp<string>(null);

    public PhxProp<float> LifeSpan = new PhxProp<float>(3f);

    // -1 means "not set in the odf" so the legacy grouped scales can apply;
    // see PhxDamage.Resolve().
    public PhxProp<float> VehicleScale = new PhxProp<float>(-1f);
    public PhxProp<float> PersonScale = new PhxProp<float>(-1f);
    public PhxProp<float> DroidScale = new PhxProp<float>(-1f);
    public PhxProp<float> AnimalScale = new PhxProp<float>(-1f);
    public PhxProp<float> BuildingScale = new PhxProp<float>(-1f);

    // Legacy grouped scales (HealthScale -> person/animal/droid,
    // ArmorScale -> vehicle/building)
    public PhxProp<float> HealthScale = new PhxProp<float>(-1f);
    public PhxProp<float> ArmorScale = new PhxProp<float>(-1f);

    public PhxDamageScales GetDamageScales()
    {
        return PhxDamage.Resolve(PersonScale, AnimalScale, DroidScale,
                                 VehicleScale, BuildingScale,
                                 HealthScale, ArmorScale);
    }

    public PhxProp<Color> LightColor = new PhxProp<Color>(Color.white);
    public PhxProp<float> LightRadius = new PhxProp<float>(7f);
    public PhxProp<float> LightDuration = new PhxProp<float>(1f);    
}



public static class PhxExplosionManager
{
    static PhxGame Game => PhxGame.Instance;
    static PhxScene Scene => PhxGame.GetScene();


    // Reused so a burst of explosions doesn't allocate every frame
    static readonly Collider[] OverlapCache = new Collider[256];

    public static void AddExplosion(PhxPawnController Originator, PhxExplosionClass Exp, Vector3 Position, Quaternion Rotation)
    {
        if (Exp == null)
            return;


        // Play effect
        Scene.EffectsManager.PlayEffectOnce(Exp.Effect.Get(), Position, Rotation);

        // Damage and push were never applied here, so every explosive weapon
        // in the game (grenades, rockets, detpacks, vehicle deaths) did
        // nothing at all. Both fall off linearly between their inner and
        // outer radius per the explosion documentation: full effect inside
        // the inner radius, none beyond the outer.
        float damageOuter = Exp.DamageRadiusOuter;
        float pushOuter = Exp.PushRadiusOuter;
        float radius = Mathf.Max(damageOuter, pushOuter);
        if (radius <= 0f) return;

        PhxDamageScales scales = Exp.GetDamageScales();
        float maxDamage = Exp.Damage;
        float maxPush = Exp.Push;

        int count = Physics.OverlapSphereNonAlloc(Position, radius, OverlapCache,
                                                  ~0, QueryTriggerInteraction.Ignore);

        // One collider per instance: a soldier/vehicle has many colliders and
        // must not be damaged once per limb.
        HashSet<PhxInstance> alreadyHit = new HashSet<PhxInstance>();

        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (coll == null) continue;

            PhxInstance inst = coll.GetComponentInParent<PhxInstance>();
            if (inst != null && !alreadyHit.Add(inst))
            {
                continue;   // this object already took the blast
            }

            Vector3 targetPos = inst != null ? inst.transform.position : coll.bounds.center;
            float dist = Vector3.Distance(Position, targetPos);

            // ---- damage ----
            if (maxDamage > 0f && dist <= damageOuter)
            {
                float falloff = PhxDamage.RadialFalloff(dist, Exp.DamageRadiusInner, damageOuter);
                if (falloff > 0f)
                {
                    PhxDamage.ApplyToCollider(coll, maxDamage * falloff, scales, targetPos);
                }
            }

            // ---- push ----
            if (maxPush > 0f && dist <= pushOuter)
            {
                float falloff = PhxDamage.RadialFalloff(dist, Exp.PushRadiusInner, pushOuter);
                Rigidbody body = coll.attachedRigidbody;
                if (falloff > 0f && body != null && !body.isKinematic)
                {
                    Vector3 dir = targetPos - Position;
                    if (dir.sqrMagnitude < 0.0001f) dir = Vector3.up;
                    body.AddForce(dir.normalized * (maxPush * falloff), ForceMode.Impulse);
                }
            }
        }
    }
}
