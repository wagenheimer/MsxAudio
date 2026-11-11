using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

public sealed partial class VisualizerGame : Game
{
    private void TryScreenshot()
    {
        if (_shotDone)
            return;

        _frame++;
        string? path = Environment.GetEnvironmentVariable("MSXAUDIO_SHOT");
        if (string.IsNullOrWhiteSpace(path))
            return;

        int target = int.TryParse(Environment.GetEnvironmentVariable("MSXAUDIO_SHOT_FRAME"), out int f) ? f : 90;
        if (_frame < target)
            return;

        _shotDone = true;
        SavePng(path);
        Exit();
    }

    private Color[] ReadFrame(out int w, out int h)
    {
        w = VW;
        h = VH;
        var data = new Color[VW * VH];
        _target.GetData(data);
        return data;
    }

    private static Color[] Downscale(Color[] src, int sw, int sh, int factor, out int dw, out int dh)
    {
        dw = Math.Max(1, sw / factor);
        dh = Math.Max(1, sh / factor);
        var dst = new Color[dw * dh];
        int n = factor * factor;

        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int r = 0, g = 0, b = 0;
            for (int dy = 0; dy < factor; dy++)
            for (int dx = 0; dx < factor; dx++)
            {
                var c = src[(y * factor + dy) * sw + x * factor + dx];
                r += c.R;
                g += c.G;
                b += c.B;
            }

            dst[y * dw + x] = new Color(r / n, g / n, b / n);
        }

        return dst;
    }

    private void SavePng(string path)
    {
        try
        {
            var data = ReadFrame(out int w, out int h);

            using var texture = new Texture2D(GraphicsDevice, w, h);
            texture.SetData(data);
            using var stream = File.Create(path);
            texture.SaveAsPng(stream, w, h);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] png falhou: {ex.Message}");
        }
    }

    private void CaptureFrame()
    {
        if (_player is null)
        {
            Exit();
            return;
        }

        if (_gif is not null)
        {
            var frame = ReadFrame(out int w, out int h);
            var small = Downscale(frame, w, h, _gifFactor, out int dw, out int dh);
            _gif.AddFrame(small, dw, dh);
        }
        else
        {
            SavePng(Path.Combine(_recordDir, $"frame_{_recordFrame:D5}.png"));
        }

        // Audio for this frame's interval: exactly 1/fps of music, so the WAV matches the video.
        AdvanceAudio(recorded: true);

        _recordFrame++;

        if (_recordFrame >= _recordTotal)
            FinishRecording();
    }

    private void AdvanceAudio(bool recorded)
    {
        if (_player is null)
            return;

        int remaining = Math.Max(1, 48000 / _recordFps);
        while (remaining > 0)
        {
            int chunk = Math.Min(2048, remaining);
            _player.Render(_render, chunk);
            if (recorded)
                _wav?.Write(_render, chunk);
            remaining -= chunk;
        }

        UpdateSpectrum();
        UpdateMeters(1f / _recordFps);
        UpdateNotes();
    }

    private void FinishRecording()
    {
        _wav?.Dispose();
        _wav = null;

        if (_gif is not null)
        {
            _gif.Save();
            _gif.Dispose();
            Console.WriteLine($"[Visualizer] GIF written: {_recordPath} ({_recordFrame} frames)");
            Exit();
            return;
        }

        Console.WriteLine($"[Visualizer] {_recordFrame} frames in {_recordDir}");

        if (_recordPath is not null && TryEncode(_recordPath))
            Console.WriteLine($"[Visualizer] recording written: {_recordPath}");
        else if (_wavPath is not null)
            Console.WriteLine($"[Visualizer] ffmpeg not found: frames in {_recordDir} + audio in {_wavPath}\n" +
                              $"              ffmpeg -framerate {_recordFps} -i \"{Path.Combine(_recordDir, "frame_%05d.png")}\" " +
                              $"-i \"{_wavPath}\" -c:v libx264 -pix_fmt yuv420p -crf 20 -c:a aac -b:a 192k -shortest \"{_recordPath}\"");
        else
            Console.WriteLine("[Visualizer] ffmpeg not found: use the PNG sequence above to build the video");

        Exit();
    }

    private bool TryEncode(string output)
    {
        string input = Path.Combine(_recordDir, "frame_%05d.png");
        string audio = string.Empty;
        if (_wavPath is not null && File.Exists(_wavPath))
            audio = $"-i \"{_wavPath}\" -c:a aac -b:a 192k -shortest ";

        string ext = Path.GetExtension(output).ToLowerInvariant();
        string args = ext switch
        {
            ".gif" =>
                $"-y -framerate {_recordFps} -i \"{input}\" -vf \"fps={Math.Min(30, _recordFps)},scale=960:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -loop 0 \"{output}\"",
            _ =>
                $"-y -framerate {_recordFps} -i \"{input}\" {audio}-c:v libx264 -pix_fmt yuv420p -crf 20 \"{output}\"",
        };

        try
        {
            var info = new ProcessStartInfo("ffmpeg", args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(info);
            if (process is null)
                return false;

            process.WaitForExit(120_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
