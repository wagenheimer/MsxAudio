using System;
using System.Collections.Generic;
using System.IO;

namespace Wagenheimer.MsxAudio.Vgm;

/// <summary>Tipo de escrita de chip guardada num evento de VGM.</summary>
public enum VgmEventKind : byte
{
    /// <summary>Escrita no PSG (AY-3-8910): <c>P0</c> = registrador, <c>P1</c> = valor.</summary>
    Psg = 0,

    /// <summary>Escrita no SCC: <c>P0</c> = porta, <c>P1</c> = registrador, <c>P2</c> = valor.</summary>
    Scc = 1,
}

/// <summary>Uma escrita de registrador de chip com o instante em que aconteceu.</summary>
public readonly struct VgmEvent
{
    /// <summary>Cria o evento.</summary>
    public VgmEvent(long sample, VgmEventKind kind, byte p0, byte p1, byte p2)
    {
        Sample = sample;
        Kind = kind;
        P0 = p0;
        P1 = p1;
        P2 = p2;
    }

    /// <summary>Instante em amostras de 44100 Hz (base de tempo do VGM).</summary>
    public long Sample { get; }

    /// <summary>Chip que recebeu a escrita.</summary>
    public VgmEventKind Kind { get; }

    /// <summary>Registrador (PSG) ou porta (SCC).</summary>
    public byte P0 { get; }

    /// <summary>Valor (PSG) ou registrador (SCC).</summary>
    public byte P1 { get; }

    /// <summary>Valor (SCC) â€” ignorado no PSG.</summary>
    public byte P2 { get; }
}

/// <summary>
/// Leitor de arquivos VGM (Video Game Music) restrito ao que o MSX usa: escritas no PSG
/// (<c>0xA0</c>) e no SCC (<c>0xD2</c>) com os comandos de espera.
/// </summary>
/// <remarks>
/// O VGM Ã© o "log exato" que o openMSX grava das escritas de registrador: Ã© a fonte da verdade
/// lossless do som do original. Aqui ele Ã© lido para uma lista de eventos; quem transforma em
/// Ã¡udio Ã© o <see cref="Mixing.MsxSoundRenderer"/>.
/// </remarks>
public sealed class VgmFile
{
    private VgmFile(string name, double ayClock, long totalSamples, VgmEvent[] events)
    {
        Name = name;
        AyClock = ayClock;
        TotalSamples = totalSamples;
        Events = events;
    }

    /// <summary>Nome do arquivo (sem extensÃ£o).</summary>
    public string Name { get; }

    /// <summary>Clock do PSG em Hz (do cabeÃ§alho; padrÃ£o MSX 1789772,5).</summary>
    public double AyClock { get; }

    /// <summary>DuraÃ§Ã£o total capturada, em amostras de 44100 Hz.</summary>
    public long TotalSamples { get; private set; }

    /// <summary>Escritas de registrador, em ordem de tempo.</summary>
    public IReadOnlyList<VgmEvent> Events { get; }

    /// <summary>InÃ­cio do loop em amostras de 44100 Hz (-1 = sem loop conhecido).</summary>
    public long LoopSample { get; private set; } = -1;

    /// <summary>DuraÃ§Ã£o do loop em amostras de 44100 Hz (0 = sem loop conhecido).</summary>
    public long LoopDuration { get; private set; }

    /// <summary>Tem loop definido?</summary>
    public bool HasLoop => LoopSample >= 0 && LoopDuration > 0;

    /// <summary>InÃ­cio do loop em segundos.</summary>
    public double LoopStartSeconds => LoopSample / 44100.0;

    /// <summary>DuraÃ§Ã£o do loop em segundos.</summary>
    public double LoopSeconds => LoopDuration / 44100.0;

    /// <summary>DuraÃ§Ã£o total capturada em segundos.</summary>
    public double TotalSeconds => TotalSamples / 44100.0;

    /// <summary>Define o loop (em segundos) â€” usado pelo detector ou por um manifesto.</summary>
    public void SetLoop(double startSeconds, double durationSeconds)
    {
        LoopSample = (long)Math.Round(startSeconds * 44100.0);
        LoopDuration = (long)Math.Round(durationSeconds * 44100.0);
    }

    /// <summary>LÃª um VGM do disco.</summary>
    public static VgmFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Parse(data, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>LÃª um VGM em memÃ³ria.</summary>
    public static VgmFile Parse(byte[] data, string name = "vgm")
    {
        if (data.Length < 0x40 || data[0] != (byte)'V' || data[1] != (byte)'g' || data[2] != (byte)'m' || data[3] != (byte)' ')
            throw new InvalidDataException($"{name}: nÃ£o Ã© um arquivo VGM.");

        uint version = ReadU32(data, 0x08);
        long total = ReadU32(data, 0x18);

        double ayClock = 0;
        if (version >= 0x151 && data.Length >= 0x78)
            ayClock = ReadU32(data, 0x74);
        if (ayClock <= 0) ayClock = 1789772.5;

        uint eofRelative = ReadU32(data, 0x34);
        int pos = eofRelative != 0 ? 0x34 + (int)eofRelative : 0x40;

        var events = new List<VgmEvent>(data.Length / 8);
        long t = 0;
        while (pos < data.Length)
        {
            byte cmd = data[pos];
            if (cmd == 0x66) break;

            if (cmd == 0xA0)
            {
                events.Add(new VgmEvent(t, VgmEventKind.Psg, data[pos + 1], data[pos + 2], 0));
                pos += 3;
                continue;
            }

            if (cmd == 0xD2)
            {
                events.Add(new VgmEvent(t, VgmEventKind.Scc, data[pos + 1], data[pos + 2], data[pos + 3]));
                pos += 4;
                continue;
            }

            if (cmd == 0x61)
            {
                t += data[pos + 1] | (data[pos + 2] << 8);
                pos += 3;
                continue;
            }

            if (cmd is 0x62 or 0x63)
            {
                t += cmd == 0x62 ? 735 : 882;
                pos += 1;
                continue;
            }

            if (cmd is >= 0x70 and <= 0x7F)
            {
                t += (cmd & 15) + 1;
                pos += 1;
                continue;
            }

            pos += CommandSize(cmd);
        }

        var file = new VgmFile(name, ayClock, total, events.ToArray());
        if (total <= 0 && events.Count > 0)
            file.TotalSamples = t;
        return file;
    }

    /// <summary>Tamanho total (em bytes) de um comando VGM, para pular o que nÃ£o interessa.</summary>
    private static int CommandSize(byte cmd) => cmd switch
    {
        >= 0x30 and <= 0x3F => 2,
        >= 0x40 and <= 0x4E => 3,
        0x4F => 2,
        0x50 => 2,
        >= 0x51 and <= 0x5F => 3,
        >= 0x90 and <= 0x95 => 5,
        >= 0xA0 and <= 0xBF => 3,
        >= 0xC0 and <= 0xDF => 4,
        >= 0xE0 => 5,
        _ => 1,
    };

    private static uint ReadU32(byte[] d, int offset) =>
        (uint)(d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16) | (d[offset + 3] << 24));
}

