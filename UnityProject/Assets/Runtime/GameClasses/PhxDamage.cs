using UnityEngine;

/// <summary>
/// SWBF2's damage model, per the mod tools documentation.
///
/// Damage from ordnance and explosions is scaled by the *health type of the
/// target*, not by what kind of object it is in code:
///
///   HealthType = person | animal | droid | vehicle | building | mine
///
/// and the attacking ordnance/explosion declares a scale per health type:
/// PersonScale, AnimalScale, DroidScale, VehicleScale, BuildingScale.
/// For example an anti-vehicle rocket sets PersonScale low and VehicleScale
/// high; a droid-popper sets DroidScale high.
///
/// Two legacy properties still appear in stock and mod odfs and must keep
/// working:
///   HealthScale -> sets PersonScale, AnimalScale and DroidScale
///   ArmorScale  -> sets VehicleScale and BuildingScale
/// </summary>
public enum PhxHealthType
{
    Person,
    Animal,
    Droid,
    Vehicle,
    Building,
    Mine,
}

/// <summary>The five per-health-type damage scales an attack can declare.</summary>
public struct PhxDamageScales
{
    public float Person;
    public float Animal;
    public float Droid;
    public float Vehicle;
    public float Building;

    /// <summary>
    /// Unscaled: every health type takes the attack's full damage. For damage
    /// sources that are not ordnance and declare no scales of their own - a
    /// force power, a scripted kill - where the alternative is a zeroed struct
    /// that silently does nothing.
    /// </summary>
    public static PhxDamageScales Default => new PhxDamageScales
    {
        Person = 1f, Animal = 1f, Droid = 1f, Vehicle = 1f, Building = 1f,
    };

    public float For(PhxHealthType type)
    {
        switch (type)
        {
            case PhxHealthType.Animal: return Animal;
            case PhxHealthType.Droid: return Droid;
            case PhxHealthType.Vehicle: return Vehicle;
            case PhxHealthType.Building: return Building;
            // 'mine' has no scale of its own; it takes damage like a building
            case PhxHealthType.Mine: return Building;
            default: return Person;
        }
    }
}

public static class PhxDamage
{
    /// <summary>
    /// Resolve a target's HealthType from its odf class. Falls back to the
    /// documented per-class defaults when the property is absent (soldiers are
    /// 'person', vehicles 'vehicle', buildings 'building').
    /// </summary>
    public static PhxHealthType GetHealthType(PhxInstance instance)
    {
        if (instance == null) return PhxHealthType.Building;

        PhxClass cl = instance.GetClassRef();
        if (cl != null)
        {
            PhxProp<string> healthType = cl.P.Get<PhxProp<string>>("HealthType");
            if (healthType != null)
            {
                PhxHealthType? parsed = Parse(healthType.Get());
                if (parsed.HasValue) return parsed.Value;
            }
        }

        // no odf value - use the class-based default
        if (instance is PhxSoldier) return PhxHealthType.Person;
        if (instance is PhxVehicle) return PhxHealthType.Vehicle;
        return PhxHealthType.Building;
    }

    public static PhxHealthType? Parse(string healthType)
    {
        if (string.IsNullOrEmpty(healthType)) return null;
        switch (healthType.ToLowerInvariant())
        {
            case "person": return PhxHealthType.Person;
            case "animal": return PhxHealthType.Animal;
            case "droid": return PhxHealthType.Droid;
            case "vehicle": return PhxHealthType.Vehicle;
            case "building": return PhxHealthType.Building;
            case "mine": return PhxHealthType.Mine;
            default: return null;
        }
    }

    /// <summary>
    /// Resolve the effective scales, honoring the legacy HealthScale /
    /// ArmorScale properties. A specific scale left at its "unset" sentinel
    /// falls back to the legacy value, then to 1.
    /// </summary>
    public static PhxDamageScales Resolve(
        float person, float animal, float droid, float vehicle, float building,
        float healthScale, float armorScale)
    {
        float Pick(float specific, float legacy)
        {
            if (specific >= 0f) return specific;      // explicitly set in the odf
            if (legacy >= 0f) return legacy;          // legacy group value
            return 1f;
        }

        return new PhxDamageScales
        {
            Person = Pick(person, healthScale),
            Animal = Pick(animal, healthScale),
            Droid = Pick(droid, healthScale),
            Vehicle = Pick(vehicle, armorScale),
            Building = Pick(building, armorScale),
        };
    }

