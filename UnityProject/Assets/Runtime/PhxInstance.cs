using System;
using System.Reflection;
using UnityEngine;
using System.Collections.Generic;

using LibSWBF2.Wrappers;

// Bare minimum requirements
public interface IPhxInstantiable
{
    // Use this as constructor
    // (MonoBehaviour constructors don't get called, and Awake() won't be called until next frame)
    //
    // Use this to add Components like Rigidbody, etc.
    // and bind property change events.
    // Will be called BEFORE instance properties assignments!
    public void Init();

    // Use this as destructor
    public void Destroy();
}

public interface IPhxTickable
{
    public void Tick(float deltaTime);
}

public interface IPhxTickablePhysics
{
    public void TickPhysics(float deltaTime);
}

// Use this whenever we're dealing with a Unity Component attached to a GameObject in general.
// From here on, instances of all inheriting classes are poolable.
public abstract class PhxComponent : MonoBehaviour, IPhxInstantiable
{
    public PhxPool ParentPool;

    public abstract void Init();
    public abstract void Destroy();
}

// Use this when we're dealing with instances that contain reflected properties that are exposed to Lua
public abstract class PhxInstance : PhxComponent
{
    public PhxPropertyDB P { get; private set; } = new PhxPropertyDB();

    // Every SWBF2 object has a Team
    public PhxProp<int> Team = new PhxProp<int>(0);

    // The odf class this instance was created from, without needing to know
    // the concrete generic type. Used to read shared class properties such as
    // HealthType, which drive damage scaling for every object type.
    public virtual PhxClass GetClassRef()
    {
        return null;
    }


    public virtual void InitInstance(ISWBFProperties instOrClass, PhxClass classProperties)
    {
        Type type = GetType();
        MemberInfo[] members = type.GetMembers();

        foreach (MemberInfo member in members)
        {
            if (member.MemberType == MemberTypes.Field && typeof(IPhxPropRef).IsAssignableFrom(type.GetField(member.Name).FieldType))
            {
                IPhxPropRef refValue = (IPhxPropRef)type.GetField(member.Name).GetValue(this);
                P.Register(member.Name, refValue);
            }
        }

        // Call before property assignments, such that we can react to their initialization
        Init();

        foreach (MemberInfo member in members)
        {
            if (member.MemberType == MemberTypes.Field && typeof(IPhxPropRef).IsAssignableFrom(type.GetField(member.Name).FieldType))
            {
                IPhxPropRef refValue = (IPhxPropRef)type.GetField(member.Name).GetValue(this);
                PhxPropertyDB.AssignProp(instOrClass, member.Name, refValue);
            }
        }
    }
}

public abstract class PhxInstance<T> : PhxInstance where T : PhxClass
{
    public bool IsInit => C != null;
    public T C { get; private set; } = null;

    public override PhxClass GetClassRef()
    {
        return C;
    }


    public override void InitInstance(ISWBFProperties instOrClass, PhxClass classProperties)
    {
        Debug.Assert(classProperties is T);
        C = (T)classProperties;

        base.InitInstance(instOrClass, null);
    }
}

public interface IPhxControlableInstance
{
    public PhxInstance GetInstance();
    public Vector2 GetViewConstraint();
    public Vector2 GetMaxTurnSpeed();

    PhxPawnController GetController();
    void Assign(PhxPawnController controller);
    void UnAssign();

    // Makes this instance immovable
    // Useful for testing and character selection
    public void Fixate();

    public void PlayIntroAnim();

    IPhxWeapon GetPrimaryWeapon();
}


public interface IPhxDamageableInstance
{
    /// <summary>Damage this thing, optionally recording who caused it.</summary>
    /// <remarks>
    /// The instigator is what lets a kill be credited. It used to carry only
    /// an amount, so anything that was not a soldier - vehicles, buildings,
    /// mines, capital-ship subsystems - died without the game ever learning
    /// who killed it, and the explosion a destroyed vehicle throws was
    /// unattributed along with every occupant it took with it.
    /// Optional because plenty of damage has no author: falling, drowning,
    /// a script.
    /// </remarks>
    public void AddDamage(float Damage, PhxPawnController Instigator = null);
}






public abstract class PhxControlableInstance<T> : PhxInstance<T>, IPhxControlableInstance where T : PhxClass
{
    protected PhxPawnController Controller;
    protected Vector2 ViewConstraint = Vector2.positiveInfinity;

    // degrees per second
    protected Vector2 MaxTurnSpeed = Vector2.positiveInfinity;


    public PhxInstance GetInstance()
    {
        return this;
    }

    public Vector2 GetViewConstraint()
    {
        return ViewConstraint;
    }

    public Vector2 GetMaxTurnSpeed()
    {
        return MaxTurnSpeed;
    }

    public PhxPawnController GetController()
    {
        return Controller;
    }

    public void Assign(PhxPawnController controller)
    {
        Controller = controller;
        controller.SetPawn(this);
    }

    public void UnAssign()
    {
        if (Controller != null)
        {
            Controller.RemovePawn();
            Controller = null;
        }
    }

    public abstract void Fixate();
    public abstract void PlayIntroAnim();
    public abstract IPhxWeapon GetPrimaryWeapon();
}

public interface IPhxWeapon
{
    public PhxInstance GetInstance();
    public bool Fire(PhxPawnController owner, Vector3 targetPos);

    public void Reload();
    public void OnShot(Action callback);
    public void OnReload(Action callback);
    public string GetAnimBankName();

    public void SetFirePoint(Transform FirePoint);

    public Transform GetFirePoint();
    public void GetFirePoint(out Vector3 Pos, out Quaternion Rot);


    public void SetIgnoredColliders(List<Collider> Colliders);
    public List<Collider> GetIgnoredColliders();

    public PhxPawnController GetOwnerController();
    public bool IsFiring();




    public int GetMagazineSize();
    public int GetTotalAmmo();
    public int GetMagazineAmmo();
    public int GetAvailableAmmo();

    /// <summary>
    /// Resupply the reserve, in magazines.
    /// </summary>
    /// <remarks>
    /// Magazines rather than rounds because that is the unit the data uses:
    /// a weapon recharge station declares soldierammo = 1.0 against
    /// soldierhealth = 25.0, so one is a fraction of a clip and the other is
    /// absolute HP. Letting each weapon convert also keeps a rifle's clip and
    /// a rocket launcher's from having to mean the same number of rounds.
    /// </remarks>
    public void AddAmmo(float magazines);

    /// <summary>Magnification levels this weapon offers, least first. Empty if it does not zoom.</summary>
    public float[] GetZoomLevels();

    /// <summary>Magnification per second when sweeping between levels; 0 means step, do not sweep.</summary>
    public float GetZoomRate();

    /// <summary>Whether zoom brings up a scope overlay and hides the reticle.</summary>
    public bool HasSniperScope();
    public float GetReloadTime();
    public float GetReloadProgress();
}