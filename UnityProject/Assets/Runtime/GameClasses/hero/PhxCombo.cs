using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Enums;
using LibSWBF2.Wrappers;

/// <summary>
/// What a player can press to leave a combo move.
/// </summary>
public enum PhxComboInput
{
    /// <summary>No input needed; the transition fires when its window opens.</summary>
    None,
    Attack,
    Block,
    Jump,
    Dodge,
    Special,
}

/// <summary>A damaging window inside one combo move.</summary>
public sealed class PhxComboAttack
{
    /// <summary>Fraction of the move at which the blade starts hurting.</summary>
    public float DamageStart;

    /// <summary>Fraction of the move at which it stops.</summary>
    public float DamageEnd = 1f;

    public float Damage = 100f;
    public float Push;

    /// <summary>Reach of this swing, in metres from the wielder.</summary>
    public float Reach = 3f;

    /// <summary>Arc of the sweep in degrees.</summary>
    public float Arc = 120f;

    public bool Contains(float phase) => phase >= DamageStart && phase <= DamageEnd;
}

/// <summary>A way out of one combo move into another.</summary>
public sealed class PhxComboTransition
{
    public PhxComboInput Input = PhxComboInput.Attack;

    /// <summary>Fraction of the move during which the input is accepted.</summary>
    public float WindowStart;
    public float WindowEnd = 1f;

    public string TargetMove;

    public bool IsOpen(float phase) => phase >= WindowStart && phase <= WindowEnd;
}

/// <summary>One animation-backed move of a combo.</summary>
public sealed class PhxComboMove
{
    public string Name = "";
    public string AnimationName = "";
    public float Duration = 1f;

    /// <summary>Where to fall back to when nothing else is chosen.</summary>
    public string NextMove;

    public readonly List<PhxComboAttack> Attacks = new List<PhxComboAttack>();
    public readonly List<PhxComboTransition> Transitions = new List<PhxComboTransition>();
}

/// <summary>
/// A hero's authored move set, as shipped in the map data.
/// </summary>
/// <remarks>
/// BF2 heroes are not "a melee weapon that swings"; they are a combo state
/// machine. Each move names an animation, the window inside it during which
/// the blade is lethal, its reach and push, and which moves you can chain into
/// and when. That is what makes saber combat feel like a sequence of committed
/// attacks rather than a repeated button press.
///
/// <c>ComboAnimationBank</c> was already parsed off the melee odf and then
/// never used - which is why melee here has been a single arc sweep with a
/// cooldown. The data is a real chunk type (<see cref="EConfigType.Combo"/>)
/// sitting in the mounted levels.
/// </remarks>
public sealed class PhxComboDefinition
{
    public string Name = "";
    public readonly List<PhxComboMove> Moves = new List<PhxComboMove>();

    /// <summary>Move a fresh attack starts from; the first authored one.</summary>
    public PhxComboMove Opening => Moves.Count > 0 ? Moves[0] : null;

    public PhxComboMove Find(string moveName)
    {
        if (string.IsNullOrEmpty(moveName)) return null;

        for (int i = 0; i < Moves.Count; ++i)
        {
            if (string.Equals(Moves[i].Name, moveName, StringComparison.OrdinalIgnoreCase))
            {
                return Moves[i];
            }
        }
        return null;
    }
}

/// <summary>
/// Reads <c>.combo</c> chunks out of the mounted levels into
/// <see cref="PhxComboDefinition"/>s.
/// </summary>
/// <remarks>
/// The combo schema is not published in the mod tools documentation, so the
/// reader is deliberately tolerant: it looks for the field names the format
/// uses and leaves any it does not recognise alone, and every value has a
/// working default. A combo file that turns out to nest things differently
/// therefore degrades to "fewer moves than authored" rather than to an
/// exception during a hero's first swing.
/// </remarks>
public static class PhxComboLoader
{
    static readonly Dictionary<string, PhxComboDefinition> Cache =
        new Dictionary<string, PhxComboDefinition>(StringComparer.OrdinalIgnoreCase);

    public static void Reset()
    {
        Cache.Clear();
    }

    /// <summary>
    /// Combo by name, or null when the mounted data has none. Null results are
    /// cached too - a hero without a combo file asks on every swing otherwise.
    /// </summary>
    public static PhxComboDefinition Load(string comboName)
    {
        if (string.IsNullOrEmpty(comboName)) return null;
        if (Cache.TryGetValue(comboName, out PhxComboDefinition known)) return known;

        PhxComboDefinition definition = null;
        try
        {
            Config config = PhxGame.GetEnvironment()?.FindConfig(EConfigType.Combo, comboName);
            if (config != null)
            {
                definition = Parse(comboName, config);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Phoenix] Combo '{comboName}' could not be read: {e.Message}");
        }

        if (definition == null)
        {
            BFImportDiagnostics.Missing(BFSourceKind.Config, comboName, "combo");
        }
        else
        {
            BFImportDiagnostics.Resolved(BFSourceKind.Config, comboName);
        }

        Cache[comboName] = definition;
        return definition;
    }

