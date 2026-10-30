# Design decisions from the UI tour (Bryan, 2026-10-30) — v5

Bryan chose to build all twelve proposals on the findings page (https://claude.ai/artifact/LGrktT9R8n2T8i1XYdnSfQ).
This plan says how each is built and tested. They ship before the v0.1.0 tag, in three PRs: the notch (cards and
overlay), History, and Welcome/Settings. v2 folds in three reviews (keyboard/hook, UI/UX, architecture/tests); the
rulings are at the end.

Where the reviews changed a proposal's shape (overlay-5's countdown, overlay-14's key, overlay-15's width), the PR says
so in one line for Bryan.

## Shared Core pieces (notch PR)
- `Notices.SettingsPage(NoticeKind) → SettingsPage? (General | Keys | Advanced)`: NoKey, KeyRejected, RewriteNoKey,
  RewriteKeyRejected → Keys; RewriteConfig, EndpointInvalid → Advanced; HoldKeyReset → General (its text points
  there); anything else null. One mapping, used by the
  card (cards-9), the balloon click (settings-6) and the tests.
- `OverlayModel.Tap() → TapAction (None | Retry(id) | CopyRaw(id))`: what a bare hold-key tap does to what's on
  screen. Error card with Retry → Retry; Done or UseRaw with the pill offered → CopyRaw; else None. The App's
  `HotkeyTapped` just executes the action (and posts `UserTyped` after CopyRaw, as a click does).
