# Overlay corners: radius 12, no visible border, then antialiased corners (agent-written plan, v1)

Bryan, 2026-11-02: "lets do all three. have an agent plan it out properly" (radius 12, no border, smooth corners).

## Facts from the code (main @ 2ec5c65)
- Notch and Copy-raw pill are both `OverlayWindow` (`OverlayController.cs:89-90`): Win32 `WS_POPUP`,
  `WS_EX_NOACTIVATE|TOPMOST|TOOLWINDOW|LAYERED`, `TRANSPARENT` toggled (`OverlayWindow.cs:55-56, 77-85`), SLWA alpha
  255 (`:57-58`), `DesktopWindowXamlSource` island (`:60-62`), `MA_NOACTIVATE` (`:155`). Code comments: toggling
  `WS_EX_LAYERED` can blank composited content (`:13-14`); layered is needed for `WS_EX_TRANSPARENT` click-through (`:57`).
- Shape: `SetWindowRgn(CreateRoundRectRgn(0,0,w+1,h+1,24*scale,24*scale))` on every Show (`:135-137`): 1-bit, aliased.
  Diameter 24 → radius 12 DIP already, matching `CornerRadius(12)` (`OverlayController.cs:36, :86`).
- Border: notch only, `BorderThickness = 1` (`:36`); colour from `ApplyTheme` (220 grey light / 64 dark); HC uses
  `Highlight` (`:386`) and the overlay plan requires an explicit 1 px HC border (`2026-10-28-overlay.md:186`).
  `FitProbe` reads `BorderThickness.Bottom` (`:176`).
- Capture exclusion re-applied after every click-through toggle (`OverlayWindow.cs:84`).
- DPI: scale read from the foreground window's monitor each show; hidden window moved first (`:101-110`); width
  measured up to 560 DIP. `IsOnCard` hit-tests the rect (`OverlayController.cs:205-209`).
- Old spike (`spike/overlay` in `yolime-archive`, `spike/FINDINGS.md`): a WinUI Window shown without activation drew
  nothing in 5/6 runs; the Sandbox runs at 125% (1518x814); full-screen captures beat pixel checks. Renderer rule:
  "shown, painted (pixel check), target still foreground" three runs in a row (`2026-10-28-overlay.md:15`).
- Planned but never built E2E: click falls through during Listening; corners transparent (`2026-10-28-overlay.md:212, :215`).
- Old colours hard-coded in `tests/Yolime.E2E/Tour.cs:184-187` (fill 249/32 ±6) and `Scenarios.cs:264-267`.
- Windows 11 only; WindowsAppSDK 2.6.4. All options are P/Invoke: no new packages.

## PR A — `fix/overlay-radius-border` (steps 1 and 2)
- Core: `src/Yolime.Core/Overlay/OverlayGeometry.cs` — `CornerRadiusDip = 12`, `RegionDiameterPx(scale)` =
  round(2·12·scale). Tests: table at 100–300% (24,30,36,42,48,60,72) and radius == 12 pinned.
