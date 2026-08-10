using UnityEngine;

/// <summary>
/// Lets a saber wielder turn blaster bolts aside.
/// </summary>
/// <remarks>
/// Deflection is the single behaviour that separates a hero from a soldier
/// holding a very strong melee weapon: without it a Jedi walking into open
/// ground dies to massed fire exactly like infantry, which is neither how the
/// game plays nor how the class is balanced.
///
/// The check lives on the deflector rather than in the projectile so that the
/// bolt does not need to know anything about heroes: <see cref="PhxBolt"/>
/// asks the collider it hit whether it belongs to something that deflects, and
/// hands the bolt over if so.
/// </remarks>
public sealed class PhxSaberDeflect : MonoBehaviour
{
    /// <summary>
    /// Half-angle in front of the wielder within which bolts can be caught.
    /// A blade covers the front arc only - being shot in the back is still
    /// fatal, which is what makes flanking a hero worth doing.
    /// </summary>
    public float DeflectHalfArc = 75f;

    /// <summary>
    /// Chance a bolt inside the arc is actually turned aside. Below 1 so that
    /// sustained fire from several directions eventually gets through.
    /// </summary>
    public float DeflectChance = 0.85f;

    /// <summary>Stamina each deflection costs; 0 makes it free.</summary>
    public float EnergyCost = 0.5f;

    /// <summary>
    /// Deflected bolts are sent back at whoever fired, rather than scattered.
    /// This is what makes deflection an attack as well as a defence.
    /// </summary>
    public bool ReturnToSender = true;

    PhxSoldier Owner;

    void Awake()
    {
        Owner = GetComponentInParent<PhxSoldier>();
    }

    /// <summary>Whether this wielder can deflect right now.</summary>
    public bool CanDeflect()
    {
        if (Owner == null || Owner.IsDead) return false;

        // Only while actually holding the blade: swapping to a blaster must
        // not leave a hero deflecting with a pistol.
        IPhxWeapon weapon = Owner.GetPrimaryWeapon();
        return weapon is PhxMeleeWeapon;
    }

    /// <summary>
    /// Try to turn <paramref name="boltDirection"/> aside. Returns the new
    /// direction, or null when the bolt gets through.
    /// </summary>
    public Vector3? TryDeflect(Vector3 boltPosition, Vector3 boltDirection, Vector3 shooterPosition)
    {
        if (!CanDeflect()) return null;

        Vector3 toBolt = boltPosition - transform.position;
        toBolt.y = 0f;
        if (toBolt.sqrMagnitude < 0.0001f) return null;

        if (Vector3.Angle(Owner.transform.forward, toBolt.normalized) > DeflectHalfArc) return null;
        if (Random.value > DeflectChance) return null;
        if (EnergyCost > 0f && !Owner.TrySpendEnergy(EnergyCost)) return null;

        if (ReturnToSender)
        {
            Vector3 back = shooterPosition - boltPosition;
            if (back.sqrMagnitude > 0.0001f)
            {
                return back.normalized;
            }
        }

        // No shooter to send it back to: reflect off the blade plane, which is
        // taken as facing the wielder's forward.
        return Vector3.Reflect(boltDirection, Owner.transform.forward).normalized;
    }

    /// <summary>
    /// The deflector covering a collider, or null. Used by projectiles, which
    /// hit limb colliders rather than the soldier root.
    /// </summary>
    public static PhxSaberDeflect Find(Collider collider)
    {
        return collider == null ? null : collider.GetComponentInParent<PhxSaberDeflect>();
    }
}
