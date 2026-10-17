# First game checks

This build is ready for diagnostic testing, not a completed shared-world release. Male/female presentation, networking and feedback are implemented; real game integration still needs the observations below.

First prepare a disposable profile using [INSTALL.md](INSTALL.md). Continue only after preparation succeeds. The test profile is disposable. The original progress is preserved separately by the recovery journal. Keep Steam Cloud off until the game is closed and the original profile has been restored and verified. Run only one game process on this Windows account.

1. Launch My Summer Car normally. Choose windowed mode for convenience. At the main menu, look for the MSC-MP panel. F8 shows or hides it. Report any loader error or missing panel.
2. Select Male and Female. Each should show one complete textured character, including face, hair, clothing and shoes. Switch several times. Report blank, pink, black, clipped or flickering portraits, or any change to the normal scene lighting.
3. Click Host diagnostics, wait for a listening state, then Leave. Repeat once. Leaving should keep the panel working. No other player is needed for this check; it does not prove a multiplayer connection.
4. Start a NEW game only in the prepared disposable profile. Once walking normally, press F6 to check the player and coffee-cup bindings. The result is written to the diagnostic log. F8 reveals the panel; use the pause menu to click its controls. A paused pose can legitimately be unavailable, so use F6 while unpaused for the walking check.
5. Press F7 once. It should say the problem report was saved. In the pause/menu panel, scroll to Problem note, enter a short description such as “manual feedback test,” and click Report a problem. Automatic mod exceptions are captured without this button. Reports stay on your PC; share reviewed evidence in a GitHub bug report.
6. Press F9 while aiming at a nearby ordinary item, then F10 to export diagnostics. Move around, pause/unpause and return to the menu through the game's normal flow. Load the disposable game again. Check that the panel and portraits still work and no duplicate UI remains.
7. Quit the game completely and record which step failed, or which checks passed. Follow INSTALL.md to restore and verify the original profile before re-enabling Steam Cloud.

Do not copy your original save into the test profile. A second PC/avatar test comes after this single-game gate. Shared items, driving, assembly and the wider world are not enabled in this build and should not be reported as working.

Useful keys: F6 binding check; F7 problem report; F8 panel; F9 aimed-object inspection; F10 diagnostic export. Keys can be changed in the loader's mod settings.
