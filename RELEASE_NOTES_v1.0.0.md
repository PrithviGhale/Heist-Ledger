# Heist Ledger v1.0.0

The first public Windows release of Heist Ledger.

## Highlights

- Track GTA Online earnings manually or with user-triggered Quick Scan.
- Review, edit, confirm, or reject every detected payout before it is saved.
- See rolling totals for the last 24 hours and 7 days.
- Select exactly which visible screen region Quick Scan may analyze.
- Use the full dashboard or compact always-on-top overlay.
- Choose from Dark, Light, Purple, Galaxy, Midnight, and Crimson themes.
- Optionally post confirmed Quick Scan payouts and payout previews to Discord.
- Keep each Windows user's earnings and settings locally in their own profile.

## Safety and privacy

Heist Ledger is a normal standalone Windows application. It does not read game memory, inspect GTA processes, inject DLLs, hook DirectX, modify game files, automate input, or interact with BattlEye. OCR analyzes only visible pixels in the region selected by the user. Discord sharing is disabled by default and remains opt-in.

## Installation

1. Download `Heist-Ledger-v1.0.0-win-x64.zip`.
2. Extract the entire ZIP to a folder.
3. Run `HeistLedger.exe` from the top level of the extracted folder.
4. Select the GTA payout area when prompted.

The ZIP contains only the main EXE at its top level and a `Documentation` folder. The EXE bundles the .NET 9 Windows Desktop runtime, WPF, and the OCR components into one self-extracting file. OCR requires the Microsoft Visual C++ 2015–2022 x64 Redistributable.

This first release is not Authenticode-signed, so Windows SmartScreen may identify it as an unrecognized app. The release includes a SHA-256 checksum for verification. A publicly trusted code-signing identity or Microsoft Store distribution is required to provide a trusted publisher experience.
