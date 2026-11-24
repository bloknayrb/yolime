# Spoken mode commands: Dictate by default, the mode picked by voice (agent-written plan)

Bryan, 2026-11-24: "Instead of switching between different dictation modes, I want it to default to just dictation
always, and I can tell it if I want it to do something specific at the beginning. Like if I say 'write a prompt' …
'write a reply' … if I don't specify, it should just take dictation and clean up what I wrote."

Decisions (asked before planning): remove Shift-tap cycling (Shift held at release still previews); every app starts
in Dictate (no editor/terminal Prompt default, no global or per-app starting modes); Raw by a spoken command; a fixed
phrase list matched locally, with no extra Claude call.

The plan below had a three-way adversarial review before implementation (pipeline correctness; UI/UX; matcher,
security and tests). Its rulings are folded in; the review notes are summarised at the end.

## 1. Command matcher (new, Core): `src/Yolime.Core/Modes/ModeCommands.cs`

`ModeCommands.Match(string transcript) → CommandMatch?`, where `CommandMatch(Mode Mode, int Length)`. It returns
null when no command is found, never "Dictate". The function is pure. It covers English only, and the help text says
so.

**Normalising (for matching only).** NFKC. Treat any Unicode whitespace or NBSP as a space. Skip leading
whitespace, quotes (`" ' “ ” ‘ ’`) and `…`. Compare ordinal-ignore-case, never with culture rules.

**Optional filler.** At most one of `um | uh | er | okay | ok | so`, followed by `,` or `.`. It is allowed before
multi-word phrases only.

**Multi-word phrases**, longest first. Each must be followed by end of text or `[\s.,:;!?…\-–—]`.

| Mode | Phrases |
|---|---|
| Prompt | write me a prompt, write a prompt, make this a prompt, make a prompt, prompt mode |
| Reply | write me a reply, write a reply, draft a reply, write a response, draft a response, reply mode |
| Raw | raw mode, verbatim mode |

**Bare words** (`prompt`, `reply`, `raw`, `verbatim`) match only when followed by `[.:!?…–—]` or end of text,
never a comma. So "Reply all is broken", "Raw, unfiltered…" and "Verbatim, he said…" stay dictation.

**Phrases left out on purpose**, because they occur in ordinary text: "reply to this", "respond", "write back".

**Stripping.** `ModeCommands.Strip(raw, match)` returns the text after the matched command and the separator run
that follows it. It upper-cases the first character only when all of these hold:
- the character is a lowercase letter;
- the next character is not uppercase (so "iPhone" is left alone);
- the mode is not Raw.

## 2. Pipeline (`src/Yolime.Core/Pipeline/DictationPipeline.cs`, `PipelineTypes.cs`)

**Turning detection on and off.** Add `RunRequest.DetectCommand` (true for real holds). Remove the now-meaningless
`RunIntent.Mode` and `RunRequest.InitialMode`; runs start as Dictate.

| Path | Behaviour |
|---|---|
| Real hold | Detects. |
| `RetryAsync(asMode: null)` | Detects. |
| `RetryAsync(asMode: X)` | Strips a matched command, but X sets the mode. |
| `RerunAsync`, `RunFromTranscriptAsync` | Do not detect. They strip only if the stored row's mode equals the matched command's mode. |
| `TestAudioRunner` (the CI `--test-audio` check) | Stays Raw with detection off, so the `hello-e2e` check is unchanged. |

**On the audio paths**, after the empty-transcript check (≈ line 398):
- Run `Match(raw)`. On a match, set `run.Mode`.
- `rest = Strip(...)`. The **full transcript stays in `RawText`** in history, so a misfire can be recovered by
  re-running as Dictate.
- The rewrite input, `RunResult.RawText` (Copy raw and Use raw) and `TranscriptReady` all use `rest`.
- Report the new `ModeResolved(Guid RunId, Mode Mode)` before the rewrite. It is logged as a tag only.
- No match keeps the run's current mode. Existing tests that start Prompt or Reply runs directly keep working.

