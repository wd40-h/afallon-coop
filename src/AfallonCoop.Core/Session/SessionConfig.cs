namespace AfallonCoop.Session;

public sealed class SessionConfig
{
    public string ModVersion = "";
    public string GameBuild = "";
    public string PlayerName = "Player";
    public ulong SteamId;
    /// <summary>This install's own random id (see <see cref="Protocol.HelloMsg.ClientId"/>).</summary>
    public string ClientId = "";
    /// <summary>Host: players must give it to join (empty = anyone). Client: the one given.</summary>
    public string Password = "";
}

public enum SessionState { Connecting, Handshaking, Connected, Closed }
