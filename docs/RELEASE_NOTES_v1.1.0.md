# Heist Ledger v1.1.0 — Reliable OCR Update

Version 1.1.0 substantially improves Quick Scan accuracy, makes uncertain results easier to verify, and reorganizes the source repository for easier navigation.

## OCR accuracy and reliability

- Added PP-OCRv5 mobile recognition through ONNX Runtime as a second local OCR engine.
- Combined Tesseract, PaddleOCR, and the GTA-specific digit reader under a strict reader-consensus policy.
- Added Rockstar-overlay preprocessing for bright, translucent, textured payout digits.
- Added projection-based digit segmentation for tightly kerned Rockstar glyphs, including the split-stroke `4`.
- Added safe recovery for currency symbols that OCR sometimes reads as a letter without allowing letters inside an amount.
- Improved leading merged-digit, trailing-digit, repeated-digit, and incomplete-number recovery.
- Filtered page-sized duplicate OCR symbols and geometrically incomplete readings.
- Added five-frame scan aggregation, additional fallback variants, and complete-number selection when one reader loses leading digits.
- Added local model status and processing-time details to OCR settings and diagnostics.
- Added regression coverage for every supplied payout screenshot; all 11 fixtures produce their exact expected amounts.

## Payout verification workflow

- Replaced the old confirmation actions with clear **Accept**, **Edit**, **Rescan**, and **Reject** controls in both layouts.
- Added forced rescanning while retaining the previous result until a fresh scan finishes.
- Added scan-attempt history, reader agreement, possible alternatives, and uncertain digit positions.
- Added clearer verification-required and conflicting-reader states.
- Added the captured GTA payout image to the manual Edit view so an amount can be entered by sight when OCR is uncertain.
- Prevented uncertain or conflicting OCR results from being silently saved automatically.
- Removed duplicate-result suppression: the same visible payout can now be rescanned and saved repeatedly when the user chooses.

## Safety and stability

- Preserved explicit user confirmation for low-confidence and conflicting detections.
- Improved saved-payout and pending-card state handling during rescans.
- Kept OCR fully local; no captured image is stored by the application.
- Fixed the capture-selection window code-behind so the project builds cleanly without false generated-field errors.
- Expanded regression tests for reader consensus, invalid AI output, rescan behavior, UI controls, and duplicate-free repeated scans.

## Repository organization

- Moved the WPF application and runtime assets under `src/HeistLedger`.
- Consolidated OCR screenshots under `Tests/Fixtures` and removed duplicate fixture copies.
- Moved maintenance scripts to `scripts` and release documentation to `docs`.
- Added dedicated build, test, and publish instructions.
- Kept `README.md` at the root so GitHub renders the project overview automatically.

## Installation

1. Download `Heist-Ledger-v1.1.0-win-x64.zip`.
2. Extract the entire ZIP to a normal folder.
3. Run `HeistLedger.exe`.
4. Select the visible GTA payout area when prompted.

The Windows x64 package is self-contained and does not require a separate .NET installation. OCR requires the Microsoft Visual C++ 2015–2022 x64 Redistributable. This release is not Authenticode-signed, so Windows SmartScreen may show an unrecognized-app warning.
