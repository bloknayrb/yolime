# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

yolime is a personal, Windows-only, hold-to-talk voice writer in the style of heylemon.ai. You hold a key, speak, and the text is pasted at the cursor in any app. It has four modes: **Dictate** (clean dictation), **Prompt** (rambling speech turned into a structured AI prompt), **Reply** (a draft from the copied or selected message) and **Raw**. Stack: C# / .NET 10 / WinUI 3 on Windows App SDK, unpackaged and self-contained, win-x64. Cloud STT is OpenAI or Groq; rewriting uses Claude through plain `HttpClient`, with no SDK. No Lemon branding, copy or assets.

**The approved design lives in the plan, not here:** `docs/plan.md`. It covers 9 PRs, every behaviour rule (hotkeys, clipboard, terminals, reply context, overlay), and the verification matrix. Read it before starting a PR. Session handoff notes are in `.remember/now.md` (not committed).

## Status

The eight plan steps after the spike are all on `main` (as of 2026-10-28): core pipeline, hook and injection, LLM modes, overlay, onboarding and settings, history, latency (#8) and publish (#10), plus two follow-up fixes, hold races (#9) and the preview gesture (#11). `spike/overlay` was the throwaway spike and stays unmerged. Each step's plan, with its review rulings and deferrals, is in `docs/superpowers/plans/`. The README describes what the app actually does; where it and `docs/plan.md` differ, the README is right.

Deferred to their own PRs, each with constraints recorded in `2026-10-28-latency.md`:
- delayed clipboard rendering;
- streaming upload, only if the timing log shows upload time matters;
- an optional warm mic.

Install for daily use with `yolime-setup.exe` (`tools/build-setup.ps1`, Inno Setup; `installer/yolime.iss` only wraps
`tools/install.ps1`, which does the work) or with `tools/install.ps1` directly (see the README). Both are tested by
`tests/Install/run-install-test.ps1`, in Windows Sandbox.

Layout:
- `src/Yolime.Core`: net10.0, no UI. All decision logic lives here so it can be unit-tested: the hold state machine, output planner, clipboard policy, context filters, prompts, pipeline, settings and history.
- `src/Yolime.App`: WinUI 3. Thin Win32 wrappers only: keyboard hook, injection, UIA, clipboard, WASAPI, overlay windows, tray.
- `tests/Yolime.Core.Tests`: xUnit.
- `tests/Yolime.E2E`: built with `YOLIME_E2E`, run only in Windows Sandbox (`tests/Yolime.E2E/run-e2e.ps1`).
- `tools/`: `install.ps1`, `build-setup.ps1` (also run in CI), `check-publish.ps1` (also run in CI), `timing-report.ps1`.
- `installer/yolime.iss`: the setup.exe wrapper.

## Commands

```powershell
dotnet build                                   # Yolime.slnx at the repo root
dotnet test                                    # all tests (Microsoft.Testing.Platform, set in global.json)
dotnet test --filter "FullyQualifiedName~HoldStateMachine"   # one class or test; a filter matching nothing exits with code 8
dotnet build -p:YolimeE2E=true                 # E2E build: defines YOLIME_E2E, outputs to bin\e2e\ and obj\e2e\
dotnet test -p:YolimeE2E=true
```

- `YolimeE2E` and `YolimeUnlock` must be `-p:` properties. The build refuses them as environment variables, and refuses an E2E Release or publish build.
- Restore is locked. To add a package, add a `PackageVersion` pin, run `dotnet restore -p:YolimeUnlock=true`, and commit the changed `packages.lock.json` files. CI fails when a lock file is missing or stale.
- Solution builds put the App under `src\Yolime.App\bin\x64\<Config>\…\win-x64\`, or `bin\e2e\x64\Debug\…` for E2E. Project builds (`dotnet build src/Yolime.App`) drop the `x64` folder.
- E2E-only, one Raw dictation from a WAV file, with no window. Exit codes: 0 done, 2 no speech, 3 STT failed, 4 output failed, 5 capture failed.
  ```
  Yolime.App.exe --test-audio x.wav [--test-out out.txt] [--test-data-dir dir] [--test-transcript "fixed text"]
  ```
  - **Without `--test-out`, it writes to the real clipboard.**
  - Data goes to a fresh temp folder unless `--test-data-dir` is given.
  - `--test-transcript` skips the network.
  - PowerShell doesn't wait for a WinExe, so use bash or `Start-Process -Wait -PassThru` to read the exit code.
- **PR2 end-to-end (hold, paste and the guards), Windows Sandbox only:** run `pwsh -File tests/Yolime.E2E/run-e2e.ps1`. It builds, stages `artifacts/e2e`, runs the harness in a sealed Sandbox, and prints `artifacts/e2e-results/e2e.json`. The app's `--e2e` mode refuses to run outside the Sandbox user.
- Live STT smoke test: `YOLIME_LIVE=1 dotnet test --filter "FullyQualifiedName~LiveSttSmoke"`. It needs `OPENAI_API_KEY` and costs well under a cent.
- WindowsSandbox has no `.wsb` file association here. Launch with `WindowsSandbox.exe <file>.wsb`. Only one Sandbox runs at a time.

Spike (on the `spike/overlay` branch):
```powershell
dotnet build spike/OverlaySpike                # WinUI XAML compiles with the plain dotnet CLI; no Visual Studio needed
start spike\overlay-spike.wsb                  # runs every variant in Windows Sandbox → spike\results\*.txt|png + DONE
```

## Hard rules for this repo

- **Never drive the real desktop.** Anything that sends synthetic keys or clicks, or changes the foreground window, runs only in Windows Sandbox (`*.wsb` with networking off, the code folder mapped read-only and a separate writable results folder). The user is usually working on this machine.
- **Never use Notepad as a test target.** Windows 11 Notepad restores the user's real tabs. Harnesses launch and kill their own target window (`spike/test-target.ps1`).
- **Test-only code compiles only under `#if YOLIME_E2E`**: the `--test-*` switches, accepting `LLKHF_INJECTED` keys, the App's use of the file audio source, and `FakeTranscriptionSession`. `FileAudioSource` itself stays in Core, because Retry and the tests need it. Release builds must not contain the rest, and `ReleaseHasNoTestCodeTests` checks that in CI. `YOLIME_LIVE=1` (live API smoke tests) is read only by test projects.
- **Logs never contain content**: no transcripts, context, prompts, HTTP bodies or headers, and no key codes. Only state changes, timings, character counts and status codes. A unit test enforces this.
- **Dependencies are pinned centrally** in `Directory.Packages.props` (exact versions, transitive pinning), restored from nuget.org only (`nuget.config` source mapping). Add a `PackageVersion` there and a version-less `PackageReference` in the project. Product projects commit `packages.lock.json` and set `RestoreLockedMode`.
- Before PR2 (the keyboard hook), check whether Smart App Control is on; it may block an unsigned exe that hooks the keyboard.
- **Claude API:** no `temperature`. Set `thinking` and `effort` explicitly for each mode, as in the plan. Check model IDs and parameters against the claude-api skill before changing them.
- Line endings are LF (`.gitattributes`). Never use `Set-Content` for repo files.

## Workflow

- Branch `type/short-description`, conventional commits, never commit to `main`. Every change goes through a PR. Plans get multi-angle adversarial review (always including UI/UX) before implementation, and PRs get an agent review before merging.
- The .NET SDK is pinned in `global.json`. yolime reads `OPENAI_API_KEY` and `YOLIME_ANTHROPIC_API_KEY` and ignores `ANTHROPIC_API_KEY` on purpose, because Claude Code reads that one.
