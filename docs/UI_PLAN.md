# Visualizer UI overhaul — plan

> **Status:** Phase 1 is implemented — folder browser (`F2`), recursive scanning, persisted settings,
> playlist with durations and the mixing-profile dialog (`O`). Phase 2 (transport bar) is next.

Goal: make `tools/MsxAudioVisualizer` feel like an application, not a demo. The player already
shows more than most MSX tools (per-channel scopes, wave RAM, roll, piano, song map, dB spectrum);
what is missing is **navigation** (where the music comes from), **control** (play, loop, volume) and
**explanation** (what the mixing profiles actually do).

## Principles

1. The UI keeps rendering to the fixed **1280×720** target and is letterboxed, so layout never
   depends on the window size.
2. **No native dialogs**: MonoGame/SDL has no portable folder picker. The browser is drawn by the
   game itself, in the same pixel style, and works on Windows/Linux/macOS.
3. **Keyboard and mouse parity**: every action is reachable both ways; no feature lives only on a
   hidden key.
4. Nothing that changes the audio is hidden: mute/solo/profile/loop always show their state.
5. Small, testable steps: each phase ships on its own, with screenshots through the existing
   deterministic gate (`MSXAUDIO_SHOT`).

## What hurts today (audit)

| Problem | Where |
| :--- | :--- |
| The playlist only knows the folder passed on the command line; no way to open another one in the app | `ScanTracks(_startDir)` |
| No recursive scan: subfolders are invisible | `ScanTracks` uses `TopDirectoryOnly` |
| Dropping a **folder** is ignored (only files are handled) | `Window.FileDrop` |
| The list is a flat wall of names: no search, no sort, no length, no empty state | `DrawPlaylist` |
| The profile is a hidden `O` cycle with no explanation | `CycleProfile` |
| Transport is keyboard-only: no play/pause button, no loop toggle, no volume | `DrawFooter` |
| Everything lives in one 2000-line file with ad-hoc hit rectangles | `VisualizerGame.cs` |

## Target layout

