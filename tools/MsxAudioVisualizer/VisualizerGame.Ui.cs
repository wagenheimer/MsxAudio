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
    private void Rect(int x, int y, int w, int h, Color color) =>
        _batch.Draw(_pixel, new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)), color);

    private void Rect(Rectangle r, Color color) =>
        _batch.Draw(_pixel, r, color);

    private void Panel(int x, int y, int w, int h, Color? fill = null)
    {
        Rect(x, y, w, h, fill ?? PanelColor);
        Rect(x, y, w, 1, Grid);
        Rect(x, y + h - 1, w, 1, Grid);
        Rect(x, y, 1, h, Grid);
        Rect(x + w - 1, y, 1, h, Grid);
    }

    private void Text(string text, int x, int y, Color color, int scale = 1) =>
        PixelFont.Draw(_batch, _pixel, text, x, y, color, scale);

    private void DrawHeader()
    {
        Rect(0, 0, VW, 46, PanelHi);
        Rect(0, 45, VW, 1, Grid);

        Text("MSX AUDIO VISUALIZER", 16, 12, Accent, 2);
        Text("PSG AY-3-8910 + KONAMI SCC", 16, 30, Dim);

        if (_player is not null)
        {
            string name = Path.GetFileNameWithoutExtension(_player.Name).ToUpperInvariant();
            Text(name, 360, 16, TextColor, 2);

            string time = $"{FormatTime(_player.Time)} / {FormatTime(_player.TotalSeconds)}";
            Text(time, VW - 16 - PixelFont.Measure(time), 12, TextColor, 2);

            string info = $"PSG {(int)(_player.Song.AyClock / 1000)}KHZ  SCC {(int)(1789772.5 / 1000)}KHZ  OUT 48KHZ  X{_player.Oversample}  {_player.Profile.Name.ToUpperInvariant()}";
            Text(info, VW - 16 - PixelFont.Measure(info), 30, Dim);
        }
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int total = (int)seconds;
        return $"{total / 60:00}:{total % 60:00}";
    }

    private void DrawSeekBar()
    {
        const int x = 336, y = 52, w = VW - 348, h = 44;
        Panel(x, y, w, h);
        Text("POSITION", x + 10, y + 6, Accent);

        int barX = x + 10, barY = y + 24, barW = w - 20, barH = 10;
        _seekBar = new Rectangle(barX, barY - 4, barW, barH + 8);

        Rect(barX, barY, barW, barH, Grid);

        if (_player is null)
            return;

        double total = Math.Max(0.001, _player.TotalSeconds);
        double time = Math.Clamp(_player.Time, 0, total);
        int filled = (int)(barW * time / total);

        Rect(barX, barY, filled, barH, PsgColor);

        // Loop window (start..end) when the VGM exposes it.
        var song = _player.Song;
        if (song.HasLoop)
        {
            int lx = barX + (int)(barW * (song.LoopStartSeconds / total));
            int lw = Math.Max(2, (int)(barW * (song.LoopSeconds / total)));
            Rect(lx, barY - 3, lw, 3, Accent);
        }

        // Marker.
        int mx = barX + filled;
        Rect(mx - 1, barY - 4, 3, barH + 8, Accent);

        string text = $"{FormatTime(time)} / {FormatTime(total)}";
        Text(text, x + w - 10 - PixelFont.Measure(text), y + 6, TextColor);
    }

    private void CycleView()
    {
        _view = _view switch
        {
            PanelView.Spectrum => PanelView.Roll,
            PanelView.Roll => PanelView.Piano,
            _ => PanelView.Spectrum,
        };

        if (_view != PanelView.Piano)
        {
            StopPianoHold();

            if (_demoRunning)
            {
                _demoRunning = false;
                _demoChannel = -1;
                _player?.StopAudition();
            }
        }

        _status = ViewName(_view);
        _statusTimer = 2f;
    }

    private static string ViewName(PanelView view) => view switch
    {
        PanelView.Roll => "PIANO ROLL",
        PanelView.Piano => "PIANO",
        _ => "SPECTRUM",
    };

    /// <summary>Label of the view that the header button switches to.</summary>
    private string NextViewLabel() => ViewName(_view switch
    {
        PanelView.Spectrum => PanelView.Roll,
        PanelView.Roll => PanelView.Piano,
        _ => PanelView.Spectrum,
    });

    private void DrawViewToggle(int x, int y, string label)
    {
        int w = PixelFont.Measure(label) + 12;
        _viewToggleHit = new Rectangle(x, y, w, 16);
        DrawButton(_viewToggleHit, label, false, Accent);
        Text(label, x + 6, y + 5, Dim);
    }

    private void DrawButton(Rectangle rect, string label, bool active, Color activeColor)
    {
        Rect(rect, active ? activeColor : Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, active ? PanelHi : PanelColor);

        int tw = Math.Max(0, PixelFont.Measure(label) - 1);
        Text(label, rect.X + (rect.Width - tw) / 2, rect.Y + (rect.Height - 7) / 2,
            active ? activeColor : Dim);
    }

    private void DrawFooter()
    {
        // Kept away from the very bottom edge: a maximised window can have the taskbar over the
        // last pixels, and the help line must stay readable.
        Rect(0, VH - 56, VW, 44, PanelHi);
        Rect(0, VH - 56, VW, 1, Grid);

        var p = _player;
        string chipState = p is null ? "" : $"PSG[{(p.PsgEnabled ? "ON" : "OFF")}] SCC[{(p.SccEnabled ? "ON" : "OFF")}]{(p.SoloChannel is int s ? "  SOLO " + ChannelName(s) : "")}";
        Text(chipState, 16, VH - 50, TextColor);

        Text("TAB/HEADER VIEW   UP/DOWN TRACK   ENTER PLAY   SPACE PAUSE   LEFT/RIGHT SEEK   CLICK: TRACK / BAR / S-M", 16, VH - 36, Dim);
        Text("P PSG   S SCC   1-8 CHANNEL   O PROFILE   L AUTONEXT   R RESTART   H HELP   ESC QUIT", 16, VH - 22, Dim);

        if (_statusTimer > 0)
        {
            int w = PixelFont.Measure(_status, 2);
            Text(_status, VW - 16 - w, VH - 82, Accent, 2);
        }
    }

    private void DrawHelp()
    {
        const int w = 660, h = 356;
        int x = (VW - w) / 2, y = (VH - h) / 2;
        Panel(x, y, w, h, PanelHi);
        Rect(x, y, w, 24, PanelColor);
        Text("HELP", x + 10, y + 8, Accent);

        string[] lines =
        {
            "TAB / HEADER   SPECTRUM / PIANO ROLL / PIANO",
            "UP / DOWN      SELECT TRACK",
            "ENTER          PLAY SELECTED",
            "SPACE          PAUSE / RESUME",
            "LEFT / RIGHT   SEEK -/+ 5 S",
            "CLICK BAR      SEEK TO POSITION",
            "CLICK MAP      JUMP ANYWHERE IN THE SONG",
            "CLICK LABEL    SOLO THAT CHANNEL",
            "P / S          TOGGLE PSG / SCC",
            "1 2 3          PSG CHANNELS",
            "4 5 6 7 8      SCC CHANNELS",
            "O              AUDIO PROFILE",
            "L              AUTO NEXT",
            "R              RESTART",
            "H              THIS HELP",
            "ESC            QUIT",
            "",
            "PIANO: CLICK AND HOLD A KEY TO PLAY THE SELECTED",
            "CHANNEL'S INSTRUMENT (WORKS WHILE PAUSED); RELEASE",
            "TO STOP. DRAG ACROSS THE KEYS TO SLIDE. 'PLAY ALL'",
            "WALKS THE AUDIBLE CHANNELS; '<' / '>' CHANGE OCTAVE.",
            "",
            "DRAG A .VGM FILE INTO THE WINDOW TO PLAY IT",
            "MSXAUDIOVISUALIZER SONG.VGM --RECORD OUT.GIF --SECONDS 10",
        };

        for (int i = 0; i < lines.Length; i++)
            Text(lines[i], x + 16, y + 36 + i * 14, i >= lines.Length - 2 ? Dim : TextColor);
    }
}
