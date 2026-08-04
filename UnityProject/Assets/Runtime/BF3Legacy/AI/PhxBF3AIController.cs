using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Modernized soldier AI. Replaces the stub PhxSoldierAIController with a real
/// combat brain: target acquisition with line-of-sight, reaction time, burst
/// fire discipline, strafing / cover behavior, objective seeking (command post
/// capture) and squad flanking - all parameterized by PhxAISkillProfile so the
/// AI director can scale from "Classic" to "Legendary".
/// </summary>
public class PhxBF3AIController : PhxAIController
{
    enum PhxAIState
    {
        SeekObjective,   // move to assigned/nearest capturable command post
        Engage,          // fight a visible enemy
        Capture,         // stand in CP region until captured
    }

    public PhxAISkillProfile Skill = PhxAISkillProfile.ForDifficulty(2).WithJitter();

    // Set by PhxAIDirector for squad play
    public PhxCommandpost AssignedObjective;
    public Vector3 FlankOffset;   // world-space detour offset when approaching contested areas

    PhxAIState State = PhxAIState.SeekObjective;

    // combat memory
    IPhxControlableInstance TargetPawn;
    float ReactionTimer;
    float BurstTimer;
    float BurstPauseTimer;
    float StrafeDir = 1f;
    float StrafeTimer;
    float RetargetTimer;

    static readonly Collider[] OverlapCache = new Collider[128];


    public PhxBF3AIController()
    {
        Skill = PhxAIDirector.GetSkillProfile();
        PhxAIDirector.Register(this);
    }

    public override Vector3 GetAimPosition()
    {
        if (TargetPawn != null && TargetPawn.GetInstance() != null)
        {
            // center-mass aim with skill-based error cone
            Vector3 targetPos = TargetPawn.GetInstance().transform.position + Vector3.up * 1.2f;
            Vector3 dir = (targetPos - PawnPosition()).normalized;
            dir = ApplyAimError(dir, Skill.AimErrorDegrees);
            return PawnPosition() + dir * Vector3.Distance(PawnPosition(), targetPos);
        }
        return PawnPosition() + ViewDirection * 1000f;
    }

    public override void Tick(float deltaTime)
    {
        // Note: intentionally NOT calling PhxAIController.Tick - it overwrites
        // ViewDirection from the legacy Target field.
        if (Pawn == null)
        {
            return;
        }

        Team = Pawn.GetInstance().Team;
        Reload = false;

        // periodic (re)targeting - full scans are too costly per frame
        RetargetTimer -= deltaTime;
        if (RetargetTimer <= 0f)
        {
            RetargetTimer = 0.4f;
            AcquireTarget();
        }

        switch (State)
        {
            case PhxAIState.SeekObjective: TickSeekObjective(deltaTime); break;
            case PhxAIState.Engage: TickEngage(deltaTime); break;
            case PhxAIState.Capture: TickCapture(deltaTime); break;
        }
    }

    // ---------------------------------------------------------------- states

    void TickSeekObjective(float deltaTime)
    {
        ShootPrimary = false;

        if (TargetPawn != null)
        {
            ReactionTimer = Skill.ReactionTime;
            State = PhxAIState.Engage;
            return;
        }

        PhxCommandpost cp = GetObjective();
        if (cp == null)
        {
            // nothing to do - hold position, scan around
            MoveDirection = Vector2.zero;
            return;
        }

        Vector3 goal = cp.transform.position;

        // flanking: approach via a lateral detour until close
        float dist = Vector3.Distance(PawnPosition(), goal);
        if (FlankOffset != Vector3.zero && dist > 30f)
        {
            goal += FlankOffset;
        }

        if (dist < 8f)
        {
            CapturePost = cp;
            State = PhxAIState.Capture;
            MoveDirection = Vector2.zero;
            return;
        }

        MoveTowards(goal);
    }

