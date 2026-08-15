using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A remote-detonated charge launcher: press once to place, press again to
/// set off everything placed.
/// </summary>
/// <remarks>
/// The <c>detonator</c> base class was enumerated by the importer and never
/// registered, so <c>PhxSoldier.Init</c>'s class lookup returned null and the
/// WEAPONSECTION entry naming it was skipped outright - the engineer's detpack
/// simply did not exist. Registration is the unlocking step, exactly as it was
/// for <c>mine</c>.
///
/// Measured rather than assumed, and the measurement moved things:
///
///   com_weap_inf_detpack        (base detonator) ordnancename com_weap_inf_detpack_ord
///                               roundsperclip 1  maxitems 1  offhandweapon 1
///                               shotdelay 0.5    reloadtime 3.0
///
///   com_weap_inf_detpack_ord    (base MINE, not sticky and not grenade)
///                               lifespan 60  maxhealth 20  velocity 0.0
///                               stick* 1 on every surface type
///                               explosiontrigger / explosionexpire / explosiondeath
///
/// So the charge is a mine with no proximity trigger, and PhxMine really is
/// the template - it already carries health so the charge can be shot off a
/// wall, an owner for kill credit, the Lua kill event and the destruction
/// registry. What this class adds is the half a mine does not have: placement
/// from a weapon, and a trigger held by the person who placed it.
///
/// Velocity 0.0 is why the charge is PLACED against a surface rather than
/// thrown. A thrown one would also need a non-kinematic body, and PhxMine is
/// deliberately kinematic so a charge stays where it was put.
/// </remarks>
public class PhxDetonator : PhxGenericWeapon
{
    /// <summary>
    /// Charges this launcher has out in the world, oldest first.
    /// </summary>
    /// <remarks>
    /// Held per weapon rather than per soldier so a player carrying two
    /// detonator-class weapons detonates each one's charges with its own
    /// trigger, and so charges survive a weapon switch.
    /// </remarks>
    readonly List<PhxMine> Placed = new List<PhxMine>();

    /// <summary>How far in front the charge is placed when nothing is in reach.</summary>
    const float PlaceReach = 3f;

    public override bool Fire(PhxPawnController owner, Vector3 targetPos)
    {
        // Same capture as the base class: without it the charge has no owner
        // and its kills are credited to nobody.
        OwnerController = owner ?? OwnerController;

        DropDeadCharges();

        // Anything out there? Then this press is the trigger, not a placement.
        // Checked first so a player holding one charge always gets the
        // detonate they expect rather than being told they are out of ammo.
        if (Placed.Count > 0)
        {
            DetonateAll();
            return true;
        }

        if (GetMagazineAmmo() <= 0) return false;

        PhxMine charge = PlaceCharge(targetPos);
        if (charge == null) return false;

        Placed.Add(charge);
        ConsumeRound();
        return true;
    }

    /// <summary>Set off everything this launcher has placed.</summary>
    public void DetonateAll()
    {
        // Iterated over a copy: Detonate destroys the instance, and an
        // explosion can take a neighbouring charge with it through AddDamage,
        // which would reach back into this list mid-iteration.
        PhxMine[] charges = Placed.ToArray();
        Placed.Clear();

        for (int i = 0; i < charges.Length; ++i)
        {
            if (charges[i] != null) charges[i].Detonate();
        }
    }

    /// <summary>Forget charges that went off on their own - shot, or expired.</summary>
    void DropDeadCharges()
    {
        for (int i = Placed.Count - 1; i >= 0; --i)
        {
            if (Placed[i] == null) Placed.RemoveAt(i);
        }
    }

    PhxMine PlaceCharge(Vector3 targetPos)
    {
        PhxScene scene = PhxGame.GetScene();
        PhxClass chargeClass = C.OrdnanceName.Get();
        if (scene == null || chargeClass == null)
        {
            Debug.LogWarning($"Detonator '{name}' has no ordnance class to place.");
            return null;
        }

        GetPlacement(targetPos, out Vector3 position, out Quaternion rotation);

        PhxInstance placed = scene.CreateInstance(
            chargeClass, $"{chargeClass.Name}_{PlacedCounter++}", position, rotation);

        PhxMine charge = placed as PhxMine;
        if (charge == null)
        {
            // The odf named something that is not a mine. Reported rather than
            // left as a silent no-op, because the symptom - a detonator that
            // does nothing at all - is indistinguishable from the class never
            // having been registered.
            if (placed != null)
            {
                Debug.LogWarning($"Detonator '{name}' ordnance '{chargeClass.Name}' resolved to " +
                                 $"{placed.GetType().Name}, which is not a placeable charge.");
                scene.DestroyInstance(placed);
            }
            return null;
        }

        charge.Team.Set(OwnerController != null ? OwnerController.Team : 0);
        charge.Owner = OwnerController;
        return charge;
    }

    static int PlacedCounter;

    /// <summary>
    /// Where the charge goes: against whatever is being aimed at, or just in
    /// front of the muzzle when that is nothing.
    /// </summary>
    /// <remarks>
    /// Oriented to the surface normal, so a charge on a wall sits flat against
    /// it rather than standing out at the angle it was thrown from - which is
    /// the whole visual point of a detpack.
    /// </remarks>
    void GetPlacement(Vector3 targetPos, out Vector3 position, out Quaternion rotation)
    {
        Transform firePoint = GetFirePoint();
        Vector3 origin = firePoint != null ? firePoint.position : transform.position;

        Vector3 direction = targetPos - origin;
        direction = direction.sqrMagnitude > 0.0001f
            ? direction.normalized
            : (firePoint != null ? firePoint.forward : transform.forward);

        if (Physics.Raycast(origin, direction, out RaycastHit hit, PlaceReach,
                            ~0, QueryTriggerInteraction.Ignore)
            && !IsOwnCollider(hit.collider))
        {
            // Lifted a few millimetres off the surface: placed exactly on it,
            // the charge's own collider starts intersecting what it is stuck
            // to and PhysX pushes it back out.
            position = hit.point + hit.normal * 0.02f;
            rotation = Quaternion.LookRotation(hit.normal, Vector3.up);
            return;
        }

        position = origin + direction * PlaceReach;
        rotation = Quaternion.LookRotation(-direction, Vector3.up);
    }

    bool IsOwnCollider(Collider c)
    {
        List<Collider> ignored = GetIgnoredColliders();
        for (int i = 0; i < ignored.Count; ++i)
        {
            if (ignored[i] == c) return true;
        }
        return false;
    }
}
