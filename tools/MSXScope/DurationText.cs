using System.Globalization;

namespace Wagenheimer.MsxAudio.Scope;

/// <summary>Durations for both the playlist and transport, including short sound effects.</summary>
public static class DurationText
{
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "00:00";
        long total = Math.Max(1, (long)seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{total % 60:00}");
    }
}
