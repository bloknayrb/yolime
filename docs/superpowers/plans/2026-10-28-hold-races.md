# fix/hold-races: holds lost to a foreground flash or a misread key (v1, for review)

Carry-over correctness bugs split out of PR7 (`docs/superpowers/plans/2026-10-28-latency.md`, "Carry-over correctness bugs"). One concern: a hold the user meant is lost or misread.

## Evidence (Windows Sandbox probes, 2026-10-28)

**1. The "toast mid-hold" discard isn't the toast.** A probe hooked `EVENT_SYSTEM_FOREGROUND` (out of context, like yolime's hook) over a focused WinForms window, then showed two tray balloons (yolime's notices are balloons, which Windows 11 shows as toasts).
- Neither balloon fired a foreground event.
- About 15 s after logon, a **hidden** console window took the foreground: `rgnupdt.exe`, `ConsoleWindowClass`, `IsWindowVisible = false`, not cloaked. `GetForegroundWindow` agreed. About 67 ms later the foreground went back to the focused window.
- That fits the E2E discard "about 17 s after launch, when the startup toast appeared": the toast was a coincidence, and the cause was a background process briefly taking the foreground with an invisible window. On a real machine, updaters, scheduled tasks and console helpers do the same.
- Today `HookThread.ForegroundProc` feeds `ForegroundChanged` for any foreground window outside the capture's process, so the flash discards a Pressed or Recording hold (and commits a Latched one early).

**2. The watchdog can misread a fresh key-down as "up".** A probe installed a low-level keyboard hook and injected 200 Right Ctrl key-downs. On each one it posted itself a message from inside the hook and read `GetAsyncKeyState` when that message ran, which is the earliest a timer tick on the hook thread could run.
- Inside the hook: "up" 200 of 200 times (expected: the hook runs before the key state updates).
- Right after the hook returned: **"up" 88 of 200 times**.
- 1 ms later, and 5 ms later: "up" 0 times.

`HookThread.OnTick` (every 100 ms) reads `GetAsyncKeyState(holdVk)` for two watchdogs:
- **(a)** Pressed or Recording with the key "up" becomes `Tick(down: false)`. In Pressed within `TapMs` that is a **ShortTap discard**. The hold's next auto-repeat key-down then starts a fresh hold, so the recording restarts and the first ~100 ms is lost. And `DiscardCurrent(ShortTap)` raises `HotkeyTapped`, which **retries whatever error card is showing**. That is the E2E flake from PR5 run 10: a Retry fired at the start of CopyDuringRestore's hold, after TapDuringProcessing had left a Cancelled card with Retry on screen.
- **(b)** `HoldKeyDown` while not Pressed/Recording, with the key "up", feeds a synthetic `HoldUp`. In ProcessingPressed that is a **CancelProcessing** within `TapMs`: a press during processing cancels the run on its own. In Latched with the ending press, it commits the latch slightly early.

The window is short: a tick has to land within about 1 ms of a key-down. But a 100 ms tick against a 1 ms window over every hold is a real rate, and the cost (a lost first syllable, a surprise Retry, a cancelled run) is high.

**3. Paste-last "intermittently copying"** has not been reproduced. Paste-last passed in every E2E run since PR5 was merged (34/34 on 2026-10-28), and `OutputNotPasted` now logs the reason whenever a paste becomes a copy. No fix without a cause: it stays deferred, and the next occurrence's logged reason is the evidence.

## Changes

### A. A foreground change must be visible and must last (Core rule, App wiring)
New `Yolime.Core.Hotkeys.ForegroundRule`, pure and unit-tested:
- `Observe(ForegroundSample s, long atMs)` with `ForegroundSample(int Pid, bool Visible, bool Cloaked)`.
  - Our capture's pid clears any pending switch.
  - An invisible or cloaked window is ignored: it can't be what the user switched to.
  - Any other window starts a pending switch at `atMs`, unless one is already pending.
- `Tick(ForegroundSample now, long atMs)` returns true, so the caller feeds `ForegroundChanged`, once a switch has been pending for at least `SettleMs = 200` **and** the current foreground (re-read at the tick) is still visible, not cloaked, and in another process. When the re-read shows our process again, the pending switch is cleared.
- `Reset()` runs when a hold starts or ends.

Wiring in `HookThread`:
- `ForegroundProc` builds the sample (pid, `IsWindowVisible`, `DWMWA_CLOAKED`) and calls `Observe` instead of feeding directly, only while Pressed, Recording or Latched, as today.
- `OnTick` samples `GetForegroundWindow()` and feeds `ForegroundChanged` when `Tick` says so.
- The preview card's foreground rule (`_preview.Foreground`) gets the same visibility filter (an invisible flash mustn't end a preview), but no settle time: a preview should end as soon as the user really leaves.

