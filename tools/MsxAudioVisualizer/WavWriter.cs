using System;
using System.IO;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Minimal 16-bit PCM WAV writer (interleaved), used to capture the audio while the visualizer
/// records a video. The header is patched when the writer is disposed.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly short _channels;
    private readonly int _sampleRate;
    private int _dataBytes;

    public WavWriter(string path, int sampleRate = 48000, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = (short)channels;

        _stream = File.Create(path);
        _writer = new BinaryWriter(_stream);

        _writer.Write("RIFF"u8);
        _writer.Write(0);                       // patched on dispose
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);                      // PCM chunk size
        _writer.Write((short)1);                // PCM
        _writer.Write(_channels);
        _writer.Write(_sampleRate);
        _writer.Write(_sampleRate * _channels * 2); // byte rate
        _writer.Write((short)(_channels * 2));  // block align
        _writer.Write((short)16);               // bits per sample
        _writer.Write("data"u8);
        _writer.Write(0);                       // patched on dispose
    }

    public int Frames => _channels > 0 ? _dataBytes / (_channels * 2) : 0;
    public double Seconds => _sampleRate > 0 ? (double)Frames / _sampleRate : 0;

    /// <summary>Writes <paramref name="frames"/> stereo frames from an interleaved float buffer.</summary>
    public void Write(float[] interleaved, int frames)
    {
        int count = frames * _channels;
        if (interleaved.Length < count)
            throw new ArgumentException("buffer smaller than frames*channels", nameof(interleaved));

        var bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            short v = (short)Math.Clamp((int)Math.Round(interleaved[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        _writer.Write(bytes);
        _dataBytes += bytes.Length;
    }

    public void Dispose()
    {
        try
        {
            _stream.Seek(4, SeekOrigin.Begin);
            _writer.Write(36 + _dataBytes);
            _stream.Seek(40, SeekOrigin.Begin);
            _writer.Write(_dataBytes);
            _writer.Flush();
        }
        catch
        {
            // best effort: the audio was written, only the header sizes may be zero
        }

        _writer.Dispose();
        _stream.Dispose();
    }
}
