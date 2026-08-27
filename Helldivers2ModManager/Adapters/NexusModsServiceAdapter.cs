using Helldivers2ModManager.Core.Nexus;
using Helldivers2ModManager.Models.Nexus;

namespace Helldivers2ModManager.Adapters;

internal sealed class NexusModsServiceAdapter : Services.Nexus.INexusModsService
{
    public NexusModsServiceAdapter()
        : this(static apiKey => new NexusApiClient(apiKey))
    {
    }

    internal NexusModsServiceAdapter(Func<string, NexusApiClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _clientFactory = clientFactory;
    }
    private readonly object _lock = new();
    private NexusApiClient? _client;
    private string? _apiKey;
    private readonly Func<string, NexusApiClient> _clientFactory;

    public bool Initialized => _client?.Initialized == true;

    public void Init(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        lock (_lock)
        {
            _apiKey = apiKey;
            if (_client is null)
                _client = _clientFactory(apiKey);
            else
                _client.SetApiKey(apiKey);
        }
    }

    public async Task<Mod> GetModAsync(string gameDomain, string modId, CancellationToken cancellationToken = default)
    {
        var mod = await Client().GetModAsync(gameDomain, modId, cancellationToken);
        return Map(mod);
    }

    public async Task<List<ModFile>> GetModFilesAsync(string gameDomain, string modId, CancellationToken cancellationToken = default)
    {
        var mod = await Client().GetModAsync(gameDomain, modId, cancellationToken);
        var files = new List<ModFile>();
        try
        {
            var groups = await Client().GetUpdateGroupsAsync(mod.Id, cancellationToken);
            foreach (var group in groups.Where(group => group.IsActive == true))
            {
                var versions = await Client().GetUpdateGroupVersionsAsync(group.Id, cancellationToken);
                files.AddRange(versions.Where(version => version.File is not null).Select(version => Map(version.File!)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var file = await Client().GetModFileAsync(gameDomain, mod.Id, cancellationToken);
            if (file is not null)
                files.Add(Map(file));
        }
        return files;
    }

    public async Task<List<ModFileUpdateGroup>> GetUpdateGroupsAsync(string modId, CancellationToken cancellationToken = default)
    {
        var groups = await Client().GetUpdateGroupsAsync(modId, cancellationToken);
        return groups.Select(Map).ToList();
    }

    public async Task<List<ModFileUpdateGroupVersion>> GetUpdateGroupVersionsAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var versions = await Client().GetUpdateGroupVersionsAsync(groupId, cancellationToken);
        return versions.Select(Map).ToList();
    }

    public async Task<List<TrendingMod>> GetTrendingModsAsync(string gameDomain, CancellationToken cancellationToken = default)
    {
        var mods = await Client().GetTrendingModsAsync(gameDomain, cancellationToken);
        return mods.Select(mod => new TrendingMod
        {
            Name = mod.Name,
            Author = mod.Author,
            Summary = mod.Summary,
            PictureUrl = mod.PictureUrl,
            ModPageUrl = mod.ModPageUrl
        }).ToList();
    }

    public async Task<UpdateInfo> CheckForUpdatesAsync(string modId, string currentVersion, CancellationToken cancellationToken = default)
    {
        NexusUpdateInfo update;
        try
        {
            update = await Client().CheckForUpdatesAsync(modId, currentVersion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            update = new NexusUpdateInfo(false, currentVersion, null, null);
        }
        return new UpdateInfo
        {
            HasUpdate = update.HasUpdate,
            CurrentVersion = update.CurrentVersion,
            LatestVersion = update.LatestVersion,
            LatestModFile = update.LatestFile is null ? null : Map(update.LatestFile)
        };
    }

    public async Task<string> DownloadModFileAsync(string gameDomain, string modId, string fileId, string savePath, CancellationToken cancellationToken = default) =>
        await Client().DownloadModFileAsync(gameDomain, modId, fileId, savePath, cancellationToken);

    public void ClearCache() => Client().ClearCache();

    private NexusApiClient Client()
    {
        lock (_lock)
        {
            if (_client is null)
                throw new InvalidOperationException("Nexus adapter has not been initialized.");

            return _client;
        }
    }

    private static Mod Map(NexusMod mod) => new()
    {
        Id = mod.Id,
        GameScopedId = mod.GameScopedId,
        Name = mod.Name,
        Summary = mod.Summary,
        Author = mod.Author,
        AdultContent = mod.AdultContent,
        Endorsements = mod.Endorsements,
        Downloads = mod.Downloads
    };

    private static ModFile Map(NexusFile file) => new()
    {
        Id = file.Id,
        Name = file.Name,
        Version = file.Version,
        Category = file.Category switch
        {
            NexusFileCategory.Main => ModFileCategory.main,
            NexusFileCategory.Update => ModFileCategory.update,
            NexusFileCategory.Optional => ModFileCategory.optional,
            NexusFileCategory.Old => ModFileCategory.old_version,
            NexusFileCategory.Miscellaneous => ModFileCategory.miscellaneous,
            NexusFileCategory.Deleted => ModFileCategory.removed,
            NexusFileCategory.Archived => ModFileCategory.archived,
            _ => ModFileCategory.unknown
        },
        SizeBytes = file.SizeBytes,
        IsPrimary = file.IsPrimary,
        UpdateGroupVersion = file.UpdateGroupVersion is null ? null : new Models.Nexus.UpdateGroupVersion
        {
            Position = file.UpdateGroupVersion.Position
        }
    };

    private static ModFileUpdateGroup Map(NexusUpdateGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        IsActive = group.IsActive
    };

    private static ModFileUpdateGroupVersion Map(NexusUpdateGroupVersion version) => new()
    {
        Id = version.Id,
        Position = version.Position,
        File = version.File is null ? null : Map(version.File),
        // Legacy versions carry position data on the nested File record.
    };
}



