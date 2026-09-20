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

    public static (ProviderReadiness State, string? DisplayName, string? Error) Classify(
        bool configFileExists, CloudConfig? config)
    {
        if (!configFileExists)
            return (ProviderReadiness.MissingConfig, null, null);

        if (config == null)
            return (ProviderReadiness.UnusableConfig, null, null);

        var (provider, error) = CloudProviderFactory.Create(config);
        try
        {
            if (provider == null)
            {
                var message = config.Provider == "folder" && string.IsNullOrEmpty(config.SyncPath)
                    ? "Folder sync path is not configured."
                    : error;
                return (ProviderReadiness.Error, config.DisplayName, message);
            }

            return provider.CanWrite
                ? (ProviderReadiness.Ready, provider.DisplayName, null)
                : (ProviderReadiness.ReadOnly, provider.DisplayName, null);
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