```
┌ HEADER ───────────────────────────────────────────────────────────────────────────────────┐
│ MSX AUDIO VISUALIZER   STAGE_A           [OPEN] [STEREO ▾] [VIEW: ROLL] [REC ●]  00:12/02:29│
├ PLAYLIST (316) ────────────────┬ ANALYZER ──────────────────────────────────────────────────┤
│ [search…]              [sort]  │  SPECTRUM / PIANO ROLL / PIANO                              │
│ RECURSIVE ☑  12 FILES   F5     │  (existing panels + song map)                              │
│ ▸ stage_a.vgm       02:29      ├ MASTER SCOPE ──────────────────────────────────────────────┤
│   stage_b.vgm       02:00      ├ CHANNELS ──────────────────────────────────────────────────┤
│   …                            │  (8 strips)                                                │
├ TRANSPORT ─────────────────────┴────────────────────────────────────────────────────────────┤
│ ⏮ ⏯ ⏭   [===========o=========]   LOOP ☑   VOL ▮▮▮▮▯   A-B [A][B]   H HELP                  │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

The analyzer area keeps its three views (`Tab`), the strip panel keeps colours/mute/solo and the
piano keeps the audition. The header gains real buttons; the footer becomes a transport bar.

## Phase 1 — navigation, settings, profiles (P0)

### 1.1 Folder browser (`F2`, `Ctrl+O`, `[OPEN]`)

Modal 900×520, centered, dim backdrop, `Esc` closes, input is captured by the modal.

```
┌ OPEN FOLDER ──────────────────────────────────────────────── [X] ┐
│ C:\ > Music > msx > stage_a.vgm                                   │
├ folders (300) ───────────┬ *.vgm (560) ──────────────────────────┤
│ ..                        │ stage_a.vgm      02:29   58 KB        │
│ Music/                    │ stage_b.vgm      02:00   51 KB        │
│ Tools/                    │ …                                     │
│                           │                                       │
├───────────────────────────┴───────────────────────────────────────┤
│ [RECURSIVE]  scan: 132 files in 12 folders        [SCAN] [CANCEL] │
└───────────────────────────────────────────────────────────────────┘
```

- Breadcrumb at the top; clicking a segment goes up to it.
- Left: drives/roots + folders (directories first, `..` always).
- Right: `.vgm` files of the selected folder, with duration when cheap to read (VGM header only) and
  size; with `RECURSIVE` on, it lists the matches of the whole subtree and shows the folder column.
- `Up/Down` move, `Enter` opens a folder / plays a file, `Backspace` goes up, `Esc` cancels.
- Recent folders (from settings) are offered when the list opens; `F5` rescans in place.
- Dragging a **folder** into the window opens it exactly like this dialog.

### 1.2 Library scan (recursive)

New `Library` service (`tools/MsxAudioVisualizer/Library.cs`):

- `Task<ScanResult> ScanAsync(string root, bool recursive, CancellationToken)` enumerating `*.vgm`
  (and later `.msxaudio`), skipping hidden/system folders and `bin`/`obj`/`.git`.
- Runs on a background thread; results are handed back through a thread-safe queue (never touch
  `GraphicsDevice` off the main thread). The status bar shows `SCANNING… 132 FILES` while it runs.
- Deterministic order: by relative path, case-insensitive.
- `ScanResult`: `Root`, `Files[]` (`Path`, `RelativePath`, `Folder`, `Size`, `Length?`), `Folders`,
  `Error?`.
- Refresh (`F5`) reuses the last root + recursive flag.

### 1.3 Settings

`VisualizerSettings` (`tools/MsxAudioVisualizer/VisualizerSettings.cs`), JSON in
`%APPDATA%\MsxAudio\visualizer.json` (portable `visualizer.json` next to the exe wins when present):

```json
{
  "version": 1,
  "lastFolder": "E:\\Music\\MSX",
  "recursive": true,
  "recentFolders": ["E:\\Music\\MSX", "D:\\VGM"],
  "profile": "stereo",
  "view": "roll",
  "volume": 0.85,
  "loop": true,
  "autoNext": true,
  "windowWidth": 1280,
  "windowHeight": 720,
  "pixelPerfect": false,
  "resumeLastTrack": false
}
```

Written on exit and whenever a folder/profile/view changes (debounced). Unknown fields are ignored;
a corrupt file falls back to defaults and logs a warning (never blocks startup).

### 1.4 Profile dialog (`O`, header chip)

Modal 640×400, one card per profile, applied on click (audible immediately) and persisted:

```
┌ MIXING PROFILE ────────────────────────────────────────────── [X] ┐
│ ● ORIGINAL   MONO                                                 │
│   Hardware-faithful. Channels centred, DC cut at 12 Hz, 4x.       │
│                                                                   │
│ ○ STEREO     STEREO                                               │
│   PSG ±0.30 · SCC ±0.55 · HP 20 Hz · LP 16 kHz · 4x               │
│                                                                   │
│ ○ REMASTER   STEREO                                               │
│   PSG ±0.40 · SCC ±0.70 · HP 25 Hz · LP 17 kHz · reverb 18% · 8x  │
│                                                                   │
│ Only the mix changes: the VGM and the chip emulation are identical.│
│ [O] cycles   [ENTER] apply   [ESC] close                          │
└───────────────────────────────────────────────────────────────────┘
```

The header chip shows the current profile and opens the same dialog. `O` keeps cycling for people
who are used to it, but the dialog is the discoverable path.

### Phase 1 acceptance

- Open a folder with 200 VGMs in subfolders, recursive on → all are listed; `Enter` plays; `Esc`
  cancels; the folder and flag survive a restart.
- Dropping a folder behaves like `F2`.
- Choosing REMASTER changes the sound and the header, and persists.
- Screenshot gate still deterministic; `dotnet build` and `dotnet test` clean.

## Phase 2 — playlist and transport (P1)

- **Search** (`/` focuses; Esc clears): filters by name/path, keeps the current track visible.
- **Sort** by name / duration / folder (click the header).
- **Rows**: name, duration, folder (when recursive), now-playing marker, and a scrollbar.
- **Empty state**: "no .vgm here" + `[OPEN FOLDER]` + `[SCAN SUBFOLDERS]`.
- **Transport bar**: previous / play-pause / next / stop, seek, `LOOP` toggle, master volume
  (keyboard `+`/`-`, mouse drag), `A-B` loop with two markers (great for learning a section).
- **Auto-next** stays, but moves to `Shift+L`; `L` becomes `LOOP` (documented in the help).
- Status messages become a small toast queue (3 lines) instead of one overwritten string.

## Phase 3 — views (P1/P2)

- **Roll zoom**: window 6/12/24 s (`[`/`]`), pitch range C1–C7 vs auto-fit to the notes, follow mode.
- **Piano**: computer keyboard mapping (two rows, octave with `-`/`=`), so melodies can be played
  without the mouse; optional "record performance" to WAV (reuses `WavWriter`).
- **Channels**: click a strip header to select it for the piano; the current selection is outlined.
- **Spectrum**: keep peak hold; optional average/peak toggle.
- **Overlay screenshots** for docs: `MSXAUDIO_SHOW=browser|profile|help` renders a modal at the gate
  frame.

## Phase 4 — polish (P2)

- `PIXEL PERFECT` option: integer letterbox scale (PointClamp) for crisp text on any window; the
  current smooth scale stays for fractional sizes.
- Tooltips on hover (buttons, chips, keys) and a consistent focus ring.
- Header/panel spacing scale, disabled states, and a small icon set drawn as rects (no assets).
- Record button in the header (uses the existing `--record` pipeline) with progress and the output
  path shown when it finishes.
- Optional "open output folder" (Windows Explorer) after a recording.
- Split the 2000-line `VisualizerGame.cs` into `Screens/` + `Ui/` (see technical plan).

## Keybindings (target)

| Key | Action |
| :--- | :--- |
| `F1` / `H` | help overlay |
| `F2` / `Ctrl+O` | open folder dialog |
| `F5` | rescan current folder |
| `/` | search playlist (`Esc` clears) |
| `↑` / `↓` | track selection |
| `Enter` | play selected |
| `Space` | play / pause |
| `←` / `→` | seek ∓5 s (`Shift` = 30 s) |
| `Tab` | cycle spectrum → roll → piano |
| `O` | mixing profile dialog (cycles inside) |
| `L` | loop on/off |
| `Shift+L` | auto-next |
| `R` | restart |
| `P` / `S` | PSG / SCC on/off |
| `1…8` | mute channel |
| `+` / `-` | volume |
| `Esc` | close modal, else quit |

## Technical plan

**Files** (a folder per concern keeps the game loop readable):

```
tools/MsxAudioVisualizer/
  VisualizerGame.cs        loop, audio feed, screenshot/record
  Library.cs               scan (recursive, async) + recent folders
  VisualizerSettings.cs    load/save JSON
  Ui/Theme.cs              colours, spacing, text scales
  Ui/Widgets.cs            Button, IconButton, Checkbox, List, Scrollbar, Tooltip
  Ui/ModalStack.cs         backdrop, input capture, draw order
  Ui/FolderBrowserDialog.cs
  Ui/ProfileDialog.cs
  Ui/HelpDialog.cs
  Screens/HeaderPanel.cs
  Screens/PlaylistPanel.cs
  Screens/TransportPanel.cs
  Screens/AnalyzerPanel.cs  (spectrum + roll + song map)
  Screens/PianoPanel.cs
  Screens/ChannelsPanel.cs
  SongMap.cs
