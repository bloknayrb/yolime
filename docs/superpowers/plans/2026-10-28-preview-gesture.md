# fix/preview-gesture: Shift held at release previews, without cycling the mode (v1, for review)

## The bug (found by the PR8 README fact-check, 2026-10-28)
- **The spec** (docs/plan.md, Overlay): "Preview: holding Shift at release, or the per-mode 'preview long output' setting, shows an expanded card." Elsewhere: Shift tapped while holding cycles the mode.
- **The code** (HoldStateMachine.ShiftDown):
  - In Recording, Shift's **down edge** cycles the mode at once (test `Shift_WhileRecording_CyclesOnDownEdge_AndSwallowsItsOwnUp`).
  - `Commit.ShiftHeld` is true when Shift is down at release, and the pipeline turns that into a preview.
  - So holding Shift to preview always cycles the mode first: a Dictate hold previews as Prompt. A per-run preview of the current mode is impossible.
- **The in-app help** (SettingsWindow.cs:522) says "Or hold Ctrl as you let go of the hold key to preview just that once." A Ctrl press during a hold is "other input" and **discards the recording**. Following the app's own instructions loses the dictation.

## Change
1. **Cycling moves to Shift's up edge.** In Recording, Shift down is swallowed (as now; also the Ctrl+Shift layout switch) and remembered. Its up, while still Recording, emits `CycleMode` and the cue. Auto-repeat still counts once.
2. **Shift held at release previews in the current mode.** When the hold key comes up while that Shift is still down, `Commit(ShiftHeld: true)` goes out with **no** cycle. The Shift up that follows is swallowed (its down was) and cycles nothing, since the state is no longer Recording.
3. **Latched (hands-free)** is unchanged: Shift passes through to the app (the user may be typing), and per the existing tests there's no preview by Shift there.
4. **The Settings help text** becomes "Or hold Shift as you let go of the hold key to preview just that once."
5. **The welcome window and notices** are checked for any other wording about the preview gesture, and fixed to match.

**Cost:** the mode cue and the notch's mode change come when Shift is released, not pressed, typically 80–150 ms later. A user who presses Shift and releases the hold key without releasing Shift gets a preview, not a cycle. That is the spec's gesture.

## Tests (TDD)
- Machine:
  - Shift down then up in Recording cycles on the up (the old down-edge test is rewritten);
  - Shift down, hold up, Shift up gives `Commit(Released, ShiftHeld: true)`, never a `CycleMode`, and the Shift up is swallowed;
  - auto-repeat Shift downs then one up cycle once;
  - with cycling off, the up does nothing;
  - Pressed and Latched behaviour is unchanged (existing tests).
- Coordinator: the existing cycle tests (CaptureCoordinatorTests 580-604, CoordinatorOverlayTests 120) are updated to the up edge, and a test checks that a Shift-held release keeps the starting mode and requests a preview.
- E2E (Sandbox): `PreviewByShift_KeepsTheMode`. Hold, press Shift, release the hold key, release Shift. The preview card appears and Enter inserts the Dictate result (not Prompt's). The existing "PromptViaShift_Rewrites" scenario is updated to the up edge if needed.

## v2 rulings (two reviews: correctness and platform, UI/UX)

These replace the matching parts of "Change" above where they differ.

1. **The 250 ms rule the overlay plan already approved** (`2026-10-28-overlay.md`: "Only if Shift has been down for 250 ms or more…") is finally implemented. At the hold's release, with a Shift pressed in this recording still down:
   - held for 250 ms or more: `Commit(ShiftHeld: true)`, no cycle;
   - held for less: it was a tap that rolled over the release, so `CycleMode` and then `Commit(ShiftHeld: false)`.
   The common sloppy release then does what the user meant.
2. **A per-hold `_cycleOnUp` flag.** It is set only by a Shift down in Recording, and cleared by every commit, every discard and a new press. A Shift up cycles only when this flag is set and the state is Recording, so a Shift pressed before the hold, or left over from the last hold's preview, never cycles. Swallowing stays tied to `_shiftSwallowed`, so downs and ups stay paired.
3. **`ShiftHeld` only for `Released`.** The cap, the end of hands-free, a switch away and a missed key-up never preview: typing a capital in another app while hands-free ends must not pop a card. Pinned by tests.
4. **Shift resyncs at each new hold.** `HotkeyInput.ShiftPhysicallyDown` is filled by the hook on HoldDown from `GetAsyncKeyState(VK_SHIFT)`. Null means unknown (tests). If it reads up, the stale `_shiftDown` and `_shiftSwallowed` clear, so a Shift-up lost to an elevated window can't stick.
5. **The card names its mode:** "Preview · Prompt", and the same in the narration.
6. **Copy:**
   - Settings: "Or keep Shift held as you let go of the hold key to preview that run; a quick Shift tap changes mode instead."
   - Welcome: "Tap Shift while holding to change mode, or keep it held as you let go to preview first."
7. **E2E:**
   - `PreviewByShift_KeepsTheMode`: Shift held for 400 ms before the release. The card shows; Enter is pressed **while Shift is still down**, then Shift is released. The Dictate text is inserted, and the run's mode is Dictate.
   - `ShiftTapRolledOverRelease_Cycles`: Shift down, the hold up 40 ms later, Shift up 30 ms after that. Prompt, and no card.
   - The existing scenarios' Shift taps (150 ms gaps) are unchanged.
8. **Not done:**
   - Left and right Shift still share one flag (pre-existing and rare).
   - A "will preview" signal in the Working state.
   - The long-press-to-arm design. It would arm the preview while recording, with its own cue, and is offered to Bryan as an alternative; it departs from the spec's "at release".
