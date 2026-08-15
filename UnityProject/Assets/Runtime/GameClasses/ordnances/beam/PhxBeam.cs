using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;


public class PhxBeamClass : PhxOrdnanceClass
{
    public PhxProp<float> LaserWidth = new PhxProp<float>(5f);
    public PhxProp<Texture2D> LaserTexture = new PhxProp<Texture2D>(null);
    public PhxProp<Color> LightColor = new PhxProp<Color>(Color.red);

    public PhxProp<float> Range = new PhxProp<float>(100f);

    public PhxProp<float> Gravity = new PhxProp<float>(0f);
    public PhxProp<float> Rebound = new PhxProp<float>(0f);

    public PhxProp<bool> PassThrough = new PhxProp<bool>(false);

    public PhxProp<string> ImpactEffect = new PhxProp<string>(null);
}



[RequireComponent(typeof(LineRenderer), typeof(Light))]
public class PhxBeam : PhxOrdnance, IPhxTickable
{
    static int BeamMask;

    Transform BeamRoot;

    PhxBeamClass BeamClass;
   
    LineRenderer Renderer;

    Light Light;
    HDAdditionalLightData HDLightData;


    List<Collider> IgnoredColliders;

    /// <summary>Damage is dealt once per shot, not once per frame. See Tick.</summary>
    bool DamageApplied;

    // Reused so the per-frame raycast does not allocate.
    static readonly RaycastHit[] HitCache = new RaycastHit[16];
    public override void Init()
    {
        Light = GetComponent<Light>();
        HDLightData = GetComponent<HDAdditionalLightData>();

        Renderer = GetComponent<LineRenderer>();

        BeamMask = (1 << LayerMask.NameToLayer("SoldierAll")) |
                    (1 << LayerMask.NameToLayer("TerrainAll")) |
                    (1 << LayerMask.NameToLayer("VehicleAll")) |
                    (1 << LayerMask.NameToLayer("VehicleOrdnance")) |
                    (1 << LayerMask.NameToLayer("BuildingAll")) |
                    (1 << LayerMask.NameToLayer("BuildingOrdnance"));

        
        BeamClass = OrdnanceClass as PhxBeamClass;
        
        HDLightData.color = BeamClass.LightColor;
        
        Renderer.startWidth = BeamClass.LaserWidth / 4f;
        Renderer.endWidth = BeamClass.LaserWidth / 4f;

        Renderer.material.SetTexture("Texture2D_4b993fc71878406c81c86faac6196312", BeamClass.LaserTexture);
    }


    public override void Setup(IPhxWeapon Originator, Vector3 pos, Quaternion rot)
    {
        gameObject.SetActive(true);

        Owner = Originator.GetOwnerController();
        OwnerWeapon = Originator;

        BeamRoot = transform.parent;
        transform.SetParent(Originator.GetFirePoint());
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;

        DamageApplied = false;

        IgnoredColliders = Originator.GetIgnoredColliders();
    }


    public override void Destroy()
    {
        Owner = null;
        OwnerWeapon = null;
        transform.SetParent(BeamRoot);
        gameObject.SetActive(false);

        IgnoredColliders = null;
    }

    bool IsIgnored(Collider c)
    {
        if (c == null) return true;
        if (IgnoredColliders == null) return false;

        for (int i = 0; i < IgnoredColliders.Count; ++i)
        {
            if (IgnoredColliders[i] == c) return true;
        }
        return false;
    }

    /// <remarks>
    /// A beam did no damage at all - it positioned a LineRenderer and nothing
    /// else - so every beam weapon in the game was cosmetic. It is a hitscan
    /// shot with a trail that lingers, so the damage lands ONCE, on the first
    /// tick, and the visual persists for the ordnance's lifespan. Applying it
    /// per frame would be roughly twenty-four times the authored amount for a
    /// 0.4 s beam.
    ///
    /// The shooter used to be excluded by rewriting its colliders to layer 2
    /// for the duration of the raycast and putting them back afterwards. That
    /// is not safe against anything else reading layers in the same frame, and
    /// it mutates objects this ordnance does not own. RaycastAll and a skip
    /// test does the same job without touching the scene.
    /// </remarks>
    public void Tick(float deltaTime)
    {
        int count = Physics.RaycastNonAlloc(transform.position, transform.forward,
                                            HitCache, BeamClass.Range, BeamMask,
                                            QueryTriggerInteraction.Ignore);

        // Nearest first: RaycastNonAlloc makes no ordering promise, and both
        // "where does the beam stop" and "what does a non-penetrating beam
        // hit" depend on the order.
        System.Array.Sort(HitCache, 0, count,
                          Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));

        bool passThrough = BeamClass.PassThrough;
        float endDistance = BeamClass.Range;
        bool applyDamage = !DamageApplied && BeamClass.MaxDamage > 0f;

        for (int i = 0; i < count; ++i)
        {
            RaycastHit hit = HitCache[i];
            if (IsIgnored(hit.collider)) continue;

            if (applyDamage && !PhxDamage.BlocksDirectFire(Owner, hit.collider))
            {
                PhxDamage.ApplyToCollider(hit.collider, BeamClass.MaxDamage,
                                          BeamClass.GetDamageScales(), hit.point,
                                          isSaber: false, instigator: Owner);

                SCENE.EffectsManager.PlayEffectOnce(BeamClass.ImpactEffect.Get(), hit.point,
                                                    Quaternion.LookRotation(hit.normal, Vector3.up));
            }

            if (!passThrough)
            {
                // Stops here, so this is the end of the drawn beam.
                endDistance = hit.distance;
                break;
            }
        }

        DamageApplied |= applyDamage;

        Renderer.SetPosition(1, new Vector3(0f, 0f, endDistance));
    }
}
