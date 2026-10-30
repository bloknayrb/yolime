# yolime: a Windows-native hold-to-talk voice writer (Lemon-style)

## Context
Bryan wants his own version of heylemon.ai: hold a key, speak, and polished text appears at the cursor in any app. There are three modes: clean dictation, a structured AI prompt made from rambling speech, and a reply drafted from context. The repo starts empty.

Decisions already made by Bryan:
- **WinUI 3** on native C#/.NET, unpackaged, self-contained, win-x64.
- **Cloud STT.**
- **Every v1 mode:** dictation, prompt, reply, notch overlay, history.
- **Configurable hotkeys.**
- Our own name and copy; no Lemon branding or assets.

Reviewed adversarially from four angles: Windows platform, UI/UX, security/privacy, and product/latency/architecture. Changes that came from those reviews are folded in.

### Calls I made where there was no real trade-off
- **.NET 10 LTS, not 9.** .NET 9 reaches end of support on 10 Nov 2026.
- **Claude through plain `HttpClient`, not an SDK.** This matches the STT client and avoids the `Anthropic` / `Anthropic.SDK` NuGet confusion. It also gives exact control over `thinking`/`effort`. API details are checked against the claude-api skill when implementing.
- **Solid rounded overlay instead of acrylic.** Acrylic falls back on inactive windows and fights high-contrast mode.
- **One hold key, with the mode cycled by Shift, as the default.** Right Alt opens menus and is AltGr on some layouts, and Right Ctrl is missing on Copilot-key laptops. Separate per-mode keys stay available as an option.
- **"Tone sample" replaced by per-app style profiles.** These are more useful and come from the same mechanism.

## Repo & workflow
- `git init`, then a GitHub repo.
  - The initial `main` contains `.gitignore`, `README.md`, `.editorconfig`, `Directory.Packages.props` (central, exact pins), `nuget.config` (nuget.org only, with source mapping) and Dependabot.
  - `.gitignore` covers `logs/`, `*.wav` except `tests/fixtures`, and `.env`.
  - MIT licence (added when the repo went public; it started with none).
- Install with `winget install Microsoft.DotNet.SDK.10`. (Done: SDK 10.0.403 is installed.)
- The csproj is hand-written: `UseWinUI`, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `RuntimeIdentifier=win-x64`, `net10.0-windows10.0.19041.0`, with `packages.lock.json` and `RestoreLockedMode` turned on.
- Each PR: agent review (several angles for UI and security PRs), fixes, then merge. Before PR2, check whether Smart App Control is on, since it may block an unsigned exe that hooks the keyboard.

### PRs, in order
0. **`spike/overlay`**, throwaway. Prove that a borderless, topmost, `WS_EX_NOACTIVATE` WinUI window can be:
   - shown with `AppWindow.Show(false)` over a harness-owned target window, inside Windows Sandbox, without taking focus;
   - given a click-through state and a clickable error state, both without activating;
   - rendered without ever being activated, with no XAML first-activation glitch.

   Findings go into the PR4 design.
1. **`feat/core-pipeline`.** Core library and tests.
   - Streaming-shaped interfaces, settings and history schema versioning, versioned prompt files.
   - Pending-WAV safety, a content-free logger plus a per-stage timing log.
   - An E2E-only `--test-audio` path for Raw output to the clipboard.
2. **`feat/hook-inject`.** Hook and state machine, WASAPI recorder, ClipboardGuard (decisions live in Core), target tracking.
   - Safety guards: password fields, terminals, elevated windows.
   - Minimal tray (Pause, Exit), single instance, explicit-shutdown lifetime.
3. **`feat/llm-modes`.** Claude rewriter, modes as data, per-app profiles, adaptive Prompt structure.
   - A context provider with freshness and secret filters, and injection hardening.
   - Fallbacks, and the skip-LLM gate for short dictations.
   - Needs the Anthropic key before it starts.
4. **`feat/overlay`.** The notch UI: states, preview card, sounds, Narrator, high contrast, DPI and multi-monitor handling.
5. **`feat/onboarding-settings`.**
   - A first-run wizard, a settings window, Credential Manager storage and start-with-Windows.
   - The full tray menu and a "paste last result" hotkey.
6. **`feat/history`.** A history window with diff, retention, re-run and vocabulary capture.
7. **`perf/latency`.** Pre-warmed connections, streaming STT while the key is held, LLM streaming into the overlay preview, an optional warm mic, and silence trimming. Each change is measured against the PR1 timing log.
8. **`chore/publish`.** Publish script installing to `%LOCALAPPDATA%\Programs\Yolime`, a quoted Run-key path, and a README covering usage and known limits.

