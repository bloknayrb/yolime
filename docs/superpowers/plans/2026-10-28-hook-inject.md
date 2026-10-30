# PR2 `feat/hook-inject` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Holding the hotkey records the mic. Releasing it transcribes and pastes the text into whatever app has focus. All of it is protected by a pure, tested hold state machine, an output planner and a clipboard policy. The app runs in the tray, one instance at a time, and is verified end to end in Windows Sandbox.

**Architecture:**
- **Core holds every decision.** Each one is pure and unit-tested:
  - `HoldStateMachine` decides what a key event means;
  - `OutputPlanner` decides paste versus clipboard, and the text sanitising;
  - `ClipboardPolicy` decides which formats to save and whether to restore.
- **App is thin Win32.**
  - A hook thread runs the low-level keyboard and mouse hooks, a foreground WinEvent hook, and a 100 ms watchdog timer, and calls the state machine synchronously, because Esc and Shift must be swallowed or passed inside the hook callback.
  - A controller turns state-machine effects into pipeline calls.
  - `WasapiSource` records the mic; `ClipboardGuard` and `Injector` paste; UIA snapshots the target.
  - The tray is raw `Shell_NotifyIcon`.
- **No rewriter yet** (PR3). Dictate pastes the raw transcript, using PR1's fallback.
- **No overlay yet** (PR4). Feedback comes from the tray icon tooltip and balloon notices.

**Tech Stack:** PR1's stack, plus NAudio.Wasapi and NAudio.Core 3.3.0 (WASAPI capture and `WdlResamplingSampleProvider`) and FlaUI.UIA3 5.0.0 (UIA focused element, `IsPassword`, RuntimeId).

**Spec:** `docs/plan.md`: PR 2, Behaviour → Hotkeys and modes, Audio, Output (Target, Paste, Editable check, Terminals, Password fields, Elevated foreground, ClipboardGuard), Lifetime, and Verification (hold state machine, output planner, clipboard policy, E2E). PR1's spike findings are in `spike/FINDINGS.md`.

## Global Constraints

- All of PR1's constraints still hold: logs never contain content or key codes, the E2E gating, lock files, and LF line endings.
- **Hook rules.**
  - `WH_KEYBOARD_LL` and `WH_MOUSE_LL` run on one dedicated thread, through a rooted delegate.
  - The callback reduces each event to a `HotkeyInput` (the hold key, Shift, Esc, another key, mouse) before anything leaves the hook thread. VK codes are never logged, stored or posted.
  - `LLKHF_INJECTED` events are ignored, except in E2E builds when the app runs with `--e2e-accept-injected`.
- **Never drive the real desktop.** Anything that sends keys or clicks runs only in Windows Sandbox (`ClipboardRedirection=Disable`, networking off). The hook and paste code run on the host only when Bryan uses the app himself.
- **Never Notepad.** E2E targets are the harness's own WinForms window.
- New pins: `NAudio.Wasapi` 3.3.0, `NAudio.Core` 3.3.0 and `FlaUI.UIA3` 5.0.0. `NAudio.Wasapi` 22.0.0 is an unlisted, mis-published package; never take it.

## Calls I made (no real trade-off)

1. **A raw `Shell_NotifyIcon` tray instead of H.NotifyIcon.WinUI.** That package depends on Windows App SDK 1.6, a major version behind our 2.6.4, and PR4 needs an app-owned Win32 window anyway (spike finding). The tray uses a message-only window on the UI thread.
2. **The state machine runs synchronously on the hook thread.** Swallowing Esc and Shift has to be decided inside the callback. The machine is pure and allocation-light, well inside `LowLevelHooksTimeout`. Its effects are posted to the controller asynchronously.
3. **Press versus hold during processing.** The spec says a *tap* during processing cancels, and a new *hold* is rejected as busy. So a press shorter than 300 ms cancels, and one that reaches 300 ms gets the busy notice.
4. **What a latched recording ignores.** Hands-free mode ignores other keys and mouse clicks, because the user's hands are free to work. A foreground-process change still discards it, and Esc still cancels. A hold, by contrast, is aborted by any other key, click or wheel event, as the spec says.
5. **Shift is swallowed while capturing.** Ctrl+Shift is a common keyboard-layout-switch chord, so passing Shift through during a Right-Ctrl hold could change the user's input language. The hold key itself is never swallowed, so no modifier can get stuck.
6. **Double-tap latch is off by default** (it is an "optional binding"). It is a setting, `DoubleTapLatch`.
7. **Elevated targets.** UIPI blocks `SendInput` into elevated windows, so an elevated target gets clipboard-only output plus a one-time balloon: "yolime can't type into admin windows; the text is on the clipboard."
8. **Notices.** Until the overlay exists (PR4), notices go out as tray balloons (`NIF_INFO`): "Copied, press Ctrl+V", busy, no key, mic denied and so on. PR4 moves them into the overlay.
9. **The health check is a re-arm.** The hooks are reinstalled on session unlock, on resume from sleep, and every 10 minutes while idle. Windows can silently drop an LL hook that times out, and reinstalling one is cheap and invisible.
10. **The `OutputItem` record gains `Mode` and `RunId`.** The terminal rule needs the mode, and the planner needs the run to look up its target snapshot.

