using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Minimal animated GIF encoder (GIF89a) with a fixed palette, so the visualizer can export a GIF
/// without any external tool.
/// </summary>
/// <remarks>
/// Palette: a 6x6x6 RGB cube (216 colours) plus a 40-step grey ramp for the dark UI/text. The LZW
/// encoder is the classic GIF variant (8-bit codes, clear/end codes, 255-byte sub-blocks).
/// </remarks>
public sealed class GifEncoder : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _delayCs;
    private readonly Stream _stream;
    private bool _headerWritten;

    private readonly byte[] _palette = new byte[256 * 3];

    public GifEncoder(string path, int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _delayCs = Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, fps)));
        _stream = File.Create(path);
        BuildPalette();
    }

    private void BuildPalette()
    {
        int i = 0;
        for (int r = 0; r < 6; r++)
        for (int g = 0; g < 6; g++)
        for (int b = 0; b < 6; b++)
        {
            _palette[i * 3] = (byte)(r * 255 / 5);
            _palette[i * 3 + 1] = (byte)(g * 255 / 5);
            _palette[i * 3 + 2] = (byte)(b * 255 / 5);
            i++;
        }

        for (int s = 0; s < 40; s++)
        {
            byte v = (byte)(s * 255 / 39);
            _palette[i * 3] = v;
            _palette[i * 3 + 1] = v;
            _palette[i * 3 + 2] = v;
            i++;
        }
    }

    private static byte PaletteIndex(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));

        if (max - min < 24)
        {
            int luma = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
            return (byte)(216 + Math.Min(39, luma * 39 / 255));
        }

        int r = Math.Min(5, c.R * 5 / 255);
        int g = Math.Min(5, c.G * 5 / 255);
        int b = Math.Min(5, c.B * 5 / 255);
        return (byte)(r * 36 + g * 6 + b);
    }

    private void WriteHeader()
    {
        if (_headerWritten)
            return;

        _headerWritten = true;

        Write("GIF89a");
        Write((ushort)_width);
        Write((ushort)_height);
        _stream.WriteByte(0xF7); // global table, 8-bit colour resolution, 256 entries
        _stream.WriteByte(0);
        _stream.WriteByte(0);
        _stream.Write(_palette, 0, _palette.Length);

        // NETSCAPE2.0 — loop forever.
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xFF);
        _stream.WriteByte(11);
        Write("NETSCAPE2.0");
        _stream.WriteByte(3);
        _stream.WriteByte(1);
        Write((ushort)0);
        _stream.WriteByte(0);
    }

    /// <summary>Adds one frame. <paramref name="pixels"/> must be <c>width*height</c> long.</summary>
    public void AddFrame(Color[] pixels, int width, int height)
    {
        WriteHeader();

        if (pixels.Length < width * height)
            throw new ArgumentException("pixels smaller than width*height", nameof(pixels));

        // Graphic control extension (delay + restore to background).
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xF9);
        _stream.WriteByte(4);
        _stream.WriteByte(0x04);
        Write((ushort)_delayCs);
        _stream.WriteByte(0);
        _stream.WriteByte(0);

        // Image descriptor.
        _stream.WriteByte(0x2C);
        Write((ushort)0);
        Write((ushort)0);
        Write((ushort)width);
        Write((ushort)height);
        _stream.WriteByte(0);

        var indices = new byte[width * height];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = PaletteIndex(pixels[i]);

        WriteLzw(indices);
    }

    private void WriteLzw(byte[] indices)
    {
        _stream.WriteByte(8); // minimum code size

        var clear = 256;
        var end = 257;
        int next = 258;
        int codeSize = 9;

        var dictionary = new Dictionary<int, int>(4096);
        var output = new List<byte>(indices.Length / 2);
        int bitBuffer = 0, bitCount = 0;

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                output.Add((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        Emit(clear);

        int prefix = indices.Length > 0 ? indices[0] : 0;
        for (int i = 1; i < indices.Length; i++)
        {
            int suffix = indices[i];
            int key = (prefix << 8) | suffix;

            if (dictionary.TryGetValue(key, out int existing))
            {
                prefix = existing;
                continue;
            }

            Emit(prefix);

            if (next < 4096)
            {
                dictionary[key] = next++;
                if (next > (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }
            else
            {
                Emit(clear);
                dictionary.Clear();
                next = 258;
                codeSize = 9;
            }

            prefix = suffix;
        }

        Emit(prefix);
        Emit(end);

        if (bitCount > 0)
            output.Add((byte)(bitBuffer & 0xFF));

        // Sub-blocks of at most 255 bytes.
        for (int i = 0; i < output.Count; i += 255)
        {
            int count = Math.Min(255, output.Count - i);
            _stream.WriteByte((byte)count);
            _stream.Write(output.GetRange(i, count).ToArray(), 0, count);
        }

        _stream.WriteByte(0);
    }

    private void Write(string ascii)
    {
        foreach (char c in ascii)
            _stream.WriteByte((byte)c);
    }

    private void Write(ushort value)
    {
        _stream.WriteByte((byte)(value & 0xFF));
        _stream.WriteByte((byte)(value >> 8));
    }

    public void Save()
    {
        WriteHeader();
        _stream.WriteByte(0x3B);
        _stream.Flush();
        _stream.Dispose();
    }

    public void Dispose() => _stream.Dispose();
}
