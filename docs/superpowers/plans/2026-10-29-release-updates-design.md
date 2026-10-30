# yolime: card fix, in-app updates, first public release, design pass (v5)

## Context
Bryan, 2026-10-29:
- The "didn't hear anything" card's buttons are cut off at the bottom. Run a design pass (/design, the `design:design-critique` skill) while working out the quality-of-life list.
- "make this the first release, and also plan out automatic updating from within the app".

**His choices:**
- yolime goes public as a **fresh public repo** (one squashed commit); the current repo stays private as the archive.
- Updates **ask, then install**.
- **No release signing:** releases come only from CI on `main`; a newer version can add signing later.
- No code signing.
- No big changes.

v5 folds in three rounds of adversarial review (platform, UI/UX, security, scope and tests, integration). Calls I made myself are marked **Ruling**.

**Bryan's steps** (I'll prompt for each when it comes up):
- **GitHub account settings:** "Keep my email addresses private", noreply as the web-merge email, and "Block command line pushes that expose my email".
- **Approve repos:** the throwaway public test repo; then, after reading the audit, the rename and the new public repo.
- **v0.1.0:** get past the browser's "Keep" prompt and SmartScreen once.

**Order:**
1. Card fix.
2. Updater, in three PRs.
3. Release workflow.
4. Audit, cleanup and account settings.
5. Dry run in the test repo.
6. Fresh public repo.
7. v0.1.0.
8. v0.1.1, a no-op through the updater.

The design tour and critique run alongside steps 2–4. The tour PR and the findings page land before the cut in step 6. The defect-fix PRs come after v0.1.1, in the public repo, and ship as v0.1.x through the updater.

**Every PR goes through the repo workflow:** test first, Sandbox E2E, agent review plus re-review of fix commits, CI green on the head, squash-merge.

**Step 0 of execution:** save to project memory:
- these decisions;
- that agents can read the user environment, which holds secrets;
- the feedback: "a plan revised with review fixes gets a whole-plan review before it's presented".

## Part 1: the clipped card buttons (PR `fix/overlay-card-fit`)
**Cause.** `OverlayWindow.Show` (`src/Yolime.App/Shell/OverlayWindow.cs:92-122`) measures once, at 560 DIP and before moving the window. It sizes the window to `ceil(desired*scale)` and never measures again. The island then re-lays out at the window's width. WinUI's layout rounding can leave the measured title up to 0.5 px short, so it re-wraps and the buttons drop below the edge. This is unconfirmed until instrumented.

**Test first:**
- **Probe.** An E2E-only island probe (numbers only): the content's real `ActualHeight` after layout against the window's client height, plus the scale.
- **`show-card` file hook.** Modelled on `stall-ui`, it posts a synthetic `RunFinished` (kind, mode, copied, retryable with a fake history id) through the real `Notices.For`/`Card` path.
- **`Cards_FitTheirWindow`.** Runs in the main launch and sweeps a (kind, mode, copied, retryable) table, covering every Rewrite* wording plus the preview's header and buttons. Three oracles:
  - the probe;
  - UIA: buttons exist and aren't offscreen, their bottoms sit within the padding, and they're all the same height;
  - background pixels in the centre columns just above the 1-DIP border.

  Read under PMv2. It fails if the scale is 1.0; the Sandbox runs at 1.25.
- **Negative control.** An E2E-only "shrink by 40 px" switch, red before and after the fix.
- **Gate.** If the scenario is green on `main`, stop and re-diagnose.
- **`-Only`** gains own-launch scenarios here (today `tests/Yolime.E2E/Program.cs:53-54` returns before them), for one launch at 150% text scale that also checks width.

**Fix, synchronously in `Show`:**
1. While hidden, and only when the monitor changes, move to the target monitor (`SWP_NOSIZE`).
2. Measure at 560 using `GetDpiForMonitor` of the target monitor (`s`).
3. `w = ceil((W+1)*s)`.
4. Measure again at `w/s` to get `h`.
5. `SetWindowPos`, the region, `MoveAndResize`, and update `Bounds`.

**Ruling:** no scale-change or text-scale handlers. They can't be driven in the Sandbox, Bryan has one monitor, and the next card measures fresh anyway. Markers go into `ReleaseHasNoTestCodeTests`.

