# PR1 `feat/core-pipeline` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a tested `Yolime.Core` library that takes audio through pending-WAV safety, cloud STT, an optional rewrite and an output sink. It includes versioned settings, history and prompts, a logger that cannot hold content, and a per-run timing log. Add a minimal `Yolime.App` that runs that path end to end from a WAV file (`--test-audio`, E2E builds only).

**Architecture:**
- All decisions live in `Yolime.Core` (net10.0, no UI). They sit behind interfaces that PR2–PR7 plug real implementations into: the mic, the rewriter, ClipboardGuard and injection, and the overlay.
- The pipeline is streaming-shaped. Audio chunks are written to the pending WAV and pushed to the STT session as they arrive.
- Some facts are known only when capture ends: the mode after Shift-cycling, the reply context, and whether a preview was requested. They arrive through a `Task<RunIntent>` that the pipeline awaits when the audio ends.
- Progress goes out through `IProgress<PipelineEvent>` for the overlay.
- `Yolime.App` is a WinUI 3 shell with no window yet. It proves the real csproj and hosts `--test-audio`.

**Tech Stack:** .NET 10 (SDK 10.0.403), C#, xUnit v3 on Microsoft.Testing.Platform, Microsoft.Data.Sqlite, System.Text.Json (source-generated), `HttpClient` + `SocketsHttpHandler`, WinUI 3 / Windows App SDK 2.6.4 (App only).

**Spec:** `docs/plan.md`, specifically: PRs in order (item 1), Layout, Behaviour (No lost work, STT, Output, History, Logging, Build hygiene) and Verification.

**Review history:** Four adversarial reviews ran on the first draft: platform/.NET, security/privacy, product/architecture and UI/UX. Their findings are folded in below. "Calls I made" records where the reviewers disagreed and what I chose.

## Global Constraints

- **Target frameworks.**
  - Core: `net10.0`, with no Windows-only APIs.
  - App: `net10.0-windows10.0.19041.0`, with `UseWinUI`, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `SelfContained=true`, `RuntimeIdentifier=win-x64` and `Platforms=x64`.
- **Package pins.**
  - Exact pins live in `Directory.Packages.props`, and projects use version-less `PackageReference`. Restore comes from nuget.org only.
  - Each project commits its `packages.lock.json`, and `RestoreLockedMode` is on.
  - Pins: `Microsoft.Data.Sqlite` 10.0.14, `xunit.v3` 4.0.2, `Microsoft.WindowsAppSDK` 2.6.4, `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705. The platform reviewer restore-verified each one.
  - Microsoft.Data.Sqlite brings SQLitePCLRaw 2.1.13. Don't take a lone SQLitePCLRaw 3.x Dependabot bump.
- **Logs.**
  - Logs and timing files hold only state transitions, timings, counts, HTTP status codes and enums.
  - They never hold content, headers, key codes, URLs, process names or exception messages.
  - A sentinel test asserts this, and a reflection guard backs it up.
- **Test switches.**
  - These compile only under `#if YOLIME_E2E`: `--test-audio`, `--test-out`, `--test-data-dir`, `--test-transcript`, the App's use of `FileAudioSource`, and `FakeTranscriptionSession`.
  - The build refuses `YolimeE2E` when it comes from an environment variable, and refuses it on Release or publish builds.
  - `YOLIME_LIVE` is read only by test projects.
- **Pending WAVs.**
  - They live in `pending\`.
  - Deleted after STT succeeds, and on cancel-before-STT or discard.
  - Kept on STT failure, on capture failure with audio, on NoSpeech and on a crash.
  - Pruned at 7 days, and in the same pass the history row is marked `AudioExpired`.
- **History.**
  - SQLite, versioned with `PRAGMA user_version`.
  - It stores the process name, never window titles. Retention is 30 days by default, and audio is never stored in it.
  - `secure_delete` is ON. Clear also checkpoints, vacuums and deletes the pending WAVs.
  - **A history failure never blocks delivery.**
- **Settings.** Settings carry `schemaVersion`, migrations and field-by-field validation. API keys are never stored in settings.
- **STT.**
  - Multipart `/v1/audio/transcriptions`, with the vocabulary sent as `prompt`.
  - Each key is bound to its provider's fixed host. A custom base URL needs its own key and must be https with no userinfo, query or fragment.
  - Redirects are never followed.
  - The key is set on each request, never on `DefaultRequestHeaders`.
- **Audio.** 16 kHz mono s16le PCM everywhere in Core.
- **Files.** Line endings are LF, and every file ends with a newline.
- **No real desktop.** Nothing in this PR sends keys or clicks, changes the foreground window, or writes the real clipboard during automated verification.

## Calls I made (no real trade-off, or reviewers disagreed)

1. **The App skeleton lands in PR1.** The spec puts `--test-audio` (clipboard output) in PR1, and PR2 needs the App anyway. Doing it now retires the riskiest build setup early.
2. **Tests run on Microsoft.Testing.Platform**, via `global.json` `"test": {"runner": "Microsoft.Testing.Platform"}`. xunit.v3 4.x cannot run under VSTest-mode on the .NET 10 SDK (the platform reviewer verified this), and MTP is the SDK's direction. `--filter "FullyQualifiedName~X"` still works. A filter that matches zero tests exits with code 8.
3. **`YOLIME_E2E` is an MSBuild property:** `dotnet build -p:YolimeE2E=true`. It builds to `bin\e2e\` and `obj\e2e\`, so a test build can't be mistaken for a normal one.
4. **Core writes the 44-byte WAV header itself.** That keeps NAudio out of Core.
5. **The logger's API cannot take a string,** and a reflection test keeps it that way.
6. **The history row is inserted when capture starts**, as `InProgress`, with the initial mode and the WAV file name. The UI/UX reviewer asked for this to support crash recovery and Retry. The product reviewer had deferred it. A row that exists from the start means three things:
   - a crash leaves a recoverable `Interrupted` row;
   - Retry updates the same row;
   - the row exists before injection by construction.

   Discard paths delete the row: a short hold (PR2), cancel before STT, and a password refusal at delivery.
7. **`RetryAsync` lands in PR1.** It is Core logic, cheap once the row exists, and the reviewers agreed it's needed. Re-running as another mode from the stored transcript waits for PR6. The internal split (capture+STT, then rewrite+output) happens now, so PR6's entry point stays trivial.
8. **The pipeline doesn't time output.** The sink owns its own timing: injection mechanics, the 10 s pending paste, and the unbounded preview wait. `DeliveryResult` reports what happened. The UI and product reviewers both flagged that a 5 s pipeline timeout would break preview and pending paste.
9. **The silence gate counts voiced chunks.** A run counts as speech when at least 3 chunks of 100 ms have an RMS of at least `SilenceRms`, which is a setting (default 0.01). NoSpeech **keeps** the WAV and row, so "Send anyway" and Retry stay possible. This replaces the whole-clip mean, which dilutes a short utterance inside a long hold.
10. **The STT timeout scales with the audio:** 15 s + 0.3 s per second of audio. That gives 16 s for a 3 s clip and 105 s for 5 minutes. Connect timeout is 10 s.
11. **Fallbacks while there's no rewriter (PR1/PR2).**
    - Dictate delivers the raw transcript with intent `Paste`.
    - Prompt and Reply deliver the raw transcript with intent `ClipboardOnly`, outcome `CompletedWithFallback` and `RewriteError=Unavailable`.

    So PR2's first holdable build already works in Dictate. PR3 swaps in the real rewriter and the full fallback rules.
12. **Decision C is settled now:** an E2E-only `FakeTranscriptionSession` behind `--test-transcript <text>`. PR2's SendInput E2E can then run in the Sandbox with networking off and no key mapped in.
13. **CI is on (`windows-latest`).** It stays inside the private repo's free Actions minutes, and the default $0 spending limit means it can't cost money. It's the only gate that catches lock-file drift or a broken E2E build.
14. **`--test-audio` writes to a file during automated runs.** The real clipboard path is first exercised in PR2's Sandbox E2E, with ClipboardGuard and `ClipboardRedirection=Disable`. There is no host clipboard run. A fake-sink unit test does *not* exercise `Win32Clipboard`, and the plan doesn't claim it does.
15. **Data locations.**
    - `%APPDATA%\Yolime\`: `settings.json` and `prompts\`.
    - `%LOCALAPPDATA%\Yolime\`: `history.db`, `pending\` and `logs\`, which holds both `yolime-*.log` and `timings-*.jsonl`.

    Every class takes `AppPaths`.
16. **Small decisions.**
    - `global.json` pins 10.0.403 with `rollForward: latestFeature`.
    - `.editorconfig` gets `insert_final_newline = true`.
    - Backups (`settings.json.bad-*` and `prompts\*.bak-*`) keep the newest 5.
    - The test-only prompt resource lives in the test assembly.

## Notes for later PRs (seams named here, built later)

- **PR2.**
  - `WasapiSource` feeds the `IAsyncEnumerable` through a `Channel`, so the audio callback never awaits disk.
  - The `--test-audio` branch runs before the single-instance check.
  - The overlay's 2 s "No audio" hint uses the `CaptureLevel` events.
  - The hold key acts as cancel only when `CurrentRun.Stage` is past Capture.
- **PR3.** The rewriter reports failures by throwing `RewriteException(RewriteFailure)` after any partial stream. The failure kinds are Refusal, MaxTokens, Auth, RateLimited, Timeout, Network, Server and Unavailable. Keep the shared client free of default auth headers.
- **PR6.** `RunFromTranscriptAsync(historyId, Mode)` for Re-run, and the history window.
- **PR7.**
  - Raise `PooledConnectionIdleTimeout`, or keep connections warm. The `NewConnection` timing flag proves whether it works.
  - Consider uploading FLAC.
- **PR9.**
  - Exclude yolime from WER minidumps (`WerAddExcludedApplication`).
  - Re-run the release-absence test on the `dotnet publish` output.

## Review Focus

1. **Crash mid-recording or mid-STT.** At the next start the WAV and an `Interrupted` row are both present. `RetryAsync` on that row transcribes the WAV and updates the same row (Tasks 7, 8).
2. **A quiet speaker, or a short utterance inside a long latched hold.** It isn't dropped as NoSpeech. When NoSpeech does fire, the audio is still retryable (Tasks 4, 8).
3. **A hand-edited `settings.json`.**
   - One bad field resets only that field, and the field is reported.
   - Invalid JSON is backed up, the defaults are used, and that's reported.
   - A newer schema is read-only, and `Save` says so (Task 3).
4. **Holding the key during a run.** It returns `Busy`, along with the current run's mode, stage and start time. No row or timing is written, and the first run is unaffected (Task 8).
5. **Network drop, timeout, 401, quota exhaustion or a redirect during STT.** Each gets a distinct `SttError`. The WAV and row are kept, and nothing from the request's content reaches any log or exception string (Tasks 6, 8).

---

### Task 1: Solution, build props, CI

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Yolime.slnx`, `src/Yolime.Core/Yolime.Core.csproj`, `src/Yolime.Core/BuildFlags.cs`, `tests/Yolime.Core.Tests/Yolime.Core.Tests.csproj`, `tests/Yolime.Core.Tests/BuildSanityTests.cs`, `.github/workflows/ci.yml`, and the `packages.lock.json` files
- Modify: `Directory.Packages.props`, `.editorconfig`, `.gitignore` (add `*.db`, `*.db-wal`, `*.db-shm` and `settings.json*` as a backstop), `CLAUDE.md` (Commands)

