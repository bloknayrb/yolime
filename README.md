# yolime

Hold a key, speak, and polished text appears at your cursor in any Windows app.

Personal project. Windows 11 x64, .NET 10, WinUI 3. The design is in [docs/plan.md](docs/plan.md); where
this README and the plan differ, this README describes what the app does.

## Install, upgrade, uninstall

### With yolime-setup.exe

Double-click `yolime-setup-<version>.exe` (not "Run as administrator"). It asks one thing, whether yolime should start
when you sign in, then installs to `%LOCALAPPDATA%\Programs\Yolime`, adds **yolime** to the Start menu and to
Settings > Apps, and starts it in the tray. Running a newer one upgrades in place; if yolime is running, setup offers
to close it the way its tray's Exit does (a dictation in progress isn't pasted, but stays in History to retry).

- **Getting it:** `pwsh -File tools\build-setup.ps1` builds `artifacts\setup\yolime-setup-<version>.exe` (it needs the
  .NET 10 SDK and Inno Setup: `winget install JRSoftware.InnoSetup --scope user`). Every push to `main` also builds one
  in CI: the run's `yolime-setup` artifact, a zip kept 14 days, with the exe's SHA-256 in the run summary.
- **"Windows protected your PC":** a downloaded or zip-extracted setup is unsigned and carries the download mark, so
  SmartScreen shows this for every new build. Click **More info > Run anyway**. One you built yourself doesn't prompt.
- **Uninstall** from **Settings > Apps > yolime**. It asks whether to delete your settings, history, voice recordings
  and saved keys too; No keeps them for a later install.
- The version is `<Version>` in `src/Yolime.App/Yolime.App.csproj` plus the commit, e.g. `0.1.0+abc1234`.
- Setup and the script below install the same way, so either upgrades or uninstalls what the other installed. After
  `install.ps1 -Uninstall`, the Apps entry stays until you uninstall from it too (it then has nothing left to remove).
- Silent: `/VERYSILENT /SUPPRESSMSGBOXES` (closes a running yolime without asking; add `/STARTUP=on` or `off`), and
  `/REMOVEDATA` for the uninstaller. Logs: `%TEMP%\Setup Log <date> #<n>.txt`.

### With the script

From a normal (not "Run as administrator") PowerShell, in the repo:

```powershell
pwsh -File tools\install.ps1                                  # PowerShell 7
powershell -ExecutionPolicy Bypass -File tools\install.ps1    # Windows PowerShell 5.1 (scripts are off by default)
```

This publishes a Release build (it needs the .NET 10 SDK), installs it to `%LOCALAPPDATA%\Programs\Yolime`, adds
**yolime** to the Start menu, and starts it in the tray. Running it again upgrades in place.

- **yolime is running?** The script stops and names the running copy. Exit it from the tray (right-click > Exit), or
  add `-Force`, which asks it to exit cleanly and stops it if it doesn't.
- **Start with Windows** is your choice in Settings > General. If it was already on (say, from a dev build), the
  installer points it at the installed copy.
- `-NoLaunch` installs without starting it. `-From <folder>` installs an existing publish folder.
  `-StartWithWindows on|off` also sets start-with-Windows.
- **Uninstall:** `tools\install.ps1 -Uninstall`. That removes the program, its shortcut and its start-with-Windows
  entry, and **keeps your data**; add `-RemoveData` to delete your settings, history, recordings and saved keys too
  (refused while any copy of yolime runs). Without it, the uninstaller prints what's left and the commands to remove
  it by hand.

**Smart App Control** can block yolime: it's unsigned and installs a keyboard hook, and Smart App Control has no
per-app exception. When it's on, Windows blocks yolime-setup.exe too; in evaluation mode it may turn on later. The
installers warn when they can. A locally built exe has no download mark, so SmartScreen normally doesn't prompt.
If Smart App Control blocks the new yolime after an in-app update, nothing starts again on its own: setup has already
replaced the old copy, so start yolime from the Start menu once Smart App Control allows it (or reinstall an older
setup).

### Updates

The installed copy (the one in `%LOCALAPPDATA%\Programs\Yolime`) looks for a newer release once a day: about a minute
after it starts, then every 24 hours (an hour after a failed check), never while hotkeys are paused (it catches up
when you resume). A dev build or a copy run from anywhere else never checks; Settings says "Updates are off for this
copy (not installed)".

