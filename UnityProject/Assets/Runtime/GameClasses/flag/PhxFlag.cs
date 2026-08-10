using UnityEngine;

/// <summary>
/// A CTF / 1-flag capture flag.
///
/// Attached at runtime by PhxLuaAPI.AddFlag to whatever instance the mission
/// script nominates, so nothing here runs on a map that never registers a
/// flag - conquest and the other modes are untouched.
///
/// Pickup and capture use distance tests rather than trigger colliders. The
/// flag odfs carry their own collision, which varies between stock and mod
/// content, and adding trigger volumes to imported geometry risks disturbing
/// the physics of everything else; a radius check is predictable and needs no
/// setup.
/// </summary>
public class PhxFlag : MonoBehaviour
{
    static PhxMatch Match => PhxGame.GetMatch();
    static PhxScene Scene => PhxGame.GetScene();

    public enum PhxFlagMode
    {
        /// <summary>Each team has its own flag; you steal the enemy's.</summary>
        Ctf,
        /// <summary>One neutral flag both sides carry to the enemy base.</summary>
        OneFlag,
    }

    // Radius a soldier must be within to pick the flag up, and to score it.
    const float PickupRadius = 2.5f;
    const float CaptureRadius = 5f;

    // How long a dropped flag lies before returning itself, matching BF2's
    // behaviour of not letting a flag be lost forever in scenery.
    const float ReturnTimeout = 30f;

    public PhxFlagMode Mode = PhxFlagMode.Ctf;

    /// <summary>Team this flag belongs to; 0 for the neutral 1-flag mode.</summary>
    public int HomeTeam;

    /// <summary>Points awarded to the scoring team on capture.</summary>
    public int CapturePoints = 1;

    /// <summary>Region a carrier must reach to score. Null falls back to the flag's home position.</summary>
    public PhxRegion HomeRegion;

    public PhxSoldier Carrier { get; private set; }

    Vector3 HomePosition;
    Quaternion HomeRotation;
    float DroppedTimer;
    bool Dropped;

    void Start()
    {
        HomePosition = transform.position;
        HomeRotation = transform.rotation;
    }

    void Update()
    {
        PhxMatch match = Match;
        if (match == null || match.IsMatchOver) return;

        if (Carrier != null)
        {
            TickCarried(match);
        }
        else
        {
            TickLoose(match);
        }
    }

    void TickCarried(PhxMatch match)
    {
        // A carrier that died, was destroyed, or boarded something drops it.
        if (Carrier == null || Carrier.IsDead || Carrier.gameObject == null)
        {
            Drop();
            return;
        }

        transform.position = Carrier.transform.position + Vector3.up * 1.6f;
        transform.rotation = Carrier.transform.rotation;

        if (AtScoringPoint(Carrier))
        {
            Capture(match, Carrier);
        }
    }

    void TickLoose(PhxMatch match)
    {
        if (Dropped)
        {
            DroppedTimer -= Time.deltaTime;
            if (DroppedTimer <= 0f)
            {
                ResetToHome();
                return;
            }
        }

        PhxSoldier taker = FindSoldierWithin(PickupRadius);
        if (taker == null) return;

        // In CTF you carry the enemy's flag; a friendly touching a dropped
        // flag sends it home instead. The neutral 1-flag is carried by anyone.
        if (Mode == PhxFlagMode.Ctf && HomeTeam > 0 && taker.Team == HomeTeam)
        {
            if (Dropped) ResetToHome();
            return;
        }

        PickUp(taker);
    }

    bool AtScoringPoint(PhxSoldier carrier)
    {
        if (HomeRegion != null && HomeRegion.Collider != null)
        {
            return HomeRegion.Collider.bounds.Contains(carrier.transform.position);
        }

        // No region authored: score at the carrier team's own flag position.
        return Vector3.Distance(carrier.transform.position, HomePosition) <= CaptureRadius;
    }

    PhxSoldier FindSoldierWithin(float radius)
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, radius);
        for (int i = 0; i < hits.Length; ++i)
        {
            PhxSoldier soldier = hits[i].GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.IsDead) continue;
            if (soldier.Team <= 0) continue;
            return soldier;
        }
        return null;
    }

    /// <summary>
    /// Scene index of this flag, which is the handle Lua callbacks receive.
    /// Nullable because an object not registered with the scene has none, and
    /// the events pass it straight through to Lua as-is.
    /// </summary>
    int? InstanceIndex()
    {
        PhxInstance inst = GetComponent<PhxInstance>();
        return inst != null && Scene != null ? Scene.GetInstanceIndex(inst) : null;
    }

    void PickUp(PhxSoldier taker)
    {
        Carrier = taker;
        Dropped = false;

        int? instIdx = InstanceIndex();
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFlagPickUp, instIdx);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnFlagPickUpTeam, taker.Team, instIdx);
    }

    void Drop()
    {
        Carrier = null;
        Dropped = true;
        DroppedTimer = ReturnTimeout;

        int? instIdx = InstanceIndex();
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFlagDrop, instIdx);
    }

    void Capture(PhxMatch match, PhxSoldier carrier)
    {
        int scoringTeam = carrier.Team;

        match.AddTeamPoints(scoringTeam, CapturePoints);

        PhxPawnController controller = carrier.GetController();
        if (controller != null)
        {
            controller.Score += CapturePoints;
        }

        Debug.Log($"[BF3Legacy] Flag captured by team {scoringTeam} (+{CapturePoints}).");
        ResetToHome();
    }

    public void ResetToHome()
    {
        Carrier = null;
        Dropped = false;
        DroppedTimer = 0f;
        transform.position = HomePosition;
        transform.rotation = HomeRotation;

        int? instIdx = InstanceIndex();
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFlagReset, instIdx);
    }
}
