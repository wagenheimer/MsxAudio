using System;
using System.Collections.Generic;
using System.IO;

namespace Wagenheimer.MsxAudio.Vgm;

/// <summary>Kind of chip write stored in a VGM event.</summary>
public enum VgmEventKind : byte
{
    /// <summary>PSG (AY-3-8910) write: <c>P0</c> = register, <c>P1</c> = value.</summary>
    Psg = 0,

    /// <summary>SCC write: <c>P0</c> = port, <c>P1</c> = register, <c>P2</c> = value.</summary>
    Scc = 1,
}

/// <summary>A chip register write with the instant it happened.</summary>
public readonly struct VgmEvent
{
    /// <summary>Creates the event.</summary>
    public VgmEvent(long sample, VgmEventKind kind, byte p0, byte p1, byte p2)
    {
        Sample = sample;
        Kind = kind;
        P0 = p0;
        P1 = p1;
        P2 = p2;
    }

    /// <summary>Instant in 44100 Hz samples (the VGM time base).</summary>
    public long Sample { get; }

    /// <summary>Chip that received the write.</summary>
    public VgmEventKind Kind { get; }

    /// <summary>Register (PSG) or port (SCC).</summary>
    public byte P0 { get; }

    /// <summary>Value (PSG) or register (SCC).</summary>
    public byte P1 { get; }

    /// <summary>Value (SCC) — unused for the PSG.</summary>
    public byte P2 { get; }
}

/// <summary>
/// VGM (Video Game Music) reader restricted to what the MSX uses: PSG writes (<c>0xA0</c>) and SCC
/// writes (<c>0xD2</c>) plus the wait commands.
/// </summary>
/// <remarks>
/// The VGM is the exact "log" openMSX records of the register writes: it is the lossless source of
/// truth for the original sound. Here it is read into an event list; the
/// <see cref="Mixing.MsxSoundRenderer"/> turns it into audio.
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

    /// <summary>File name (without extension).</summary>
    public string Name { get; }

    /// <summary>PSG clock in Hz (from the header; MSX default 1789772.5).</summary>
    public double AyClock { get; }

    /// <summary>Total captured duration, in 44100 Hz samples.</summary>
    public long TotalSamples { get; private set; }

    /// <summary>Register writes, in time order.</summary>
    public IReadOnlyList<VgmEvent> Events { get; }

    /// <summary>Loop start in 44100 Hz samples (-1 = no known loop).</summary>
    public long LoopSample { get; private set; } = -1;

    /// <summary>Loop duration in 44100 Hz samples (0 = no known loop).</summary>
    public long LoopDuration { get; private set; }

    /// <summary>Has a defined loop?</summary>
    public bool HasLoop => LoopSample >= 0 && LoopDuration > 0;

    /// <summary>Loop start in seconds.</summary>
    public double LoopStartSeconds => LoopSample / 44100.0;

    /// <summary>Loop duration in seconds.</summary>
    public double LoopSeconds => LoopDuration / 44100.0;

    /// <summary>Total captured duration in seconds.</summary>
    public double TotalSeconds => TotalSamples / 44100.0;

    /// <summary>Sets the loop (in seconds) — used by the detector or by a manifest.</summary>
    public void SetLoop(double startSeconds, double durationSeconds)
    {
        LoopSample = (long)Math.Round(startSeconds * 44100.0);
        LoopDuration = (long)Math.Round(durationSeconds * 44100.0);
    }

    /// <summary>Loads a VGM from disk.</summary>
    public static VgmFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Parse(data, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Creates a VGM from an in-memory register-write stream (e.g. captured from a driver).</summary>
    public static VgmFile FromEvents(
        IEnumerable<VgmEvent> events,
        long totalSamples,
        double ayClock = 1789772.5,
        string name = "capture")
    {
        ArgumentNullException.ThrowIfNull(events);

        var list = new List<VgmEvent>(events);
        return new VgmFile(name, ayClock, totalSamples, list.ToArray());
    }

    /// <summary>Parses a VGM from memory.</summary>
    public static VgmFile Parse(byte[] data, string name = "vgm")
    {
        if (data.Length < 0x40 || data[0] != (byte)'V' || data[1] != (byte)'g' || data[2] != (byte)'m' || data[3] != (byte)' ')
            throw new InvalidDataException($"{name}: not a VGM file.");

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

    /// <summary>Total size (in bytes) of a VGM command, to skip what we do not care about.</summary>
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
