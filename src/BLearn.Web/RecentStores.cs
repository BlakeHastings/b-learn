using System.Text.Json;

namespace BLearn.Web;

public sealed record RecentStore(string Path, string Name, DateTimeOffset OpenedAt, bool Exists);

/// <summary>Recently opened stores, kept in the user profile (never in a repo or a store).</summary>
public static class RecentStores
{
    private const int Max = 20;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "b-learn", "recent.json");

    public static IReadOnlyList<RecentStore> Load()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<RecentStore>>(File.ReadAllText(FilePath)) ?? [];
            return list.Select(r => r with { Exists = File.Exists(r.Path) }).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Touch(string path, string name)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path);
            var list = Load().Where(r => !string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, new RecentStore(path, name, DateTimeOffset.Now, true));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(Max)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Recent stores are a convenience; failing to record one must not fail the command.
        }
    }
}
