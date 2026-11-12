# Music extraction — how it works, what works, what to do when it does not

MSX has **no standard music format**. A cartridge's music is code plus data that only makes sense
while the game's own driver runs. So extraction here never "decodes" a file: it runs the driver on a
small in-process MSX machine and records the register writes, which become a VGM (and a `.msxaudio`
package). This is the same idea KSS players use (a RET-filled BIOS page, PSG stubs, `INIT` with the
song number, `PLAY` every frame) — with a slot model, the common mappers and a diagnosis report on top.

```
.rom / .zip ──► RomFile.Resolve ──► RomInfo (SHA-1 → openMSX database, else heuristic) ──► mapper
                                          │
                                          ▼
                    MiniMsx (Z80 + slots + mapper + 64 KB RAM + PPI + BIOS entries + PSG/SCC capture)
                                          │
              ┌───────────────────────────┼───────────────────────────┐
              ▼                           ▼                           ▼
      RomCapture (boot+keys)      RomSoundScan (find ids)     DirectRomExtractor (profile)
              │                           │                           │
              └───────────────► VgmFile ──┴──────────► VgmWriter / MsxAudioLibrary
                                                                      │
                                                        MSXScope player / WAV render
```

## The machine (`Extraction/MiniMsx.cs`)

Deliberately small — it is not an emulator you can play on, but it models what drivers touch:

| Part | What is modelled |
| :--- | :--- |
| CPU | Z80 (Z80dotNet). One CPU for the main program and a **second one for the interrupt hooks**, so registers survive a frame |
| Slots | Primary slots through the PPI (`0xA8`): slot 0 = BIOS, slot 1 = the cartridge, slot 3 = 64 KB of RAM. `ENASLT`, `RSLREG`, `WSLREG`, `RDSLT`, `WRSLT`, `CALSLT` are emulated. A 16 KB cartridge leaves RAM in page 2; a page-2 cartridge (header pointing at `0x8000`) loads there |
| Cartridge | `Linear`, `Konami` (4), `Konami SCC`, `ASCII8`, `ASCII16`, `R-Type`; see the table below |
| PPI | `0xA8` slot register, `0xA9` keyboard column, `0xAA` row select (+ `0xAB` bit set/reset) |
| PSG | register shadow (drivers read the mixer back), joystick on port A with the register-15 port select; registers 14/15 are never recorded |
| VDP | the vblank bit and `STATFL`; `RDVDP` returns the status |
| BIOS | `RST 30h`, `GICINI`, `WRTPSG`, `RDPSG`, `GTSTCK`, `GTTRIG`, `RDVDP`, `RSLREG`, `WSLREG`, `SNSMAT`, the slot calls. Everything else in page 0 is a `RET` — and is **counted**, so the diagnosis can say what a game wanted |
| BIOS work area | `JIFFY`, `STATFL`, `NEWKEY`/`OLDKEY`, `TRGFLG`, `EXPTBL`, `BOTTOM`/`HIMEM` |
| Timing | every PSG/SCC write is stamped with the **Z80 T-states** spent inside its frame (44.1 kHz), not just the frame number; `HALT` waits for the next vblank. 60 Hz by default, **50 Hz (PAL) selectable** |
| Capture | PSG and SCC can be recorded independently |

