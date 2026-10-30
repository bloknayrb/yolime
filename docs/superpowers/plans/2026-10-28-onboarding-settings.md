# PR5 `feat/onboarding-settings` Implementation Plan (v1, for multi-angle review)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans.

**Goal:** yolime can be set up and adjusted without editing JSON. The pieces:
- a first-run wizard;
- a settings window;
- keys kept in Windows Credential Manager;
- start with Windows;
- the full tray menu with icon states;
- a "paste last result" hotkey.

**Spec:** `docs/plan.md`, sections "Lifetime, onboarding, settings", "No lost work" (paste last result) and PR list item 5.

## Decisions

1. **Windows are normal WinUI windows.** The settings window and the wizard are things the user opens and works in, so they take focus as ordinary windows. Only the notch must never take focus.
   - One `SettingsWindow`, with a NavigationView: General, Hotkey, Microphone, Keys, Apps, Vocabulary, Overlay, History, Advanced.
   - One `WelcomeWindow` (the wizard), which reuses the same page controls.
   - Both are created on demand and closed, never hidden. They re-read settings when opened.
2. **Settings change live.**
   - The App holds settings in a `SettingsHolder` (Core) with `Current` plus a `Changed` event. Every `Func<YolimeSettings>` already threaded through Core reads `holder.Current`.
   - A save writes through `SettingsStore.Save`, with the existing atomic replace and backups. A failed or read-only save shows inline.
   - A few settings need re-creating something, and the holder handles each:
     - the hold key re-arms the hook;
     - the double-tap latch rebuilds the machine options;
     - capture exclusion is re-applied to the notch.
3. **Keys (Core `IApiKeySource`):**
   - A new `CredentialKeySource` reads Credential Manager (`CredRead`, type `CRED_TYPE_GENERIC`, targets `yolime/OPENAI_API_KEY`, `yolime/GROQ_API_KEY`, `yolime/YOLIME_ANTHROPIC_API_KEY`), with the environment as fallback (spec).
   - The Keys page holds a PasswordBox per provider. A stored key is never shown again: the page displays "Saved (ends …abcd)", the last 4 characters only, plus "From environment" when it came from the environment.
   - Save writes the credential; Remove deletes it.
   - Test sends one request that costs nothing or almost nothing:
     - OpenAI and Groq: `GET /v1/models` on the provider's pinned host;
     - Anthropic: `POST /v1/messages/count_tokens`, or a `max_tokens: 1` Haiku call if that endpoint isn't available.
     - The result is enum-only: OK, Rejected, Unreachable.
   - Keys never go to logs or to settings.json.
   - The copy recommends project-scoped keys with spend caps (spec).
4. **Hotkey capture:**
   - The capture box records the next key while the box is armed. A temporary hook-thread capture mode, a sibling of PreviewCapture, reports the raw VK and side. It never logs and is never active outside the box.
   - It shows "Right Ctrl" etc. and validates with `HookReducer.IsValidHoldKey`.
   - It is limited to Ctrl and Shift on either side, and Caps Lock, Scroll Lock, Pause and F13–F24. The remaining hotkey question becomes a decision here: Alt and Windows keys open menus or Start, and typing keys auto-repeat into apps. That closes the talkie lesson.
   - The "paste last result" hotkey is a chord, Ctrl+Shift+V by default, editable. It's handled by `RegisterHotKey` on the tray window (not the LL hook), and it pastes the newest history row's output through the executor's normal path, with a fresh snapshot and planning.
5. **Microphone:**
   - The picker lists WASAPI capture devices (friendly names), plus "Default device".
   - A live meter comes from a short-lived capture that runs only while the page is open. It closes the device on leave, and the Windows mic indicator shows while it's open.
   - A link opens `ms-settings:privacy-microphone`.
   - New `MicDeviceId` setting (null means default). `WasapiDevice` honours it.
