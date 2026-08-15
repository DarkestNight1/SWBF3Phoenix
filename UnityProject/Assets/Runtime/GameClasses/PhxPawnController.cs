using System;
using System.Collections.Generic;
using UnityEngine;

public abstract class PhxPawnController
{
    public IPhxControlableInstance Pawn { get; private set; }

    public float mouseX;
    public float mouseY;

    public bool SwitchSeat;

    public bool ShootPrimary;
    public bool ShootSecondary;
    public bool Crouch;
    public bool Jump;
    public bool Sprint;
    public bool Reload;
    public bool NextPrimaryWeapon;
    public bool NextSecondaryWeapon;


    /// <summary>Fire the hero's offensive force power this frame.</summary>
    /// <remarks>
    /// PhxForcePowers.Use had no callers and no binding, so a hero could be
    /// granted Push, Lightning and Choke and use none of them.
    ///
    /// A dedicated key rather than secondary fire, which the plan for this
    /// first suggested: ShootSecondary already fires the soldier's SECOND
    /// WEAPON, and a hero carrying anything besides the saber would have the
    /// two fighting over one button. Edge-triggered - a power is a press, not
    /// a hold.
    /// </remarks>
    public bool UseForcePower;

    /// <summary>
    /// Force jump this frame. Separate from Jump so holding space does not
    /// drain the same stamina bar that sprinting uses.
    /// </summary>
    public bool UseForceJump;
    public bool Enter;

    public Vector2 MoveDirection;
    public Vector3 ViewDirection;
    protected PhxInstance Target;

    public PhxCommandpost CapturePost;
    public int Team = 0;

    // Scoreboard, per BF2: Score is the objective/kill points shown in the
    // leftmost column, Kills and Deaths the two beside it. Kept on the
    // controller rather than the pawn so a player's tally survives respawning,
    // which replaces the pawn each time.
    public string DisplayName = "";
    public int Score = 0;
    public int Kills = 0;
    public int Deaths = 0;



    public bool IsIdle => !ShootPrimary && !Crouch && MoveDirection == Vector2.zero;
    public float IdleTime { get; private set; }


    public abstract Vector3 GetAimPosition();

    public PhxInstance GetAimObject()
    {
        return Target;
    }

    // For assignment, use IPhxControlableInstance.Assign()!
    public void SetPawn(IPhxControlableInstance pawn)
    {
        Debug.Assert(pawn.GetController() == this);

        Pawn = pawn;
        ViewDirection = Pawn.GetInstance().transform.forward;
    }

    // For un-assignment, use IPhxControlableInstance.UnAssign()!
    public void RemovePawn()
    {
        Pawn = null;
        CapturePost = null;
    }

    public void ResetIdleTime()
    {
        IdleTime = 0f;
    }

    public virtual void Tick(float deltaTime)
    {
        if (IsIdle)
        {
            IdleTime += deltaTime;
        }
        else
        {
            IdleTime = 0f;
        }
    }
}