## Part 2: in-app updates (three PRs)
**Trust model.** A release is trusted when all of these hold:
- it's the newest **published** (non-draft) release of `bloknayrb/yolime`;
- its tag is `vX.Y.Z` and strictly newer than the running version;
- its two assets were uploaded by `github-actions[bot]`;
- the setup's SHA-256 matches `SHA256SUMS.txt`;
- the setup's file version equals the tag.

**Not covered** (stated in the README): someone with Bryan's GitHub access, or an agent using his `gh` login, could publish a bad release through CI. The defences are the tag and `main` rulesets, immutable releases, and Bryan's account security. Signing can be added in a later version.

**PR 2a: installer (`feat/update-installer`).**
- `yolime.iss` accepts:
  - `/UPDATE`: passes `-NoLaunch` to `install.ps1`;
  - `/RESULT="<path>"`: passed through as `install.ps1 -ResultFile`;
  - `/NOLAUNCH` (for seeding tests).
- **Single launch point.** Under `/UPDATE`, `DeinitializeSetup` is the only thing that launches. It starts `Yolime\Yolime.App.exe` (working directory `Yolime`) when no `yolime-tray` window exists. Per Inno's docs it runs on every outcome, including `InitializeSetup` returning False and a silent `PrepareToInstall` error (exit 7). It does not run if setup crashes or is killed; that gap is noted. An install test covers the `InitializeSetup`-False case.
- **Result file.** `install.ps1 -ResultFile` writes an outcome code: `ok`, `running`, `files-in-use`, `check-failed`, `swap-failed` or `error`. It's written from a `try/finally` plus a `trap`, so a thrown error still writes one. If no file exists when `DeinitializeSetup` runs, it writes `setup-failed`, which covers outcomes Inno decides itself (refused elevated, `/DIR`, unpack, PowerShell won't start).
- **The running copy.** No pid wait and no self-exit. The silent path already handles a running copy: exit 2, rerun with `-Force`, and `Stop-Copy` asks the tray to Exit. `Stop-Copy` now waits before killing even when it couldn't ask.
- **Ruling: the update uses `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL`.** yolime stays up until the script asks it to exit (Part 2c), so no setup window is needed.
- **Sandbox-only setup variant.** `/DSandboxE2E` makes a third variant. It passes `-AllowE2EPayload` to `install.ps1`'s two `check-publish` calls, plus `-AllowElevated`. `build-setup.ps1` gains a test-only `-SkipCheck`, used for that variant and for the no-`.pri` payload.
- **No elevated relaunch.** `DeinitializeSetup` skips the launch when setup refused to run elevated, so a refused setup never starts yolime elevated.
- **Variant name.** `build-setup.ps1 -SandboxE2E` produces `yolime-setup-<ver>-sandboxe2e.exe`. `check-publish.ps1` gains `-AllowE2EPayload`.
- **Install tests.** Oracle: a process-start trace (WMI `Win32_ProcessStartTrace`) counts launches.
  - `/UPDATE` success: exactly one `Yolime.App` start.
  - `/UPDATE` with a payload refused for having no `.pri`: refused before anything is stopped. Writes `check-failed`, and nothing is launched, since the old copy still runs.
  - **`/UPDATE` failing after the stop** (the case the safety net exists for): a file under `Programs\Yolime` is held open so the swap fails. Writes `swap-failed`, and the old copy is relaunched exactly once. The rename into `Yolime.old` (`install.ps1:281`) moves inside the try, so it is classified as `swap-failed`.
  - `/UPDATE` refused because elevated: writes `setup-failed`, and nothing is launched.
  - `SetupShipped_RefusesE2EPayload`, built with `-SkipCheck` and the shipped defines;
  - the result file after a thrown error.

**PR 2b: Core (`feat/update-core`), unit-tested.**
- **Feed.** Read `releases/latest`. The fixture is hand-written from the API shape and replaced by a recording from the Part 5 test repo. The tag must match `^v(\d+)\.(\d+)\.(\d+)$`.
- **Version.** Only a strictly newer version is offered; an older "latest" is ignored. The current version is the trimmed `ProductVersion` base, with `+sha` optional and shown shortened to 7 characters. A `-pre` suffix counts as older.
- **Names and URLs.** We build the file names ourselves (`yolime-setup-<ver>.exe`, `SHA256SUMS.txt`) and look the two assets up by name, ignoring any others, so a later release that adds a `.sig` still works. Both must have been uploaded by `github-actions[bot]`. Asset ids are parsed as `long`. The release-page URL is built from the version.
- **Sums file.** Parsed by name, case-insensitive, tolerant of CRLF and a BOM.
- **Setup checks.** Its hash, then `FileMajor/Minor/BuildPart` must equal the version and `FilePrivatePart` must be 0 (the `FileVersion` string is space-padded).
- **Offering.** **Ruling:** nothing is offered until `SHA256SUMS.txt` has been fetched, so a release missing its assets is never shown.
- **Outcomes:**
  - "try later" (silent, retry in about an hour): 404; 403 or 429 with the rate limit spent; a release with no assets; no network;
  - refused: a hash or version mismatch;
  - download size is capped.
