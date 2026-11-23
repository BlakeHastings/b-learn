using System.Text.Json;

namespace BLearn.Web;

/// <summary>Per-user preferences, kept in the user profile next to the recent stores list.</summary>
/// <param name="AutoConnect">The agent harness to connect whenever a store opens, or null.</param>
public sealed record UserSettings(string? AutoConnect)
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "b-learn", "settings.json");

    public static UserSettings Load()
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new((string?)null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new((string?)null); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