## Review Focus

1. **A key-up that never arrives** (focus switched mid-hold, UAC prompt, RDP). The watchdog commits within about 100 ms of the physical release, and nothing stays "recording" (Task 1, Task 5).
2. **Ctrl+V while the user is still holding Shift or Alt.** The paste must wait until every physical modifier is up, or it fires the wrong shortcut (Task 4).
3. **The clipboard holds an image or files before dictation.** It is restored exactly afterwards. A copy the user makes during the 800 ms restore window is not overwritten (Task 3, Task 6, E2E).
4. **The mic is denied or unplugged mid-hold.** The user gets a clear notice, nothing is pasted, partial audio is kept, and the next hold works (Task 5).
5. **Focus moves to a different text field in the same window before the paste.** The text goes to the clipboard with a notice, and is not pasted into the wrong field (Task 2, E2E).

---

### Task 1: HoldStateMachine (Core)

**Files:** create `src/Yolime.Core/Hotkeys/{HotkeyInput.cs, HoldEffect.cs, HoldStateMachine.cs, HoldOptions.cs}`; tests in `tests/Yolime.Core.Tests/Hotkeys/HoldStateMachineTests.cs`.

**Interfaces:**
- `enum HotkeyKind { HoldDown, HoldUp, Shift, Esc, OtherKey, Mouse, ForegroundChanged, Tick }`.
- `readonly record struct HotkeyInput(HotkeyKind Kind, long AtMs, bool HoldPhysicallyDown = false)`. `AtMs` is a monotonic millisecond clock. `HoldPhysicallyDown` is meaningful only for `Tick`.
- `abstract record HoldEffect`, with these cases:
  - `StartCapture(Mode Mode)`;
  - `ShowRecording`, at the 300 ms mark;
  - `CycleMode(Mode Next)`;
  - `Commit(CommitReason Reason)`, where `CommitReason` is `Released`, `MissedKeyUp`, `Cap` or `LatchEnded`;
  - `Discard(DiscardReason Reason)`, where `DiscardReason` is `ShortTap`, `Esc`, `OtherInput` or `ForegroundChanged`;
  - `CancelProcessing`;
  - `RejectBusy`;
  - `CapWarning(int SecondsLeft)`;
  - `Latched`.
- `sealed record HoldOptions(bool DoubleTapLatch = false, int TapMs = 300, int DoubleTapWindowMs = 400, int HeldCapMs = 120_000, int LatchedCapMs = 300_000, int CapWarningMs = 10_000)`.
- `sealed class HoldStateMachine(HoldOptions options)`:
  - `(bool Swallow, IReadOnlyList<HoldEffect> Effects) Handle(HotkeyInput input, Mode startingMode)`;
  - `void SetProcessing(bool processing)`;
  - `HoldState State { get; }`, one of `Idle, Pressed, Recording, Latched, Processing, ProcessingPressed`.

**Rules the tests pin.** All times are milliseconds after the first `HoldDown`.

- **Starting and committing:**
  - From Idle, `HoldDown` gives `StartCapture` and moves to Pressed. A repeat `HoldDown` (auto-repeat) is ignored.
  - In Pressed, a `Tick` at 300 ms or later gives `ShowRecording` and moves to Recording.
  - `HoldUp` before 300 ms gives `Discard(ShortTap)` and records the tap time. At 300 ms or later it gives `Commit(Released)`, then Processing.