**Interfaces:**
- Produces: `Yolime.Core.BuildFlags.IsE2E` (a `const bool`); `InternalsVisibleTo("Yolime.Core.Tests")`; the `YOLIME_E2E` define under `-p:YolimeE2E=true`.

- [ ] **Step 1:** Write `Directory.Build.props`:
  - `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `RestorePackagesWithLockFile=true`.
  - `RestoreLockedMode=true` unless `'$(YolimeUnlock)'=='true'`.
  - When `'$(YolimeE2E)'=='true'`: append `YOLIME_E2E` to `DefineConstants`, and set `BaseOutputPath=bin\e2e\` and `BaseIntermediateOutputPath=obj\e2e\`.
  - An `<Error>` when `$([System.Environment]::GetEnvironmentVariable('YolimeE2E'))` or `('YolimeUnlock')` is non-empty.
  - An `<Error>` when `YolimeE2E` is true and `Configuration` is `Release` or `_IsPublishing` is `true`.
- [ ] **Step 2:** Write `global.json`, with the SDK pin and the MTP runner. Create two projects:
  - Core: a csproj targeting `net10.0`.
  - Tests: a csproj targeting `net10.0`, with `xunit.v3`, `<Using Include="Xunit" />` and a reference to Core.

  Then run `dotnet new sln --format slnx -n Yolime` and `dotnet sln add` for both projects.
- [ ] **Step 3:** Write two tests: `BuildSanityTests.E2EDefine_IsOffInNormalBuilds` inside `#if !YOLIME_E2E`, and `E2EDefine_IsOnInE2EBuilds` inside `#if YOLIME_E2E`. Run `dotnet test`. Expected: compile failure, because `BuildFlags` is missing. Add `BuildFlags` and run it again. Expected: PASS.
- [ ] **Step 4:** Run `dotnet restore` to generate the lock files, then **commit**: `build: add solution, central build props and locked restore`.
- [ ] **Step 5:** Verify, after the commit:
  - `rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj`, then `dotnet restore --locked-mode && dotnet test`. Expected: PASS.
  - `dotnet test -p:YolimeE2E=true`. Expected: PASS, running `E2EDefine_IsOnInE2EBuilds`.
  - `YolimeE2E=true dotnet build`, with the value set in the environment. Expected: fails with the props error.
  - `dotnet build -c Release -p:YolimeE2E=true`. Expected: fails.
  - Add a package reference without unlocking. Expected: restore fails with NU1004. Revert it.
- [ ] **Step 6:** Write `ci.yml`:
  - Triggers: `pull_request`, and `push` to `main`.
  - Settings: `permissions: contents: read`, `timeout-minutes: 20`.
  - Actions, each SHA-pinned: `actions/checkout` with `persist-credentials: false`, and `actions/setup-dotnet` with `global-json-file`.
  - Steps, in order:
    - `dotnet restore --locked-mode`;
    - `git status --porcelain -- '**/packages.lock.json'` must print nothing, because missing lock files silently pass a locked restore (verified by the platform reviewer);
    - `dotnet build -c Release --no-restore`;
    - `dotnet test -c Release --no-build`;
    - `dotnet build -p:YolimeE2E=true`;
    - `dotnet test -p:YolimeE2E=true`.
- [ ] **Step 7:** Update the CLAUDE.md Commands section:
  - the solution now exists;
  - the E2E build;
  - adding a package: pin it, run `dotnet restore -p:YolimeUnlock=true`, then commit the lock files;
  - a filter that matches nothing exits with code 8;
  - never set `YolimeE2E` or `YolimeUnlock` as environment variables.
- [ ] **Step 8:** Commit: `ci: build and test on windows with locked restore`.

### Task 2: Content-free logger and timing log

**Files:**
- Create: `src/Yolime.Core/{AppPaths.cs, Mode.cs}`, `src/Yolime.Core/Logging/{LogEvent.cs, LogField.cs, ILog.cs, FileLog.cs, RunTiming.cs, ITimingLog.cs, JsonlTimingLog.cs}`
- Test: `tests/Yolime.Core.Tests/Logging/{FileLogTests.cs, JsonlTimingLogTests.cs, NoStringFieldsTests.cs}`

