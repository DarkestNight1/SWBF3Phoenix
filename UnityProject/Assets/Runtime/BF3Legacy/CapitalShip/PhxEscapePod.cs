using System.Collections;
using UnityEngine;

/// <summary>
/// Escape pod bay in the capital ship's junction lobby. BF3's design pitch:
/// destroy the reactor, then watch the ship break apart from an escape pod;
/// Elite Squadron: "rush back to the docking bay or an escape pod and rejoin
/// the battle on the surface".
///
/// A soldier standing in the bay for a moment launches: the pod (with the
/// soldier aboard) drops away from the ship on a shallow ballistic arc, and
/// after the ride the passenger is delivered to their team's closest command
/// post on the ground (or a safe drift point on space-only maps).
/// </summary>
public class PhxEscapePod : MonoBehaviour
{
    public PhxCapitalShip Ship;
    public float BoardRadius = 2.5f;
    public float BoardTime = 1.0f;     // linger time before launch
    public float RideTime = 7f;

    PhxSoldier Boarding;
    float BoardTimer;
    bool Launched;

    static readonly Collider[] OverlapCache = new Collider[16];


    void Update()
    {
        if (Launched || Ship == null) return;

        // find a living soldier lingering in the bay (any team - stealing an
        // enemy pod is a legitimate escape)
        PhxSoldier current = null;
        int count = Physics.OverlapSphereNonAlloc(transform.position, BoardRadius, OverlapCache);
        for (int i = 0; i < count; ++i)
        {
            PhxSoldier soldier = OverlapCache[i].GetComponentInParent<PhxSoldier>();
            if (soldier != null && !soldier.IsDead)
            {
                current = soldier;
                break;
            }
        }

        if (current == null)
        {
            Boarding = null;
            BoardTimer = 0f;
            return;
        }

        if (current != Boarding)
        {
            Boarding = current;
            BoardTimer = 0f;
        }

        BoardTimer += Time.deltaTime;

        // ship dying? launch immediately - that's the BF3 fantasy
        bool urgent = Ship.State == PhxCapitalShip.PhxShipState.Dying;
        if (BoardTimer >= (urgent ? 0.1f : BoardTime))
        {
            Launch(Boarding);
        }
    }

    public void Launch(PhxSoldier passenger)
    {
        if (Launched || passenger == null) return;
        Launched = true;
        StartCoroutine(Ride(passenger));
    }

    IEnumerator Ride(PhxSoldier passenger)
    {
        transform.SetParent(null, true);

        // freeze the passenger and take them along
        Rigidbody body = passenger.GetComponent<Rigidbody>();
        if (body != null) body.isKinematic = true;
        passenger.transform.SetParent(transform, true);
        passenger.transform.localPosition = Vector3.zero;

        // eject sideways away from the hull, then arc downwards
        Vector3 side = (transform.position - (Ship != null ? Ship.transform.position : transform.position));
        side.y = 0f;
        side = side.sqrMagnitude > 0.01f ? side.normalized : Vector3.forward;
        Vector3 velocity = side * 30f;

        Vector3 destination = FindDestination(passenger);
        float t = 0f;
        while (t < RideTime)
        {
            float dt = Time.deltaTime;
            t += dt;
            velocity += Vector3.down * 25f * dt;                    // dive toward the surface
            velocity = Vector3.ClampMagnitude(velocity, 120f);
            transform.position += velocity * dt;
            transform.Rotate(Vector3.forward, dt * 40f, Space.Self);

            // don't tunnel below the destination height
            if (transform.position.y <= destination.y + 2f)
            {
                break;
            }
            yield return null;
        }

        // deliver the passenger
        passenger.transform.SetParent(null, true);
        passenger.transform.position = destination + Vector3.up * 1.5f;
        if (body != null) body.isKinematic = false;

        Debug.Log($"[BF3Legacy] Escape pod delivered {passenger.name} to {destination}");
        Destroy(gameObject, 10f);
        enabled = false;
    }

    Vector3 FindDestination(PhxSoldier passenger)
    {
        // closest friendly command post on the ground; fall back to below the pod
        PhxCommandpost[] posts = PhxGame.GetScene()?.GetCommandPosts();
        if (posts != null)
        {
            PhxCommandpost best = null;
            float bestDist = float.MaxValue;
            int passengerTeam = passenger.Team;
            foreach (PhxCommandpost cp in posts)
            {
                if (cp.Team != passengerTeam) continue;
                float d = (cp.transform.position - transform.position).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = cp; }
            }
            if (best != null)
            {
                return best.transform.position;
            }
        }

        // space-only map / no posts: drift point clear of the ship
        Vector3 fallback = transform.position + transform.forward * 100f;
        fallback.y = Mathf.Max(fallback.y - 200f, 5f);
        return fallback;
    }
}
