using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The seam a future networking layer would attach to.
/// </summary>
/// <remarks>
/// <b>Structural scaffolding only. There is no transport behind this, nothing
/// constructs it, and <see cref="Mode"/> is permanently
/// <see cref="BFNetMode.Standalone"/> until something sets it.</b>
///
/// This exists to answer one question early: what would have to change for
/// multiplayer to be possible? The honest answer is not "add sockets" - it is
/// that gameplay state currently has no single owner. Lua mutates it directly
/// in places, instances mutate themselves, and there is no notion of authority.
/// Retrofitting a transport under that is the expensive part, and it is
/// architectural rather than technical.
///
/// So this file defines the shape of the answer without pretending to
/// implement it:
///
/// <list type="bullet">
/// <item><see cref="BFNetMode"/> - who is authoritative.</item>
/// <item><see cref="IBFNetTransport"/> - the send/receive boundary, so the
/// choice of library stays swappable.</item>
/// <item><see cref="BFNetAuthority"/> - the predicate every mutation site
/// would eventually consult.</item>
/// </list>
///
/// The value of writing it now is that <see cref="BFNetAuthority.IsAuthoritative"/>
/// returns true in standalone, so call sites can adopt it incrementally
/// without any behaviour change, and the migration stops being one enormous
/// commit.
/// </remarks>
public enum BFNetMode
{
    /// <summary>No networking. Local play owns all state. The only mode today.</summary>
    Standalone,
    /// <summary>Authoritative host, also playing.</summary>
    ListenServer,
    /// <summary>Authoritative host, not playing.</summary>
    DedicatedServer,
    /// <summary>Non-authoritative; predicts locally and reconciles.</summary>
    Client,
}

/// <summary>What a replicated message is about.</summary>
public enum BFNetChannel
{
    /// <summary>Connection handshake, map change, team assignment.</summary>
    Session,
    /// <summary>Pawn transforms and input. High rate, loss tolerant.</summary>
    Movement,
    /// <summary>Damage, deaths, spawns. Must arrive.</summary>
    Gameplay,
    /// <summary>Command post ownership, tickets, match end.</summary>
    MatchState,
    /// <summary>Chat and voice-over cues.</summary>
    Chat,
}

/// <summary>
/// The transport boundary. Deliberately minimal - anything richer would be
/// guessing at a library that has not been chosen.
/// </summary>
public interface IBFNetTransport
{
    bool IsConnected { get; }

    void Send(BFNetChannel channel, ArraySegment<byte> payload, bool reliable);

    /// <summary>Raised on the main thread once per received message.</summary>
    event Action<BFNetChannel, ArraySegment<byte>> OnReceive;

    void Shutdown();
}

/// <summary>
/// Who is allowed to change gameplay state.
/// </summary>
/// <remarks>
/// Every site that mutates match state should eventually ask this before doing
/// so. In standalone it always says yes, so adopting it is free and changes
/// nothing - which is the point. The alternative is a single flag day where
/// every mutation site changes at once, and that is how this kind of migration
/// usually fails.
/// </remarks>
public static class BFNetAuthority
{
    public static BFNetMode Mode { get; private set; } = BFNetMode.Standalone;

    /// <summary>The active transport, or null in standalone.</summary>
    public static IBFNetTransport Transport { get; private set; }

    /// <summary>
    /// True when this process may change gameplay state directly.
    /// Always true today.
    /// </summary>
    public static bool IsAuthoritative =>
        Mode != BFNetMode.Client;

    /// <summary>True when this process draws a local view.</summary>
    public static bool HasLocalPlayer =>
        Mode != BFNetMode.DedicatedServer;

    /// <summary>
    /// Install a transport and mode. Not called from anywhere yet; a
    /// networking implementation would call it during session setup.
    /// </summary>
    public static void Configure(BFNetMode mode, IBFNetTransport transport)
    {
        Mode = mode;
        Transport = transport;
    }

    public static void Reset()
    {
        Transport?.Shutdown();
        Transport = null;
        Mode = BFNetMode.Standalone;
    }
}
