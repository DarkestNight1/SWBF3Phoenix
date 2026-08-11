using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-part damage for a vehicle, resolved from the model's own segments.
/// </summary>
/// <remarks>
/// A vehicle is not a health bar. Shooting an AT-ST's legs should cripple it,
/// shooting its cockpit should kill the crew, and shooting the hull should
/// simply take longer than either - and the original data already says which
/// part is which, in the MSH segment tags that <see cref="BFSegmentIdentity"/>
/// now carries through import.
///
/// The design decision worth stating: zones **modulate** damage, they do not
/// replace the vehicle's health. A vehicle still dies when
/// <c>PhxVehicle.CurHealth</c> reaches zero, so every existing consumer -
/// scoring, mission objectives, the AI's target evaluation, the destruction
/// registry - keeps working unchanged. What zones add is where the damage
/// landed and what that does besides subtract: a dead engine slows the
/// vehicle, a dead turret stops it firing, and a hit on the hull is just a
/// hit on the hull.
///
/// Vehicles whose models carry no recognisable tags get a single implicit hull
/// zone and behave exactly as they did before.
/// </remarks>
[RequireComponent(typeof(PhxVehicle))]
public sealed class PhxVehicleDamageZones : MonoBehaviour
{
    /// <summary>One damageable part of the vehicle.</summary>
    public sealed class Zone
    {
        public BFSegmentRole Role;
        public float MaxHealth;
        public float Health;

        /// <summary>How much damage here counts against the vehicle overall.</summary>
        public float HullTransfer;

        public bool IsDisabled => Health <= 0f;
    }

    readonly Dictionary<BFSegmentRole, Zone> Zones = new Dictionary<BFSegmentRole, Zone>();

    PhxVehicle Vehicle;

    /// <summary>Raised when a zone is knocked out, for effects and AI.</summary>
    public System.Action<Zone> OnZoneDisabled;

    public IEnumerable<Zone> All => Zones.Values;

    /// <summary>Speed multiplier from engine/leg/wheel damage, 0..1.</summary>
    public float MobilityFactor { get; private set; } = 1f;

    /// <summary>False once every weapon-bearing zone is out.</summary>
    public bool CanFire { get; private set; } = true;

    void Awake()
    {
        Vehicle = GetComponent<PhxVehicle>();
    }

    void Start()
    {
        Build();
    }

    /// <summary>
    /// Create a zone per distinct segment role the model actually has.
    /// </summary>
    /// <remarks>
    /// Health per zone is a share of the vehicle's own MaxHealth rather than a
    /// new authored number, because there is no authored per-zone health in
    /// the source. The shares encode what a hit on that part should mean: legs
    /// and engines are fragile relative to the hull because crippling a walker
    /// should be achievable, and the hull is deliberately the slowest way to
    /// kill anything.
    /// </remarks>
    void Build()
    {
        Zones.Clear();

        float vehicleHealth = Vehicle != null && Vehicle.GetMaxHealth() > 0f
            ? Vehicle.GetMaxHealth()
            : 100f;

        var seen = new HashSet<BFSegmentRole>();
        BFSegmentIdentity[] segments = GetComponentsInChildren<BFSegmentIdentity>(true);
        for (int i = 0; i < segments.Length; ++i)
        {
            BFSegmentRole role = segments[i].Role;
            if (role == BFSegmentRole.Unknown || !seen.Add(role)) continue;

            AddZone(role, vehicleHealth);
        }

        // Anything with no recognisable parts still has a hull, so the rest of
        // this class has something to talk about.
        if (Zones.Count == 0)
        {
            AddZone(BFSegmentRole.Hull, vehicleHealth);
        }

        Recompute();
    }

