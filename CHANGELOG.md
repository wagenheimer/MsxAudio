# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- `extract-direct`: runs the cartridge's sound driver **in process** (minimal MSX1 machine with a
  Z80 core, Konami SCC mapper, 16 KB RAM, PSG/SCC capture and BIOS stubs for `RST 30h`, `WRTPSG`
  and `RDPSG`). Experimental: the VDP vblank flag is not modelled yet, so the driver runs ~6% off
  the hardware tempo and loop detection fails.
- `probe` CLI command: boots a ROM in the built-in machine and dumps the interrupt hook state.
- Tests for the built-in machine, VGM writer round-trip, WAV decoding, loudness end detection and
  library lookups.

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
