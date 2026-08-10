using System;
using UnityEngine;

/// <summary>
/// Runs one hero through their authored move set.
/// </summary>
/// <remarks>
/// The state machine is the point, not the animation playback: a move commits
/// the hero for its duration, damage only lands inside the authored window,
/// and pressing attack again does nothing unless it arrives while a transition
/// out of the current move is open. That is what makes chained swings feel
/// earned and mistimed ones feel punished - and it is why a hero cannot be
/// implemented as a weapon with a cooldown, which is what melee was.
///
/// This owns timing and sequencing only. Applying damage is a callback, so the
/// weapon keeps its own hit detection and the runner has no opinion about
/// physics.
/// </remarks>
public sealed class PhxComboRunner
{
    /// <summary>Called once per attack window, with that window's parameters.</summary>
    public Action<PhxComboAttack> OnAttackWindow;

    /// <summary>Called when a new move begins, with its animation name.</summary>
    public Action<PhxComboMove> OnMoveStarted;

    readonly PhxComboDefinition Definition;

    PhxComboMove Current;
    float Elapsed;
    PhxComboInput Buffered = PhxComboInput.None;
    float BufferedAt = float.NegativeInfinity;
    int AttacksFired;

    /// <summary>
    /// How long an input is remembered while waiting for a window to open.
    /// Without a buffer, chaining requires pressing inside the window to the
    /// frame, which reads as unresponsive rather than demanding.
    /// </summary>
    const float InputBufferSeconds = 0.25f;

    public bool IsBusy => Current != null;
    public PhxComboMove CurrentMove => Current;

    /// <summary>Progress through the current move, 0..1.</summary>
    public float Phase => Current == null || Current.Duration <= 0f
        ? 0f
        : Mathf.Clamp01(Elapsed / Current.Duration);

    public PhxComboRunner(PhxComboDefinition definition)
    {
        Definition = definition;
    }

    /// <summary>
    /// Note an input. Returns true if it started or chained a move, false if
    /// it was only buffered - so a caller can still play a "denied" response.
    /// </summary>
    public bool Press(PhxComboInput input)
    {
        if (Definition == null) return false;

        if (Current == null)
        {
            if (input != PhxComboInput.Attack) return false;

            Begin(Definition.Opening);
            return Current != null;
        }

        // Mid-move: only an open transition accepts it now. Anything else is
        // held briefly in case a window opens shortly.
        PhxComboTransition transition = FindOpenTransition(input);
        if (transition != null)
        {
            Begin(Definition.Find(transition.TargetMove));
            return Current != null;
        }

        Buffered = input;
        BufferedAt = Time.time;
        return false;
    }

    public void Tick(float deltaTime)
    {
        if (Current == null) return;

        float previousPhase = Phase;
        Elapsed += deltaTime;
        float phase = Phase;

        FireAttackWindows(previousPhase, phase);

        // A buffered input takes effect the moment its window opens.
        if (Buffered != PhxComboInput.None && Time.time - BufferedAt <= InputBufferSeconds)
        {
            PhxComboTransition transition = FindOpenTransition(Buffered);
            if (transition != null)
            {
                Buffered = PhxComboInput.None;
                Begin(Definition.Find(transition.TargetMove));
                return;
            }
        }

        if (phase < 1f) return;

        // Move over. An authored follow-on continues the sequence; otherwise
        // the hero returns to neutral and the next press starts again.
        PhxComboMove next = Definition.Find(Current.NextMove);
        if (next != null)
        {
            Begin(next);
        }
        else
        {
            Current = null;
            Buffered = PhxComboInput.None;
        }
    }

    /// <summary>Drop out of the sequence - death, being hit out of it, exiting.</summary>
    public void Interrupt()
    {
        Current = null;
        Elapsed = 0f;
        Buffered = PhxComboInput.None;
        AttacksFired = 0;
    }

    void Begin(PhxComboMove move)
    {
        if (move == null)
        {
            Current = null;
            return;
        }

        Current = move;
        Elapsed = 0f;
        AttacksFired = 0;
        OnMoveStarted?.Invoke(move);
    }

    /// <summary>
    /// Fire each attack whose window was crossed this frame.
    /// </summary>
    /// <remarks>
    /// Crossed, not "contains": at a low frame rate a whole window can fall
    /// between two ticks, and an attack that silently does not happen is the
    /// worst kind of combat bug because it only appears under load. Attacks
    /// are fired in authored order and each one at most once per move, which
    /// is what <see cref="AttacksFired"/> tracks.
    /// </remarks>
    void FireAttackWindows(float fromPhase, float toPhase)
    {
        for (int i = AttacksFired; i < Current.Attacks.Count; ++i)
        {
            PhxComboAttack attack = Current.Attacks[i];
            bool crossed = attack.DamageStart <= toPhase && attack.DamageEnd >= fromPhase;
            if (!crossed) continue;

            AttacksFired = i + 1;
            OnAttackWindow?.Invoke(attack);
        }
    }

    PhxComboTransition FindOpenTransition(PhxComboInput input)
    {
        if (Current == null) return null;

        float phase = Phase;
        for (int i = 0; i < Current.Transitions.Count; ++i)
        {
            PhxComboTransition transition = Current.Transitions[i];
            if (transition.Input != input) continue;
            if (!transition.IsOpen(phase)) continue;
            if (Definition.Find(transition.TargetMove) == null) continue;

            return transition;
        }
        return null;
    }
}
