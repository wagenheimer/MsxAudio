using System;
using System.Runtime.InteropServices;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Reads the system clipboard through SDL2 (which MonoGame already loaded), so a path can be pasted
/// into a dialog with Ctrl+V.
/// </summary>
/// <remarks>
/// MonoGame has no clipboard API. SDL exposes <c>SDL_GetClipboardText</c> from the same library the
/// window came from; it is loaded by name (a different one per OS) and every failure is silent,
/// because a missing clipboard must never break typing.
/// </remarks>
internal static class Clipboard
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboardText();

    private static IntPtr _library;
    private static GetClipboardText? _getText;
    private static bool _tried;

    /// <summary>Clipboard text, or <c>null</c> when it is empty or unavailable.</summary>
    public static string? TryReadText()
    {
        try
        {
            if (!EnsureLoaded() || _getText is null)
                return null;

            IntPtr pointer = _getText();
            if (pointer == IntPtr.Zero)
                return null;

            string? text = Marshal.PtrToStringUTF8(pointer);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool EnsureLoaded()
    {
        if (_tried)
            return _getText is not null;

        _tried = true;

        foreach (string name in new[] { "SDL2", "SDL2.dll", "libSDL2-2.0.so.0", "libSDL2-2.0.0.dylib" })
        {
            if (!NativeLibrary.TryLoad(name, out IntPtr handle))
                continue;

            if (!NativeLibrary.TryGetExport(handle, "SDL_GetClipboardText", out IntPtr export))
                continue;

            _library = handle;
            _getText = Marshal.GetDelegateForFunctionPointer<GetClipboardText>(export);
            return true;
        }

        Console.WriteLine("[MSXScope] clipboard unavailable (SDL2 not found)");
        return false;
    }
}