**Interfaces:**
- Produces:
  - `enum Mode { Dictate, Prompt, Reply, Raw }`
  - `enum Stage { Capture, Stt, Rewrite, Output }`
  - `enum StageStatus { Ok, Skipped, Failed, TimedOut, Cancelled, FellBack }`
  - `enum RunOutcome { InProgress, Completed, CompletedWithFallback, Cancelled, Discarded, NoSpeech, Busy, CaptureFailed, SttFailed, RewriteFailed, OutputFailed, Interrupted }`
  - `sealed record AppPaths(string RoamingDir, string LocalDir)`, with `SettingsFile`, `PromptsDir`, `HistoryDb`, `PendingDir`, `LogsDir` and `static AppPaths Default()`.
  - `enum LogEvent`. Each task adds the members it needs; it starts with `AppStarted, RunStarted, StageCompleted, StageFailed, RunFinished, RunRejectedBusy, UnhandledException`.
  - `readonly struct LogField`, built only through these static factories:
    - `Count(string key, long n)`;
    - `Ms(string key, long ms)`;
    - `HttpStatus(int code)`;
    - `Tag<T>(string key, T value) where T : struct, Enum`;
    - `Error(Exception ex)`, which keeps only `ex.GetType().Name` and `ex.HResult`.

    The key must match `^[a-z][a-zA-Z0-9]{0,31}$`. Anything else throws `ArgumentException` with the fixed message `"invalid log key"`, which never includes the input.
  - `interface ILog { void Write(LogEvent evt, params ReadOnlySpan<LogField> fields); }`
  - `FileLog(AppPaths, TimeProvider) : ILog`. It writes `logs\yolime-yyyyMMdd.log`, rotates by **the date in the filename** (7 days), and is thread-safe.
  - `sealed record RunTiming`, with these fields:
    - `Guid RunId`, `Mode Mode`, `DateTimeOffset StartedUtc`, `RunOutcome Outcome`.
    - `long CaptureMs`, the hold length.
    - `long AudioBytes`.
    - `SttProviderKind Provider`, an enum rather than a URL.
    - `bool NewConnection`.
    - Milestones, as `long?` offsets from the end of the audio: `SttBodySentMs`, `SttFirstByteMs`, `SttDoneMs`, `RewriteFirstTokenMs`, `RewriteDoneMs`, `OutputDoneMs`.
    - `IReadOnlyDictionary<Stage, StageStatus> Stages`.
    - `int TranscriptChars`, `int OutputChars`, `int OutputWords`.
  - `interface ITimingLog { void Append(RunTiming t); }`
  - `JsonlTimingLog(AppPaths, TimeProvider)`. It writes `logs\timings-yyyyMMdd.jsonl`, rotates at 30 days (PR7 needs the history), writes enums as strings, and is thread-safe.

- [ ] **Step 1:** Write the failing tests:
  - `Write_FormatsTypedFields`: a line ending in `StageCompleted stage=Stt ms=812 http=200`.
  - `Error_RecordsTypeAndHResultOnly`: `new HttpRequestException("SENTINEL_x")` gives `error=HttpRequestException hr=`, with no `SENTINEL`.
  - `Key_RejectsNonIdentifier_AndMessageOmitsInput`: `Count("SENTINEL key", 1)` throws, and `ex.ToString()` has no `SENTINEL`.
  - `Rotation_DeletesFilesNamedOlderThan7Days`, with a fake `TimeProvider` and date-named files.
  - `TimingLog_AppendsOneJsonObjectPerLine_RoundTrips`.
  - `TimingLog_ConcurrentAppends_ProduceWholeLines`, with 50 parallel appends.
  - `NoStringFieldsTests`: walk `LogField`, `RunTiming` and their member types by recursive reflection, and assert that no field or property is a `string` or an `object`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~Logging"`. Expected: compile failure.
- [ ] **Step 3:** Implement. `LogField` stores a key, a discriminator, a `long`, and the enum value without boxing plus its type handle. It has no public constructor.
- [ ] **Step 4:** Run the same filter. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(core): add content-free logger and timing log`.

### Task 3: Settings

**Files:**
- Create: `src/Yolime.Core/Settings/{YolimeSettings.cs, SettingsStore.cs, SettingsLoadResult.cs, SettingsMigrations.cs, SettingsJsonContext.cs}`
- Test: `tests/Yolime.Core.Tests/Settings/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `AppPaths`, `ILog`.
- Produces:
  - `enum SttProviderKind { OpenAi, Groq, Custom }`
  - `sealed record YolimeSettings`, with these fields and defaults:
    - `SchemaVersion = 1`
    - `SttProvider = OpenAi`
    - `OpenAiSttModel = "gpt-4o-transcribe"`
    - `GroqSttModel = "whisper-large-v3-turbo"`
    - `CustomSttBaseUrl`, `CustomSttModel`
    - `Vocabulary = []`
    - `HistoryRetentionDays = 30` (range 1–3650)
    - `SilenceRms = 0.01` (range 0.0001–0.5)
    - `[JsonExtensionData] Extra`
  - `enum SettingsLoadStatus { Ok, Missing, CorruptBackedUp, NewerReadOnly }`
  - `sealed record SettingsLoadResult(YolimeSettings Settings, SettingsLoadStatus Status, string? BackupPath, IReadOnlyList<string> InvalidFields)`. `InvalidFields` holds JSON property names only, never values.
  - `enum SaveResult { Saved, ReadOnly, Failed }`
  - `SettingsStore(AppPaths, ILog)`, plus an internal overload with `Action<string>? beforeReplace` as a fault-injection seam. It has `SettingsLoadResult Load()` and `SaveResult Save(YolimeSettings)`.
  - `const int CurrentSchemaVersion = 1`, and `SettingsMigrations.Migrate(JsonObject doc, int from)`, plus an internal overload that takes a step table.

- [ ] **Step 1:** Write the failing tests:
  - Missing file and round-trips:
    - `Load_Missing_ReturnsDefaults_StatusMissing_NoFileCreated`.
    - `SaveThenLoad_RoundTrips_EmojiVocabulary`.
    - `FirstSave_UsesMove_LaterSaves_UseReplace`: the file exists after each save, and no `.tmp` is left behind.
    - `Save_FaultBeforeReplace_LeavesOriginalByteIdentical_ReturnsFailed`.
  - Corrupt files and backups:
    - `Load_InvalidJson_BacksUp_StatusCorrupt_BackupPathSet`.
    - `TwoCorruptLoadsSameSecond_DoNotCollide`: the backups get a `-1`, `-2` suffix.
    - `Backups_KeepNewest5`.
  - Field validation:
    - `Load_BadEnumValue_ResetsThatFieldOnly_ReportsName`: `"sttProvider": "Groqq"` alongside a valid vocabulary keeps the vocabulary and reports `InvalidFields == ["sttProvider"]`.
    - `Load_OutOfRangeNumber_ResetsThatFieldOnly`.
  - Schema versions:
    - `Load_NewerSchema_StatusNewerReadOnly_SaveReturnsReadOnly_FileUnchanged`.
    - `Load_OlderSchema_Migrates`.
    - `UnknownFields_PreservedOnSave`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~Settings"`. Expected: compile failure.
- [ ] **Step 3:** Implement:
  - parse to a `JsonObject`, migrate it, then deserialise each property separately through the source-generated context, so a bad field falls back to its default;
  - validate the ranges;
  - write to `settings.json.tmp`, then `File.Move` it on the first save and `File.Replace` after that.