- **Double-tap latch** (only when the option is on): a `HoldDown` within 400 ms of a ShortTap gives `StartCapture` and `Latched`, and moves to Latched. Releasing the key doesn't commit. The next press-and-release, of any length, gives `Commit(LatchEnded)`.
- **Mode cycling:** `Shift` in Pressed, Recording or Latched gives `CycleMode` (Dictate → Prompt → Reply → Raw → Dictate) and is swallowed. In other states it isn't swallowed and has no effect.
- **Esc:** in Pressed, Recording or Latched it gives `Discard(Esc)` and is swallowed. Otherwise it isn't swallowed.
- **Other input:** `OtherKey` or `Mouse` in Pressed or Recording gives `Discard(OtherInput)`. Neither is ever swallowed. Latched ignores them.
- **Foreground:** `ForegroundChanged` in Pressed, Recording or Latched gives `Discard(ForegroundChanged)`.
- **Watchdog:** a `Tick` with `HoldPhysicallyDown=false` in Pressed or Recording means a key-up was missed. At 300 ms or later it gives `Commit(MissedKeyUp)`; earlier it gives `Discard(ShortTap)`.
- **Caps:**
  - `Tick` at or past `HeldCapMs` (Recording) or `LatchedCapMs` (Latched) gives `Commit(Cap)`.
  - `CapWarning` is emitted once, at cap minus 10 s, and again each whole second after that (10, 9 … 1).
- **During processing:**
  - `HoldDown` moves to ProcessingPressed.
  - `HoldUp` before 300 ms gives `CancelProcessing`.
  - A `Tick` at 300 ms or later in ProcessingPressed gives `RejectBusy`, once.
  - `SetProcessing(false)` returns to Idle, from ProcessingPressed too, without emitting effects.
- After `Discard`, the machine is Idle. After `Commit` it is Processing until `SetProcessing(false)`.

- [ ] **Step 1:** Write the tests, one per rule above, in this table:

  | Area | Tests |
  |---|---|
  | Basic flow | `Press_StartsCapture_AtKeyDown`, `AutoRepeat_Ignored`, `ShortTap_Discards`, `LongHold_Commits` |
  | Shift and Esc | `Shift_Cycles_AndIsSwallowed_OnlyWhileCapturing`, `Esc_Cancels_AndIsSwallowed_OnlyWhileCapturing` |
  | Aborts | `OtherKey_Aborts_NotSwallowed`, `Mouse_Aborts`, `ForegroundChange_Discards` |
  | Watchdog | `Watchdog_MissedKeyUp_Commits`, `Watchdog_Before300_Discards` |
  | Caps | `HeldCap_Commits_WithCountdown`, `LatchedCap_Commits` |
  | Latch | `DoubleTap_Latches_WhenEnabled`, `DoubleTap_Ignored_WhenDisabled`, `Latched_IgnoresOtherKeysAndMouse`, `Latched_TapEnds` |
  | Processing | `Processing_Tap_Cancels`, `Processing_Hold_RejectsBusy_Once`, `SetProcessingFalse_ReturnsIdle` |

  Run `dotnet test --filter "FullyQualifiedName~HoldStateMachine"`. Expected: compile failure.
- [ ] **Step 2:** Implement it. Then run the same filter. Expected: PASS.
- [ ] **Step 3:** Commit: `feat(core): add hold-key state machine`.

### Task 2: OutputPlanner and terminal detection (Core)

**Files:** create `src/Yolime.Core/Output/{TargetSnapshot.cs, OutputPlanner.cs, TerminalDetector.cs}`; modify `src/Yolime.Core/Output/IOutputSink.cs` (`OutputItem` gains `Mode Mode, Guid RunId`) and `DictationPipeline.cs` (it passes them); tests in `tests/Yolime.Core.Tests/Output/OutputPlannerTests.cs`.

**Interfaces:**
- `sealed record TargetSnapshot(nint Hwnd, int ProcessId, string ProcessName, string WindowClass, string? FocusRuntimeId, string? FocusClass, bool IsPassword, bool? IsEditable, bool IsElevated)`.
- `enum OutputAction { Paste, ClipboardOnly, Refuse }`.
- `enum OutputNotice { None, CopiedTargetChanged, CopiedTerminalReply, CopiedClipboardOnly, CopiedNotEditable, CopiedElevated, Refused }`.
- `sealed record OutputDecision(OutputAction Action, string Text, OutputNotice Notice)`.
- `static OutputDecision OutputPlanner.Plan(OutputItem item, TargetSnapshot atStart, TargetSnapshot now)`.
- `static bool TerminalDetector.IsTerminal(TargetSnapshot t)`. It returns true for the classes `ConsoleWindowClass`, `CASCADIA_HOSTING_WINDOW_CLASS` and `mintty`, and for process `Code` when the focus class contains `xterm-helper-textarea`.

**Rules, in priority order:**
1. `now.IsPassword` gives Refuse, with notice `Refused`.
2. If `Hwnd` or `FocusRuntimeId` differs between the two snapshots, the result is ClipboardOnly with notice `CopiedTargetChanged`. A null RuntimeId on both sides counts as unchanged.
3. `now.IsElevated` gives ClipboardOnly with notice `CopiedElevated`.
4. A terminal in Reply mode gives ClipboardOnly with notice `CopiedTerminalReply`.
5. `item.Intent == ClipboardOnly` gives ClipboardOnly with notice `CopiedClipboardOnly`.
6. `now.IsEditable == false` gives ClipboardOnly with notice `CopiedNotEditable`. `null` (unknown) still pastes.
7. Otherwise the action is Paste.

