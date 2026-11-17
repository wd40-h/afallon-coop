using AfallonCoop.Protocol;

namespace AfallonCoop.Session;

/// <summary>Host side: decides whether a hello is let in, and if not, tells the player why.</summary>
internal sealed class JoinPolicy
{
    private readonly SessionConfig _config;
    private readonly Func<ulong, string, bool> _isBanned;

    public JoinPolicy(SessionConfig config, Func<ulong, string, bool> isBanned)
    {
        _config = config;
        _isBanned = isBanned;
    }

    /// <summary>The first rule the hello breaks, or null when the player may join. The order is part of the behaviour.</summary>
    public string RejectionReason(HelloMsg hello, bool alreadyConnected, int playerCount)
    {
        if (hello.Protocol != ProtocolInfo.Version)
            return $"Mod protocol mismatch (host {ProtocolInfo.Version}, you {hello.Protocol}). Update the mod.";
        if (IsGameBuildMismatch(hello))
            return "Different game version than the host. Both players must update Afallon.";
        if (IsPasswordWrong(hello))
            return string.IsNullOrEmpty(hello.Password) ? "This game has a password: enter it under CO-OP > JOIN GAME." : "Wrong password.";
        if (_isBanned(hello.SteamId, hello.ClientId))
            return "The host has banned you from this game.";
        if (alreadyConnected)
            return "Already connected.";
        if (playerCount >= ProtocolInfo.MaxPlayers)
            return $"Session is full ({ProtocolInfo.MaxPlayers} players).";
        return null;
    }

    private bool IsGameBuildMismatch(HelloMsg hello) =>
        !string.IsNullOrEmpty(_config.GameBuild)
        && !string.IsNullOrEmpty(hello.GameBuild)
        && hello.GameBuild != _config.GameBuild;

    private bool IsPasswordWrong(HelloMsg hello) =>
        !string.IsNullOrEmpty(_config.Password) && hello.Password != _config.Password;
}
