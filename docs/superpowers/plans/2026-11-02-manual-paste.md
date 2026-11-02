# Paste automatically (manual paste), 2026-11-02

Bryan asked for a setting to choose between pasting automatically and pasting by hand. The copy-only path already
existed (`DeliveryIntent.ClipboardOnly`, `CopyAsync`, the "Copied" notice), so the setting picks that intent for
unattended runs. Three reviews (correctness, UI/UX, tests and repo rules) shaped the rulings below. Manual mode turns
several rare side effects into ones that happen on every run.

## Behaviour
- **`AutoPaste`** (bool, default `true`) in Settings > General, as "Paste automatically", above "Paste last result".
  It needs no migration and no range check: a missing value takes the default, and a bad one is reset field by field.
- **Off:** every unattended result is delivered as `ClipboardOnly`. That covers Dictate, Prompt, Reply, Raw, the
  rewrite fallbacks, Retry and runs from a transcript. The clipboard isn't put back. A password field is still refused.
- **Still paste**, because each is an explicit request:
  - Insert on the preview card (the Shift gesture or Always preview);
  - Paste last result (the chord and the tray);
  - History's Re-insert.

  An expired preview copies nothing (unchanged); the text is in History.

## Review rulings
1. **One notice, not two.** A copy caused by the setting is quiet (`QuietCopy`). Otherwise the executor's "Copied"
   card and the run's Done card would both show, Narrator would read both, and a full-screen app would get a balloon
   on every run. The admin-window card isn't shown in manual mode, and `WarnIfElevatedOnce` is skipped: nothing was
   going to be pasted anyway.
2. **Win+V history.** A manual copy stays in Windows clipboard history, like today's "copied, not pasted" results,
   except while Pause history is on. Then it's set with `excludeFromHistory`, through
   `OutputItem.KeepOutOfClipboardHistory`, so "yolime keeps nothing" stays true. Cloud upload is always blocked. The
   help text and the README say this.
3. **Copy raw** is offered after a rewritten result that was copied, as well as one that was pasted. After a copy,
   the pill ends with the 4 s Copied card instead of lingering alone.
4. **Reply's click-a-field wait** never starts in manual mode: the planner's ClipboardOnly rule comes before
   NotEditable. A planner test and an executor test pin this order.
5. **Retry** had been copied only because it has no start snapshot. The override doesn't depend on that, and a test
   covers it.
6. **One settings snapshot per run** in `RewriteAndDeliverAsync`.
7. **Copy.** The paste-last help no longer says "again", and neither does `PasteLastUnavailable`. Always preview's
   help says a preview is how to paste a single run in manual mode.
8. **Follow-up, done:** the "Ctrl+V" hint was wrong in Warp, Alacritty, mintty and PuTTY. The bug predates this
   change, but manual mode made it frequent. The follow-up PR carries the paste chord (an enum) from the planner
   through `OutputExecutor` to `RunResult` and `Notice`, so the Done card, its narration and the notices name the key
   of the window in front ("Ctrl+Shift+V", "Shift+Insert"), and say Ctrl+V when that is unknown.

## Tests
- Core:
  - `Pipeline/AutoPasteTests` (new);
  - `RerunTests`;
  - `OutputExecutorTests`: copy by request, and kept out of history;
  - `PendingPasteTests`;
  - `OutputPlannerTests`: rule order;
  - `OverlayModelTests`: Copy raw after a copy, and the pill's lifetime;
  - `SettingsStoreTests`: default, round-trip, null reset.
- E2E: `Settings_PasteAutomaticallyOff_Copies` toggles the switch through UIA, dictates, and checks that the box is
  empty and the clipboard keeps the transcript. It then switches the setting back on.
