using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudReEdit.Services.Cloud;

/// <summary>Parsed + decrypted S3/R2 credentials from s3_credentials.json.</summary>
public sealed class S3Credentials
{
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";

    public static S3Credentials? Read(string path)
    {
        if (!File.Exists(path)) return null;
        string json = CloudCredentials.ReadJson(path);
        if (json == null) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var c = new S3Credentials();
        c.AccessKeyId = GetString(root, "access_key_id");
        c.SecretAccessKey = GetString(root, "secret_access_key");
        c.Bucket = GetString(root, "bucket");
        c.KeyPrefix = GetString(root, "key_prefix");
        c.Endpoint = GetString(root, "endpoint");
        string region = GetString(root, "region");
        if (!string.IsNullOrEmpty(region)) c.Region = region;

        if (string.IsNullOrEmpty(c.AccessKeyId) ||
            string.IsNullOrEmpty(c.SecretAccessKey) ||
            string.IsNullOrEmpty(c.Bucket))
            return null;

        return c;
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) ? (p.GetString() ?? "") : "";
}

public static class CloudCredentials
{
    /// <summary>DPAPI token file I/O, compatible with the DLL's dpapi_util.h.
    /// Plaintext JSON (first byte '{') is returned as-is.</summary>
    public static string? ReadJson(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length == 0) return null;

        if (raw[0] == (byte)'{')
            return Encoding.UTF8.GetString(raw);

        try
        {
            var plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            AppLog.Warn("cloud", $"credential decryption failed: {ex.GetType().Name}");
            return null;
        }
    }

    public static string? ResolveTokenPath(string? tokenPath, string provider)
    {
        if (!string.IsNullOrEmpty(tokenPath) && File.Exists(tokenPath))
            return tokenPath;

        // Fall back to the conventional filename in the config dir.
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudRedirect");
        string fileName = provider switch
        {
            "s3" => "s3_credentials.json",
            "r2" => "s3_credentials.json",
            "gdrive" => "google_tokens.json",
            "onedrive" => "onedrive_tokens.json",
            _ => "s3_credentials.json"
        };
        var candidate = Path.Combine(configDir, fileName);
        return File.Exists(candidate) ? candidate : null;
    }
}