    /// <summary>
    /// Apply damage to whatever damageable sits on (or above) this collider,
    /// scaled for its health type. Returns the damage actually dealt.
    /// </summary>
    public static float ApplyToCollider(Collider collider, float maxDamage,
                                        PhxDamageScales scales, Vector3 hitPos,
                                        bool isSaber = false,
                                        PhxPawnController instigator = null)
    {
        if (collider == null) return 0f;

        PhxSoldier soldier = collider.GetComponentInParent<PhxSoldier>();
        if (soldier != null)
        {
            float dmg = maxDamage * scales.For(GetHealthType(soldier));
            if (dmg > 0f) soldier.AddDamageFrom(dmg, hitPos, isSaber, instigator);
            return dmg;
        }

        IPhxDamageableInstance damageable = collider.GetComponentInParent<IPhxDamageableInstance>();
        if (damageable == null) return 0f;

        PhxHealthType type = damageable is PhxInstance inst
            ? GetHealthType(inst)
            : PhxHealthType.Building;   // e.g. capital ship subsystems

        float scaled = maxDamage * scales.For(type);
        if (scaled <= 0f) return 0f;

        // Route a vehicle hit through its damage zones, where it has them.
        //
        // The zone decides how much of the hit reaches the vehicle's own
        // health - a canopy hit carries almost all of it, a track hit carries
        // a third and cripples the track instead. The vehicle's health, death
        // path and every consumer of it are untouched; only the amount
        // changes, and only for vehicles whose model actually has recognisable
        // parts.
        PhxVehicleDamageZones zones = collider.GetComponentInParent<PhxVehicleDamageZones>();
        if (zones != null)
        {
            scaled = zones.ApplyZoneDamage(BFSegmentIdentity.Of(collider), scaled);
            if (scaled <= 0f) return 0f;
        }

        damageable.AddDamage(scaled, instigator);
        return scaled;
    }

    /// <summary>
    /// Linear falloff between an inner and outer radius, per the explosion
    /// documentation: full effect inside inner, none beyond outer.
    /// </summary>
    public static float RadialFalloff(float distance, float innerRadius, float outerRadius)
    {
        if (distance <= innerRadius) return 1f;
        if (distance >= outerRadius) return 0f;
        float span = outerRadius - innerRadius;
        if (span <= 0.0001f) return 1f;
        return 1f - (distance - innerRadius) / span;
    }

    /// <summary>
    /// Whether <paramref name="target"/> is on the attacker's own side.
    /// </summary>
    /// <remarks>
    /// One rule in one place. Melee, force powers and mines each carried their
    /// own copy of it and they had already drifted apart - melee tested
    /// `team != 0` where the other two tested `team > 0`, which disagree for a
    /// negative team id. Team 0 is the unaffiliated/neutral team, and nothing
    /// is friendly to it: a neutral turret shoots everyone.
    ///
    /// Deliberately NOT called from ApplyToCollider. Putting an unconditional
    /// team check there would silently disable self-damage, and a grenade
    /// landing at your own feet killing you is correct BF2 behaviour, not a
    /// bug. Direct-fire callers gate on this; area effects do not.
    /// </remarks>
    public static bool IsFriendly(int attackerTeam, PhxInstance target)
    {
        if (target == null) return false;
        if (attackerTeam <= 0) return false;

        return target.Team.Get() == attackerTeam;
    }

    /// <summary>Same rule, starting from the controller that did the damage.</summary>
    public static bool IsFriendly(PhxPawnController attacker, PhxInstance target)
    {
        // An unattributed hit - a map turret, a script, a scenery explosion -
        // has no side and so is friendly to nobody.
        if (attacker == null) return false;

        return IsFriendly(attacker.Team, target);
    }

    /// <summary>
    /// Whether direct fire from <paramref name="attacker"/> should be allowed
    /// to damage whatever is on this collider.
    /// </summary>
    /// <remarks>
    /// Gunfire only. Off by default and behind a config flag because team-kill
    /// penalties become reachable the moment shots are attributed at all: with
    /// 32v32 and AI that have no line-of-fire check and will happily fire
    /// through a squadmate, leaving this on would punish the player for the
    /// bots' behaviour.
    /// </remarks>
    public static bool BlocksDirectFire(PhxPawnController attacker, Collider target)
    {
        if (PhxBF3.Config.FriendlyFire) return false;
        if (attacker == null || target == null) return false;

        PhxInstance inst = target.GetComponentInParent<PhxInstance>();
        return inst != null && IsFriendly(attacker, inst);
    }
}
