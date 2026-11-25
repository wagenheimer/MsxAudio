# MSXScope library UI refinement

Status: approved and implemented.

## Direction

Evolve the current dark navy and amber instrument-panel style. Improve the library and action hierarchy without replacing the rendering stack, font, visualizers or playback engine. Keep the 1280x720 design canvas and existing letterboxing.

## Alternatives

1. Refine the current instrument-panel layout (recommended): a wider library, visible filters and clearer package/track hierarchy while retaining familiar navigation.
2. Add only three small filter buttons: minimal disruption, but leaves cramped metadata, short track names and crowded actions.
3. Redesign as an album-oriented player: a larger change requiring artwork and new navigation, outside this refinement.

## Layout

Use a 380px library panel at x=12 with a 12px gap before the analysis area at x=404. Update spectrum, piano, roll, waveform, channels and seek-bar bounds consistently through named layout constants. Keep the bottom transport at full width.

Library header contains LIBRARY, a concise package/track summary, and an OPEN action. A second row has three equal clickable segmented controls: ALL, MUSIC and SFX, each with a count. The selected segment has an amber fill and dark label; hover is distinct from selection. F5 remains an alternative to the same state transition.

Place collapse/expand actions on a separate small toolbar rather than squeezing them into the heading. Use the remaining height for the scrollable list and a short footer. Move lengthy technical folder paths away from the primary heading; truncate them within their own footer space.

Package rows show the manifest title, a separately allocated variant badge (MSX1, MSX2 or UNKNOWN), and matching-track count. Long titles truncate before the badge instead of hiding it. Indent tracks under packages, reserve the right column for duration, and use the wider available name column. Clearly distinguish the playing track, keyboard cursor and mouse hover.

Keep the main title and transport visible while filtering. The analysis area retains current views and channel controls. Improve header spacing so version text, now-playing information and extraction/export/profile actions cannot overlap.

## Behavior

Mouse and F5 update one shared filter state. Changing a filter resets the list viewport but does not restart or stop playback. Next/previous and auto-next continue following the filter. Counts represent music and clips according to metadata; loose VGMs remain visible under ALL without guessing a category.

A filter with zero matching tracks shows a dedicated message and an actionable ALL control. Empty library, active scan and scan errors use separate states. Mouse hit rectangles match rendered controls and do not overlap list rows or scrollbar areas.

## Validation

Verify ALL/MUSIC/SFX mouse transitions, counts and empty results; pack folding, scrolling, keyboard navigation and duration alignment; long titles and both cartridge badges; and unchanged ongoing playback during filtering. Build the solution and run relevant tests. Capture and inspect the native app at 1280x720 and a smaller letterboxed window. Use temporary settings and temporary test packages for diagnostic content, preserving the user's library.

## Scope

No extraction-format changes, generated album artwork, browser frontend, or audio-engine changes. Existing in-progress library work is preserved.