- [ ] **Step 4:** Run the same filter. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(core): add versioned settings with per-field validation`.

### Task 4: Audio, WAV files and the pending store

**Files:**
- Create: `src/Yolime.Core/Audio/{AudioFormat.cs, AudioChunk.cs, IAudioSource.cs, FileAudioSource.cs, WavFile.cs, Rms.cs, SpeechGate.cs, PendingAudioStore.cs, PendingRecording.cs, AudioCaptureException.cs}`, `tools/make-test-audio.ps1`, `tests/fixtures/audio/{speech-short.wav, speech-quiet.wav, speech-in-long-noise.wav, silence-2s.wav, noise-floor-5s.wav}`
- Test: `tests/Yolime.Core.Tests/Audio/{WavFileTests.cs, FileAudioSourceTests.cs, SpeechGateTests.cs, PendingAudioStoreTests.cs}`

**Interfaces:**
- Produces:
  - `AudioFormat`: `SampleRate = 16000`, `Channels = 1`, `BitsPerSample = 16`, `BytesPerMs = 32`.
  - `readonly record struct AudioChunk(ReadOnlyMemory<byte> Pcm, float Rms)`.
  - `interface IAudioSource { IAsyncEnumerable<AudioChunk> ReadAsync(CancellationToken ct); }`. A source signals a mic failure by throwing `AudioCaptureException(CaptureError)`, where `enum CaptureError { AccessDenied, NoDevice, DeviceLost }`.
  - `FileAudioSource(string wavPath, int chunkMs = 100)`, using `[EnumeratorCancellation]`.
  - `WavFile`:
    - `WriteHeader(Stream s, long dataBytes)`.
    - `byte[] ReadPcm(string path)`. It opens with `FileShare.ReadWrite | FileShare.Delete` and requires 16 kHz mono s16. If the data size is 0 or larger than the file, it uses `fileLength - 44`. Otherwise it throws `InvalidDataException`.
  - `Rms.Of(ReadOnlySpan<byte>) : float`.
  - `SpeechGate(float threshold, int minVoicedChunks = 3)`, with `void Add(AudioChunk)` and `bool HeardSpeech`.
  - `PendingAudioStore(AppPaths, ILog, TimeProvider)`:
    - `PendingRecording Begin(Guid runId)`;
    - `string PathOf(string fileName)`;
    - `IReadOnlyList<string> List()`;
    - `IReadOnlyList<string> PruneOlderThan(TimeSpan)`, which goes by file creation time and returns the pruned names;
    - `void Delete(string fileName)`, which is idempotent.
  - `PendingRecording : IAsyncDisposable`:
    - `string FileName`, which is `<runId>.wav`;
    - `AppendAsync(ReadOnlyMemory<byte>)`, which calls `Flush(false)` but doesn't fsync;
    - `FinaliseAsync()`, which rewrites the header sizes;
    - `long DataBytes`.

    The writer opens with `FileShare.Read | FileShare.Delete`. Delete disposes the stream first.

- [ ] **Step 1:** Write `tools/make-test-audio.ps1`. It runs under Windows PowerShell 5.1 using System.Speech, and writes WAV files directly without playing any sound. It produces:
  - `speech-short.wav`: "The quick brown fox jumps over the lazy dog."
  - `speech-quiet.wav`: the same phrase, with its samples scaled to a -40 dBFS peak.
  - `speech-in-long-noise.wav`: 30 s of noise at -60 dBFS, with the short phrase in the middle.
  - `silence-2s.wav`: digital zero.
  - `noise-floor-5s.wav`: noise at -60 dBFS.

  Run it, then commit the fixtures (about 1.3 MB in total).
- [ ] **Step 2:** Write the failing tests:
  - WAV files: `Header_RoundTrips`, `ReadPcm_ZeroSizedHeader_UsesFileLength`, `ReadPcm_WrongFormat_Throws`, `ReadPcm_WhileWriterOpen_Succeeds`.
  - File source: `FileAudioSource_YieldsChunksOf3200Bytes_LastMayBeShorter`, `FileAudioSource_HonoursCancellation`.
  - Speech gate: `Gate_Silence_And_NoiseFloor_NotSpeech`, `Gate_Speech_Quiet_And_InLongNoise_AreSpeech`.
  - Pending store:
    - `Pending_BeginAppendFinalise_Readable`.
    - `Pending_UnfinalisedAfterDispose_ListedAndReadable`.
    - `Prune_UsesCreationTime_ReturnsNames`, which backdates files with `File.SetCreationTimeUtc`.
    - `Delete_Idempotent`.
- [ ] **Step 3:** Run `dotnet test --filter "FullyQualifiedName~Audio"`. Expected: compile failure.
- [ ] **Step 4:** Implement, then run the same filter. Expected: PASS.
- [ ] **Step 5:** Commit: `feat(core): add audio io, speech gate and pending-audio store`.

### Task 5: Prompt library

**Files:**
- Create: `src/Yolime.Core/Prompts/{PromptLibrary.cs, PromptFile.cs}`
- Test: `tests/Yolime.Core.Tests/Prompts/PromptLibraryTests.cs`, and `tests/Yolime.Core.Tests/Prompts/Defaults/{sample.v1.md, sample.v2.md}`, embedded in the *test* assembly

**Interfaces:**
- Produces:
  - `sealed record PromptFile(string Id, int Version, string Body, bool IsUserOverride)`.
  - `PromptLibrary(AppPaths)` uses Core's own assembly, and an internal `PromptLibrary(AppPaths, Assembly resources)` takes another one. Resources are named `yolime.prompts.<id>.v<N>.md`.
  - `PromptFile Get(string id)`: the override at `prompts\<id>.md` wins over the embedded default.
  - `bool NewerDefaultExists(string id)`: true only when the override's version is at least 1 and lower than the highest embedded version. An override without a version line is treated as user-authored and never triggers the notice.
  - `void ResetToDefault(string id)`: renames the override to `<id>.md.bak-<yyyyMMddHHmmss>` and keeps the newest 5 backups.
  - File format: an optional first line, `<!-- version: N -->`.

- [ ] **Step 1:** Write the failing tests:
  - `Get_NoOverride_ReturnsHighestEmbedded`.
  - `Get_Override_Wins`.
  - `NewerDefaultExists_OverrideV1_DefaultV2_True`.
  - `NewerDefaultExists_UnversionedOverride_False`.
  - `Reset_RenamesNotDeletes_Keeps5`.
  - `Get_UnknownId_ThrowsKeyNotFound`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~Prompts"`. Expected: compile failure.
- [ ] **Step 3:** Implement, then run the same filter. Expected: PASS.
- [ ] **Step 4:** Commit: `feat(core): add versioned prompt library with user overrides`.

### Task 6: Batch STT

**Files:**
- Create: `src/Yolime.Core/Stt/{ITranscriptionSession.cs, ITranscriptionSessionFactory.cs, SttSessionOptions.cs, SttEndpoint.cs, BatchTranscriptionSession.cs, BatchTranscriptionSessionFactory.cs, SttException.cs, IApiKeySource.cs, EnvironmentApiKeySource.cs, FakeTranscriptionSession.cs}`, `src/Yolime.Core/Net/{HttpClients.cs, HostPinningHandler.cs}`
- Test: `tests/Yolime.Core.Tests/Stt/{SttEndpointTests.cs, BatchTranscriptionSessionTests.cs, HttpClientsTests.cs, LiveSttSmokeTests.cs}`

