# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added (export assistant, track list, scan)
- **Export assistant** (F3): 1 ROM, 2 METHOD, 3 EXTRACT. A test run of the cartridge reports which
  chips it uses and recommends openMSX (FM / MSX-AUDIO / SCC+) or the built-in extractor.
- **openMSX support**: `OpenMsxLocator`, `OpenMsxInstaller` (portable build from the project's GitHub
  releases, size + SHA-256 checked, per-user folder, never bundled) and `OpenMsxCapture` (records
  every chip the machine has). Capture and profile modes run on openMSX.
- **Track list**: a keyboard cursor (arrows with repeat, page, home/end, Enter plays, `T` play-on-move),
  different drawing for the playing row, the cursor and the mouse row, and `.msxaudio` packs shown as
  a group (colour stripe, `PACK` tag, "N SONGS IN M PACKS").
- **Open Folder dialog**: OPEN FOLDER button, recent folders with their full path.
- **Scan**: the game's own calls to the sound routine are ignored while scanning (its title music no
  longer replaces or mixes with the id), one fresh machine per id in parallel, each sound cut where it
  ends, ids 1-255, a filter for sparse never-ending tones read past the id table.

### Fixed
- Clicking a song of a `.msxaudio` pack highlighted the pack's first song.
- The scan searched Konami (4)'s fixed `0x4000` window in every bank and could pick a routine that
  cannot be called (Penguin Adventure found 6 "sounds"; it now finds 238 entries).
- **Konami (4) mapper** used the SCC cartridge's bank registers, so it never switched banks. It now
  uses `0x6000`/`0x8000`/`0xA000` with bank 0 fixed. This is what left Penguin Adventure silent.
- **SCC window** was always visible: reading `0x9800` returned `0xFF` even when that bank held ROM
  data. It now answers only while bank register `0x9000` holds `0x3F`.
- **Keyboard** was read through port `0xA8` (the slot register). The row is now selected on `0xAA`.
- The sound scan truncated its candidates by address (not by relevance), assumed a linear ROM and
  only tried `A` as the id register.

### Added
- **In-process MSX machine**: primary slots with 64 KB of RAM, `ENASLT`/`RSLREG`/`WSLREG`/`RDSLT`/
  `WRSLT`/`CALSLT`, the PPI, PSG read-back, joystick port select, `GICINI`, `GTSTCK`, `GTTRIG`,
  `RDVDP`, the BIOS key state (`NEWKEY`, `TRGFLG`), `HALT` waiting for vblank, page-2 cartridges and
  `R-Type`; **T-state timestamps** on every captured write; **50 Hz (PAL)** support.
- **Mapper detection** like openMSX: SHA-1 lookup in `softwaredb.xml` (when found) and the
  `LD (nn),A` vote heuristic; `RomInfo`, `rom-info` command and a **DETECTED** line in the player.
- **Diagnosis**: a silent capture or scan now says why (missing header, unsupported MSX-MUSIC /
  MSX-AUDIO / SCC+ writes, BIOS services that are stubs).
- **Smarter scan**: candidates ranked by callers and searched in every ROM bank, the id passed in
  `A`/`HL`/`DE`/`BC`, probing by *distinct* sounds against the game idling, an ambiguity warning.
  King's Valley 1's `setMusic` is now found without a profile.
- Capture: **auto-press** (`--auto 1`), `--refresh 50|60`; profiles gain `refreshRate`,
  `idRegister` and `soundCallBank`. The dialog gets the matching 50/60 Hz and AUTO-PRESS chips.
- `extract-direct`: runs the cartridge's sound driver **in process** (minimal MSX1 machine with a
  Z80 core, Konami SCC mapper, 16 KB RAM, PSG/SCC capture and BIOS stubs for `RST 30h`, `WRTPSG`
  and `RDPSG`).
- `probe` CLI command: boots a ROM in the built-in machine and dumps the interrupt hook state.
- Tests for the built-in machine (mappers, slots, PPI, timing, detection, scan), VGM writer
  round-trip, WAV decoding, loudness end detection and library lookups.

## [1.0.0] - 2026-11-11

### Added
- **Chips**: faithful AY-3-8910 (emu2149 port) and Konami SCC (K051649) emulation.
- **VGM**: parser, writer and sample-accurate loop detection.
- **Mixing**: `AudioProfile` (original / stereo / remaster), FIR decimation, biquad filters and a
  short stereo reverb.
- **`MsxAudioEngine`**: streaming playback straight from the VGM (continuous chip loop) plus a PCM
  sample player with polyphony.
- **`MsxAudioLibrary`**: `.msxaudio` container (ZIP with manifest + VGMs) to save and ship
  extractions.
- **`OpenMsxRomExtractor`**: drives openMSX with a per-game JSON profile to capture a ROM's music
  and effects.
- **`Wagenheimer.MsxAudio.MonoGame`**: `MsxAudioPlayer` bridge over `DynamicSoundEffectInstance`.
- **CLI**: `import`, `extract`, `info`, `render`, `verify`, `unpack`.
- **Example profile**: `profiles/kingsvalley2.json` (Konami King's Valley II, 1988).
