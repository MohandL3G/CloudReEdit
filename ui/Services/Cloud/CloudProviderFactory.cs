namespace CloudReEdit.Services.Cloud;

public static class CloudProviderFactory
{
    /// <summary>Builds a provider for the configured cloud. Returns an error
    /// string (and null provider) when the config can't be used.</summary>
    public static (ICloudProvider? Provider, string? Error) Create(CloudConfig config)
    {
        switch (config.Provider)
        {
            case "folder" when !string.IsNullOrEmpty(config.SyncPath):
                return (new FolderProvider(config.SyncPath!), null);

            case "s3":
            case "r2":
            {
                var tokenPath = CloudCredentials.ResolveTokenPath(config.TokenPath, config.Provider);
                if (tokenPath == null)
                    return (null, $"{config.DisplayName} credentials not found. Configure CloudRedirect first.");
                var creds = S3Credentials.Read(tokenPath);
                if (creds == null)
                    return (null, $"{config.DisplayName} credentials could not be read or are missing required fields.");
                try
                {
                    return (new S3Provider(creds), null);
                }
                catch (Exception ex)
                {
                    return (null, ex.Message);
                }
            }

            case "gdrive":
                return (new UnsupportedProvider(config.DisplayName), null);

            case "onedrive":
                return (new UnsupportedProvider(config.DisplayName), null);

            case "local":
                return (new UnsupportedProvider(config.DisplayName), null);

            default:
                return (null, $"Unknown provider: {config.Provider}");
        }
    }
}
