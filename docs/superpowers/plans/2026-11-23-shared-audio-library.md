# Shared audio library implementation plan

Goal: games publish independent variant packages that MSXScope discovers automatically.

Architecture: extend the existing package manifest compatibly, centralize canonical paths and manifest-only discovery in the core, and consume them in the player. Preserve the MSXScope executable and existing package format.

- [x] Add temporary-directory regression tests for variant paths, traversal rejection, identity roundtrip, legacy packages, nested discovery, malformed packages and cancellation.
- [x] Implement optional package identity, canonical path API, atomic file saves and reusable manifest-only catalog.
- [x] Integrate catalog metadata into player scanning, enable recursive default-library discovery and watch package publication with a debounce while preserving playback selection.
- [x] Add profile identity and provenance to both profile extractors and use canonical destinations for identified profiles.
- [x] Document the public contract, run core tests and solution builds, inspect actual local catalog without modifying packages.

Validation: dotnet test tests/Wagenheimer.MsxAudio.Tests/Wagenheimer.MsxAudio.Tests.csproj; dotnet build MsxAudio.slnx. All filesystem tests use unique temporary directories. No ROM extraction or migration touches the user library.

## Verification results

- 130 core/player-scanner tests passed, including a regression that first reproduced unstable package-track ordering with 32 entries.
- MSXScope built and opened without an explicit folder, finding the existing King's Valley II package (64 tracks). The legacy variant remained unknown.
- Runtime monitoring check used temporary copies: publishing a second package changed the list from 64 to 128 tracks while the original title continued playing (single load in the log, playback at 5 seconds in the final screenshot).
- King's Valley solution compiled against the modified library. Its extractor has not yet adopted the new canonical paths.
- No ROM extraction was run and no existing user packages were modified or migrated. Two existing nullable warnings were corrected where scan tasks legitimately return null; other pre-existing warnings remain outside this work.
