using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services.AI;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class AiTranslationService : IDisposable
{
    private const int MaxBatchItems = 24;
    private const int MaxBatchCharacters = 8_000;
    private const int MaxConcurrentBatches = 3;

    private readonly SettingsService _settings;
    private readonly ILogger<AiTranslationService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public AiTranslationService(SettingsService settings, ILogger<AiTranslationService> logger)
        : this(settings, logger, new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }))
    {
    }

    public async Task<IReadOnlyDictionary<string, string>> GetCachedTranslationsAsync(
        IEnumerable<string> sourceTexts,
        CancellationToken cancellationToken = default)
    {
        var texts = sourceTexts
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (texts.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in texts)
                if (cache.TryGetValue(BuildCacheKey(source), out var translated))
                    result[source] = translated;
            return result;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    internal AiTranslationService(SettingsService settings, ILogger<AiTranslationService> logger, HttpMessageHandler handler)
        : this(settings, logger, new HttpClient(handler, disposeHandler: true))
    {
    }

    private AiTranslationService(SettingsService settings, ILogger<AiTranslationService> logger, HttpClient httpClient)
    {
        _settings = settings;
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<AiTranslationResult> TranslateAsync(
        IEnumerable<string> sourceTexts,
        bool forceRefresh = false,
        IProgress<AiTranslationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var texts = sourceTexts
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (texts.Length == 0)
            return new AiTranslationResult(new Dictionary<string, string>(StringComparer.Ordinal), 0, 0, 0, 0, 0);

        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var pending = new List<(string Source, string Key)>();
            var localCacheHits = 0;
            foreach (var source in texts)
            {
                var key = BuildCacheKey(source);
                if (!forceRefresh && cache.TryGetValue(key, out var cached))
                {
                    result[source] = cached;
                    localCacheHits++;
                    progress?.Report(new AiTranslationProgress(source, cached, localCacheHits, texts.Length, true));
                    continue;
                }
                pending.Add((source, key));
            }

            if (pending.Count == 0)
                return new AiTranslationResult(result, localCacheHits, 0, 0, 0, 0);

            var apiKey = _settings.AiTranslationApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("请先在设置中配置 AI API Key。");
            if (!Uri.TryCreate(_settings.AiTranslationEndpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("AI 翻译端点必须是有效的 HTTPS 地址。");

            var batches = BuildBatches(pending);
            long promptCacheHitTokens = 0;
            long promptCacheMissTokens = 0;
            long completionTokens = 0;
            var apiCompleted = 0;
            for (var batchOffset = 0; batchOffset < batches.Count; batchOffset += MaxConcurrentBatches)
            {
                var wave = batches.Skip(batchOffset).Take(MaxConcurrentBatches).ToArray();
                var tasks = wave.Select(batch => RequestTranslationBatchAsync(
                    endpoint,
                    apiKey,
                    batch,
                    item =>
                    {
                        var completed = localCacheHits + Interlocked.Increment(ref apiCompleted);
                        progress?.Report(new AiTranslationProgress(item.Source, item.Translation, completed, texts.Length, false));
                    },
                    cancellationToken)).ToArray();
                foreach (var task in tasks)
                {
                    var translatedBatch = await task.ConfigureAwait(false);
                    promptCacheHitTokens += translatedBatch.PromptCacheHitTokens;
                    promptCacheMissTokens += translatedBatch.PromptCacheMissTokens;
                    completionTokens += translatedBatch.CompletionTokens;
                    foreach (var item in translatedBatch.Items)
                    {
                        result[item.Source] = item.Translation;
                        cache[item.Key] = item.Translation;
                    }
                    await SaveCacheAsync(cache, cancellationToken).ConfigureAwait(false);
                }
            }
            return new AiTranslationResult(
                result,
                localCacheHits,
                pending.Count,
                promptCacheHitTokens,
                promptCacheMissTokens,
                completionTokens);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private string BuildCacheKey(string source)
    {
        var thinkingMode = _settings.AiTranslationEnableThinking
            ? $"enabled:{_settings.AiTranslationReasoningEffort}"
            : "disabled";
        var material = string.Join("\n", _settings.AiTranslationEndpoint, _settings.AiTranslationModel,
            _settings.AiTranslationTargetLanguage, thinkingMode, source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static List<List<PendingTranslation>> BuildBatches(IEnumerable<(string Source, string Key)> pending)
    {
        var batches = new List<List<PendingTranslation>>();
        var current = new List<PendingTranslation>(MaxBatchItems);
        var currentCharacters = 0;

        foreach (var (source, key) in pending)
        {
            if (current.Count > 0
                && (current.Count >= MaxBatchItems || currentCharacters + source.Length > MaxBatchCharacters))
            {
                batches.Add(current);
                current = new List<PendingTranslation>(MaxBatchItems);
                currentCharacters = 0;
            }

            current.Add(new PendingTranslation(current.Count, source, key));
            currentCharacters += source.Length;
        }

        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    private async Task<CompletedBatch> RequestTranslationBatchAsync(
        Uri endpoint,
        string apiKey,
        IReadOnlyList<PendingTranslation> batch,
        Action<CompletedTranslation> onTranslation,
        CancellationToken cancellationToken)
    {
        var maxOutputTokens = Math.Clamp(batch.Sum(static item => item.Source.Length) * 2 + 256, 512, 8_192);
        var inputJson = JsonSerializer.Serialize(new
        {
            items = batch.Select(static item => new { id = item.Id, text = item.Source })
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _settings.AiTranslationModel,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = $"Translate every items[].text into {_settings.AiTranslationTargetLanguage}. Stream one compact JSON object per line using exactly {{\"id\":0,\"text\":\"translation\"}}. Keep every input id exactly once and in order. Escape line breaks inside text as JSON \\n. Preserve placeholders, file paths, IDs, and line breaks. Do not return a JSON array, explanations, or markdown fences."
                },
                new { role = "user", content = inputJson }
            },
            ["temperature"] = 0.2,
            ["max_tokens"] = maxOutputTokens,
            ["thinking"] = new { type = _settings.AiTranslationEnableThinking ? "enabled" : "disabled" },
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true }
        };
        if (_settings.AiTranslationEnableThinking)
            payload["reasoning_effort"] = _settings.AiTranslationReasoningEffort;
        request.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"AI 翻译请求失败（{(int)response.StatusCode}）：{TrimError(body)}");
		}

        try
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            return string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
                ? await ReadStreamingBatchAsync(response, batch, onTranslation, cancellationToken).ConfigureAwait(false)
                : await ReadNonStreamingBatchAsync(response, batch, onTranslation, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "AI translation response was not a Chat Completions response");
            throw new InvalidOperationException("AI 返回格式不是 Chat Completions 格式。", ex);
        }
    }

    private static async Task<CompletedBatch> ReadStreamingBatchAsync(
        HttpResponseMessage response,
        IReadOnlyList<PendingTranslation> batch,
        Action<CompletedTranslation> onTranslation,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var contentBuffer = new StringBuilder();
        var completed = new Dictionary<int, CompletedTranslation>();
        var usage = default(Usage);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line[5..].TrimStart();
            if (data.Length == 0 || data == "[DONE]")
                continue;

            using var chunk = JsonDocument.Parse(data);
            var chunkUsage = ReadUsage(chunk.RootElement);
            if (chunkUsage != default)
                usage = chunkUsage;

            if (!chunk.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
                continue;

            contentBuffer.Append(content.GetString());
            ConsumeCompleteLines(contentBuffer, batch, completed, onTranslation);
        }

        ConsumeFinalContent(contentBuffer, batch, completed, onTranslation);
        EnsureComplete(batch, completed);
        return new CompletedBatch(
            batch.Select(item => completed[item.Id]).ToArray(),
            usage.PromptCacheHitTokens,
            usage.PromptCacheMissTokens,
            usage.CompletionTokens);
    }

    private static async Task<CompletedBatch> ReadNonStreamingBatchAsync(
        HttpResponseMessage response,
        IReadOnlyList<PendingTranslation> batch,
        Action<CompletedTranslation> onTranslation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(content.GetString()))
            throw new InvalidOperationException("AI 返回格式不是 Chat Completions 格式。");

        var completed = new Dictionary<int, CompletedTranslation>();
        ParseCompletedContent(content.GetString()!, batch, completed, onTranslation);
        EnsureComplete(batch, completed);
        var usage = ReadUsage(document.RootElement);
        return new CompletedBatch(
            batch.Select(item => completed[item.Id]).ToArray(),
            usage.PromptCacheHitTokens,
            usage.PromptCacheMissTokens,
            usage.CompletionTokens);
    }

    private static void ConsumeCompleteLines(
        StringBuilder buffer,
        IReadOnlyList<PendingTranslation> batch,
        Dictionary<int, CompletedTranslation> completed,
        Action<CompletedTranslation> onTranslation)
    {
        while (true)
        {
            var text = buffer.ToString();
            var lineEnd = text.IndexOf('\n');
            if (lineEnd < 0)
                return;

            var line = text[..lineEnd].Trim();
            buffer.Remove(0, lineEnd + 1);
            TryAddCompletedLine(line, batch, completed, onTranslation);
        }
    }

    private static void ConsumeFinalContent(
        StringBuilder buffer,
        IReadOnlyList<PendingTranslation> batch,
        Dictionary<int, CompletedTranslation> completed,
        Action<CompletedTranslation> onTranslation)
    {
        var remaining = buffer.ToString().Trim();
        if (remaining.Length > 0)
            ParseCompletedContent(remaining, batch, completed, onTranslation);
    }

    private static void ParseCompletedContent(
        string content,
        IReadOnlyList<PendingTranslation> batch,
        Dictionary<int, CompletedTranslation> completed,
        Action<CompletedTranslation> onTranslation)
    {
        var normalized = StripMarkdownFence(content);
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.TryGetProperty("translations", out var translations)
                && translations.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in translations.EnumerateArray())
                    TryAddCompletedElement(item, batch, completed, onTranslation);
                return;
            }
        }
        catch (JsonException)
        {
            // JSON Lines is intentionally not a single JSON document.
        }

        foreach (var line in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            TryAddCompletedLine(line, batch, completed, onTranslation);
    }

    private static void TryAddCompletedLine(
        string line,
        IReadOnlyList<PendingTranslation> batch,
        Dictionary<int, CompletedTranslation> completed,
        Action<CompletedTranslation> onTranslation)
    {
        if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
            return;
        try
        {
            using var document = JsonDocument.Parse(line);
            TryAddCompletedElement(document.RootElement, batch, completed, onTranslation);
        }
        catch (JsonException)
        {
            // A partial line stays in the stream buffer; non-JSON decoration is ignored.
        }
    }

    private static void TryAddCompletedElement(
        JsonElement item,
        IReadOnlyList<PendingTranslation> batch,
        Dictionary<int, CompletedTranslation> completed,
        Action<CompletedTranslation> onTranslation)
    {
        if (!item.TryGetProperty("id", out var idProperty)
            || !idProperty.TryGetInt32(out var id)
            || id < 0
            || id >= batch.Count
            || !item.TryGetProperty("text", out var textProperty)
            || textProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(textProperty.GetString())
            || completed.ContainsKey(id))
            return;

        var source = batch[id];
        var translation = new CompletedTranslation(source.Source, source.Key, textProperty.GetString()!.Trim());
        completed.Add(id, translation);
        onTranslation(translation);
    }

    private static void EnsureComplete(
        IReadOnlyList<PendingTranslation> batch,
        IReadOnlyDictionary<int, CompletedTranslation> completed)
    {
        if (completed.Count != batch.Count)
            throw new InvalidOperationException($"AI 返回的翻译条目不完整（需要 {batch.Count} 条，实际 {completed.Count} 条）。");
    }

    private static Usage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return default;

        return new Usage(
            ReadInt64(usage, "prompt_cache_hit_tokens"),
            ReadInt64(usage, "prompt_cache_miss_tokens"),
            ReadInt64(usage, "completion_tokens"));
    }

    private static long ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }

    private async Task<Dictionary<string, string>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        var path = CachePath;
        if (!File.Exists(path))
            return new(StringComparer.Ordinal);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
                ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "AI translation cache could not be read; starting with an empty cache");
            return new(StringComparer.Ordinal);
        }
    }

    private async Task SaveCacheAsync(Dictionary<string, string> cache, CancellationToken cancellationToken)
    {
        var path = CachePath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
                await JsonSerializer.SerializeAsync(stream, cache, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private string CachePath => Path.Combine(_settings.StorageDirectory, "AiTranslation", "cache.json");

    private static string TrimError(string body) => body.Length <= 500 ? body : body[..500];

    private sealed record PendingTranslation(int Id, string Source, string Key);

    private sealed record CompletedTranslation(string Source, string Key, string Translation);

    private sealed record CompletedBatch(
        IReadOnlyList<CompletedTranslation> Items,
        long PromptCacheHitTokens,
        long PromptCacheMissTokens,
        long CompletionTokens);

    private readonly record struct Usage(long PromptCacheHitTokens, long PromptCacheMissTokens, long CompletionTokens);

    public void Dispose()
    {
        _httpClient.Dispose();
        _cacheGate.Dispose();
    }
}

internal sealed record AiTranslationResult(
    IReadOnlyDictionary<string, string> Translations,
    int LocalCacheHits,
    int ApiTextCount,
    long PromptCacheHitTokens,
    long PromptCacheMissTokens,
    long CompletionTokens);

internal sealed record AiTranslationProgress(
    string Source,
    string Translation,
    int Completed,
    int Total,
    bool FromLocalCache);