    void AddZone(BFSegmentRole role, float vehicleHealth)
    {
        float share;
        float transfer;

        switch (role)
        {
            case BFSegmentRole.Engine:  share = 0.30f; transfer = 0.55f; break;
            case BFSegmentRole.Leg:     share = 0.28f; transfer = 0.50f; break;
            case BFSegmentRole.Wheel:   share = 0.22f; transfer = 0.35f; break;
            case BFSegmentRole.Track:   share = 0.25f; transfer = 0.35f; break;
            case BFSegmentRole.Turret:  share = 0.35f; transfer = 0.40f; break;
            case BFSegmentRole.Weapon:  share = 0.30f; transfer = 0.35f; break;

            // A canopy is thin, and a crew hit through it should be decisive -
            // most of the damage carries straight to the vehicle.
            case BFSegmentRole.Cockpit: share = 0.25f; transfer = 0.90f; break;
            case BFSegmentRole.Glass:   share = 0.10f; transfer = 0.80f; break;

            case BFSegmentRole.Wing:    share = 0.30f; transfer = 0.45f; break;
            default:                    share = 1.00f; transfer = 1.00f; break;   // hull
        }

        float health = vehicleHealth * share;
        Zones[role] = new Zone
        {
            Role = role,
            MaxHealth = health,
            Health = health,
            HullTransfer = transfer,
        };
    }

    /// <summary>
    /// Apply damage that landed on a particular part.
    /// </summary>
    /// <returns>
    /// How much should count against the vehicle's own health. Callers pass
    /// this to <c>PhxVehicle.AddDamage</c> rather than the raw amount, which
    /// is what keeps the vehicle's existing death path in charge.
    /// </returns>
    public float ApplyZoneDamage(BFSegmentIdentity segment, float damage)
    {
        if (damage <= 0f) return 0f;

        BFSegmentRole role = segment != null ? segment.Role : BFSegmentRole.Unknown;
        if (role == BFSegmentRole.Unknown || !Zones.TryGetValue(role, out Zone zone))
        {
            // Hit a part with no zone of its own - unlabelled geometry, or a
            // vehicle whose model carries no tags. That is the hull.
            if (!Zones.TryGetValue(BFSegmentRole.Hull, out zone)) return damage;
        }

        bool wasAlive = !zone.IsDisabled;
        zone.Health = Mathf.Max(0f, zone.Health - damage);

        if (wasAlive && zone.IsDisabled)
        {
            Recompute();
            OnZoneDisabled?.Invoke(zone);

            Debug.Log($"[Phoenix] {name}: {zone.Role} disabled " +
                      $"(mobility {MobilityFactor:F2}, can fire {CanFire}).");
        }

        return damage * zone.HullTransfer;
    }

    /// <summary>Health of one zone as a fraction, or 1 when it has none.</summary>
    public float ZoneFraction(BFSegmentRole role)
    {
        return Zones.TryGetValue(role, out Zone zone) && zone.MaxHealth > 0f
            ? zone.Health / zone.MaxHealth
            : 1f;
    }

    /// <summary>
    /// Recompute the effects zone damage has on the whole vehicle.
    /// </summary>
    /// <remarks>
    /// Mobility takes the worst of whatever moves the vehicle rather than
    /// averaging: an AT-ST with one good leg and one shattered one is not
    /// half-mobile, it is crippled. Firing needs one working weapon-bearing
    /// zone, not all of them.
    /// </remarks>
    void Recompute()
    {
        float mobility = 1f;
        foreach (BFSegmentRole role in MobilityRoles)
        {
            if (!Zones.TryGetValue(role, out Zone zone)) continue;

            mobility = Mathf.Min(mobility, Mathf.Lerp(0.25f, 1f, zone.Health / Mathf.Max(1f, zone.MaxHealth)));
        }
        MobilityFactor = mobility;

        bool anyWeapon = false;
        bool hasWeaponZone = false;
        foreach (BFSegmentRole role in WeaponRoles)
        {
            if (!Zones.TryGetValue(role, out Zone zone)) continue;

            hasWeaponZone = true;
            if (!zone.IsDisabled) anyWeapon = true;
        }
        CanFire = !hasWeaponZone || anyWeapon;
    }

    static readonly BFSegmentRole[] MobilityRoles =
    {
        BFSegmentRole.Engine, BFSegmentRole.Leg, BFSegmentRole.Wheel, BFSegmentRole.Track,
    };

    static readonly BFSegmentRole[] WeaponRoles =
    {
        BFSegmentRole.Turret, BFSegmentRole.Weapon,
    };
}