- **When there's one,** a balloon says so once per version, the tray menu gets **Install update X…**, and the tooltip
  ends in "update ready". Clicking either opens a small window: **Install now**, **What's new** (the release page)
  or **Not now**.
- **Install now** waits for a dictation in progress to finish, downloads the setup, and installs it only if its
  SHA-256 matches the release's `SHA256SUMS.txt` and its file version is the release's. A failure says so in the
  window, nothing changes, and you can try again. Then yolime shows "Updating to X", closes, and the new version starts
  with "Updated to X". Holds are refused from Install until then.
- **If the install fails,** the previous version keeps running (or is started again) and says so; the notice opens
  setup's log, in `%LOCALAPPDATA%\Yolime\updates`. If setup runs five minutes without closing yolime, yolime gives your
  hotkeys back and says so.
- **Settings > General**, at the bottom: the version, **Check for updates automatically** (on by default) and
  **Check now**, with what the last check found.
- **Running as administrator:** an elevated copy can't install updates (yolime never runs elevated, and its setup
  refuses to); it says so once per version. Exit it and start yolime from the Start menu.
- **Trusted releases only:** the newest published release of `bloknayrb/yolime` whose tag is `vX.Y.Z` and newer than
  yours, with both files uploaded by GitHub Actions. Not covered: someone with access to the GitHub account (or an
  agent using its login) could publish a bad release through CI. Releases aren't signed yet.

A dev build (`dotnet run`) and the installed copy share the same data folders and the same single-instance lock:
only one runs at a time (a second launch just says "already running"), and setup done in one carries over, so the
welcome window won't appear again after installing on a machine that ran a dev build.

## First run and keys

The welcome window walks through the microphone, the hold key and your keys. Keys can come from:

| What | Environment variable | Or |
|---|---|---|
| Transcription (OpenAI, the default) | `OPENAI_API_KEY` | Settings > Keys |
| Transcription (Groq) | `GROQ_API_KEY` | Settings > Keys |
| Transcription (your own endpoint) | `YOLIME_STT_API_KEY` | Settings > Keys |
| Claude (Dictate clean-up, Prompt, Reply) | `YOLIME_ANTHROPIC_API_KEY` | Settings > Keys |

`ANTHROPIC_API_KEY` is **ignored on purpose**: Claude Code reads that one, and setting it can change how Claude Code
bills. A key saved in Settings (in Windows Credential Manager) wins over an environment variable. Environment variables
are read when yolime starts. Without a Claude key, Dictate pastes the plain transcript, and Prompt and Reply copy it
and say why.

## Using it

**Hold Right Ctrl, speak, let go.** The text is pasted where your cursor is. The hold key can be Right Ctrl, Right
Shift or Pause (Settings > General).

- Under 300 ms is a tap, not a recording: Right Ctrl shortcuts keep working.
- **Any other key, a click or a scroll** during the hold cancels it, and so does switching to another app.
- **Esc** cancels while recording (with "Send audio while you speak" on, what you'd said has already been sent; see
  "What is sent where"). **Tapping the hold key while it's working** cancels the run but keeps the
  recording, so you can Retry it.
- A hold is capped at 2 minutes (5 in hands-free), and is sent at the cap, never dropped.
- **Hands-free:** turn on double-tap in Settings, then double-tap the hold key to record without holding, and tap it
  again to finish. Other keys and clicks don't stop it (you can keep working), and Esc cancels only while you hold
  the key. Switching to another app sends what you've said so far, to the clipboard.

**Modes.** Tap **Shift** while holding to cycle: Dictate, Prompt, Reply, Raw. With sounds on, each mode has its own
cue (one to four beeps).

- **Dictate:** clean dictation. Short dictations skip Claude; longer ones get punctuation and filler removed.
- **Prompt:** turns rambling speech into a structured prompt for an AI tool. **Code editors and terminals start in
  Prompt**; per-app starting modes are in Settings (process names as Task Manager's Details tab shows them; `.exe`
  optional).
- **Reply:** drafts a reply to the text you've selected, or to what you copied in the last 2 minutes. Copies that
  look like passwords or tokens are skipped. If you selected the message somewhere you can't type (a reading pane),
  it waits up to 10 seconds for you to click where the reply should go.
- **Raw:** the transcript as heard.

Style follows the app: casual in Slack and Teams, email in Outlook, code-aware in editors and terminals. These profiles
are built in.

