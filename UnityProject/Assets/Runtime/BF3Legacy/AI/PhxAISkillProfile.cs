using System;
using UnityEngine;

/// <summary>
/// Tunable AI skill parameters. The AI director hands these out based on the
/// selected difficulty tier, with per-unit jitter so squads don't act in
/// lockstep. Higher tiers react faster, aim tighter, use cover and flank.
/// </summary>
[Serializable]
public struct PhxAISkillProfile
{
    public float ReactionTime;        // seconds from spotting to first shot
    public float AimErrorDegrees;     // random cone applied to aim
    public float BurstLength;         // seconds of sustained fire
    public float BurstPause;          // seconds between bursts
    public float DetectionRange;      // spotting distance
    public float StrafeAggression;    // 0..1 chance-weight to strafe in combat
    public float CoverUsage;          // 0..1 chance-weight to crouch/use cover
    public float FlankTendency;       // 0..1 chance a squad detours around a fight
    public float ObjectiveFocus;      // 0..1 how strongly CP capture beats hunting kills

    public static PhxAISkillProfile ForDifficulty(int difficulty)
    {
        switch (difficulty)
        {
            case 0: // Classic - close to the 2005 experience
                return new PhxAISkillProfile
                {
                    ReactionTime = 0.9f,
                    AimErrorDegrees = 7f,
                    BurstLength = 0.7f,
                    BurstPause = 1.4f,
                    DetectionRange = 45f,
                    StrafeAggression = 0.1f,
                    CoverUsage = 0.1f,
                    FlankTendency = 0.05f,
                    ObjectiveFocus = 0.6f,
                };
            case 1: // Veteran
                return new PhxAISkillProfile
                {
                    ReactionTime = 0.55f,
                    AimErrorDegrees = 4.5f,
                    BurstLength = 1.0f,
                    BurstPause = 1.0f,
                    DetectionRange = 65f,
                    StrafeAggression = 0.35f,
                    CoverUsage = 0.35f,
                    FlankTendency = 0.2f,
                    ObjectiveFocus = 0.7f,
                };
            case 2: // Elite
                return new PhxAISkillProfile
                {
                    ReactionTime = 0.3f,
                    AimErrorDegrees = 2.5f,
                    BurstLength = 1.4f,
                    BurstPause = 0.6f,
                    DetectionRange = 90f,
                    StrafeAggression = 0.6f,
                    CoverUsage = 0.55f,
                    FlankTendency = 0.4f,
                    ObjectiveFocus = 0.8f,
                };
            default: // Legendary
                return new PhxAISkillProfile
                {
                    ReactionTime = 0.15f,
                    AimErrorDegrees = 1.2f,
                    BurstLength = 1.8f,
                    BurstPause = 0.35f,
                    DetectionRange = 120f,
                    StrafeAggression = 0.8f,
                    CoverUsage = 0.7f,
                    FlankTendency = 0.6f,
                    ObjectiveFocus = 0.9f,
                };
        }
    }

    /// <summary>Per-unit variation so a squad doesn't behave identically.</summary>
    public PhxAISkillProfile WithJitter(float amount = 0.15f)
    {
        PhxAISkillProfile p = this;
        p.ReactionTime *= UnityEngine.Random.Range(1f - amount, 1f + amount);
        p.AimErrorDegrees *= UnityEngine.Random.Range(1f - amount, 1f + amount);
        p.BurstLength *= UnityEngine.Random.Range(1f - amount, 1f + amount);
        p.BurstPause *= UnityEngine.Random.Range(1f - amount, 1f + amount);
        p.StrafeAggression = Mathf.Clamp01(p.StrafeAggression * UnityEngine.Random.Range(1f - amount, 1f + amount));
        p.CoverUsage = Mathf.Clamp01(p.CoverUsage * UnityEngine.Random.Range(1f - amount, 1f + amount));
        return p;
    }
}
