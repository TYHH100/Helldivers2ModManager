using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Core.Nexus;

public sealed class NexusApiClient : IDisposable
{
    private const string DefaultBaseUrl = "https://api.nexusmods.com/v3/";
    internal static readonly TimeSpan ModCacheDuration = TimeSpan.FromHours(1);
    internal static readonly TimeSpan UpdateGroupCacheDuration = TimeSpan.FromHours(4);
    private const int MaxRetryCount = 3;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly bool _ownsHttpClient;
    private string? _apiKey;

    public bool Initialized => !string.IsNullOrWhiteSpace(_apiKey);

    public NexusApiClient(string apiKey) : this(CreateDefaultClient(), apiKey, true) { }

    public NexusApiClient(HttpClient httpClient, string apiKey, bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _httpClient = httpClient;
        _apiKey = apiKey;
        _ownsHttpClient = ownsHttpClient;
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(header => header.MediaType == "application/json"))
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void SetApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
    }

    public async Task<NexusMod> GetModAsync(string gameDomain, string modId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var wrapper = await GetOrAddCachedAsync($"mod_{gameDomain}_{modId}", ModCacheDuration,
            () => GetJsonAsync<ModWrapper>($"games/{gameDomain}/mods/{modId}", cancellationToken), cancellationToken);
        return new NexusMod(wrapper.Data.Id, wrapper.Data.GameScopedId, wrapper.Data.Name, wrapper.Data.Summary,
            wrapper.Data.Author, wrapper.Data.AdultContent, wrapper.Data.Endorsements, wrapper.Data.Downloads);
    }

    public async Task<NexusFile?> GetModFileAsync(string gameDomain, string modId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var wrapper = await GetOrAddCachedAsync($"modfiles_{gameDomain}_{modId}", ModCacheDuration,
            () => GetJsonAsync<SingleModFileWrapper>($"games/{gameDomain}/mod-files/{modId}", cancellationToken),
            cancellationToken);
        return wrapper.Data is null ? null : ToPublicModel(wrapper.Data);
    }

    public async Task<IReadOnlyList<NexusUpdateGroup>> GetUpdateGroupsAsync(string modId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var wrapper = await GetOrAddCachedAsync($"updategroups_{modId}", UpdateGroupCacheDuration,
            () => GetJsonAsync<UpdateGroupsWrapper>($"mods/{modId}/file-update-groups", cancellationToken),
            cancellationToken);
        return wrapper.Data?.Groups?.Select(group => new NexusUpdateGroup(group.Id, group.Name, group.IsActive)).ToArray() ?? [];
    }

    public async Task<IReadOnlyList<NexusUpdateGroupVersion>> GetUpdateGroupVersionsAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var wrapper = await GetOrAddCachedAsync($"updategroupversions_{groupId}", UpdateGroupCacheDuration,
            () => GetJsonAsync<UpdateGroupVersionsWrapper>($"file-update-groups/{groupId}/versions", cancellationToken),
            cancellationToken);
        return wrapper.Data?.Versions?.Select(version => new NexusUpdateGroupVersion(
            version.Id,
            version.Position,
            version.File is null ? null : ToPublicModel(version.File),
            version.File?.UpdateGroupVersion)).ToArray() ?? [];
    }

    public async Task<IReadOnlyList<NexusTrendingMod>> GetTrendingModsAsync(string gameDomain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var wrapper = await GetOrAddCachedAsync($"trending_{gameDomain}", ModCacheDuration,
            () => GetJsonAsync<TrendingModsWrapper>($"games/{gameDomain}/trending-mods", cancellationToken), cancellationToken);
        return wrapper.Data?.Mods?.Select(mod => new NexusTrendingMod(
            mod.Name, mod.Author, mod.Summary, mod.PictureUrl, mod.ModPageUrl)).ToArray() ?? [];
    }

    public async Task<NexusUpdateInfo> CheckForUpdatesAsync(string modId, string currentVersion, CancellationToken cancellationToken = default)
    {
        var groups = await GetUpdateGroupsAsync(modId, cancellationToken);
        var activeGroup = groups.FirstOrDefault(group => group.IsActive == true);
        if (activeGroup is null)
            return new NexusUpdateInfo(false, currentVersion, null, null);

        var versions = await GetUpdateGroupVersionsAsync(activeGroup.Id, cancellationToken);
        var validVersions = versions
            .Where(version => version.File is not null && version.UpdateGroupVersion is not null)
            .OrderByDescending(version => ParsePosition(version.Position))
            .ToArray();
        if (validVersions.Length == 0)
            return new NexusUpdateInfo(false, currentVersion, null, null);

        var latest = validVersions[0];
        var currentPosition = 0m;
        foreach (var version in validVersions)
        {
            if (!string.Equals(version.File!.Version, currentVersion, StringComparison.OrdinalIgnoreCase))
                continue;

            var position = ParsePosition(version.UpdateGroupVersion!.Position);
            if (position > currentPosition)
                currentPosition = position;
        }

        var hasUpdate = ParsePosition(latest.UpdateGroupVersion!.Position) > currentPosition;
        return new NexusUpdateInfo(hasUpdate, currentVersion, latest.File!.Version, hasUpdate ? latest.File : null);
    }

    public async Task<string> DownloadModFileAsync(string gameDomain, string modId, string fileId, string savePath, CancellationToken cancellationToken = default)
    {
        GuardInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        var linkWrapper = await GetJsonAsync<DownloadLinkResponse>(
            new Uri("https://api.nexusmods.com/v1/games/" + gameDomain + "/mods/" + modId + "/files/" + fileId + "/download_link.json").ToString(), cancellationToken);
        if (string.IsNullOrWhiteSpace(linkWrapper.Uri))
            throw new NexusApiException("Nexus did not return a download URL.", 502, "InvalidResponse");
        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var response = await _httpClient.GetAsync(linkWrapper.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowIfErrorAsync(response, "download-link", cancellationToken);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(savePath);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
        return savePath;
    }

    public void ClearCache() => _cache.Clear();

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private async Task<T> GetOrAddCachedAsync<T>(string key, TimeSpan expiration, Func<Task<T>> factory, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return (T)entry.Value!;
        cancellationToken.ThrowIfCancellationRequested();
        var value = await factory();
        _cache[key] = new CacheEntry(value, DateTimeOffset.UtcNow.Add(expiration));
        return value;
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        GuardInitialized();
        var retryCount = 0;
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            using var request = CreateJsonRequest(path);
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
                await ThrowIfErrorAsync(response, path, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken) ??
                    throw new NexusApiException("Nexus returned an empty JSON payload.", 502, "InvalidResponse");
            }
            catch (Exception ex) when (retryCount < MaxRetryCount &&
                (ex is HttpRequestException || (ex is OperationCanceledException canceled && !cancellationToken.IsCancellationRequested && canceled.InnerException is TimeoutException)))
            {
                retryCount++;
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private HttpRequestMessage CreateJsonRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("apikey", _apiKey);
        return request;
    }

    private async Task ThrowIfErrorAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? detail = null;
        try
        {
            var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
            using var document = JsonDocument.Parse(content);
            detail = document.RootElement.TryGetProperty("detail", out var property) ? property.GetString() : null;
        }
        catch (JsonException) { }
        detail ??= response.ReasonPhrase ?? "request failed";
        switch ((int)response.StatusCode)
        {
            case 403 when detail.Contains("premium", StringComparison.OrdinalIgnoreCase):
                throw new NexusApiException(detail, 403, "PremiumRequired");
            case 403:
                throw new NexusApiException(detail, 403, "ApiKeyInvalid");
            case 404:
                throw new NexusApiException(detail, 404, "ModNotFound");
            case 429:
                throw new NexusRateLimitException(response.Headers.RetryAfter?.Delta);
            default:
                throw new NexusApiException($"{detail} ({(int)response.StatusCode}) while calling {path}.",
                    (int)response.StatusCode, (int)response.StatusCode >= 500 ? "ServerError" : "RequestFailed");
        }
    }

    private void GuardInitialized()
    {
        if (!Initialized)
            throw new InvalidOperationException("NexusApiClient is not initialized.");
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(300),
            BaseAddress = new Uri(DefaultBaseUrl)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Helldivers2ModManager");
        return client;
    }

    private static NexusFile ToPublicModel(ModFile model) => new(model.Id, model.Name, model.Version,
        model.Category, model.SizeBytes, model.IsPrimary, model.UpdateGroupVersion);

    private static decimal ParsePosition(string? position) =>
        decimal.TryParse(position, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0m;

    private sealed record CacheEntry(object? Value, DateTimeOffset ExpiresAt);

    private sealed class ModWrapper { [JsonPropertyName("data")] public Mod Data { get; set; } = new(); }
    private sealed class SingleModFileWrapper { [JsonPropertyName("data")] public ModFile? Data { get; set; } }
    private sealed class Mod
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("game_scoped_id")] public string GameScopedId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("adult_content")] public bool? AdultContent { get; set; }
        [JsonPropertyName("endorsements")] public int? Endorsements { get; set; }
        [JsonPropertyName("downloads")] public int? Downloads { get; set; }
    }
    private sealed class UpdateGroupsWrapper { [JsonPropertyName("data")] public UpdateGroupsData? Data { get; set; } }
    private sealed class UpdateGroupsData { [JsonPropertyName("groups")] public List<UpdateGroup>? Groups { get; set; } }
    private sealed class UpdateGroup
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
    }
    private sealed class UpdateGroupVersionsWrapper { [JsonPropertyName("data")] public UpdateGroupVersionsData? Data { get; set; } }
    private sealed class UpdateGroupVersionsData { [JsonPropertyName("versions")] public List<UpdateGroupVersion>? Versions { get; set; } }
    private sealed class UpdateGroupVersion
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("position")] public string Position { get; set; } = string.Empty;
        [JsonPropertyName("file")] public ModFile? File { get; set; }
    }
    private sealed class ModFile
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("category")] public NexusFileCategory? Category { get; set; }
        [JsonPropertyName("size_bytes")] public long? SizeBytes { get; set; }
        [JsonPropertyName("is_primary")] public bool? IsPrimary { get; set; }
        [JsonPropertyName("update_group_version")] public NexusUpdateGroupPosition? UpdateGroupVersion { get; set; }
    }
    private sealed class TrendingModsWrapper { [JsonPropertyName("data")] public TrendingModsData? Data { get; set; } }
    private sealed class TrendingModsData { [JsonPropertyName("mods")] public List<TrendingMod>? Mods { get; set; } }
    private sealed class TrendingMod
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("picture_url")] public string? PictureUrl { get; set; }
        [JsonPropertyName("mod_page_url")] public string ModPageUrl { get; set; } = string.Empty;
    }
    private sealed class DownloadLinkResponse { [JsonPropertyName("URI")] public string Uri { get; set; } = string.Empty; }
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}


