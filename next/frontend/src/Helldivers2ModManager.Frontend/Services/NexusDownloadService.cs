using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Nexus;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record NexusFetchResult(NexusMod Mod, IReadOnlyList<NexusFile> Files);

public sealed class NexusDownloadService(
    HttpClient httpClient,
    ApplicationSettingsService settings,
    LocalizationCatalog localization,
    TaskExecutionService tasks)
{
    private static readonly Regex NexusUrlPattern = new(
        @"nexusmods\.com/([^/]+)/mods/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string GameDomain, string ModId)? ParseUrl(string url)
    {
        var match = NexusUrlPattern.Match(url);
        return match.Success
            ? (match.Groups[1].Value, match.Groups[2].Value)
            : null;
    }

    public async Task<NexusFetchResult> FetchAsync(
        string gameDomain,
        string modId,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        NexusFetchResult? result = null;
        await tasks.RunAsync(
            localization.GetString("Next.Tasks.NexusFetch"),
            string.Format(localization.GetString("Next.Tasks.NexusFetchingFormat"), modId),
            async (_, token) =>
            {
                var mod = await client.GetModAsync(gameDomain, modId, token).ConfigureAwait(false);
                var files = await GetFilesAsync(client, gameDomain, mod.Id, token).ConfigureAwait(false);
                result = new(mod, files);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public async Task<string> DownloadAsync(
        string gameDomain,
        NexusMod mod,
        NexusFile file,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var tempDirectory = settings.Current.TempDirectory;
        Directory.CreateDirectory(tempDirectory);
        var fileName = Path.GetFileName(file.Name);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{mod.Name}.zip";
        }

        var savePath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}-{fileName}");
        string? result = null;
        await tasks.RunAsync(
            localization.GetString("Next.Tasks.NexusDownload"),
            string.Format(localization.GetString("Next.Tasks.NexusDownloadingFormat"), file.Name ?? mod.Name),
            async (_, token) =>
            {
                result = await client.DownloadModFileAsync(
                    gameDomain,
                    mod.GameScopedId,
                    file.Id,
                    savePath,
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private static async Task<IReadOnlyList<NexusFile>> GetFilesAsync(
        NexusApiClient client,
        string gameDomain,
        string globalModId,
        CancellationToken cancellationToken)
    {
        try
        {
            var groups = await client.GetUpdateGroupsAsync(globalModId, cancellationToken).ConfigureAwait(false);
            var activeGroup = groups.FirstOrDefault(group => group.IsActive == true);
            if (activeGroup is null)
            {
                return [];
            }

            var versions = await client.GetUpdateGroupVersionsAsync(activeGroup.Id, cancellationToken).ConfigureAwait(false);
            return [.. versions.Where(version => version.File is not null).Select(version => version.File!)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var file = await client.GetModFileAsync(gameDomain, globalModId, cancellationToken).ConfigureAwait(false);
            return file is null ? [] : [file];
        }
    }

    private NexusApiClient CreateClient()
    {
        var apiKey = settings.Current.NexusApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Nexus API key is not configured.");
        }

        return new NexusApiClient(httpClient, apiKey);
    }
}
