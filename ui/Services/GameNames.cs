namespace CloudReEdit.Services;

/// <summary>Pure game-title logic shared by the local scanner and the Games page
/// enrichment pass. Games appear with an AppId-based fallback title immediately,
/// then the real store name replaces it when metadata arrives.</summary>
public static class GameNames
{
    public static string InitialName(uint appId) => $"App {appId}";

    public static string DisplayName(uint appId, string? storeName) =>
        string.IsNullOrEmpty(storeName) ? InitialName(appId) : storeName;
}