    void TickEngage(float deltaTime)
    {
        if (TargetPawn == null || TargetPawn.GetInstance() == null)
        {
            ShootPrimary = false;
            State = PhxAIState.SeekObjective;
            return;
        }

        Vector3 targetPos = TargetPawn.GetInstance().transform.position;
        ViewDirection = (targetPos + Vector3.up * 1.2f - PawnPosition()).normalized;

        // reaction delay before opening fire
        if (ReactionTimer > 0f)
        {
            ReactionTimer -= deltaTime;
            ShootPrimary = false;
            return;
        }

        // burst fire discipline
        if (BurstPauseTimer > 0f)
        {
            BurstPauseTimer -= deltaTime;
            ShootPrimary = false;
        }
        else
        {
            BurstTimer += deltaTime;
            ShootPrimary = true;
            if (BurstTimer >= Skill.BurstLength)
            {
                BurstTimer = 0f;
                BurstPauseTimer = Skill.BurstPause;

                // consider reloading in the pause
                IPhxWeapon weapon = Pawn.GetPrimaryWeapon();
                if (weapon != null && weapon.GetMagazineAmmo() <= weapon.GetMagazineSize() / 4)
                {
                    Reload = true;
                }
            }
        }

        // combat movement: strafe and use cover based on skill
        StrafeTimer -= deltaTime;
        if (StrafeTimer <= 0f)
        {
            StrafeTimer = Random.Range(0.8f, 1.8f);
            StrafeDir = Random.value < 0.5f ? -1f : 1f;
            Crouch = Random.value < Skill.CoverUsage;
        }

        float dist = Vector3.Distance(PawnPosition(), targetPos);
        Vector2 move = Vector2.zero;
        if (Random.value < Skill.StrafeAggression)
        {
            move.x = StrafeDir;
        }
        if (dist > 25f) move.y = 1f;        // close in
        else if (dist < 8f) move.y = -0.7f; // back off
        MoveDirection = move;
    }

    void TickCapture(float deltaTime)
    {
        ShootPrimary = false;
        MoveDirection = Vector2.zero;

        if (TargetPawn != null)
        {
            State = PhxAIState.Engage;
            return;
        }

        PhxCommandpost cp = CapturePost;
        if (cp == null || cp.Team == Team)
        {
            // captured (or lost the reference) - find the next objective
            CapturePost = null;
            AssignedObjective = null;
            State = PhxAIState.SeekObjective;
        }
    }

    // ---------------------------------------------------------------- helpers

    Vector3 PawnPosition()
    {
        return Pawn.GetInstance().transform.position;
    }

    void MoveTowards(Vector3 goal)
    {
        Vector3 toGoal = goal - PawnPosition();
        toGoal.y = 0f;
        if (toGoal.sqrMagnitude < 0.01f)
        {
            MoveDirection = Vector2.zero;
            return;
        }
        ViewDirection = toGoal.normalized;
        MoveDirection = new Vector2(0f, 1f);
        Sprint = toGoal.magnitude > 40f;
    }

    void AcquireTarget()
    {
        TargetPawn = null;

        int count = Physics.OverlapSphereNonAlloc(PawnPosition(), Skill.DetectionRange, OverlapCache);
        float bestDist = float.MaxValue;

        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null) continue;
            if (soldier.Team == Team || soldier.Team == 0) continue;
            if (ReferenceEquals(soldier, Pawn)) continue;

            float d = Vector3.Distance(PawnPosition(), soldier.transform.position);
            if (d >= bestDist) continue;

            if (HasLineOfSight(soldier.transform.position + Vector3.up * 1.2f))
            {
                bestDist = d;
                TargetPawn = soldier;
            }
        }

        if (TargetPawn != null && State == PhxAIState.SeekObjective)
        {
            ReactionTimer = Skill.ReactionTime;
        }
    }

    bool HasLineOfSight(Vector3 targetPoint)
    {
        Vector3 eye = PawnPosition() + Vector3.up * 1.6f;
        Vector3 dir = targetPoint - eye;
        if (Physics.Raycast(eye, dir.normalized, out RaycastHit hit, dir.magnitude))
        {
            // if we hit something that's not (part of) the target, sight is blocked
            return hit.collider.GetComponentInParent<PhxSoldier>() != null;
        }
        return true;
    }

    PhxCommandpost GetObjective()
    {
        if (AssignedObjective != null && AssignedObjective.Team != Team)
        {
            return AssignedObjective;
        }

        // fallback: nearest enemy/neutral command post
        PhxCommandpost[] posts = PhxGame.GetScene()?.GetCommandPosts();
        if (posts == null) return null;

        PhxCommandpost best = null;
        float bestDist = float.MaxValue;
        foreach (PhxCommandpost cp in posts)
        {
            if (cp.Team == Team) continue;
            float d = Vector3.Distance(PawnPosition(), cp.transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = cp;
            }
        }
        return best;
    }

    static Vector3 ApplyAimError(Vector3 dir, float errorDegrees)
    {
        if (errorDegrees <= 0f) return dir;
        Quaternion error = Quaternion.Euler(
            Random.Range(-errorDegrees, errorDegrees),
            Random.Range(-errorDegrees, errorDegrees),
            0f);
        return error * dir;
    }
}
