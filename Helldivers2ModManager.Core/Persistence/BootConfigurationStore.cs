using System.Text.Json;

namespace Helldivers2ModManager.Core.Persistence;

public static class BootConfigurationStore
{
    public static string DefaultPath { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "boot.json");

    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static BootConfiguration? Load(string path = "")
    {
        var resolved = Resolve(path);
        if (!File.Exists(resolved)) return null;
        var json = File.ReadAllText(resolved);
        return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.BootConfiguration);
    }

    public static async Task<BootConfiguration?> LoadAsync(string path = "", CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (!File.Exists(resolved)) return null;
        var json = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.BootConfiguration);
    }

    public static void Save(BootConfiguration configuration, string path = "") =>
        File.WriteAllText(Resolve(path), JsonSerializer.Serialize(configuration, PersistenceJsonContext.Default.BootConfiguration));

    public static async Task SaveAsync(
        BootConfiguration configuration,
        string path = "",
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(configuration, PersistenceJsonContext.Default.BootConfiguration);
        await File.WriteAllTextAsync(Resolve(path), json, cancellationToken).ConfigureAwait(false);
    }

    private static string Resolve(string path) =>
        string.IsNullOrWhiteSpace(path) ? DefaultPath : System.IO.Path.GetFullPath(path);
}
