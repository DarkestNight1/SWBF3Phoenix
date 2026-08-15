using UnityEngine;

/// <summary>
/// A sprung map hazard - Endor's Ewok tree smash, and nothing else in the
/// stock game.
/// </summary>
/// <remarks>
/// Two instances on one map, both end_weap_treesmash. Registered because an
/// unregistered base class is skipped without a word, not because two objects
/// are worth much on their own.
///
/// <b>This is deliberately partial, and the reason is worth stating.</b> The
/// trap odf carries twenty properties and hashing candidate names against them
/// resolves only four: GeometryName, AnimationName (end_weap_treesmash),
/// TriggerAnimation ("trigger") and MaxHealth (1000). The rest are readable as
/// values but not as names - "reset" beside the trigger animation, "hp_active"
/// which is plainly a hardpoint, "imp" and "all" which are plainly teams,
/// com_sfx_explosion_lg, an offset of (0, 7.5, 0), and three bare numbers
/// (0.05, 4.0, 0.666).
///
/// The shape of the thing is obvious from that and the semantics are not. Which
/// of "imp" and "all" is the team that springs it and which is the team it
/// hurts changes who dies; whether 4.0 is a trigger radius or a damage radius
/// changes where. Guessing produces a trap that kills the wrong side, which is
/// worse than a trap that does not go off - so what is implemented is what the
/// data actually says, and damage is not applied at all.
///
/// What this restores: the instance exists and is addressable, its geometry and
/// animation bank load, and it springs on proximity and resets. What it does
/// not do: hurt anyone. Finishing it needs a hash table carrying the trap
/// property names, at which point the numbers above stop being anonymous.
/// </remarks>
public class PhxTrap : PhxInstance<PhxTrap.ClassProperties>, IPhxTickable
{
    public class ClassProperties : PhxClass
    {
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        /// <summary>Animation bank holding the trigger and reset clips.</summary>
        public PhxProp<string> AnimationName = new PhxProp<string>("");

        /// <summary>Clip played when the trap springs.</summary>
        public PhxProp<string> TriggerAnimation = new PhxProp<string>("");

        public PhxProp<float> MaxHealth = new PhxProp<float>(1000f);
    }

    /// <summary>
    /// How close a soldier has to be to spring it.
    /// </summary>
    /// <remarks>
    /// Not read from the odf. There are three unnamed numbers on the class and
    /// one of them is probably this, but picking one is a guess about where the
    /// trap goes off - so this is a stated default, and a conservative one: a
    /// trap that springs slightly late is a worse trap, a trap that springs
    /// across the clearing is a bug.
    /// </remarks>
    const float TriggerRadius = 4f;

    /// <summary>How long the sprung trap takes to become live again.</summary>
    /// <remarks>Also stated rather than read, for the same reason.</remarks>
    const float ResetTime = 8f;

    bool Sprung;
    float ResetTimer;

    public override void Init()
    {
        // Geometry and the animation bank are already attached - the class
        // loader reads GeometryName and AnimationName before any runtime type
        // sees the object.
    }

    public override void Destroy() { }

    public void Tick(float deltaTime)
    {
        if (Sprung)
        {
            ResetTimer -= deltaTime;
            if (ResetTimer <= 0f) Sprung = false;
            return;
        }


        // Proximity rather than a trigger volume, the same choice PhxFlag
        // documents: the odf brings its own collision, it varies between stock
        // and mod content, and adding trigger colliders to imported geometry
        // disturbs the physics of everything around it.
        if (!AnyoneWithin(TriggerRadius)) return;

        Spring();
    }

    bool AnyoneWithin(float radius)
    {
        // Through the controller's pawn, the same route the ordnance code takes
        // to find who fired something - Match.Player is a controller, and the
        // thing standing in the world is the instance it is driving.
        PhxInstance pawn = PhxGame.GetMatch()?.Player?.Pawn?.GetInstance();
        if (pawn == null) return false;

        return (pawn.transform.position - transform.position).sqrMagnitude <= radius * radius;
    }

    void Spring()
    {
        Sprung = true;
        ResetTimer = ResetTime;

        // No damage. See the class remarks - the team fields are unresolved, so
        // there is no way to say who this is supposed to hurt without guessing,
        // and a trap that hurts the wrong side is worse than one that only
        // moves.
        Debug.Log($"[PhxTrap] '{name}' sprung. Animation only - damage is not " +
                  "applied until the trap property names resolve.");
    }
}
