using UnityEngine;

/// <summary>
/// How well one soldier shoots, expressed the way shooting actually fails.
/// </summary>
/// <remarks>
/// The difference between AI that feels like an opponent and AI that feels
/// like a turret is almost entirely here. A turret raycasts at your head and
/// hits. A player acquires you late, swings past you, corrects, fires a burst
/// that walks off target, and re-settles - and misses more the further away
/// you are and the faster you are moving across their view.
///
/// So error is not one number. It is composed, per shot, from causes that
/// behave differently:
///
/// <list type="bullet">
/// <item><b>Base</b> - how tight this soldier's hold is at all.</item>
/// <item><b>Settling</b> - a large error immediately after acquiring, decaying
/// as they track. This is what makes stepping into view survivable.</item>
/// <item><b>Tracking</b> - proportional to the target's angular speed across
/// their view, so strafing genuinely works and standing still genuinely does
/// not.</item>
/// <item><b>Range</b> - error growing with distance beyond the soldier's
/// competent range, so a recruit with a rifle is not a sniper.</item>
/// <item><b>Burst walk</b> - error accumulating within a burst and resetting
/// between them, which is why controlled bursts beat holding the trigger.</item>
/// </list>
///
/// No tier has perfect aim. The elite profile below still misses; it just
/// misses less, settles faster and tracks better.
/// </remarks>
public struct BFAimProfile
{
    /// <summary>Base error cone half-angle, in degrees.</summary>
    public float BaseErrorDegrees;

    /// <summary>Extra error the instant a target is acquired.</summary>
    public float SettlingErrorDegrees;

    /// <summary>Seconds for the settling error to decay away.</summary>
    public float SettleTime;

    /// <summary>
    /// Error per degree/second of target angular velocity. The single most
    /// important term for making movement matter.
    /// </summary>
    public float TrackingErrorPerDegreePerSecond;

    /// <summary>Distance the soldier shoots competently to, in metres.</summary>
    public float CompetentRange;

    /// <summary>Extra error per metre beyond the competent range.</summary>
    public float RangeErrorPerMetre;

    /// <summary>Error added per shot within a burst.</summary>
    public float BurstWalkPerShot;

    /// <summary>Shots after which the soldier stops to re-settle.</summary>
    public int BurstLength;

    /// <summary>Seconds between seeing and first shot.</summary>
    public float ReactionTime;

    /// <summary>
    /// How well they lead a moving target, 0 none to 1 perfect. Below 1 for
    /// everyone: perfect leading is unmissable and reads as aimbotting.
    /// </summary>
    public float TargetPrediction;

    /// <summary>
    /// Chance per second of losing interest in the current target for another.
    /// Human attention is not exclusive, and an AI that never re-targets under
    /// fire from two directions reads as scripted.
    /// </summary>
    public float TargetSwitchChancePerSecond;

