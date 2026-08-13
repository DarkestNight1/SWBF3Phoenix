using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Animation-driven leg motion for walkers: leg cycles played at the rate the
/// machine is actually travelling, feet placed on the ground they are actually
/// over, and a body carried on those feet.
/// </summary>
/// <remarks>
/// A walker moved by sliding its whole model over a raycast height reads
/// wrong at any distance - the legs cycle at a rate unrelated to the ground
/// passing beneath them, or do not cycle at all, and the hull stays level over
/// terrain the legs are visibly straddling.
///
/// Three separate things fix that and they are independent:
///
/// <list type="number">
/// <item><b>Cycle rate from distance.</b> The clip is played at
/// <c>speed / strideSpeed</c>, so one leg cycle covers one stride of ground
/// however fast the walker is going, forwards or backwards. This is the piece
/// that stops the feet skating.</item>
/// <item><b>Foot placement.</b> After the animation has posed the skeleton,
/// each foot is probed against the world and lifted to whatever is under it,
/// within a limit - so a leg on a rock stands on the rock.</item>
/// <item><b>Body carried by the feet.</b> The hull's height and tilt come from
/// where the feet ended up rather than from a single probe under its centre,
/// which is what makes an AT-AT crossing a ridge lean instead of float.</item>
/// </list>
///
/// Foot nodes are discovered from the model rather than configured: BF2 walker
/// models name them consistently enough to find (<c>*foot*</c> / <c>*toe*</c>)
/// and there is no odf property that lists them.
/// </remarks>
public sealed class PhxWalkerLocomotion
{
    /// <summary>
    /// Clip names to try, in order. The mod tools do not publish a walker's
    /// animation set, so this follows the naming the stock banks use and the
    /// first one that resolves wins. A walker whose bank matches none of them
    /// still moves - it simply does not animate, which is where this started.
    /// </summary>
    /// <summary>
    /// BF2 walker gait clips, in preference order.
    /// </summary>
    /// <remarks>
    /// These are the real names, recovered by hashing lookup.csv against the
    /// animation CRCs in imp.lvl. The list this used to hold - walk,
    /// walkforward, forward, move, run - matched NOTHING: across 188
    /// animations only "idle" ever hit, so HasWalkClip was always false and
    /// AT-ATs and AT-STs slid along with their legs frozen.
    ///
    /// The naming is a gait state machine rather than a loop: each clip is one
    /// half-step, named for which foot is planted and which is coming down.
    /// The _leftup/_rightup variants are the turning forms. Playing the two
    /// base halves alternately reproduces the walk cycle.
    /// </remarks>
    static readonly string[] WalkClipNames =
    {
        "walk_leftfoot_rightfoot", "walk_rightfoot_leftfoot",

        // Kept as fallbacks: mod walkers do not have to follow the stock
        // naming, and a custom machine calling its cycle "walk" should still
        // animate.
        "walk", "walkforward", "forward", "move", "run",
    };

    /// <summary>The second half of the gait, alternated with the first.</summary>
    static readonly string[] WalkClipNamesB =
    {
        "walk_rightfoot_leftfoot", "walk_leftfoot_rightfoot",
    };
    static readonly string[] IdleClipNames = { "idle", "stand", "rest" };

    /// <summary>
    /// How far the machine travels in one leg cycle, as a multiple of its ride
    /// height.
    /// </summary>
    /// <remarks>
    /// Stride length is not authored anywhere we can read, and it is what
    /// converts travel into cycle rate. A legged machine's step is close to
    /// its own leg length, so ride height is the best available proxy; it is a
    /// single constant here precisely so it can be corrected in one place if
    /// real walkers read wrong.
    /// </remarks>
    const float StrideToHeightRatio = 1.2f;

    /// <summary>Most a foot may be lifted or dropped from its animated pose.</summary>
    const float MaxFootAdjust = 2.5f;

    /// <summary>How fast foot corrections and body tilt catch up, per second.</summary>
    const float AdjustLerpRate = 8f;

    sealed class Foot
    {
        public Transform Node;
        public float GroundHeight;
        public bool HasGround;
        public float AppliedOffset;
        public float PreviousHeight;
        public bool Planted;
    }

    readonly Transform Root;
    readonly LayerMask GroundMask;
    readonly List<Foot> Feet = new List<Foot>();

    CraPlayer WalkPlayer;
    CraPlayer IdlePlayer;
    bool HasWalkClip;
    bool WalkPlaying;

    float StrideSpeed = 1f;
    float ClipDuration;

    // The other half of the gait. Stock walkers author the cycle as two
    // half-steps rather than one loop, so alternating them on each plant is
    // what produces a walk rather than one leg twitching.
    CraPlayer WalkPlayerB;
    bool OnSecondHalf;

