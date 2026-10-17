# Heist Ledger v1.5.0 - Full-screen Quick Scan

- Quick Scan captures the entire primary display automatically. Manual capture-area setup is no longer required, and saved rectangles are no longer used for Quick Scan.
- Added automatic Total Cash Earned region location before digit verification and adjusted green cash-icon discovery for full-screen captures.
- The tracker hides itself during captures that overlap its window. Manual payout confirmation remains required.
- Replaced the Dashboard/Settings tab bar with a small Settings gear in the header and Back to Home navigation. The gear also opens Settings from compact mode.
- Added a persistent `!` badge when a newer update is detected. The indicator clears after that version is installed or a fresh check reports no newer update.
- Refined the Settings page with rounded cards, section shortcuts, clearer headings, consistent buttons, and brief smooth scrolling.
- Scrolling releases animation clocks when complete and respects reduced-animation preferences. The badge is static, with no continuous animation or additional polling timer.
- Automatically located result crops own their pixels so payout confirmation does not retain unnecessary full-screen frame buffers.

## Update

Installed v1.4.0+ users can open Settings > App Updates, check, download, then choose Install & Restart. Existing earnings and preferences remain in LocalAppData.

For first-time installation or older ZIP copies, close the old app and run `HeistLedger-win-Setup.exe`. Use the installed shortcut afterward.

Full-screen scanning uses visible pixels on the **primary monitor**, including desktop content visible there. Use GTA on that display. More screen context does not guarantee perfect OCR and may increase scan time; compare every proposed amount with the game before confirming. Optional Discord screenshot sharing continues to attach the detected amount crop, not the full desktop capture.

The app remains standalone: no game-process/memory access, injection, DirectX hooks, game-file changes, automated input, or BattlEye interaction. This release is unsigned, and Windows may show an unrecognized-app prompt.
