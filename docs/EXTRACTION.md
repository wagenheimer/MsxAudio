# Music extraction — how it works, what works

MSX has **no standard music format**. A cartridge's music is code plus data that only makes sense
while the game's own driver runs. So extraction here never "decodes" a file: it runs the driver on a
small in-process MSX machine and records the register writes, which become a VGM (and a `.msxaudio`
package).

```
.rom / .zip ──► RomFile.Resolve ──► MiniMsx (Z80 + mapper + RAM + hooks + PSG/SCC capture)
                                          │
              ┌───────────────────────────┼───────────────────────────┐
              ▼                           ▼                           ▼
      RomCapture (boot+keys)      RomSoundScan (probe ids)     DirectRomExtractor (profile)
              │                           │                           │
              └───────────────► VgmFile ──┴──────────► VgmWriter / MsxAudioLibrary
                                                                      │
                                                        MSXScope player / WAV render
```

## The machine (`Extraction/MiniMsx.cs`)

Deliberately minimal — it is not an emulator you can play on:

| Part | What is modelled |
| :--- | :--- |
| CPU | Z80 (Z80dotNet), one CPU for the main program and a **second one for the interrupt hooks**, so registers survive a frame |
| Cartridge | `Linear` (16/32 KB), `Konami`, `Konami SCC`, `ASCII8`, `ASCII16` — `Auto` looks for SCC references instead of assuming 128 KB = SCC |
| RAM | 16 KB at `0xC000` |
| VDP | only the vblank bit and `STATFL` (most drivers poll it) |
| BIOS | entry stubs: `RST 30h` (inter-slot), `WRTPSG` (`0x0093`), `RDPSG` (`0x0096`), `SNSMAT` (`0x0141`) |
| Input | keyboard matrix (`0xA8`/`0xA9`, 9 rows) and joystick through PSG port A (reg 14) |
| Capture | every PSG/SCC write with its timestamp; PSG and SCC can be recorded independently |

