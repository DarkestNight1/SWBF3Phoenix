using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A destructible capital ship, implementing the Battlefront III / Elite Squadron
/// assault flow on top of the Phoenix runtime:
///
///   1. Shielded       - hull immune, shields must be depleted first. Shields drain
///                       from space weapon fire or (instantly) from a ground ion cannon.
///   2. ShieldsDown    - hangar shields drop, enemies can land inside. Hull takes
///                       damage, but the ship can only be killed from within.
///   3. Breached       - internal critical subsystems (auto-turret mainframe, engines,
///                       shield generator, life support) can be sabotaged, weakening
///                       the ship and exposing the main reactor.
///   4. ReactorExposed - the main reactor can be attacked directly.
///   5. Dying          - reactor destroyed; staged breakup sequence plays
///                       (see PhxCapitalShipDestruction), ship is lost.
///
/// Ships register themselves per team so game modes, the AI director, HUD and
/// ion cannons can find them.
/// </summary>
public class PhxCapitalShip : MonoBehaviour, IPhxDamageableInstance, IPhxTickable
{
    public enum PhxShipState
    {
        Shielded,
        ShieldsDown,
        Breached,
        ReactorExposed,
        Dying,
        Destroyed
    }

    [Header("Identity")]
    public string ShipName = "Capital Ship";
    public int Team = 1;

    [Header("Shields")]
    public float MaxShields = 12000f;
    public float ShieldRegenPerSecond = 40f;
    // Once shields hit 0, they stay down this long before regen may resume.
    // Elite Squadron ships never regenerated after an ion hit; a value < 0 disables regen once down.
    public float ShieldDownTime = -1f;

    [Header("Hull")]
    public float MaxHull = 30000f;
    // External fire alone can never fully kill the ship - it clamps at this
    // fraction until the reactor is destroyed from inside (BF3 design).
    [Range(0f, 1f)]
    public float MinHullFromExternalDamage = 0.25f;

    [Header("Wiring")]
    public Transform HangarEntrance;      // where attackers land once shields are down
    public GameObject HangarShieldVisual; // translucent barrier, disabled on ShieldsDown

    public float CurShields { get; private set; }
    public float CurHull { get; private set; }
    public PhxShipState State { get; private set; } = PhxShipState.Shielded;

    public event Action<PhxCapitalShip, PhxShipState> OnStateChanged;
    public event Action<PhxCapitalShip> OnShipDestroyed;

    readonly List<PhxCapitalShipSubsystem> Subsystems = new List<PhxCapitalShipSubsystem>();
    float ShieldRegenBlockedTimer;
    bool ShieldsPermanentlyDown;

    // global registry
    static readonly List<PhxCapitalShip> All = new List<PhxCapitalShip>();

    public static IReadOnlyList<PhxCapitalShip> GetAll() => All;

    public static PhxCapitalShip GetShipOfTeam(int team)
    {
        for (int i = 0; i < All.Count; ++i)
        {
            if (All[i].Team == team && All[i].State != PhxShipState.Destroyed)
            {
                return All[i];
            }
        }
        return null;
    }


    void Awake()
    {
        CurShields = MaxShields;
        CurHull = MaxHull;
        All.Add(this);

        GetComponentsInChildren(true, Subsystems);
        foreach (PhxCapitalShipSubsystem sys in Subsystems)
        {
            sys.Ship = this;
        }
    }

    void OnDestroy()
    {
        All.Remove(this);
    }

    void Update()
    {
        Tick(Time.deltaTime);
    }

    public void Tick(float deltaTime)
    {
        if (State == PhxShipState.Dying || State == PhxShipState.Destroyed)
        {
            return;
        }

        // shield regeneration
        if (CurShields < MaxShields && !ShieldsPermanentlyDown)
        {
            if (ShieldRegenBlockedTimer > 0f)
            {
                ShieldRegenBlockedTimer -= deltaTime;
            }
            else
            {
                CurShields = Mathf.Min(CurShields + ShieldRegenPerSecond * deltaTime, MaxShields);
                if (State == PhxShipState.ShieldsDown && CurShields > MaxShields * 0.1f)
                {
                    SetState(PhxShipState.Shielded);
                }
            }
        }
    }