**Command with nothing after it.** This gets a new `RunOutcome.CommandOnly` and the notice "Heard 'write a prompt',
but nothing after it". Nothing is sent and the outcome is not retryable.

There is one exception: Reply with an empty rest **and** usable context goes ahead, and drafts from the context
alone. If there is no usable context, the run ends with the notice "Nothing to reply to: select or copy the message
first". `LatestRetryable` and `RunFromTranscriptAsync` must agree on what counts as retryable.

`ShortDictation.CanSkip` still runs after detection, so "Reply. Thanks!" goes to Reply.

## 3. Capture and context (`src/Yolime.Core/Capture/CaptureCoordinator.cs`, `src/Yolime.App/Native/ContextReader.cs`)

**Hold state machine** (`src/Yolime.Core/Hotkeys/HoldStateMachine.cs`). Delete `CycleMode`, `Next()` and the
`ModeCycling` option. A Shift tap during a hold, or one that rolls over the release, is still swallowed so it can't
stick, but it changes nothing. A Shift still held at release still requests the preview. `HoldDown` no longer takes a
mode.

**AppHost.** Remove `StartingModes.For`, the per-event `ForegroundNow` that only fed it, and `ModeCycling`.

**Context reading is split in two:**
- **Selection** (UIA, 300 ms budget, no synthetic keys) is read during every hold, as Prompt and Reply holds read it
  today.
- **Clipboard** is read only after `ModeResolved` resolves to Prompt or Reply. Freshness makes this cheap. Plain
  dictation never calls `OpenClipboard`, so it never forces Office to render delayed clipboard formats and never
  contends with its own paste.

**Showing context.** The snippet is not shown in the notch during the listening phase.

**Plumbing `ModeResolved`.** The coordinator wraps the run's progress object and handles `ModeResolved` under
`_gate`. It:
- updates `Capture.Mode` and `_processing`/`_current`, so Busy notices name the real mode;
- plays that mode's cue;
- reports `ModeChanged(RunId, mode)`;
- reports `ContextShown(RunId, …)` once both the mode and the context read are known, in either order.

Dictate and Raw runs never send or store context. It is still written only inside `ContextAsync`.

## 4. Overlay and feedback (`OverlayModel.cs`, `OverlayProgress.cs`, `Notices.cs`)

- **Working state.** It accepts `ModeChanged` and `ContextShown`, both checked against `RunId`. The title becomes
  "Writing a prompt for {app}…" or "Writing a reply for {app}…". Reply shows "Replying to: '…'" or "No message found:
  writing from your words".
- **Done cards and the preview card** name the mode when it isn't Dictate, e.g. "→ Code · Prompt · 54 words".
- **Sound cues.** The per-mode cue is kept, but now plays when a non-Dictate command is recognised. This confirms the
  mode before anything pastes. The toggle becomes "Sound when a spoken command is heard".
- **Notices.** Add the two new ones (`CommandOnly`, and nothing to reply to).

## 5. Settings, migration and copy

- **`YolimeSettings`.** Remove `StartingMode` and `AppStartingModes`, and their validate/reset entries
  (`YolimeSettings.cs:134,138,157,161`). Rename `ModeCycleCues` to `CommandCues`. Delete the per-app helper in
  `SettingsEdits` and `StartingModes` in `Profiles.cs`; the tone profiles stay.
- **Migration.** `[JsonExtensionData] Extra` (`YolimeSettings.cs:119`) would keep the old keys forever. Add a
  `SettingsMigrations` step: bump the schema to 2, drop `startingMode` and `appStartingModes`, and map
  `modeCycleCues` to `commandCues`.
- **One-time notice.** The same step sets a flag that shows, on first launch after the upgrade: "Modes are now
  spoken: start with 'write a prompt', 'write a reply' or 'raw'."
