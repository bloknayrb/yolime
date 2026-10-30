using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Logging;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingLog _log = new();

    public void Dispose() => _root.Dispose();

    private SettingsStore Store(Action<string>? beforeReplace = null) => new(_root.Paths, _log, _time, beforeReplace);

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(_root.Paths.RoamingDir);
        File.WriteAllText(_root.Paths.SettingsFile, json);
    }

    private string[] Backups() => Directory.GetFiles(_root.Paths.RoamingDir, "settings.json.bad-*");

    [Fact]
    public void Load_Missing_ReturnsDefaults_StatusMissing_NoFileCreated()
    {
        var result = Store().Load();
        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
        Assert.Equal(new YolimeSettings().SttProvider, result.Settings.SttProvider);
        Assert.False(File.Exists(_root.Paths.SettingsFile));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_EmojiVocabulary()
    {
        var store = Store();
        var saved = new YolimeSettings { SttProvider = SttProviderKind.Groq, Vocabulary = ["Zoë", "yolime 🍋", "Kubernetes"] };
        Assert.Equal(SaveResult.Saved, store.Save(saved));

        var loaded = store.Load();
        Assert.Equal(SettingsLoadStatus.Ok, loaded.Status);
        Assert.Equal(SttProviderKind.Groq, loaded.Settings.SttProvider);
        Assert.Equal(saved.Vocabulary, loaded.Settings.Vocabulary);
        Assert.Contains("\"sttProvider\": \"Groq\"", File.ReadAllText(_root.Paths.SettingsFile));
    }

    [Fact]
    public void FirstSave_UsesMove_LaterSaves_UseReplace()
    {
        var store = Store();
        Assert.Equal(SaveResult.Saved, store.Save(new YolimeSettings()));
        Assert.True(File.Exists(_root.Paths.SettingsFile));
        Assert.Equal(SaveResult.Saved, store.Save(new YolimeSettings { HistoryRetentionDays = 10 }));
        Assert.True(File.Exists(_root.Paths.SettingsFile));
        Assert.Empty(Directory.GetFiles(_root.Paths.RoamingDir, "*.tmp"));
        Assert.Equal(10, store.Load().Settings.HistoryRetentionDays);
    }

    [Fact]
    public void Save_FaultBeforeReplace_LeavesOriginalByteIdentical_ReturnsFailed()
    {
        Store().Save(new YolimeSettings { HistoryRetentionDays = 12 });
        var before = File.ReadAllBytes(_root.Paths.SettingsFile);

        var faulty = Store(_ => throw new IOException("disk full"));
        Assert.Equal(SaveResult.Failed, faulty.Save(new YolimeSettings { HistoryRetentionDays = 99 }));

        Assert.Equal(before, File.ReadAllBytes(_root.Paths.SettingsFile));
        Assert.Empty(Directory.GetFiles(_root.Paths.RoamingDir, "*.tmp"));
    }

    [Fact]
    public void Load_InvalidJson_BacksUp_StatusCorrupt_BackupPathSet()
    {
        WriteRaw("{ \"sttProvider\": ");
        var result = Store().Load();

        Assert.Equal(SettingsLoadStatus.CorruptBackedUp, result.Status);
        Assert.NotNull(result.BackupPath);
        Assert.Equal("{ \"sttProvider\": ", File.ReadAllText(result.BackupPath!));
        Assert.Contains(LogEvent.SettingsCorruptBackedUp, _log.Events);
    }

    [Fact]
    public void TwoCorruptLoadsSameSecond_DoNotCollide()
    {
        WriteRaw("not json 1");
        var first = Store().Load();
        WriteRaw("not json 2");
        var second = Store().Load();

        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal(2, Backups().Length);
    }

    [Fact]
    public void Backups_KeepNewest5()
    {
        for (var i = 0; i < 7; i++)
        {
            WriteRaw($"broken {i}");
            Store().Load();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var kept = Backups().Select(File.ReadAllText).OrderBy(s => s).ToArray();
        Assert.Equal(["broken 2", "broken 3", "broken 4", "broken 5", "broken 6"], kept);
    }

    [Fact]
    public void Load_BadEnumValue_ResetsThatFieldOnly_ReportsName()
    {
        WriteRaw("""{ "schemaVersion": 1, "sttProvider": "Groqq", "vocabulary": ["Zoë"] }""");
        var result = Store().Load();

        Assert.Equal(SettingsLoadStatus.Ok, result.Status);
        Assert.Equal(new YolimeSettings().SttProvider, result.Settings.SttProvider);
        Assert.Equal(["Zoë"], result.Settings.Vocabulary);
        Assert.Equal(["sttProvider"], result.InvalidFields);
    }

    [Fact]
    public void Load_DuplicateKey_IsCorrupt_NotACrash()
    {
        WriteRaw("""{ "silenceRms": 0.1, "silenceRms": 0.2 }""");
        Assert.Equal(SettingsLoadStatus.CorruptBackedUp, Store().Load().Status);
    }

    [Theory]
    [InlineData("""{ "openAiSttModel": null }""", "openAiSttModel")]
    [InlineData("""{ "openAiSttModel": "  " }""", "openAiSttModel")]
    [InlineData("""{ "vocabulary": null }""", "vocabulary")]
    [InlineData("""{ "vocabulary": [null, "x"] }""", "vocabulary")]
    [InlineData("""{ "sttProvider": 7 }""", "sttProvider")]
    [InlineData("""{ "holdKeyVk": 27 }""", "holdKeyVk")]
    [InlineData("""{ "holdKeyVk": 17 }""", "holdKeyVk")]
    [InlineData("""{ "startingMode": 9 }""", "startingMode")]
    [InlineData("""{ "promptModel": "claude-sonet-5-5" }""", "promptModel")]
    [InlineData("""{ "promptModel": null }""", "promptModel")]
    [InlineData("""{ "appStartingModes": null }""", "appStartingModes")]
    [InlineData("""{ "overlayPosition": 9 }""", "overlayPosition")]
    [InlineData("""{ "previewModes": null }""", "previewModes")]
    public void Load_NullBlankOrUndefinedValue_ResetsThatFieldOnly(string json, string field)
    {
        WriteRaw(json);
        var result = Store().Load();
        Assert.Equal(SettingsLoadStatus.Ok, result.Status);
        Assert.Equal([field], result.InvalidFields);
        var defaults = new YolimeSettings();
        Assert.Equal(defaults.OpenAiSttModel, result.Settings.OpenAiSttModel);
        Assert.Equal(defaults.SttProvider, result.Settings.SttProvider);
        Assert.NotNull(result.Settings.Vocabulary);
        Assert.DoesNotContain(null, result.Settings.Vocabulary);
    }

    [Fact]
    public void Load_OutOfRangeNumber_ResetsThatFieldOnly()
    {
        WriteRaw("""{ "schemaVersion": 1, "historyRetentionDays": -1, "silenceRms": 0.004 }""");
        var result = Store().Load();

        Assert.Equal(30, result.Settings.HistoryRetentionDays);
        Assert.Equal(0.004, result.Settings.SilenceRms, 6);
        Assert.Equal(["historyRetentionDays"], result.InvalidFields);
    }

    [Fact]
    public void Load_NewerSchema_StatusNewerReadOnly_SaveReturnsReadOnly_FileUnchanged()
    {
        WriteRaw("""{ "schemaVersion": 99, "historyRetentionDays": 9 }""");
        var store = Store();
        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.NewerReadOnly, result.Status);
        Assert.Equal(9, result.Settings.HistoryRetentionDays);
        var before = File.ReadAllBytes(_root.Paths.SettingsFile);
        Assert.Equal(SaveResult.ReadOnly, store.Save(result.Settings));
        Assert.Equal(before, File.ReadAllBytes(_root.Paths.SettingsFile));
    }

    [Fact]
    public void Load_OlderSchema_Migrates()
    {
        WriteRaw("""{ "schemaVersion": 1, "retention": 14 }""");
        var steps = new Dictionary<int, Action<JsonObject>>
        {
            [1] = doc =>
            {
                doc["historyRetentionDays"] = doc["retention"]?.DeepClone();
                doc.Remove("retention");
            },
        };
        var store = new SettingsStore(_root.Paths, _log, _time, null, currentVersion: 2, steps);
        var result = store.Load();

        Assert.Equal(14, result.Settings.HistoryRetentionDays);
        Assert.Equal(2, result.Settings.SchemaVersion);
        Assert.Contains(LogEvent.SettingsMigrated, _log.Events);
    }

    [Fact]
    public void Overlay_Settings_Default_RoundTrip_AndParseLeniently()
    {
        var d = new YolimeSettings();
        Assert.Equal(OverlayPosition.TopCentre, d.OverlayPosition);
        Assert.True(d.HideOverlayFromCapture); // the notch shows transcripts and reply context
        Assert.Empty(d.PreviewModes);

        var store = Store();
        store.Save(d with { OverlayPosition = OverlayPosition.BottomCentre, HideOverlayFromCapture = false, PreviewModes = ["Prompt", "Nonsense"] });
        var loaded = store.Load().Settings;
        Assert.Equal(OverlayPosition.BottomCentre, loaded.OverlayPosition);
        Assert.False(loaded.HideOverlayFromCapture);
        Assert.True(loaded.PreviewsAlways(Mode.Prompt));
        Assert.False(loaded.PreviewsAlways(Mode.Dictate));
    }

    [Fact]
    public void Llm_Settings_RoundTrip_AndDefault()
    {
        var defaults = new YolimeSettings();
        Assert.Equal("claude-sonnet-5-5", defaults.PromptModel);
        Assert.Empty(defaults.AppStartingModes);
        Assert.True(defaults.ModeCycleCues);

        var store = Store();
        store.Save(defaults with { PromptModel = "claude-haiku-5-5", AppStartingModes = new() { ["slack"] = "Reply" }, ModeCycleCues = false });
        var loaded = store.Load();
        Assert.Equal(SettingsLoadStatus.Ok, loaded.Status);
        Assert.Equal("claude-haiku-5-5", loaded.Settings.PromptModel);
        Assert.Equal("Reply", loaded.Settings.AppStartingModes["slack"]);
        Assert.False(loaded.Settings.ModeCycleCues);
    }

    [Fact]
    public void UnknownFields_PreservedOnSave()
    {
        WriteRaw("""{ "schemaVersion": 1, "futureThing": { "a": 1 } }""");
        var store = Store();
        store.Save(store.Load().Settings with { HistoryRetentionDays = 5 });

        var doc = JsonNode.Parse(File.ReadAllText(_root.Paths.SettingsFile))!.AsObject();
        Assert.Equal(1, (int)doc["futureThing"]!["a"]!);
        Assert.Equal(5, (int)doc["historyRetentionDays"]!);
    }
}