    /// <summary>External damage (starfighters, turrets, other capital ships).</summary>
    public void AddDamage(float damage)
    {
        if (State == PhxShipState.Dying || State == PhxShipState.Destroyed) return;

        if (CurShields > 0f)
        {
            CurShields = Mathf.Max(CurShields - damage, 0f);
            ShieldRegenBlockedTimer = 5f;
            if (CurShields <= 0f)
            {
                DropShields(false);
            }
            return;
        }

        // Hull damage from outside can cripple, but never finish the ship
        float minHull = MaxHull * MinHullFromExternalDamage;
        CurHull = Mathf.Max(CurHull - damage, State >= PhxShipState.ReactorExposed ? 0f : minHull);
        if (CurHull <= 0f)
        {
            BeginDestruction();
        }
    }

    /// <summary>
    /// Ion cannon strike (Elite Squadron): instantly strips shields and keeps them down.
    /// </summary>
    public void IonStrike()
    {
        if (State == PhxShipState.Dying || State == PhxShipState.Destroyed) return;

        CurShields = 0f;
        DropShields(true);
    }

    void DropShields(bool permanent)
    {
        ShieldsPermanentlyDown = permanent || ShieldDownTime < 0f;
        ShieldRegenBlockedTimer = ShieldsPermanentlyDown ? float.MaxValue : ShieldDownTime;

        if (HangarShieldVisual != null)
        {
            HangarShieldVisual.SetActive(false);
        }
        if (State == PhxShipState.Shielded)
        {
            SetState(PhxShipState.ShieldsDown);
        }
    }

    /// <summary>Called by subsystems when they die.</summary>
    internal void NotifySubsystemDestroyed(PhxCapitalShipSubsystem sys)
    {
        if (sys.Type == PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor)
        {
            BeginDestruction();
            return;
        }

        if (State == PhxShipState.ShieldsDown)
        {
            SetState(PhxShipState.Breached);
        }

        // Reactor exposes once all non-reactor critical systems are gone
        bool allCriticalDown = true;
        foreach (PhxCapitalShipSubsystem s in Subsystems)
        {
            if (s.Type != PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor && s.IsCritical && s.IsAlive)
            {
                allCriticalDown = false;
                break;
            }
        }
        if (allCriticalDown && State < PhxShipState.ReactorExposed)
        {
            foreach (PhxCapitalShipSubsystem s in Subsystems)
            {
                if (s.Type == PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor)
                {
                    s.SetInvulnerable(false);
                }
            }
            SetState(PhxShipState.ReactorExposed);
        }
    }

    public void BeginDestruction()
    {
        if (State == PhxShipState.Dying || State == PhxShipState.Destroyed) return;
        if (!PhxBF3.Config.CapitalShipDestruction)
        {
            // Feature disabled: ship just counts as lost, no breakup sequence
            SetState(PhxShipState.Destroyed);
            OnShipDestroyed?.Invoke(this);
            return;
        }

        CurHull = 0f;
        SetState(PhxShipState.Dying);

        PhxCapitalShipDestruction seq = gameObject.GetComponent<PhxCapitalShipDestruction>();
        if (seq == null)
        {
            seq = gameObject.AddComponent<PhxCapitalShipDestruction>();
        }
        seq.Play(this, () =>
        {
            SetState(PhxShipState.Destroyed);
            OnShipDestroyed?.Invoke(this);
        });
    }

    void SetState(PhxShipState newState)
    {
        if (State == newState) return;
        State = newState;
        Debug.Log($"[BF3Legacy] {ShipName} (Team {Team}) -> {newState}");
        OnStateChanged?.Invoke(this, newState);
    }

    public float GetShieldPercent() => MaxShields <= 0f ? 0f : CurShields / MaxShields;
    public float GetHullPercent() => MaxHull <= 0f ? 0f : CurHull / MaxHull;
    public bool CanBeBoarded() => State >= PhxShipState.ShieldsDown && State < PhxShipState.Dying;
}
