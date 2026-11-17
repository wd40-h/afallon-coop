using AfallonCoop.World;
using Xunit;

namespace AfallonCoop.Tests;

/// <summary>
/// The place key shared by the mod and the protocol: "scene" outside instances, "scene#instance" inside one.
/// Several modules match on its suffix (a group's dungeon is "#g3", a battleground's match "bg..."), so the shape
/// must not drift.
/// </summary>
public class PlaceNameTests
{
    [Fact]
    public void A_scene_without_an_instance_is_just_the_scene()
    {
        Assert.Equal("CoalwayWoods", PlaceName.Of("CoalwayWoods", ""));
        Assert.Equal("CoalwayWoods", PlaceName.Of("CoalwayWoods", null!));
    }

    [Fact]
    public void An_instanced_scene_is_scene_hash_instance()
    {
        Assert.Equal("Dungeon1#g3", PlaceName.Of("Dungeon1", "g3"));
        Assert.Equal("Battleground1#bg12", PlaceName.Of("Battleground1", "bg12"));
    }

    [Fact]
    public void The_instance_stays_the_suffix_other_modules_match_on()
    {
        // GroupLoot: a group's run is "…" + "#g" + group id.
        Assert.EndsWith("#g3", PlaceName.Of("Dungeon1", "g3"));
        // Dungeons.InDungeon: a battleground's match is "#bg…", which is not a dungeon.
        Assert.StartsWith("bg", PlaceName.Of("Battleground1", "bg12")[(PlaceName.Of("Battleground1", "bg12").IndexOf('#') + 1)..]);
    }

    [Fact]
    public void An_empty_scene_is_empty_not_a_bare_separator()
    {
        Assert.Equal("", PlaceName.Of("", ""));
        Assert.Equal("#g1", PlaceName.Of("", "g1"));
    }
}
