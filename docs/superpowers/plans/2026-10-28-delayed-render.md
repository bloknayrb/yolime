# feat/delayed-render: restore the clipboard only after the target has read the paste (v1, for review)

PR7's item 3 (`2026-10-28-latency.md`). It was built and then reverted (b8deb35 → 8220536) after two branch reviews. The ruling that moved it here sets five requirements:

1. a dedicated clipboard-owner thread;
2. a success check based on `GetLastError`;
3. a Sandbox check of the sequence number and `WM_CLIPBOARDUPDATE` around a render;
4. no extra wait when the saved clipboard is a password manager's;
5. logging of paste-to-read time.

## Why
After a paste, yolime puts the user's old clipboard back once the target is idle (`WM_NULL` round trip) plus 800 ms. An app that reads the clipboard lazily or slowly (Outlook, Electron apps, RDP or VM windows) can read *after* that, and then pastes the user's old clipboard instead of the dictation. That's worse than any latency. With delayed rendering the paste text is offered as "available, rendered on request". The target's read arrives as `WM_RENDERFORMAT`, so yolime knows when the paste has been read.

## Why the first attempt was reverted, and what changes
- **The owner was the tray window, on yolime's UI thread.** Every app's paste then waited on yolime's UI thread. A stall there (the exit flush, a heavy window) hung another app's paste for about 5 s (the `SendMessage` timeout).
  - **Now:** a dedicated **`ClipboardOwnerThread`**. It's a background thread with its own message-only window (`HWND_MESSAGE`) and its own message loop, and it does nothing but answer clipboard messages: `WM_RENDERFORMAT`, `WM_RENDERALLFORMATS` and `WM_DESTROYCLIPBOARD`.
  - All of yolime's clipboard opens use this window as the owner. Its only blocking work is copying a byte array into an HGLOBAL.
- **The success check was `SetClipboardData(fmt, NULL) == 0`.** For a delayed-render call the documented return isn't a handle, so 0 can't distinguish success from failure.
  - **Now:** `SetLastError(0)` before the call. Success is `GetLastError() == 0` after it. On anything else, write the text outright, which is today's behaviour.
- **Whether a render changes the clipboard sequence number, and whether it fires `WM_CLIPBOARDUPDATE`, was unverified.** The restore only runs while the clipboard is still ours, judged by sequence number.
  - **Now:** a Sandbox probe (step 1) measures both. If a render bumps the sequence number, the read handler records the new number as ours, as the first attempt did.
- **The wait grew for a password manager's clipboard.** Its clear timer could then miss its own copy.
  - **Now:** when the saved clipboard is sensitive, the restore keeps today's idle + 800 ms rule, with no read wait.

## Design
- **`ClipboardOwnerThread` (App):** the thread, the window and a `Post(Action)` to run an open, set or close on it. Rendering runs in the message handler, with no open or close, as the API requires.
- **`IClipboard`** gains:
  - `SetTextAsync(text, excludeFromHistory, delayed: true)` for the transient paste copy;
  - `WaitUntilReadAsync(ourSequence, max, ct)`, which returns the sequence after the read, or 0 if the clipboard was emptied by someone else (a new copy).
  - A copy kept for the user (Copy, or a copied result) is still written outright.
- **`OutputExecutor.RestoreLaterAsync` (Core):** after idle + 800 ms, if the paste was offered delayed and hasn't been read, wait up to **3 s more** for the read. Then restore, with the existing "only if it's still ours" check.
  - If the read came back as 0, someone else's copy replaced ours, so don't restore.
  - Sensitive saved clipboard: no extra wait.
