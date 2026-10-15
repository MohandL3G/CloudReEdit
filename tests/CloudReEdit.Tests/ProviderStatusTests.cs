using System;
using System.IO;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class ProviderStatusTests
{
    [Fact]
    public void ValidS3_IsReady()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), $"cre-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tokenPath,
                "{\"access_key_id\":\"AK\",\"secret_access_key\":\"SK\",\"bucket\":\"b\"," +
                "\"endpoint\":\"https://example.com\",\"region\":\"us-east-1\",\"key_prefix\":\"\"}");

            var r = ProviderStatus.Classify(true, new CloudConfig("s3", tokenPath, null));

            Assert.Equal(ProviderReadiness.Ready, r.State);
            Assert.NotNull(r.DisplayName);
            Assert.Null(r.Error);
            Assert.StartsWith("Ready", ProviderStatus.Text(r.State, r.DisplayName, r.Error));
        }
        finally
        {
            File.Delete(tokenPath);
        }
    }

    [Fact]
    public void ValidR2_IsReady()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), $"cre-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tokenPath,
                "{\"access_key_id\":\"AK\",\"secret_access_key\":\"SK\",\"bucket\":\"b\"," +
                "\"endpoint\":\"https://example.com\",\"region\":\"us-east-1\",\"key_prefix\":\"\"}");

            var r = ProviderStatus.Classify(true, new CloudConfig("r2", tokenPath, null));

            Assert.Equal(ProviderReadiness.Ready, r.State);
        }
        finally
        {
            File.Delete(tokenPath);
        }
    }

    [Fact]
    public void ValidFolderWithSyncPath_IsReady()
    {
        var r = ProviderStatus.Classify(true, new CloudConfig("folder", null, "/tmp/sync"));

        Assert.Equal(ProviderReadiness.Ready, r.State);
        Assert.Equal("Folder / network drive", r.DisplayName);
        Assert.StartsWith("Ready — Folder / network drive",
            ProviderStatus.Text(r.State, r.DisplayName, r.Error));
    }

    [Theory]
    [InlineData("gdrive", "Google Drive")]
    [InlineData("onedrive", "OneDrive")]
    [InlineData("local", "Local only")]
    public void ReadOnlyProviders_AreReadOnly(string provider, string expectedName)
    {
        var r = ProviderStatus.Classify(true, new CloudConfig(provider, null, null));

        Assert.Equal(ProviderReadiness.ReadOnly, r.State);
        Assert.Equal(expectedName, r.DisplayName);
        var text = ProviderStatus.Text(r.State, r.DisplayName, r.Error);
        Assert.Contains("read-only", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownProvider_IsError()
    {
        var r = ProviderStatus.Classify(true, new CloudConfig("mystery", null, null));

        Assert.Equal(ProviderReadiness.Error, r.State);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void FolderWithoutSyncPath_IsConfigurationError()
    {
        var r = ProviderStatus.Classify(true, new CloudConfig("folder", null, null));

        Assert.Equal(ProviderReadiness.Error, r.State);
        Assert.Contains("sync path", ProviderStatus.Text(r.State, r.DisplayName, r.Error),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingConfigFile_IsMissingConfig()
    {
        var r = ProviderStatus.Classify(false, null);

        Assert.Equal(ProviderReadiness.MissingConfig, r.State);
        Assert.Equal(ProviderStatus.MissingConfig, ProviderStatus.Text(r.State, r.DisplayName, r.Error));
    }

    [Fact]
    public void ConfigFileExistsButUnusable_IsUnusableConfig()
    {
        var r = ProviderStatus.Classify(true, null);

        Assert.Equal(ProviderReadiness.UnusableConfig, r.State);
        Assert.Equal(ProviderStatus.UnusableConfig, ProviderStatus.Text(r.State, r.DisplayName, r.Error));
    }

    [Fact]
    public void PlainHttpEndpoint_IsFlaggedButDoesNotBlockSaving()
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), $"cre-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tokenPath,
                "{\"access_key_id\":\"AK\",\"secret_access_key\":\"SK\",\"bucket\":\"b\"," +
                "\"endpoint\":\"http://storage.internal:9000\",\"region\":\"us-east-1\",\"key_prefix\":\"\"}");

            var r = ProviderStatus.Classify(true, new CloudConfig("s3", tokenPath, null));

            Assert.Equal(ProviderReadiness.Ready, r.State);
            Assert.True(r.InsecureEndpoint);
            Assert.Null(r.Error);
        }
        finally
        {
            File.Delete(tokenPath);
        }
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("objects.example.com")]
    public void HttpsOrSchemelessEndpoint_IsNotFlagged(string endpoint)
    {
        var tokenPath = Path.Combine(Path.GetTempPath(), $"cre-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tokenPath,
                "{\"access_key_id\":\"AK\",\"secret_access_key\":\"SK\",\"bucket\":\"b\"," +
                $"\"endpoint\":\"{endpoint}\",\"region\":\"us-east-1\",\"key_prefix\":\"\"}}");

            var r = ProviderStatus.Classify(true, new CloudConfig("s3", tokenPath, null));

            Assert.Equal(ProviderReadiness.Ready, r.State);
            Assert.False(r.InsecureEndpoint);
        }
        finally
        {
            File.Delete(tokenPath);
        }
    }

    [Fact]
    public void MissingOrUnusableConfig_NeverFlagsInsecureEndpoint()
    {
        Assert.False(ProviderStatus.Classify(false, null).InsecureEndpoint);
        Assert.False(ProviderStatus.Classify(true, null).InsecureEndpoint);
        Assert.False(ProviderStatus.Classify(true, new CloudConfig("mystery", null, null)).InsecureEndpoint);
    }

    [Fact]
    public void InsecureEndpointWarning_NeverContainsAnEndpointValue()
    {
        Assert.Contains("http://", ProviderStatus.InsecureEndpointWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("storage.internal", ProviderStatus.InsecureEndpointWarning, StringComparison.Ordinal);
        Assert.DoesNotContain(":", ProviderStatus.InsecureEndpointWarning.Replace("http://", ""), StringComparison.Ordinal);
    }
}