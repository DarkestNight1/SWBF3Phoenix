using System;
using System.Collections.Generic;
using UnityEngine;

public class PhxPlayerController : PhxPawnController
{
    PhxMatch Match => PhxGame.GetMatch();
    PhxCamera Camera => PhxGame.GetCamera();

    public bool CancelPressed { get; private set; }
    Vector3? TargetPos;

    // Starts expired so entering works from the first frame of a match.
    const float VehicleEnterCooldown = 1.0f;
    float VehicleEnterTimer = 0.0f;


    public PhxPlayerController()
    {
        Team = 1;
    }

    public override Vector3 GetAimPosition()
    {
        return TargetPos.HasValue ? TargetPos.Value : Camera.transform.position + ViewDirection * 1000f;
    }

    public override void Tick(float deltaTime)
    {
        base.Tick(deltaTime);

        // UI Controls, are always checked
        CancelPressed = Input.GetButtonDown("Cancel");

        // Nothing to control if either there's no pawn
        // to control or we're currently in some menu
        if (Pawn == null || Cursor.lockState != CursorLockMode.Locked)
        {
            MoveDirection = Vector2.zero;
            Jump = false;
            Sprint = false;
            Reload = false;
            NextPrimaryWeapon = false;
            NextSecondaryWeapon = false;
            ShootPrimary = false;
            ShootSecondary = false;
            Crouch = false;
            return;
        }

        MoveDirection.x = Input.GetAxis("Horizontal");
        MoveDirection.y = Input.GetAxis("Vertical");

        mouseX =  Input.GetAxis("Mouse X");
        mouseY = -Input.GetAxis("Mouse Y");

        Vector2 rotConstraints = Pawn.GetViewConstraint();
        Vector2 maxTurnSpeed = Pawn.GetMaxTurnSpeed();

        // max turn degrees per frame
        Vector2 maxTurn = maxTurnSpeed * deltaTime;
        float turnX = Mathf.Clamp(mouseY * 2f, -maxTurn.x, maxTurn.x);
        float turnY = Mathf.Clamp(mouseX * 2f, -maxTurn.y, maxTurn.y);

        Quaternion rot = Quaternion.LookRotation(ViewDirection);
        Vector3 euler = rot.eulerAngles;
        PhxUtils.SanitizeEuler(ref euler);
        euler.x = Mathf.Clamp(euler.x + turnX, -rotConstraints.x, rotConstraints.x);
        euler.y = Mathf.Clamp(euler.y + turnY, -rotConstraints.y, rotConstraints.y);

        rot = Quaternion.Euler(euler);
        ViewDirection = rot * Vector3.forward;

        UpdatePosture();

        Sprint = Input.GetButton("Sprint");
        Reload = Input.GetButtonDown("Reload");
        NextPrimaryWeapon = Input.GetAxis("WeaponChange") < 0;
        NextSecondaryWeapon = Input.GetAxis("WeaponChange") > 0;
        ShootPrimary = Input.GetButton("Fire1");
        ShootSecondary = Input.GetButton("Fire2");


        Debug.DrawRay(Camera.transform.position, ViewDirection * 1000f, Color.blue);

        // What the aim ray may hit is what a SHOT may hit - see
        // PhxLayers.OrdnanceHits, which derives it from the collision matrix.
        //
        // This was `int layerMask = 7;` commented "ignore vehicle colliders".
        // 7 is not a layer index, it is a bitmask: bits 0, 1 and 2, or Default,
        // TransparentFX and Ignore Raycast. Soldiers are layer 10, terrain 11,
        // buildings 12 - so the ray could hit essentially nothing in the game
        // and TargetPos fell through to null on virtually every frame, sending
        // GetAimPosition to its "1000 metres along the camera ray" fallback.
        //
        // Shots converge from the barrel onto that point, so a target a
        // thousand metres away means the barrel-to-camera offset has barely
        // begun to close at the range people actually fight at. That is why
        // nothing could be killed, and why the damage trace showed every bolt
        // landing in terrain with enemies alive seven metres away.
        if (Physics.Raycast(Camera.transform.position, ViewDirection, out RaycastHit hit,
                            1000f, PhxLayers.OrdnanceHits, QueryTriggerInteraction.Ignore))
        {
            PhxInstance GetInstance(Transform t)
            {
                PhxInstance inst = t.gameObject.GetComponent<PhxInstance>();
                if (inst == null && t.parent != null)
                {
                    return GetInstance(t.parent);
                }
                return inst;
            }

            Target = GetInstance(hit.collider.gameObject.transform);
            TargetPos = hit.point;
        }
        else
        {
            TargetPos = null;
        }
        
        SwitchSeat = Input.GetKeyDown(KeyCode.G);

        // Force powers. Direct key reads, following the G and E precedent
        // above, rather than editing InputManager.asset by hand - a malformed
        // axis entry there breaks input for the whole project and cannot be
        // validated without opening the editor.
        UseForcePower = Input.GetKeyDown(KeyCode.F);
        UseForceJump = Input.GetKeyDown(KeyCode.Q);

        // Zoom on middle mouse, not right.
        //
        // Right mouse is the obvious shooter binding and it is already taken
        // by something that matters more: Fire2 throws the secondary item,
        // which is how grenades, mines and detpacks are used. Putting zoom
        // there as well meant every scope press also lobbed a thermal
        // detonator at your feet.
        ZoomPressed = Input.GetKeyDown(KeyCode.Mouse2);

        // Vehicle enter/exit.
        //
        // GetKeyDown is already edge-triggered, so no debounce is needed to stop
        // repeats; the cooldown exists only to stop you re-entering the vehicle
        // you just stepped out of. It therefore must start on an ACCEPTED press.
        // The previous version restarted it on every press - including presses
        // that found no vehicle - so mashing E next to one (the natural reaction
        // when nothing happens) held the timer permanently above zero and entry
        // never fired at all. It also started at 1.0, dead-zoning the first
        // second of every match.
        VehicleEnterTimer = Mathf.Max(VehicleEnterTimer - Time.deltaTime, -0.01f);

        // Drop a request nobody consumed, so a stale one can't fire later.
        Enter = false;

        if (Input.GetKeyDown(KeyCode.E) && VehicleEnterTimer <= 0.0f)
        {
            Enter = true;
            VehicleEnterTimer = VehicleEnterCooldown;
        }
    }

