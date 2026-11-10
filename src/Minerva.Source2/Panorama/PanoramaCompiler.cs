using System.Text;
using Minerva.Source2.Kv3;
using Minerva.Source2.Resources;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace Minerva.Source2.Panorama;

/// <summary>
/// Produces compiled Panorama resources without Valve's resource compiler.
/// </summary>
/// <remarks>
/// Block contents follow what the resource compiler writes, as documented by modlock's
/// <c>source2/panorama/compile.go</c> (https://github.com/paralin/modlock, MIT). Layout overrides are derived from
/// the game's own compiled layout so they track the installed build rather than a hand-written copy.
/// </remarks>
public static class PanoramaCompiler
{
    /// <summary>Compiled resource name a layout references, e.g. <c>panorama/scripts/x.js</c> → <c>panorama/scripts/x.vjs</c>.</summary>
    public static string CompiledName(string sourcePath) => Path.ChangeExtension(sourcePath, Path.GetExtension(sourcePath) switch
    {
        ".js" => ".vjs",
        ".xml" => ".vxml",
        ".css" => ".vcss",
        var ext => throw new ArgumentException($"Not a Panorama source: {ext}"),
    }).Replace('\\', '/');

    /// <summary>Compile JavaScript to a <c>.vjs_c</c> resource (the DATA block stores the script text).</summary>
    /// <param name="addon">Addon name recorded in the edit info (informational).</param>
    /// <param name="sourcePath">Source path inside the addon, e.g. <c>panorama/scripts/minerva/minerva.js</c>.</param>
    public static byte[] CompileScript(string addon, string sourcePath, string javascript)
    {
        var source = Encoding.UTF8.GetBytes(javascript);
        var red2 = EditInfo(
        [
            Dependency(addon, sourcePath, Crc32.Compute(source), exists: true),
            Dependency(addon, CompiledName(sourcePath), 0, exists: false),
        ], "CompileJavaScript", "JavaScript Compiler Version", 4);
        return new ResourceFile(4, [new ResourceBlock("RED2", red2), new ResourceBlock("DATA", source)]).Write();
    }

    /// <summary>Read the compiled layout tree (the LaCo block) of a <c>.vxml_c</c>.</summary>
    public static Kv3Object ReadLayoutTree(byte[] compiledLayout)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(compiledLayout));
        if (resource.GetBlockByType(BlockType.LaCo) is not BinaryKV3 laco)
            throw new InvalidDataException("Layout has no LaCo block (pre-v3 layout format?).");
        return Kv3Convert.FromKv(laco.Data.Root) as Kv3Object
               ?? throw new InvalidDataException("LaCo root is not an object.");
    }

    /// <summary>
    /// Re-encode a game layout with a modified tree. Every other block (edit info, DATA) and the header versions are
    /// copied from the original, so the output matches the installed build's format.
    /// </summary>
    public static byte[] ReplaceLayoutTree(byte[] compiledLayout, Kv3Object tree)
    {
        var original = ResourceFile.Read(compiledLayout);
        if (original.Find("LaCo") == null) throw new InvalidDataException("Layout has no LaCo block.");
        return original.WithBlock("LaCo", Kv3Writer.Write(tree)).Write();
    }

    /// <summary>Decompile a compiled layout back to XML (via ValveResourceFormat) for inspection and tests.</summary>
    public static string DecompileLayout(byte[] compiledLayout)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(compiledLayout));
        using var writer = new IndentedTextWriter();
        resource.DataBlock!.WriteText(writer);
        return writer.ToString();
    }

    private static Kv3Object Dependency(string addon, string path, uint crc, bool exists) => new()
    {
        { "m_RelativeFilename", path },
        { "m_SearchPath", "citadel_addons/" + addon },
        { "m_nFileCRC", crc },
        { "m_bOptional", !exists },
        { "m_bFileExists", exists },
        { "m_bIsGameFile", false },
    };

    private static byte[] EditInfo(Kv3Object[] inputs, string compiler, string name, int version)
    {
        Kv3Object Special(string text, int fingerprint) => new()
        {
            { "m_String", text },
            { "m_CompilerIdentifier", compiler },
            { "m_nFingerprint", fingerprint },
            { "m_nUserData", 0 },
        };

        return Kv3Writer.Write(new Kv3Object
        {
            { "m_InputDependencies", new Kv3Array(inputs) },
            { "m_AdditionalInputDependencies", new Kv3Array() },
            {
                "m_ArgumentDependencies", new Kv3Array
                {
                    new Kv3Object
                    {
                        { "m_ParameterName", "___OverrideInputData___" },
                        { "m_ParameterType", "BinaryBlobArg" },
                        { "m_nFingerprint", 0 },
                        { "m_nFingerprintDefault", 0 },
                    },
                }
            },
            { "m_SpecialDependencies", new Kv3Array { Special(name, version), Special("Panorama Preprocessor Presence/Version", 0) } },
            { "m_SpecialInputDependencies", new Kv3Array() },
            { "m_AdditionalRelatedFiles", new Kv3Array() },
            { "m_ChildResourceList", new Kv3Array() },
            { "m_WeakReferenceList", new Kv3Array() },
            { "m_SearchableUserData", new Kv3Object { { "IsChildResource", 0 } } },
            { "m_SubassetReferences", null },
            { "m_SubassetDefinitions", null },
        });
    }
}
