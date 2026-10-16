# Heist Ledger v1.4.0 - In-app Updates

- Added Check for Updates in Settings, with current version and release notes.
- Added an optional automatic check when the app opens.
- Added download progress, cancellation, and an explicit Install & Restart button.
- Added an update notification on the dashboard.
- Updates download from the public Heist Ledger GitHub repository; no account is required.
- Downloaded packages are verified before installation. Failed or cancelled downloads leave the running version in place.
- Restarting waits until the current scan/payout review is finished and uses the normal shutdown cleanup.
- Earnings and settings remain in the existing LocalAppData folder.

## Install once to enable updates

Existing ZIP users need to close their old copy and run `HeistLedger-win-Setup.exe` once. Use the installed Heist Ledger shortcut afterward. Future updater-enabled releases can be downloaded within Settings. Running the old ZIP executable will continue to run the old version.

The installer includes .NET and can install the Visual C++ runtime required by OCR. The app is unsigned, so Windows may display an unrecognized-app prompt.

Update checks need internet access. Manual tracking and local OCR continue working offline. Startup checks can be disabled in Settings; updates are never downloaded or installed without clicking the corresponding button.
