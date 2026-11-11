using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Sets the SDL window icon at runtime. The embedded <c>.ico</c> brands the executable (Explorer,
/// shortcuts); the title bar and the running taskbar entry use the window icon instead, so it has to
/// be pushed to SDL as well. Best effort: any failure is logged and ignored.
/// </summary>
internal static class WindowIcon
{
    private const string Sdl = "SDL2";

    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowIcon(IntPtr window, IntPtr icon);

    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_CreateRGBSurfaceFrom(
        IntPtr pixels, int width, int height, int depth, int pitch,
        uint rMask, uint gMask, uint bMask, uint aMask);

    [DllImport(Sdl, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_FreeSurface(IntPtr surface);

    public static void TryApply(GameWindow window, GraphicsDevice device, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            using FileStream stream = File.OpenRead(path);
            using var texture = Texture2D.FromStream(device, stream);

            var colors = new Color[texture.Width * texture.Height];
            texture.GetData(colors);

            // SDL's ARGB8888 layout: on little-endian the bytes are B, G, R, A.
            var pixels = new byte[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++)
            {
                Color color = colors[i];
                pixels[i * 4 + 0] = color.B;
                pixels[i * 4 + 1] = color.G;
                pixels[i * 4 + 2] = color.R;
                pixels[i * 4 + 3] = color.A;
            }

            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                IntPtr surface = SDL_CreateRGBSurfaceFrom(
                    handle.AddrOfPinnedObject(), texture.Width, texture.Height, 32, texture.Width * 4,
                    0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);

                if (surface == IntPtr.Zero)
                    return;

                SDL_SetWindowIcon(window.Handle, surface);
                SDL_FreeSurface(surface);
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MSXScope] window icon not set: {ex.Message}");
        }
    }
}
