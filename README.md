# Heist Ledger

Heist Ledger is a standalone Windows companion for tracking GTA Online earnings. It supports manual entries and user-triggered Quick Scan OCR while reading only the visible screen region selected by the user. It does not access the GTA process, automate input, modify game files, or interact with anti-cheat software.

## Download

Download the latest Windows build from the [GitHub Releases page](https://github.com/PrithviGhale/Heist-Ledger/releases/latest). Extract the ZIP and run `HeistLedger.exe`.

The Windows x64 package is self-contained, so users do not need to install .NET separately. OCR requires the Microsoft Visual C++ 2015-2022 x64 Redistributable. Version 1.3.0 is not Authenticode-signed, so Windows SmartScreen may identify it as an unrecognized app; the release includes a SHA-256 checksum for verification.

User earnings and settings are stored under `%LocalAppData%\GTAEarningsTracker`. Updating the application does not remove that data.

## Features

- Manual payout entry with rolling 24-hour and 7-day totals
- User-selected visible screen region for passive Quick Scan OCR
- Green cash-icon payout detection alongside Total Cash Earned result-screen detection
- Three-reader OCR verification using Tesseract, local PaddleOCR AI, and a GTA-specific digit reader
- Rockstar-font preprocessing and segmented digit recovery for difficult payout overlays
- Five-frame rescanning with reader agreement, alternatives, and uncertainty details
- Accept, edit, rescan, and reject workflow before saving uncertain results
- Captured payout preview while manually editing an amount
- Repeated rescanning and saving of the same visible payout without duplicate suppression
- Compact always-on-top overlay and six visual themes
- Smooth compact/expanded transitions, sliding pages, and a moving navigation highlight
- Lower idle CPU usage and faster capture bursts with reusable native capture resources
- Persistent session countdowns, earning calendar, and per-entry history deletion
- Separate configurable Quick Scan and minimize global shortcuts
- Optional Discord webhook notifications with an in-memory payout preview
- Local JSON storage with no game-process or anti-cheat interaction

## OCR and privacy

OCR runs locally. The app combines the Tesseract 5.2.0 wrapper, the English `tessdata_fast` model, the PP-OCRv5 mobile recognition model through ONNX Runtime, and a specialized GTA digit reader. Captured images and preprocessing variants remain in memory and are not saved by the application.

If Discord screenshot sharing is explicitly enabled, the in-memory crop from a confirmed Quick Scan is uploaded with that Discord message. Discord integration is disabled by default.

## Optional Discord webhook

Open **Settings > Discord Webhook**, enter the display username, and paste a Discord incoming webhook URL. Use **Send Test**, then enable **Send confirmed Quick Scan payouts** and save. The URL is protected with Windows Data Protection for the current Windows user.

## Repository layout

```text
src/HeistLedger/   WPF application, OCR models, themes, and runtime assets
Tests/             Regression projects and screenshot fixtures
scripts/           Maintenance scripts
docs/              Release notes and build documentation
```

Build and test instructions are in [docs/BUILDING.md](docs/BUILDING.md). Release history is available under [docs](docs).

## Safety

Heist Ledger does not read game memory, inspect GTA processes, inject DLLs, hook DirectX, modify game files, automate input, or interact with BattlEye. Quick Scan analyzes only visible pixels inside the region selected by the user.
