using System.Collections.Generic;

namespace CloudReEdit.Resources;

/// <summary>
/// Minimal string lookup for CloudReEdit. Returns the key itself when a
/// translation is missing so UI never shows blank text.
/// </summary>
public static class S
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["Dialog_OK"] = "OK",
        ["Dialog_Yes"] = "Yes",
        ["Dialog_No"] = "No",
        ["Dialog_Save"] = "Save",
        ["Dialog_Discard"] = "Discard",
        ["Dialog_Cancel"] = "Cancel",
        ["Provider_GoogleDrive"] = "Google Drive",
        ["Provider_OneDrive"] = "OneDrive",
        ["Provider_R2"] = "Cloudflare R2",
        ["Provider_S3"] = "S3",
        ["Provider_FolderNetworkDrive"] = "Folder / Network drive",
        ["Provider_LocalOnly"] = "Local only",
    };

    public static string Get(string key)
        => Map.TryGetValue(key, out var value) ? value : key;

    public static string Format(string key, params object[] args)
        => string.Format(Get(key), args);
}