**Interfaces:**
- Consumes: `YolimeSettings`, `WavFile.WriteHeader`, `ILog`.
- Produces:
  - `sealed record SttSessionOptions(IReadOnlyList<string> Vocabulary, string? Language = null)`.
  - `interface ITranscriptionSession : IAsyncDisposable`, with `void PushAudio(ReadOnlyMemory<byte> pcm)` and `Task<SttResult> CompleteAsync(CancellationToken ct)`.
  - `sealed record SttResult(string Text, long BodySentMs, long FirstByteMs, bool NewConnection)`. Both times are measured from the start of `CompleteAsync`.
  - `interface ITranscriptionSessionFactory`, with `ITranscriptionSession Create(SttSessionOptions o)` and `SttReadiness CheckReady()`. `CheckReady` is the preflight the App calls at key-down, so it can show "Set up a key" before recording anything.
  - `enum SttReadiness { Ready, MissingKey, InsecureEndpoint, InvalidEndpoint }`.
  - `sealed record SttEndpoint(Uri BaseUrl, string Model, string ApiKey, SttProviderKind Kind)`, resolved by `static bool TryResolve(YolimeSettings, IApiKeySource, out SttEndpoint?, out SttReadiness)` or by `static SttEndpoint Resolve(...)`, which throws:
    - OpenAI: `https://api.openai.com/v1/`, with `OPENAI_API_KEY`.
    - Groq: `https://api.groq.com/openai/v1/`, with `GROQ_API_KEY`.
    - Custom: an absolute https URL with no userinfo, query or fragment, normalised to end in `/`, with `YOLIME_STT_API_KEY`.
    - No provider ever falls back to another provider's key.
  - `enum SttError { MissingKey, InsecureEndpoint, InvalidEndpoint, Auth, RateLimited, QuotaExceeded, BadRequest, Server, Redirected, MalformedResponse, Timeout, Network }`.
  - `SttException(SttError Error, int? HttpStatus)`. Its message is the enum name, and it has **no InnerException**.
  - `HttpClients.Shared`, one client:
    - `SocketsHttpHandler` with `AllowAutoRedirect=false`, `PooledConnectionLifetime=5 min`, `ConnectTimeout=10 s`, and a `ConnectCallback` that flags new connections;
    - `Timeout=Infinite`;
    - no `DefaultRequestHeaders.Authorization`, and no certificate callback.
  - `HostPinningHandler(DelegatingHandler)`: refuses any request whose host isn't the endpoint's host.
  - `BatchTranscriptionSession(HttpClient, SttEndpoint, SttSessionOptions, ILog)`:
    - POSTs multipart to `{BaseUrl}audio/transcriptions`, with:
      - `file` (`audio.wav`, `audio/wav`);
      - `model`;
      - `response_format=json`;
      - `prompt`: the vocabulary joined with ", ", included only when the vocabulary isn't empty.
    - Sets `Authorization` on the request message, and uses `HttpCompletionOption.ResponseHeadersRead`.
    - Wraps the multipart content so that `BodySentMs` is stamped when serialisation completes.
    - On a 429, reads only the JSON `error.code`, to tell `insufficient_quota` (QuotaExceeded) apart from RateLimited. The body is never logged or kept.
  - `FakeTranscriptionSession(string text)` returns the fixed text. Its factory exists only under `#if YOLIME_E2E`.

- [ ] **Step 1:** Write the failing tests, using a fake handler that captures the request:
  - Request shape: `Multipart_HasModelFileJsonFormat`, `Vocabulary_AsPrompt_OmittedWhenEmpty`, `Wav_HasHeaderAndAllBytes`.
  - Key binding: `OpenAiKey_OnlyToOpenAiHost`, `GroqKey_OnlyToGroqHost`, `Resolve_Groq_DoesNotUseOpenAiKey`, `Resolve_Custom_DoesNotUseOpenAiKey`.
  - Endpoint resolution: `Resolve_Custom_RejectsHttp_UserInfo_Query_Fragment_Relative`, `Resolve_Custom_NormalisesTrailingSlash_PostsToBasePlusPath`, `Resolve_MissingKey_TryResolveReportsMissingKey`.
  - Status mapping: `Status401_Auth`, `Status429_RateLimited`, `Status429InsufficientQuota_QuotaExceeded`, `Status500_Server`, `Status400_BadRequest`, `Status307_NotFollowed_Redirected`, `Status200MalformedJson_MalformedResponse`.
  - Transport failures: `HttpRequestException_Network`, `ConnectTimeout_Timeout`, `CallerTimeoutToken_Timeout_UserCancel_ThrowsOperationCanceled`.
  - `Exception_ToString_NeverContainsBodyOrUri`: the body is `SENTINEL_BODY` and the custom host is `sentinel-host.example`. Run it for both the 400 and the malformed-200 cases.
  - `HttpClientsTests`: `Shared_HasNoDefaultAuth_NoRedirects_NoCertCallback`, `HostPinning_RefusesForeignHost`.
  - `LiveSttSmokeTests.SpeechFixture_Transcribes`: skipped unless `YOLIME_LIVE=1`. It sends `speech-short.wav` to OpenAI and checks that the lowercased text contains "quick", "fox" and "lazy dog".
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~Stt"`. Expected: compile failure.
- [ ] **Step 3:** Implement. Log only `Tag(stage)`, `HttpStatus`, `Count("audioBytes")` and `Tag(sttError)`.
- [ ] **Step 4:** Run the same filter. Expected: PASS, with the live test skipped.
- [ ] **Step 5:** Run `$env:YOLIME_LIVE='1'; dotnet test --filter "FullyQualifiedName~LiveSttSmoke"`. Expected: PASS. The cost is under $0.01. This also confirms that `gpt-4o-transcribe` is still a current model ID.
- [ ] **Step 6:** Commit: `feat(core): add batch STT with host-pinned keys`.

### Task 7: History store

**Files:**
- Create: `src/Yolime.Core/History/{IHistoryStore.cs, HistoryStore.cs, HistoryRow.cs, HistorySchema.cs, HistoryUnavailableException.cs}`
- Test: `tests/Yolime.Core.Tests/History/HistoryStoreTests.cs`

**Interfaces:**
- Consumes: `AppPaths`, `ILog`, `TimeProvider`, `PendingAudioStore`, and the enums from Task 2.
- Produces:
  - `enum DeliveryKind { None, Pasted, Copied, PendingPaste, Refused, Discarded }`
  - `sealed record HistoryRow`, with these fields:
    - `long Id`, `Guid RunId`, `DateTimeOffset StartedUtc`, `Mode Mode`, `string? ProcessName`.
    - `string? RawText`, `string? Context`, `string? Output`.
    - `RunOutcome Outcome`, `DeliveryKind Delivery`.
    - `SttError? SttError`.
    - `string? RewriteError`: an enum name, stored as text so PR3 needs no migration.
    - `bool FallbackUsed`.
    - `string? PendingAudioFile`, `bool AudioExpired`.
    - `IReadOnlyDictionary<Stage, StageStatus> Stages`, `long? TotalMs`.
  - `interface IHistoryStore`:
    - `bool IsReadOnly`.
    - `long Insert(HistoryRow)`.
    - `void Update(long id, Func<HistoryRow, HistoryRow> change)`.
    - `void Delete(long id)`.
    - `HistoryRow? Get(long id)`.
    - `IReadOnlyList<HistoryRow> Recent(int limit)`.
    - `HistoryRow? LatestRetryable()`: the newest row with an `Outcome` of SttFailed, CaptureFailed, NoSpeech or Interrupted, whose audio hasn't expired.
    - `int MarkInterrupted()`: sets every `InProgress` row to `Interrupted`.
    - `void MarkAudioExpired(IEnumerable<string> fileNames)`.
    - `int PruneOlderThan(TimeSpan)`.
    - `void Clear()`.
  - Writes on a read-only store, and any SQLite failure such as a locked database or a full disk, throw `HistoryUnavailableException`.
  - Storage:
    - times are unix milliseconds, in INTEGER columns;
    - the stages go in a JSON text column;
    - there's an index on `started_ms`;
    - every connection runs `PRAGMA secure_delete=ON`;
    - `journal_mode=WAL` is set outside the migration transaction.
  - `Clear()` deletes every row and the pending WAVs those rows reference (through `PendingAudioStore`), then runs `wal_checkpoint(TRUNCATE)` and `VACUUM`.
  - When the database's `user_version` is newer than the code knows, `IsReadOnly = true` and `HistoryNewerSchema` is logged.

- [ ] **Step 1:** Write the failing tests. Each uses a temp-dir db with `Pooling=False`:
  - Schema and round-trips:
    - `NewDb_UserVersion1_WalOn_SecureDeleteOn`.
    - `InsertGet_RoundTrips_EmojiCjk20kChars`.
    - `Update_AppliesChange`.
    - `Delete_RemovesRow`.
  - Recovery and expiry:
    - `LatestRetryable_SkipsExpiredAndCompleted`.
    - `MarkInterrupted_ConvertsInProgressOnly`.
    - `MarkAudioExpired_ClearsRetryability`.
    - `Prune_RemovesOnlyOlderThanRetention`.
  - Clear:
    - `Clear_SentinelNotRecoverableFromDbWalOrShm`: insert `SENTINEL_HIST`, call `Clear()`, close the store, then byte-scan the database files.
    - `Clear_DeletesReferencedPendingWavs`.
  - Read-only mode and schema:
    - `NewerUserVersion_IsReadOnly_InsertThrowsHistoryUnavailable`.
    - `Schema_HasNoTitleWindowOrAudioBlobColumns`.