## Layout
```
src/Yolime.Core/   net10.0, no UI, unit-tested
  Hotkeys/HoldStateMachine.cs      hold / Shift-cycle / double-tap lock / abort rules
  Audio/IAudioSource.cs            chunked audio + levels; FileAudioSource for tests
  Stt/ITranscriptionSession.cs     PushAudio(chunk) + CompleteAsync; BatchTranscriptionSession (OpenAI|Groq, base URL)
  Llm/IRewriter.cs                 StreamAsync → IAsyncEnumerable<string>; ClaudeRewriter (HttpClient)
  Context/IContextProvider.cs      selection, clipboard (freshness/secret filter), foreground app
  Modes/ModeDefinition.cs          data: model, effort, prompt id, context sources, output policy, fallback
  Modes/Profiles.cs                per-app style (process → casual/email/code/terminal)
  Prompts/                         embedded versioned defaults; user overrides in %APPDATA%\Yolime\prompts\*.md
  Output/OutputPlanner.cs          paste vs clipboard vs pending-paste decision; terminal sanitising
  Clipboard/ClipboardPolicy.cs     save/restore/sequence/exclusion rules behind IClipboard
  Pipeline/DictationPipeline.cs    stages, timeouts, cancellation, single-run gate, timings
  Settings/ (schemaVersion + migrations)  History/ (Sqlite, PRAGMA user_version)  Logging/
src/Yolime.App/    WinUI 3: Native/{KeyboardHook, Injector, Win32Clipboard, Uia (FlaUI.UIA3), WindowStyles, ForegroundWatcher}
                   Audio/WasapiSource (NAudio)  Secrets/CredentialStore  Views/{Overlay, Onboarding, Settings, History}  Tray (H.NotifyIcon.WinUI)
tests/Yolime.Core.Tests/ (xUnit)   tests/Yolime.E2E/ (built with YOLIME_E2E)   tools/make-test-audio.ps1 (System.Speech fixtures)
```

## Behaviour

### Hotkeys and modes
- **Default: one hold key.** Onboarding detects the keyboard and suggests a key. Right Ctrl is the default; on Copilot-key or AltGr machines it suggests another.
  - Tapping Shift while holding cycles through Dictate, Prompt, Reply and Raw. The overlay shows the current mode until release.
  - Each app has its own starting mode, e.g. terminal and editor start in Prompt.
- **Optional bindings:**
  - Separate per-mode keys.
  - A double-tap of the hold key latches hands-free recording; a tap ends it.
  - If Alt is bound, the 0xE8 mask key keeps menus from opening.
- **Hook:**
  - `WH_KEYBOARD_LL` runs on a dedicated thread with a rooted delegate. The callback reduces each event to {configured key, Shift, Esc, OtherKey} before posting, so other VKs never leave the callback.
  - `LLKHF_INJECTED` events are ignored except in the `YOLIME_E2E` build.
  - The hook is reinstalled on resume and unlock, with a health check.
- **Hold rules:**
  - Capture starts at key-down. The overlay appears only after 300 ms; shorter holds are discarded and the WAV deleted.
  - Any other key, mouse click or wheel event aborts the hold.
  - While recording, `GetAsyncKeyState` is polled at about 100 ms to catch a missed key-up. If the foreground process changes, the recording is discarded.
  - Recording is capped at 2 minutes held, or 5 minutes latched, with a countdown, and is committed (not dropped) at the cap.
- **Cancel:**
  - Esc while recording cancels and is swallowed only then.
  - While processing, tapping the hold key cancels.
  - A run cancelled after STT keeps its transcript in history.
- **Busy:** a new hold during a run is rejected, and the overlay says what is in progress and how to cancel.

### Audio
- WASAPI shared mode, resampled to 16 kHz mono (`WdlResamplingSampleProvider`).
- Clear errors for the mic privacy setting being off or `E_ACCESSDENIED`, and handling for device changes.
- After 2 s of silence the overlay says "No audio: is the mic muted?". An RMS gate skips STT entirely and says "Didn't hear anything".
- Optional warm mic for Bluetooth headsets, which turns on the Windows mic indicator.

