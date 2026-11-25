# MSXScope library layout implementation

Approved design: ../specs/2026-11-23-msxscope-library-ui-design.md.

- Add shared named geometry for the wider library and all analyzer views.
- Add clickable All/Music/SFX segments with category counts and a single filter transition shared with F5.
- Separate library title, summary, folding controls and package badges; reserve duration columns and add empty-filter recovery.
- Rebalance application header and preserve transport, visualizers, scrolling and playback.
- Build, run tests, inspect screenshots and exercise real mouse input with temporary settings.

## Validation

Native mouse checks: ALL/MUSIC/SFX showed 64/11/53 tracks for the existing package, without restarting its title stream. Scrolling, FOLD and EXPAND, Spectrum, Piano Roll and Piano were inspected. A temporary two-package fixture with long titles displayed separate MSX1/MSX2 badges in a 960x540 window. SFX with zero matches displayed recovery UI; SHOW ALL TRACKS restored the list. Fixture variant labels were used only to test geometry, not to assert MSX2 extraction fidelity. The F5 state transition remains shared with mouse selection; short synthetic key presses were not reliably captured by the frame-polled input during manual automation.

User packages and per-user settings were preserved. Portable test settings were removed after verification.
