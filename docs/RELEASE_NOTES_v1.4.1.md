# Heist Ledger v1.4.1 - Update Verification

- The up-to-date message now includes the running app version, making completed upgrades easier to verify.
- This small patch provides an upgrade target for testing the new v1.4.0 in-app updater.
- Scanning, earnings, and settings behavior are unchanged.

## Try the in-app update

If you already installed v1.4.0, open **Settings > App Updates > Check for Updates**, choose **Download Update**, then **Install & Restart**. After restarting, Settings should show **Heist Ledger v1.4.1**. Your saved earnings and preferences should remain in place.

To test from an older ZIP copy, first close that copy and install `HeistLedger-win-Setup.exe` from the [v1.4.0 release](https://github.com/PrithviGhale/Heist-Ledger/releases/tag/v1.4.0). Use the installed shortcut, then follow the steps above. Installing the v1.4.1 Setup directly skips the update test.

Downloads are user-controlled. The installer and update are unsigned, so Windows may show an unrecognized-app prompt. The installer includes .NET and declares the required Visual C++ runtime for OCR.
