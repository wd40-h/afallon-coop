namespace AfallonCoop.Protocol;

public enum MsgType : byte
{
    /// <summary>Client to host.</summary>
    Hello = 1,
    /// <summary>Host to client.</summary>
    Welcome = 2,
    /// <summary>Host to client, then disconnect.</summary>
    Reject = 3,
    /// <summary>Host to all.</summary>
    PlayerJoined = 4,
    /// <summary>Host to all.</summary>
    PlayerLeft = 5,
    /// <summary>Client to host to others: character chosen, level, scene.</summary>
    PlayerInfo = 6,
    /// <summary>Unreliable; client to host to others.</summary>
    PlayerState = 7,
    Chat = 8,
    Ping = 9,
    Pong = 10,
    /// <summary>Client to host: "I have a menu open" (for the all-in-menu pause rule).</summary>
    MenuState = 11,
    /// <summary>Host to all: authoritative pause on/off and reason.</summary>
    PauseState = 12,
    /// <summary>Client to host to others: an animation, visual effect or sound the owner's character played.</summary>
    Fx = 13,
    /// <summary>Same as <see cref="Fx"/>, unreliable: continuous updates where only the latest matters (projectile positions).</summary>
    FxStream = 14,
    /// <summary>Client to host: "I am near spawner K" or "no longer near" (lease subscription).</summary>
    Interest = 15,
    /// <summary>Client to host: the owner reports whether spawner K is fighting (ownership stays put meanwhile).</summary>
    LeaseCombat = 16,
    /// <summary>Host to all: spawner K is now simulated by player P (epoch E).</summary>
    LeaseOwner = 17,
    /// <summary>Client to host to others: world object events from their owner (mob spawned, died).</summary>
    World = 18,
    /// <summary>Same as <see cref="World"/>, unreliable: world object state stream (positions, animation).</summary>
    WorldStream = 19,
    /// <summary>Player groups: requests client to host, state, invites and notices host to clients (<see cref="GroupOp"/>).</summary>
    Group = 20,
    /// <summary>Client to host to one player: a world stream meant for that player only; arrives as <see cref="WorldStream"/>.</summary>
    WorldStreamTo = 21,
}

/// <summary>Sub-kinds of <see cref="MsgType.Group"/>. 1-9: client to host. 10 and up: host to client.</summary>
public enum GroupOp : byte
{
    Invite = 1,
    Answer = 2,
    Leave = 3,
    Kick = 4,
    Promote = 5,
    DismissBot = 6,
    Bots = 7,
    /// <summary>A message for the sender's group (the plugin's own content); the host relays it as <see cref="FromMember"/>.</summary>
    Cast = 8,
    State = 10,
    InvitedBy = 11,
    Notice = 12,
    BotDismissed = 13,
    ReturnTo = 14,
    FromMember = 15,
}

public enum PauseReason : byte
{
    None = 0,
    Host = 1,
    AllInMenu = 2,
}
