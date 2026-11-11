using Minerva.Mod;
using Minerva.Mod.Formats;

namespace Minerva.Tests.Mod;

public class ClassProbeTests
{
    [Fact]
    public void Compound_selectors_give_the_classes_that_apply_to_one_panel()
    {
        const string css = """
            /* .commented { } */
            .map_button.neutral.active, .map_button.player > #Image
            {
                background-image: url("s2r://panorama/images/minimap/x.vtex");
            }
            @media (max-width: 10px) { .invert_map .map_button.enemy { opacity: 0.5; } }
            @keyframes pulse { 0% { opacity: 0; } 100% { opacity: 1; } }
            """;

        var sets = CssSelectors.CompoundClassSets(css).Select(s => string.Join(' ', s.Order(StringComparer.Ordinal))).ToList();

        Assert.Equal(["active map_button neutral", "map_button player", "invert_map", "enemy map_button"], sets);
    }

    [Fact]
    public void Minimap_probe_is_derived_from_the_installed_game()
    {
        var (script, probes, warnings) = ModBuilder.GenerateScript(GameFiles.Install);

        Assert.Empty(warnings);
        var minimap = Assert.Single(probes);
        Assert.Equal("hud_minimap", minimap.Layout);
        Assert.Equal("map_button", minimap.Gate);
        foreach (var expected in new[] { "player", "enemy", "friend", "PlayerDead", "neutral", "active", "boss" })
            Assert.Contains(expected, minimap.Classes);
        Assert.Contains("\"hud_minimap\":{\"gate\":\"map_button\"", script);
        Assert.DoesNotContain(ModBuilder.ProbesPlaceholder, script);
    }
}