- **Settings window.** Remove the starting-mode combo, the per-app list and the "Shift tap changes mode" copy
  (`SettingsWindow.cs:520-530, 590`). Add a help block listing every phrase. Keep "Always preview" for each mode, and
  note that previewing Prompt is a way to confirm the mode before it pastes.
- **Welcome window** (`WelcomeWindow.cs:292`): "Start with 'write a prompt', 'write a reply' or 'raw' to change what
  yolime writes; keep Shift held as you let go to preview first."
- **README:**
  - Rewrite the Modes section: Dictate by default, the phrase list, English only, editors no longer start in Prompt.
  - Recovery: History → re-run in another mode works on the full transcript.
  - Cold Bluetooth mics: wait for the start cue, or "write a" may be cut off.
  - "What is sent where": the selection is read on every hold, but sent only for Prompt and Reply; the clipboard is
    read only for those modes.
- **CLAUDE.md:** update the status line.

## 6. Tests

- **`ModeCommandsTests` (new):**
  - every phrase with STT-style casing and punctuation (`.`, `:`, `—`, `…`);
  - fillers ("Um, write a reply…", "Okay so write a prompt", which should not match because only one filler is
    allowed);
  - bare-word guards ("Reply all…", "Raw, unfiltered", "Verbatim, he said", "Prompted by…", "Prompt payment is due");
  - a command in mid-sentence;
  - quotes, NBSP and NFKC input;
  - re-capitalisation, including "iPhone" left as is and Raw never re-capitalised;
  - "reply to this email by Friday" stays Dictate.
- **`DictationPipelineTests`:**
  - "Write a reply. …" runs Reply with context; the row stores the full transcript and `Reply`; the rewrite gets
    `rest`;
  - a command alone gives `CommandOnly`;
  - empty Reply with context runs; without context it ends with the "nothing to reply to" notice;
  - Retry with a null mode re-detects; Retry with an explicit mode strips but keeps the given mode;
  - Rerun as Dictate gets the full transcript;
  - "Reply. Thanks!" isn't skipped as a short dictation;
  - "Always preview" for Reply applies to a Reply chosen by voice;
  - a transcript with no command keeps the given mode, so the existing ~40 Prompt and Reply tests are unchanged;
  - context containing "write a reply" never changes the mode;
  - a Dictate row has `Context == null`.
- **`HoldStateMachineTests`:**
  - remove the cycling tests;
  - rewrite `AShiftTapRolledOverTheRelease_StillCycles` (line 113) to expect a plain Commit with Shift swallowed;
  - keep the auto-repeat and stale-Shift tests (lines 103, 136, 152) without `CycleMode`.
- **`CaptureCoordinatorTests` and `CoordinatorOverlayTests`:**
  - invert `DictateHold_NeverReadsContext` (line 619): the selection is read, the clipboard is not;
  - delete the cycle and cue tests (lines 630, 657, 668, and line 118 of the overlay tests);
  - update `ShiftHeldAtRelease_PreviewsInTheStartingMode` (line 679);
  - a Busy notice after `ModeResolved(Reply)` names Reply;
  - a `ContextShown` from a previous run is ignored;
  - the cue plays on a resolved command.
- **`SettingsStoreTests`:** load a v1 file with the old keys, save it, and assert that the keys are gone and
  `commandCues` was carried over.
- **Logs:** extend `NoContentInLogsTests` with a transcript containing "SENTINEL Write a reply."; `NoStringFieldsTests`
  covers the new event.
- **E2E** (`tests/Yolime.E2E`, `#if YOLIME_E2E` only):
  - add a per-hold transcript override that the fake STT session reads on each hold;
  - rewrite `HoldCycling` and the scenarios that use it (`PromptViaShift_Rewrites`, `ReplyFromSelection_UsesIt`,
    `FreshCopy_UsedAsReplyContext`, `ShiftTapRolledOverRelease_Cycles`, `Scenarios.Preview.cs`, `Tour.cs:348`) to
    speak commands;
  - add "Shift tap mid-hold does nothing".