    public static BFAimProfile ForDifficulty(int difficulty)
    {
        switch (difficulty)
        {
            case 0:  // Recruit - slow, loose, reacts late, corrects visibly
                return new BFAimProfile
                {
                    BaseErrorDegrees = 4.5f,
                    SettlingErrorDegrees = 9f,
                    SettleTime = 1.6f,
                    TrackingErrorPerDegreePerSecond = 0.10f,
                    CompetentRange = 25f,
                    RangeErrorPerMetre = 0.055f,
                    BurstWalkPerShot = 0.9f,
                    BurstLength = 6,
                    ReactionTime = 0.95f,
                    TargetPrediction = 0.15f,
                    TargetSwitchChancePerSecond = 0.25f,
                };

            case 1:  // Normal - competent, makes ordinary mistakes
                return new BFAimProfile
                {
                    BaseErrorDegrees = 2.8f,
                    SettlingErrorDegrees = 6f,
                    SettleTime = 1.1f,
                    TrackingErrorPerDegreePerSecond = 0.07f,
                    CompetentRange = 40f,
                    RangeErrorPerMetre = 0.035f,
                    BurstWalkPerShot = 0.6f,
                    BurstLength = 5,
                    ReactionTime = 0.6f,
                    TargetPrediction = 0.35f,
                    TargetSwitchChancePerSecond = 0.15f,
                };

            case 2:  // Hard - strong, fast, disciplined bursts
                return new BFAimProfile
                {
                    BaseErrorDegrees = 1.7f,
                    SettlingErrorDegrees = 3.8f,
                    SettleTime = 0.7f,
                    TrackingErrorPerDegreePerSecond = 0.045f,
                    CompetentRange = 60f,
                    RangeErrorPerMetre = 0.022f,
                    BurstWalkPerShot = 0.4f,
                    BurstLength = 4,
                    ReactionTime = 0.35f,
                    TargetPrediction = 0.6f,
                    TargetSwitchChancePerSecond = 0.09f,
                };

            default: // Elite - very good, still human
                return new BFAimProfile
                {
                    // Deliberately not zero. An elite soldier who cannot miss
                    // is not an elite soldier, it is a hitscan turret, and the
                    // whole point of this system is that no tier gets that.
                    BaseErrorDegrees = 1.0f,
                    SettlingErrorDegrees = 2.4f,
                    SettleTime = 0.45f,
                    TrackingErrorPerDegreePerSecond = 0.03f,
                    CompetentRange = 85f,
                    RangeErrorPerMetre = 0.014f,
                    BurstWalkPerShot = 0.25f,
                    BurstLength = 4,
                    ReactionTime = 0.22f,
                    TargetPrediction = 0.8f,
                    TargetSwitchChancePerSecond = 0.05f,
                };
        }
    }

    /// <summary>
    /// Per-soldier variation, so a squad is a group of individuals rather than
    /// a rank of identical shooters.
    /// </summary>
    public BFAimProfile WithJitter()
    {
        float f = Random.Range(0.82f, 1.22f);

        BaseErrorDegrees *= f;
        SettlingErrorDegrees *= f;
        SettleTime *= Random.Range(0.85f, 1.2f);
        TrackingErrorPerDegreePerSecond *= f;
        ReactionTime *= Random.Range(0.8f, 1.25f);
        CompetentRange *= Random.Range(0.9f, 1.1f);
        TargetPrediction = Mathf.Clamp01(TargetPrediction * Random.Range(0.85f, 1.15f));
        return this;
    }
}

/// <summary>
/// The per-soldier aiming state that <see cref="BFAimProfile"/> describes the
/// shape of: where they are actually pointing right now, and why.
/// </summary>
/// <remarks>
/// Kept as a separate object rather than fields on the controller because it
/// carries the history that makes aim behave over time - when the target was
/// acquired, how fast it is crossing, how many shots into a burst they are.
/// Aim with no memory cannot settle, walk or recover, and those are the three
/// things that make it read as a person.
/// </remarks>
public sealed class BFAimState
{
    public BFAimProfile Profile;

    object CurrentTarget;
    float AcquiredAt = float.NegativeInfinity;
    Vector3 LastTargetPosition;
    float LastAngularSpeed;
    int ShotsInBurst;
    float BurstEndedAt = float.NegativeInfinity;

    /// <summary>The error cone currently applied, in degrees. For diagnostics.</summary>
    public float CurrentErrorDegrees { get; private set; }

    public BFAimState(BFAimProfile profile)
    {
        Profile = profile;
    }

    /// <summary>Forget everything - respawn, or losing the target entirely.</summary>
    public void Reset()
    {
        CurrentTarget = null;
        AcquiredAt = float.NegativeInfinity;
        ShotsInBurst = 0;
        LastAngularSpeed = 0f;
    }

    /// <summary>True once the reaction delay since acquiring has elapsed.</summary>
    public bool HasReacted => Time.time - AcquiredAt >= Profile.ReactionTime;