Boot = cartridge `INIT` + `H.STKE`. Each frame = vblank + JIFFY + `H.KEYI` + `H.TIMI` + a slice of the
main program. A hook that does not fit the frame budget **continues on the next frame** instead of
being restarted (restarting it left King's Valley 1 stuck on its re-entry guard).

Diagnostics that paid for themselves: `ProbeHook()` (hot PCs), `PcTrace`, `CapturedEvents`,
`CallAddress`, `Advance`, `HookBudgetExhausted`.

## The three extraction modes

| Mode | Entry point | Needs a profile? | What you get |
| :--- | :--- | :--- | :--- |
| **Capture** | `RomCapture.Run` | no | whatever the game plays after the key script (any cartridge) |
| **Scan** | `RomSoundScan.Run` | no (optional) | the ids the driver accepts, classified music/sfx, deduplicated |
| **Profile** | `DirectRomExtractor.Extract` | yes | every id with its name and duration |

### Profiles (`profiles/*.json`)

`RomExtractionProfile` describes one game:

| Field | Meaning |
| :--- | :--- |
| `soundCallAddress` | routine that plays a sound, id in `A` |
| `playerAddress` | per-frame sound player — when set, **only the driver runs** (no game logic) |
| `resetAddresses` | RAM bytes cleared before each id (per-channel current-song bytes) |
| `idMask` | bits OR-ed into the id (e.g. `0x80` = "music stream") |
| `stopId` | id that silences everything before a capture |
| `ids` / `musicIds` / `secondsById` / `names` | what to capture and how to label it |

`soundCallAddress` + `ids` is enough for drivers with an id API (King's Valley II). `playerAddress`
matters when the driver's interrupt handler also runs the whole game: then the game overwrites the
song as soon as it changes state (King's Valley 1).

### Scan (`Extraction/RomSoundScan.cs`)

When there is no profile: find candidate routines (every `CALL` target whose body, or the body of a
routine it calls, writes to the PSG/SCC — direct `OUT`, `CALL WRTPSG`, or `LD (0x98xx)`), **probe**
each with a few ids and keep the one that produces pitch, then call it with every id, stop early when
an id writes nothing, and classify:

- loop found (≥ 2 s) → music;
- notes filling the capture (first→last pitch write ≥ 75 % of it) → music;
- otherwise → effect.

Identical streams are dropped as duplicates. Ids come out named `id_NN`; the caller renames.

## What works today

| Game | Cartridge | Mode | Result |
| :--- | :--- | :--- | :--- |
| King's Valley 1 (RC-727) | 16 KB, linear, PSG | Capture | music from ~5–7 s after `START@1`, 394 pitch writes in 30 s |
| King's Valley 1 | " | Profile | **8 songs + 8 effects named**; `ingame` = 12.8 s loop (RMS 0.040 across the loop) |
| King's Valley 1 | " | Scan | 24 sounds in ids 1–31 |
| King's Valley II (RC-760) | 128 KB, Konami SCC | Profile | full list, ~97k writes in 100 s |
| King's Valley II | " | Capture | 13.4k writes / 5.7k pitch in 15 s without a profile |
| King's Valley II | " | Scan | 65 sounds, 5 duplicate ids dropped |
| Penguin Adventure (RC-743) | 128 KB, Konami **without** SCC | Capture/Scan | ⚠️ opens (`.zip` included) and boots, the driver runs (mixer writes), **no music yet** |

## Limits worth knowing

- **No static decode.** A game whose driver cannot be started without its state machine needs a
  profile (or reverse engineering), not a better scanner.
- **The machine is not a full MSX.** No video beyond vblank/STATFL, no BIOS beyond the four stubs: a
  game that waits on real VDP/BIOS behaviour will not progress.
- **Discovery is a heuristic.** It finds routines called with `CALL`; a driver reached only through a
  jump table, or taking its id in `HL`/memory, is not found.
- **Loop detection needs length.** King's Valley 1's theme is a 12.8 s loop: captures shorter than
  ~2 loops show "no loop".
- **Names are curation.** The ROM does not store them: they come from a profile, or from `id_NN`.

## Where to look

| Path | Contents |
| :--- | :--- |
| `Extraction/MiniMsx.cs` | the machine |
| `Extraction/RomCapture.cs` | capture + `KeyPress` (`START@1`, `RIGHT@4`, `8:1@2`) |
| `Extraction/RomSoundScan.cs` | routine discovery, id probing, classification |
| `Extraction/DirectRomExtractor.cs` | profile-driven extraction |
| `Extraction/RomExtractionOptions.cs` | profile/options model, `RomExtractionProfiles` |
| `Extraction/RomFile.cs` | `.zip` → cartridge inside (cached extraction) |
| `Extraction/RomMapper.cs` | mappers, `Auto` detection |
| `Vgm/VgmLoopFinder.cs` | loop detection on the recorded stream |
| `Songs/MsxAudioLibrary.cs` | `.msxaudio` package (ZIP + manifest) |
| `tools/MsxAudioTool/Program.cs` | CLI: `capture`, `scan`, `extract-direct`, `probe`, `render` |
| `tools/MsxAudioVisualizer/VisualizerGame.Extract.cs` | MSXScope dialog (F3): capture / profile / scan |
| `profiles/` | curated per-game profiles |
| `tests/Wagenheimer.MsxAudio.Tests/` | machine, scan and key-script tests |

## Next steps

1. **Penguin Adventure**: find its `setMusic`/table (same method used for King's Valley 1) and add a
   profile — or determine what the driver waits for.
2. Rename scanned ids in the player (write the package back).
3. Scan: detect indirect SCC writes (`LD HL,0x98xx` + `LD (HL),A` is covered, `OUT (C),A` is not),
   and drivers whose entry is reached through a jump table.
4. Tag a release (`v1.1.0`) so the extraction UI ships in the prebuilt archives.
