using UnityEngine;
using LibSWBF2.Utils;

/// <summary>
/// Decides which soldiers are heroes and gives them their hero behaviour.
/// </summary>
/// <remarks>
/// There is no "IsHero" property in the data - a hero is a soldier odf that
/// happens to carry a lightsaber and, usually, declares force powers. So the
/// test is what the unit is equipped with, applied once at spawn: carrying a
/// saber gets deflection, and each recognised force property gets its ability.
///
/// This is separate from <see cref="PhxHeroRules"/> on purpose. Rules decide
/// *whether a team may field* a hero; this decides *what a hero can do* once
/// one exists. A campaign soldier handed a saber by a script is a hero for
/// this purpose whether or not any rule set is on.
/// </remarks>
public static class PhxHeroLoadout
{
    /// <summary>
    /// Force properties as they appear on hero odfs, and the ability each
    /// grants. The value in the odf is that power's strength, which is used
    /// as damage for the offensive powers and as impulse for the others.
    /// </summary>
    static readonly (string Property, PhxForcePower Power)[] ForceProperties =
    {
        ("ForcePush", PhxForcePower.Push),
        ("ForcePull", PhxForcePower.Pull),
        ("ForceLightning", PhxForcePower.Lightning),
        ("ForceChoke", PhxForcePower.Choke),
        ("ForceJump", PhxForcePower.Jump),
    };

    public static void Apply(PhxSoldier soldier, IPhxWeapon[][] weapons)
    {
        if (soldier == null || weapons == null) return;
        if (!CarriesSaber(weapons)) return;

        if (soldier.GetComponent<PhxSaberDeflect>() == null)
        {
            soldier.gameObject.AddComponent<PhxSaberDeflect>();
        }

        PhxForcePowers powers = soldier.GetComponent<PhxForcePowers>();
        if (powers == null)
        {
            powers = soldier.gameObject.AddComponent<PhxForcePowers>();
        }

        GrantFromClass(soldier, powers);
    }

    static bool CarriesSaber(IPhxWeapon[][] weapons)
    {
        for (int channel = 0; channel < weapons.Length; ++channel)
        {
            IPhxWeapon[] slots = weapons[channel];
            if (slots == null) continue;

            for (int i = 0; i < slots.Length; ++i)
            {
                if (slots[i] is PhxMeleeWeapon melee && melee.C != null && melee.C.IsLightSaber.Get())
                {
                    return true;
                }
                // Class naming is the fallback the melee weapon itself uses -
                // most stock saber odfs never set IsLightSaber.
                if (slots[i] is PhxMeleeWeapon named && named.C != null &&
                    named.C.Name != null && named.C.Name.ToLowerInvariant().Contains("saber"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    static void GrantFromClass(PhxSoldier soldier, PhxForcePowers powers)
    {
        PhxClass soldierClass = soldier.GetClassRef();
        if (soldierClass?.EntityClass == null) return;

        for (int i = 0; i < ForceProperties.Length; ++i)
        {
            (string property, PhxForcePower power) = ForceProperties[i];
            if (!soldierClass.EntityClass.GetProperty(HashUtils.GetFNV(property), out string raw)) continue;
            if (powers.Has(power)) continue;

            float strength = PhxUtils.FloatFromString(raw);
            if (strength <= 0f) strength = 1f;

            powers.Grant(BuildAbility(power, strength));
        }
    }

    /// <summary>
    /// Turn one odf strength value into a usable ability.
    /// </summary>
    /// <remarks>
    /// The odfs give a single number per power and no ranges, costs or
    /// cooldowns, so those are set per power to values that keep each one
    /// distinct: push is short, wide and cheap; lightning is narrow, damaging
    /// and expensive; choke reaches furthest and costs most. Anything the data
    /// does specify (the strength) is used rather than overridden.
    /// </remarks>
    static PhxForcePowers.Ability BuildAbility(PhxForcePower power, float strength)
    {
        switch (power)
        {
            case PhxForcePower.Push:
                return new PhxForcePowers.Ability
                {
                    Power = power, Range = 12f, HalfArc = 60f,
                    Push = 10f * strength, Damage = 10f * strength,
                    EnergyCost = 15f, Cooldown = 3f,
                };

            case PhxForcePower.Pull:
                return new PhxForcePowers.Ability
                {
                    Power = power, Range = 15f, HalfArc = 40f,
                    Push = 8f * strength, Damage = 0f,
                    EnergyCost = 15f, Cooldown = 4f,
                };

            case PhxForcePower.Lightning:
                return new PhxForcePowers.Ability
                {
                    Power = power, Range = 14f, HalfArc = 25f,
                    Push = 0f, Damage = 60f * strength,
                    EnergyCost = 30f, Cooldown = 6f,
                };

            case PhxForcePower.Choke:
                return new PhxForcePowers.Ability
                {
                    Power = power, Range = 20f, HalfArc = 15f,
                    Push = 4f * strength, Damage = 80f * strength,
                    EnergyCost = 40f, Cooldown = 8f,
                };

            default:  // Jump
                return new PhxForcePowers.Ability
                {
                    Power = PhxForcePower.Jump, Range = 0f, HalfArc = 0f,
                    Push = 8f * strength, Damage = 0f,
                    EnergyCost = 10f, Cooldown = 1.5f,
                };
        }
    }
}