6. **Start with Windows:** a `HKCU\...\Run\yolime` value with the quoted exe path, written and removed by the toggle. Its state is read back from the registry, never assumed.
7. **Apps page:**
   - A list of the per-app starting modes from `AppStartingModes`: add a process (from the running apps' names, or typed), choose a mode, remove.
   - The built-in rule (code and terminal apps start in Prompt) is stated.
   - Profiles stay automatic. The page shows which profile each listed app gets.
8. **Vocabulary page:** an editable list, one term per line. It's sent to both STT and the LLM, as already wired.
9. **Overlay page:**
   - position (Top or Bottom centre);
   - hide from screen capture;
   - sounds, and mode cues;
   - preview modes (a checkbox per mode).
10. **History page:** retention days (1–3650), pause history, and Clear history. Clear asks for confirmation in the page itself, not a dialog. The History window itself is PR6.
11. **Advanced page:**
    - the STT provider, models and custom endpoint (the endpoint is validated as https);
    - the Prompt model (Sonnet or Haiku);
    - prompts: "Edit dictate/prompt/reply" opens the override file (created from the default, with its version line) in the default `.md` editor; "Reset to default" calls `PromptLibrary.ResetToDefault`;
    - a notice when `NewerDefaultExists`;
    - "Open settings.json".
12. **Pause history** (new setting `HistoryPaused`): while on, runs write no row and no WAV. Retry is then unavailable, and the tray says so. This touches the pipeline: `TryHistory` is skipped, so the run writes no history.
13. **Tray:**
    - Left-click opens History (PR6). Until then it opens Settings.
    - The menu: Paste last result, Retry last, Pause hotkeys, Pause history, Settings, History (disabled until PR6), Exit.
    - Icon states: idle, recording, paused, setup needed (no STT key, or the mic denied), elevated. Each is a generated 16/32 px icon (no external assets), recoloured.
14. **First run:**
    - The wizard opens when settings.json is missing, or when no STT key is found.
    - Its steps:
      1. Keys: Test, plus the env status.
      2. Mic: meter and picker.
      3. "Hold your hotkey": live feedback from the hook, without starting a run.
      4. A practice box: a TextBox in the wizard. Dictation pastes into it for real, since it's the foreground app.
      5. Done: the pin-the-tray hint, and "yolime is running".
    - Skippable at every step. Its completion is recorded as `FirstRunDone`.
15. **E2E:** a Sandbox scenario opens Settings from the tray, changes the overlay position, checks settings.json, and closes it. A wizard smoke test checks it opens on a fresh data dir and completes with Skip. The key-test buttons have no network in the Sandbox, so they show Unreachable, and that's asserted.

## Tasks
1. Core `SettingsHolder` and live-reload wiring, with tests.
2. Core settings: `MicDeviceId`, `HistoryPaused`, `FirstRunDone`, `PasteLastHotkey`, plus the hold-key whitelist (`IsValidHoldKey` tightened), with tests.
3. Core `ICredentialStore` port, and `KeySource` combining the store and the environment, with tests using a fake store. App `WindowsCredentialStore` (`CredRead`/`CredWrite`/`CredDelete`).
4. Core `KeyTester` (enum results, pinned hosts, no body logging), with tests using a fake HTTP handler.
5. Pipeline: history pause (no row, no WAV), with tests.
6. **Core paste-last.** The newest row with output goes through the executor.
   - It pastes at the cursor: the hotkey is a deliberate request, so a fresh snapshot is both the start target and the current one, and every safety rule still runs (password, admin, terminal).
   - Tests cover this.
7. App: `RegisterHotKey` for paste-last, `RunKey` for start with Windows, tray menu and icon states.
8. App: `SettingsWindow` with its pages.
9. App: `WelcomeWindow`.
10. App: mic enumeration and the meter (WASAPI).
11. E2E scenarios.

## v2 rulings after four reviews (UI/UX, platform, security, product/architecture). These override the text above.

**Paste last result**
1. **The chord is Win+Alt+V** (changed after implementation: Ctrl+Alt+V is Paste Special in Word, Excel and Outlook; Bryan chose Win+Alt+V), registered with `MOD_NOREPEAT`, and editable. All four reviewers flagged Ctrl+Shift+V: it is "paste as plain text" in Chrome, Edge, Slack, VS Code, Word and Terminal, and a global registration would take it away from them.
   - If `RegisterHotKey` fails, the reason shows inline in Settings and in the tray tooltip.
2. **The source is an in-memory `LastResult` (Core)**, not "the newest history row".
   - It's the last output delivered in this session, from a Completed or CompletedWithFallback run.
   - It's cleared by Clear history and kept for at most 30 minutes.
   - It isn't available while history is paused, or after a Refused or Discarded run.
   - It goes through the coordinator's one-run-at-a-time check (Busy while a run is in progress), then through the executor. The fresh snapshot is both the start and the current target, so every safety rule runs: password refuses, and elevated and remote copy.

**Hold key**
3. **The allowed hold keys are Right Ctrl (the default), Right Shift and Pause.**
   - **Left Ctrl and Left Shift** would open the mic on every shortcut, because the mic opens at key-down.
   - **Caps Lock and Scroll Lock** would toggle with every dictation, since the hold key isn't swallowed.
   - **F13–F24** come only from remappers, which inject input, and injected input is ignored.
   - **Alt and Win** open menus and Start.
   - The spec's Alt plus mask-key option, and editable per-app profiles, are recorded as deferred under "Decisions after approval" in docs/plan.md.
   - **A saved key that is no longer allowed** resets to Right Ctrl with a notice: "Hold key reset to Right Ctrl (your key isn't supported any more)". It is never reset silently.
   - **This is Bryan's open question.** It stays the default until he answers.
4. **Capture mode, on the hook thread:**
   - It runs before the reducer and the preview capture, so the current hold key never starts a run while you rebind.
   - It's armed only while Settings is the foreground window, and disarms on deactivation or after 10 s.
   - It swallows the captured key, down and up, and ignores injected keys.
   - It filters to the allowed list on the hook thread. A refused key comes back only as a category (Alt, Win, Lock, Typing, Left modifier), never as a key code, and the UI gives the reason.
   - The wizard's "Copilot-key or AltGr keyboard?" hint says: "If your keyboard has no Right Ctrl, choose Right Shift or Pause."

**Live settings**
5. **`SettingsHolder` (Core):**
   - `Current`, plus `Update(Func<YolimeSettings, YolimeSettings>)`, which is atomic, saves, and raises `Changed`.
   - A `FileSystemWatcher` on settings.json, debounced, reloads hand edits into the holder. The Settings window refreshes on `Changed`, so a save never overwrites a hand edit.
   - The pipeline snapshots settings once per run.
6. **Values captured once at startup, and how each reloads:**
   - **Hold key, HookReducer and PreviewCapture:** the hook thread swaps them through `Post`, only when Idle and the key isn't down; otherwise the change is queued.
   - **HoldOptions (latch, mode cycling):** the same, on the machine.
   - **OverlayModel's silence threshold:** read at each RecordingStarted.
   - **Retention:** pruning runs again when it changes. Lowering it asks for confirmation in the page.
   - **Readiness tooltip and icon:** recomputed after any key save or remove, and on settings changes.
   - **Sounds:** already read live.

**Keys**
7. **One `KeySource` (Core) serves both STT and Claude**, replacing the two `EnvironmentApiKeySource` instances.
   - Precedence: Credential Manager, then the environment. The page shows which one is in use, and notes that environment variables are read when yolime starts.
   - There are slots for OpenAI, Groq, the custom endpoint (`YOLIME_STT_API_KEY`) and Claude (`YOLIME_ANTHROPIC_API_KEY`), with a note that `ANTHROPIC_API_KEY` is deliberately ignored.
   - **On save:** trim, check the characters (printable ASCII, no whitespace) and check the length (no more than 2,560 bytes).
   - **The last 4 characters** are shown only for keys of 20 characters or more. The text of any exception is never shown.
   - **Storage:** `CRED_PERSIST_LOCAL_MACHINE`, a UTF-8 blob with no terminator, and no UserName. `CredFree` is always called and buffers are zeroed. The copy doesn't claim more protection than environment variables give.
   - **Remove** asks for confirmation in the page.
   - **Notice copy:** "then restart yolime" becomes "add it in Settings".
8. **`KeyTester` hosts are constants**, except that the custom endpoint test uses the validated https custom endpoint with its own key.
   - OpenAI and Groq: `GET /v1/models`.
   - Anthropic: `POST /v1/messages/count_tokens`, and only that.
   - The result is an enum; bodies are never logged or shown.

**Wizard and windows**
9. **The wizard opens only while `!FirstRunDone`.** The predicate lives in Core and is tested.
   - Closing the window counts as Skip.
   - After that, a missing key shows the setup-needed icon, a tooltip giving the reason, and a tray item, "Finish setup…".
   - **Step 3 (hold your hotkey):** the hook's `SuppressRuns` flag shows live feedback without starting a run.
   - **Step 4 (practice box):** it is the only one of yolime's own windows that dictation may paste into.
10. **Pasting into yolime's own windows.** `TargetSnapshot` gets an `IsOwnProcess` field, and the planner copies instead of pasting into yolime's own windows, except the practice box. The App passes the practice box's HWND as allowed. This stops UIA, context reads and SendInput from targeting yolime's own UI thread.
11. **Windows:**
    - one instance each, re-activated when opened again, and the reference dropped on Closed;
    - saves go through `holder.Update`;
    - all windows close before the host is disposed on Exit.
12. **Settings pages (five, not nine):**
    - **General:** hotkey, latch, mode cycling, mic and meter, sounds and cues, start with Windows.
    - **Keys.**
    - **Apps & words:** per-app starting modes, vocabulary.
    - **Overlay & history:** position, hide from capture, preview modes, retention, pause history, clear.
    - **Advanced:** prompts (edit and reset, with the newer-default notice), Prompt model, open settings.json, Reset yolime.
    - The STT provider, model and endpoint UI is cut: the spec keeps those in JSON.
13. **Mic:**
    - A saved device that has been unplugged falls back to the default, with a notice.
    - The default device keeps stream routing.
    - The meter stops when the window deactivates or minimises.
14. **Run key:** `"\"{Environment.ProcessPath}\""`. The page reads both `Run` and `StartupApproved\Run`, and shows "points to another copy of yolime" with a Fix button when the path differs.
15. **Reset yolime (Advanced):** confirmed in the page. It removes the credentials, the Run value and the data directory, then exits.
16. **Clear history** deletes rows and pending WAVs, checkpoints the WAL, clears `LastResult`, and waits until no run is in progress.
17. **Pause history:** no row and no WAV. The card hides Retry and Use raw when there is no history id (Use raw now requires one). The tray item has a checked state.
18. **Tray:**
    - The icon state comes from a Core `TrayState.From(...)`: idle, recording, paused, setup needed (with a reason), or elevated.
    - A one-time balloon when the foreground window is elevated (spec).
    - The label is "Retry last failed" (spec). Pause items show checked.
    - Left-click opens Settings until PR6, then History.

**Revised task order:**
1. Core: settings fields, the hold-key list with a reset notice, the FirstRun predicate, `TrayState`.
2. Core: `SettingsHolder` and its watcher.
3. Core: `KeySource`/`ICredentialStore`/`KeyTester`.
4. Core: `LastResult` and paste-last.
5. Core: history pause, clear, and the own-process planner rule.
6. App: the credential store, the Run key, and the tray (`RegisterHotKey`, menu, icons, tooltip).
7. App: the hook's capture mode, `SuppressRuns`, and live hook reconfiguration.
8. App: mic enumeration and meter.
9. App: `SettingsWindow` (five pages).
10. App: `WelcomeWindow`.
11. E2E.

## Decisions made during implementation

These are deviations from, or additions to, the rulings above. Each gives the decision, why, and what it costs if wrong.

- **KeyTestResult adds `NotSet`, `BadEndpoint` and `Malformed`.** The page needs "nothing to test", "fix the URL first" and "that isn't a key". A typed key is validated before it is sent. Cost: none.
- **A 429 from the key test counts as OK.** Rate limits are applied after the key is accepted. Cost: none on these APIs.
- **History pause is read once per run.** A run that started on the other side of a pause toggle is not kept, because toggling pause clears the last result. A paused run's recording is named `*.paused.wav`, and Housekeeping deletes such files instead of adopting them after a crash.
- **`LastResult` has a clear generation.** A run that started before a Clear (or a pause toggle) can't put its result back. Two reviewers found this race independently.
- **Paste-last doesn't mark the coordinator busy while it delivers.** The executor's gate serialises clipboard work. Cost: a hold started mid-paste waits for that paste.
- **Paste-last into a password field copies rather than refusing.** This is the existing planner rule for a focused password at delivery. Cost: the text is on the clipboard, never typed.
- **The tray menu runs its command after giving the foreground back.** Otherwise paste-last and retry would target yolime's hidden window.
- **Pause history in the tray follows settings.json.** A failed save shows a notice and leaves the check mark unchanged.
- **"Pause hotkeys" also pauses the paste-last chord.**
- **Lowering retention asks first and prunes at once.** Raising it does neither.
- **Key capture runs even while hotkeys are paused**, because the user is choosing a key, not dictating. A captured key whose key-up is missed stops being swallowed once it is physically up.
- **The Started notice names the hold key.** `HoldKeyReset` replaces it when the saved key was reset.
- **The tray's History item is grey ("coming soon") until PR6.**
- **The mic-fallback notice shows once per session.**
- **The wizard and Settings are separate code**, with some duplication: the key row and the mic meter. Cost: copy changes in two places.
- **E2E settings mark the wizard done unless `--test-first-run` is passed.** The wizard would otherwise cover the harness's target window.
- **Async button work goes through `Guarded`.** An exception escaping an async WinUI handler ends the app; E2E found two such crashes, a host-pinning throw in the key tester and a second dialog.

**Deferred (minor):**
- A page rebuild (after a key change or a hand edit) drops unsaved text in other fields.
- A UI save can overwrite a hand edit still inside the 300 ms debounce.
- A hand-edited, refused hold key resets silently on reload. At startup it shows the HoldKeyReset notice.
- High-contrast colours for the wizard's hold light, and help text at 0.75 opacity.
- 16 px tray dots upscale at 150 %.
- Clearing SuppressRuns while the key is held can start a hold on the next auto-repeat.
- Retry doesn't count as busy for paste-last.
- In E2E, paste-last's first dictation was copied in two of seven runs. The reason is now logged (`OutputNotPasted`).
- A toast banner appearing mid-hold discarded a recording in the Sandbox. Check in PR7 whether it does on a real desktop.
