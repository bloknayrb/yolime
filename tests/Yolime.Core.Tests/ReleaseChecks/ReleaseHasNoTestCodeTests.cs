using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Yolime.Core.Tests.ReleaseChecks;

/// <summary>
/// Test-only code must compile only into E2E builds. Reads built assemblies' metadata: type definitions and
/// references (UTF-8 #Strings heap) and string literals (UTF-16 #US heap, which a plain grep can't see).
/// Point YOLIME_RELEASE_DLL_DIR at a Release App output folder and YOLIME_E2E_DLL_DIR at an E2E one; the E2E case
/// proves the check can fail.
/// </summary>
public class ReleaseHasNoTestCodeTests
{
    private static readonly string[] AppTestTypes = ["TestAudioRunner", "FileOutputSink", "ClipboardSink", "E2EMode", "FileMicDevice", "FileAudioSource", "FakeTranscriptionSessionFactory", "E2ERewriter", "FileFeedHandler", "FileConfig", "TourStates"];
    private static readonly string[] CoreTestTypes = ["FakeTranscriptionSession", "FakeTranscriptionSessionFactory"];

    /// <summary>The E2E file hooks the harness drops into the results folder.</summary>
    private static readonly string[] AppHookFiles = ["stall-ui", "show-card", "probe-fit", "shrink-card", "check-updates", "tour-state", "tour-freeze", "probe-theme", "set-capture-hidden"];

    /// <summary>Test members on product types: TestShrinkPx on OverlayWindow, the fit probe and the tour's fields and methods on OverlayController.</summary>
    private static readonly string[] AppTestFields = ["TestShrinkPx", "TestFreezeTicks", "TestNotchTheme", "TestPillTheme", "TestApply", "TestSetCaptureHidden", "FitProbe"];

    /// <summary>The update scenarios' second way in, and the files of its fake feed.</summary>
    private static readonly string[] AppUpdateMarkers = ["e2e.json", "latest.json", "asset-"];

    private static (HashSet<string> Types, List<string> Strings) Read(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in md.TypeDefinitions) types.Add(md.GetString(md.GetTypeDefinition(h).Name));
        foreach (var h in md.TypeReferences) types.Add(md.GetString(md.GetTypeReference(h).Name));
        var strings = new List<string>();
        var handle = MetadataTokens.UserStringHandle(1);
        while (!handle.IsNil)
        {
            strings.Add(md.GetUserString(handle));
            handle = md.GetNextHandle(handle);
        }
        return (types, strings);
    }

    /// <summary>Field and method names defined in the assembly (test switches that are members, not types or strings).</summary>
    private static HashSet<string> ReadMembers(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in md.FieldDefinitions) names.Add(md.GetString(md.GetFieldDefinition(h).Name));
        // And methods: test entry points on product types (TestApply, TestSetCaptureHidden) are methods, not fields.
        foreach (var h in md.MethodDefinitions) names.Add(md.GetString(md.GetMethodDefinition(h).Name));
        return names;
    }

    private static string Dir(string variable)
    {
        var dir = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(dir)) Assert.Skip($"set {variable} to a built output folder");
        return dir!;
    }

    [Fact]
    public void ReleaseBuild_HasNoTestTypesOrSwitches()
    {
        var dir = Dir("YOLIME_RELEASE_DLL_DIR");
        var app = Read(Path.Combine(dir, "Yolime.App.dll"));
        Assert.DoesNotContain(AppTestTypes, app.Types.Contains);
        Assert.DoesNotContain(app.Strings, s => s.Contains("--test-", StringComparison.Ordinal) || s.Contains("--e2e", StringComparison.Ordinal) || s.Contains("WDAGUtilityAccount", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Strings, s => AppHookFiles.Contains(s));
        Assert.DoesNotContain(app.Strings, s => AppUpdateMarkers.Any(m => s.Contains(m, StringComparison.Ordinal)));
        Assert.DoesNotContain(AppTestFields, ReadMembers(Path.Combine(dir, "Yolime.App.dll")).Contains);
        var core = Read(Path.Combine(dir, "Yolime.Core.dll"));
        Assert.DoesNotContain(CoreTestTypes, core.Types.Contains);
    }

    [Fact]
    public void E2EBuild_ContainsThem_SoTheCheckCanFail()
    {
        var dir = Dir("YOLIME_E2E_DLL_DIR");
        var app = Read(Path.Combine(dir, "Yolime.App.dll"));
        Assert.Contains("TestAudioRunner", app.Types);
        Assert.Contains(app.Strings, s => s == "--test-audio");
        Assert.Contains(app.Strings, s => s == "--e2e");
        Assert.Contains("E2EMode", app.Types);
        Assert.All(AppHookFiles, f => Assert.Contains(f, app.Strings));
        Assert.All(AppUpdateMarkers, m => Assert.Contains(app.Strings, s => s.Contains(m, StringComparison.Ordinal)));
        Assert.Contains("FileFeedHandler", app.Types);
        var members = ReadMembers(Path.Combine(dir, "Yolime.App.dll"));
        Assert.All(AppTestFields, f => Assert.Contains(f, members));
        Assert.Contains("TourStates", app.Types);
        Assert.Contains("FakeTranscriptionSession", Read(Path.Combine(dir, "Yolime.Core.dll")).Types);
    }
}
