
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using LibSWBF2.Utils;
using LibSWBF2.Enums;
using LibSWBF2.Wrappers;
using System.Runtime.ExceptionServices;


public class PhxDestructableBuilding : PhxInstance<PhxDestructableBuilding.ClassProperties>,
                                        IPhxTickable, IPhxDamageableInstance, IPhxDestructible
{
    protected static PhxScene SCENE => PhxGame.GetScene();

    public class ClassProperties : PhxClass
    {
        public PhxProp<float> MaxHealth = new PhxProp<float>(100.0f);

        public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);

        // Buildings carry authored break-up geometry exactly like vehicles do;
        // the same section, read the same way, so both go through
        // PhxChunkSpawner rather than each inventing its own debris.
        public PhxPropertySection ChunkSection = new PhxPropertySection(
            "CHUNKSECTION",
            ("ChunkGeometryName", new PhxProp<string>(null)),
            ("ChunkNodeName", new PhxProp<string>(null)),
            ("ChunkTerrainCollisions", new PhxProp<int>(1)),
            ("ChunkTerrainEffect", new PhxProp<string>("")),
            ("ChunkPhysics", new PhxProp<string>("")),
            ("ChunkOmega", new PhxMultiProp(typeof(float), typeof(float), typeof(float))),
            ("ChunkBounciness", new PhxProp<float>(0.0f)),
            ("ChunkStickiness", new PhxProp<float>(0.0f)),
            ("ChunkSpeed", new PhxProp<float>(1.0f)),
            ("ChunkUpFactor", new PhxProp<float>(0.5f)),
            ("ChunkTrailEffect", new PhxProp<string>("")),
            ("ChunkSmokeEffect", new PhxProp<string>("")),
            ("ChunkSmokeNodeName", new PhxProp<string>(""))
        );

        // This is the reason for the addition of the IsGeometryManuallyInitialized check in ClassLoader.
        // The different geometries will be attached as children of the root and activated/deactivated
        // when building is destroyed or repaired
        public PhxProp<string> GeometryName = new PhxProp<string>("");
        public PhxProp<string> DestroyedGeometryName = new PhxProp<string>("");
    }

    public PhxProp<float> CurHealth = new PhxProp<float>(1f);

    public List<PhxDamageEffect> DamageEffects = new List<PhxDamageEffect>();

    public GameObject BuiltGeometry;
    public GameObject DestroyedGeometry;


    public float Health;

    protected bool IsBuilt = true;




    public override void Init()
    {
        // This is so colliding ordnance can easily get the root object and add damage
        Rigidbody Body = gameObject.AddComponent<Rigidbody>();
        Body.isKinematic = true;

        gameObject.layer = LayerMask.NameToLayer("BuildingAll");


        BuiltGeometry = ModelLoader.Instance.GetGameObjectFromModel(C.GeometryName.Get(), null);

        if (BuiltGeometry != null)
        {
            SWBFModel BuiltModelMapping = ModelLoader.Instance.GetModelMapping(BuiltGeometry, C.GeometryName.Get());

            if (BuiltModelMapping != null)
            {
                BuiltModelMapping.GameRole = SWBFGameRole.Building;
                BuiltModelMapping.ExpandMultiLayerColliders();
                BuiltModelMapping.SetColliderLayerFromMaskAll();                
            }

            BuiltGeometry.transform.SetParent(transform);
            BuiltGeometry.transform.localPosition = Vector3.zero;
            BuiltGeometry.transform.localRotation = Quaternion.identity;
            BuiltGeometry.SetActive(true);            
        }
        

        DestroyedGeometry = ModelLoader.Instance.GetGameObjectFromModel(C.DestroyedGeometryName.Get(), null);

        if (DestroyedGeometry != null)
        {
            SWBFModel DestroyedModelMapping = ModelLoader.Instance.GetModelMapping(DestroyedGeometry, C.DestroyedGeometryName.Get());
            if (DestroyedModelMapping != null)
            {
                DestroyedModelMapping.GameRole = SWBFGameRole.Building;
                DestroyedModelMapping.ExpandMultiLayerColliders();
                DestroyedModelMapping.SetColliderLayerFromMaskAll(); 
            }            

            DestroyedGeometry.transform.SetParent(transform);
            DestroyedGeometry.transform.localPosition = Vector3.zero;
            DestroyedGeometry.transform.localRotation = Quaternion.identity;
            DestroyedGeometry.SetActive(false);
        }

        CurHealth.Set(C.MaxHealth.Get());
        PhxDestructionRegistry.Register(this);

        EntityClass EC = C.EntityClass;
        EC.GetAllProperties(out uint[] properties, out string[] values);

        PhxDamageEffect CurrDamageEffect = null;

        int i = 0;
        while (i < properties.Length)
        {
            if (properties[i] == HashUtils.GetFNV("DamageStartPercent"))
            {
                CurrDamageEffect = new PhxDamageEffect();
                DamageEffects.Add(CurrDamageEffect);

                CurrDamageEffect.DamageStartPercent = float.Parse(values[i], System.Globalization.CultureInfo.InvariantCulture) / 100f;
            }
            else if (properties[i] == HashUtils.GetFNV("DamageStopPercent"))
            {
                CurrDamageEffect.DamageStopPercent = float.Parse(values[i], System.Globalization.CultureInfo.InvariantCulture) / 100f;
            }
            else if (properties[i] == HashUtils.GetFNV("DamageEffect"))
            {
                CurrDamageEffect.Effect = SCENE.EffectsManager.LendEffect(values[i]);
            }
            else if (properties[i] == HashUtils.GetFNV("DamageAttachPoint"))
            {
                CurrDamageEffect.DamageAttachPoint = UnityUtils.FindChildTransform(transform, values[i]);
            }

            i++;
        }
    }


    public virtual void Tick(float deltaTime)
    {
        float HealthPercent = CurHealth.Get() / C.MaxHealth.Get();

        Health = CurHealth.Get();

        if (HealthPercent > 0.0001f)
        {
            if (!IsBuilt)
            {
                if (BuiltGeometry != null) BuiltGeometry.SetActive(true);
                if (DestroyedGeometry != null) DestroyedGeometry.SetActive(false);

                IsBuilt = true;
                PhxDestructionRegistry.Register(this);

                // Call respawn events
                PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectRespawnName, gameObject.name.ToLower());
            }

            foreach (PhxDamageEffect DamageEffect in DamageEffects)
            {
                DamageEffect.Update(HealthPercent);
            }
        }
        else 
        {
            if (IsBuilt)
            {
                PhxExplosionManager.AddExplosion(null, C.ExplosionName.Get() as PhxExplosionClass, transform.position, transform.rotation);

                // Authored break-up, thrown from the still-standing geometry
                // before it is swapped out for the ruin.
                PhxChunkSpawner.Spawn(C.ChunkSection, transform, Vector3.zero);

                if (BuiltGeometry != null) BuiltGeometry.SetActive(false);
                if (DestroyedGeometry != null) DestroyedGeometry.SetActive(true);

                IsBuilt = false;
                PhxDestructionRegistry.NotifyDestroyed(this);

                // Call death events. Scripts subscribe by object name, by the
                // owning team, or by odf class - all three describe the same
                // kill, so all three fire together.
                int? objIdx = PhxGame.GetScene()?.GetInstanceIndex(this);
                PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);
                PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillTeam, Team.Get(), objIdx);
                PhxClass killedClass = GetClassRef();
                if (killedClass != null)
                {
                    PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillClass, killedClass.Name.ToLower(), objIdx);
                }
            }
        }
    }

    public override void Destroy()
    {
        PhxDestructionRegistry.Unregister(this);
    }

    // ------------------------------------------------------ IPhxDestructible
    public PhxDestructibleKind DestructibleKind => PhxDestructibleKind.Building;
    public GameObject GetGameObject() => gameObject;
    public string GetDestructibleName() => name;
    public int GetTeam() => Team.Get();
    public float GetHealth() => CurHealth.Get();
    public float GetMaxHealth() => C == null ? 0f : C.MaxHealth.Get();
    public bool IsDestroyed => !IsBuilt;

    /// <summary>
    /// Subtract damage from health.
    /// </summary>
    /// <remarks>
    /// This used to ignore <paramref name="damage"/> entirely and toggle
    /// health between -1 and 1, so a destructible building was levelled by the
    /// first scratch it took - a single rifle round destroyed a bunker - and
    /// the next hit rebuilt it. MaxHealth was authored and never consulted.
    /// </remarks>
    public void AddDamage(float damage)
    {
        if (damage <= 0f || !IsBuilt) return;

        CurHealth.Set(Mathf.Max(CurHealth.Get() - damage, 0f));
    }

    /// <summary>Rebuild the structure. See <see cref="IPhxDestructible.Restore"/>.</summary>
    /// <remarks>
    /// Only health is set: Tick already watches for health crossing back above
    /// the rebuild threshold and swaps the model, collision and effects there.
    /// Doing that work here as well would run it twice on the same frame.
    /// </remarks>
    public void Restore()
    {
        if (C == null) return;

        CurHealth.Set(C.MaxHealth.Get());
    }
}