Boot = cartridge `INIT` (if the header has one) + `H.STKE`. Each frame = vblank + `JIFFY` + `H.KEYI` +
`H.TIMI` + a slice of the main program. A hook that does not fit the frame budget **continues on the
next frame** instead of being restarted (restarting it left King's Valley 1 stuck on its re-entry guard).

### Mappers and how one is chosen

| Mapper | Bank registers | Notes |
| :--- | :--- | :--- |
| Linear | — | up to 32 KB at `0x4000`; a 16 KB image whose `AB` header points into `0x8000` loads at `0x8000` |
| Konami (4) | `0x6000` / `0x8000` / `0xA000` | `0x4000` is fixed to bank 0. Penguin Adventure |
| Konami SCC (5) | `0x5000` / `0x7000` / `0x9000` / `0xB000` | the SCC window (`0x9800`) is visible **only while bank register `0x9000` holds `0x3F`**; otherwise it is ordinary ROM data |
| ASCII8 | `0x6000` / `0x6800` / `0x7000` / `0x7800` | starts on bank 0 everywhere |
| ASCII16 | `0x6000` / `0x7000` | |
| R-Type | `0x6000`–`0x7FFF` | bank `0x0F` fixed at `0x4000`, a 16 KB bank at `0x8000` |

`RomInfo.Analyze` (shown as the **DETECTED** line in the player) picks the mapper like openMSX does:

1. **SHA-1 lookup** in an openMSX `softwaredb.xml`, when one is found (`MSXAUDIO_SOFTWAREDB`, next to the
   player, or in an openMSX install). This is the only way to tell ROMs under 64 KB apart.
2. Otherwise the **heuristic**: below 64 KB nothing is banked; above, every `LD (nn),A` (`0x32`) votes
   for the mapper that owns that register address (`0x5000/0x9000/0xB000` → SCC, `0x4000/0x8000/0xA000`
   → Konami, `0x6800/0x7800` → ASCII8, `0x6000` → Konami + ASCII8 + ASCII16, `0x7000` → SCC + ASCII8 +
   ASCII16, `0x77FF` → ASCII16; ASCII8 is penalised by one vote and a tie goes to the later entry).

A wrong mapper is the most common reason a driver "runs but plays nothing", so the dialog lets you
override it, and `MsxAudioTool rom-info` prints what was decided.

## The three extraction modes

| Mode | Entry point | Needs a profile? | What you get |
| :--- | :--- | :--- | :--- |
| **Capture** | `RomCapture.Run` | no | whatever the game plays after the key script (any cartridge) |
| **Scan** | `RomSoundScan.Run` | no (optional) | the ids the driver accepts, classified music/sfx, deduplicated |
| **Profile** | `DirectRomExtractor.Extract` | yes | every id with its name and duration |

### Capture

Boots the ROM, sends the key script and records. `--auto 1` (the **AUTO-PRESS** chip) taps fire at 1 s and
then fire/space alternately every 3 s when no script is given, so most games leave their title screen
without hand-written keys. `--refresh 50` records a PAL game at its real tempo.

### Scan (`Extraction/RomSoundScan.cs`)

When there is no profile it has to find the "play sound" routine by itself:

1. **Candidates.** Every call target (`CALL nn` and the conditional calls) whose body — or the body of a
   routine it calls or jumps to — writes to the PSG/SCC (`OUT`, `CALL WRTPSG`, `LD (9800h+),A`,
   `LD HL/DE,98xx` + a store, `LD C,A0` + any `OUT (C),r`). On banked cartridges **every bank** is
   searched. They are ranked by how many places call them. The busiest call targets are added even when
   they write nothing themselves, because a "play sound" entry often only queues the id for a per-frame
   player.
2. **Probe.** Each candidate is called with eight ids, with the id passed in `A`, then (only if nothing
   answered) in `HL`, `DE` or `BC`; the bank it lives in is forced for the call. A candidate scores by how
   many ids make a **distinct** sound — different from the game idling on its own and from each other —
   with at least `MinNoteWrites` notes. A register helper (it writes whatever it is given) never reaches
   the bar.
3. **Scan.** The chosen routine is called with every id; ids that write nothing within
   `FailureSeconds` are skipped, identical streams are dropped as duplicates, and each sound is
   classified: loop found (≥ 2 s) → music; notes filling the capture (≥ 75 %) → music; otherwise an effect.

Ids come out as `id_NN`; rename them in a profile (`names`) or in the package.

> **Honest limit.** A game that plays its own music while the probe runs makes every routine look alike.
> When fewer than three distinct sounds separate the winner from the rest, the scan says so
> (`the sound routine could not be told apart from the game's own music`) and the list may contain copies
> of that music. A profile is the answer there.

### Profiles (`profiles/*.json`)

`RomExtractionProfile` describes one game:

| Field | Meaning |
| :--- | :--- |
| `soundCallAddress` | routine that plays a sound, id in `A` (or see `idRegister`) |
| `soundCallBank` | ROM bank the routine lives in (`-1` = whatever is mapped after boot) |
| `idRegister` | `A` (default), `HL`, `DE` or `BC` |
| `playerAddress` | per-frame sound player — when set, **only the driver runs** (no game logic) |
| `resetAddresses` | RAM bytes cleared before each id (per-channel current-song bytes) |
| `idMask` | bits OR-ed into the id (e.g. `0x80` = "music stream") |
| `stopId` | id that silences everything before a capture |
| `romType` | mapper name (`KonamiSCC`, `Konami`, `ASCII8`, `ASCII16`, `R-Type`, `linear`); empty = detect |
| `refreshRate` | `60` (default) or `50` |
| `ids` / `musicIds` / `secondsById` / `names` | what to capture and how to label it |

`soundCallAddress` + `ids` is enough for drivers with an id API (King's Valley II). `playerAddress`
matters when the driver's interrupt handler also runs the whole game: then the game overwrites the
song as soon as it changes state (King's Valley 1).

## When nothing plays: the diagnosis

The machine counts what a game asked for and could not get. A silent capture or scan ends with lines like:

```
why: no 'AB' header at 0x4000: this does not look like a plain cartridge (wrong mapper, or a BASIC/disk-loaded program)
why: the game writes to MSX-MUSIC (YM2413): 4180 writes that are NOT recorded
why: the game writes to the SCC+ registers: 1930 writes that are NOT recorded
why: BIOS 00DE GTPDL was called 812x and is a plain RET here
```

| Message | What it means | What to try |
| :--- | :--- | :--- |
| no `AB` header | wrong mapper, or not a plain cartridge | pick the mapper by hand; `rom-info` |
| MSX-MUSIC / MSX-AUDIO / SCC+ writes not recorded | the music is on a chip that is not emulated yet | extract with openMSX (`extract`), or use the PSG/SCC part only |
| a BIOS service called many times | the game waits for something that is a stub here | tell us which one (open an issue with the ROM's SHA-1) |
| only mixer writes | the game stayed on its title screen | `--auto 1`, a longer capture, or a key script |

## What works today

Verified on the real cartridges:

| Game | Cartridge | Mode | Result |
| :--- | :--- | :--- | :--- |
| King's Valley 1 (RC-727) | 16 KB, linear, PSG | Capture | music from ~5–7 s after `START@1`, 394 pitch writes in 30 s |
| King's Valley 1 | " | Profile | **8 songs + 8 effects named**; `ingame` = 12.8 s loop |
| King's Valley 1 | " | Scan, no profile | finds `setMusic` (`0x7AA9`) by itself: 29 sounds in ids 1–31 |
| King's Valley II (RC-760) | 128 KB, Konami SCC | Capture | 12.2k writes / 5.9k pitch in 15 s |
| King's Valley II | " | Profile | full named list (needs a profile: the game plays its own music while probing) |
| Penguin Adventure (RC-743) | 128 KB, **Konami (4)**, PSG | Capture | **music now captured**: 2822 pitch writes in 40 s with auto-press (it was silent before the mapper fix) |

## Limits worth knowing

- **No static decode.** A game whose driver cannot be started without its state machine needs a
  profile (or reverse engineering), not a better scanner.
- **The machine is not a full MSX.** No video beyond vblank/`STATFL`, a handful of BIOS entries; a game
  that waits on real VDP/BIOS behaviour will not progress (the diagnosis says which entry).
- **Chips.** PSG and SCC are recorded. **SCC+, MSX-MUSIC (YM2413), MSX-AUDIO (Y8950) and MoonSound are
  not**: the writes are counted and reported, not recorded.
- **Discovery is a heuristic.** It finds routines reached with `CALL`; a driver reached only through a
  jump table, or one that lives in RAM, is not found.
- **Loop detection needs length.** King's Valley 1's theme is a 12.8 s loop: captures shorter than
  ~2 loops show "no loop".
- **Names are curation.** The ROM does not store them: they come from a profile, or from `id_NN`.

## Where to look

| Path | Contents |
| :--- | :--- |
| `Extraction/MiniMsx.cs` | the machine |
| `Extraction/RomMapper.cs` | mappers, detection (`RomMapperNames.Guess`) |
| `Extraction/RomInfo.cs` | size, SHA-1, mapper (database/heuristic), header; the `softwaredb.xml` reader |
| `Extraction/RomCapture.cs` | capture + `KeyPress` (`START@1`, `RIGHT@4`, `8:1@2`) + auto-press |
| `Extraction/RomSoundScan.cs` | candidates, probe, id scan, classification |
| `Extraction/DirectRomExtractor.cs` | profile-driven extraction |
| `Extraction/RomExtractionOptions.cs` | profile/options model |
| `Extraction/RomFile.cs` | `.zip` → cartridge inside (cached extraction) |
| `Vgm/VgmLoopFinder.cs` | loop detection on the recorded stream |
| `Songs/MsxAudioLibrary.cs` | `.msxaudio` package (ZIP + manifest) |
| `tools/MsxAudioTool/Program.cs` | CLI: `capture`, `scan`, `extract-direct`, `probe`, `rom-info`, `render` |
| `tools/MsxAudioVisualizer/VisualizerGame.Extract.cs` | MSXScope dialog (F3) |
| `profiles/` | curated per-game profiles |
| `tests/Wagenheimer.MsxAudio.Tests/` | machine, mapper, scan and key-script tests |

## Prior art

- **KSS** (`libkss`, `kss2vgm`): a KSS file is the driver plus its data; a player sets `A` to the song
  number, calls `INIT`, then `PLAY` at the refresh rate, with a RET-filled page 0 and PSG stubs at
  `0x0093`/`0x0096`. This machine follows the same model but starts from the original cartridge.
- **openMSX `vgm_rec`** records everything a real emulated MSX plays (PSG, SCC, MSX-MUSIC, MSX-AUDIO,
  MoonSound). It is the reference path (`MsxAudioTool extract`) for what this machine cannot run.
- openMSX's `guessRomType` and `softwaredb.xml` are the source of the mapper rules above.

## Next steps

1. MSX-MUSIC (YM2413) and SCC+ capture: VGM already has the commands, the machine needs the ports and
   the SCC+ register block.
2. Find the per-frame player automatically (shadow call stack on the interrupt CPU) so the scan can
   silence a game's own music, instead of asking for a profile.
3. Rename scanned ids in the player (write the package back).
4. Scan: jump-table entries and drivers copied to RAM; SRAM mappers (Hydlide 2, Xanadu).