```

- **Modal stack**: `Esc` closes the top modal; while one is open the rest of the UI is dimmed and
  receives no input. The screenshot gate can render a specific modal (`MSXAUDIO_SHOW`).
- **Async scan**: `Task.Run` + `ConcurrentQueue<ScanProgress>`; the game drains the queue in
  `Update`. Cancel with a `CancellationTokenSource` when the folder changes or the dialog closes.
- **Drag & drop**: `Directory.Exists(path)` → open in the browser; `File.Exists` → load and scan its
  folder (current behaviour).
- **Settings**: write with `File.WriteAllText` to a temp file and `File.Move` over the target (atomic
  enough on all three OSes); corruption falls back to defaults.
- **Volume / loop / A-B**: `VgmPlayer` gains `Volume` (applied at the mix), `LoopEnabled` and
  `LoopStart/LoopEnd` (seek back to A when `B` is crossed, in `Render`).
- **Refactor order**: move widgets first (no behaviour change), then the panels, then add the new
  dialogs — the screenshot gate proves each step did not move the existing pixels.

## Risks

| Risk | Mitigation |
| :--- | :--- |
| Recursive scan of a huge drive stalls the UI | background task + progress + cancel; hard cap with a warning (e.g. 5000 files) |
| Modal + existing ad-hoc hit rectangles conflict | modal stack checked before any panel input |
| Reading duration of every file is slow on network shares | read only the VGM header, and only for the selected folder (lazy in the list) |
| Settings file in a read-only folder | fall back to `%APPDATA%`, never crash |
| Big refactor breaks the screenshot baseline | one commit per extracted panel, compare PNGs with `gumcli`-style pixel diff (simple `Bitmap` compare) |

## Out of scope (later)

- Browsing `.msxaudio` packages inside the app (list songs, play, export) — high value, needs the
  library reader in the tool.
- MIDI export of the piano performance.
- Video recording from the UI (the CLI already does GIF/MP4; the header button is Phase 4).
- Full localization (the UI stays English, matching the README).

## Recommended order

1. Phase 1 (open folder, recursive scan, settings, profile dialog) — this is what prompted the plan.
2. Phase 2 transport (play/pause/loop/volume/A-B) — makes the player usable without the keyboard.
3. Phase 3 roll zoom + piano keyboard — depth for people studying a song.
4. Phase 4 polish, then `.msxaudio` browsing.
