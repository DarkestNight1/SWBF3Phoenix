using System.Collections;
using UnityEngine;

/// <summary>
/// Ground-based ion cannon, the Elite Squadron mechanic tying the ground war to
/// the space war: the team holding the cannon can fire it at the enemy capital
/// ship to strip its shields, opening it up for boarding.
///
/// Ownership can be driven by a linked command post (hold the CP to own the
/// cannon), and firing is on a cooldown.
/// </summary>
public class PhxIonCannon : MonoBehaviour, IPhxTickable
{
    [Header("Wiring")]
    // If set, cannon ownership follows this command post's owning team
    public PhxCommandpost LinkedCommandPost;
    public Transform Muzzle;

    [Header("Firing")]
    public float Cooldown = 90f;
    public float ChargeTime = 4f;
    public float BeamDuration = 2.5f;

    public int OwnerTeam { get; private set; }
    public float CooldownRemaining { get; private set; }
    public bool IsCharging { get; private set; }

    void Update()
    {
        Tick(Time.deltaTime);
    }

    public void Tick(float deltaTime)
    {
        if (LinkedCommandPost != null)
        {
            OwnerTeam = LinkedCommandPost.Team;
        }
        if (CooldownRemaining > 0f)
        {
            CooldownRemaining -= deltaTime;
        }
    }

    public bool CanFire()
    {
        if (OwnerTeam == 0 || CooldownRemaining > 0f || IsCharging) return false;
        return FindTarget() != null;
    }

    /// <summary>Fire at the enemy capital ship. Returns false if not possible right now.</summary>
    public bool Fire()
    {
        if (!CanFire()) return false;

        PhxCapitalShip target = FindTarget();
        StartCoroutine(FireSequence(target));
        return true;
    }

    PhxCapitalShip FindTarget()
    {
        // target: any living enemy capital ship
        foreach (PhxCapitalShip ship in PhxCapitalShip.GetAll())
        {
            if (ship.Team != OwnerTeam &&
                ship.State != PhxCapitalShip.PhxShipState.Dying &&
                ship.State != PhxCapitalShip.PhxShipState.Destroyed)
            {
                return ship;
            }
        }
        return null;
    }

    IEnumerator FireSequence(PhxCapitalShip target)
    {
        IsCharging = true;
        yield return new WaitForSeconds(ChargeTime);

        Vector3 from = Muzzle != null ? Muzzle.position : transform.position + Vector3.up * 10f;
        PhxIonBeam.Spawn(from, target.transform.position, BeamDuration);

        yield return new WaitForSeconds(BeamDuration * 0.5f);

        target.IonStrike();
        Debug.Log($"[BF3Legacy] Ion cannon (Team {OwnerTeam}) stripped shields of {target.ShipName}");

        IsCharging = false;
        CooldownRemaining = Cooldown;
    }
}

/// <summary>Simple stretched-cylinder ion beam visual (placeholder for a proper VFX).</summary>
public class PhxIonBeam : MonoBehaviour
{
    float Life;
    float Duration;

    public static void Spawn(Vector3 from, Vector3 to, float duration)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        GameObject.Destroy(go.GetComponent<Collider>());
        go.name = "IonBeam";

        Vector3 mid = (from + to) * 0.5f;
        go.transform.position = mid;
        go.transform.up = (to - from).normalized;
        go.transform.localScale = new Vector3(4f, (to - from).magnitude * 0.5f, 4f);
        go.GetComponent<Renderer>().material.color = new Color(0.4f, 0.7f, 1f, 0.8f);

        PhxIonBeam beam = go.AddComponent<PhxIonBeam>();
        beam.Duration = duration;
    }

    void Update()
    {
        Life += Time.deltaTime;
        if (Life >= Duration)
        {
            GameObject.Destroy(gameObject);
        }
    }
}