### No lost work
- The WAV goes into `pending\` and is deleted after STT succeeds, or on cancel or discard. Pending files older than 7 days are pruned.
- STT failure offers Retry from the overlay error card (clickable), tray, history, or a tap of the hotkey.
- LLM failure, refusal or `max_tokens`:
  - Dictate pastes the raw transcript.
  - Prompt and Reply put the raw transcript on the clipboard, show the error and offer Retry.
- The history row is written before injection.
- The "paste last result" hotkey and tray item are always available.

### STT
- Multipart `/v1/audio/transcriptions`, with the vocabulary sent as the `prompt`.
- Each API key is bound to its provider's fixed host. A custom base URL needs its own key and must be https.

### Rewrite (Claude, `HttpClient`)
- **Dictate:** Haiku 5.5, thinking disabled, effort low.
  - Rules: remove fillers, apply self-corrections ("no wait, Tuesday"), and honour spoken formatting ("new line", "bullet").
  - Keep the speaker's words, never answer or execute, and don't rewrite code or commands in terminal and editor profiles.
  - Fewer than ~12 words with no correction or filler cues skips the LLM and uses local regex cleanup.
- **Prompt:** model is a setting, default Haiku 5.5 at low effort; Sonnet 5.5 is chosen by evaluating both on about 20 fixtures.
  - Structure scales with the input: under about 40 words it is one clean paragraph; above that, only the sections that have content.
  - Coding profile: Task / Context / Requirements / Done when. Chat profile: Goal / Context / Constraints / Desired output.
  - Written in the user's voice, addressed to the AI. It never invents requirements, keeps identifiers, paths and code verbatim, and uses no XML in the output.
  - Selected code or an error message is included verbatim as context.
- **Reply:** Sonnet 5.5, low effort, with a `thinking` setting the API accepts for Sonnet; it is verified at implementation.
  - The app's style profile sets the tone.
  - Context is read after the modifier keys are up: UIA3 on an MTA worker with a 300 ms timeout. It falls back to Ctrl+C only outside terminals and only through ClipboardGuard. After that it uses the clipboard, but only if the clipboard changed less than 2 minutes ago.
  - Password-manager-flagged clipboard contents and secret-looking tokens are skipped (`sk-`, `ghp_`, `AKIA`, `xox`, or high-entropy text with no spaces).
  - The overlay shows "Replying to: '…' (selection | clipboard, N s old)" while the key is held, or warns that there is no context.
- **Injection hardening:** the transcript and context go in separate tags, with tag look-alikes removed from them. The context is declared untrusted data and is capped at about 8k characters. The output never ends with a newline. The context is stored with the history row.
- **Request shape:** no `temperature`, one shared `SocketsHttpHandler`, a timeout per stage, and streaming into the overlay preview.

### Output
- **Target:** the foreground HWND plus the UIA focused-element RuntimeId when the run starts. If either changes, the output goes to the clipboard with a 4 s "Copied, press Ctrl+V" notice.
- **Paste:** Ctrl+V once all physical modifiers are up.
- **Editable check:** the paste goes ahead unless there is positive evidence the field isn't editable; Chromium often misreports.
- **Pending paste:** for selection-based Reply with a read-only selection, the overlay says "Click where the reply should go" and pastes the next time focus lands on an editable field within 10 s.
- **Terminals** (ConsoleWindowClass, CASCADIA, mintty, Code.exe terminal):
  - Reply output goes to the clipboard only.
  - In every mode, a leading `!`, `/` or `#` and trailing newlines are stripped.
- **Password fields** (UIA `IsPassword`): the run is refused and no history row is written.
- **Elevated foreground:** the tray icon dims, plus a one-time toast.
- **ClipboardGuard:**
  - retries and backs off when opening the clipboard;
  - saves only formats that can be round-tripped safely;
  - excludes the brief paste copy, the restored original and any Ctrl+C capture from Win+V history and cloud clipboard sync;
  - restores after about 800 ms, only if the sequence number is still ours;
  - leaves "copied, not pasted" results in Win+V history.
- **Preview:** holding Shift at release, or the per-mode "preview long output" setting, shows an expanded card. Enter inserts, Esc discards and C copies. The keys are captured by the hook, so focus isn't stolen.
- **Undo:** "use raw instead" is offered for 5 s after Done.

### Overlay (the notch)
- **Position:** top centre of the foreground monitor's work area by default (the notch Bryan chose). Near the caret or bottom centre are options. DPI-aware.
- **Appearance:** a solid rounded window. System colours in high contrast. Respects reduced animations and text scaling, and follows the light/dark theme.
- **Click-through:** on, except for the error and preview cards.
- **Screen sharing:** optional `WDA_EXCLUDEFROMCAPTURE`.
- **States:**
  - Listening: mode icon + label, elapsed time, level meter, reply-context snippet.
  - Transcribing / Writing: "Writing for Slack…", elapsed seconds, raw-transcript or streamed preview.
  - Done: "→ Slack · 42 words · 1.9 s", for about 1 s.
  - Error: message plus Retry.
- **Sounds and Narrator:** optional start/stop/error sounds, on by default. Narrator announcements via `RaiseNotificationEvent`.

### Lifetime, onboarding, settings
- **Lifetime:** `DispatcherShutdownMode=OnExplicitShutdown`. Single instance via `AppInstance.FindOrRegisterForKey`; arguments on a redirected activation are ignored.
- **First-run wizard:**
  - key status with a Test button (shows the OpenAI key as found in the environment);
  - mic picker with a live meter and a privacy-settings link;
  - "hold your hotkey" test;
  - practice box;
  - pin-tray hint and "yolime is running" toast.