- [ ] **Step 2:** Run `dotnet test --filter "FullyQualifiedName~History"`. Expected: compile failure.
- [ ] **Step 3:** Implement, using parameterised commands only. Run the same filter. Expected: PASS.
- [ ] **Step 4:** Commit: `feat(core): add SQLite history with secure clear and recovery`.

### Task 8: Dictation pipeline

**Files:**
- Create: `src/Yolime.Core/Pipeline/{DictationPipeline.cs, RunRequest.cs, RunIntent.cs, RunResult.cs, PipelineEvent.cs, CurrentRun.cs, SttTimeout.cs}`, `src/Yolime.Core/Llm/{IRewriter.cs, RewriteRequest.cs, RewriteException.cs}`, `src/Yolime.Core/Output/{IOutputSink.cs, OutputItem.cs}`, `src/Yolime.Core/Startup/Housekeeping.cs`
- Test: `tests/Yolime.Core.Tests/Pipeline/{DictationPipelineTests.cs, RetryTests.cs, NoContentInLogsTests.cs}`, `tests/Yolime.Core.Tests/Startup/HousekeepingTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - Request types:
    - `sealed record RunIntent(Mode Mode, string? Context, bool PreviewRequested)`.
    - `sealed record RunRequest(IAudioSource Audio, Mode InitialMode, string? ProcessName, Task<RunIntent> Intent, IProgress<PipelineEvent>? Progress = null)`. `Intent` resolves when capture ends. The App completes it on key-up, and tests complete it from the source.
  - Progress and status types:
    - `abstract record PipelineEvent`, with these subtypes: `StageStarted(Stage)`, `StageFinished(Stage, StageStatus)`, `CaptureLevel(float Rms, long ElapsedMs)`, `TranscriptReady(string Text)`, `RewriteDelta(string Text)`.
    - `sealed record CurrentRun(Guid RunId, Mode Mode, Stage Stage, DateTimeOffset StartedUtc)`.
  - `sealed record RunResult`, with these fields:
    - `Guid RunId`, `RunOutcome Outcome`, `Mode Mode`.
    - `string? RawText`, `string? Output`, `DeliveryKind Delivery`.
    - `long? HistoryId`, `bool HistoryWritten`.
    - `SttError? SttError`, `CaptureError? CaptureError`, `RewriteFailure? RewriteError`.
    - `CurrentRun? BusyWith`, set only when the outcome is `Busy`.
    - `RunTiming Timing`.
  - Rewriter contract:
    - `enum RewriteFailure { Refusal, MaxTokens, Auth, RateLimited, Timeout, Network, Server, Unavailable }`, and `RewriteException(RewriteFailure)`.
    - `sealed record RewriteRequest(Mode Mode, string Transcript, string? Context)`.
    - `interface IRewriter { IAsyncEnumerable<string> StreamAsync(RewriteRequest, CancellationToken); }`. A failure throws `RewriteException`, possibly after a partial stream.
  - Output contract:
    - `enum DeliveryIntent { Paste, ClipboardOnly }`.
    - `sealed record OutputItem(string Text, DeliveryIntent Intent, bool PreviewRequested)`.
    - `interface IOutputSink { Task<DeliveryKind> DeliverAsync(OutputItem item, CancellationToken ct); }`. The sink owns all output timing.
  - `DictationPipeline(ITranscriptionSessionFactory, IRewriter?, IOutputSink, IHistoryStore, PendingAudioStore, ILog, ITimingLog, Func<YolimeSettings>, TimeProvider)`, with:
    - `Task<RunResult> RunAsync(RunRequest, CancellationToken)`.
    - `Task<RunResult> RetryAsync(long historyId, Mode? asMode, IProgress<PipelineEvent>?, CancellationToken)`.
    - `CurrentRun? Current`.
  - `SttTimeout.For(long audioBytes) : TimeSpan`: 15 s plus 0.3 s per second of audio.
  - `Housekeeping.Run(IHistoryStore, PendingAudioStore, YolimeSettings)`, called at startup. It:
    - calls `MarkInterrupted()`;
    - prunes pending audio at 7 days, then calls `MarkAudioExpired`;
    - prunes history at `HistoryRetentionDays`;
    - gives each orphan WAV that has no row an `Interrupted` row, with mode `Raw` and no process name.

**Run behaviour the tests pin:**
1. **Gate.** `Interlocked.CompareExchange` runs before the first await.
   - If another run is active, return `Busy` at once, with `BusyWith = Current`. Write no row and no timing, and log `RunRejectedBusy`.
   - Release the gate in `finally`.
2. **Capture start.** Call `pending.Begin`, then `history.Insert` with `InProgress`, `InitialMode`, `ProcessName` and `PendingAudioFile`. If the insert throws `HistoryUnavailableException`, set `HistoryWritten=false` and carry on.
3. **Capture.** For each chunk: append it to the WAV, call `stt.PushAudio`, feed the `SpeechGate`, and report `CaptureLevel`.
   - If the source throws `AudioCaptureException` with zero audio: the outcome is `CaptureFailed`, and the WAV and row are deleted.
   - If it throws after some audio: the outcome is `CaptureFailed`, and the WAV and row are kept for Retry.
   - After capture: `FinaliseAsync`, then await `Intent`.
4. **Cancel before STT finishes.** If `ct` is cancelled first, the outcome is `Cancelled` and the row and WAV are deleted.
5. **No speech.** If the gate didn't hear speech, the outcome is `NoSpeech` and STT isn't called. The WAV and row are kept, so the run is retryable.
6. **STT.** It runs under a linked token with `SttTimeout.For(bytes)`. The session was created at capture start, with the vocabulary.
   - On success: report `TranscriptReady`, update the row with `RawText`, delete the WAV and clear `PendingAudioFile`.
   - On failure: the outcome is `SttFailed`, the row records `SttError`, and the WAV is kept.
7. **Cancel after STT.** If `ct` is cancelled after STT, the outcome is `Cancelled`. The row keeps `RawText`, and the WAV has already been deleted.
8. **Rewrite.**
   - Raw skips it, with stage status `Skipped`.
   - With a rewriter: report each `RewriteDelta`, then concatenate the deltas.
   - With no rewriter, or on a `RewriteException`, fall back:
     - Dictate: `Output = RawText`, with intent `Paste`.
     - Prompt and Reply: `Output = RawText`, with intent `ClipboardOnly`.
     - The stage status is `FellBack`, `FallbackUsed` is true, the outcome is `CompletedWithFallback`, and `RewriteError` is set (`Unavailable` when there's no rewriter).
9. **Delivery.** First update the row with `Output`, so the history row exists before injection. Then call `DeliverAsync`:
   - `Refused`: delete the row (no history row for a password field).
   - `Discarded`: the outcome is `Discarded`, and the row is kept.
   - Exception: the outcome is `OutputFailed`, and the row keeps `Output`.
   - Otherwise: `Completed`, or `CompletedWithFallback`.
10. **Timing.** Append one `RunTiming` for every run except Busy. Milestones are measured from the end of the audio. `OutputWords` counts whitespace-separated tokens. That undercounts CJK text, which is known, and a comment in the code says so.
11. **`RetryAsync`.** Load the row; it must be retryable, and its audio must not have expired. Read its WAV through `FileAudioSource`. The `Intent` is already resolved, with mode `asMode ?? row.Mode` and the row's `Context`. Run steps 5–10 against **the same row and the same WAV**, without creating a new pending file.

- [ ] **Step 1:** Write the failing tests. Use fakes for STT, the rewriter, the sink and the clock, and a real temp-dir history and pending store:
  - Happy path: `Raw_HappyPath_EventOrder_RowBeforeDeliver_OutputBeforeDeliver_WavDeleted`. Assert the exact `PipelineEvent` sequence.
  - Speech gate:
    - `Silence_NoSpeech_NoSttCall_WavAndRowKept_Retryable`.
    - `QuietSpeechFixture_NotNoSpeech`: the real `speech-quiet.wav`, with a fake STT.
  - Busy: `SecondRunWhileBusy_BusyWithCurrent_NoRowNoTiming_FirstUnaffected`.
  - Capture failures:
    - `SourceThrowsAccessDenied_CaptureFailed_GateReleased_NothingKept`.
    - `DeviceLostMidRecording_CaptureFailed_PartialWavAndRowKept`.
  - Cancel:
    - `CancelDuringStt_RowAndWavDeleted`.
    - `CancelAfterStt_RowKeepsRaw_Cancelled`.
  - STT failures:
    - `SttFailure_Network_And_Auth_WavKept_RowHasErrorAndFile`.
    - `SttTimeout_ScalesWithAudio_IsTimeoutNotCancelled`.
  - Rewrite and fallback:
    - `Dictate_NoRewriter_PastesRaw_FellBack`.
    - `Prompt_NoRewriter_ClipboardOnlyRaw_FellBack`.
    - `Rewriter_StreamsDeltas_Concatenates`.
    - `RewriteExceptionAfterPartial_FallsBack_ErrorRecorded`.
  - Delivery:
    - `Delivery_Refused_DeletesRow`.
    - `Delivery_Discarded_Outcome`.
    - `DeliverThrows_OutputFailed_RowKeepsOutput`.
  - `HistoryUnavailable_StillDelivers_HistoryWrittenFalse`.
  - `Timing_MilestonesRelativeToAudioEnd_AndStageStatuses`.
- [ ] **Step 2:** Write `RetryTests`:
  - `Retry_AfterSttFailure_SameRowSameWav_DeletesWavOnSuccess`.
  - `Retry_AsOtherMode_UsesThatMode`.
  - `Retry_ExpiredAudio_ThrowsInvalidOperation`.

  Write `HousekeepingTests`:
  - `Startup_MarksInProgressInterrupted_AndRetryable`.
  - `Startup_OrphanWav_GetsInterruptedRow`.
  - `Startup_PruneMarksAudioExpired`.
- [ ] **Step 3:** Write `NoContentInLogsTests.Sentinels_NeverReachLogsOrTimings`. It wires a real `FileLog`, `JsonlTimingLog`, `BatchTranscriptionSession`, `HistoryStore` and `PendingAudioStore` to a temp root, with fake HTTP.
  - Plant a `SENTINEL`-prefixed value in each of these: the transcript, the API key, a vocabulary entry, the context, the process name, the rewriter output, an error body, the custom host, and a thrown `HttpRequestException` message.
  - Run these paths: happy, 401, 429 insufficient_quota, malformed 200, network exception, timeout, Busy, NoSpeech, cancel, and Retry.
  - Scan every file under the temp `LocalDir` and `RoamingDir`, except `history.db*` and `pending\`, and assert that the `SENTINEL` prefix never appears. Also assert the logs are non-empty.
  - Negative control: temporarily add a string-taking `LogField` factory, use it once in the pipeline, and confirm the test fails. Then revert.
- [ ] **Step 4:** Run `dotnet test --filter "FullyQualifiedName~Pipeline|FullyQualifiedName~Startup"`. Expected: compile failure.
- [ ] **Step 5:** Implement. Split the pipeline internally into `CaptureAndTranscribeAsync` and `RewriteAndDeliverAsync`; PR6's re-run calls the second one directly.
- [ ] **Step 6:** Run the full `dotnet test`. Expected: PASS.
- [ ] **Step 7:** Commit: `feat(core): add dictation pipeline with retry, recovery and timings`.

### Task 9: App skeleton and `--test-audio`

**Files:**
- Create: `src/Yolime.App/{Yolime.App.csproj, app.manifest, App.xaml, App.xaml.cs, Program.cs, Native/Win32Clipboard.cs, E2E/TestAudioRunner.cs, E2E/FileOutputSink.cs}`, `tests/Yolime.Core.Tests/Release/ReleaseHasNoTestCodeTests.cs`
- Modify: `Yolime.slnx`, `Directory.Packages.props`, `CLAUDE.md`

**Interfaces:**
- Consumes: everything in Core.
- Produces:
  - `Win32Clipboard : IOutputSink`:
    - calls `SetText`, retrying up to 5 times at 50 ms;
    - also sets `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory=0` (DWORD 0);
    - returns `Copied`;
    - is not exercised on the host in this PR.
  - Under `#if YOLIME_E2E` only:
    - `TestAudioRunner.RunAsync(string[] args) → int`. It takes these args:
      - `--test-audio <wav>`;
      - `[--test-out <file>]`;
      - `[--test-data-dir <dir>]`, which defaults to a fresh temp dir, never the real data;
      - `[--test-transcript <text>]`, which uses the `FakeTranscriptionSession`.
    - It runs `Housekeeping`, then a Raw run.
    - Exit codes: 0 Completed, 2 NoSpeech, 3 SttFailed, 4 OutputFailed, 5 CaptureFailed, and 1 for anything else.
    - `FileOutputSink` opens its file with `FileMode.CreateNew` and returns `Pasted`.
  - `Program.Main`:
    - installs `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` handlers, which log only `LogField.Error`;
    - under `#if YOLIME_E2E`, routes `--test-audio` to `TestAudioRunner` **before** XAML starts;
    - otherwise calls `Application.Start`, whose `OnLaunched` calls `Exit()` (PR2 adds the tray).
  - The csproj **appends** `DISABLE_XAML_GENERATED_MAIN` to `DefineConstants`.