    // --- Posture -----------------------------------------------------------

    /// <summary>When the last C press was, for detecting the double tap.</summary>
    float LastCrouchTapTime = float.NegativeInfinity;

    /// <summary>
    /// How long after a C press a second one still counts as a double tap.
    /// </summary>
    /// <remarks>
    /// 0.3 s is the usual double-click window. Much shorter and going prone
    /// takes a deliberately fast tap; much longer and toggling crouch off
    /// immediately after turning it on drops you flat instead.
    /// </remarks>
    const float DoubleTapWindow = 0.3f;

    /// <summary>
    /// C to crouch, C twice to go prone, space to get back up.
    /// </summary>
    /// <remarks>
    /// Space is shared with jumping and takes priority when a posture is held:
    /// standing up IS the action there, and the alternative - leaping out of
    /// prone - is not something anyone means by pressing it. From a standing
    /// start it jumps exactly as before.
    ///
    /// Direct key reads, following the G / E / F / Q precedent in this file
    /// rather than hand-editing InputManager.asset, which cannot be validated
    /// without opening the editor.
    /// </remarks>
    void UpdatePosture()
    {
        bool jumpPressed = Input.GetButtonDown("Jump");

        // Space gets you up first, and does not also jump on that press.
        if (jumpPressed && (Crouch || Prone))
        {
            Crouch = false;
            Prone = false;
            Jump = false;
            return;
        }

        Jump = jumpPressed;

        if (!Input.GetKeyDown(KeyCode.C)) return;

        bool doubleTap = Time.time - LastCrouchTapTime <= DoubleTapWindow;
        LastCrouchTapTime = Time.time;

        if (doubleTap)
        {
            // Second tap: all the way down. Crouch stays set because Prone
            // means "crouched and then some" to everything downstream.
            Crouch = true;
            Prone = true;
        }
        else if (Prone)
        {
            // A single tap from prone comes up one step rather than all the
            // way, which is what makes C a posture ladder rather than a
            // three-state cycle you have to think about.
            Prone = false;
            Crouch = true;
        }
        else
        {
            Crouch = !Crouch;
            Prone = false;
        }
    }
}