- **Tray:**
  - Left-click opens History.
  - Menu: Paste last result, Retry last failed, Pause hotkeys, Pause history, Settings, History, Exit.
  - Icon states: idle, recording, paused, setup needed, elevated.
- **Settings UI:** hotkeys (capture distinguishes left/right modifiers and lone modifiers), mic, start with Windows, keys (PasswordBox, never redisplayed), per-app profiles and starting modes, vocabulary, overlay position, sounds, preview, retention.
- **Advanced:** providers, models and effort live in JSON; prompts open in an editor and offer "reset to default" plus a notice when a newer default exists.
- **Keys:** Credential Manager, with environment variables as fallback. Recommend project-scoped API keys with spend caps.

### History
- **Rows:** SQLite storing time, mode, process name (never window titles), raw text, context, output, per-stage status and timings.
- **Window:** a list (time, mode, app, first line, status), a raw vs output diff, Copy, Re-insert (into the window that was in front before History opened), Re-run as another mode, Retry from pending audio, and Add word to vocabulary.
- **Retention:** 30 days by default, with a Clear button. Audio is never stored.

### Logging
- Logs only state transitions, timings, character counts and HTTP status codes, never content, headers or key codes. A unit test asserts this.
- 7-day rotation. No full crash dumps.

### Build hygiene
- All test switches (`--test-audio`, accepting injected keys, the fixture source) are compiled only under `#if YOLIME_E2E`. `YOLIME_LIVE` is read only by test projects.

## Latency / cost targets (estimates; verified with the PR1 timing log)
| Mode | As first drafted | Target after PR3 + PR7 |
|---|---|---|
| Dictate | 1.5–5 s | 0.4–1 s |
| Prompt | 6–18 s | 2–4 s (first preview text < 1.5 s) |
| Reply | 4–13 s | 1.5–3 s |

Cost at about 100 uses a day: about $9–12/month with gpt-4o-transcribe, about $4 with Groq STT. The biggest factor is leaving thinking at its default, which can more than double the cost and add seconds.

## Verification
- **Unit tests** (`dotnet test`):
  - Hold state machine: auto-repeat, Shift cycling, double-tap latch, other key / mouse / foreground-change aborts, under-300 ms discard, injected events, missed key-up watchdog.
  - Output planner: terminal sanitising, password refusal, target changed, pending paste.
  - Clipboard policy: sequence-number guard, exclusion formats.
  - Context filters: stale clipboard, secret patterns.
  - Prompt assembly: escaping, profile choice, adaptive structure.
  - Request shape: no `temperature`, the expected thinking/effort values (mocked handler), and LLM refusal/failure fallbacks per mode.
  - Pending-WAV lifecycle, settings and history migrations, and the logger containing no content.
- **Live smoke** (`YOLIME_LIVE=1`): OpenAI STT, then Claude per mode. A fixture eval of about 20 cases per mode checks for no preamble, no invented constraints, identifiers kept verbatim, self-corrections applied, and short prompts staying a paragraph.
- **E2E** (`YOLIME_E2E` build, driven by me): this goes through the **real hook**, with the real hotkeys sent by SendInput and `FileAudioSource` in place of the mic. It runs only inside Windows Sandbox, never on Bryan's desktop, against a target window the harness launches and kills itself. Never Notepad: Windows 11 Notepad restores Bryan's real tabs.
  - In the harness-owned target window, assert that:
    - `GetForegroundWindow()` is unchanged before, during and after the overlay appears;
    - the overlay HWND has `WS_EX_NOACTIVATE`;
    - the target isn't in menu mode;
    - the text read back through UIA is non-empty, with no preamble, and the history row shows the LLM stage OK.
  - Prompt: structure matches the input length.
  - Reply: the clipboard-context flow lands in the field without overwriting it.
  - Terminal: Reply goes to the clipboard and nothing is executed.
  - Password field: the run is refused.
  - Clipboard: text and a bitmap present beforehand both survive; a copy made during the restore window isn't overwritten.
  - Timing log: per-stage latencies are recorded against the targets.
- **Human check (Bryan, once):** the real mic and keys in Notepad, Slack, a browser, Outlook and Claude Code in Windows Terminal, plus the first-run wizard.

## Needs from Bryan
- An Anthropic API key, preferably a workspace key with a monthly cap, before PR3. The existing OpenAI key covers STT.

## Decisions after approval

Bryan chose these on 2026-10-28, and they override the text above where they differ:
- **Cancelling during processing keeps the recording.** A tap that cancels a run *after* recording has ended keeps the audio and the history row, so it can be retried. Discarding *during* recording (Esc, a short tap, other input) still deletes.
- **Terminals strip a leading `!` and `#`, but keep `/`.** Dictated slash commands (in Claude Code, for example) work. Newlines are still neutralised, so nothing runs by itself.
- **The mic opens at key-down, as specified above.**
