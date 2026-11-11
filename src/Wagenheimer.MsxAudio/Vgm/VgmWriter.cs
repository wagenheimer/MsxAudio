using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wagenheimer.MsxAudio.Vgm;

/// <summary>Escreve um <see cref="VgmFile"/> de volta no formato VGM (bytes).</summary>
/// <remarks>
/// Usado para empacotar músicas extraídas de outras fontes (por exemplo, um driver emulado) no
/// mesmo formato que o openMSX gera — assim tudo (loop, render, comparação) usa um caminho só.
/// </remarks>
public static class VgmWriter
{
    /// <summary>Serializa o VGM (cabeçalho 1.51 + comandos + fim).</summary>
    public static byte[] Write(VgmFile vgm, int totalSamples = 0, double? loopStartSeconds = null, double? loopSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(vgm);

        using var body = new MemoryStream();
        long current = 0;

        foreach (VgmEvent e in vgm.Events)
        {
            long delta = e.Sample - current;
            while (delta > 0)
            {
                int step = (int)Math.Min(delta, 0xFFFF);
                body.WriteByte(0x61);
                body.WriteByte((byte)(step & 0xFF));
                body.WriteByte((byte)((step >> 8) & 0xFF));
                delta -= step;
            }

            current = e.Sample;
            if (e.Kind == VgmEventKind.Psg)
            {
                body.WriteByte(0xA0);
                body.WriteByte(e.P0);
                body.WriteByte(e.P1);
            }
            else
            {
                body.WriteByte(0xD2);
                body.WriteByte(e.P0);
                body.WriteByte(e.P1);
                body.WriteByte(e.P2);
            }
        }

        body.WriteByte(0x66);

        long loopSample = loopStartSeconds is { } start ? (long)Math.Round(start * 44100.0) : -1;
        long loopLength = loopSeconds is { } length ? (long)Math.Round(length * 44100.0) : 0;

        const int headerSize = 0x100;
        byte[] bodyBytes = body.ToArray();
        var data = new byte[headerSize + bodyBytes.Length];
        data[0] = (byte)'V';
        data[1] = (byte)'g';
        data[2] = (byte)'m';
        data[3] = (byte)' ';
        WriteU32(data, 0x08, 0x161);
        WriteU32(data, 0x18, (uint)(totalSamples > 0 ? totalSamples : current));
        WriteU32(data, 0x34, headerSize - 0x34);
        WriteU32(data, 0x74, (uint)Math.Round(vgm.AyClock));

        // O loop NÃO vai no cabeçalho: o VGM só guarda um único offset de loop por arquivo e o
        // nosso loop é por música (fica no manifesto do pacote .msxaudio). Deixar 0 é o correto.
        _ = loopSample;
        _ = loopLength;

        Buffer.BlockCopy(bodyBytes, 0, data, headerSize, bodyBytes.Length);
        return data;
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
