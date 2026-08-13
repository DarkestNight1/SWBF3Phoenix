using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;




public enum PilotAnimationType : int 
{
    NinePose,
    FivePose,
    StaticPose,
    None
}


public interface IPhxSeatable
{
    public bool HasAvailableSeat();

    public int GetNextAvailableSeat(int startIndex = -1);

    public bool TrySwitchSeat(int index);

    public PhxSeat TryEnterVehicle(PhxSoldier soldier);

    public bool Eject(int i);

    public Transform GetRootTransform();
}
    

 

public abstract class PhxSeat : IPhxTrackable, IPhxTickable
{    
    protected static PhxCamera CAM => PhxGame.GetCamera();

    // Max 2, min 1
    public List<PhxWeaponSystem> WeaponSystems;

    // Vehicle to which section belongs
    public IPhxSeatable Owner { get; protected set; }

    // Transform that moves with the pilot's input 
    // (will have to build in MountPoint soon for turrets)
    protected Transform BaseTransform;

    public PhxSoldier Occupant;
    protected PhxInstance Aim;

    // eg with flyers one cannot exit
    public bool CanExit = true;

    // Pilot params common to all sections
    public Transform PilotPosition { get; protected set; }
    public string PilotAnimation { get; protected set; }
    public string Pilot9Pose { get; protected set; }

    // How to interpret pilot anim fields
    public PilotAnimationType PilotAnimationType { get; protected set; } = PilotAnimationType.None;

    // Camera control values, common to all sections
    protected Vector3 EyePointOffset;
    protected Vector3 TrackCenter;
    protected Vector3 TrackOffset;
    protected float TiltValue;

    protected Vector2 PitchLimits = new Vector2(0f,0f);
    protected Vector2 YawLimits = new Vector2(0f,0f);

    // Index in OwnerVehicle's Sections list
    protected int Index;

    // Seat 0 is the driver/pilot; the rest are gunner positions
    public int SeatIndex => Index;
    public bool IsDriverSeat => Index == 0;

    // Accumulators for view control
    protected float PitchAccum;
    protected float YawAccum;

    // How far a seat looks for what it is aiming at. Beyond this the shot is
    // treated as going to infinity, which is what the 30km sentinel meant.
    protected const float AimRange = 1000f;

    // View position in local space
    protected Vector3 ViewPoint;

    // View direction in local space
    protected Vector3 ViewDirection = Vector3.forward;

    // When set, weapon systems aim here instead of at the camera-derived
    // target point. AI occupants use this - the default aim path traces from
    // the player camera, which is meaningless for a bot in a turret.
    public Vector3? AimOverride;

    public virtual Vector3 GetCameraPosition()
    {
        return BaseTransform.transform.TransformPoint(ViewPoint);
    }

    public virtual Quaternion GetCameraRotation()
    {
        return Quaternion.LookRotation(BaseTransform.TransformDirection(ViewDirection), Vector3.up);
    }


    public virtual void Tick(float deltaTime)
    {
        PhxPawnController Controller;
        if (Occupant == null || ((Controller = Occupant.GetController()) == null)) 
        {
            return; 
        }

        if (Controller.SwitchSeat && Owner.TrySwitchSeat(Index))
        {
            Occupant = null;
            Controller.SwitchSeat = false;
            return;
        }

        if (Controller.Enter && Owner.Eject(Index))
        {
            Occupant = null;
            Controller.Enter = false;
            return;
        }
        

        PitchAccum += Controller.mouseY;
        PitchAccum = Mathf.Clamp(PitchAccum, PitchLimits.x, PitchLimits.y);

        YawAccum += Controller.mouseX;
        YawAccum = Mathf.Clamp(YawAccum, YawLimits.x, YawLimits.y);        


        // Yaw was accumulated, clamped to the odf's YawLimits, and then never
        // read by anything - so looking left or right in a vehicle seat moved
        // neither the camera nor the aim. Only pitch worked. It is applied
        // about the vehicle's up axis, before pitch, so the two compose the way
        // a turret ring and elevation do.
        Quaternion look = Quaternion.Euler(PitchAccum, YawAccum, 0f);

        // TrackOffset is authored behind the track centre; the importer's
        // handedness flip is on X, not Z.
        Vector3 CameraOffset = TrackOffset;
        CameraOffset.x *= -1f;

        // PitchAccum is already clamped to PitchLimits, which the odf gives in
        // degrees. The old 3x multiplier drove the camera three times past the
        // limit the vehicle author set, which is most of "the camera is off".
        ViewPoint = TrackCenter + look * CameraOffset;
        ViewDirection = look * Quaternion.Euler(-TiltValue, 0f, 0f) * Vector3.forward;

        // Aim from the camera outward, not from the far point outward.
        //
        // This used to cast FROM the 30km target point ALONG (target - camera),
        // i.e. starting 30km away and continuing further out, then took the
        // first hit within 1000m of there. It could only ever hit something
        // 30km behind the player, so what the seat believed it was aiming at
        // was unrelated to where it was pointing.
        Vector3 aimOrigin = BaseTransform.transform.TransformPoint(ViewPoint);
        Vector3 aimDir = BaseTransform.transform.TransformDirection(ViewDirection);
        Vector3 TargetPos = aimOrigin + aimDir * AimRange;

        if (AimOverride.HasValue)
        {
            // AI-controlled seat: aim straight at the designated point
            TargetPos = AimOverride.Value;
        }
        else if (Physics.Raycast(aimOrigin, aimDir, out RaycastHit hit, AimRange,
                                 ~0, QueryTriggerInteraction.Ignore))
        {
            TargetPos = hit.point;

            PhxInstance GetInstance(Transform t)
            {
                PhxInstance inst = t.gameObject.GetComponent<PhxInstance>();
                if (inst == null && t.parent != null)
                {
                    return GetInstance(t.parent);
                }
                return inst;
            }

            Aim = GetInstance(hit.collider.gameObject.transform);
        }
        else
        {
            // Nothing within range: the seat is pointing at open sky, so it has
            // no aim target. Leaving the previous one set made a turret keep
            // reporting whatever it last swept past.
            Aim = null;
        }
        // Update aimers for each weapon system
        foreach (PhxWeaponSystem System in WeaponSystems)
        {
            System.Update(TargetPos);
        }


        // A vehicle whose turret or weapon mounts have been shot off cannot
        // fire, whatever the occupant presses. Vehicles with no recognisable
        // weapon parts in their model - and everything that is not a vehicle -
        // report operational and behave exactly as before.
        PhxVehicle vehicle = Owner as PhxVehicle;
        if (vehicle != null && !vehicle.WeaponsOperational) return;

        if (Controller.ShootPrimary)
        {
            if (WeaponSystems.Count > 0)
            {
                WeaponSystems[0].Fire(TargetPos);
            }
        }

        if (Controller.ShootSecondary)
        {
            if (WeaponSystems.Count > 1)
            {
                WeaponSystems[1].Fire(TargetPos);
            }
        }
    }


