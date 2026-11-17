namespace AfallonCoop.Protocol;

public sealed class HelloMsg
{
    public ushort Protocol;
    public string ModVersion = "";
    public string GameBuild = "";
    public string PlayerName = "";
    public ulong SteamId;
    /// <summary>Random id this mod install made for itself once: who a LAN/IP player is across reconnects.</summary>
    public string ClientId = "";
    /// <summary>The host's password, if it has one (a co-op guard against strangers, not a secret store).</summary>
    public string Password = "";

    public void Write(NetWriter writer)
    {
        writer.U8((byte)MsgType.Hello);
        writer.U16(Protocol);
        writer.Str(ModVersion);
        writer.Str(GameBuild);
        writer.Str(PlayerName);
        writer.U64(SteamId);
        writer.Str(ClientId);
        writer.Str(Password);
    }

    public static HelloMsg Read(ref NetReader reader)
    {
        var hello = new HelloMsg
        {
            Protocol = reader.U16(),
            ModVersion = reader.Str(),
            GameBuild = reader.Str(),
            PlayerName = reader.Str(),
            SteamId = reader.U64(),
        };
        // older versions end here: still read, then rejected by version
        if (reader.Remaining > 0) hello.ClientId = reader.Str();
        if (reader.Remaining > 0) hello.Password = reader.Str();
        return hello;
    }
}
