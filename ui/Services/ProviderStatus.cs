using CloudReEdit.Services.Cloud;

namespace CloudReEdit.Services;

public enum ProviderReadiness
{
    MissingConfig,
    UnusableConfig,
    Ready,
    ReadOnly,
    Error,
}

/// <summary>Pure Accounts-page provider readiness. Classification delegates to
/// CloudProviderFactory (the single source of truth for configuration/credential
/// capability); this maps its result to a user-facing state, distinguishes
/// missing vs unusable CloudRedirect config, and disposes any constructed
/// provider immediately. No network operation is ever performed here.</summary>
public static class ProviderStatus
{
    public const string MissingConfig = "CloudRedirect is not configured.";
    public const string UnusableConfig = "CloudRedirect configuration exists but could not be used.";

    /// <summary>Non-blocking warning shown next to the provider status when the
    /// configured storage endpoint is plain <c>http://</c>. Saving is never
    /// blocked by it, and it never contains the endpoint value itself.</summary>
    public const string InsecureEndpointWarning =
        "The storage endpoint uses plain http:// - requests to it (including signed ones) are sent unencrypted.";

    /// <summary>Readiness of the configured provider, plus whether its endpoint is
    /// plain <c>http://</c> (<paramref name="InsecureEndpoint"/>) so the UI can warn
    /// without blocking.</summary>
    public static (ProviderReadiness State, string? DisplayName, string? Error, bool InsecureEndpoint) Classify(
        bool configFileExists, CloudConfig? config)
    {
        if (!configFileExists)
            return (ProviderReadiness.MissingConfig, null, null, false);

        if (config == null)
            return (ProviderReadiness.UnusableConfig, null, null, false);

        var (provider, error) = CloudProviderFactory.Create(config);
        try
        {
            if (provider == null)
            {
                var message = config.Provider == "folder" && string.IsNullOrEmpty(config.SyncPath)
                    ? "Folder sync path is not configured."
                    : error;
                return (ProviderReadiness.Error, config.DisplayName, message, false);
            }

            bool insecure = provider is S3Provider s3 && s3.UsesInsecureEndpoint;
            return provider.CanWrite
                ? (ProviderReadiness.Ready, provider.DisplayName, null, insecure)
                : (ProviderReadiness.ReadOnly, provider.DisplayName, null, insecure);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    public static string Text(ProviderReadiness state, string? displayName, string? error)
    {
        return state switch
        {
            ProviderReadiness.MissingConfig => MissingConfig,
            ProviderReadiness.UnusableConfig => UnusableConfig,
            ProviderReadiness.Ready => $"Ready — {displayName}",
            ProviderReadiness.ReadOnly => $"{displayName} — read-only in CloudReEdit",
            ProviderReadiness.Error => error ?? "Cloud provider could not be used.",
            _ => "Cloud provider could not be used.",
        };
    }
}