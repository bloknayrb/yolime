> **Status (2026-11-05): on hold, not approved.** Bryan paused this plan before approving it ("lets hold off on this for now. save the plan and commit it for later"). It went through four review angles on v1, whole-plan reviews of v2 to v5, and a final consistency check. Before building, re-read it against `main`, since the line numbers will drift.

# yolime voice commands, tier 1 (plan v5)

## Context

Bryan wants yolime to take spoken commands as well as dictation. What prompted it, in his words: "i got a phone call and wanted to be able to quickly write something down and would've liked to be able to say "open a new scratchpad in tandem" or "open a new blank file in notepad"". His other examples were "open my browser and go to xyz website", "open warp terminal and start a new claude code session in the tandem directory", and "arrange app 1 and app 2 so they are side by side on my screen".

Tier 1 means fixed OS actions only: no screen reading, no clicking inside apps, no screenshot loops.

### Bryan's decisions (2026-11-04)
1. **Trigger: a separate command hold key**, set in Settings. The default is **Right Shift**; Pause is the alternative. He chose "Right Shift, as planned", accepting four costs: the mic opens briefly on capitals, a Bluetooth headset drops to its hands-free profile, his active Filter Keys shortcut fires on holds of 8 s or more, and the card flashes on slow Shift selections.
2. **Commands only open things, everywhere** ("Open only, everywhere"). On Notepad he said: "Let's limit it to opening; I can add the text myself after".
3. **Terminal commands run whatever was heard, with no preview** ("Anything, no preview").
4. **Names map to folders** through his Warp tab configs, plus a small yolime alias list.
5. **Dictating early waits for the command** ("Wait for it, then paste"). A dictation held before the command's window is up records straight away and is delivered after the command.
6. **Where an early note goes.** Asked whether a note should go anywhere or only into certain windows, he answered "My bkvault obsidian vault". He then chose "Vault instead of copying":
   - the note **pastes** only into a Notepad, Tandem or Warp window the command opened;
   - in every case that would otherwise only copy it, the note is **saved as a new note in BKvault's Inbox**.
7. **The program must be spoken** ("Yes, program must be spoken"). A terminal command's program must appear in what he said. Otherwise the command fails with "Say the program's name". This extends to chained commands ("Every program spoken"): every program joined by `&&`, `||`, `;` or `|` has to be spoken.

### Calls I made (check these)
- **Command is a run kind, not a mode.** It is a separate `HoldKind`, not a fifth `Mode`. A command run carries the placeholder `Mode = Raw` from key-down, so no context is read. Every site that reads the mode also checks the kind.
- **An early note is part of the command run.** One run, one coordinator slot, one overlay card. A second concurrent run broke the coordinator, the foreground rule and the overlay in review. The note's exact mechanism is designed and reviewed as the first step of PR 3, against the requirements and acceptance tests below. I stopped writing that mechanism into this plan because each round's Highs were bugs in my sketches of it.
- **Commands commit only after an 800 ms hold.** A shorter hold is discarded silently. Nobody speaks a command faster than that, and the limit stops slow Shift use and room audio from committing commands.
- **Retry** is offered only when no step has run, and only from the button; a key tap never retries a command. History never re-runs a command.
- **Planner:** one structured-output call to Haiku 5.5, with thinking disabled and low effort. Sonnet can be chosen in Settings.
- **Only names reach Claude:** alias names with their kind, Warp config names, app names, registered browser names, and an indexed list of open windows by process name. Window titles are opt-in and off by default. Every list is marked as untrusted data, and code binds each returned field to what was sent.
- **A command may start from a password field.** A note whose opened window is a password field is saved to the vault, not pasted (N5).
- **Commands are on by default.** A one-time notice after the update names the command key.
- **"Go to xyz" with no domain** becomes a Google search.
- **`arrange`** works only on windows that are already open.
- **A plan that mixes real steps with `unsupported`** fails before anything runs.
- **Notes are always Dictate mode.** The Done hint names the mode ("Hold Right Ctrl to dictate into it").
- **Notes go to `BKvault\Inbox`** as `Note YYYY-MM-DD HHmm.md`, with a `-2`, `-3` suffix as needed so nothing is overwritten.
  - Format: UTF-8 without a BOM, LF line endings, frontmatter `created:` and `source: yolime`, matching the existing Inbox notes.
  - The file is written to a temp file *outside* the vault, then moved in without overwriting. The vault syncs and shows unsupported files.
  - The setting holds "auto". That resolves at each use to the open vault in `%APPDATA%\obsidian\obsidian.json`, plus `\Inbox`.
  - If the folder is missing, the note is copied to the clipboard and the card says so.
  - Deleting the History row doesn't delete the vault note.
