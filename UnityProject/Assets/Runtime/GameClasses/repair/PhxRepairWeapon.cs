using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The engineer's fusioncutter: repairs hardware, and clears mines.
/// </summary>
/// <remarks>
/// The <c>repair</c> base class was enumerated by the importer and never
/// registered, so the class lookup returned null and the WEAPONSECTION entry
/// naming it was skipped - an engineer carried nothing in that slot.
///
/// The plan for this expected <c>repair</c> to cover the dispensers as well
/// and to be "two behaviours behind one base class". Measurement says
/// otherwise: <c>dispenser</c> is its own base class with 39 leaves across the
/// four sides against <c>repair</c>'s 19, and the powerup, mine, autoturret
/// and timebomb dispensers all sit on it. So this is one behaviour, and the
/// dispensers are a separate job.
///
/// What it does was fully specified in the data and needed no invention.
/// From com_weap_inf_fusioncutter:
///
///   vehiclehealth 200   buildinghealth 100   droidhealth 0
///   buildingbuild 50    buildingrebuild 50   minehealth -1000
///   targetvehicle 1     targetbuilding 1     targetmine 1
///   targetfriendly 1    targetneutral 1      targetenemy 1
///   lockonrange 4.0     lockonangle 90.0     shotdelay 0.5
///   roundsperclip -1    heatpershot 0.06     heatrecoverrate 0.16
///   heatthreshold 0.7
///
/// Three things follow from that. It is per-target-type, so a tank takes
/// twice what a bunker does. MineHealth is NEGATIVE, so pointing it at a mine
/// destroys it - that is the engineer's answer to a minefield, not an
/// oversight. And RoundsPerClip -1 means it never runs dry: heat is the
/// limit, and heat was declared on the weapon class and consumed by nothing.
/// </remarks>
public class PhxRepairWeapon : PhxGenericWeapon
{
    public new class ClassProperties : PhxGenericWeapon.ClassProperties
    {
        /// <summary>Health restored per shot to a vehicle.</summary>
        public PhxProp<float> VehicleHealth = new PhxProp<float>(0f);

        /// <summary>Health restored per shot to a standing structure.</summary>
        public PhxProp<float> BuildingHealth = new PhxProp<float>(0f);

        /// <summary>Health restored per shot to a droid.</summary>
        public PhxProp<float> DroidHealth = new PhxProp<float>(0f);

        /// <summary>Health restored per shot to an unbuilt structure.</summary>
        public PhxProp<float> BuildingBuild = new PhxProp<float>(0f);

        /// <summary>Health restored per shot to a levelled structure.</summary>
        public PhxProp<float> BuildingRebuild = new PhxProp<float>(0f);

        /// <summary>Negative in stock data: the tool destroys mines rather than mending them.</summary>
        public PhxProp<float> MineHealth = new PhxProp<float>(0f);

        public PhxProp<bool> TargetVehicle = new PhxProp<bool>(true);
        public PhxProp<bool> TargetBuilding = new PhxProp<bool>(true);
        public PhxProp<bool> TargetDroid = new PhxProp<bool>(false);
        public PhxProp<bool> TargetMine = new PhxProp<bool>(true);

        /// <summary>How close the target has to be.</summary>
        public PhxProp<float> LockOnRange = new PhxProp<float>(4f);

        /// <summary>Half-angle of the cone it works within, in degrees.</summary>
        public PhxProp<float> LockOnAngle = new PhxProp<float>(90f);

        /// <summary>The repair sparks. Spelled MuzzleFlashEffect on this class, not MuzzleFlash.</summary>
        public PhxProp<string> MuzzleFlashEffect = new PhxProp<string>(null);
    }

    ClassProperties RepairClass => C as ClassProperties;

    /// <summary>Heat, 0 to 1. Over the threshold the tool stops until it cools.</summary>
    float Heat;
    bool Overheated;

    /// <summary>Time left before the next application.</summary>
    /// <remarks>
    /// Fire is re-entered every frame the trigger is held, and this class
    /// bypasses the base salvo state machine, so without its own delay the
    /// odf's ShotDelay of 0.5 s would be ignored - a 0.06 heat cost per shot
    /// would overheat the tool in a fifth of a second and repair a tank in
    /// about the same time.
    /// </remarks>
    float ShotTimer;

    // Reused so holding the trigger does not allocate a collider array a frame.
    static readonly Collider[] OverlapCache = new Collider[32];