**Text, in every case:** trailing `\r` and `\n` are stripped. In a terminal, leading `!`, `/` and `#` characters are also stripped, along with the whitespace that follows them.

- [ ] **Step 1:** Write the tests:

  | Area | Tests |
  |---|---|
  | Each rule | One test for each of rules 1–7 |
  | Precedence | `Password_BeatsTargetChanged` |
  | Terminal detection | `TerminalClasses_Detected`, `VsCodeEditor_NotTerminal_VsCodeTerminal_Is` |
  | Text sanitising | `Terminal_StripsLeadingBangSlashHash`, `TrailingNewlines_AlwaysStripped`, `NonTerminal_KeepsLeadingSlash` (for example "/etc/hosts is…" pasted into Word) |

  Run them. Expected: compile failure.
- [ ] **Step 2:** Implement the planner, and pass `Mode` and `RunId` into `OutputItem` from the pipeline. Then run the full `dotnet test`. Expected: PASS.
- [ ] **Step 3:** Commit: `feat(core): add output planner and terminal detection`.

### Task 3: ClipboardPolicy (Core)

**Files:** create `src/Yolime.Core/Clipboard/ClipboardPolicy.cs`; tests in `tests/Yolime.Core.Tests/Clipboard/ClipboardPolicyTests.cs`.

**Interfaces:**
- `readonly record struct ClipFormat(uint Id, string? Name)`.
- `static class ClipboardPolicy` has these members:
  - `IReadOnlyList<ClipFormat> Saveable(IEnumerable<ClipFormat> present)`.
    - Keep: CF_UNICODETEXT (13), CF_DIBV5 (17), or CF_DIB (8) only if CF_DIBV5 is absent, CF_HDROP (15), CF_LOCALE (16), and the registered "HTML Format", "Rich Text Format", "PNG", "FileGroupDescriptorW" and "Shell IDList Array".
    - Drop: synthesized or handle formats (CF_TEXT, CF_OEMTEXT, CF_BITMAP, CF_ENHMETAFILE, CF_METAFILEPICT, CF_PALETTE, CF_OWNERDISPLAY, private 0x200–0x2FF and GDI 0x300–0x3FF), and anything over 64 MB in total.
  - `bool IsSensitive(IEnumerable<ClipFormat> present)`: true when "ExcludeClipboardContentFromMonitorProcessing" or "Clipboard Viewer Ignore" is present, which marks password-manager content. PR3 uses it.
  - `bool ShouldRestore(uint ourSequence, uint currentSequence)`: `current == our`.
  - `TimeSpan RestoreDelay`: 800 ms.
  - `TimeSpan OpenRetryDelay(int attempt)`: 10 ms × 2^attempt, capped at 200 ms. `MaxOpenAttempts` is 10.
  - `IReadOnlyList<string> ExclusionFormats`: "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory" (DWORD 0) and "CanUploadToCloudClipboard" (DWORD 0).

- [ ] **Step 1:** Write the tests: `Saveable_KeepsTextImageFilesHtml`, `Saveable_DropsSynthesizedAndHandleFormats`, `Saveable_PrefersDibV5`, `Sensitive_Detected`, `Restore_OnlyWhenSequenceStillOurs`, `RetryDelay_BacksOffAndCaps`. Run them. Expected: compile failure.
- [ ] **Step 2:** Implement, then run the tests again. Expected: PASS.
- [ ] **Step 3:** Commit: `feat(core): add clipboard policy`.

### Task 4: App Win32 layer: clipboard guard, injector, target snapshot

**Files:** create the following:
- `src/Yolime.App/Native/{User32.cs, ClipboardGuard.cs, Injector.cs, TargetReader.cs, Elevation.cs}`;
- `src/Yolime.App/Output/AppOutputSink.cs`;
- delete `Native/Win32Clipboard.cs` (`ClipboardGuard` replaces it; the E2E runner uses `ClipboardGuard.SetText`).

**Interfaces:**
- `ClipboardGuard`:
  - `Task<SavedClipboard> SaveAsync()` saves the formats that `ClipboardPolicy.Saveable` keeps, as byte arrays.
  - `Task<uint> SetTextAsync(string text, bool excludeFromHistory)` returns the clipboard sequence number after the set.
  - `Task RestoreAsync(SavedClipboard saved, uint ourSequence)` waits for `RestoreDelay`, and restores only if `ShouldRestore`. The restored content carries the exclusion formats.
  - Every open uses the retry and backoff policy.
