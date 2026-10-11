using System;
using System.Reflection;

namespace CloudReEdit.Services;

/// <summary>Reads the app version from the assembly so the UI, the logs and the
/// release tag can never drift apart. No version string is hardcoded anywhere;
/// the value comes from <c>ui/CloudReEdit.csproj</c>'s <c>&lt;Version&gt;</c>.</summary>
public static class AppVersion
{
    /// <summary>The version in tag form, e.g. <c>0.2.3</c>. Falls back to an empty
    /// string rather than throwing.</summary>
    public static string Display()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                        ?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                var buildMetadata = informational.IndexOf('+');
                return buildMetadata >= 0 ? informational.Substring(0, buildMetadata) : informational;
            }

            var version = asm.GetName().Version;
            return version == null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "";
        }
    }
}