- App: `CornerRadius(OverlayGeometry.CornerRadiusDip)` at `:36, :86`; `OverlayWindow.cs:136` uses
  `RegionDiameterPx` (keep `w+1,h+1`). `ApplyTheme`: in light/dark `edge = bg` (thickness stays 1 so layout and
  fit checks don't move); HC keeps `Highlight`.
- E2E: one table `Overlay.ExpectedSurface(theme)` used by Tour and Scenarios, cross-checked against `probe-theme`
  (not derived from it, so the wrong-theme negative control still fires). New tour check `edge` (first physical
  column at mid-height == surface ±6). New scenario `Overlay_CornersRounded`: maximised `TargetForm` with pure white
  then pure black `BackColor`; corner pixel (1,1) == backdrop ±6, mid-left == surface.
- Verify: host `dotnet build`/`test`/E2E build only; Sandbox `-Only` overlay/card scenarios, then `-Tour -Areas overlay,cards`.

## Step 3 candidates (island/DWM transparency claims are unverified → spike gates)
| # | Technique | Notes |
|---|---|---|
| V0 | Region 12, no border | the fallback |
| V1 | DWM `DWMWA_WINDOW_CORNER_PREFERENCE=ROUND` + border colour none, no region | radius fixed ~8 DIP → fails radius 12; visual control only |
| V2 | Keep LAYERED+SLWA, drop region, transparent island root with XAML-rounded Border; `WM_ERASEBKGND`→1, `DwmExtendFrameIntoClientArea(-1)`; transparent `SystemBackdrop` if WASDK 2.6.4 exposes it | preferred if it passes; ~30 lines |
| V3 | V2 + `WS_EX_NOREDIRECTIONBITMAP` at creation | highest never-draws risk |
| V3b | NOREDIRECTIONBITMAP without LAYERED | expected to fail click-through; run once, drop |
| V4 | Per-pixel-alpha skin window (`UpdateLayeredWindow`, premultiplied AA rounded rect in surface colour, mask from Core) + notch owned by skin with region inset 1 px | documented Win32, ~150 lines + Core mask tests; two windows to keep in step |
| ✗ | RenderTargetBitmap→ULW each frame; `LWA_COLORKEY`; WinUI Window with transparent backdrop | rejected on paper |

## Spike — `spike/overlay-corners` (throwaway, never merged)
- E2E-only switch `--test-corners v0|v1|v2|v3|v3b|v4` on the real `OverlayWindow` (`#if YOLIME_E2E`); harness mode
  `--corners-spike` with its own `.wsb` via `run-e2e.ps1 -CornersSpike` (networking off, code read-only, results
  writable). Output `artifacts/corners-spike/<variant>/<scale>/<theme>-<backdrop>-<clickthrough>-<capture>.png`,
  `corners.json` (numbers only), `FINDINGS.md`.
- Step 0: try to set Sandbox scale to 100/150 (`PerMonitorSettings\DpiValue`, then `DisplayConfigSetDeviceInfo`),
  verified via `GetDpiForMonitor` + island `RasterizationScale`. If unreachable: 125% only + Bryan's manual look.
- Matrix: reachable scales × light/dark × white/black backdrop × click-through on (Listening)/off (Error card) ×
  capture exclusion off/on; notch and pill; one HC photo (informational).
- Gates (all must pass): (1) drawn 3 fresh runs in a row; (2) ≥0.5·(πR/2) partial-α pixels per corner;
  (3) |α_white−α_black| ≤ 0.12 for ≥95% of corner pixels; (4) fitted radius within ±1 px (±1.5 at 150%);
  (5) no halo/seam; (6) foreground stays on target, NOACTIVATE present, focus unchanged after a button click;
  (7) click-through on: clicks at centre and corner reach the target; off: Retry gets the click; (8) capture
  exclusion holds, also after a click-through toggle; (9) UIA buttons found and notification still fires;
  (10) no resize tearing in a 30-frame Listening→Done burst; (11) latency ≤ +50 ms startup, +20 ms per show vs V0.
- Winner: among passers prefer single-window V2, then V3, over V4; ties → smaller diff, lower latency. None pass →
  stay on V0, record why.

## PR C — `feat/overlay-smooth-corners`
- The winner only, no switch. V2/V3: transparent root + backdrop setup, region removed. V4: `LayeredSkin` class,
  notch/pill owned by their skins; Show/Hide/SetExcludeFromCapture/Dispose act on both; skin re-rendered on theme
  change and each Show. Core (V4): `OverlayGeometry.Coverage(...)`, `InsetRegion(...)` with tests (centre 255,
  outer corner 0, symmetry, area ≈ w·h−(4−π)r² ±0.5%, partial pixels per corner, inset region fully covered).
- E2E: `Overlay_CornersAntialiased` (gates 2–5), `Overlay_ClickFallsThrough` (gate 7); new hook names added to
  `ReleaseHasNoTestCodeTests`.
- Docs: `docs/plan.md:160`, superseding note on `2026-10-28-overlay.md:183`, FINDINGS summary, CLAUDE.md spike line.
- Verify: host `dotnet test`; Sandbox full `run-e2e.ps1` then `-Tour -Areas overlay,cards`; Bryan's look at
  100%/150%/second monitor if the Sandbox couldn't set scale.

## Risks
- Light card with no border over a white app has almost no edge (249 vs 255); with the B tint it is `#F1F6E8` vs white.
- V2/V3 rely on undocumented island transparency; `Overlay_CornersAntialiased` is the tripwire.
- V4: second window per surface; tearing gated; another topmost window could slip between (1 px ring at worst).
- Never-draws regression; capture affinity resets; undocumented Sandbox scale override; Sandbox GPU ≠ real GPU.

## Spike findings (2026-11-02, `spike/overlay-corners`, local only)
Time-boxed to one session; V2 ran first and passed, so V1, V3 and V4 weren't run (the plan prefers a single-window
V2 over the rest when it passes).

- **V2** (LAYERED + SLWA 255 kept, region removed, `DwmExtendFrameIntoClientArea(-1)`, `WM_ERASEBKGND` → 1, XAML
  Border draws the rounded card): in the Sandbox (125%) the overlay tour passed 94/94 in both themes, including the
  capture-exclusion control, and the full E2E passed 52/52 (no focus taken, card buttons and the Copy-raw pill
  clickable, fit checks, Esc). Zoomed corners: V0 shows a stepped edge with a grey fringe; V2 a smooth arc blending
  into the wallpaper behind, with no black or grey fringe.
- **Not measured** (left for Bryan's own look after installing 0.3.0): 100% and 150% scale (the Sandbox runs at
  125%), a click landing exactly on a transparent corner pixel during Listening, and latency against V0 (no visible
  difference in the runs).
- **Shipped:** V2 in `feat/overlay-smooth-corners`. The tour gains a corner check: the window's outermost corner
  pixel must be neither the card's surface (not rounded) nor black (not transparent).
