# yolime design implementation + v0.3.0 release (v2, after 3 angle reviews)

## Context
Bryan approved the Step 2 spec ("Spec: B, icon A" page of https://claude.ai/artifact/8iAtU3ioHAGG8WNeMox71g): mood B
(Calm & soft), app icon A, light-theme accent fills deep green `#36600C`. Overlay corners (2026-11-02, "lets do all
three"): radius 12, no visible border, smooth corners (spike plan: `~/.claude/plans/yolime-overlay-corners.md`). Goal
(/goal): "implement the new design and cut the release with it in place" → ends with tag **v0.3.0**.

Today: `App.xaml` merges only `XamlControlsResources`; UI is code-behind in `src/Yolime.App/Shell/*.cs`; the overlay
hard-codes greys (`OverlayController.ApplyTheme` :379-408); the tray draws a 16 px dot (`TrayIcon.Dot` :357-377); no
app icon. Repo rules (CLAUDE.md) bind every PR: Sandbox only for UI driving, `#if YOLIME_E2E` test code, no content in
logs, pinned packages, LF, branch + conventional commits + reviewed PR.

## Cross-cutting decisions
1. **Colour source of truth: `Yolime.Core/Design/Palette.cs`** (done on `feat/design-tokens`: Light/Dark tokens,
   accent ramp, WCAG `Contrast`; `PaletteTests` assert every pair incl. hover 90% / pressed 80% blends). Control
   border darkened to `#748463` after the test caught 2.88:1 on the selected-row tint.
2. **App.xaml theme dictionaries** Light / Dark / HighContrast. Light and Dark override `SystemAccentColor*` **and**
   the derived brushes WinUI builds inside `XamlControlsResources` (`AccentFillColorDefault/Secondary/Tertiary/
   DisabledBrush`, `AccentTextFillColorPrimary/Secondary/TertiaryBrush`, `TextOnAccentFillColorPrimary/Secondary
   Brush`, `TextOnAccentFillColorSelectedTextBrush`), plus selection keys (NavigationView item selected backgrounds,
   `ListViewItemBackgroundSelected*`, `NavigationViewContentBackground`, `NavigationViewContentGridBorderBrush`),
   `ControlCornerRadius` 8, and the `yolime.*` brushes. HighContrast defines every `yolime.*` key as the matching
   `SystemColor*` brush and overrides nothing of WinUI's. Unit test parses App.xaml: hex values == Palette, and the
   three dictionaries have identical `yolime.*` key sets.
3. **How colour reaches code-built UI (theme flips must work):** TextBlocks use XAML Styles with `{ThemeResource}`
   setters (`yolime.Muted`, `yolime.Caption`, `yolime.PageHeading` 20/500, `yolime.WelcomeHeading` 24/500). Things a
   Style can't reach (History diff `Run`s, the hold-key light, custom meters) go through an App helper
   `ThemeColors.For(FrameworkElement)` that returns Palette by the element's `ActualTheme` (system colours in HC),
   re-applied on the window root's `ActualThemeChanged`. `Application.Current.Resources[...]` snapshot lookups for
   colour are removed (WelcomeWindow.cs:248-257, :347).
4. **Window tint:** a small `TintedMicaBackdrop : SystemBackdrop` (MicaController with `TintColor` = WindowTint,
   `TintOpacity` 0.6, `FallbackColor` = ContentBackground, updated on theme change) replaces `new MicaBackdrop()` in
   all four windows, so the title bar is tinted too (no seam). Content layer = `yolime.Content.Background`.
5. **Motion, simplified on review:** no meter easing (levels arrive ~30 Hz and nothing drives a 60 ms ease; dropped,
   meter updates per sample). Overlay: when the view's *State* changes (never on ticks, countdown or detail
   updates), the stack fades in from opacity 0.4 to 1 over 200 ms; nothing else animates. Hold-key light uses
   `Border.BackgroundTransition` (150 ms). All of it off when `UISettings.AnimationsEnabled` is false; the E2E Sandbox
   turns animations off so captures never race a fade. (Spec said 300 ms cross-fade + 4 px card rise + eased meter;
   these trims are mine, recorded for Bryan.)
6. **Icons generated** by `tools/make-icons.py` (Pillow 11.3.0 via `uv run --no-project --with`, dev-time only; output
   committed). Entries: DIB (32 bpp BMP) for ≤64 px, PNG for 256. `IconFileTests` check sizes, 32 bpp, per-size
   distinct images, and that tray files == `TrayIconState` × {light, dark}.

## PRs (sequential; each reviewed by an agent on its full diff, fixes applied, CI green, then merged)

### PR 1 — `feat/design-tokens`: palette, resources, windows
- Palette + tests (done), App.xaml dictionaries + parity test, `ThemeColors`, `TintedMicaBackdrop`.
- Settings/Welcome/History/Update: backdrop, headings via styles, every text `Opacity` 0.7–0.85 → `yolime.Muted`,
  spacing per the Tokens board (Settings page 36,24,36,36 and stack 18; Welcome 40,30; label→control 6).
- `LevelMeter` (App): 6 px rounded track + fill (`Meter.Fill`/`Track`; HC → Highlight/WindowText), replacing the
  level `ProgressBar`s in Welcome step 2 and Settings › Microphone. Update window's download bar stays a ProgressBar.
- Welcome status lines get a check / warning glyph; hold-key light via `ThemeColors` + `BackgroundTransition`.
- History diff via `ThemeColors` (strike/underline kept), re-rendered on theme change; non-Ok rows get a warning glyph.
- E2E: tour gains (a) a pixel check that Welcome's Next is `#36600C` light / `#C2E38A` dark, (b) a theme-flip entry
  (Settings, Welcome, History open; flip; recapture), (c) best-effort HC entry (if HC can't be set in the Sandbox
  without sign-out, the entry records "skipped: HC unavailable" and we rely on HC keys being system colours).

### PR 2 — `feat/overlay-look` (absorbs the corners plan's PR A)
- `Yolime.Core/Overlay/OverlayGeometry.cs`: `CornerRadiusDip = 12`, `RegionDiameterPx(scale)`, `PaddingH = 20`,
  `PaddingV = 13`, `Border = 1`, `StackWidth(maxDip)` = 560 − 2·(20 + 1) = **518**, used at OverlayController :78 and
  :246. Tests pin all of them.
- `ApplyTheme` reads Palette: background `NotchBackground`, border **= background** (thickness stays 1), title/preview
  `Text`, detail/elapsed `TextMuted`, title weight 500, glyph gap 12, button row top margin 10. HC: border Highlight,
  all text WindowText, meter Highlight on WindowText-track.
- `LevelMeter` replaces the overlay's 3 px `ProgressBar`.
- State-change fade (decision 5).
- E2E: `Overlay.ExpectedSurface(theme)` table used by `Scenarios.cs:264-267`, `Tour.cs:158` and `Tour.cs:184-187`,
  cross-checked against `probe-theme`; new tour `edge` check; new `Overlay_CornersRounded` scenario (white/black
  maximised `TargetForm` backdrop). Fit checks (`Cards_FitAt150PercentText`, fit sweep) re-run.

### PR 3 — `feat/icons`
- Assets from the generator; csproj `ApplicationIcon` + `<Content Include="Assets\*.ico" CopyToOutputDirectory=
  "PreserveNewest" CopyToPublishDirectory="PreserveNewest"/>`; `tools/check-publish.ps1` requires each `.ico`.
- `AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "yolime.ico"))` in each window.
- `TrayIcon`: `LoadImageW(0, path, IMAGE_ICON, cx, cy, LR_LOADFROMFILE)` with `cx = GetSystemMetricsForDpi(
  SM_CXSMICON, dpi)`; `NIM_MODIFY` then `DestroyIcon` the old handle. Variant: dark taskbar
  (`SystemUsesLightTheme` = 0, which includes the Windows 11 accent-coloured taskbar) → `-dark`; light → `-light`;
  HC → by the HC window colour's luminance. Reload on `TaskbarCreated`, `WM_SETTINGCHANGE "ImmersiveColorSet"`,
  `WM_DPICHANGED` and `WM_DISPLAYCHANGE`. If a file fails to load, fall back to the existing drawn dot (logged as a
  state change, no content). Balloons keep `NIIF_INFO`/`NIIF_WARNING`.
- `installer/yolime.iss`: `SetupIconFile`. Welcome step 5 and History empty state show the icon.
- E2E: tour photographs the notification area (tray icon) in light and dark.

### PR 4 — smooth corners: spike, then maybe `feat/overlay-smooth-corners`
- Spike per the corners plan (variants V0–V4 behind E2E-only `--test-corners`, gates 1–11), branch
  `spike/overlay-corners`, **local only** (origin is public), time-boxed to one working session; finished =
  FINDINGS.md written whatever the outcome.
- A winner ships in 0.3.0 only if its PR is reviewed and the full E2E passes before the bump; otherwise 0.3.0 ships
  the fallback (region 12, surface-coloured border) and the winner follows in 0.3.1.

### PR 5 — `chore/release-0.3.0`
- `<Version>0.3.0</Version>` in `Yolime.App.csproj` only (as #14).
- Before merge: Smart App Control probe in Sandbox; full `run-e2e.ps1`; `run-e2e.ps1 -Updates`;
  `tests/Install/run-install-test.ps1`; full `-Tour` (current snapshot copied aside first, then compared); CI green.
- Merge; push tag `v0.3.0` alone on the merge commit; confirm the run started; watch build/release/smoke; check
  `releases/latest`. A failed run is retried as v0.3.1 (tags are immutable).

## Whole-plan review (v2) rulings — 0 Critical, 1 High, fixed
- **HC meter (High):** track = `SystemColorWindow` with a 1 px `WindowText` outline, fill = `Highlight`, in both the
  overlay and `LevelMeter`. New key `yolime.Meter.Outline` (Light/Dark: same as the track, so no visible outline).
- `ThemeColors.Track` also subscribes `AccessibilitySettings.HighContrastChanged`. `TintedMicaBackdrop` in HC sets
  no tint and `FallbackColor` = system Window colour, and keeps `SystemBackdropConfiguration.IsHighContrast` in sync.
- Title-bar tint is **unverified** (no `ExtendsContentIntoTitleBar`); checked by eye on Bryan's real desktop after
  install, not claimed. If the bar stays untinted, that's accepted (it's Mica either way).
- Accent override proof is PR 1's first Sandbox check: Next button, ToggleSwitch on, CheckBox checked, NavigationView
  indicator; if any stays blue, add control-level keys (`AccentButtonBackground*`, `ToggleSwitchFillOn*`,
  `CheckBoxCheckBackgroundFillChecked*`) to Light/Dark.
- HC muted text = WindowText (never GrayText).
- Tray: `SystemUsesLightTheme = 0` (dark, incl. accent-coloured) → `-dark`; the script comment is corrected. Reload
  builds the whole new icon set, `NIM_MODIFY`, then destroys the old set; DPI from `GetDpiForWindow(trayHwnd)`.
- E2E animations off: the harness sets `SPI_SETCLIENTAREAANIMATION` off in the Sandbox; the app reads
  `UISettings.AnimationsEnabled` at each fade (not cached).

## Not doing (recorded)
- Stock button borders stay WinUI's (the spec's tinted button border is cosmetic and fights WinUI's elevation brush).
- "Other cards and panels" radius 16: there are none in the windows today.
- Tour entries for Update window states, Welcome problem states, Settings bars/dialog, History paused/empty: not
  added now; they're reviewed by hand in the Sandbox before release instead.
- Balloon app icon (`NIIF_USER`): dropped, it would lose the info/warning distinction.

## Verification
- Each PR: `dotnet build`, `dotnet test`, E2E build; Sandbox runs listed per PR; CI green.
- Before release: the PR 5 gate; I look through the tour images in both themes against the spec boards and show
  Bryan the white-backdrop overlay image (borderless light card on white is ~1.08:1 edge contrast, as the spec
  accepted; fallback if he dislikes it: `Notch.Border = Divider`).
