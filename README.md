# Heist Ledger

Heist Ledger is a passive, standalone Windows companion for tracking GTA Online earnings. It tracks manually confirmed earnings and analyzes only the visible pixels selected by the user. It does not access the GTA process, automate input, modify game files, or interact with anti-cheat software.

## Download

Download the latest Windows release from the [GitHub Releases page](https://github.com/PrithviGhale/Heist-Ledger/releases/latest). Extract the ZIP to a normal folder and run `HeistLedger.exe`.

The ZIP has a clean layout: `HeistLedger.exe` is at the top level and explanatory files are under `Documentation`. The Windows x64 release is a self-contained single-file application, so the .NET runtime does not need to be installed separately. OCR also depends on the Microsoft Visual C++ 2015–2022 x64 Redistributable.

The EXE is larger than a typical small desktop utility because it contains the complete .NET 9 Windows Desktop runtime, WPF libraries, both managed and native Tesseract OCR components, and the English OCR model. Bundling these dependencies makes setup easier and ensures every user runs the same tested versions.

### Windows security notice

Version 1.0.0 is not Authenticode-signed. Windows SmartScreen may therefore describe it as an unrecognized app. The release includes a SHA-256 checksum so the downloaded ZIP can be checked for corruption or tampering. Removing the SmartScreen warning reliably requires publishing through the Microsoft Store or signing future releases with a publicly trusted code-signing identity; a self-signed certificate does not solve the warning for other users.

Each Windows user gets independent earnings and settings under `%LocalAppData%\GTAEarningsTracker`. Updating or replacing the application folder does not remove that local data.

## Features

- Manual payout entry with rolling 24-hour and 7-day totals
- User-selected visible screen region for passive Quick Scan OCR
- Manual confirmation, editing, rejection, undo, and duplicate protection
- Compact always-on-top overlay and six visual themes
- Optional Discord webhook notifications with an in-memory payout preview
- Local JSON storage with no game-process or anti-cheat interaction

## OCR runtime requirements

Stage 3 uses the `Tesseract` 5.2.0 .NET wrapper and the English `tessdata_fast` model in `tessdata/eng.traineddata`. The model is copied beside the application during build and is resolved from `AppContext.BaseDirectory`; no machine-specific path is required.

The Tesseract and Leptonica native binaries in this wrapper were built with Visual Studio 2019. Windows systems running the OCR feature therefore need the Microsoft Visual C++ 2015–2022 x64 Redistributable installed.

OCR processing is local. Captured images and preprocessing variants remain in memory and are not saved by the application. If the user explicitly enables the Discord webhook option and screenshot sharing, the in-memory payout crop from a confirmed Quick Scan is uploaded with that Discord message.

## Optional Discord webhook

Open **Settings > Discord Webhook**, enter the display username that should appear beside payouts, and paste a Discord incoming webhook URL. Use **Send Test** to verify the connection, then enable **Send confirmed Quick Scan payouts** and save.

Discord messages are sent only after a Quick Scan payout is manually confirmed and saved locally. They include the confirmed amount, rolling 24-hour and 7-day totals, the Quick Scan method, and local time. Screenshot attachment sharing can be disabled independently. The webhook URL is protected with Windows Data Protection for the current Windows user and stored in `%LocalAppData%\GTAEarningsTracker\discord-settings.json`.
