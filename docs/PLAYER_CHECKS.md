# End-of-build manual checklist

Run these checks when you are ready to test. Version 0.3.0 implements a limited cup/van experiment; it does not complete the full-game scope. Automated checks do not count as these game tests.

Use an independently prepared disposable profile on each PC, following [INSTALL.md](INSTALL.md). Keep Steam Cloud off. Retain the original preparation folder and journal for restoration; an already active profile must not be prepared again from a new ZIP. Do not copy personal saves into it.

1. Install the same 0.3.0 ZIP on both PCs while the game is closed. Load the disposable game and wait until you can walk. Put down any native held item. Open F8 using the pause menu.
2. Choose Male on one PC and Female on the other. Both portraits should be complete, textured and free of pink/black/flickering parts. Swap choices later and check that the old character disappears.
3. Enable **Shared world** on both PCs. Host on port **20577**; the friend joins using the host's LAN or authorized Tailscale IPv4 address. Wait for **Shared cup and van ready** on both. Record a refusal with F7; do not bypass a failed binding or profile check.
4. Hide F8 and unpause both games. Verify that each player sees the other's walking, turning and selected character. Try Ping from the panel. A listening host with zero peers is still waiting for a friend.
5. At the coffee cup, left-click within reach to pick it up. Both players should see exactly one cup held by the same player, with a carrying pose. Race to pick it up: only one may succeed. Left-click to drop; right-click while holding to throw. Check position and rotation on both PCs. Drinking/pouring and other items are outside this build.
6. Stand within six metres of the Hayosiko, empty-handed. Use **Request van driver seat**. Only one player may receive the seat, including simultaneous attempts. Close F8, unpause, hold **I** to crank and use configured steering/throttle/brake/clutch/gears. The other PC should see the same body motion and the driver's seated pose. Doors/dashboard, passengers, wheel/damage state and other vehicles are outside this build.
7. Stop before exiting. Try the normal driving-mode key or **Leave van driver seat**. Confirm the walking camera/controller returns. Swap drivers. Open the panel while driving: controls should brake instead of continuing throttle. Host pause stops host physics.
8. Leave and rejoin while another player holds the cup or occupies the van. A joining player must wait for the baseline, then agree on the holder, driver and body position. Disconnect the holder/driver: the cup should release, the seat should clear and stale vehicle controls should stop within 500 ms while host simulation is running.
9. Repeat Host/Leave, then return to the menu and load again. Check for duplicate characters/UI, stuck hands/cameras, enabled client physics or continued vehicle movement after disconnect. Press F7 or add a short panel note for any failure; reports include this mod's recent diagnostics. They stay local until shared deliberately.
10. Quit normally. Record pass/fail observations and the installed version on both PCs. From each original preparation folder, restore and verify the personal profile using INSTALL.md before re-enabling Steam Cloud.

Useful keys: F6 read-only player/cup bindings, F7 problem report, F8 panel, F9 aimed-object inspection, F10 diagnostic export. Keys can be changed in the loader settings.

Do not judge unrelated assembly, shops/jobs, needs/death, weather/time, NPCs or vanilla save progression as synchronized. Those adapters remain unfinished. Use the current checklist to identify concrete failures in the implemented scope.