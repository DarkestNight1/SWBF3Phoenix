using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>The things that happen in a Battlefront match.</summary>
/// <remarks>
/// Named after the original scripting architecture's vocabulary rather than
/// after any Unity concept, because that is the level at which the rest of the
/// game already thinks: mission scripts, the HUD, scoring, AI and audio all
/// reason about "a command post was captured", not about a MonoBehaviour
/// calling another MonoBehaviour.
/// </remarks>
public enum BFEvent
{
    MissionStart,
    MissionEnd,

    ObjectCreated,
    ObjectDestroyed,

    UnitSpawned,
    UnitDied,

    CommandPostCaptured,
    CommandPostNeutralized,
    CommandPostContested,

    ObjectiveUpdated,
    ObjectiveCompleted,

    HeroSpawned,
    HeroLost,

    VehicleSpawned,
    VehicleDestroyed,

    FlagTaken,
    FlagDropped,
    FlagCaptured,

    TeamPointsChanged,
    ReinforcementsChanged,
}

/// <summary>What happened, in terms every subscriber can read.</summary>
public readonly struct BFEventArgs
{
    public readonly BFEvent Event;

    /// <summary>Team the event is about, or 0.</summary>
    public readonly int Team;

    /// <summary>The scene object involved, when there is one.</summary>
    public readonly GameObject Subject;

    /// <summary>Its authored identity, when it has one.</summary>
    public readonly BFSourceRef Source;

    /// <summary>Who caused it, when that is known and different from the subject.</summary>
    public readonly GameObject Instigator;

    /// <summary>Event-specific magnitude - points scored, tickets lost.</summary>
    public readonly float Value;

    /// <summary>Event-specific name - an objective, a region, an odf class.</summary>
    public readonly string Name;

    public BFEventArgs(BFEvent e, int team = 0, GameObject subject = null,
                       GameObject instigator = null, float value = 0f, string name = null)
    {
        Event = e;
        Team = team;
        Subject = subject;
        Instigator = instigator;
        Value = value;
        Name = name ?? string.Empty;
        Source = subject != null ? BFSourceLink.Find(subject) : null;
    }
}

/// <summary>
/// The one place game events are announced, and the one place to listen.
/// </summary>
/// <remarks>
/// The architectural point is a separation of ownership: <b>Lua tells the game
/// to do something; the game owns what actually happened and says so here.</b>
///
/// Without that, every consumer of an event has to be wired to its producer -
/// the HUD to the command post, scoring to the damage system, audio to the
/// match, AI to all three - and adding a consumer means editing every
/// producer. It also means Lua ends up owning gameplay state, because the
/// script layer becomes the only thing that sees everything.
///
/// The existing <c>PhxLuaEvents</c> stays exactly as it is and keeps doing its
/// job, which is a different one: it is the bridge that lets mission scripts
/// register callbacks with the signatures BF2 scripts expect. This bus is for
/// engine systems talking to each other, and it is what the Lua bridge itself
/// should eventually be driven from rather than being called directly by every
/// system that fires an event.
/// </remarks>
public static class BFEventBus
{
    static readonly Dictionary<BFEvent, List<Action<BFEventArgs>>> Listeners =
        new Dictionary<BFEvent, List<Action<BFEventArgs>>>();

    static readonly List<Action<BFEventArgs>> AnyListeners = new List<Action<BFEventArgs>>();

    /// <summary>Events raised since the last reset. Diagnostics.</summary>
    public static int RaisedCount { get; private set; }

    public static void Reset()
    {
        Listeners.Clear();
        AnyListeners.Clear();
        RaisedCount = 0;
    }

    public static void Subscribe(BFEvent e, Action<BFEventArgs> listener)
    {
        if (listener == null) return;

        if (!Listeners.TryGetValue(e, out List<Action<BFEventArgs>> list))
        {
            list = new List<Action<BFEventArgs>>();
            Listeners.Add(e, list);
        }
        if (!list.Contains(listener)) list.Add(listener);
    }

    /// <summary>Listen to everything - for logging, replay, or a debug overlay.</summary>
    public static void SubscribeAll(Action<BFEventArgs> listener)
    {
        if (listener != null && !AnyListeners.Contains(listener)) AnyListeners.Add(listener);
    }

    public static void Unsubscribe(BFEvent e, Action<BFEventArgs> listener)
    {
        if (Listeners.TryGetValue(e, out List<Action<BFEventArgs>> list))
        {
            list.Remove(listener);
        }
    }

    public static void UnsubscribeAll(Action<BFEventArgs> listener)
    {
        AnyListeners.Remove(listener);
    }

    /// <summary>
    /// Announce that something happened.
    /// </summary>
    /// <remarks>
    /// A throwing listener is isolated. One misbehaving subscriber must not
    /// stop the others hearing about a capture - that failure mode turns a
    /// bug in the HUD into a match that cannot end.
    /// </remarks>
    public static void Raise(in BFEventArgs args)
    {
        ++RaisedCount;

        if (Listeners.TryGetValue(args.Event, out List<Action<BFEventArgs>> list))
        {
            for (int i = 0; i < list.Count; ++i)
            {
                Invoke(list[i], args);
            }
        }

        for (int i = 0; i < AnyListeners.Count; ++i)
        {
            Invoke(AnyListeners[i], args);
        }
    }

    /// <summary>Shorthand for the common case.</summary>
    public static void Raise(BFEvent e, int team = 0, GameObject subject = null,
                             GameObject instigator = null, float value = 0f, string name = null)
    {
        Raise(new BFEventArgs(e, team, subject, instigator, value, name));
    }

    static void Invoke(Action<BFEventArgs> listener, in BFEventArgs args)
    {
        try
        {
            listener(args);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[BFEventBus] Listener for {args.Event} threw: {ex}");
        }
    }
}