    /// <summary>Effect played where a foot lands; empty for none.</summary>
    public string FootstepEffect;

    /// <summary>Raised on each foot plant, with the world position of the foot.</summary>
    public Action<Vector3> OnFootPlanted;

    public int FootCount => Feet.Count;
    public bool IsAnimated => HasWalkClip;

    public PhxWalkerLocomotion(Transform root, string animationBank, float rideHeight, LayerMask groundMask)
    {
        Root = root;
        GroundMask = groundMask;

        DiscoverFeet(root);
        LoadClips(root, animationBank);

        // Cycle length in metres, converted to the ground speed at which the
        // clip should play at its authored rate.
        float stride = Mathf.Max(0.5f, rideHeight * StrideToHeightRatio);
        StrideSpeed = ClipDuration > 0f ? stride / ClipDuration : 1f;
    }

    void DiscoverFeet(Transform root)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            string lower = child.name.ToLowerInvariant();
            if (lower.Contains("foot") || lower.Contains("toe"))
            {
                Feet.Add(new Foot { Node = child, PreviousHeight = child.position.y });
            }
        }
    }

    void LoadClips(Transform root, string animationBank)
    {
        if (string.IsNullOrEmpty(animationBank)) return;

        WalkPlayer = TryCreate(root, animationBank, WalkClipNames);
        WalkPlayerB = TryCreate(root, animationBank, WalkClipNamesB);
        IdlePlayer = TryCreate(root, animationBank, IdleClipNames);

        HasWalkClip = WalkPlayer.IsValid();
        if (!HasWalkClip)
        {
            Debug.LogWarning($"[Phoenix] Walker bank '{animationBank}' has none of the expected " +
                             "leg-cycle clips - this walker will move without stepping. " +
                             "Add its clip name to PhxWalkerLocomotion.WalkClipNames.");
            return;
        }

        ClipDuration = WalkPlayer.GetDuration();
        WalkPlayer.SetLooping(true);

        if (IdlePlayer.IsValid())
        {
            IdlePlayer.SetLooping(true);
            IdlePlayer.Play();
        }
    }

    static CraPlayer TryCreate(Transform root, string bank, string[] candidates)
    {
        for (int i = 0; i < candidates.Length; ++i)
        {
            CraPlayer player = PhxAnimationLoader.CreatePlayer(root, bank, candidates[i], true);
            if (player.IsValid())
            {
                return player;
            }
        }
        return CraPlayer.CreateEmpty();
    }

    /// <summary>
    /// Match the leg cycle to how fast the machine is moving over the ground.
    /// </summary>
    /// <param name="groundSpeed">
    /// Signed speed along the walker's forward axis. Negative plays the cycle
    /// backwards, which is what walking backwards looks like.
    /// </param>
    public void SetGroundSpeed(float groundSpeed)
    {
        if (!HasWalkClip) return;

        // Below this the walker is standing still as far as its legs are
        // concerned; without a threshold the cycle creeps forever from
        // floating-point drift in the drive input.
        const float MinMovingSpeed = 0.05f;

        if (Mathf.Abs(groundSpeed) < MinMovingSpeed)
        {
            if (WalkPlaying)
            {
                ActiveWalk.SetPlaybackSpeed(0f);
                WalkPlaying = false;
                if (IdlePlayer.IsValid()) IdlePlayer.Play();
            }
            return;
        }

        if (!WalkPlaying)
        {
            WalkPlaying = true;
            ActiveWalk.Play();
        }
        ActiveWalk.SetPlaybackSpeed(groundSpeed / StrideSpeed);
    }

    /// <summary>The gait half currently running.</summary>
    CraPlayer ActiveWalk => (OnSecondHalf && WalkPlayerB.IsValid()) ? WalkPlayerB : WalkPlayer;

    /// <summary>
    /// Swap to the other half of the gait.
    /// </summary>
    /// <remarks>
    /// Stock walkers author the cycle as two half-steps - walk_leftfoot_rightfoot
    /// and walk_rightfoot_leftfoot - rather than one loop, so a single clip on
    /// repeat plays the same half forever and the machine limps. Alternating on
    /// each plant is what the naming is telling us to do. A walker whose bank
    /// only had one clip keeps using it; ActiveWalk falls back when B is
    /// invalid.
    /// </remarks>
    void AdvanceGait()
    {
        if (!WalkPlayerB.IsValid() || !WalkPlaying) return;

        CraPlayer previous = ActiveWalk;
        OnSecondHalf = !OnSecondHalf;
        CraPlayer next = ActiveWalk;

        if (!ReferenceEquals(previous, next))
        {
            previous.SetPlaybackSpeed(0f);
            next.Play();
        }
    }

    /// <summary>
    /// Place the feet and report plants. Must run after the animation has
    /// posed the skeleton for this frame, otherwise the corrections are
    /// overwritten before anything sees them.
    /// </summary>
    public void TickFeet(float deltaTime)
    {
        for (int i = 0; i < Feet.Count; ++i)
        {
            Foot foot = Feet[i];
            if (foot.Node == null) continue;

            Vector3 animated = foot.Node.position;

            // The animated pose is where the foot wants to be; the probe says
            // where the ground actually is. Probe from above so a foot already
            // below the surface is pushed back up rather than missed.
            Vector3 probeOrigin = animated + Vector3.up * MaxFootAdjust;
            foot.HasGround = Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit hit,
                                             MaxFootAdjust * 2f, GroundMask,
                                             QueryTriggerInteraction.Ignore);

            float wanted = 0f;
            if (foot.HasGround)
            {
                foot.GroundHeight = hit.point.y;
                wanted = Mathf.Clamp(hit.point.y - animated.y, -MaxFootAdjust, MaxFootAdjust);

                // Only pull a foot DOWN to meet ground below it; lifting a foot
                // that is mid-swing onto the terrain under it would drag the
                // whole leg along the floor.
                if (wanted > 0f && !IsNearGround(foot, animated.y))
                {
                    wanted = 0f;
                }
            }

            foot.AppliedOffset = Mathf.Lerp(foot.AppliedOffset, wanted, deltaTime * AdjustLerpRate);
            foot.Node.position = animated + Vector3.up * foot.AppliedOffset;

            DetectPlant(foot, deltaTime);
            foot.PreviousHeight = foot.Node.position.y;
        }
    }

    /// <summary>Whether a foot is low enough in its cycle to be taking weight.</summary>
    bool IsNearGround(Foot foot, float animatedHeight)
    {
        const float ContactBand = 0.35f;
        return foot.HasGround && animatedHeight - foot.GroundHeight < ContactBand;
    }

    void DetectPlant(Foot foot, float deltaTime)
    {
        if (deltaTime <= 0f) return;

        float verticalSpeed = (foot.Node.position.y - foot.PreviousHeight) / deltaTime;
        bool touching = foot.HasGround && foot.Node.position.y - foot.GroundHeight < 0.25f;

        if (touching && !foot.Planted && verticalSpeed <= 0f)
        {
            foot.Planted = true;
            OnFootPlanted?.Invoke(foot.Node.position);
            AdvanceGait();
            return;
        }

        // Leaving the ground again re-arms the detector; without this a
        // standing walker reports one plant per frame.
        if (!touching && foot.Planted && verticalSpeed > 0f)
        {
            foot.Planted = false;
        }
    }

    /// <summary>
    /// Where the hull should sit given where the feet ended up: the average
    /// ground height under the feet, and the plane they define.
    /// </summary>
    /// <returns>False when no foot found ground, so the caller keeps its
    /// own single-probe behaviour rather than snapping to nothing.</returns>
    public bool GetBodySupport(out float groundHeight, out Vector3 groundNormal)
    {
        groundHeight = 0f;
        groundNormal = Vector3.up;

        SupportPoints.Clear();
        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < Feet.Count; ++i)
        {
            Foot foot = Feet[i];
            if (!foot.HasGround || foot.Node == null) continue;

            Vector3 point = new Vector3(foot.Node.position.x, foot.GroundHeight, foot.Node.position.z);
            SupportPoints.Add(point);
            groundHeight += foot.GroundHeight;
            centroid += point;
        }
        if (SupportPoints.Count == 0) return false;

        groundHeight /= SupportPoints.Count;
        centroid /= SupportPoints.Count;

        // Fit a plane to the grounded feet: with three or more, their normal
        // is the surface the machine is actually standing on, which is what
        // the hull should tilt to. Fewer than three cannot define one, so the
        // caller gets level ground and keeps its slope from elsewhere.
        if (SupportPoints.Count < 3) return true;

        // Newell's method over the support polygon - stable for the near-flat,
        // near-degenerate arrangements four feet on gentle ground produce,
        // where a single cross product of two edges is mostly noise.
        Vector3 normal = Vector3.zero;
        Vector3 previous = SupportPoints[SupportPoints.Count - 1];
        for (int i = 0; i < SupportPoints.Count; ++i)
        {
            Vector3 current = SupportPoints[i];
            normal += Vector3.Cross(previous - centroid, current - centroid);
            previous = current;
        }
        if (normal.sqrMagnitude > 0.0001f)
        {
            groundNormal = normal.normalized;
            if (groundNormal.y < 0f) groundNormal = -groundNormal;
        }
        return true;
    }

    // Reused per query; a walker asks for this every physics step.
    readonly List<Vector3> SupportPoints = new List<Vector3>();
}
