namespace AfallonCoop.World;

/// <summary>
/// The name a place goes by across machines: the scene, and in an instance "scene#instance"
/// (a dungeon's run, a battleground's match). Spawner keys, who sees whom, whose mobs are whose,
/// the scene a player reports and the scene a payload is addressed to all compare these strings.
/// </summary>
public static class PlaceName
{
    public const char Separator = '#';

    /// <summary>The scene, or "scene#instance" when the scene is instanced (no instance: the scene itself).</summary>
    public static string Of(string scene, string instance) =>
        string.IsNullOrEmpty(instance) ? scene ?? "" : (scene ?? "") + Separator + instance;
}
