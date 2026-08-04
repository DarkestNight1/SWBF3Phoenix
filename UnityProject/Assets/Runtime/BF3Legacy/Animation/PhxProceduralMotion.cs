using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Modernizes the feel of the stock 2005 animations without touching the
/// loaded animation banks: a light procedural layer applied AFTER the
/// animator each frame (LateUpdate), the way contemporary shooters do it.
///
///  - torso lean into lateral movement (strafing reads as weight shift)
///  - subtle idle sway/breathing on the spine
///  - recoil kick on shots, recovering exponentially
///
/// Everything is additive and small (a few degrees), so the original
/// SWBF2 keyframes stay recognizable. PhxProceduralMotionManager attaches
/// this to every soldier automatically when Config.ProceduralAnimation is on.
/// </summary>
public class PhxProceduralMotion : MonoBehaviour
{
    const float LeanMaxDegrees = 7f;
    const float LeanResponsiveness = 6f;
    const float SwayDegrees = 1.2f;
    const float RecoilDegrees = 3.5f;
    const float RecoilRecovery = 12f;

    PhxSoldier Soldier;
    Transform Spine;
    Vector3 LastPosition;
    float CurrentLean;
    float RecoilKick;
    float SwayPhase;
    bool Hooked;


    void Start()
    {
        Soldier = GetComponent<PhxSoldier>();
        LastPosition = transform.position;

        // find a spine-ish bone on the SWBF2 human skeleton
        foreach (Transform t in GetComponentsInChildren<Transform>())
        {
            string lower = t.name.ToLowerInvariant();
            if (lower.Contains("spine") || lower.Contains("ribcage"))
            {
                Spine = t;
                break;
            }
        }

        // recoil hook
        if (Soldier != null)
        {
            IPhxWeapon weapon = Soldier.GetPrimaryWeapon();
            if (weapon != null)
            {
                weapon.OnShot(OnShotFired);
                Hooked = true;
            }
        }
    }

    void OnShotFired()
    {
        RecoilKick = Mathf.Min(RecoilKick + RecoilDegrees, RecoilDegrees * 2f);
    }

    void LateUpdate()
    {
        if (Spine == null || (Soldier != null && Soldier.IsDead))
        {
            return;
        }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // late hook in case the weapon wasn't ready in Start
        if (!Hooked && Soldier != null)
        {
            IPhxWeapon weapon = Soldier.GetPrimaryWeapon();
            if (weapon != null)
            {
                weapon.OnShot(OnShotFired);
                Hooked = true;
            }
        }

        // lateral velocity in local space -> lean target
        Vector3 velocity = (transform.position - LastPosition) / dt;
        LastPosition = transform.position;
        float lateral = Vector3.Dot(velocity, transform.right);
        float targetLean = Mathf.Clamp(-lateral * 1.2f, -LeanMaxDegrees, LeanMaxDegrees);
        CurrentLean = Mathf.Lerp(CurrentLean, targetLean, dt * LeanResponsiveness);

        // idle sway
        SwayPhase += dt;
        float sway = Mathf.Sin(SwayPhase * 1.7f) * SwayDegrees * 0.5f
                   + Mathf.Sin(SwayPhase * 0.9f) * SwayDegrees * 0.5f;

        // recoil recovery
        RecoilKick = Mathf.Lerp(RecoilKick, 0f, dt * RecoilRecovery);

        // additive on top of the animator's pose (we run in LateUpdate)
        Spine.localRotation = Spine.localRotation *
            Quaternion.Euler(-RecoilKick + sway * 0.4f, 0f, CurrentLean);
    }
}

/// <summary>Attaches PhxProceduralMotion to every living soldier.</summary>
public class PhxProceduralMotionManager : MonoBehaviour
{
    float ScanTimer;

    void Update()
    {
        ScanTimer -= Time.deltaTime;
        if (ScanTimer > 0f) return;
        ScanTimer = 2f;

        foreach (PhxSoldier soldier in FindObjectsOfType<PhxSoldier>())
        {
            if (soldier.GetComponent<PhxProceduralMotion>() == null)
            {
                soldier.gameObject.AddComponent<PhxProceduralMotion>();
            }
        }
    }
}
