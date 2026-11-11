using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Helpers shared by the tests (synthetic VGM, WAV).</summary>
internal static class TestSignals
{
    public const double Clock = 1789772.5;

    public static VgmFile Tone(double totalSeconds, double loopStart, double loopSeconds)
    {
        var commands = new List<byte[]>
        {
            new byte[] { 0xA0, 7, 0x3E },
            new byte[] { 0xA0, 0, 200 },
            new byte[] { 0xA0, 1, 0 },
            new byte[] { 0xA0, 8, 15 },
        };

        int remaining = (int)(totalSeconds * 44100);
        while (remaining > 0)
        {
            // rewrites the frequency every 0.5 s (like a real driver does every frame)
            int step = Math.Min(remaining, Math.Min(22050, 65535));
            commands.Add(new[] { (byte)0xA0, (byte)0, (byte)(200 + (step % 3)) });
            commands.Add(new[] { (byte)0x61, (byte)(step & 0xFF), (byte)((step >> 8) & 0xFF) });
            remaining -= step;
        }

        var vgm = VgmFile.Parse(BuildVgm(commands, (int)(totalSeconds * 44100)), "tone");
        if (loopSeconds > 0) vgm.SetLoop(loopStart, loopSeconds);
        return vgm;
    }

    public static byte[] BuildVgm(List<byte[]> commands, int totalSamples)
    {
        using var stream = new MemoryStream();
        var header = new byte[0x100];
        header[0] = (byte)'V';
        header[1] = (byte)'g';
        header[2] = (byte)'m';
        header[3] = (byte)' ';
        BitConverter.GetBytes(0x161).CopyTo(header, 0x08);
        BitConverter.GetBytes(totalSamples).CopyTo(header, 0x18);
        BitConverter.GetBytes(0x100 - 0x34).CopyTo(header, 0x34);
        BitConverter.GetBytes((uint)Clock).CopyTo(header, 0x74);
        stream.Write(header);
        foreach (var command in commands) stream.Write(command);
        stream.WriteByte(0x66);
        return stream.ToArray();
    }

    public static void WriteWav(string path, RenderedAudio audio)
    {
        byte[] pcm = audio.ToPcmBytes();
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)audio.Channels);
        writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * audio.Channels * 2);
        writer.Write((short)(audio.Channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    public static void WriteWavFloats(string path, float[] samples, int sampleRate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataLength = samples.Length * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (float sample in samples)
            writer.Write((short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue));
    }
}