- **Scheduler** (tested with `FakeTimeProvider`): first check about 1 minute after start, then every 24 h from the saved last check. Skipped while paused and caught up on resume. "Last checked" is shown.
- **Network.**
  - `api.github.com`, User-Agent `yolime/<ver>`, authenticated only in the CI smoke test. **Ruling:** no ETag. One request a day is nowhere near the rate limit, and a 304 with nothing saved behind it would stall an offer.
  - Assets come from `.../releases/assets/{id}` with `Accept: application/octet-stream`, either as a direct 200 or after one 302.
  - The 302's `Location` must be absolute https on the default port, with no user info, and on `release-assets.githubusercontent.com` (or `objects.githubusercontent.com`). That's checked before `Expect`.
  - No headers are carried over, the 302 is disposed, and a timeout token applies.
  - Fake-handler tests cover each case, including a truncated body.
- **Logs** carry status codes and versions only. The feed body, the `Location` and exception messages are content. The updater's events join `NoContentInLogsTests`.

**PR 2c: App and Sandbox scenarios (`feat/updates`).**
- **When it's off:**
  - **Not installed:** the exe folder isn't exactly `GetFolderPath(LocalApplicationData)\Programs\Yolime` (compared with `GetFullPath`, `TrimEnd('\')`, case-insensitive). Settings then says "Updates are off for this copy (not installed)".
  - **Elevated:** "Elevated" means the mandatory label is above Medium, the same test the harness asserts. Shown once per version: "yolime 0.2.0 is available, but this copy runs as administrator, so it can't install it. Exit and start yolime from the Start menu."
- **Offer:**
  - **Balloon,** once per version, info icon: "yolime 0.2.0 is available. Click to install, or use the tray menu."
  - **Tray item:** "Install update 0.2.0…".
  - **Tooltip suffix:** " · update ready", placed before any hotkey problem, within the 127-character limit.
  - **Settings > General:** one bottom row with the version, a "Check for updates automatically" toggle and "Check now". The row's help text shows the result: "Up to date · checked 2 h ago" or "0.2.0 is available [Install]" or "Couldn't reach GitHub".
  - **Balloon clicks.** `TrayIcon` keeps the whole last `Notice`, so a click routes correctly:
    - available → the update window;
    - Updated → the release page;
    - failed → the log.
- **Update window.** A small WinUI window, "Install yolime 0.2.0?", opened from the balloon, the tray item or Settings.
  - **Text:** "Installing closes yolime briefly and reopens it."
  - **Buttons:** **Install now**, **What's new** (opens the browser, window stays), **Not now**.
  - **After Install now**, the window stays open with a progress bar through download and verify:
    - new holds are refused at once, with "Finishing your dictation first…" while a run finishes;
    - an open preview or pending paste ends on its own path;
    - a second click does nothing.
  - **Failures shown in the window:**
    - "Couldn't download 0.2.0. Nothing changed. [Try again]"
    - "The 0.2.0 download didn't match its checksum, so it wasn't installed."

    Afterwards the tray item reads "Install update 0.2.0…" again.
- **Install:**
  1. Delete the old `result.txt` and `setup.log` (stop if that fails), then write the `expected-version` marker. A missing `result.txt` after setup exits counts as a failure.
  2. Launch setup with `/VERYSILENT … /UPDATE /RESULT="<updates>\result.txt" /LOG="<updates>\setup.log"`. `UseShellExecute=false`, an absolute path, `WorkingDirectory` set to the updates folder, and the file held open with `FileShare.Read` until it's launched.
  3. Close the window and show the **Updating card**: its own overlay state, with an info glyph, no buttons, polite narration, and no auto-hide (the deadline in step 6 still applies). Text: "Updating to 0.2.0. yolime will close and reopen in a moment."
  4. yolime stays up, holds still refused, until `install.ps1` asks it to exit through the tray.
  5. If setup exits first (the app is watching the `Process`), read `result.txt`. The Updating card becomes the failure warning: "The update to 0.2.0 didn't install; yolime is still on 0.1.0." Click it to open the log. Holds are re-enabled.
  6. If setup is still running after 5 minutes without asking yolime to exit, the card becomes a warning that can be dismissed, and holds are re-enabled. Setup itself isn't killed.