- Button labels (and the pill's, which isn't an `OverlayButton`: `ButtonLabels.Pill(...)`) mention the tap only when it
  reaches the overlay (`DoubleTapLatch` off: `CaptureCoordinator.cs:551`
  never raises the tap otherwise): "Retry (tap Right Ctrl)" / "Retry", "Copy raw (tap Right Ctrl)" / "Copy raw". A Core
  function `ButtonLabels.For(OverlayButton, holdKeyName, tapReaches)` makes the text (tested); the controller reads
  `HoldKeyVk` and `DoubleTapLatch` from settings on every render and rebuilds the buttons (and the pill's label) whenever
  the labels it would build differ from those shown. Because `Apply` skips rendering an unchanged view, the
  controller also refreshes the labels when settings change (AppHost's settings-changed handler already calls into
  the overlay for the capture setting; it calls `RefreshLabels()` too when `HoldKeyVk` or `DoubleTapLatch` change). `Tap()` doesn't
  need the flag: no tap arrives when latch is on.

## The notch (PR `feat/notch-decisions`, after fix/cards and fix/overlay-copy merge)

**cards-9: key errors offer Open yolime Settings, not Retry.** In `Card()`, a notice with a non-null `SettingsPage`
gets no Retry and a new `OverlayButton.OpenYolimeSettings` ("Open yolime Settings"), which opens Settings on that page
through a controller event wired to `AppHost.OpenSettings(page)` (it activates the window, as the tray's command does).
The text for these kinds says nothing about Retry on the card: the rewrite tail for a Settings kind is "The raw
transcript is copied: press Ctrl+V." (no "or use Retry last failed"), so `Text(onCard)`'s Retry substitution has
nothing to turn into a button that isn't there. The balloon keeps "…or use Retry last failed in the tray" only when
the run is retryable (`Retryable(r)`: an STT failure with its recording, or a Prompt/Reply fallback), since the tray
retries the newest retryable run. "Retryable" moves to Core as one public rule, `RunRetry.IsRetryable(RunResult)`
(today's private `OverlayModel.Retryable`), used by the model and by `Notices.For(r)`, which sets a new
`Notice.Retryable` flag as it sets `NotKept`. For a Settings kind with that flag, the text (card and balloon alike, so
the card needs no balloon) ends "Your recording is kept: once the key is in, choose Retry last failed in the tray
menu." ("transcript" for rewrite kinds; "once that's fixed" for EndpointInvalid and RewriteConfig, which aren't about a
key). The rewrite-tail change applies only to the Prompt/Reply branch; Dictate's tails ("was pasted instead", "is
copied") are unchanged. That phrase is not the one `Text(onCard)` turns into a button, so it survives
on the card, which has no Retry button for these kinds. A missing key at hold time has no recording
(`PendingError`) and says nothing about one. Test: Core table over every `NoticeKind`, through both `RunFinished` (with a history id, SttFailed/Auth etc.) and
`NoticeOnly`: buttons are `[OpenYolimeSettings, Dismiss]` exactly when `SettingsPage` is non-null, and the card text
has no Retry button for those kinds, and its text has the tray line exactly when `RunRetry.IsRetryable(r)`.
`TourStates.Find` and `CardTable` build realistic runs for these kinds (SttFailed for STT kinds; CompletedWithFallback
with the matching `RewriteError` and mode for rewrite kinds) instead of a NoSpeech stand-in, and take the notice from
`Notices.For(run)` (so `Retryable` and `NotKept` are set as a real run sets them; the spec's fourth field becomes "has a
history id"), so the kept variants
they show and fit-check are ones a real run produces. The E2E button checks take their expectation from the model's
rules, so they prove the controller renders what the model says; the rules themselves are proved by the Core table.

**cards-11: title and detail.** `Card()` splits the card text at the first ". ", "? " or "! " followed by an uppercase
letter: `Title` = first sentence, `Detail` = the rest (null for one sentence). Narration stays the whole text; the hide
time still counts the whole text. The card's detail line allows 3 lines. `UpdateSlowWithdrawn` keeps matching on the title, but `_slowTitle`
stores the title as shown after the split (the first sentence), so it still only withdraws that card and nothing that
replaced it. Tests: Core property test over every kind ×
mode × copied × NotKept: `Title + (Detail is null ? "" : " " + Detail) == text`; UpdateSlow still withdraws after the
split; `FitProbe` reports `IsTextTrimmed` for title and detail, and the fit check fails on it (so a cut detail can't
pass). On Error cards the title gets `MaxLines = 2` and the detail `MaxLines = 3`, both with `TextTrimming =
CharacterEllipsis`, so `IsTextTrimmed` can report a cut (with no `MaxLines` nothing trims, and the probe could never
fire); other states keep today's values, set per render.

**cards-10: one phrasing for "copied".** "Copied: press Ctrl+V" everywhere: the Done title for a copied delivery,
`Copied` ("Copied: press Ctrl+V to paste."), `CopiedAdminWindow` ("yolime can't paste into admin windows. The text is
copied: press Ctrl+V."); `RawCopied` and the rewrite tails use the same words. In all of them "press" and "Ctrl+V"
are joined by a no-break space (U+00A0, written as an escape), and their tests change with them. Tests: Core text tests, including the Done title.

**cards-8: Esc dismisses a card, and still reaches your app.** Esc is never swallowed for this: the card never has focus,
so the user's Esc is meant for their app. The key code stays in `HookReducer`, which already returns `HotkeyKind.Esc`
unconditionally (`HookReducer.cs:26`) and `HoldStateMachine.Esc()` needs it for cancel and discard, so the reducer is
unchanged. The gate lives in `HookThread.KeyboardProc` after the reducer and the hold machine: Esc that the machine
didn't consume, the hold key not down, no preview armed, and a volatile "card dismissable" flag set from
`ViewChanged`; then it raises `EscPressed`, using the hook's existing `injected` value (which counts the E2E
harness's input as real in E2E builds; reading the raw `LLKHF_INJECTED` flag would make the E2E scenario unable to
pass) and skipping our own input marker. While Welcome is open (`SuppressRuns`), the hook returns early and Esc doesn't
dismiss a card; accepted, since Welcome shows no cards of its own. Paused hotkeys skip it, as they skip every
key. In Processing the machine doesn't consume Esc (`HoldStateMachine.cs:248-255`), so the Busy card shown then is
Esc-dismissable too, as its "Dismiss (Esc)" label says; while recording, Esc is the hold's own (consumed) and the card
on screen is Listening anyway. The rule is
in the model: a new `EscPressed` input hides an `Error` card, info and update cards included (UpdateSlow's whole point
is that it lingers; Esc is the keyboard way to clear it), and does nothing in any other state. The Dismiss button reads
"Dismiss (Esc)". Tests: Core, `EscPressed` hides Error (each kind), no-op elsewhere; E2E
`Card_EscDismisses_AndReachesTheApp`: show a card, focus the target box, tap Esc, the card hides within 1 s (well inside
its own timer) and `TargetForm.EscDowns` went up.

**overlay-14: the Use raw pill gets a key: a tap of the hold key (changed from "Shift plus the hold key").** Through
`Tap()` above. Shift plus the hold key already has a meaning during a hold, and a bare tap already means "act on the
card". With double-tap-to-latch on, no key (the tap never reaches the overlay) and the label says only "Copy raw". The
pill's measured width goes from 200 to 280 DIP. Tests: Core `Tap()` table; E2E `UseRaw_HoldKeyTapCopies`; the pill is
added to the fit check.

**overlay-5: the preview counts down, and hovering pauses it, within a cap.** The hook alone owns the deadline
(`PreviewCapture`, hook thread, `Environment.TickCount64`):
- `PreviewCapture.Hover(bool over, long nowMs)`, reached from the UI thread only through `HookThread.PreviewHover(bool)`,
  which `Post`s to the hook thread and stamps the time there (as `ArmPreviewAsync` does).
- While hovered, the deadline doesn't advance; on leaving, it moves forward by the hovered time. The total extension is
  capped at 40 s, so the card can hold keys for 60 s at most. Arm and Disarm clear the hover. Deadline checks in `Key`
  and `Tick` respect the pause.
- The hook reports `(remainingMs, paused)` to the UI through a new `HookThread.PreviewClock` event (not
  `PreviewSignal`, which is an enum) whenever the ceiling-second changes or the pause state changes, including when the
  40 s cap ends a pause. `PreviewShown` carries the initial remaining ms, since the arm-time push is queued before the
  card shows (`PreviewPrompt.cs:40-41`). A new model input `PreviewClock(remainingMs, paused)` updates the hint. The
  model never computes the deadline itself, so there's one clock.
- Hover wiring: the controller raises `PreviewHover(bool)` from the card's `PointerEntered/Exited` while in Preview;
  AppHost forwards it to `HookThread.PreviewHover`, which `Post`s to the hook thread.
- The hint line: "Enter insert · C copy · Esc discard" (the buttons' order), and in the last 10 s "8 s · Enter insert
  · …", so a long read isn't distracted by a ticking number until it matters. While hovered: "Paused · Enter insert ·
  …".
Tests: Core `PreviewCapture`: hover pauses, leaving extends by the hovered time, the cap holds, a missed hover-leave
still ends by the cap, Arm resets; model hint text for >10 s, ≤10 s and paused; tour `preview-short` `Shows` updated.

**overlay-15: Listening keeps one width: the card's maximum (changed from 440 DIP).** Listening and Working use the
full card width (stack 526 / window 560 DIP, the existing maximum) so a context line arriving mid-recording doesn't
resize the card; 440 was too narrow for a 40-character app name plus the context line. Other states still size to
their content. The state → fixed-width rule is in Core (`OverlayView.FixedWidth`), the controller applies it. Tests:
Core per state; tour check on `FitProbe` cardWidth for the `listen-*`, `reply-*`, `work-*`, `long-listen*` entries all
equal, with a Done entry as the control.

**Harness updates the notch PR owns:** `Scenarios.CardTable()`/`CardsFit` and `TourStates.Find` take expected buttons
from the model's rules (not a second hard-coded `1 + retryable + MicDenied`); `done-copied` and preview `Shows` follow
the new text; `Preview()` builder follows `PreviewShown`'s new shape; all new hooks/probes under `#if YOLIME_E2E` and in
`ReleaseHasNoTestCodeTests`. No new log line records a key; Esc-dismiss and tap-copy log the action only.

## History (PR `feat/history-decisions`, off main now that #3 is in)

**history-guard:** Retry runs under `SetBusy` exactly as Re-run does: `RetryAsync` already awaits the whole run, so busy
ends when it returns. No 60 s timer, no release on `HistoryChanged`. The change is in the window (the same `_busy`
as Re-run's), covered by PR review; a Core test of `RetryAsync` would pass either way, so there isn't one.

**history-7: a right-click menu on rows.** Right-click (or Shift+F10, the menu key) first selects the row, as Explorer
does, then opens a `ContextFlyout` with Copy output, Copy transcript, Re-run as (submenu), Delete, with the detail
pane's enable rules, including `_busy` and paused. Delete confirms as the detail pane's does. Test: E2E
`History_RowMenu_ActsOnTheClickedRow`: right-click an unselected row through UIA, invoke Copy output, the clipboard has
that row's output and the selection moved to it.

**history-8: one box when output and transcript match.** `HistoryItem.OutputMatchesTranscript` (Core): `Output ==
RawText` and not a fallback (`FallbackUsed` keeps both boxes, so a failed rewrite stays visible). The detail shows one
box, "Output (same as transcript)". Tests: Core truth table; tour History image.

## Welcome and Settings (on `fix/welcome-settings`, before its first push)

**welcome-2, all four:**
1. The mic step's denied message (only `AccessDenied`) gets an "Open privacy settings" link.
2. "Skip setup" is hidden on the last step.
3. Choosing a hold key with the arrow keys keeps focus: the radio change updates the heading and pill text in place,
   keeping the `HoldKeySeen` handler and `SuppressRuns` as they are.
4. Enter in a key box runs Save and test when the button is enabled (not while a test is running).
Tests: E2E `Wizard_HoldKeyArrows_KeepFocus` (arrow between radios, focus stays on a radio); `Wizard_EnterSavesKey`
(paste a fake key, Enter, the status line changes); the tour shows the link only in the denied state (the Sandbox has no
mic, so it shows the no-mic text, and the tour's Welcome step 2 image is the check that the link isn't there).

**settings-6** moved to the notch PR: it needs `Notices.SettingsPage`, which that PR adds, and it's the balloon twin of
cards-9's button.

## Order and verification
- No branch is pushed until its base is on main, then it's rebased onto `origin/main` first (no force-push needed).
  Merge order: fix/cards → fix/overlay-copy → feat/notch-decisions; feat/history-decisions; fix/welcome-settings.
- Each PR: Core tests, a review agent on its diff, its area's tour, the E2E scenarios it touches, CI. The notch PR runs
  the full E2E (it touches the hook).

## Review rulings (v5b, whole-plan review of v5: no Critical or High)
- The kept line's wording follows the kind; tour cards take their notice from `Notices.For`; the tail change is
  Prompt/Reply only.

## Review rulings (v5, whole-plan review of v4)
- cards-9: the card itself carries the tray instruction for a retryable run, in words the on-card Retry substitution
  doesn't touch (High). `RunRetry.IsRetryable` in Core, and a `Notice.Retryable` flag, so the balloon can know.
- Tour/fit rows for these kinds use realistic runs; the E2E button check's limits are stated.
- cards-8: the gate uses the hook's `injected`; no Esc-dismiss while Welcome is open.

## Review rulings (v4, whole-plan review of v3)
- cards-9: a kept line without the word Retry when the run is retryable (High); the fit rows with a history id now test
  something.
- cards-8: no idle requirement; Busy during Processing is dismissable, matching its label.
- UpdateSlow: keep title matching, store the split title (no new flag to go stale).
- Labels: the pill gets its own label function; settings changes refresh labels without a view change.
- cards-11: `MaxLines` so trimming (and the probe) can fire. HoldKeyReset gets a General page. cards-10's no-break
  space covers all the "copied" texts. settings-6 moves to the notch PR. history-guard's Core test dropped (the
  change is in the window; PR #6 covers it by review).

## Review rulings (v3, whole-plan review of v2)
- cards-9: no Retry wording on the card for Settings kinds; the balloon's "Retry last failed" only when `Retryable(r)`
  (High). Fit table gains the kept-history variants.
- Labels: built in Core from live settings, rebuilt by the controller when they'd differ; the pill too (High).
- cards-8: gate in `HookThread` after the reducer, reducer unchanged; Busy-while-Processing accepted.
- overlay-5: a `PreviewClock` event with `(remainingMs, paused)`, initial value on `PreviewShown`, pushes on
  ceiling-second change; hover wiring named.
- cards-11: trimming enabled so the trimmed-text probe can fire; detail max lines per state.

## Review rulings (v2)
- cards-9 decided once in Core (`SettingsPage`), not in `Retryable`; the card says when the recording is kept (UI/UX,
  architecture: High).
- overlay-14: with latch on the tap never arrives, so no key and no "(tap …)" in the label; the same fix for Retry's
  label; the tap rule moves to Core (keyboard, architecture: High).
- overlay-5: one clock (the hook's), remaining ms pushed to the UI, a 40 s cap on hover, hover crosses threads by
  `Post`; the number only in the last 10 s (keyboard, UI/UX, architecture: High).
- overlay-15: full card width, not 440 (UI/UX: High).
- cards-11: UpdateSlow withdrawal by kind (UI/UX: High); trimmed-text probe (architecture).
- cards-8: Esc through `HookReducer`, gated, rule in the model; info and update cards dismissable on purpose; "Dismiss
  (Esc)" (keyboard, architecture, UI/UX).
- history-guard: busy for the awaited run, no timer (architecture). history-7: right-click selects first (UI/UX).
  history-8: not when it was a fallback (UI/UX).
- The harness's hard-coded button table is derived from the model's rules (architecture: High).