- **`WM_RENDERALLFORMATS`** (yolime exiting while it's still the owner) writes the text for real, so a pending paste isn't lost.
- **Timing (content-free):** `PasteReadMs`, from paste sent to `WM_RENDERFORMAT` (null if never read), plus `RestoreWaitedForRead` (bool). Both go in `RestoreTiming` and the `ClipboardRestored` event.

## Steps
1. **Sandbox probe**, a throwaway harness scenario. The harness owns a message-only window on its own thread. It:
   - offers delayed text;
   - has the target form read it;
   - records the sequence number before, after the offer and after the render, plus `WM_CLIPBOARDUPDATE` counts;
   - checks `GetLastError` after `SetClipboardData(CF_UNICODETEXT, NULL)`.
   The results decide the sequence handling.
2. **Core** (TDD): the executor's restore logic. With a fake clipboard, the tests are:
   - read at 100 ms after the minimum: the restore follows the read;
   - never read: the restore happens at minimum + 3 s;
   - read returns 0 (a new copy): no restore;
   - sensitive: no read wait;
   - a copy for the user: never delayed.
3. **App:** `ClipboardOwnerThread`, `ClipboardGuard` moved onto it, and the render handlers.
4. **E2E (Sandbox):**
   - `SlowReader_GetsTheDictation`: the target form reads the clipboard 1.5 s after the paste, long after idle + 800 ms. It must get the dictation, and the clipboard must be restored afterwards.
   - The existing clipboard scenarios (restore, image restore, copy during restore, password manager) must still pass.
   - `UiThreadStall_DoesNotBlockAPaste`: an E2E-only hook stalls yolime's UI thread for 3 s while a paste is read. The read must complete in under 500 ms.

## v2 rulings (two reviews: Win32 clipboard platform; privacy, UX and architecture)

These replace the matching parts above where they differ.

1. **The owner thread only pumps messages and answers clipboard messages.** Opens, saves, sets and restores stay on pool threads, as today, and pass the owner thread's window to `OpenClipboard`, which is legal. The owner thread never blocks on anything but its message loop. That rules out the deadlock where a slow target holds the clipboard open waiting on a render.
2. **Every message handler catches everything** and logs it content-free, so an exception can't kill the process with an unrenderable format on the clipboard. The window-procedure delegate is rooted.
3. **Shutdown order:**
   - flush the pending restore;
   - post a close to the owner thread, whose `DestroyWindow` triggers `WM_RENDERALLFORMATS`;
   - join the thread with a bounded wait.
   `EndingSession` (`WM_ENDSESSION`) does the same before it returns.
   - In `WM_RENDERALLFORMATS`, the open is retried with a `PeekMessage` loop (`QS_SENDMESSAGE`), never `Sleep`, so a reader that's waiting on us still gets answered. It then checks `GetClipboardOwner() == hwnd` and sets the data with no empty.
4. **The pending render is published inside the open, before** `SetClipboardData(CF_UNICODETEXT, NULL)`. It's cleared if the offer fails, and its sequence number is filled in after the close. That way a monitor reacting to `WM_CLIPBOARDUPDATE` can't ask for a render before the handler knows the text.
5. **The success check.** `SetClipboardData` gets `SetLastError = true`. The offer counts as made when `Marshal.GetLastPInvokeError() == 0` **and** `IsClipboardFormatAvailable(CF_UNICODETEXT)`, checked while the clipboard is still held and never through `GetClipboardData`.
   - Otherwise the text is written outright in the same open, and `DelayedRenderUnavailable` is logged, so the feature can't go silently off again.
   - It's never offered with owner 0 (the E2E sink).
6. **Who read it.** In `WM_RENDERFORMAT`, `GetOpenClipboardWindow()` gives the reader's window, and `GetWindowThreadProcessId` its process:
   - **The target:** the wait ends.
   - **Another process** (a clipboard monitor, a sync tool): the target's own later read can no longer be seen, so the full 3 s wait runs instead of restoring early.
   - **Unknown** (`OpenClipboard(NULL)`): treated as the target.
   - Logged content-free as `PasteReader` (`Target`, `Other` or `Unknown`), plus whether the read came before the paste was sent, which is certainly not the target.
7. **The read is latched per offer**, in a TCS created at the offer, so a read before the wait starts isn't missed.
   - "0" means "emptied", whether by our own empty or a foreign one. Our own empties (restore, next set, clear) set a flag on the guard around `EmptyClipboard`, so a foreign empty (a new user copy) reads as 0 and an own one doesn't.
8. **Ownership survives a render that bumps the sequence number.**
   - The guard tracks "our offer's sequence, or its rendered successor".
   - `RestoreAsync` and `ClearIfOursAsync` accept either.
   - The guard raises `Rendered(seq)`, which the executor marks `Own()`, so freshness never takes our own paste for a user copy.
   - The Sandbox probe measures whether a render bumps the number at all.
9. **After a read, the restore waits for the target to go idle again** (the `WM_NULL` round trip) plus 250 ms. OLE readers (Word, Outlook) open the clipboard once per format they read, and a restore between those reads would give them a mixed paste.
10. **The flush cancels the read wait** (`skipDelay` is passed through). So the exit flush stays inside its 4 s budget, and the next delivery restores at once.
    - The back-to-back case (a second dictation while the first is unread) is accepted and documented: the second delivery restores at once, as today. Holding paste 2 until paste 1 is read would make every quick follow-up slower.
11. **Cut: "no extra wait for a sensitive clipboard".** The review showed it protects nothing. A sensitive original is never written back (it's cleared), so during the wait the clipboard holds the dictation, not the password, and the password manager's clear timer has nothing left to miss. All the rule did was give a slow reader an empty clipboard. Recorded so the PR7 ruling's requirement 4 isn't silently dropped.
12. **Remote and VM targets keep today's rule** (`RemoteRestoreDelay`, no delayed offer). Their clipboard goes through the redirector (rdpclip), whose read isn't the target app's anyway.
13. **Hygiene.** The byte[] the owner holds is wiped after a render and on `WM_DESTROYCLIPBOARD`.
14. **The Reply context reader checks freshness before it opens the clipboard**, using `GetClipboardSequenceNumber` with no open. Today it's safe only because yolime's markers make it return before `GetClipboardData`. The invariant "nothing in yolime reads its own pending paste" is now written down, and the check enforces it.
15. **Order of work:**
    1. **The Sandbox probe:**
       - the sequence number before the offer, after the close, inside the handler after the set, and after the reader's close;
       - the `WM_CLIPBOARDUPDATE` counts;
       - a `CF_TEXT` reader;
       - `GetLastError` after the NULL set;
       - kill-while-owning (what the next reader gets);
       - the render timeout with a stalled owner;
       - Win+V history turned on, to see whether it renders early.
    2. Core, test-first:
       - a read before or after the minimum;
       - never read;
       - a foreign versus own empty;
       - a render bump followed by a flush, the exit flush or the next delivery;
       - flush during the read wait;
       - reader Target versus Other;
       - the idle-plus-grace wait after a read.
    3. App: the owner thread and the handlers.
    4. E2E:
       - `SlowReader_GetsTheDictation`: a timer-delayed read on an idle UI thread. Run it against the current code first, where it must fail. No `ClipText()` during the wait.
       - `UiThreadStall_DoesNotBlockAPaste`, with a variant that copies during the stall.
       - a helper process that reads on `WM_CLIPBOARDUPDATE` (records the outcome);
       - a Reply hold during the pending window;
       - exit during the read wait (the original is restored);
       - two quick dictations into the slow reader (documents the accepted outcome).

## Rulings made while building

- **Which process is "the target".** The guard isn't told the target, and the port stays `WaitUntilReadAsync(ours, max, ct)`. In `WM_RENDERFORMAT` the reader counts as the target when its process owns the foreground window. The paste went to the foreground window, checked just before Ctrl+V. *If wrong:* a target that reads from a helper process counts as `Other` and gets the full 3 s wait. That's slower, never wrong.
- **No `Rendered(seq)` event (ruling 8).** The first probe found that a render doesn't change the sequence number. The executor still marks a moved number as ours (`ARenderedSequence_IsStillOurs`), and the guard's restore and clear accept the offer's rendered successor (`StillOurs`), so a bump on some other Windows build would still be handled.
- **"Read before the paste was sent" isn't a separate log field.** `readAfterOffer` covers it: the offer-to-paste gap is the few ms of the Output lap, so a reading below that came before the paste.
- **`WM_RENDERALLFORMATS` retries its open for up to 500 ms**, pumping sent messages with `PeekMessage(PM_QS_SENDMESSAGE)` and `Thread.Yield`, never `Sleep`. The flush has already restored the original by then, so this path only matters when the flush ran out of time.
- **Our own empty ends an offer as `TimedOut`, not `Emptied`.** Nobody is normally waiting by then: the restore, the next paste and a clear all come after the wait or flush it. A late waiter then restores under the usual "still ours" check.
- **E2E scope.** Built: `SlowReader_GetsTheDictation` (run against main first), `UiThreadStall_DoesNotBlockAPaste` and `TwoQuickDictations_IntoASlowReader`, through `SlowPasteBox`, which reads 1.5 s after Ctrl+V. Not built as Sandbox scenarios, with what covers them instead:
  - the clipboard-listener reader: Core's `AReadByAnotherProcess_StillWaitsTheFullTime` and the probe's history case;
  - a Reply hold during the pending window: `ContextReader` now checks freshness before it opens the clipboard, and the offer carries the exclusion markers that make `ReadTextAsync` return before `GetClipboardData`;
  - exit during the read wait: Core's `TheFlush_EndsTheReadWait`, plus `WM_RENDERALLFORMATS` for a flush that times out.
  The copy-during-stall variant isn't built either. `CopyDuringRestore_NotOverwritten` still covers a copy during the wait.

## Probe results (Sandbox, second run)

- **Killed while owning (B).** The offer from another thread succeeded (`lastError=0`, format available). After a kill with the paste still unrendered, the next reader opened the clipboard and got **no text**: Windows dropped the format and bumped the sequence number. *Consequence:* if yolime crashes during the pending window, the paste is lost and the clipboard is empty. Before this change, a crash in the 800 ms window left the dictation there instead. Either way the user's original was already gone, so this is accepted rather than guarded: a crash already loses the restore.
- **History on, with yolime's markers (D2).** Clipboard history **never rendered** the offer in 6 s. With `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory=0` set, Win+V doesn't read the paste early, so the read the restore waits for is still the target's.
- **History on, no markers (D3, the control).** History rendered the offer at about 320 ms, from its own process, and the sequence number stayed the same through the render (20 before, 20 after the set). That's the second time a render was seen not to move the sequence number.
- **SlowReader on main.** With only the new harness on `origin/main` (6b41ee0), `SlowReader_GetsTheDictation` failed with "the slow reader got 'sentinel-clipboard'": today a target that reads 1.5 s after the paste pastes the user's old clipboard.

## Branch review: what was fixed, what stands

**Fixed (29cc201):**
- `WM_RENDERALLFORMATS` could write the wiped offer over a late restore on exit.
- `Offer`'s render fields were not volatile.
- Four tests that couldn't fail without the feature:
  - the flush test;
  - the next-delivery test;
  - the minimum-wait test;
  - the UI-stall E2E, which now checks that the read began inside the stall.

**Stands, as rulings:**
- **The owner is always destroyed after the flush, even one that timed out.** 29cc201 kept it alive for a still-running restore. Its re-review showed that makes things worse: the process exits at once anyway, and a teardown with no `WM_RENDERALLFORMATS` leaves an offer nobody renders, losing both the paste and the original. With the render-all re-check, whichever of the late restore and the render-all gets the clipboard first leaves something usable. Reverted.
- **The post-read grace is measured from the end of the minimum, not from the read.** A read before 800 ms still gets idle + 250 ms after the minimum, so every restore is about 260 ms later than before. Nobody waits on the restore (the next delivery flushes it), and measuring from the read would mix the guard's clock with the executor's injectable one. *Cost if wrong:* a quarter-second longer window where the dictation sits on the clipboard.
- **A foreign copy before the wait starts logs `read=NotOffered`, not `Emptied`.** Behaviour is right: the restore's ownership check skips it. Only the telemetry label is off.
- **The sequence is read after `CloseClipboard`, in the offer as in `SetTextAsync`.** This is pre-existing, not part of this change.
- **Packaged apps.** UWP and packaged apps show their foreground window under ApplicationFrameHost, so their reads likely count as `Other` and get the full 3 s wait. That's slower, never wrong.
- **A crash during the pending window leaves the clipboard empty** (probe case B). Accepted, as recorded above.
- **`TwoQuickDictations_IntoASlowReader` asserts only that the user's clipboard comes back.** What the first read gets is the accepted outcome of ruling 10, so it isn't asserted.
- **No Core test covers a flush during the post-read grace.** It goes through the same `skipDelay` token as the waits that are tested.
- **The `WM_RENDERALLFORMATS` race fix is verified by reading, not by a test.** Reproducing it needs a restore contending for the clipboard at the moment the owner window is destroyed. Nothing in the Sandbox harness can schedule that.
