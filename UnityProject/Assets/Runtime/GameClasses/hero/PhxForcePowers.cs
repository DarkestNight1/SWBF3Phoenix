using System.Collections.Generic;
using UnityEngine;

/// <summary>The force abilities a hero odf can grant.</summary>
public enum PhxForcePower
{
    None,
    Push,
    Pull,
    Lightning,
    Choke,
    Jump,
}

/// <summary>
/// The force framework: what a hero can do beyond swinging, what it costs, and
/// how often.
/// </summary>
/// <remarks>
/// Deliberately a small, explicit set rather than a general ability system.
/// Every stock hero's non-melee move is one of these five, they all share one
/// resource (the same stamina bar sprinting uses, which is how BF2 gates
/// them), and they all resolve against things in a cone or radius. A general
/// system would be more code and no more capability.
///
/// Costs and cooldowns come from the class where the odf declares them and
/// fall back to values in the same range as the stock heroes' otherwise -
/// enough that a hero cannot chain a power indefinitely.
/// </remarks>
public sealed class PhxForcePowers : MonoBehaviour
{
    public sealed class Ability
    {
        public PhxForcePower Power;
        public float EnergyCost = 20f;
        public float Cooldown = 4f;
        public float Range = 12f;

        /// <summary>Half-angle of the affected cone; 180 makes it a radius.</summary>
        public float HalfArc = 45f;

        public float Damage;
        public float Push = 15f;
        public string Effect;

        public float ReadyAt;

        public bool IsReady => Time.time >= ReadyAt;
    }

    readonly List<Ability> Abilities = new List<Ability>();
    static readonly Collider[] OverlapCache = new Collider[64];

    PhxSoldier Owner;

    void Awake()
    {
        Owner = GetComponentInParent<PhxSoldier>();
    }

    public void Grant(Ability ability)
    {
        if (ability == null || ability.Power == PhxForcePower.None) return;
        Abilities.Add(ability);
    }

    public Ability Get(PhxForcePower power)
    {
        for (int i = 0; i < Abilities.Count; ++i)
        {
            if (Abilities[i].Power == power) return Abilities[i];
        }
        return null;
    }

    public bool Has(PhxForcePower power) => Get(power) != null;

    /// <summary>
    /// Use a power. Returns false when the hero does not have it, it is on
    /// cooldown, or there is not enough stamina - the caller can distinguish
    /// with <see cref="Get"/> if it wants to say which.
    /// </summary>
    public bool Use(PhxForcePower power)
    {
        Ability ability = Get(power);
        if (ability == null || !ability.IsReady) return false;
        if (Owner == null || Owner.IsDead) return false;
        if (ability.EnergyCost > 0f && !Owner.TrySpendEnergy(ability.EnergyCost)) return false;

        ability.ReadyAt = Time.time + ability.Cooldown;

        if (!string.IsNullOrEmpty(ability.Effect))
        {
            PhxGame.GetScene()?.EffectsManager.PlayEffectOnce(
                ability.Effect, transform.position, transform.rotation);
        }

        switch (power)
        {
            case PhxForcePower.Jump:
                ApplyJump(ability);
                break;
            case PhxForcePower.Pull:
                ApplyToTargets(ability, -1f);
                break;
            default:
                ApplyToTargets(ability, 1f);
                break;
        }
        return true;
    }

    void ApplyJump(Ability ability)
    {
        Rigidbody body = Owner.GetComponent<Rigidbody>();
        if (body != null && !body.isKinematic)
        {
            body.AddForce(Vector3.up * ability.Push, ForceMode.VelocityChange);
        }
    }

    /// <summary>
    /// Damage and shove everything in the cone.
    /// <paramref name="pushSign"/> is +1 to push away and -1 to pull in, which
    /// is the only difference between Push and Pull.
    /// </summary>
    void ApplyToTargets(Ability ability, float pushSign)
    {
        Vector3 origin = Owner.transform.position;
        Vector3 forward = Owner.transform.forward;
        int team = Owner.Team.Get();

        int count = Physics.OverlapSphereNonAlloc(origin, ability.Range, OverlapCache,
                                                  ~0, QueryTriggerInteraction.Ignore);

        var alreadyHit = new HashSet<PhxInstance>();
        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (coll == null) continue;

            PhxInstance instance = coll.GetComponentInParent<PhxInstance>();
            if (instance == null || ReferenceEquals(instance, Owner)) continue;
            if (!alreadyHit.Add(instance)) continue;
            if (team > 0 && instance.Team.Get() == team) continue;

            Vector3 to = instance.transform.position - origin;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) continue;
            if (ability.HalfArc < 180f && Vector3.Angle(forward, to.normalized) > ability.HalfArc) continue;

            if (ability.Damage > 0f)
            {
                PhxDamage.ApplyToCollider(coll, ability.Damage, PhxDamageScales.Default,
                                          instance.transform.position, isSaber: false,
                                          instigator: Owner.GetController());
            }

            if (ability.Push <= 0f) continue;

            Rigidbody body = coll.attachedRigidbody;
            if (body != null && !body.isKinematic)
            {
                Vector3 direction = (to.normalized + Vector3.up * 0.35f).normalized * pushSign;
                body.AddForce(direction * ability.Push, ForceMode.VelocityChange);
            }
        }
    }
}