- [ ] **Step 1:** Write the csproj according to the Global Constraints, add the WindowsAppSDK pins, and run `dotnet restore` to generate the App's lock file. Write `Program.cs`, `App.xaml(.cs)` and the manifest (PerMonitorV2).
- [ ] **Step 2:** Write `ReleaseHasNoTestCodeTests`. It is skipped unless the `YOLIME_RELEASE_DLL` env var points at a built `Yolime.App.dll`. It loads that DLL with `System.Reflection.Metadata` and asserts:
  - no TypeDef or TypeRef named `TestAudioRunner`, `FileOutputSink`, `FileAudioSource` or `FakeTranscriptionSession`;
  - no `--test-audio` in the #US heap, compared as UTF-16 strings via `GetUserString`.

  A second case runs against the E2E DLL and asserts that they *are* present, which proves the check can fail.
- [ ] **Step 3:** Run `dotnet build src/Yolime.App -c Release` and `dotnet build src/Yolime.App -p:YolimeE2E=true`. Expected: both succeed. Then run the release test against `bin\Release\…\Yolime.App.dll` (expect it absent) and against `bin\e2e\Debug\…\Yolime.App.dll` (expect it present). Expected: both pass.
- [ ] **Step 4:** Run the fake-STT path. Use bash, or PowerShell's `Start-Process -Wait -PassThru`, because PowerShell doesn't wait for a WinExe:
  `Yolime.App.exe --test-audio tests/fixtures/audio/speech-short.wav --test-transcript "hello sentinel" --test-out <scratch>/a.txt --test-data-dir <scratch>/d1`
  Expected:
  - exit code 0;
  - `a.txt` is `hello sentinel`;
  - `<scratch>/d1/.../logs` has a timings line and no `sentinel`;
  - the real `%LOCALAPPDATA%\Yolime` is untouched.
