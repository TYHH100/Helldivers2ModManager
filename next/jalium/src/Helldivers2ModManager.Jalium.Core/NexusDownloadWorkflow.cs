using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services.Nexus;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class NexusDownloadWorkflow(INexusModsService service)
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar" };

    public static bool TryParseUrl(string input, out string gameDomain, out string modId)
    {
        gameDomain = modId = string.Empty;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || (uri.Host != "nexusmods.com"
                && !uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase)))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segments[2].All(char.IsAsciiDigit))
            return false;
        gameDomain = segments[0];
        modId = segments[2];
        return true;
    }

    public async Task<(Mod Mod, IReadOnlyList<ModFile> Files)> FetchAsync(
        string url, string apiKey, CancellationToken cancellationToken)
    {
        if (!TryParseUrl(url, out var domain, out var id))
            throw new ArgumentException("Invalid Nexus Mods URL.", nameof(url));
        service.Init(apiKey);
        var mod = await service.GetModAsync(domain, id, cancellationToken);
        var files = await service.GetModFilesAsync(domain, id, cancellationToken);
        foreach (var file in files)
            if (string.IsNullOrWhiteSpace(file.Name))
                file.Name = string.IsNullOrWhiteSpace(file.Version)
                    ? mod.Name : $"{mod.Name} v{file.Version}";
        return (mod, files);
    }

    public async Task<string> DownloadAsync(string url, Mod mod, ModFile file,
        string tempDirectory, CancellationToken cancellationToken)
    {
        if (!TryParseUrl(url, out var domain, out _))
            throw new ArgumentException("Invalid Nexus Mods URL.", nameof(url));
        var extension = Path.GetExtension(file.Name);
        if (extension is null || !ArchiveExtensions.Contains(extension))
            extension = ".zip";
        var root = Path.GetFullPath(tempDirectory);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, $"nexus-{Guid.NewGuid():N}{extension}");
        try
        {
            var saved = await service.DownloadModFileAsync(domain, mod.GameScopedId,
                file.GameScopedId, target, cancellationToken);
            if (!Path.GetFullPath(saved).Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Nexus download path changed unexpectedly.");
            return target;
        }
        catch (Exception downloadError)
        {
            try
            {
                if (File.Exists(target))
                    File.Delete(target);
            }
            catch (IOException cleanupError)
            {
                downloadError.Data["CleanupError"] = cleanupError;
            }
            catch (UnauthorizedAccessException cleanupError)
            {
                downloadError.Data["CleanupError"] = cleanupError;
            }
            throw;
        }
    }
}
