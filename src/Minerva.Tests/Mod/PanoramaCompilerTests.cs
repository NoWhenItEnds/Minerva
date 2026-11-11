using System.Text;
using Minerva.Mod;
using Minerva.Mod.Formats;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Minerva.Tests.Mod;

public class PanoramaCompilerTests
{
    [Fact]
    public void Re_encoding_the_games_layout_tree_is_lossless()
    {
        var (_, original) = ModBuilder.ReadHostLayout(GameFiles.Install);

        var tree = PanoramaCompiler.ReadLayoutTree(original);
        var reencoded = PanoramaCompiler.ReplaceLayoutTree(original, tree);

        Assert.Equal(PanoramaCompiler.DecompileLayout(original), PanoramaCompiler.DecompileLayout(reencoded));
    }

    [Fact]
    public void Host_layout_override_includes_the_script_and_keeps_the_original_panels()
    {
        var (_, original) = ModBuilder.ReadHostLayout(GameFiles.Install);
        var tree = PanoramaCompiler.ReadLayoutTree(original);

        LayoutTree.AddScriptInclude(tree, ModBuilder.CompiledScriptReference);
        LayoutTree.AddScriptInclude(tree, ModBuilder.CompiledScriptReference); // idempotent
        var xml = PanoramaCompiler.DecompileLayout(PanoramaCompiler.ReplaceLayoutTree(original, tree));
        var originalXml = PanoramaCompiler.DecompileLayout(original);

        Assert.Single(LayoutTree.Includes(tree), i => i == ModBuilder.CompiledScriptReference);
        Assert.Contains("<scripts>", xml);
        Assert.Contains($"s2r://{ModBuilder.CompiledScriptReference}", xml);
        // Everything Valve had is still there.
        foreach (var line in originalXml.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('<') && !l.StartsWith("<!--")))
            Assert.Contains(line, xml);
    }

    [Fact]
    public void Compiled_script_reads_back_as_the_same_javascript()
    {
        const string js = "'use strict';\n$.Msg('hello \u00e9');\n";
        var compiled = PanoramaCompiler.CompileScript("minerva", "panorama/scripts/minerva/test.js", js);

        using var resource = new Resource { FileName = "test.vjs_c" };
        resource.Read(new MemoryStream(compiled));

        var panorama = Assert.IsAssignableFrom<Panorama>(resource.DataBlock);
        Assert.Equal(js, Encoding.UTF8.GetString(panorama.Data));
        Assert.Equal(4, resource.Version);
    }

    [Fact]
    public void Resource_container_round_trips()
    {
        var file = new ResourceFile(3, [new ResourceBlock("RED2", [1, 2, 3]), new ResourceBlock("DATA", new byte[37])]);
        var back = ResourceFile.Read(file.Write());

        Assert.Equal(3, back.ResourceVersion);
        Assert.Equal(["RED2", "DATA"], back.Blocks.Select(b => b.Tag));
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Blocks[0].Data);
        Assert.Equal(37, back.Blocks[1].Data.Length);
    }

    [Fact]
    public void Built_addon_contains_override_script_and_manifest()
    {
        var install = GameFiles.Install;
        var vpk = Path.Combine(Path.GetTempPath(), $"minerva_test_{Guid.NewGuid():N}_dir.vpk");
        try
        {
            var manifest = ModBuilder.Build(install, vpk);
            var readBack = ModBuilder.ReadManifest(vpk);

            Assert.NotNull(readBack);
            Assert.Equal(manifest.HostLayoutCrc, readBack.HostLayoutCrc);
            Assert.Equal(manifest.GameBuild, readBack.GameBuild);

            using var package = new SteamDatabase.ValvePak.Package();
            package.Read(vpk);
            Assert.NotNull(package.FindEntry(manifest.HostLayout));
            Assert.NotNull(package.FindEntry(ModBuilder.CompiledScriptReference + "_c"));
        }
        finally
        {
            File.Delete(vpk);
        }
    }
}