- [ ] **Step 5:** Run the real STT path: the same command without `--test-transcript`, using `--test-out <scratch>/b.txt --test-data-dir <scratch>/d2`. Expected: exit 0, and `b.txt` contains "quick" and "lazy dog". Then run it on `silence-2s.wav`. Expected: exit 2, and no output file.
- [ ] **Step 6:** Add to the CI E2E job: the release-absence test against both builds, and Step 4's fake-STT run.
- [ ] **Step 7:** Update the CLAUDE.md Commands section:
  - the build output paths: `dotnet build` of the solution puts the App under `bin\x64\…`;
  - the `--test-*` flags;
  - the note on WinExe exit codes.
- [ ] **Step 8:** Commit: `feat(app): add WinUI app skeleton with E2E-only --test-audio`.

### After merge: residual to verify, not assume

Watch whether Dependabot's NuGet updater rewrites `packages.lock.json` under central package management. Check its first PR after merge. If it doesn't, CI's lock-file check will fail that PR. Then note it in CLAUDE.md and regenerate the lock files on that branch.

---

## Execution rulings (after the consistency review of this revision)

A fifth reviewer checked this revision for internal consistency and found the gaps below. Each one is settled here, and these rulings override the task text above wherever they disagree.

**The log-field guard (H1).**
- `LogField.Key` is the single string that `NoStringFieldsTests` allows. Keys are validated identifiers.
- The test walks the declared instance fields of Yolime types, plus generic arguments, and stops at BCL types. A `Type` field is not walked into.

**The speech gate (H2).**
- The gate counts voiced *duration*, not chunks: speech means at least 300 ms of audio in chunks whose RMS is at or above `SilenceRms`. This way WASAPI chunk sizes in PR2 don't change the rule.
- `SilenceRms` defaults to 0.0025.
- Fixtures: `speech-quiet.wav` peaks at -30 dBFS (an RMS of roughly 0.006–0.01), and the noise fixtures are -60 dBFS *RMS* (0.001).
- Gate tests use the default threshold.

**`RetryAsync` (H3).**
- It takes the same single-run gate, and returns `Busy` while a run is active.
- It sets the row's outcome to `InProgress`, and writes `Mode = asMode ?? row.Mode` to the row.
- It creates the STT session at retry start and pushes the WAV's PCM itself. **It skips the speech gate**, so a NoSpeech retry acts as "Send anyway".
- `PreviewRequested` is false.
- If the retry is cancelled, or fails with an unexpected exception, the row's previous outcome is restored. If STT itself fails, the row takes the new error, which is more current. The WAV is kept in every case. A retry never deletes saved audio unless STT succeeds.
- `LatestRetryable` and `RetryAsync` both require `PendingAudioFile != null && !AudioExpired`. An `Interrupted` row whose WAV is already gone is shown but can't be retried from audio, and it never re-pastes.

**What the row records, and when (H4).**
- When the intent resolves, the row is updated with `Mode = Intent.Mode` and `Context = Intent.Context`.
- The final update writes `Delivery`, `Stages`, `StageMs` (per-stage milliseconds, added to `HistoryRow`), `TotalMs`, `FallbackUsed`, `RewriteError` and `CaptureError` (also added to `HistoryRow`).

**Password refusal (H5).** This deviates from the spec's "no row is written", because the row now exists from capture start. On `Refused`, the row is deleted and then `wal_checkpoint(TRUNCATE)` runs, which together with `secure_delete` leaves nothing recoverable. If the delete fails, the run logs `HistoryDeleteFailed`, and the next `Housekeeping` deletes any row whose `Delivery = Refused`.

**Missing type (M1).** `SttProviderKind` is declared in Task 2, in `Enums.cs`, alongside `Mode`, `Stage`, `StageStatus` and `RunOutcome`.

**History failures (M2).**
- If any history write fails, the run sets `HistoryWritten=false` and carries on.
- The WAV is deleted only when the transcript is safe: either the row holds `RawText`, or delivery succeeded.

**Test-only code in Core (M3).**
- `FakeTranscriptionSession` and its factory sit wholly inside `#if YOLIME_E2E` in Core. `ReleaseHasNoTestCodeTests` checks `Yolime.Core.dll` as well as `Yolime.App.dll`.
- `FileAudioSource` stays ungated in Core, because the spec's layout and the tests need it there. Only the App's use of it is gated. The CLAUDE.md hard rule is reworded to match.

**`Win32Clipboard` (M4).** In PR1 it serves only the E2E runner, so it excludes its text from clipboard history (it is test text). PR2's ClipboardGuard decides this case by case, following the spec: "copied, not pasted" results stay in Win+V history.

**Pruning and Clear (M5).**
- History `PruneOlderThan` also deletes the WAVs its rows reference.
- `Clear()` deletes every file in `pending\`. The App disables Clear while a run is active (PR6).
- An `Update` on a missing id counts as unavailable history.

**Orphan WAVs (M6).**
- `IHistoryStore.ReferencedAudioFiles() : IReadOnlySet<string>` lists the WAVs that rows point to.
- An orphan WAV gets an `Interrupted` row whose `RunId` is parsed from its file name and whose `StartedUtc` is the file's creation time.

**STT preflight (M7).**
- The constructor is `BatchTranscriptionSessionFactory(Func<YolimeSettings>, IApiKeySource, HttpClient, ILog)`.
- The pipeline calls `CheckReady()` before `pending.Begin`. If the result isn't `Ready`, the run returns `SttFailed` with the matching `SttError`, and leaves no row or WAV.

**Host pinning (M8).**
- `HttpClients.Shared` is `new HttpClient(new HostPinningHandler { InnerHandler = socketsHandler })`.
- Each request carries an `HttpRequestOptions` key `yolime.expectedHost`. The handler refuses requests that lack it or don't match it, and the session maps a refusal to `SttError.InvalidEndpoint`.

**Retry after a rewrite failure (M9).** The spec's "Retry after LLM failure" needs `RunFromTranscriptAsync`. That moves from PR6 to PR3.

**Smaller items (LOW).**
- **Delivery outcomes.**
  - `Refused` gives the outcome `Discarded`, plus the row deletion described under H5.
  - `PendingPaste` means the sink has taken the job, and gives the outcome `Completed`.
  - `RunOutcome.RewriteFailed` is kept for PR3, for the case where even the fallback can't run.
- **Discard wording.** A user cancel before STT (which is what PR2's short-hold discard uses) deletes the row. `Discarded` means the preview was rejected, and the row is kept.
- **Result and storage types.**
  - `RunResult.Timing` is nullable, and is null for `Busy`.
  - `IApiKeySource` has a single member, `string? Get(string name)`.
  - `PendingRecording.AppendAsync` and `FinaliseAsync` return `ValueTask`.
  - `PendingRecording.DeleteAsync()` disposes the recording and then deletes it.
  - `PendingAudioStore.Delete(name)` is for files that are already closed.
- **Timing measurement.**
  - Milestones are converted by the pipeline from its own `TimeProvider`. "Audio end" is the end of capture, or the retry start for a retry.
  - `NewConnection` comes from a connection counter that `ConnectCallback` increments, compared before and after the request. The single-run gate makes that sound.
  - The pipeline maps its own linked-token timeout to `SttError.Timeout`. The session maps only `ConnectTimeout`.
- **Lock files.** They are generated with `-p:YolimeUnlock=true` everywhere.
- **Spec deviations, recorded.**
  - The timing log rotates at 30 days rather than 7, because PR7 needs the history. The main log stays at 7.
  - History migrations get a step test, `HistorySchema_MigratesThroughFakeStep`.
- **Test details.**
  - The sentinel test loads settings in memory, so nothing sentinel-bearing is written to `settings.json`.
  - Test prompt resources use an explicit `LogicalName`.
  - The release test reads `YOLIME_RELEASE_DLL_DIR` and `YOLIME_E2E_DLL_DIR`.
  - CLAUDE.md warns that leaving out `--test-out` writes to the real clipboard.
  - `--test-data-dir <d>` uses `<d>\roaming` and `<d>\local`.
  - Tag keys are `stage` and `sttError`.
  - Settings JSON uses camelCase with string enums.