    public override void Tick(float deltaTime)
    {
        base.Tick(deltaTime);

        ShotTimer = Mathf.Max(ShotTimer - deltaTime, 0f);

        if (Heat <= 0f) return;

        Heat = Mathf.Max(Heat - C.HeatRecoverRate * deltaTime, 0f);

        // Hysteresis: it comes back at zero rather than the moment it drops
        // under the threshold, so an overheated tool cannot be re-triggered
        // into a stutter by holding the button.
        if (Overheated && Heat <= 0f) Overheated = false;
    }

    public override bool Fire(PhxPawnController owner, Vector3 targetPos)
    {
        OwnerController = owner ?? OwnerController;

        if (Overheated || ShotTimer > 0f) return false;

        ClassProperties cls = RepairClass;
        if (cls == null) return false;

        IPhxDestructible target = FindTarget();
        if (target == null) return false;

        float amount = GetRepairAmount(target);
        if (Mathf.Approximately(amount, 0f)) return false;

        if (target.AddHealth(amount) == 0f) return false;

        ShotTimer = C.ShotDelay;

        Heat += C.HeatPerShot;
        if (Heat >= C.HeatThreshold) Overheated = true;

        Transform firePoint = GetFirePoint();
        string sparks = cls.MuzzleFlashEffect.Get();
        if (firePoint != null && !string.IsNullOrEmpty(sparks))
        {
            PhxGame.GetScene()?.EffectsManager.PlayEffectOnce(
                sparks, firePoint.position, firePoint.rotation);
        }

        return true;
    }

    /// <summary>
    /// How much this target gets, by what it is. Negative destroys.
    /// </summary>
    float GetRepairAmount(IPhxDestructible target)
    {
        ClassProperties cls = RepairClass;
        if (cls == null) return 0f;

        if (target is PhxMine)
        {
            return cls.TargetMine ? cls.MineHealth : 0f;
        }

        if (target is PhxVehicle)
        {
            return cls.TargetVehicle ? cls.VehicleHealth : 0f;
        }

        if (target is PhxDestructableBuilding building)
        {
            if (!cls.TargetBuilding) return 0f;

            // Three separate amounts, because the data has three: mending a
            // standing structure, finishing an unbuilt one and raising a
            // levelled one are different jobs with different rates.
            float max = building.GetMaxHealth();
            float health = building.GetHealth();

            if (health <= 0f) return cls.BuildingRebuild;
            if (max > 0f && health < max * 0.5f) return cls.BuildingBuild;
            return cls.BuildingHealth;
        }

        // Capital-ship subsystems and anything else destructible fall back to
        // the building rate, which is what they are: fixed hardware.
        return cls.TargetBuilding ? cls.BuildingHealth : 0f;
    }

    /// <summary>
    /// The nearest repairable inside the tool's reach and cone.
    /// </summary>
    /// <remarks>
    /// A sphere query rather than a raycast: LockOnAngle is 90 degrees, which
    /// is a wide arc rather than a beam, and an engineer standing against the
    /// side of a tank should not have to find a particular panel.
    /// </remarks>
    IPhxDestructible FindTarget()
    {
        ClassProperties cls = RepairClass;
        if (cls == null) return null;

        Transform firePoint = GetFirePoint();
        Vector3 origin = firePoint != null ? firePoint.position : transform.position;
        Vector3 forward = firePoint != null ? firePoint.forward : transform.forward;

        float range = cls.LockOnRange;
        float halfArc = cls.LockOnAngle;

        int count = Physics.OverlapSphereNonAlloc(origin, range, OverlapCache,
                                                  ~0, QueryTriggerInteraction.Ignore);

        IPhxDestructible best = null;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (coll == null) continue;

            IPhxDestructible destructible = coll.GetComponentInParent<IPhxDestructible>();
            if (destructible == null || destructible.IsDestroyed) continue;

            // Nothing to do to something already at full health - and without
            // this the tool burns heat holding against an undamaged wall.
            // A mine is the exception: it is never "already repaired".
            if (!(destructible is PhxMine) && destructible.GetHealth() >= destructible.GetMaxHealth())
            {
                continue;
            }

            Vector3 to = destructible.GetGameObject().transform.position - origin;
            float distance = to.magnitude;
            if (distance > range || distance >= bestDistance) continue;

            if (distance > 0.001f && halfArc < 180f &&
                Vector3.Angle(forward, to / distance) > halfArc)
            {
                continue;
            }

            best = destructible;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>Heat, 0 to 1, for the HUD's ammo readout.</summary>
    public float GetHeat() => C.HeatThreshold > 0f ? Mathf.Clamp01(Heat / C.HeatThreshold) : 0f;
}
