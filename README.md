# Wagenheimer.MsxAudio

Emulador de áudio de **MSX** (PSG **AY-3-8910** + **Konami SCC/K051649**), leitor/gravador de
**VGM**, extração de música de **ROM** e um **motor de reprodução** para jogos — com um formato de
biblioteca próprio (`.msxaudio`) para salvar e distribuir as extrações.

Sem dependência de engine no núcleo: o pacote `Wagenheimer.MsxAudio` é C# puro. A ponte com o
MonoGame fica em `Wagenheimer.MsxAudio.MonoGame`.

```bash
dotnet add package Wagenheimer.MsxAudio
dotnet add package Wagenheimer.MsxAudio.MonoGame   # opcional (MonoGame)
```

---

## Por que existe

O áudio de um jogo de MSX é o que os chips tocam — não uma gravação. Este pacote guarda e reproduz
as **escritas de registrador** (o VGM), que é a fonte lossless: alguns KB por música, loop exato e
timbre idêntico em qualquer máquina, sem codec no meio.

| Problema do caminho "gravar WAV/OGG" | Como o pacote resolve |
| :--- | :--- |
| estalo na emenda do loop (corte por tempo) | loop detectado na sequência de escritas, e no streaming o chip **não reinicia** (loop contínuo) |
| artefato de codec, volume inconsistente | render de chip em 4–8× de sobreamostragem + FIR, nível fiel ao hardware |
| centenas de MB de áudio pronto | VGMs de poucos KB, renderizados em tempo real |
| perder a extração | pacote `.msxaudio` (ZIP) com manifesto + VGMs, versionável |

## Os chips

| Chip | Implementação | O que é emulado |
| :--- | :--- | :--- |
| **PSG AY-3-8910** | porte do **emu2149** (mesmo núcleo do VGMPlay) | 3 canais de onda quadrada, ruído LFSR de 17 bits, 16 formas de envelope, tabela de volume do AY |
| **Konami SCC (K051649)** | porte do núcleo do **MAME/libvgm** | 5 canais de wavetable de 32 bytes, acumulador de fase 16.16, key-on em `0x8F`, wave do canal 5 compartilhada com o 4 |

Clocks do MSX: PSG **1.789.772,5 Hz** (divisor 16) e SCC **1.789.772,5 Hz** no VGM
(`f = clock / (16 × (período + 1))`, o que equivale a `3579545 / (32 × (período + 1))`).

## Formato `.msxaudio`

ZIP com manifesto + VGMs:

```
manifest.json
songs/stage_a.vgm
clips/jump.vgm
```

`manifest.json` traz título, origem, clocks e, por música, `loop`, `loopStart`, `loopSeconds`,
`seconds` e `originalId` (o id da rotina de som da ROM).

```csharp
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Mixing;

using var library = MsxAudioLibrary.Load("Content/Audio/kingsvalley.msxaudio");
using var engine = new MsxAudioEngine(library, AudioProfile.Original);
engine.PlaySong("stage_a");
engine.PlaySample("jump");
```

## Motor de reprodução

- **Música em streaming**: o VGM é consumido enquanto toca; o loop é contínuo (o estado dos chips
  nunca é reiniciado). Cada faixa ocupa só o VGM — sem PCM pré-renderizado.
- **Player de sample**: efeitos como PCM, com volume, pan e polifonia (`MaxSampleVoices`, rouba o
  mais antigo). Um efeito guardado como VGM curto é renderizado uma vez e cacheado.
- **Barramentos** de volume: `MusicVolume`, `SampleVolume`, `MasterVolume`.
- `Render(float[] destino, int frames)` entrega **estéreo intercalado** pronto para a placa.

### MonoGame

```csharp
using Wagenheimer.MsxAudio.MonoGame;

var player = new MsxAudioPlayer(engine);   // usa DynamicSoundEffectInstance (streaming)
player.Start();
player.PlaySong("menu");
player.PlaySample("password_key");

// no Update():
player.Update();                           // mantém a fila de buffers cheia
```

## Extrair de uma ROM

O extrator dirige o **openMSX** (o emulador faz o trabalho sujo: VDP, CPU, mapper, SCC) e injeta
`CALL <rotina de som>` com cada id, gravando o VGM. O que muda de jogo para jogo é descrito num
**perfil JSON** (como chegar numa tela silenciosa, o endereço da rotina e os ids) — nada de
recompilar.

```bash
MsxAudioTool extract "King's Valley II (JP)[RC-760].rom" kingsvalley.msxaudio \
    --profile profiles/kingsvalley2.json --openmsx tools/omsx/openmsx.exe
```

`profiles/kingsvalley2.json` (exemplo real, Konami 1988):

```json
{
  "name": "King's Valley II",
  "machine": "C-BIOS_MSX2_JP",
  "soundCallAddress": 17198,
  "stopId": 1,
  "ids": [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30],
  "musicIds": [3,5,6,7,8,9,10,13,14,17,18],
  "secondsPerId": 12,
  "secondsPerSong": 120,
  "secondsById": { "9": 300 },
  "firstCaptureAt": 23,
  "navigation": [
    { "at": 12, "row": 8, "mask": 1 },
    { "at": 16, "row": 8, "mask": 1 },
    { "at": 19, "row": 8, "mask": 64 },
    { "at": 20.5, "row": 8, "mask": 1 }
  ]
}
```

