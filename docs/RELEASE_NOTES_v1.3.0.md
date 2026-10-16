# Heist Ledger v1.3.0 - Performance and Motion Update

This update reduces background CPU usage, speeds up capture bursts, and refreshes the app's animations and rounded interface.

## Performance and capture

- Reduced idle CPU work by stopping background animation after entrance and pausing UI timers and previews while the window is hidden or minimized.
- Limited local AI inference to two worker threads and disabled idle thread spinning.
- Reused screen-capture resources across each five-frame burst and reduced the total inter-frame waiting time from 300 ms to 200 ms.
- Replaced fixed capture-hide delays with Windows desktop composition synchronization.
- Added a bounded OCR cache for identical image pixels and recognition settings; changed pixels always receive fresh recognition.
- Moved capture previews off the UI thread and discard stale previews after capture-area changes.
- Prevented repeated Quick Scan hotkey presses from queuing extra scans.
- Improved capture selection rounding at fractional display scaling so edge pixels are preserved.

In local tests, the mean analysis time across 11 Total Cash Earned fixtures improved by about 15%. Actual scan time depends on the image and computer. All 13 Total Cash Earned and seven green cash-icon screenshots matched their expected amounts. OCR still requires checking the proposed amount and manually confirming it.

## Interface and animations

- Added rounded content clipping, softer background accents, refined cards, and animated button hover feedback.
- Reworked compact/expanded switching into one continuous resize and crossfade, removing the separate second reveal and fixing compact width limits during expansion.
- Added horizontal sliding between Dashboard and Settings.
- Added a moving navigation bubble with a subtle stretch effect; rapid tab clicks settle on the most recently selected page.
- Replaced the opening animation with a staggered logo, wordmark, and personal greeting reveal followed by an upward fade into the app.
- Made the greeting fit both compact and full-size windows, with Windows reduced-animation preferences respected.

## Validation

- Release solution builds without warnings or errors.
- Screenshot, reader-consensus, earnings-session, and local Discord smoke regressions pass.
- Native capture checks cover pixel orientation, independent frames, excluded regions, invalid coordinates, and resource disposal.
- Layout checks cover six themes at 100%, 125%, and 150% rendering scales, including both intro sizes.
- Transition checks cover rapid tab changes, navigation alignment, repeated compact/full switching, final window bounds, restored interaction, and bitmap cleanup.

## Download and update

1. Download `Heist-Ledger-v1.3.0-win-x64.zip` from this release.
2. Close Heist Ledger and extract the entire ZIP into a new folder.
3. Run `HeistLedger.exe`. Existing earnings and settings remain in `%LocalAppData%\GTAEarningsTracker`.

The Windows x64 build includes .NET. OCR requires the Microsoft Visual C++ 2015-2022 x64 Redistributable. The app is not Authenticode-signed; Windows may display an unrecognized-app prompt. A SHA-256 checksum accompanies the ZIP.

Heist Ledger continues analyzing only visible pixels in the selected screen region. Quick Scan requires manual confirmation. It does not access GTA processes or memory, inject DLLs, hook DirectX, modify game files, automate input, or interact with BattlEye. Optional Discord sharing remains controlled by your settings.