- `Injector.PasteAsync(CancellationToken)` waits until Ctrl, Shift, Alt and Win are all physically up (`GetAsyncKeyState`, polled every 10 ms for up to 3 s; it throws `TimeoutException` after that), then sends Ctrl+V with `SendInput` as 4 inputs.
- `TargetReader.Snapshot()` reads:
  - the foreground HWND, its process ID and name, and the window class;
  - the elevation of the target process (`OpenProcess(QUERY_LIMITED)` and `TokenElevation`; access denied counts as elevated);
  - the UIA focused element (RuntimeId joined with `.`, ClassName, `IsPassword`, and whether a ValuePattern exists and isn't read-only), read with FlaUI.UIA3 on a dedicated MTA thread with a 300 ms timeout. A timeout leaves those fields null or false.
- `AppOutputSink(TargetReader, ClipboardGuard, Injector, Func<Guid, TargetSnapshot?> startSnapshot, INotices notices) : IOutputSink`:
  - takes a fresh snapshot and plans with `OutputPlanner`;
  - Refuse returns `Refused`;
  - ClipboardOnly sets the text with `excludeFromHistory: false` and returns `Copied`;
  - Paste saves the clipboard, sets the text with `excludeFromHistory: true`, pastes, restores in the background, and returns `Pasted`;
  - shows the notice;
  - if the paste throws, the text stays on the clipboard, the notice is "Copied, press Ctrl+V", and the result is `Copied`.
- `interface INotices { void Show(OutputNotice n); void Show(string message); }`. The tray implements it in Task 6.

- [ ] **Step 1:** Build the App and confirm there are no warnings. These classes are Win32 wrappers whose logic sits in Core (Tasks 2 and 3), so their behaviour is verified by the Sandbox E2E (Task 8), not by host unit tests, which would have to drive the real desktop.
- [ ] **Step 2:** Commit: `feat(app): add clipboard guard, injector and target reader`.

### Task 5: Hook thread, recorder, controller

**Files:** create `src/Yolime.App/Native/{HookThread.cs, KeyCodes.cs}`, `src/Yolime.App/Audio/WasapiSource.cs` and `src/Yolime.App/Controller.cs`; add `HoldKeyVk` (default `0xA3`, Right Ctrl), `DoubleTapLatch` (false) and `StartingMode` (Dictate) to `YolimeSettings` (Core, with defaults, so no migration is needed).

**Interfaces:**
- `HookThread(int holdVk, bool acceptInjected, HoldStateMachine machine, Func<Mode> startingMode, Func<int?> captureProcessId, Action<HoldEffect> post)`. It runs a dedicated STA thread with its own message loop, and handles these inputs:
  - the keyboard and mouse LL hooks;
  - `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`, which yields `ForegroundChanged` only when the new foreground process differs from `captureProcessId()`;
  - a 100 ms `SetTimer` while capturing, which yields `Tick` with `GetAsyncKeyState(holdVk) < 0`;
  - `WTSRegisterSessionNotification` and power broadcasts, plus a 10-minute idle timer, all of which reinstall the hooks.

  The hold key is matched on VK. Right and left are distinguished by their own VKs (`VK_RCONTROL` 0xA3 and so on), never by the extended flag alone.
- `WasapiSource : IAudioSource`. It wraps NAudio `WasapiCapture`, shared mode on the default capture device, and converts to 16 kHz mono s16 through `WdlResamplingSampleProvider`. Audio flows through a bounded `Channel<AudioChunk>` (capacity 600, so the callback never blocks). It has `Start()` and `Stop()`, and `Stop` completes the channel.

  It maps failures to `CaptureError`:
  - `E_ACCESSDENIED` (privacy setting off) becomes AccessDenied;
  - no endpoint becomes NoDevice;
  - `RecordingStopped` with an exception becomes DeviceLost.
- `Controller` owns the pipeline, the hook thread and the notices. It acts on each effect:
  - `StartCapture`: snapshot the target, call `CheckReady` (if not ready, show "Set up an API key" and discard), start `WasapiSource`, create the intent `TaskCompletionSource`, and start `pipeline.RunAsync` with a `CancellationTokenSource`.
  - `CycleMode`: change the mode, and update the tray tooltip.
  - `Commit`: stop the source, then complete the intent with the current mode.
  - `Discard`: cancel the run.
  - `CancelProcessing`: cancel the run.
  - `RejectBusy`: show "Still working on your <mode>. Tap the hotkey to cancel."
  - `CapWarning`: update the tooltip.

  When a run completes, it calls `machine.SetProcessing(false)` and maps the outcome to a notice:
  - NoSpeech: "Didn't hear anything";
  - SttFailed: Auth gives "API key rejected", Network/Timeout gives "Couldn't reach the transcription service, Retry from the tray", MissingKey gives "Set up an API key";
  - CaptureFailed: AccessDenied gives "Microphone access is off in Windows privacy settings";
  - Completed: no notice.

  Calls into the machine go through `HookThread.Post`, which marshals onto the hook thread so the machine stays single-threaded.

- [ ] **Step 1:** Add the settings fields. Extend `SettingsStoreTests.SaveThenLoad_RoundTrips` to cover them. Run the settings tests. Expected: PASS.
- [ ] **Step 2:** Implement and build. Expected: no warnings.
- [ ] **Step 3:** Commit: `feat(app): add hook thread, WASAPI recorder and controller`.

### Task 6: Tray, single instance, lifetime

**Files:** create `src/Yolime.App/Shell/{TrayIcon.cs, MessageWindow.cs}`; modify `App.xaml.cs` and `Program.cs`.

**Interfaces:**
- **Single instance.** In `Program.Main` (non-test path), `AppInstance.FindOrRegisterForKey("yolime-main")`. If another instance is already the key's owner, this instance redirects its activation there, ignores its own arguments, and exits with code 0.
- **Lifetime.** `App` sets `DispatcherShutdownMode = OnExplicitShutdown` and starts the Controller on launch. Exit is only through the tray.
- **Tray.** `TrayIcon : INotices`, using `Shell_NotifyIconW` on a message-only window:
  - Icons: `IDI_APPLICATION` for now (PR5 draws real icons). The tooltip carries the state: "yolime: ready", "recording (Prompt)", "paused", "setup needed" or "admin window: clipboard only".
  - Right-click opens a menu (`TrackPopupMenu`) with "Pause hotkeys" (a toggle) and "Exit".
  - Balloons (`NIF_INFO`) show notices. The elevated notice shows once per app run.
- **Housekeeping.** It runs at startup.

- [ ] **Step 1:** Implement. Build Release and E2E. Expected: no warnings.
- [ ] **Step 2:** Commit: `feat(app): add tray, single instance and explicit-shutdown lifetime`.

### Task 7: E2E hooks in the App (E2E build only)

**Files:** modify `Program.cs` and `Controller.cs` under `#if YOLIME_E2E`.

**Interfaces:**
- **`--e2e`.** This flag starts the full app with these test settings:
  - `acceptInjected = true`;
  - a `WasapiSource` replacement: each capture plays `--test-mic <wav>` through `FileAudioSource` at real time (one chunk every 100 ms), and stops on Commit;
  - `FakeTranscriptionSessionFactory(--test-transcript)`;
  - data in `--test-data-dir`;
  - `--e2e-results <dir>`: after each run it writes `run-<n>.json` there with the outcome, delivery and notice (enum names only).
- `ReleaseHasNoTestCodeTests` adds `--e2e` and the replacement source type to its lists.

- [ ] **Step 1:** Implement. Run the release-absence test against both builds. Expected: PASS, with the E2E control showing the new names.
- [ ] **Step 2:** Commit: `feat(app): add E2E-only app mode`.

### Task 8: Sandbox E2E

**Files:** create the following:
- `tests/Yolime.E2E/{Yolime.E2E.csproj, Program.cs, TargetForm.cs, Scenarios.cs}`, a self-contained win-x64 console app with WinForms;
- `tests/Yolime.E2E/e2e.wsb`, with networking off, `ClipboardRedirection=Disable`, the publish folders mapped read-only and `results` writable;
- `tests/Yolime.E2E/run-e2e.ps1`, which publishes, writes the `.wsb` with absolute paths, launches it, and waits for `DONE`.

**The harness, inside the Sandbox:**
- It starts the App E2E build with `--e2e --test-mic speech-short.wav --test-transcript "hello e2e" --e2e-results C:\results\app`.
- It shows its own `TargetForm`, which has a normal TextBox, a password TextBox and a read-only TextBox.
- It focuses the form by clicking it. In the Sandbox's logon session a click is what reliably gives focus (spike lesson).
- It drives scenarios with `SendInput` on Right Ctrl.

**Scenarios and assertions.** Each scenario resets the TextBoxes and seeds the clipboard with sentinel text before it runs.

| Scenario | Action | Expected |
|---|---|---|
| `Hold_Pastes` | Hold 1.5 s, release | The TextBox becomes "hello e2e"; the foreground never left the form; the clipboard is restored to the sentinel within 2 s |
| `ShortTap_Discards` | Hold 150 ms | Nothing is pasted; run JSON says Cancelled, or no run is recorded |
| `Esc_Cancels` | Hold, Esc, release | Nothing is pasted; the Esc never reached the form (its KeyDown counter stays 0) |
| `Shift_Cycles` | Hold, Shift, release | The run's mode is Prompt; delivery is ClipboardOnly with no rewriter (fallback); the clipboard holds "hello e2e" and the TextBox is empty |
| `Password_Refused` | Focus the password box, hold, release | Nothing is typed; outcome Discarded, delivery Refused; the clipboard still holds the sentinel |
| `ImageClipboard_Restored` | Seed the clipboard with a bitmap, then Hold_Pastes | The bitmap is back on the clipboard |
| `TargetChanged_Copies` | Hold, move focus to the second TextBox mid-run, release | ClipboardOnly with notice CopiedTargetChanged |
| `CopyDuringRestore_NotOverwritten` | Hold_Pastes, then set the clipboard to "user copy" 200 ms after the paste | After 2 s the clipboard is "user copy" |

The harness writes `C:\results\e2e.json` with each scenario's pass or fail and its reason.

- [ ] **Step 1:** Write the harness and `run-e2e.ps1`. Run it. Expected: every scenario passes. Read `e2e.json` and any screenshots.
- [ ] **Step 2:** Document the command in CLAUDE.md. Commit: `test(e2e): add Sandbox end-to-end harness for hold, paste and guards`.

### After merge

Bryan's one-time human check (spec Verification) waits until PR4/5. The overlay and the setup wizard are what make the app usable day to day.

---

## Rulings after four adversarial reviews (platform, security/privacy, product, UI/UX), overriding the tasks above

### Done in code (90c6532, 4fd753a)

- **State machine.**
  - The hold key is tracked physically. Auto-repeat is ignored in every state, and after a commit or discard while the key is still held, a real key-up is needed before it can start again.
  - Under 300 ms, Shift and Esc count as a shortcut: they pass through and the capture is discarded.
  - Shift is tracked as separate down and up events. A Shift-up is swallowed only when its down was swallowed.
  - `ModeCycling` is an option. `Commit` carries `ShiftHeld`.
  - During processing, a re-tap within 400 ms of the commit, or a shortcut chord, doesn't cancel.
  - Latched (hands-free) recording passes Shift and Esc through. Esc cancels it only while the hold key is down.
  - A foreground change commits a latched recording. It still discards a held one.
  - `SessionEnded` (lock, suspend, disconnect) discards. `Abort()` exists for runs that end early.
- **Planner.**
  - A password field at key-down: Refuse. A password field only at delivery: CopiedTargetChanged.
  - No start snapshot (a retry): ClipboardOnly.
  - A null RuntimeId on either side: compare HWNDs only.
  - In a terminal, newlines become spaces and control characters (other than tab) are dropped.
  - mintty pastes with Shift+Insert.
- **Clipboard policy.**
  - `ExclusionFormats(excludeFromHistory)`: cloud upload is always blocked, and history exclusion applies only to transient copies.
  - Registered formats are kept except an OLE denylist. FileGroupDescriptorW is dropped.

### To build (these replace Tasks 4-8)

1. **Orchestration lives in Core and is unit-tested with fakes.**
   - **`Notices`.** Each `RunResult` and `OutputNotice` maps to an enum-only `Notice(Kind, Mode?, Seconds?)`, with one copy table. A test covers every outcome and error combination. Notices never carry free text.
   - **`HookReducer`.** A pure function from (vk, up, injected, extraInfo) to `HotkeyKind?`:
     - our own `SendInput` marker is always ignored;
     - injected input is accepted only under `#if YOLIME_E2E`;
     - key-ups of other keys are ignored;
     - mouse moves are ignored; only button-downs and the wheel count.
   - **`OutputExecutor`**, behind `IClipboard`, `IInjector` and `ITargetReader`. It works in this order:
     1. Wait until the modifiers are up.
     2. Snapshot the target and plan.
     3. Flush any pending restore, save the clipboard, set the text.
     4. Re-check the foreground HWND; if it changed, leave the text on the clipboard instead.
     5. Paste.
     6. Restore after the target is idle (`WM_NULL`), plus 800 ms. Only one restore is pending at a time.

     It never restores sensitive originals; it clears them. The restore carries only the `Can*` markers, because "Exclude…" and "Viewer Ignore" would read as sensitive in PR3. If nothing could be saved and the original wasn't empty, it skips the restore.
   - **`CaptureCoordinator`.**
     - The mic starts at key-down into a memory buffer. `RunAsync` starts only at 300 ms, so Ctrl+C presses touch no disk and can't hit the busy gate.
     - It awaits the previous run before starting the next. The caller-supplied `RunRequest.RunId` keys the start snapshot.
     - Failures at key-down are remembered and shown at 300 ms. A shortcut tap stays silent.
     - Discard and cancel stop the device.
     - When a run completes, the machine gets `SetProcessing(false)`, or `Abort()` if it is still capturing. A generation number guards against stale completions.
     - A password field at key-down is discarded before anything is recorded or uploaded.
2. **App, Win32 layer.**
   - **Elevation** means the target's integrity level is above ours. The "hotkey unavailable in admin windows" tooltip is driven by the foreground WinEvent. The manifest is `asInvoker`.
   - **UIA (FlaUI):**
     - connection and transaction timeouts of about 300 ms;
     - a stuck worker is replaced, and the worker is warmed at startup;
     - it never reads Value, Name or Text;
     - password fallback: `GUITHREADINFO.hwndFocus` plus `ES_PASSWORD`.
   - **WASAPI:**
     - the channel completes in `RecordingStopped`;
     - construction is wrapped (no endpoint gives NoDevice);
     - HRESULT mapping: 0x80070005 is AccessDenied, 0x88890004 is DeviceLost, 0x8889000A is DeviceLost;
     - custom downmix to mono, `ReadFully=false`.
   - **Hooks.** Callbacks are wrapped in try/catch and always call `CallNextHookEx`. The mouse hook is installed only while Pressed or Recording. The timer runs in every non-Idle state.
   - **SendInput** uses `VK_LCONTROL` plus scan codes, with the marker in `dwExtraInfo`. `LWIN` and `RWIN` are checked separately.
   - **Tray.**
     - A hidden top-level window (for TaskbarCreated and end-session messages), `RegisterSuspendResumeNotification`, and `SetForegroundWindow` plus `WM_NULL` around the menu.
     - Menu: Retry last recording, Pause hotkeys, Exit. The recording icon is a red dot.
     - Startup balloon: "Hold Right Ctrl and speak". A second launch shows "Already running".
     - Clicking the mic notice opens the microphone privacy settings.
   - **Sounds** play at start (300 ms), stop and error, and survive Do Not Disturb. The same notice is shown at most once every 30 s.
   - **Mode cycling** is off in PR2: Prompt and Reply have no rewriter until PR3.
   - **Single instance** uses `RedirectActivationToAsync` the documented way. Redirected arguments are ignored. E2E builds use the key `yolime-main-e2e`.
3. **E2E gating.**
   - Accepting injected input exists only under `#if YOLIME_E2E`.
   - `--e2e` refuses to run unless the user is WDAGUtilityAccount, and requires `--test-mic`.
   - `ReleaseHasNoTestCodeTests` also checks `--e2e*`.
   - The App E2E build is mapped from build output, never published.
4. **Sandbox E2E.**
   - **Focus.** A focus mini-spike comes first (`AttachThreadInput` plus `SetForegroundWindow`, and a DPI-correct click). The harness fails loudly if the form isn't in the foreground.
   - **Sandbox config.** The `.wsb` disables audio and video input and printers.
   - **Target form.** It runs on its own UI thread, with two normal TextBoxes, a password box and a read-only box. The bitmap is seeded as raw `CF_DIB`. `--test-stt-delay` slows STT so focus can move mid-run.
   - **Scenarios:**
     - hold-paste;
     - short tap;
     - Esc;
     - Right Ctrl+C passes through;
     - a tap cancels processing, a chord doesn't;
     - password refused at start;
     - read-only field copies;
     - target change copies;
     - image restored (pixel compare);
     - a copy made during the restore survives;
     - two pastes back to back keep the original;
     - cap with repeats still pastes;
     - silence gives NoSpeech.
5. **Smart App Control.** Checked 2026-10-28: Off.

### For Bryan (spec questions; defaults stay with the spec)

- **(a) When the mic opens.** At key-down, the Bluetooth headset switches profile and the mic indicator flickers on every Right-Ctrl press. At 300 ms, the first syllable can be clipped. Default: key-down. PR7's warm mic solves this.
- **(b) Late interruptions.** Should other input, or a focus change, after speech has been heard still discard the recording? Default: discard, with a notice after 300 ms.
- **(c) Cancelling during processing.** Should a cancel tap delete the recording, or keep it for Retry? Default: delete, with a 400 ms debounce.
- **(d) Terminal slash commands.** Stripping a leading `/` in terminals breaks dictated Claude Code slash commands. Default: strip.
