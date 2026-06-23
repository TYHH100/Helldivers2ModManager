using Helldivers2ModManager.Exceptions.Nexus;
using Helldivers2ModManager.Models.Nexus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Services.Nexus
{
    [RegisterService(ServiceLifetime.Singleton, Contract = typeof(INexusHttpClient))]
    internal sealed class NexusHttpClient : INexusHttpClient
    {
        private const string BaseAddress = "https://api.nexusmods.com/v3/";
        private const string V1BaseAddress = "https://api.nexusmods.com/";
        private const int MaxRetryCount = 3;
        private const int TimeoutSeconds = 300;

        private readonly ILogger<NexusHttpClient> _logger;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _v1HttpClient;
        private string? _apiKey;

        public bool Initialized => !string.IsNullOrEmpty(_apiKey);

        public NexusHttpClient(ILogger<NexusHttpClient> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseAddress),
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
            _v1HttpClient = new HttpClient
            {
                BaseAddress = new Uri(V1BaseAddress),
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
            };
            _v1HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void Init(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API Key cannot be null or empty.", nameof(apiKey));

            _apiKey = apiKey;
            _httpClient.DefaultRequestHeaders.Remove("apikey");
            _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);
            _v1HttpClient.DefaultRequestHeaders.Remove("apikey");
            _v1HttpClient.DefaultRequestHeaders.Add("apikey", apiKey);
            _logger.LogInformation("Nexus HttpClient initialized");
        }

        public void SetApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API Key cannot be null or empty.", nameof(apiKey));

            _apiKey = apiKey;
            _httpClient.DefaultRequestHeaders.Remove("apikey");
            _httpClient.DefaultRequestHeaders.Add("apikey", apiKey);
            _v1HttpClient.DefaultRequestHeaders.Remove("apikey");
            _v1HttpClient.DefaultRequestHeaders.Add("apikey", apiKey);
            _logger.LogInformation("Nexus API Key updated");
        }

        public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
        {
            GuardInitialized();

            int retryCount = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1);

            while (true)
            {
                try
                {
                    _logger.LogDebug("GET {Path}", path);

                    using var response = await _httpClient.GetAsync(path, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogDebug("Response received for {Path}", path);
                        return Deserialize<T>(content);
                    }

                    await HandleErrorResponse(response, path, cancellationToken);
                }
                catch (HttpRequestException ex) when (retryCount < MaxRetryCount)
                {
                    retryCount++;
                    _logger.LogWarning("Network error on attempt {RetryCount}/{MaxRetryCount}: {Message}", 
                        retryCount, MaxRetryCount, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
                catch (OperationCanceledException) when (retryCount < MaxRetryCount)
                {
                    retryCount++;
                    _logger.LogWarning("Request canceled on attempt {RetryCount}/{MaxRetryCount}", 
                        retryCount, MaxRetryCount);
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
            }
        }
        
        private async Task<T> GetV1Async<T>(string path, CancellationToken cancellationToken = default)
        {
            GuardInitialized();

            int retryCount = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1);

            while (true)
            {
                try
                {
                    _logger.LogDebug("GET V1 API: {Path}", path);

                    using var response = await _v1HttpClient.GetAsync(path, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogDebug("Response received for V1 API {Path}", path);
                        return Deserialize<T>(content);
                    }

                    await HandleErrorResponse(response, path, cancellationToken);
                }
                catch (HttpRequestException ex) when (retryCount < MaxRetryCount)
                {
                    retryCount++;
                    _logger.LogWarning("Network error on attempt {RetryCount}/{MaxRetryCount}: {Message}", 
                        retryCount, MaxRetryCount, ex.Message);
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
                catch (OperationCanceledException) when (retryCount < MaxRetryCount)
                {
                    retryCount++;
                    _logger.LogWarning("Request canceled on attempt {RetryCount}/{MaxRetryCount}", 
                        retryCount, MaxRetryCount);
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2);
                }
            }
        }

        public async Task<string> DownloadFileAsync(string gameDomain, string modId, string fileId, string savePath, CancellationToken cancellationToken = default)
        {
            GuardInitialized();
            
            // 使用 V1 API 端点获取下载链接
            var path = $"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json";
            
            _logger.LogInformation("Getting download link for file {FileId} using V1 API", fileId);
            
            // V1 API 直接返回包含 URL 的 JSON 对象
            var downloadLinkResponse = await GetV1Async<V1DownloadLinkResponse>(path, cancellationToken);
            var downloadUrl = downloadLinkResponse.URI;
            
            _logger.LogInformation("Downloading file from {Url}", downloadUrl);
            
            var directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            using var fileStream = File.Create(savePath);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await contentStream.CopyToAsync(fileStream, cancellationToken);
            
            _logger.LogInformation("File downloaded successfully to {Path}", savePath);
            
            return savePath;
        }

        private void GuardInitialized()
        {
            if (!Initialized)
            {
                throw new InvalidOperationException("NexusHttpClient has not been initialized. Call Init() first.");
            }
        }

        private async Task HandleErrorResponse(HttpResponseMessage response, string path, CancellationToken cancellationToken)
        {
            ProblemDetails? problemDetails = null;

            try
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    problemDetails = Deserialize<ProblemDetails>(content);
                }
            }
            catch (JsonException)
            {
                _logger.LogDebug("Failed to parse error response as ProblemDetails");
            }

            var detail = problemDetails?.Detail ?? response.ReasonPhrase;

            switch ((int)response.StatusCode)
            {
                case 400:
                    _logger.LogError("Bad request for {Path}: {Detail}", path, detail);
                    throw new NexusApiException($"Bad request: {detail}", 400, "InvalidRequest");

                case 403:
                    _logger.LogError("Unauthorized request for {Path}: {Detail}", path, detail);
                    if (!string.IsNullOrEmpty(detail) && detail.Contains("premium", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NexusPremiumRequiredException("下载功能需要 Nexus Mods Premium 会员资格");
                    }
                    throw new NexusApiKeyInvalidException(detail ?? "Invalid or missing API Key");

                case 404:
                    _logger.LogError("Resource not found for {Path}: {Detail}", path, detail);
                    throw new NexusModNotFoundException(ExtractModIdFromPath(path));

                case 422:
                    var errors = problemDetails?.Errors?.Select(e => e.Detail ?? string.Empty).ToList() ?? new List<string>();
                    _logger.LogError("Validation failed for {Path}: {Errors}", path, string.Join(", ", errors));
                    throw new NexusValidationException(errors);

                case 429:
                    var retryAfter = ParseRetryAfter(response);
                    _logger.LogError("Rate limit exceeded for {Path}", path);
                    throw retryAfter.HasValue 
                        ? new NexusRateLimitException(retryAfter.Value) 
                        : new NexusRateLimitException();

                case >= 500:
                    _logger.LogError("Server error ({StatusCode}) for {Path}: {Detail}", 
                        (int)response.StatusCode, path, detail);
                    throw new NexusApiException($"Server error: {detail}", (int)response.StatusCode, "ServerError");

                default:
                    _logger.LogError("Unexpected HTTP error ({StatusCode}) for {Path}: {Detail}", 
                        (int)response.StatusCode, path, detail);
                    throw new NexusApiException($"HTTP error {(int)response.StatusCode}: {detail}", 
                        (int)response.StatusCode, "Unknown");
            }
        }

        private string ExtractModIdFromPath(string path)
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "mods" && i + 1 < parts.Length)
                {
                    return parts[i + 1];
                }
            }
            return "unknown";
        }

        private TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    return response.Headers.RetryAfter.Delta.Value;
                }
                if (response.Headers.RetryAfter.Date.HasValue)
                {
                    return response.Headers.RetryAfter.Date.Value - DateTimeOffset.Now;
                }
            }

            if (response.Headers.TryGetValues("Retry-After", out var values))
            {
                if (int.TryParse(values.FirstOrDefault(), out int seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }

            return null;
        }

        private T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new JsonException($"Cannot deserialize null or empty JSON to {typeof(T).Name}");
            }

            // 避免在日志中输出完整 JSON，防止敏感信息泄露
            var jsonLength = json.Length;
            var truncatedJson = jsonLength > 200 ? json[..200] + "..." : json;
            _logger.LogDebug("Attempting to deserialize JSON to {TypeName} (length: {Length})", typeof(T).Name, jsonLength);
            _logger.LogTrace("JSON content preview: {JsonPreview}", truncatedJson);
            try
            {
                var result = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    Converters = 
                    {
                        new JsonStringEnumConverter(allowIntegerValues: true)
                    }
                });
                
                if (result == null)
                {
                    throw new JsonException($"Failed to deserialize JSON to {typeof(T).Name}: result was null");
                }
                
                return result;
            }
            catch (JsonException ex)
            {
                // 错误日志中也截断 JSON 内容
                _logger.LogError(ex, "JSON deserialization failed for {TypeName} (length: {Length}). Content preview: {JsonPreview}", 
                    typeof(T).Name, jsonLength, truncatedJson);
                throw;
            }
        }
    }

    internal sealed class DownloadLinkWrapper
    {
        [JsonPropertyName("data")]
        public DownloadLinkData Data { get; set; } = null!;
    }

    internal sealed class DownloadLinkData
    {
        [JsonPropertyName("URI")]
        public string URI { get; set; } = string.Empty;
    }
    
    internal sealed class V1DownloadLinkResponse
    {
        [JsonPropertyName("URI")]
        public string URI { get; set; } = string.Empty;
    }
}