> Songs with long loops need longer captures (KV2's `stage_e` only reveals its 67.287 s loop with
> 300 s of recording). Use `secondsById` for that.

### Direct extraction, no external emulator (experimental)

`extract-direct` runs the **cartridge's own sound driver in process**: a minimal MSX1 machine
(Z80 core + Konami SCC mapper + 16 KB RAM + PSG/SCC capture + BIOS entry stubs for `RST 30h`,
`WRTPSG` and `RDPSG`) boots the ROM, installs the interrupt hook and records the register writes
— no openMSX, no window.

```bash
MsxAudioTool extract-direct "rom.msx" game.msxaudio --profile profiles/kingsvalley2.json
```

Status: **works** (it boots King's Valley II, runs the driver, captures ~97k writes in 100 s and
renders audio), but the VDP vblank flag/status is not modelled yet, so the driver runs ~6% off the
hardware tempo and the loop is not detected. The `extract` path (openMSX) remains the bit-exact
reference.

## CLI

```bash
MsxAudioTool import <pasta-vgm> <saida.msxaudio> [--title T] [--clips]
MsxAudioTool info <pacote.msxaudio>
MsxAudioTool extract <rom> <saida.msxaudio> --profile perfil.json [--openmsx caminho]
MsxAudioTool render <pacote.msxaudio> <id|all> <saida.wav> [--profile original|stereo|remaster]
MsxAudioTool verify <vgm> <captura.wav>
MsxAudioTool unpack <pacote.msxaudio> <pasta>
```

## Perfis de mixagem

| Perfil | O que muda |
| :--- | :--- |
| `original` | fiel: os 8 canais no centro, só o corte de DC |
| `stereo` | PSG em −30/0/+30% e SCC em −55/−25/0/+25/+55% + passa-baixa de 16 kHz |
| `remaster` | sobreamostragem 8×, pan mais aberto, passa-baixa 17 kHz, reverb curto, normalização de pico |

## Validação

Contra a captura do openMSX do mesmo VGM:

| Medida | Resultado |
| :--- | :--- |
| LTAS (forma do espectro médio) | correlação **0,998** |
| Envelope de amplitude | correlação **0,95** (offset constante ≈ latência do `record`) |
| Nível | diferença de **0,3 dB** (calibrado em `AudioProfile.DefaultMasterGain`) |
| Emenda do loop (render de uma volta) | salto **0,025** contra delta típico **0,047** |

A correlação amostra a amostra não serve como métrica: o `record` do openMSX reamostra e atrasa, e
qualquer fração de milissegundo desalinha uma onda quadrada.

## Estrutura do repositório

```
src/Wagenheimer.MsxAudio/             núcleo (C# puro)
  Chips/       Ay8910, KonamiScc
  Vgm/         VgmFile, VgmWriter, VgmLoopFinder
  Mixing/      AudioProfile, MsxSoundRenderer, FirDecimator
  Songs/       MsxAudioLibrary (.msxaudio)
  Playback/    MsxAudioEngine, WavDecoder
  Extraction/  VgmImporter, OpenMsxRomExtractor (+ perfis)
src/Wagenheimer.MsxAudio.MonoGame/    MsxAudioPlayer (DynamicSoundEffectInstance)
tools/MsxAudioTool/                   CLI
tests/Wagenheimer.MsxAudio.Tests/     testes (chips, VGM, pacote, motor)
```

## Status

| Area | State |
| :--- | :--- |
| PSG + SCC emulation | ✅ validated against openMSX (LTAS correlation 0.998) |
| VGM import/export, loop detection | ✅ |
| `.msxaudio` package | ✅ |
| Streaming engine + sample player | ✅ (parity test with the offline renderer) |
| MonoGame bridge | ✅ |
| ROM extraction via openMSX | ✅ (bit-exact reference path) |
| ROM extraction in process | 🧪 experimental (tempo ~6% off; VDP vblank not modelled) |
| Remaster profile (stereo/reverb) | ✅ |

### Roadmap

1. Model the VDP vblank flag/status and the BIOS interrupt chain so `extract-direct` matches the
   hardware cadence (then loop detection works and no external emulator is needed).
2. A `play` CLI command that streams through `MsxAudioEngine` (live preview).
3. Optional `SoundEffect`-based player for projects that cannot use `DynamicSoundEffectInstance`.

## Tests

```bash
dotnet test MsxAudio.slnx
```

Covers chip frequencies (PSG/SCC formulas), VGM parse/write round-trip, loop detection, package
IO, the streaming engine (loop continuity, sample voices, volume), streaming vs offline parity, the
built-in MSX machine (mapper, PSG/SCC capture, id pass-through) and WAV decoding.

## Aviso

O jogo, a arte e as músicas de MSX pertencem aos seus autores (a Konami, no caso do King's Valley).
Este pacote **não** distribui ROMs nem dados extraídos: ele emula os chips e lê VGMs/pacotes que
você gerar a partir da sua própria ROM. Use somente com material que você tem direito de usar.
