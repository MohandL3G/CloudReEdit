using System;
using System.Reflection;

namespace CloudReEdit.Services;

/// <summary>Reads the app version from the assembly so the UI, the logs and the
/// release tag can never drift apart. No version string is hardcoded anywhere;
/// the value comes from <c>ui/CloudReEdit.csproj</c>'s <c>&lt;VersionPrefix&gt;</c>
/// and <c>&lt;VersionSuffix&gt;</c> (e.g. <c>0.2.7-pre-release</c>).</summary>
public static class AppVersion
{
    /// <summary>The version exactly as the app should display it, e.g.
    /// <c>0.2.7-pre-release</c>. Build metadata (<c>+...</c>) added by the build
    /// is stripped. Falls back to an empty string rather than throwing.</summary>
    public static string Display()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                        ?.InformationalVersion;
            var stripped = StripBuildMetadata(informational);
            if (!string.IsNullOrEmpty(stripped))
                return stripped;

            var version = asm.GetName().Version;
            return version == null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Removes <c>+buildMetadata</c> from an informational version, so a
    /// release build shows <c>0.2.7-pre-release</c> instead of
    /// <c>0.2.7-pre-release+&lt;commit&gt;</c>. Returns null for null/empty input.</summary>
    public static string? StripBuildMetadata(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion)) return null;

        var buildMetadata = informationalVersion.IndexOf('+');
        return buildMetadata >= 0
            ? informationalVersion.Substring(0, buildMetadata)
            : informationalVersion;
    }
}