    /// <summary>
    /// Controller of whoever is sitting here, or null when empty.
    /// (Previously only the flyer/hover main sections exposed this, so other
    /// seat types had no way to read their occupant's input.)
    /// </summary>
    public virtual PhxPawnController GetController()
    {
        return Occupant == null ? null : Occupant.GetController();
    }

    public void ClearOccupant()
    {
        Occupant = null;
    }

    public bool SetOccupant(PhxSoldier s) 
    {
        if (Occupant != null) return false;
        
        Occupant = s;

        return true;
    }

    public bool IsOccupied()
    {
        return Occupant != null;
    }


    protected PhxSeat(IPhxSeatable v, int index)
    {
        Owner = v;
        BaseTransform = v.GetRootTransform();
        Index = index;
    }


    public virtual void InitManual(EntityClass EC, int StartIndex, string HeaderName, string HeaderValue)
    {
        EC.GetAllProperties(out uint[] properties, out string[] values);

        WeaponSystems = new List<PhxWeaponSystem>();
        WeaponSystems.Add(new PhxWeaponSystem(this));
        int WeaponIndex = 0;
        bool WeaponsSet = false;
        
        int i = StartIndex;

        while (i < properties.Length)
        {
            if (properties[i] == 0x41568c97 /*EyePointOffset*/)
            {
                EyePointOffset = PhxUtils.Vec3FromString(values[i]);
            }
            else if (properties[i] == 0xe85d5895 /*TrackCenter*/)
            {
                TrackCenter = PhxUtils.Vec3FromString(values[i]);
            }
            else if (properties[i] == 0x2c3e8078 /*YawLimits*/)
            {
                YawLimits = PhxUtils.Vec2FromString(values[i]);
            }
            else if (properties[i] == 0x3403b139 /*PitchLimits*/)
            {
                PitchLimits = PhxUtils.Vec2FromString(values[i]);
            }
            else if (properties[i] == 0x359d5227 /*TiltValue*/)
            {
                TiltValue =  PhxUtils.FloatFromString(values[i]);
            }
            else if (properties[i] == 0xfd3d9507 /*TrackOffset*/)
            {
                TrackOffset = PhxUtils.Vec3FromString(values[i]);
            }
            else if (properties[i] == 0x51ca39a6 /*PilotPosition*/)
            {
                PilotPosition = UnityUtils.FindChildTransform(Owner.GetRootTransform(), values[i]);   
            }  
            else if (properties[i] == 0x6e4fc069 /*PilotAnimation*/)
            {
                PilotAnimation = values[i];
                PilotAnimationType = PilotAnimationType.StaticPose;
            }            
            else if (properties[i] == 0xa976d065 /*Pilot9Pose*/)
            {
                Pilot9Pose = values[i];
                PilotAnimationType = PilotAnimationType.NinePose;
            }
            else if (properties[i] == 0xd0329e80 /*WEAPONSECTION*/)
            {
                WeaponsSet = true;

                int newSlot = Int32.Parse(values[i], System.Globalization.CultureInfo.InvariantCulture);
                
                if (newSlot > WeaponSystems.Count)
                {
                    WeaponSystems.Add(new PhxWeaponSystem(this));
                    WeaponIndex = newSlot - 1;
                }

                WeaponSystems[WeaponIndex].InitManual(EC, i, values[i]);                
            }          
            // TURRETSECTION was excluded here, which meant a turret odf's
            // section headers never terminated the property scan - a turret
            // seat would swallow the following sections' properties.
            else if (properties[i] == HashUtils.GetFNV("FLYERSECTION") ||
                    properties[i] == HashUtils.GetFNV("WALKERSECTION") ||
                    properties[i] == HashUtils.GetFNV("TURRETSECTION") ||
                    properties[i] == HashUtils.GetFNV("BUILDINGSECTION"))
            {
                if (properties[i] == HashUtils.GetFNV(HeaderName) &&
                    values[i].Equals(HeaderValue, StringComparison.OrdinalIgnoreCase))
                {
                    // nada
                }
                else
                {
                    break;   
                }
            }

            i++;
        }

        if (!WeaponsSet)
        {
            WeaponSystems[0].InitManual(EC, StartIndex + 1);
        }
    } 
}