**The notch** at the top (or bottom) centre of the screen shows Listening, the mode, progress and errors. Error cards
have Retry; a bare tap of the hold key retries too (not with double-tap on, where a tap may start hands-free). After
a rewritten paste, a **Use raw** pill is offered for 5 seconds (any key dismisses it): it copies the original
transcript.

**Preview.** Hold **Shift** as you let go of the hold key, or tick "Always preview" for a mode in Settings. The
result shows on a card: **Enter** inserts, **Esc** discards, **C** copies, and Up, Down, Page Up and Page Down scroll.
Any other key or click ends it, and so does 20 seconds.

**Paste last:** **Win+Alt+V** pastes your last result again, for 30 minutes. It can be changed in Settings > General.
"Pause hotkeys" pauses it as well.

**Where text goes.**
- Normal apps get a paste, and your clipboard is put back afterwards.
- **Password fields** are refused, with a notice, and nothing is kept in history.
- **Admin (elevated) windows** and read-only fields get the text on the clipboard instead, with a notice.
- **Terminals:**
  - Dictate, Prompt and Raw paste: Ctrl+V, or Shift+Insert in mintty and PuTTY, or Ctrl+Shift+V in Warp, Alacritty,
    WezTerm, kitty, Hyper and Tabby.
  - Line breaks become spaces, a leading `!` or `#` is removed (`/` is kept), and no newline is added, so nothing runs
    by itself.
- **Reply in a terminal or a remote session** (RDP, a VM) is copied, not pasted.
- If you switch apps before the text is ready, it's copied, not pasted into the wrong window.

**Tray.**
- **Left-click:** History, or setup while setup is needed.
- **Right-click:** Paste last result, Retry last failed, Pause hotkeys, Pause history, Settings, History, Exit (and
  Finish setup while setup is needed).
- **The icon:** a green dot is ready; red is recording; a grey ring is paused; an orange ring means setup is needed;
  purple means it's running as admin, which isn't recommended.
- Drag it out of the ^ overflow to keep it in view.

**History** (tray > History) lists your runs:
- the transcript and the result, side by side;
- Copy, Re-insert into the last app, re-run in another mode, Add word to your vocabulary, and Delete;
- runs are kept for 30 days by default (1 to 3650, in Settings), and older ones are pruned at start;
- **Pause history** keeps nothing, so there's also nothing to Retry or paste again.

**Keep the microphone open for a minute after each hold** (Settings > General, under Microphone; off by default).
The next hold then starts without waiting for the microphone, which matters most for Bluetooth headsets. Meanwhile
Windows shows yolime as using the microphone (the tray tooltip says "microphone open"), and a Bluetooth headset may stay
in call mode, so music through it sounds worse and briefly drops out when the minute ends. Only a real dictation starts
the minute: a tap or a Right Ctrl shortcut doesn't. Pause hotkeys, locking or sleep close it at once.

**Vocabulary** (Settings): one word or name per line. It's sent with each recording to help transcription, and to
Claude with each rewrite so it keeps your spellings.

### settings.json

The transcription provider, its models and a custom endpoint are set in `settings.json`
(Settings > Advanced > Open settings.json). Keys are camelCase, enums are strings, and comments are allowed:

```jsonc
{
  "sttProvider": "Groq",            // "OpenAi" (default), "Groq" or "Custom"
  "groqSttModel": "whisper-large-v3-turbo",
  "customSttBaseUrl": "https://example.com/v1/",   // https only, with "sttProvider": "Custom"
  "customSttModel": "my-model",
  "streamingUpload": false          // "Send audio while you speak" (Settings > Advanced)
}
```

Prompt overrides, and resetting them, are in Settings > Advanced.

**Send audio while you speak** (Settings > Advanced, off by default, OpenAI only) uploads the recording during the hold,
from the first speech in a hold past 300 ms, so key-up waits only for transcription: about 0.07 s quicker for 10 s of
speech, 0.2 s for 30 s. It sends exactly what the normal upload would (long silences trimmed the same way), and falls
back to the normal upload if the stream fails. Verified with `gpt-4o-transcribe` and `whisper-1`.

## What is sent where

- **Your audio** goes to the transcription provider (OpenAI, Groq or your endpoint), with your vocabulary: at key-up,
  or as you speak if "Send audio while you speak" is on. With that on, a hold you then cancel (Esc, another key, a
  click, switching apps) has already sent what you'd said; the upload is stopped before it's complete, so nothing is
  transcribed, but what was sent can't be recalled. A hold with no speech in it never starts the upload.