- **The note pastes into the last window opened in a Notepad, Tandem or Warp step.** If there's no such step, it goes to the vault.
- **Office apps wait for Ctrl to come up.** `launch_app` for Office executables (Word, Excel, PowerPoint, Outlook) waits until Ctrl is released, so with a note being held, that means until the note ends. Otherwise the held dictation key makes them offer Safe Mode. The card's step line reads "Opening Word when you let go of Ctrl". Test: `Command_OfficeLaunch_WaitsForCtrl` (Core, with a fake executor). Other apps launch immediately.
- **A command retry carries no note.**
- **Considered, not adopted:** opening the mic late for Right Shift (decision 1); commands writing text (decision 2); pasting notes into other apps (decision 6).

## Design

### Run kind and plumbing
- `enum HoldKind { Dictation, Command }` (Enums.cs) is carried on `StartCapture`, `RunRequest`, `RunResult`, `HistoryRow`, `RunTiming`, `OverlayView` and `Notice`.
- A command's `StartCapture` holds `Mode.Raw`, so `ReadContextIfNeeded` (CaptureCoordinator.cs:432) never runs.
- **Sites that branch on the kind before the mode:**
  - `Notices.For`, and the Busy and Cancelled copy.
  - The RunFinished card, and `OverlayModel.Listening`'s title.
  - `OverlayController.Glyph`. The button-label cache key and the Retry key label (OverlayController.cs:246, 316-323) also include the kind.
  - `OverlayModel.Tap()` and `HotkeyTapped`: command cards never take a tap.
  - `RunRetry.IsRetryable` (OverlayRules.cs:25).
  - `HistoryStore.LatestRetryable` (HistoryStore.cs:149) excludes commands.
  - `HistoryItem.From` (HistoryView.cs:44-62) gives commands no reinsert, rerun or audio retry.
  - `CaptureCoordinator.RetryAsync` (:312-339) refuses command rows.
  - `LastResult.Record` (LastResult.cs:35-51) skips command results but records a pasted note.
  - The password refusal in `RunAsync` (:479-497) applies to dictation only.
  - History's Raw filter adds `kind='Dictation'`, and a Commands filter sits outside the `Mode` index mapping (HistoryWindow.cs:72,222).
  - `AppOf` and `RowProfile`.
  - `timing-report.ps1` groups by kind.
- **Untouched:** the `Mode` switches, `ToneSounds._cues` and the `Enum.GetValues<Mode>()` lists.

### Two hold keys
- **Settings.**
  - `CommandsEnabled`: default on, but forced off until PR 6.
  - `CommandKeyVk`: default Right Shift (`0xA1`). It must pass `HoldKeyRules.Judge` and differ from `HoldKeyVk`. A clash moves it to the next allowed key with its own code (Right Shift dictation key → Pause). Settings states that in one line.
  - Both pickers offer Swap.
