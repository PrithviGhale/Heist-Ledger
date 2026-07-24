# Heist Ledger v1.2.0 - Dashboard and Green Cash Update

Version 1.2.0 refreshes the dashboard and compact overlay, adds persistent earning sessions and history tools, and expands Quick Scan to recognize GTA's green cash-icon payout notification.

## Quick Scan

- Added a dedicated detector for the green circular cash icon and the positive amount displayed beside it.
- Kept the existing Total Cash Earned result-screen scanner unchanged and covered it with stricter regression assertions.
- Added regression screenshots for seven green cash payouts and expanded the secondary OCR fixture set.
- Kept all detected payouts behind the existing confirmation workflow; Quick Scan does not save a detected amount automatically.
- Continued analyzing only visible pixels inside the screen area selected by the user.

## Dashboard and history

- Added persistent 24-hour and 7-day earning sessions that are calculated from UTC timestamps and remain accurate while the app is closed.
- Added countdown displays for the active daily and weekly sessions.
- Added an earning calendar with month navigation and daily history filtering.
- Added per-entry deletion so an older payout can be removed without undoing newer entries.
- Improved the dashboard layout, money typography, greeting, startup animation, and animated background.

## Compact mode and settings

- Improved compact-mode sizing and payout confirmation so Quick Scan results can be accepted, edited, rescanned, or rejected without reopening the full dashboard.
- Added separate configurable global shortcuts for Quick Scan and minimizing/restoring the app.
- Added a capture-area preview to Settings.
- Moved the player name into the profile section and use `Player` as the default greeting.
- Improved themed controls, calendar styling, and transition animations.

## Installation

1. Download `Heist-Ledger-v1.2.0-win-x64.zip`.
2. Extract the entire ZIP to a normal folder.
3. Run `HeistLedger.exe`.
4. Select the visible GTA payout area when prompted.

The Windows x64 package is self-contained and does not require a separate .NET installation. OCR requires the Microsoft Visual C++ 2015-2022 x64 Redistributable. This release is not Authenticode-signed, so Windows SmartScreen may show an unrecognized-app warning.

## Safety and privacy

Heist Ledger remains a normal standalone Windows application. It does not read game memory, inspect GTA processes, inject DLLs, hook DirectX, modify GTA files, automate input, or interact with BattlEye. OCR runs locally, and captured images are not saved unless the user explicitly enables screenshot sharing for a confirmed Discord notification.
