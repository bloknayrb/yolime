# feat/latency-leftovers: the three items PR7 deferred (v1, for review)

`2026-10-28-latency.md` ends with "Still deferred":
- the "Mic starting..." notch text;
- `ClipboardOpenMs` and attempts;
- `HistoryWriteMs`.

Everything else in that plan has shipped (#10, #14–#17). This PR builds the three deferred items.

## 1. "Mic starting…" on the notch

**Why.** The start sound already waits for the mic's first audio, up to 2 s. The Listening card doesn't wait: it appears at 300 ms whether or not the mic is delivering. On a Bluetooth headset that switches profile, the card says "Dictate · Word" for up to a second or two while nothing is recorded. After 2 s with no voice it says "No audio: is the mic muted?", which is the wrong reason. Whoever reads the card rather than waiting for the sound starts talking too early and loses their first words.

**Design (Core, TDD):**
- `RecordingStarted` gains `MicReady` (default `true`). The coordinator sets it from the hold's `FirstAudio`: ready when there's no live capture or it has already delivered.
- New input `AudioFlowing(AtMs)`. The coordinator reports it in the same continuation that plays the start sound: on the first buffer, or at the 2 s cap. It only reports for the hold still in progress.
- `OverlayModel.Listening()` detail, in this priority:
  1. the cap countdown;
  2. **"Mic starting…" while not ready**;
  3. "No audio: is the mic muted?";
  4. the context line;
  5. "Tap to stop".

  While the mic is starting, the no-audio hint is held back, because the honest reason then is that the mic is starting. Once the cap fires with still no audio, the card falls back to the no-audio hint as today.
- The title (mode · app), the elapsed time and the level meter are unchanged. There's no narration: Listening never narrates.

**Tests:**
- `OverlayModelTests`:
  - starting, then flowing;
  - the cap countdown wins over "Mic starting…";
  - no-audio is held back while starting and shown after the cap;
  - a mode change while starting keeps it;
  - an `AudioFlowing` arriving after the card left Listening does nothing.
- `CoordinatorOverlayTests`:
  - a cold mic reports `MicReady=false`, then `AudioFlowing` on the first buffer;
  - a mic already delivering reports `MicReady=true` and no `AudioFlowing`;
  - the 2 s cap reports `AudioFlowing`;
  - a hold that ended reports nothing.

## 2. `ClipboardOpenMs` and `ClipboardOpenAttempts`

**Why.** PR7 suspected the clipboard open retries (up to about 1.27 s each) of causing the 5 s stall. `ClipboardSaveMs` and `ClipboardSetMs` include the open, but can't show whether the time went into retries.

**Design:**
- A Core `ClipboardOpenMeter`, an `AsyncLocal` scope. `OutputExecutor.DeliverAsync` starts one, and `ClipboardGuard.OpenAsync` records into it the time spent opening (retry delays included) and the number of `OpenClipboard` calls.
- The scope flows into whatever this delivery awaits, so it counts this paste's save and set. A previous paste's restore, flushed at the start, runs in that paste's captured context and isn't counted here (it's already in `FlushMs`). The background restore this delivery starts records into a scope that's finished, and is ignored.
- `OutputBreakdown` gains `ClipboardOpenMs` and `ClipboardOpenAttempts`, both defaulted, so old timing lines still parse.
- `tools/timing-report.ps1` prints them with the other breakdown medians.

**Tests:**
- The meter: nested and parallel scopes are isolated, and recording with no scope does nothing.
- The executor: a fake clipboard records two opens into the scope, and the breakdown reports them.
- Timing JSON: a round trip, and an old line without the fields.

## 3. `HistoryWriteMs`

**Why.** History writes (SQLite, WAL) run inline in the pipeline between stages. One of them, the row's output update, sits right before the paste. A slow disk would add straight to latency, unseen.

**Design:**
- `RunTiming.HistoryWriteMs`: the total time spent inside the run's `TryHistory` writes, measured with the pipeline's `TimeProvider`.
- A failed write still counts its time.
- The timing report prints its median.

**Tests:**
- A pipeline test with a history fake whose writes advance the fake clock reports the sum.
- History paused: 0.

## Privacy
Every new field is a number. The no-content logging test covers `RunTiming` and `OutputBreakdown` by reflection; check that it picks up the new fields. "Mic starting…" is fixed copy.

## E2E
- The Sandbox suite runs unchanged, as the regression guard.
- The E2E file mic delivers at once, so "Mic starting…" isn't exercised there. Its behaviour is covered by the Core tests. Adding a start delay to the E2E mic only to see the text is left out: overlay.txt records state and buttons, not the detail line, by the content rule.

## v2 rulings (three reviews: UI/UX; correctness and architecture; privacy, product and tests)

These replace the matching parts above where they differ.

**Split into two PRs, one concern each:**
- **A.** `feat/mic-starting`: section 1.
- **B.** `feat/open-history-timing`: sections 2 and 3.

### A: "Mic starting…"
1. **One reading of readiness.** `PlayStartWhenAudioFlows` returns the `FirstAudio.IsCompleted` value it tested, and that value is `RecordingStarted.MicReady`. Two separate readings could disagree, and then "Mic starting…" would never clear.
2. **`AudioFlowing(AtMs, Delivered, RunId)`.**
   - `Delivered` is `t.IsCompletedSuccessfully`. It's false when the 2 s cap fired with no audio, or when the mic failed (that run then ends with MicLost anyway).
   - The model ignores the input outside Listening, and for a different run.
3. **The silence window counts from ready, not from the card.**
   - The model keeps `_readyAtMs`. It's set from `RecordingStarted.AtMs` when the mic is ready, from `AudioFlowing.AtMs` when the audio was delivered, or from the first `Level`.
   - "No audio: is the mic muted?" shows only once 2 s have passed since then with no voice.
   - Without this, a headset ready at 1.9 s would show the muted hint 100 ms later, which is the very message this PR removes.
4. **A mic that never delivered says so.** With `Delivered=false`, the detail is "Mic not responding" at once. A mic muted in Windows still delivers silent buffers, so "muted?" is kept for real silence.
5. **No flash for a slightly slow mic.** "Mic starting…" shows only when the mic isn't ready **and** the card is 300 ms old or more. Until then the slot shows its usual lines, without the no-audio hint.
6. **Any `Level` means audio is flowing,** as a backstop for a missed `AudioFlowing`. The model checks the flag, not the coincidence of the 2 s silence time and the 2 s cap.
7. **Detail priority:**
   1. the cap countdown;
   2. "Mic not responding";
   3. "Mic starting…";
   4. "No audio: is the mic muted?";
   5. the context line;
   6. "Tap to stop".

   In Reply, "Mic starting…" covers the context line until the mic is ready. That's the line that tells him to act, and the context comes back as soon as he can speak (UX finding 5, decided this way). Tested.
8. **The "Why" is narrowed.**
   - The start sound already keeps first words from being clipped.
   - The card adds the right message for someone who can't hear the sound (`Sounds` off, or a muted speaker), and removes the wrong "muted?" hint.
   - The PR claims no measured drop in clipped words. `MicFirstAudioMs` is already logged for anyone who wants the exposure.

### B: open and history timing
9. **The open retry loop moves into Core** as `ClipboardOpen.RetryAsync(tryOpen, delayFor, ct)`, and `ClipboardGuard.OpenAsync` calls it. It records into the meter in a `finally`, so a "clipboard is busy" failure, the stall itself, is measured. It's tested with a fake that fails N times.
10. **The meter is a mutable reference object.**
    - The `AsyncLocal` is set once, in the executor's own method body, next to `new Laps`.
    - Recording adds with `Interlocked` and never reassigns `.Value`, because a value set in a callee doesn't flow back.
    - `ToRecord` snapshots it and closes the meter, so a late record is dropped visibly.
11. **Retries, not attempts.** A normal paste makes two opens, so `ClipboardOpenAttempts` would always be 2. The fields are `ClipboardOpenMs` and `ClipboardOpenRetries`.
12. **The restore gets its own numbers.**
    - `RestoreLaterAsync` begins its own meter and logs `openMs` and `openRetries` in `ClipboardRestored`.
    - Contention most likely happens right after the paste, while the target is still reading.
    - `CopyAsync` laps `Set` too, so its open time is inside a lap.
13. **History timing is measured once.**
    - Only the generic `TryHistory<T>` is timed, after its skip guard, in a `try/finally`.
    - Raw timestamp ticks are summed and converted once, in `Finish`, so sub-millisecond WAL writes aren't lost to rounding.
    - Two fields:
      - `HistoryWriteMs`, the whole run;
      - `HistoryBeforePasteMs`, the running total snapshotted when the Output stage starts, for the question "does history delay the paste?".
    - Not counted: direct calls outside `TryHistory` (`DeleteRow`, `MarkAudioExpired`).
14. **The report shows the tail.**
    - For each new field, `timing-report.ps1` skips lines that don't have it.
    - It prints p50, p90 and max, plus counts: runs with open retries above 0, and runs with history above 50 ms.