- **The transcript** goes to Anthropic (Claude) for the rewrite, with your vocabulary, the app's style profile
  (casual, email, code, terminal or neutral; not the app's name) and, for Reply, the selected or copied text.
- **The update check** (installed copies only, while "Check for updates automatically" is on, or when you click
  Check now): once a day to `api.github.com` for the latest release and, when it's newer, its `SHA256SUMS.txt`; on
  Install, the setup. Both files come from `api.github.com` and then `release-assets.githubusercontent.com` (or
  `objects.githubusercontent.com`). GitHub sees your IP address and a `yolime/<version>` User-Agent; nothing else is
  sent. Turn it off in Settings > General.
- Nothing else leaves the machine. The connections are pinned to those hosts, with no redirects except the update
  download's one hop to GitHub's asset host, which is checked before it's followed.
- With the microphone kept open, everything it hears between holds is thrown away as it arrives: never kept, shown,
  written or sent.

**On this machine:**
- `%APPDATA%\Yolime`: settings and prompt overrides.
- `%LOCALAPPDATA%\Yolime`:
  - `history.db`;
  - `pending\`: recordings kept until transcription succeeds, for Retry, and deleted after 7 days at most;
  - `logs\`;
  - `updates\`: a downloaded setup (deleted after the update), and the last update's result and setup log.
- Credential Manager: saved keys, as `yolime/<KEY NAME>`.

**Logs** hold states, timings, counts and status codes, never text, keys or audio. A test enforces this.
`./tools/timing-report.ps1 [-Days N]` turns the timing log into per-mode latency figures.

## Known limits

- Windows 11 x64 only. Unsigned, so see Smart App Control above.
- While the microphone is kept open, an app that wants it exclusively (some recording or call apps) can't take it until
  the minute ends or you pause hotkeys.
- It can't paste into admin windows; you get the text on the clipboard instead.
- Reply in terminals and remote sessions is copied, not pasted.
- The overlay sits at top or bottom centre only, never next to the cursor.
- The per-app style profiles are built in and can't be edited.
- One hold key for all modes: no Alt key, and no separate key per mode.
- A re-run from History can't be cancelled once started.
- Windows clipboard history (Win+V) keeps copies of anything yolime puts on the clipboard, including History's Copy.
- Changing some Settings pages rebuilds the page, which can drop unsaved text in another field on it.
- At 150 % display scaling the tray icon's dot looks soft.

## Development

```powershell
dotnet build
dotnet test
./tests/Yolime.E2E/run-e2e.ps1               # end-to-end scenarios, in Windows Sandbox only
./tests/Yolime.E2E/run-e2e.ps1 -Updates      # the in-app update scenarios (installs two E2E builds), Sandbox only
./tests/Install/run-install-test.ps1         # install, upgrade and uninstall, in Windows Sandbox only
```

Never drive the real desktop: anything that sends keys or clicks runs in Windows Sandbox. See [CLAUDE.md](CLAUDE.md).

### Cutting a release

1. Set `<Version>` in `src/Yolime.App/Yolime.App.csproj` to the new `X.Y.Z` in a PR, and merge it.
2. Tag that commit on `main` and push the tag: `git tag vX.Y.Z <commit> && git push origin vX.Y.Z`. Bryan tags, or
   Claude does when he says so.
3. `.github/workflows/release.yml` refuses a tag that isn't `vX.Y.Z`, isn't on `main`, or doesn't match `<Version>`.
   It builds and tests with read-only access, then a separate job publishes the setup (`yolime-setup-X.Y.Z.exe`) and
   `SHA256SUMS.txt` as the latest release, after reading them back. A last job checks the published release the way
   yolime's updater reads it.

Only Releases are offered by the in-app updater; the `yolime-setup` artifacts CI builds on `main` are not. Releases
aren't signed: the updater trusts the latest published release whose files were uploaded by GitHub Actions, whose
SHA-256 and file version match, and which is newer than the running copy. Anyone who can push to this repo (Bryan's
GitHub access, or an agent using it) could get a bad one published through a workflow.

**If the smoke job fails** after publishing: the release is final, so fix the problem and release the next patch
version; installed copies take the newer one.

**Rolling back:** run an older release's setup over the installed copy. A newer version's `settings.json` may then be
refused as "from a newer version"; Settings says so.