    static PhxComboDefinition Parse(string name, Config config)
    {
        var definition = new PhxComboDefinition { Name = name };

        Field[] moves = config.GetFields("Move");
        if (moves == null || moves.Length == 0) return null;

        for (int i = 0; i < moves.Length; ++i)
        {
            PhxComboMove move = ParseMove(moves[i]);
            if (move != null) definition.Moves.Add(move);
        }

        return definition.Moves.Count > 0 ? definition : null;
    }

    static PhxComboMove ParseMove(Field field)
    {
        Scope scope = field.Scope;
        if (scope == null) return null;

        var move = new PhxComboMove
        {
            Name = field.GetString(),
        };

        if (scope.GetString("Animation", out string anim)) move.AnimationName = anim;
        if (move.AnimationName.Length == 0) move.AnimationName = move.Name;

        if (scope.GetFloat("Duration", out float duration) && duration > 0f) move.Duration = duration;
        if (scope.GetString("Next", out string next)) move.NextMove = next;

        Field[] attacks = scope.GetFields("Attack");
        if (attacks != null)
        {
            for (int i = 0; i < attacks.Length; ++i)
            {
                PhxComboAttack attack = ParseAttack(attacks[i]);
                if (attack != null) move.Attacks.Add(attack);
            }
        }

        Field[] transitions = scope.GetFields("Transition");
        if (transitions != null)
        {
            for (int i = 0; i < transitions.Length; ++i)
            {
                PhxComboTransition transition = ParseTransition(transitions[i]);
                if (transition != null) move.Transitions.Add(transition);
            }
        }

        return move;
    }

    static PhxComboAttack ParseAttack(Field field)
    {
        Scope scope = field.Scope;
        if (scope == null) return null;

        var attack = new PhxComboAttack();

        // DamageLength is authored as the pair (start, end) of the window in
        // which the blade is live, as fractions of the move.
        if (scope.GetVec2("DamageLength", out LibSWBF2.Types.Vector2 window))
        {
            attack.DamageStart = window.X;
            attack.DamageEnd = Mathf.Max(window.X, window.Y);
        }

        if (scope.GetFloat("Damage", out float damage)) attack.Damage = damage;
        if (scope.GetFloat("Push", out float push)) attack.Push = push;
        if (scope.GetFloat("DamageLength", out float length) && length > 0f)
        {
            // Single-value form: the window is that long, starting immediately.
            attack.DamageStart = 0f;
            attack.DamageEnd = Mathf.Clamp01(length);
        }

        // "Edge" is the reach of the swing in the authored data; a second
        // value, where present, is the arc.
        if (scope.GetVec2("Edge", out LibSWBF2.Types.Vector2 edge))
        {
            if (edge.X > 0f) attack.Reach = edge.X;
            if (edge.Y > 0f) attack.Arc = edge.Y;
        }
        else if (scope.GetFloat("Edge", out float reach) && reach > 0f)
        {
            attack.Reach = reach;
        }

        return attack;
    }

    static PhxComboTransition ParseTransition(Field field)
    {
        Scope scope = field.Scope;
        if (scope == null) return null;

        var transition = new PhxComboTransition
        {
            TargetMove = scope.GetString("State"),
        };

        if (string.IsNullOrEmpty(transition.TargetMove))
        {
            // Some transitions name their target as the field's own value.
            transition.TargetMove = field.GetString();
        }
        if (string.IsNullOrEmpty(transition.TargetMove)) return null;

        transition.Input = InputFromString(scope.GetString("Input"));

        if (scope.GetVec2("Duration", out LibSWBF2.Types.Vector2 window))
        {
            transition.WindowStart = window.X;
            transition.WindowEnd = Mathf.Max(window.X, window.Y);
        }
        else if (scope.GetFloat("Duration", out float open) && open > 0f)
        {
            transition.WindowStart = open;
            transition.WindowEnd = 1f;
        }

        return transition;
    }

    static PhxComboInput InputFromString(string value)
    {
        if (string.IsNullOrEmpty(value)) return PhxComboInput.None;

        string lower = value.ToLowerInvariant();
        if (lower.Contains("attack") || lower.Contains("fire")) return PhxComboInput.Attack;
        if (lower.Contains("block") || lower.Contains("guard")) return PhxComboInput.Block;
        if (lower.Contains("jump")) return PhxComboInput.Jump;
        if (lower.Contains("dodge") || lower.Contains("roll")) return PhxComboInput.Dodge;
        if (lower.Contains("special") || lower.Contains("force")) return PhxComboInput.Special;
        return PhxComboInput.None;
    }
}
