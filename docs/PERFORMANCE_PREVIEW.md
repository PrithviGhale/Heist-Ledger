# Performance and UI update validation

Prepared October 16, 2026 during local testing for v1.3.0. These are development measurements, not performance guarantees across computers.

## Changes

- AI OCR uses at most two inference workers, with idle thread spinning disabled.
- The daily/weekly session calculations are reused until ledger changes, a session expires, or the clock moves backwards. Countdown labels still use the current UTC time.
- UI refresh/countdown timers and live preview pause when the tracker is hidden or minimized. They refresh when it returns.
- Background motion plays briefly on entrance, then settles. Expensive animated blur filters were removed from the startup experience.
- The main window has rounded content clipping, a separate static shadow, softer accent glows, more rounded cards, improved button hover feedback, and a revised greeting.
- Compact/expanded resizing uses one 360 ms motion: the old snapshot fades while the final layout scales with the window. There is no second reveal fade, and expansion releases compact width constraints before resizing. Temporary snapshots and bitmap caches are released afterward.
- Dashboard and Settings slide together inside a clipped viewport. A moving accent pill stretches subtly during navigation; rapid clicks settle on the most recently requested page.
- Startup uses a staggered emblem, wordmark, and cursive greeting reveal, followed by an upward fade into the app. The intro scales down to fit compact windows and respects the Windows animation preference.
- Quick Scan still takes five fresh, full-resolution frames. A burst reuses one GDI DIB and device context, with a 50 ms frame interval instead of 75 ms. Every frame owns a frozen copy of its pixels.
- When the tracker overlaps the selected region, capture waits for Windows desktop composition after hiding the tracker, replacing the fixed 100 ms delay.
- Selection rounds outward to physical pixel boundaries and clamps to the monitor, preserving edge pixels at fractional DPI scaling.
- An exact-image OCR cache keeps at most 64 text results per OCR worker. Its key includes the complete PNG hash, page mode, whitelist, and line-information setting. It stores no images and never reuses recognition for changed pixel data or different OCR settings.
- Repeated hotkey presses cannot queue additional Quick Scans. Preview captures run off the UI thread and discard stale results if the selected area changes.
- Manual payout confirmation remains required by the Quick Scan UI. Game process access, memory access, injection, hooks, game-file changes, anti-cheat interaction, and input automation were not added.

## Measurements on this PC

Recorded Release builds, compact mode visible, current 125% display scaling. Idle samples lasted 12 seconds. The second optimized idle sample was taken after entrance effects had settled.

| Measurement | Previous build | Local preview |
| --- | ---: | ---: |
| Idle CPU, percent of one core | 61.36% | 0.26% settled |
| Working set during those samples | 307.1 MB | 288.8 MB |
| Mean analysis time, 11 user Total Cash fixtures | 3,232.0 ms | 2,742.3 ms |
| Inter-frame wait in a five-frame capture | 300 ms total | 200 ms total |

The fixture analysis average improved by 15.2% in the recorded runs. These timings vary with screen content, CPU load, and power mode. Difficult screenshots still use full verification and can take several seconds. No accuracy thresholds were lowered.

## Validation

- Release solution builds without warnings or errors.
- All 13 Total Cash Earned screenshots return their exact expected values.
- All seven green cash-icon screenshots return their exact expected values.
- Generated acceptance/rejection, reader-consensus, and PaddleOCR service regressions pass.
- Six earnings-session tests and the Discord smoke tests pass.
- Exact-image OCR tests cover changed amounts, negative signs, page-mode/whitelist isolation, cancellation, and bounded cache eviction.
- Native capture tests verify pixel orientation/color, native pixel size at 125% scaling, independent frozen burst frames, exclusions, invalid coordinates, and repeated disposal.
- 250 frames across 50 capture sessions left the GDI handle count unchanged (41 before, 41 after).
- Offscreen XAML checks cover six themes, dashboard/settings/compact/confirmation layouts, both intro sizes, 100%/125%/150% rendering scales, visible action buttons, and transparent outer corners.
- Dispatcher-driven transition checks cover rapid tab requests, navigation-pill alignment, repeated compact/full changes, final sizing constraints, restored interaction, and temporary bitmap cleanup. These tests do not load or save personal settings or ledger data.

Interactive Windows inspection was stopped with the physical Escape key. Static rendered layout previews were inspected afterward. Live transition feel and real GTA captures should be tried by the user before publication.

## Try it

Run `src/HeistLedger/bin/Release/net9.0-windows/HeistLedger.exe` after a Release build, or use:

```powershell
dotnet run --project src\HeistLedger\GTAEarningsTracker.csproj -c Release --no-build
```

Try a Total Cash Earned result and a green-icon payout, rescan, edit/reject, compact/expanded switching, minimize/restore, and preview refresh in Settings. Compare the actual capture with each proposed amount before accepting it.
