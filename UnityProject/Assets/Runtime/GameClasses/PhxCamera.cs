using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PhxCamera : MonoBehaviour
{
    public enum CamMode
    {
        Fixed,
        Free,
        Follow,
        Track,

        /// <summary>
        /// Player death. Pulls back off the body and drifts, biased toward
        /// whoever did it - BF2 shows you your corpse and your killer rather
        /// than freezing on a first-person view of the ground.
        /// </summary>
        Death,
    }
                     
    public CamMode    Mode { get; private set; } = CamMode.Free;
    public float      FreeMoveSpeed              = 100.0f;
    public float      FreeRotationSpeed          = 5.0f;
    public Vector3    PositionOffset             = new Vector3(0f, 2f, -2f);
    public float      FollowSpeed                = 100.0f;
    public float      MouseSensitivity           = 5f;

    // Death camera framing. Roughly BF2's: a few metres back, a little above,
    // turning slowly so the moment reads as deliberate.
    public float      DeathCamDistance           = 4.5f;
    public float      DeathCamHeight             = 2.0f;
    public float      DeathCamDriftDegrees       = 8f;


    IPhxControlableInstance FollowInstance;

    IPhxTrackable TrackableInstance;



    public void Track(IPhxTrackable Track)
    {
        Mode = CamMode.Track;
        TrackableInstance = Track;
    }


    public void Follow(IPhxControlableInstance follow)
    {
        Mode = CamMode.Follow;
        FollowInstance = follow;
    }

    public void Fixed()
    {
        Mode = CamMode.Fixed;
        FollowInstance = null;
    }

    public void Fixed(PhxTransform t)
    {
        Fixed();
        transform.position = t.Position;
        transform.rotation = t.Rotation;
    }


    // --- death camera --------------------------------------------------
    Transform DeathVictim;
    Transform DeathKiller;
    float DeathCamTime;
    Vector3 DeathCamPivot;

    /// <summary>
    /// Watch the player's own death.
    /// </summary>
    /// <remarks>
    /// BF2 pulls the view off the body and holds it there, turned toward
    /// whoever killed you, until the spawn screen comes up. Phoenix left the
    /// camera in Follow on a pawn that had just been unassigned, so the view
    /// stayed locked to a corpse's last aim direction - which reads as the game
    /// hanging rather than as dying.
    ///
    /// The killer is optional: a fall, a vehicle explosion or a stray
    /// explosion has none, and in that case the camera simply looks down at
    /// the body.
    /// </remarks>
    public void Death(Transform victim, Transform killer)
    {
        Mode = CamMode.Death;
        DeathVictim = victim;
        DeathKiller = killer;
        DeathCamTime = 0f;
        FollowInstance = null;

        // Remember where the body is now. The corpse keeps simulating for a
        // couple of seconds and can slide or roll, and a camera that chases it
        // turns a death into a rollercoaster.
        DeathCamPivot = victim != null ? victim.position : transform.position;
    }

    public void Free()
    {
        Mode = CamMode.Free;
        FollowInstance = null;
    }

    public void RotateRigidBodyAroundPointBy(Rigidbody rb, Vector3 origin, Vector3 axis, float angle)
    {
        Quaternion q = Quaternion.AngleAxis(angle, axis);
        rb.MovePosition(q * (rb.transform.position - origin) + origin);
        rb.MoveRotation(rb.transform.rotation * q);
    }


    // ------------------------------------------------------------- shake

    float ShakeAmount;
    float ShakeTimeLeft;
    float ShakeDuration;

    /// <summary>
    /// Rattle the camera for a moment. The strongest request wins.
    /// </summary>
    /// <remarks>
    /// Applied after every mode has placed the camera, as an offset rather
    /// than by moving the camera itself - otherwise the modes that lerp toward
    /// a target would chase the shake and drift.
    /// </remarks>
    public void Shake(float amount, float duration)
    {
        if (amount <= 0f || duration <= 0f) return;

        // A weaker shake arriving mid-shake must not cut the current one short.
        if (amount < ShakeAmount && ShakeTimeLeft > 0f) return;

        ShakeAmount = amount;
        ShakeDuration = duration;
        ShakeTimeLeft = duration;
    }

    void ApplyShake(float deltaTime)
    {
        if (ShakeTimeLeft <= 0f) return;

        ShakeTimeLeft -= deltaTime;
        if (ShakeTimeLeft <= 0f)
        {
            ShakeAmount = 0f;
            return;
        }

        // Fade out over the tail so it settles instead of stopping dead.
        float strength = ShakeAmount * (ShakeTimeLeft / Mathf.Max(0.0001f, ShakeDuration));

        transform.position += transform.rotation * new Vector3(
            Random.Range(-strength, strength),
            Random.Range(-strength, strength),
            0f) * 0.1f;

        transform.rotation *= Quaternion.Euler(
            Random.Range(-strength, strength),
            Random.Range(-strength, strength),
            Random.Range(-strength, strength));
    }

    void LateUpdate()
    {
        float deltaTime = Time.deltaTime;

        if (Mode == CamMode.Free)
        {
            transform.position += transform.forward * Input.GetAxis("Vertical") * deltaTime * FreeMoveSpeed;
            transform.position += transform.right * Input.GetAxis("Horizontal") * deltaTime * FreeMoveSpeed;
            transform.position += transform.up * Input.GetAxis("UpDown") * deltaTime * FreeMoveSpeed;

            if (Input.GetMouseButton(1))
            {
                float newRotationX = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * FreeRotationSpeed;
                float newRotationY = transform.localEulerAngles.x - Input.GetAxis("Mouse Y") * FreeRotationSpeed;
                transform.localEulerAngles = new Vector3(newRotationY, newRotationX, 0f);
            }
        }
        else if (Mode == CamMode.Death)
        {
            DeathCamTime += deltaTime;

            // The body can slide as it settles; follow it loosely so the shot
            // stays framed without chasing every twitch.
            if (DeathVictim != null)
            {
                DeathCamPivot = Vector3.Lerp(DeathCamPivot, DeathVictim.position, deltaTime * 2f);
            }

            Vector3 focus = DeathCamPivot + Vector3.up * 1.0f;

            // Face the killer if there was one, so you can see what hit you.
            // Otherwise hold whatever direction the camera already had, which
            // is where the player was last looking.
            Vector3 viewDir;
            if (DeathKiller != null)
            {
                viewDir = DeathKiller.position - focus;
                viewDir.y = 0f;
            }
            else
            {
                viewDir = transform.forward;
                viewDir.y = 0f;
            }
            if (viewDir.sqrMagnitude < 1e-4f) viewDir = Vector3.forward;
            viewDir.Normalize();

            // Ease back and up over the first second, then drift slowly around
            // the body. Both are what makes it read as a considered shot
            // rather than a camera that stopped.
            float pullBack = Mathf.Lerp(1.5f, DeathCamDistance, Mathf.Clamp01(DeathCamTime));
            float height = Mathf.Lerp(0.5f, DeathCamHeight, Mathf.Clamp01(DeathCamTime));
            float drift = DeathCamTime * DeathCamDriftDegrees;

            Vector3 back = Quaternion.Euler(0f, drift, 0f) * -viewDir;
            Vector3 wanted = focus + back * pullBack + Vector3.up * height;

            // Keep the camera out of geometry - a death cam inside a wall shows
            // the inside of a wall.
            if (Physics.Linecast(focus, wanted, out RaycastHit hit,
                                 PhxLayers.SoldierGround, QueryTriggerInteraction.Ignore))
            {
                wanted = hit.point + hit.normal * 0.3f;
            }

            transform.position = Vector3.Lerp(transform.position, wanted, deltaTime * 4f);

            Vector3 lookAt = focus - transform.position;
            if (lookAt.sqrMagnitude > 1e-4f)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation,
                                                      Quaternion.LookRotation(lookAt),
                                                      deltaTime * 4f);
            }
        }
        else if (Mode == CamMode.Follow)
        {
            // The followed pawn can vanish while the camera is still in Follow
            // mode - death, map unload, leaving play mode - and dereferencing it
            // unguarded throws a NullReferenceException from LateUpdate every
            // single frame.
            var followed = FollowInstance?.GetInstance();
            PhxMatch match = PhxGame.GetMatch();
            if (followed == null || match == null || match.Player == null)
            {
                return;
            }

            Vector3 rotPoint = followed.transform.position;
            rotPoint.y += PositionOffset.y;

            //Vector3 viewDir = (FollowInstance.GetTargetPosition() - rotPoint).normalized;
            Vector3 viewDir = match.Player.ViewDirection;
            Vector3 camTargetPos = rotPoint + viewDir * PositionOffset.z;
            Quaternion camTargetRot = Quaternion.LookRotation(viewDir);
            camTargetPos += camTargetRot * new Vector3(PositionOffset.x, 0f, 0f);

            transform.position = camTargetPos;// Vector3.Lerp(transform.position, camTargetPos, deltaTime * FollowSpeed);
            transform.rotation = camTargetRot;// Quaternion.Slerp(transform.rotation, camTargetRot, deltaTime * FollowSpeed);
        }
        else if (Mode == CamMode.Track)
        {
            // Unguarded, this threw from LateUpdate every frame once the
            // tracked thing was gone - and a vehicle exploding while the player
            // is riding it is the ordinary case, not an edge one. IPhxTrackable
            // is an interface, so a plain != null would not have caught a
            // destroyed Unity object either; the cast is what makes Unity's own
            // null check apply.
            var tracked = TrackableInstance as MonoBehaviour;
            if (TrackableInstance == null || tracked == null)
            {
                // Nothing left to watch. Free leaves the view where it is
                // rather than snapping to the origin.
                Free();
                return;
            }

            transform.rotation = TrackableInstance.GetCameraRotation();
            transform.position = TrackableInstance.GetCameraPosition();
        }

        // Last, so it offsets whatever the active mode decided.
        ApplyShake(deltaTime);
    }
}