    /// <summary>
    /// True when the soldier should pause between bursts. Holding the trigger
    /// forever is what a machine does.
    /// </summary>
    public bool IsBetweenBursts
    {
        get
        {
            if (ShotsInBurst < Profile.BurstLength) return false;

            // Pause scales with how badly the burst walked, so a loose shooter
            // spends longer re-settling.
            float pause = 0.25f + Profile.BurstWalkPerShot * 0.35f;
            return Time.time - BurstEndedAt < pause;
        }
    }

    public void NotifyShotFired()
    {
        ++ShotsInBurst;
        if (ShotsInBurst >= Profile.BurstLength)
        {
            BurstEndedAt = Time.time;
        }
    }

    void EndBurstIfRested()
    {
        if (ShotsInBurst < Profile.BurstLength) return;

        float pause = 0.25f + Profile.BurstWalkPerShot * 0.35f;
        if (Time.time - BurstEndedAt >= pause)
        {
            ShotsInBurst = 0;
        }
    }

    /// <summary>
    /// Where to aim at <paramref name="targetPosition"/>, with every error term
    /// composed. Call once per frame while engaging.
    /// </summary>
    public Vector3 Aim(Vector3 eyePosition, object target, Vector3 targetPosition,
                       Vector3 targetVelocity, float deltaTime)
    {
        if (!ReferenceEquals(target, CurrentTarget))
        {
            CurrentTarget = target;
            AcquiredAt = Time.time;
            LastTargetPosition = targetPosition;
            ShotsInBurst = 0;
        }
        EndBurstIfRested();

        Vector3 toTarget = targetPosition - eyePosition;
        float distance = toTarget.magnitude;
        if (distance < 0.01f) return targetPosition;

        // How fast the target is crossing the shooter's view, in degrees per
        // second. Radial movement is easy to track and lateral movement is not,
        // and this is what tells them apart.
        if (deltaTime > 0f)
        {
            Vector3 previous = (LastTargetPosition - eyePosition).normalized;
            Vector3 current = toTarget / distance;
            LastAngularSpeed = Vector3.Angle(previous, current) / deltaTime;
        }
        LastTargetPosition = targetPosition;

        // Leading, partially. The fraction is the skill: nobody leads perfectly
        // and nobody good leads not at all.
        Vector3 aimPoint = targetPosition + targetVelocity * (distance / 500f) * Profile.TargetPrediction;

        float error = Profile.BaseErrorDegrees;

        float sinceAcquired = Time.time - AcquiredAt;
        if (sinceAcquired < Profile.SettleTime)
        {
            float t = 1f - sinceAcquired / Profile.SettleTime;
            error += Profile.SettlingErrorDegrees * t * t;
        }

        error += LastAngularSpeed * Profile.TrackingErrorPerDegreePerSecond;

        if (distance > Profile.CompetentRange)
        {
            error += (distance - Profile.CompetentRange) * Profile.RangeErrorPerMetre;
        }

        error += ShotsInBurst * Profile.BurstWalkPerShot;

        CurrentErrorDegrees = error;

        return eyePosition + ApplyConeError(aimPoint - eyePosition, error).normalized * distance;
    }

    /// <summary>
    /// Rotate a direction by a random amount within a cone.
    /// </summary>
    /// <remarks>
    /// Gaussian-ish rather than uniform: a uniform cone puts as many shots at
    /// the rim as near the centre, which reads as spraying. Squaring a uniform
    /// sample biases toward the middle, so most shots are close and the
    /// occasional one is wide - which is what a person's grouping looks like.
    /// </remarks>
    public static Vector3 ApplyConeError(Vector3 direction, float coneDegrees)
    {
        if (coneDegrees <= 0.001f) return direction;

        float u = Random.value;
        float magnitude = coneDegrees * u * u;
        float roll = Random.Range(0f, 360f);

        Vector3 axis = Vector3.Cross(direction, Vector3.up);
        if (axis.sqrMagnitude < 0.0001f) axis = Vector3.right;

        return Quaternion.AngleAxis(roll, direction) *
               Quaternion.AngleAxis(magnitude, axis.normalized) * direction;
    }
}
