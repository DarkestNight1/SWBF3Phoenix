using System;
using UnityEngine;

/// <summary>
/// A damageable subsystem inside (or on) a capital ship. Mirrors the BF2 space
/// assault critical systems plus BF3/Elite Squadron's main reactor, which is
/// the actual kill objective.
///
/// Place these on child objects of a PhxCapitalShip with a Collider so
/// projectiles can hit them.
/// </summary>
public class PhxCapitalShipSubsystem : MonoBehaviour, IPhxDamageableInstance
{
    public enum PhxSubsystemType
    {
        ShieldGenerator,
        Engines,
        AutoTurretMainframe,
        LifeSupport,
        Communications,
        Bridge,
        MainReactor,
    }

    public PhxSubsystemType Type = PhxSubsystemType.ShieldGenerator;
    public float MaxHealth = 2000f;

    // Critical subsystems gate the reactor: all of them must be destroyed
    // before the reactor drops its invulnerability.
    public bool IsCritical = true;

    // Internal systems can only be damaged by attackers who boarded the ship
    // (i.e. after shields are down). External ones (e.g. engines) are always fair game.
    public bool IsInternal = true;

    [NonSerialized] public PhxCapitalShip Ship;

    public float CurHealth { get; private set; }
    public bool IsAlive => CurHealth > 0f;

    public event Action<PhxCapitalShipSubsystem> OnDestroyed;

    bool Invulnerable;


    // NOTE: reactor invulnerability is applied by PhxCapitalShip when the
    // subsystem is registered - Awake() runs on AddComponent, before Type is set.
    void Awake()
    {
        CurHealth = MaxHealth;
    }

    public void SetInvulnerable(bool value)
    {
        Invulnerable = value;
    }

    public void AddDamage(float damage)
    {
        if (!IsAlive || Invulnerable || Ship == null) return;

        // Internal systems are protected while the ship is still shielded
        if (IsInternal && !Ship.CanBeBoarded()) return;

        CurHealth = Mathf.Max(CurHealth - damage, 0f);
        if (CurHealth <= 0f)
        {
            OnSubsystemKilled();
        }
    }

    void OnSubsystemKilled()
    {
        // Side effects per subsystem type. These intentionally act on the ship,
        // gameplay code can subscribe to OnDestroyed for scoring / voice-over.
        switch (Type)
        {
            case PhxSubsystemType.ShieldGenerator:
                Ship.IonStrike(); // shields down for good
                break;
            case PhxSubsystemType.Engines:
                // Listing drift is handled by the destruction sequence; a ship
                // with dead engines already starts to slowly drift.
                break;
        }

        OnDestroyed?.Invoke(this);
        Ship.NotifySubsystemDestroyed(this);
    }
}
