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
        // On Windows the native clipboard also understands a file copied in Explorer (Ctrl+C on the
        // file), which SDL does not, so it goes first.
        if (OperatingSystem.IsWindows() && WindowsClipboard.TryRead() is { Length: > 0 } native)
            return native;

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

    /// <summary>Win32 clipboard: Unicode text, or the first file of a copied file list.</summary>
    private static class WindowsClipboard
    {
        private const uint CfUnicodeText = 13;
        private const uint CfHdrop = 15;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr drop, uint index, char[]? buffer, uint size);

        public static string? TryRead()
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return null;

                try
                {
                    if (IsClipboardFormatAvailable(CfHdrop))
                    {
                        IntPtr drop = GetClipboardData(CfHdrop);
                        if (drop != IntPtr.Zero)
                        {
                            uint length = DragQueryFileW(drop, 0, null, 0);
                            if (length > 0)
                            {
                                var buffer = new char[length + 1];
                                DragQueryFileW(drop, 0, buffer, length + 1);
                                return new string(buffer, 0, (int)length);
                            }
                        }
                    }

                    if (!IsClipboardFormatAvailable(CfUnicodeText))
                        return null;

                    IntPtr handle = GetClipboardData(CfUnicodeText);
                    if (handle == IntPtr.Zero)
                        return null;

                    IntPtr locked = GlobalLock(handle);
                    if (locked == IntPtr.Zero)
                        return null;

                    try
                    {
                        return Marshal.PtrToStringUni(locked);
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch (Exception)
            {
                return null;
            }
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
