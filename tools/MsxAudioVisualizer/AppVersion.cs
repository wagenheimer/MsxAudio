using System;
using System.Reflection;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Application version shown in the window title, the help overlay and <c>--version</c>. MinVer fills
/// <see cref="AssemblyInformationalVersionAttribute"/> from the nearest git tag (e.g. <c>v1.2.3</c>);
/// untagged builds get <c>1.2.4-alpha.0.5</c> and local ones <c>1.0.0-alpha.0.0</c>.
/// </summary>
internal static class AppVersion
{
    public static string Value { get; } = Read();

    private static string Read()
    {
        try
        {
            string? info = typeof(AppVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(info))
                return "dev";

            // Drop the "+<commit>" suffix MinVer appends on untagged commits.
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        catch (Exception)
        {
            return "dev";
        }
    }
}