- **Next start:**
  - It reads the marker and `result.txt`, then deletes both, so the notice shows once.
  - On success, "Updated to 0.2.0." replaces Started (info icon), and HoldKeyReset follows if it's pending.
  - On failure, a warning: "The update to 0.2.0 didn't install, so yolime is still on 0.1.0. Click to open the log." The reason is a fixed enum from the result code. `Notice` gains `Version` and `UpdateFailure`.
  - Downloaded setups are deleted later, with retry.
- **Smart App Control** blocking the new exe means nothing relaunches. That's documented next to the existing SAC paragraph.
- **Sandbox scenarios** run under `run-e2e.ps1 -Updates`, with their own stage and deadline:
  - **Token.** The harness creates the copy under test itself: `CreateRestrictedToken` with BUILTIN\Administrators in SidsToDisable (deny-only, so `IsInRole` and Inno's `IsAdmin` both read false), plus Medium integrity (`S-1-16-8192`), then `CreateProcessAsUser`, which returns the pid. Each scenario asserts both the mandatory label and the deny-only group.
  - **Feed override.** The `e2e.json` feed source swaps the whole network layer, feed request and asset download together, behind one fake. The hash and version checks still run on the file.
  - **E2E settings** come from `%LOCALAPPDATA%\Yolime\e2e.json`, under `#if YOLIME_E2E`. It's a second way in, used only when `--e2e` is absent, still gated on the Sandbox user and still requiring the mic file. It pins `--test-data-dir`, `--e2e-results`, the feed source and the owner/repo override. Arguments win when present, so the existing harness is unchanged. Both copies under test start without arguments. The harness deletes `ready` before expecting a relaunch; `run-N.json` may be overwritten, and the "Updated to" check reads `notices.txt`, which only appends.
  - **Seed.** `install.ps1 -From … -NoLaunch -AllowE2EPayload -AllowElevated`. Payloads are E2E builds, 0.1.0 and 0.1.1 (the second with `-p:Version=0.1.1`).
  - **Scenarios:**
    - `Update_InstallsTheNewerSetup`: start-with-Windows on first. Afterwards it's still on and pointing at the install, 0.1.1 runs from `Yolime`, and "Updated to" is recorded.
    - `Update_RefusedPayload_KeepsRunning`: no `.pri`, so the update is refused before the stop. 0.1.0 keeps running, the Updating card turns into the failure warning (recorded in `overlay.txt`), and holds are re-enabled.
    - `Update_FailsAfterStop_Relaunches`: a file held open in `Programs\Yolime`. 0.1.0 is relaunched, and its next start shows the failure notice.
    - `Update_ChecksumMismatch_Refused`: setup never starts.
    - `Update_Elevated_ShowsTheAdminNotice`: full token.
    - `Update_RefusesHoldsWhileUpdating`: the foreground is a medium-integrity target child started with the same lowered token. A hook can't see input going to a higher-integrity window.
  - **Markers.** The literals of the `e2e.json` reader and the feed override go into `ReleaseHasNoTestCodeTests` and `check-publish`.

## Part 3: release workflow (PR `ci/release`)
- **Trigger and permissions.** `release.yml` runs on `push: tags: v*` only, with top-level `permissions: {}`. `concurrency` is set with `cancel-in-progress: false`, and actions are pinned by SHA.
- **Build job** (`contents: read`, `fetch-depth: 0` plus `origin/main`, no caches):
  - the tag must match `^v\d+\.\d+\.\d+$` and equal `<Version>`;
  - `git rev-parse "<tag>^{commit}"` must be an ancestor of `origin/main`;
  - no *published* release may exist for the tag;
  - build, test, `check-publish`, then `build-setup`, asserting the ISCC version;
  - the ProductVersion must equal the tag plus the commit;
  - copy the setup to `yolime-setup-<ver>.exe`, write a standard `SHA256SUMS.txt`, and upload both as an artifact.
- **Release job** (`contents: write`):
  1. download the artifact;
  2. `gh release create --draft --verify-tag --generate-notes`;
  3. download the assets back and check them against the sums;
  4. publish.
- **Leftover drafts.** The release job, not the build job (which can only read), deletes any leftover draft for the tag, found by id from the releases list, since `releases/tags/` doesn't return drafts. It then looks up the new draft's numeric id with `gh api` and uses that from then on. `gh release create` prints a URL, which for a draft is `untagged-…`.
- **Smoke job.** A third job in `release.yml` (`needs: release`, `contents: read`, authenticated with the job token) runs the Core live smoke against the release just published. The smoke takes owner/repo as parameters. A separate `release: published` workflow wouldn't fire, because events caused by `GITHUB_TOKEN` don't trigger workflows.
- **README:**
  - how to cut a release, and who tags (Bryan, or me when he says so);
  - rollback: run an older setup (a newer `settings.json` may then be refused);
  - only Releases are offered by the updater; CI artifacts aren't.

## Part 4: audit, cleanup and account settings (before anything is public)
- **Audit `main`'s tree:**
  - gitleaks;
  - grep for, and read every hit of: `blokn`, `C:\Users`, `_API_KEY`, "logged in as", `.remember`, `~/.claude`, `OneDrive`, `session_`, `Bryan`, email addresses, and `.wsb` `HostFolder` paths.
- **Cleanup PR:**
  - fix `docs/plan.md:4,13,25` and CLAUDE.md's pointers to this machine and to the plans;
  - add an MIT `LICENSE`;
  - README: drop the "private" line, and update "What is sent where" with the GitHub hosts and what they see (IP, a `yolime/<ver>` User-Agent, once a day).

  Record the TTS audio fixtures and the synthetic eval strings as reviewed and fine.
- **Bryan's account settings** are done before the test repo is created.

## Part 5: dry run in a throwaway public repo (Bryan approves)
- **Setup.** `bloknayrb/yolime-release-test`. Secret scanning and push protection go on while it's still empty. Then push the cleaned tree, re-scanned, with author and committer set to the noreply address: a base commit, a bump commit, and a side-branch commit.
- **Run `release.yml`** for one good release, plus three tags that must be refused: a malformed tag, the side-branch commit, and a version mismatch.
- **From the published release:**
  - record the feed fixture, replaced in Core by a small PR before the Part 6 cut;
  - the release's own smoke job must pass.
- **Ruling:** there's no networked Sandbox update. It would break the "Sandbox networking off" hard rule, and its shipped setup refuses the Sandbox admin anyway. The real-network chain is proven instead by the smoke job (download and verify) and by Bryan's v0.1.1 update in Part 7.
- **Delete** the repo afterwards.

## Part 6: the fresh public repo (nothing left open)
0. **Confirm with Bryan** before steps 2–4.
1. **Nothing in flight.** Every branch is merged or abandoned: `spike/overlay` is throwaway, and the rest are listed in the report. No PR is open.
2. **Rename and repoint.** Rename the private repo to `yolime-archive`. Repoint `origin` to the archive in every checkout and worktree, and check `git remote -v` everywhere. Add a pre-push hook in the old checkout that refuses any push whose remote URL is the public `bloknayrb/yolime`. Pushes to the archive still work. Push no tags from it.
3. **Re-audit the exact tree.** Run gitleaks and the grep list again on the exact `main^{tree}` that will be published, and diff it against the tree audited in Part 4. Create the public repo empty, and turn on secret scanning and push protection before the first push. **Make the single commit in a scratch repo:** `git commit-tree main^{tree}`, with author and committer set to the noreply address (`GIT_COMMITTER_EMAIL` and `user.email`). Push it to the new public `bloknayrb/yolime`.
4. **New clone,** done from a session started outside `GitHub\yolime`, since Windows refuses to rename a folder that's open. Rename the old folder to `yolime-archive`, then run `git worktree repair` there and check its worktrees. Fresh-clone the public repo to `GitHub\yolime`, so the project memory path still matches. Set `user.email` to the noreply address there, and copy `.remember/now.md` across.
5. **Prove the noreply web-merge** on one test PR. Then turn on the rulesets (tags `v*`: create restricted, no update or delete; `main`: PR plus required check), immutable releases, and fork-PR approval.
6. **Trailers.** `Co-Authored-By` and `Claude-Session` stay on future commits. They show session ids, not their content. His call if he wants them off.

## Part 7: v0.1.0 and v0.1.1
- **v0.1.0.** Tag it. Bryan downloads it in the browser and checks it with `Get-FileHash` against the published `SHA256SUMS.txt`. He gets past "Keep" and SmartScreen, then installs over his current copy. He confirms the no-speech card now fits.
- **v0.1.1.** Straight away, a no-op bump. He uses "Check now" and installs it through the update window: the whole chain, proven.

## Part 8: the design pass (the tour PR and the findings page come alongside Parts 2–4, before the Part 6 cut; the defect-fix PRs come after v0.1.1)
**Tour v1 (PR `test/ui-tour`):**
- **Capture settings.** E2E already sets `hideOverlayFromCapture: false`. One step turns it on to prove it works; that image is expected to be black.
- **Image checks.** No other image is uniformly black. Each image's theme matches a probe of `ApplyTheme`.
- **Coverage:**
  - every overlay state;
  - the card table from Part 1;
  - long text: app name, Reply context, a preview over 260 DIP, a URL;
  - the first page of Welcome, Settings and History.
- **Themes.** Light and dark, each by registry change plus app restart. **Ruling:** high contrast and the frame bursts are cut from v1.
- **Capture mechanics.** Per-monitor-aware capture and crop, via `run-e2e.ps1 -Tour`.

**Critique.** Four agents (overlay, Welcome, Settings, History), each following `design:design-critique`. Each gets the images, the copy sources, the layout constants, and a checklist:
- every claim matches a button or key;
- the glyph and urgency fit;
- the card stays up long enough to read;
- there's a key path;
- fixed widths hold long text.

**Ruling on what to fix directly.** Fix it directly when the fix makes the UI do what it already claims: the text fits, a label matches its action, the glyph follows the tray's info/warning split, the copy matches the buttons present. Anything that adds, removes or moves a control, or changes timing or behaviour, goes to Bryan.
- **Fixed directly:**
  - a card saying "Retry" with no Retry button (Core test: text that says Retry implies the button);
  - MicDenied's "Open settings" relabelled to say it opens Windows privacy settings;
  - info notices getting the warning glyph and urgent narration.
- **To Bryan:**
  - queueing a `NoticeOnly` instead of replacing Listening/Working;
  - an Open Settings button on key errors;
  - hover-pause limited to the buttons;
  - a title/detail split;
  - consistent "copied" wording;
  - the preview hint line;
  - a top-aligned icon on wrapped cards.

**Findings page.** An artifact in his explanatory format:
- what's fixed;
- his decisions: the top 5 by value with "leave it" as the default, the rest collapsed;
- a "before" crop and the proposed fix for each ("after" crops added as fixes land);
- the QoL list below, ranked.

The defect PRs go one per area, each re-running only its tour, each released as v0.1.x.

## Appendix: the QoL list (ranked on the findings page)
- **Every day:**
  - an app icon;
  - a richer tray tooltip;
  - a remembered or default mode;
  - the mode name from the first frame;
  - word count on Done.
- **When something goes wrong:**
  - plain-word errors with a next step;
  - a Retry that names its recording;
  - an Undo pill.
- **History:** search and a mode filter, keyboard support, app and duration per row, a re-run you can cancel.
- **Settings:**
  - the version line (done in 2c);
  - provider and model dropdowns;
  - "Test my setup";
  - a mic picker (exists: check);
  - help text under toggles.
- **Polish:**
  - notch at left or right, and per monitor;
  - a "copied, not pasted" sound;
  - theme-aware cards (exists: check).

## Constraints
- **Sandbox only:** synthetic input, theme and text-scale changes, and installs happen only in Windows Sandbox.
- **Test code** is under `#if YOLIME_E2E`, with markers in `ReleaseHasNoTestCodeTests` and `check-publish`.
- **Logs never hold content:** not from the probe, the updater (no feed bodies or `Location`s), or the tour index.
- **Bryan confirms first:** the test repo, the rename, the public repo, and each release tag.

## Verification
- **Part 1:** the fit scenario is red on `main` and green after the fix; the shrink control is red both times; 150% text is green; the full Sandbox E2E, Core and CI are green.
- **Part 2:**
  - 2a: the install tests are green, including the failure after the stop;
  - 2b: the Core tests are green;
  - 2c: the six update scenarios are green under `-Updates`.
- **Part 3, via Part 5:** the release checks refuse the three bad tags, and the good tag publishes.
- **Part 5:** the smoke job passes against the published test release.
- **Part 6:** every remote checked, the noreply merge proven, Bryan approved.
- **Part 7:** v0.1.0 installed from the browser, with the card fitting on his screen; v0.1.1 arrives through the update window.
- **Part 8:** a "before" crop per finding, and "after" crops as fixes merge.
