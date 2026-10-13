using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudReEdit.Services;

/// <summary>What a playtime save does with Steam.</summary>
public enum SaveSteamBehaviour
{
    /// <summary>Ask with a dialog every time Steam is running. The default.</summary>
    Ask,

    /// <summary>Close Steam for the save and leave it closed afterwards.</summary>
    NeverOpen,

    /// <summary>Always have Steam open once the save is done.</summary>
    AlwaysOpen,

    /// <summary>Put Steam back the way it was before the save.</summary>
    LastState,
}

/// <summary>Persisted application preferences. Deliberately separate from
/// <c>SteamDetector.ReadConfig</c>, which reads CloudRedirect's own
/// <c>config.json</c> (provider/token/sync) and is not ours to write.</summary>
/// <remarks>The path-taking overloads exist so the rules can be unit tested
/// against temp directories without touching real AppData. Every member here
/// swallows its own failures: a settings problem must never block a save.</remarks>
public static class AppSettings
{
    public const string SaveSteamBehaviourKey = "saveSteamBehaviour";

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CloudReEdit", "settings.json");

    /// <summary>Turns a stored token into a behaviour. Unknown, missing or
    /// malformed values fall back to <see cref="SaveSteamBehaviour.Ask"/> rather
    /// than guessing, so a corrupt file degrades to the safest option (ask).</summary>
    public static SaveSteamBehaviour ParseSaveSteamBehaviour(string? token)
        => token?.Trim().ToLowerInvariant() switch
        {
            "ask" => SaveSteamBehaviour.Ask,
            "neveropen" or "never_open" or "never" => SaveSteamBehaviour.NeverOpen,
            "alwaysopen" or "always_open" or "always" => SaveSteamBehaviour.AlwaysOpen,
            "laststate" or "last_state" or "last" => SaveSteamBehaviour.LastState,
            _ => SaveSteamBehaviour.Ask,
        };

    /// <summary>The canonical token written back to disk for a behaviour.</summary>
    public static string SaveSteamBehaviourToken(SaveSteamBehaviour behaviour)
        => behaviour switch
        {
            SaveSteamBehaviour.NeverOpen => "neverOpen",
            SaveSteamBehaviour.AlwaysOpen => "alwaysOpen",
            SaveSteamBehaviour.LastState => "lastState",
            _ => "ask",
        };

    public static SaveSteamBehaviour Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return SaveSteamBehaviour.Ask;

            var node = JsonNode.Parse(File.ReadAllText(path));
            var token = node?[SaveSteamBehaviourKey]?.GetValue<string>();
            return ParseSaveSteamBehaviour(token);
        }
        catch (Exception ex)
        {
            AppLog.Exception("settings", $"settings could not be read from \"{path}\"", ex);
            return SaveSteamBehaviour.Ask;
        }
    }

    public static void Write(string path, SaveSteamBehaviour behaviour)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new JsonObject
            {
                [SaveSteamBehaviourKey] = SaveSteamBehaviourToken(behaviour),
            };

            File.WriteAllText(path, payload.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Exception("settings", $"settings could not be written to \"{path}\"", ex);
        }
    }

    /// <summary>Loads the save/Steam behaviour from the real settings file.</summary>
    public static SaveSteamBehaviour LoadSaveSteamBehaviour() => Read(FilePath);

    /// <summary>Persists the save/Steam behaviour to the real settings file.</summary>
    public static void SetSaveSteamBehaviour(SaveSteamBehaviour behaviour)
        => Write(FilePath, behaviour);
}