## 7. Process (per CLAUDE.md)

Work on `claude/keen-cori-k2tq1v` and open a PR into `main`. Save this plan as
`docs/superpowers/plans/2026-11-24-spoken-mode-commands.md`, including the review rulings above. Implement it, then
have an agent review the PR before merging.

## Verification

1. `dotnet build` and `dotnet test`. Core tests may run in this Linux container if the pinned SDK installs; the App
   build needs Windows or CI.
2. CI: `ReleaseHasNoTestCodeTests`, `tools/check-publish.ps1`, the `--test-audio` `hello-e2e` check (unchanged), and
   the locked restore.
3. Windows Sandbox only: `pwsh -File tests/Yolime.E2E/run-e2e.ps1`, with the rewritten scenarios passing.
4. Manually on your machine:
   - plain dictation in VS Code pastes dictation, not a prompt;
   - "Write a prompt …" in Claude Code shows "Writing a prompt…" and plays the cue, then pastes a structured prompt
     with the selection appended;
   - "Write a reply …" with a copied message drafts a reply;
   - "Write a reply" alone with nothing copied shows the "nothing to reply to" notice;
   - a Shift tap mid-hold does nothing, and Shift held at release previews;
   - the old settings show the one-time notice, and their keys are gone after a save.

## Implementation notes (where the code differs from the plan above)

- `RunIntent.Mode` and `RunRequest.InitialMode` were kept, not removed: holds always pass Dictate, but the ~40
  pipeline tests that start Prompt or Reply runs directly (and Retry/Rerun) still use them. `RunRequest.DetectCommand`
  is what turns detection on, and only the coordinator sets it.
- The matcher doesn't NFKC-normalise: it matches the original text in place (case-insensitive, any Unicode whitespace
  between words, leading quotes skipped), so stripping never rewrites the user's characters.
- A hyphen after a phrase isn't a boundary ("write a prompt-engineering guide" is dictation); en and em dashes are.
- `ModeResolved(Mode, Spoken)` is reported for every transcribed run, matched or not; the coordinator uses it to read
  the clipboard (Prompt/Reply only) and to play the cue (spoken commands only), and `OverlayProgress` maps it to
  `ModeChanged`.
- "Write a reply" with nothing after it and usable context: the system message (code, not the prompt file) says to
  write a brief reply, since `reply.v1.md` returns nothing for an empty transcript. If that rewrite fails there's no
  transcript to fall back to, so the run ends RewriteFailed (nothing delivered) and the card's Retry rewrites again.
- Copy raw (`AppHost`) copies `ModeCommands.ContentFor(raw, mode)`: the words the mode worked on.
- E2E: the fake STT reads `<results>\next-transcript` once per session, so a scenario speaks a command per hold.
- The App can't be built on Linux (the XAML compiler is a Windows exe); its C# was compiled in a scratch project
  without XAML, in both normal and E2E flavours, with warnings as errors.

## Review rulings (summary)

- Correctness: no-match must keep the run's mode (not force Dictate); the CI `--test-audio` check stays Raw with
  detection off; the Sandbox E2E Shift scenarios need a per-hold transcript; `ContextShown` must be accepted while
  Working and carry a RunId; Busy notices must name the resolved mode; Retry with an explicit mode must still strip.
- UI/UX: confirm a recognised command before pasting (Working title, Done title, cue); "command only" and "nothing to
  reply to" get their own notices, not "Didn't hear anything"; keep the full transcript so a misfire is recoverable;
  tell upgraders once; list the phrases in Settings and the welcome window; drop phrases common in ordinary text.
- Matcher/security/tests: `[JsonExtensionData]` would keep the old settings keys forever, so migrate to schema 2;
  "reply to this" is a false positive; bare words need a full stop, colon or dash (never a comma); don't capitalise
  "iPhone" or Raw; read the clipboard only for Prompt/Reply; context can never pick a mode (verified: it never reaches
  the transcript); extend the no-content-in-logs test.