- **HookReducer** takes `(holdVk, commandVk)`. The command check comes before the Shift mapping (HookReducer.cs:22-23), producing `CommandDown` and `CommandUp`.
- **Machine rules** (requirements; the PR 1 tests cover each one):
  - The kind is recorded at the Idle press. Each key keeps its own physical-down state, its own last-tap time (so there's no cross-key latch), and its own swallow state.
  - A key-up is swallowed only if its key-down was. A key-up is classified the way its key-down was.
  - The command key has no latch.
  - **The watchdog never marks or polls the command key while it is acting as Shift.** A swallowed key reads as up (HoldStateMachine.cs:106-107).
  - The Right Shift preview gesture during a dictation still works.

| State | Dictation key | Command key | Other input / Esc |
|---|---|---|---|
| Idle | dictation hold | command hold | — |
| Dictation hold | — | Right Shift: acts as Shift (cycles; preview gesture). Pause: other input | as today |
| Command hold < 300 ms | discards the command, starts a dictation hold (keeping its press time) | — | discards silently |
| Command hold 300–800 ms | discards the command, starts a dictation hold | release: silent discard | discards silently; Esc discards with a short "Command cancelled" |
| Command hold ≥ 800 ms | **PR 1:** other input (discards silently). **PR 3:** the roll, per N2b | release: commits | Shift swallowed, never cycles or previews; other input discards silently; Esc as above |
| Dictation processing | as today | hold ≥ 300 ms, not voided by other input (`_pressVoided` carried over): Busy, "A dictation is still finishing"; tap: nothing | as today |
| Command processing | **PR 1:** as today's ProcessingPressed (tap: nothing; hold: Busy). **PR 3:** the note, per N1–N4 | tap: cancels the command; hold: Busy ("A command is still running") | any key or click voids a pending command tap |

The table uses roles, so it applies unchanged after a Swap. Esc during a command hold is swallowed.

- **In PR 1, Busy is today's card-replacing notice** (OverlayModel.cs:243), with copy that depends on the kind and names the right key. PR 3 turns Busy into a sub-line on command runs (N7).
- **PR 1 also installs the mouse hook during command processing** (HookThread.cs:493), so that a click voids a pending command tap.
- **PR 1's watchdog covers the command key**, except while it acts as Shift.
- The rest of N8 belongs to PR 3.

- **HookThread and AppHost.**
  - `RefuseHolds` (HookThread.cs:373) refuses `CommandDown`.
  - `_capturePid` and `_switch.Reset` (:379-385) handle `CommandDown`.
  - `ApplyReconfigureIfIdle` (:229) checks both keys.
  - The Settings trigger (AppHost.cs:383) includes the new settings.
  - `PreviewCapture` (:79) and `UserKeyPressed` (:365) ignore the command key.
- **Noise.**
  - Command holds discarded by other input show no notice.
  - For the command kind, these start when the hold is shown at 300 ms: the UIA snapshot, the warm-up, the Claude-key check and the context snapshot.
  - The mic still opens at key-down.

### The note (decisions 5 and 6): requirements and acceptance tests
PR 3 opens with a short note-mechanism design. It gets its own review (correctness and UX) before any code, and it must meet these.

**Requirements**
- **N1. It belongs to the command run.**
  - A note shares the command run's coordinator slot and overlay card.
  - It has its **own** id for its History row, its WAV and its timing row. `run_id` is UNIQUE (HistorySchema.cs:15), WAVs are named by id (PendingAudioStore.cs:17), and `_startSnapshots` is keyed by id (CaptureCoordinator.cs:498).
  - It is cancelled by its own token, never by the command's. Cancelling or failing the command still transcribes and delivers the note.
- **N2. Nothing at the start is lost.**
  - Capture opens tentatively at the dictation key-down during command processing. It is dropped on a tap, and on other input before 300 ms.
  - The note's start cue plays when audio actually flows (as `PlayStartWhenAudioFlows` does).
  - A press that lands just as the command finishes is never lost. It continues as an ordinary dictation hold with its original press time.
  - Rolling from the command key onto the dictation key loses neither the command's tail nor the note's start.
- **N3. It can be stopped and is bounded.**
  - Esc discards a note.
  - Other input in its first 300 ms discards it.
  - Lock and sign-out discard it, as `SessionEnded` does.
  - The usual hold cap applies, with a concurrent reader, so audio is never silently dropped (ReadyMicDevice.cs:239, 319).
  - Every error path closes its capture.
  - Other input discards a note at any time, as in Recording (HoldStateMachine.cs:261-262).
  - Foreground changes never discard a note. That rule alone is turned off.
- **N2b. The roll.** When the dictation key goes down during a command hold of 800 ms or more, the command commits only once that note passes 300 ms or Shift is released. Other input before then discards both silently, so Ctrl+Shift+T and similar never reach the planner.
- **N2c. Overlapping capture.** PR 3 may change `ReadyMicDevice` so that a stopping hold and a new hold overlap. Today `Start()` ends the previous hold at once (ReadyMicDevice.cs:64), and the `StartAt` cut (:278) would drop the command's tail.
- **N2d. A late press.** When a press becomes an ordinary dictation as the command ends, the start snapshot is taken at the moment it converts, not at the original press.
- **N4. One note per command.** A further hold gets a sub-line on the card ("Still opening Tandem: hold again in a moment"), not a notice that replaces the card.
- **N5. Delivery.** Once the steps have finished and the note hold has ended, the note is transcribed in Dictate mode. Then:
  - **Paste** only if all of these hold:
    - the command succeeded;
    - the command's step was `open_notepad`, `tandem_scratchpad` or `warp_open`;
    - the window it opened is in front, identified as a new HWND of the expected exe (or, for single-window apps, that exe's window that reached the foreground after the step);
    - for Notepad and Tandem, P6 and P2 passed.

    The target comparison is by window only. Its start snapshot is replaced with the opened window's snapshot, with the focus id cleared.
  - **Order of decisions.** `OutputPlanner` decides first; only a Paste verdict is delivered. **Any other verdict** (Copy, ClipboardOnly, own-window, elevated, not-editable, **or a password Refuse**) saves the note to the vault instead, so the clipboard is never written. The note wasn't spoken into a password field, so it isn't discarded. Terminal chords still apply when pasting.
  - **Otherwise save to the vault:** a new `.md` in the notes folder (see "Calls I made"). The card says "Saved to <folder name> · 23 words".
  - **If the folder is unavailable, copy it,** with a reason.
  - **Retrying a note** (History, or the tray's retry-last) goes through the same rules. If the opened window is gone, it saves to the vault, never to the clipboard.
  - **Warp:** a note pastes only once Warp's shell is ready. That's a P1 question; if it can't be checked, the note goes to the vault.
- **N6. Persistence.** The note's row and WAV are written when its hold ends. `RunResult` carries a `NoteResult` (where it went, how many words) for the Done or error card.
- **N7. Overlay.**
  - The card shows the note: "Note · recording" with the level meter, then "Note · waiting for Tandem", then "Note · writing", with the step line kept.
  - Busy states are sub-lines. While a command run is on screen, `OverlayModel`'s `NoticeOnly` (OverlayModel.cs:243-244) becomes a sub-line rather than replacing the card. The level meter (`Level`, :201; OverlayController.cs:242) is shown in the note state.
  - The error tone waits until the note hold ends.
- **N8. HookThread.** The mouse hook (HookThread.cs:491-495) stays installed during command processing. The watchdog (:321) and `HotkeyInput`'s physical-key state (HoldStateMachine.cs:10) cover both keys.

**Acceptance tests** (Core, plus Sandbox E2E with a stand-in exe and a fake planner whose delay can be set):
- `Note_DuringPlanning_PastesIntoStandIn`. The stand-in counts as a Notepad-class target under an E2E-only switch.
- `Note_GeneralApp_SavedToVault`. The notes folder is a temp dir.
- `Note_CommandFailed_SavedToVault`.
- `Note_CommandCancelled_SavedToVault`.
- `Note_FolderMissing_Copied`.
- `Note_RollFromCommandKey`: the command's tail and the note's first word are both present. This is a Core test with a timestamped fake mic, because E2E's `FileMicDevice` has no capture timestamps.
- `Note_RollThenCtrlShiftShortcut_DiscardsBoth`.
- `Note_PlannerCopyVerdict_SavedToVault`, and `Note_Retry_NeverClipboard`.
- `Note_PressAsCommandEnds_BecomesDictation`.
- `Note_Esc_Discards`.
- `Note_TypingRightCtrlShortcut_DoesNotStartNote`.
- `Note_Lock_Discards`.
- `Note_Cap`.
- `Note_SecondHold_SubLine`.
- `Note_PasswordInOpenedWindow_SavedToVault`.

### Planner (Core, `Llm/CommandPlanner.cs`)
- **Request.** One streaming structured-output request (`output_config.format: json_schema`).
  - The SSE text parser (ClaudeRewriter.cs:95-139) is pulled out into a shared `ClaudeStream`, and the JSON is parsed once at the end.
  - Thinking comes from `ModeCatalog.ThinkingFor(model)`. There are no `fallbacks`, so a refusal is `PlanFailed`.
  - Low effort, `max_tokens` 1024, no temperature or prefill.
  - It uses the pooled `HttpClient`, `HostPinningHandler`, the key source and the 429/529 retry.
  - Timeouts: 6 s to the first token, 15 s total.
- **Schema.** Byte-stable, with no dynamic enums: `{asked_to_write: bool, steps:[Action]}`.
  - Every object has `additionalProperties:false`, an `action` const discriminator, and all properties required. Optional values are typed `["string","null"]`.
  - The actions:
    - `open_url{url, browser|null}`
    - `launch_app{app}`
    - `open_notepad{}`
    - `tandem_scratchpad{}`
    - `warp_open{place, command|null}`
    - `arrange{windows:[int], layout: left_right|top_bottom|maximize}`
    - `unsupported{reason: no_such_app|no_such_place|not_tier1|unclear, name|null}`
- **Context snapshot.** Built at 300 ms, kept with the run, and rebuilt on retry. It holds:
  - aliases (name and kind) and Warp config names;
  - app names: **only** Start Menu `.lnk` files whose target is an `.exe`, plus packaged AUMIDs from `shell:AppsFolder`, deduplicated, at most about 400, plus a synthetic "default browser";
  - registered browsers (`StartMenuInternet`);
  - open windows as `[i] process display name` in Z-order, with titles only when opted in.

  Each list goes in its own nonce-tagged untrusted block. Control and format characters are stripped and lengths are capped. `PromptBuilder.Defuse` (Llm/PromptBuilder.cs:95) is extended to cover these blocks. The transcript is hardened as it is now, and the vocabulary is included.
- **System prompt.**
  - Only these actions; when unsure, return `unsupported`.
  - A request to write text sets `asked_to_write` and never becomes a step.
  - `command` is null unless a command was spoken. It uses the program names exactly as spoken (`claude`, not `claude-code` or `npx claude`), because every program must appear in the speech.
  - `url` is a full URL or a Website alias name.
- **Binding** (`CommandPlan.TryParse`). Any failure fails the plan.
  - `app` must be a sent app name or an App alias.
  - `browser` must be null or a sent browser.
  - `place` must be a Project-folder alias or a Warp config name.
  - `url` must be a Website alias, or pass the `open_url` checks.
  - `windows` must be valid indices, two for left/right and top/bottom, one for maximize. They are re-validated before use.
  - **Every program in `command` must appear in the transcript** (decision 7). Otherwise the error is "Say the program's name".
    - A `command` containing control characters (newline, CR, tab, other Cc/Cf) is refused. So is one containing anything that can start a second program inside one segment: a lone `&` (anything other than `&&` or a leading call `&`), `$(`, a backtick, `<(` or `>(`. These fail as "Say the program's name".
    - Split on `&&`, `||`, `;` and `|`.
    - In each segment, the program is the first whitespace token, with quotes stripped, a leading `&`, `.\` or `./` removed, reduced to its basename, and `.exe`, `.cmd`, `.bat` or `.ps1` dropped.
    - Each program is compared case-insensitively against the transcript's words. The transcript is the one before `Defuse`, split on non-word characters.
    - Consequences:
      - `cd x && y` needs both `cd` and `y` spoken;
      - quoted or path-like programs that don't reduce to a spoken word fail, which is the safe outcome;
      - STT spellings ("NPX", "UV") are helped by the vocabulary list.
  - `unsupported` can't be mixed with other steps.
  - Invalid JSON, a refusal, `max_tokens` or a timeout is `PlanFailed`.
  - An action this build can't execute fails with "This build can't do that yet".

### Running a command
`CommandRunner` (Core) runs the steps in order behind `ICommandExecutor` (App, `Native/CommandExecutor.cs`), inside the pipeline run. It stops at the first failure, and the command-key cancel stops the remaining steps and any wait.

- **`launch_app`**
  - Resolution order: App alias, then the `.lnk` file's **target exe** (its arguments are ignored), then an AUMID. It runs only the resolved target; anything else is NotFound.
  - A folder target is refused.
  - "default browser" resolves through `AssocQueryString` on `http`.
- **`open_url`**
  - The URL is either a Website alias value or the result of `Uri.TryCreate` (absolute). Either way it must be `http`/`https` with no user info, and it is passed on as `AbsoluteUri`.
  - If `browser` is set, that browser's exe is started with `ArgumentList`. Otherwise ShellExecute opens it.
- **`open_notepad`** runs `%SystemRoot%\System32\notepad.exe`.
- **`tandem_scratchpad`** uses its own loopback `HttpClient`, with no proxy and no redirects.
  1. `GET :3479/health`, then confirm the server is Tandem's, using whatever P8 finds. If it isn't: "Something else is using Tandem's port (a dev server?)". If no `tandem-desktop.exe` window exists: "Tandem is running without its window".
  2. If Tandem is running: POST `/api/scratchpad` with an empty body, `Content-Type: application/json` and `Origin: http://127.0.0.1:3479`, then `SetForegroundWindow` on the existing window. Never relaunch it to raise it.
  3. If Tandem isn't running: launch `tandem-desktop.exe`, poll `/health` for up to 10 s (cancellable, with the card saying "Starting Tandem…"), then POST.
- **`warp_open`**
  - Resolve the place, then check `Directory.Exists`.
  - Write `tab_configs\yolime-<32 hex>.yolime-tmp`, then rename it to `.toml`. All strings use real basic-string escaping. The file has `directory`, plus `commands = [cmd]` only when there is a command.
  - Open `warp://tab_config/<key>?new_window=true`, where `<key>` comes from P1.
  - **Delete the file as soon as Warp's window has appeared**, or after 10 s at most. At startup, sweep `^yolime-[0-9a-f]{32}\.(toml|yolime-tmp)$`. The config reader excludes these files.
  - The P1-B fallback pastes the command and presses Enter. Enter is sent only if the paste returned `Pasted` into that Warp window and it is still in front. It waits until no hold is active, for up to 10 s. This path is reachable from commands only.
- **`arrange`**
  - It acts only on snapshot windows, on the monitor under the window that was in front at release.
  - Each window is restored with `SetWindowPlacement` (`SW_SHOWNOACTIVATE`, workspace coordinates) and sized to the work area, corrected by `DWMWA_EXTENDED_FRAME_BOUNDS` and in physical pixels. It is placed with `SetWindowPos(NOZORDER|NOACTIVATE|ASYNCWINDOWPOS)`, then read back.
  - Hung windows and elevated windows are skipped and reported.
  - For `ApplicationFrameHost` windows, the frame is moved.
  - If an app's minimum width wins, the result is reported as partial.
  - The P/Invokes move into User32.cs.

### Command retry
Retry is offered only when no step has run: STT failed, `PlanFailed` from a timeout or the network, or a cancel before any step. It is only the error card's **Retry** button, which calls its own `CaptureCoordinator.RetryCommandAsync`. That re-transcribes if needed, rebuilds the snapshot, plans and runs.

### Overlay and sounds
Key names in the card text below ("Right Shift", "Right Ctrl") stand for the configured command and dictation keys.
- **Listening.** A "Command" card with a lightning glyph and its own tone: two rising notes, pitched away from the 1200 Hz ticks.
- **Planning.** "Planning…", with the heard transcript in Detail. It's visible, but not a confirmation step.
- **Running.** One line per step, which names the cancel key: "Opening Warp in Tandem · runs claude · tap Right Shift to cancel".
- **Done.**
  - Examples: "Opened a Tandem scratchpad · note pasted (23 words)", "Opened Notepad", "Opened Warp in Tandem · ran claude", "Opened github.com · note saved to Inbox".
  - If `asked_to_write` is set and there's no note, it adds "Hold Right Ctrl to dictate into it" and a **Copy what I said** button.
  - Done cards that name a terminal command, or carry a hint or button, stay up for 4 s and can be clicked.
- **Behind (P3-B).** "Tandem opened behind your window", with **Show it**. Whether a click on the NOACTIVATE overlay grants raise rights is checked in P3.
- **Errors.**
  - "No app called 'X'" → **Open Settings → Commands**.
  - Key missing or rejected → **Open Settings → Keys**.
  - Timeout or network → **Retry**.
  - Unclear → "Didn't catch a command: say it again".
  - "Say the program's name".
  - `not_tier1` → "Can't do that yet. Commands can open apps, websites, Notepad, Tandem scratchpads and Warp, and arrange open windows".
  - Partial results.
  - With a note, each card also says where the note went.
- **Cancelled.** The copy never suggests retrying from the tray.
- **Narration and the UI tour.** Narration is polite for steps and urgent for errors. The tour's `TourStates` gain Command, Planning, Step, Note, Partial, Behind and Error.

### Settings: Commands page (`SettingsPage.Commands`, PR 6)
- An on/off switch, and a key picker (reusing `CaptureKeyAsync`, SettingsWindow.cs:213) with Swap and the clash line.
- **Right Shift notes:** capitals briefly open the mic, and headsets switch profile. The Filter Keys and Sticky Keys shortcut status (`SPI_GETFILTERKEYS`, `SPI_GETSTICKYKEYS`) is shown with a link to the setting.
- **Notes folder:** shows "Automatic", which resolves to the open vault's `Inbox` (today that's `%USERPROFILE%\Obsidian\BKvault\Inbox`). A picker overrides it.
- **Aliases** of three kinds: **Project folder** (for Warp), **Website** and **App**. Each is validated on save (the folder exists, the URL is http(s), the app resolves) and stored as `Dictionary<string, AliasEntry>`.
- **Warp tab configs,** read-only, with Refresh. The reader accepts basic and literal strings, the top-level `name` and `[[panes]] directory`. It skips `[params]`, `startup_config.toml` and `yolime-*`.
- **Try a command** plans without running anything, and writes no History or timing row. It says it sends the phrase and the lists to Anthropic, and shows a searchable list of the app names it sends.
- **Advanced:** the planner model, and "send window titles", with a warning that web pages set titles and so can steer plans.
- **Notice:** a one-time "New: hold <command key> for commands. Settings → Commands."

### History, logging, timing
- **History schema v4** adds `kind TEXT NOT NULL DEFAULT 'Dictation'`, `plan_summary TEXT` and `command_run_id TEXT` (on note rows).
  - `output` stays null for commands.
  - Command rows offer Copy transcript only. Note rows record where the note went (pasted, vault or copied). Retrying one follows N5's retry rule, never the clipboard.
  - An older build shows command rows as Raw. That's accepted: one user, with auto-update.
- **Logs stay content-free.** New enums `CommandAction`, `CommandOutcome`, `CommandStepError` and `NoteDestination`. `LogEvent.CommandStep` carries tags and milliseconds only. `NoStringFieldsTests` and `NoContentInLogsTests` cover them.
- **Timing.** `RunTiming` gains `Kind`, `PlanFirstTokenMs`, `PlanDoneMs`, per-step milliseconds and the note's `WaitMs`.
- **Expected latency** from release to the window appearing: about 3–4 s warm, and 5–12 s when Tandem is closed or for the first schema of the day.

## PR sequence
Commands stay off until PR 6. Each PR's E2E turns them on with an E2E-only switch.

| PR | Branch | Contents | Gated on |
|---|---|---|---|
| 1 | `feat/command-key` | HoldKind plumbing, the second key in the reducer, machine and HookThread, the key table's PR 1 cells (note cells behave as today's ProcessingPressed), the 800 ms commit, unit tests | — |
| 2 | `feat/command-planner` | Planner, schema, binding (including decision 7's tokenizer), hardening, aliases, Warp reader, context snapshot, the P4b fast path if needed. FakeHttp tests and a `YOLIME_LIVE` smoke test (P4a/P4b, `command: null`) | P4a |
| 3 | `feat/command-run` | **Note-mechanism design and its review first.** It owns the note's key-table cells, N1–N8 and any `ReadyMicDevice` change. Then the pipeline branch, CommandRunner, the executor for `launch_app`/`open_url`/`open_notepad`, the note including vault saving and the notes-folder setting (stored as "auto"; the UI comes in PR 6), history v4, kind-aware sites, command retry, overlay, tone, timing. E2E | P3 (Sandbox part, including the held-key case), P6, P9 |
| 4 | `feat/command-warp-tandem` | `warp_open`, `tandem_scratchpad` | P1, P2, P3 (Tandem/Warp part), P8 |
| 5 | `feat/command-arrange` | `arrange` and the P/Invoke move | P7 |
| 6 | `feat/command-settings` | Commands page, notes folder, alias editor, Try a command, accessibility warnings, clash handling, new-feature notice, README. Turns commands on | — |

## Probes
On the real desktop these are **Bryan's** runs, done from steps I write; CLAUDE.md forbids a harness or me driving his desktop. Anything that works with a stand-in runs in the Sandbox instead.

| # | Probe | If A | If B |
|---|---|---|---|
| P1 | Warp: rename a `.toml` into the tab configs folder and open it via `warp://tab_config/<key>?new_window=true`. Is the key the file stem or the `name`? Does it open in the folder and run `commands`? Also check `{{…}}` templating, English-looking commands going to Warp AI, Warp Drive sync, and a missing folder. **When has Warp finished reading the file** (does deleting it as soon as the window appears break anything)? **Can yolime tell when the shell is ready** for a pasted note? (Bryan) | Works and doesn't sync: generate the TOML and delete it once read. If the shell's readiness can be detected, Warp notes paste | `warp://action/new_window?path=…`, then paste and Enter under the guards. If readiness can't be detected, Warp notes go to the vault |
| P2 | Does `POST /api/scratchpad` make the new scratchpad the active tab, with the caret in its editor? (Bryan) | Notes paste | Notes go to the vault. File a Tandem issue for a focus option |
| P3 | From the tray, after a key release, and **again while the dictation key is held for a note**, does each path come to the front: `launch_app`, `open_notepad`, `open_url`, a cold Tandem start, `SetForegroundWindow` on a running Tandem, Warp's URL handler? Does a click on the NOACTIVATE overlay grant raise rights? (Sandbox stand-in; Bryan for Tandem and Warp) | Use as is | Behind card, and notes go to the vault. Sandbox-probe Alt down → `SetForegroundWindow` → Alt up plus a no-op key after `WaitForModifiersUpAsync`; adopt it only if no menu opens |
| P4a | Is structured output, with `ThinkingFor(model)` and low effort, accepted on both models? (claude-api skill plus live smoke test) | Use as is | A tool with `tool_choice: auto` plus a prompt rule; `ClaudeStream` also parses `input_json_delta` |
| P4b | Planner latency, cold and warm | Warm p50 ≤ 1.5 s | A local fast path for an exact "open <alias or app name>" |
| P6 | Launching `notepad.exe` with Notepad already open, and with it closed (restore on): does a new empty tab get focus? (Bryan) | Notes paste | Notes go to the vault; the card says "press Ctrl+N for a new tab" |
| P7 | Does Snap state or a hung window break `SetWindowPlacement`/`SetWindowPos`? (Sandbox) | Use as is | Restore first; skip hung windows |
| P8 | Does `/health` report a PID? Does `GetExtendedTcpTable` give the real owner? (`netstat` shows PID 0 for :3479 here.) (Bryan, read-only) | Verify by PID | Verify by identity fields; failing that, record it as a residual risk |
| P9 | Is a Ctrl+Shift layout-switch hotkey active on his setup (the roll briefly holds both keys unswallowed)? (Read-only registry check: `HKCU\Keyboard Layout\Toggle`) | None: the roll as designed | Active: swallow the command key's key-up after a roll, which N2b's design must then allow |

## Verification
- **Core xUnit.**
  - Every row of the key table, including:
    - the 800 ms commit;
    - the watchdog never polling the command key while it acts as Shift;
    - the Right Shift preview during a dictation;
    - after a roll, the key-up passes through (P9-A) or is swallowed (P9-B), whichever P9 found;
    - a command-key Busy during a dictation's processing.
  - Settings clash and Swap.
  - Planner request shape, parse and binding for each action and each failure, including decision 7. Each of these needs a test:
    - `claude`
    - `cd x && claude` with only `claude` spoken (refused)
    - `git status & foo`
    - `$(foo)`, a backtick, `<(foo)`
    - a newline
    - `.\tool.exe`
    - `"C:\x\y.exe"`
    - `npx foo` with only "foo" spoken (refused)
  - Injected names stay inside their blocks.
  - Alias kinds, and the Warp TOML reader and writer (apostrophe, newline, quote, `\U`, `\b`).
  - CommandRunner: sequencing, cancel, window wait.
  - The note acceptance tests above.
  - History v4 and the kind exclusions.
  - Content-free logs.
- **E2E (Sandbox, never Notepad):**
  - `Command_LaunchApp`
  - `CommandKey_ShortTap_Silent`
  - `CommandKey_Under800ms_Silent`
  - `RightShiftCommand_DuringDictation_Cycles`
  - `RightShift_PreviewDuringDictation`
  - `CommandTap_NeverRetries`
  - the note scenarios
- **Manual on the real desktop (Bryan's runs):**
  - a Tandem scratchpad, closed and open, with an early note;
  - Notepad, open and closed, with a note;
  - "open github.com" with a note (it should go to the vault);
  - "open warp in tandem and start claude", and "…run git status" (it should say "Say the program's name" only when no program was spoken);
  - Chrome plus Warp side by side.
- **Each PR:** `dotnet build`, `dotnet test`, `tests/Yolime.E2E/run-e2e.ps1`, and an agent review before merging.