**Cost of the settle:** a real switch away discards up to about 300 ms later (200 ms settle plus up to one 100 ms tick). Meanwhile the hold keeps recording, and the end-target check still protects the paste, so nothing goes to the wrong window. Sound in the user's new app for 300 ms is the only effect.

### B. A key reads as up only twice in a row (Core rule, App wiring)
New `Yolime.Core.Hotkeys.KeyUpDebounce`:
- `bool Released(bool readUp)` is true only on the second consecutive "up" reading.
- `Reset()` runs on every hook key-down for the hold key.

`HookThread.OnTick` passes each `GetAsyncKeyState` reading through it, and **both** watchdogs, (a) and (b), use the debounced value.

Cost: a missed key-up (from an elevated window) is detected one tick later, about 100 ms.

### C. A tap retries only a card that was showing before the key went down
Defence in depth for the retry path, whatever misreads a hold as a tap in future:
- `Capture` records `StartedAtMs` (the coordinator's `NowMs`) at StartCapture.
- `HotkeyTapped` becomes `Action<long> pressedAtMs`.
- `OverlayModel` keeps `ErrorShownAtMs` for the current error card, from `RunFinished.AtMs`.
- `OverlayController.HotkeyTapped(pressedAt)` retries only if `ErrorShownAtMs < pressedAt`. Both are the same Stopwatch-based clock in production.

## Tests (TDD, each written to fail first)
- `ForegroundRuleTests`:
  - an invisible flash (`rgnupdt`'s shape) never fires;
  - a cloaked window never fires;
  - a visible switch that returns within 200 ms never fires;
  - a visible switch held for 200 ms fires once at the first tick after it;
  - a switch to a window of our own process clears a pending one;
  - `Reset` clears a pending switch.
- `KeyUpDebounceTests`: one "up" never releases; two in a row release; a key-down between them resets.
- `HoldStateMachine` is unchanged. A coordinator test checks that a tap pressed **before** an error card appeared doesn't retry it, and that a tap after it does.
- E2E (Sandbox): a new scenario, `HiddenForegroundFlash_KeepsTheHold`. Mid-hold, the harness starts a process with a hidden console window that takes the foreground (`cmd /c ping -n 1 127.0.0.1` via `CreateProcess` with `SW_HIDE`), and the hold must still paste. First check, in the harness, that this actually fires `EVENT_SYSTEM_FOREGROUND`; if it doesn't, the scenario reproduces nothing and gets dropped, with a note.
- Existing E2E: the full suite, run three times so a flake in TapDuringProcessing or CopyDuringRestore shows.

## Not changed
- Paste-last copying: deferred until its logged reason is seen.
- The harness's 20 s wait after ready stays: it protects every scenario from logon-time flashes, and the new E2E covers the fix directly.

## v2 rulings (three reviews: platform and correctness, UI/UX, architecture and tests)

These replace the matching parts of "Changes" above where they differ.

1. **Correction to the evidence.** A misread costs more than a first syllable. After a misread ShortTap, `_holdDown` is false, so the real key-up is ignored and the hold restarts only at the first auto-repeat (about 500 ms by default). Injected E2E input has no auto-repeat, so there the hold is lost. In Latched, a misread on the press that ends the latch commits, and the auto-repeat 500 ms later then lands as a press in Processing past the 400 ms debounce, so its release cancels the run.
2. **B moves into `HoldStateMachine`.** `OnTick` reads the key once per tick and feeds `Tick(down)`. It does so whenever the state isn't Idle or the machine thinks the key is down. The machine owns both watchdogs:
   - (a) a missed key-up in Pressed or Recording;
   - (b) the synthetic HoldUp in the other states, now `HoldKeyDown && !down`.
   Both act only on the **second consecutive** "up" reading. A "down" reading and every HoldDown reset the count. This fixes the double read within one tick (both reviewers caught it) and makes the exact race a machine test: HoldDown, then `Tick(down:false)` at 1 ms, gives no discard; ProcessingPressed gives no CancelProcessing. `Watchdog_Before300_Discards` becomes a two-reading test.
3. **C is replaced.** All three reviews found that the time-stamp rule wouldn't have stopped the observed flake, because the card was already up before the key went down. Instead, `Discard` carries `FromWatchdog`, and the coordinator raises `HotkeyTapped` (tap-to-retry) only for a ShortTap whose key-up the hook actually saw. Cost: a real tap whose key-up went to an elevated window doesn't retry. A tap can't usually be made there anyway, since the hook wouldn't see its key-down either. Pinned by a machine test and a coordinator test.
4. **A: visibility is judged at the re-read, not when the event fires.**
   - Any foreground event from a pid other than `_capturePid` (the target app at key-down) starts a pending switch, whatever its visibility. A second foreign event keeps the original time. Windows that are cloaked or hidden when activated and appear a moment later (UWP launch, virtual desktop swipes) are no longer missed.
   - At each tick, `GetForegroundWindow()` is re-read:
     - `_capturePid` again: the pending switch is cleared;
     - hwnd 0, an invisible or cloaked window, or a shell tray class: undecided (neither fires nor clears);
     - a real window in another process, pending for at least 200 ms: fires once and clears.
   - The rule is consulted only in Pressed, Recording or Latched, and reset at hold start.
   - "Real window" is one shared predicate in Core, `ForegroundSample.IsReal`: visible, not cloaked, not a tray class. AppHost's last-app tracker moves onto it.
5. **The preview card uses the same rule with a settle of 0.** An invisible flash doesn't end the preview, and a real switch ends it at the next tick (at most 100 ms). The decision moves into Core (`PreviewCapture.Foreground` takes the sample) and is tested there.
6. **No loud discard from the settle.** `HotkeyInput` carries `SwitchPending`. While a switch is pending, Pressed doesn't turn into Recording at 300 ms. A switch that settles into a discard stays silent, with no Start chime, Listening card or context read, as today. A flash that clears shows Recording up to 200 ms late.
7. **Key-down during a flash.** HookThread remembers the pid of the last *real* foreground window. If the foreground at key-down isn't real, `_capturePid` is that pid instead.
8. **Logging (content-free):**
   - `HoldDiscarded` with `reason`, `watchdog` and `ms`, for every discard;
   - `ForegroundSettled` (`ms` pending) when a pending switch clears without firing.
   Both bugs would have been visible in the field with these.
9. **E2E:**
   - `ForegroundFlash_KeepsTheHold`. The harness calls `AllowSetForegroundWindow` and starts itself with `--flash <formHwnd>` mid-hold. That child creates a hidden top-level window, calls `SetForegroundWindow` on it, waits 80 ms, gives the foreground back to the form and exits. The hold must paste.
   - `ForegroundSwitch_DiscardsTheHold`. The same, but with a visible window kept for 1 s. The run's outcome must be Discarded.
   - Both are run against current main first, and the flash scenario must fail there.
   - Race B has no E2E (a 1 ms window); the machine tests pin it.
10. **Documented residuals:**
    - a 1×1 or off-screen "visible" window that keeps the foreground still discards;
    - in Latched, up to 300 ms of speech after a real switch is kept (and goes to the clipboard through the end-target check);
    - a hidden window that keeps the foreground leaves the hold running, and the end-target check copies instead of pasting.
