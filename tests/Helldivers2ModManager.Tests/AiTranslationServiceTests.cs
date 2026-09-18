using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Helldivers2ModManager.Services.AI;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AiTranslationServiceTests
{
    [TestMethod]
    public async Task TranslateAsyncUsesChatCompletionsAndCachesBySourceText()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-ai-translation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance);
            settings.InitDefault();
            settings.StorageDirectory = tempDir;
            settings.AiTranslationEndpoint = "https://example.test/chat/completions";
            settings.AiTranslationModel = "test-model";
            settings.AiTranslationTargetLanguage = "简体中文";
            settings.AiTranslationApiKey = "test-key";

            var handler = new RecordingHandler();
            using var service = new AiTranslationService(settings, NullLogger<AiTranslationService>.Instance, handler);
            var streamed = new List<AiTranslationProgress>();

            var first = await service.TranslateAsync(
                ["Original text"],
                progress: new InlineProgress<AiTranslationProgress>(streamed.Add));
            Assert.AreEqual("译文：Original text", first.Translations["Original text"]);
            Assert.AreEqual(0, first.LocalCacheHits);
            Assert.AreEqual(1, first.ApiTextCount);
            Assert.AreEqual(100, first.PromptCacheHitTokens);
            Assert.AreEqual(25, first.PromptCacheMissTokens);
            Assert.AreEqual(50, first.CompletionTokens);
            Assert.AreEqual(1, handler.RequestCount);
            Assert.AreEqual("Bearer", handler.AuthorizationScheme);
            Assert.AreEqual("test-key", handler.AuthorizationParameter);
            Assert.AreEqual("test-model", handler.Model);
            Assert.AreEqual("disabled", handler.ThinkingType);
            Assert.IsNull(handler.ReasoningEffort);
            Assert.AreEqual(1, streamed.Count);
            Assert.AreEqual("Original text", streamed[0].Source);

            settings.AiTranslationApiKey = null;
            var cached = await service.TranslateAsync(["Original text"]);
            Assert.AreEqual("译文：Original text", cached.Translations["Original text"]);
            Assert.AreEqual(1, cached.LocalCacheHits);
            Assert.AreEqual(0, cached.ApiTextCount);
            Assert.AreEqual(1, handler.RequestCount, "缓存命中时不应再次请求 API");

            settings.AiTranslationApiKey = "test-key";
            var changed = await service.TranslateAsync(["Changed text"]);
            Assert.AreEqual("译文：Changed text", changed.Translations["Changed text"]);
            Assert.AreEqual(2, handler.RequestCount, "原文变化后应生成新缓存并重新请求");
            Assert.IsTrue(File.Exists(Path.Combine(tempDir, "AiTranslation", "cache.json")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task TranslateAsyncBatchesManyTextsInsteadOfSendingOneRequestPerText()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-ai-translation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance);
            settings.InitDefault();
            settings.StorageDirectory = tempDir;
            settings.AiTranslationEndpoint = "https://example.test/chat/completions";
            settings.AiTranslationModel = "test-model";
            settings.AiTranslationTargetLanguage = "简体中文";
            settings.AiTranslationApiKey = "test-key";
            settings.AiTranslationEnableThinking = true;
            settings.AiTranslationReasoningEffort = "max";

            var handler = new RecordingHandler();
            using var service = new AiTranslationService(settings, NullLogger<AiTranslationService>.Instance, handler);
            var sources = Enumerable.Range(0, 30).Select(index => $"Option text {index}").ToArray();
            var streamed = new List<AiTranslationProgress>();

            var result = await service.TranslateAsync(
                sources,
                progress: new InlineProgress<AiTranslationProgress>(item =>
                {
                    lock (streamed)
                        streamed.Add(item);
                }));

            Assert.AreEqual(30, result.Translations.Count);
            Assert.AreEqual(2, handler.RequestCount, "30 段短文本应按每批最多 24 段合并成 2 次请求");
            Assert.AreEqual(0, result.LocalCacheHits);
            Assert.AreEqual(30, result.ApiTextCount);
            Assert.AreEqual(200, result.PromptCacheHitTokens);
            Assert.AreEqual(50, result.PromptCacheMissTokens);
            Assert.AreEqual(100, result.CompletionTokens);
            Assert.AreEqual("enabled", handler.ThinkingType);
            Assert.AreEqual("max", handler.ReasoningEffort);
            Assert.AreEqual(30, streamed.Count);
            foreach (var source in sources)
                Assert.AreEqual($"译文：{source}", result.Translations[source]);

            settings.AiTranslationReasoningEffort = "low";
            await service.TranslateAsync([sources[0]]);
            Assert.AreEqual(3, handler.RequestCount, "开启思考后切换推理强度不应复用旧强度的缓存");
            Assert.AreEqual("low", handler.ReasoningEffort);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? Model { get; private set; }
        public string? ThinkingType { get; private set; }
        public string? ReasoningEffort { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            Assert.AreEqual(new Uri("https://example.test/chat/completions"), request.RequestUri);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;

            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var requestDocument = JsonDocument.Parse(requestBody);
            Model = requestDocument.RootElement.GetProperty("model").GetString();
            var messages = requestDocument.RootElement.GetProperty("messages");
            Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
            Assert.AreEqual("user", messages[1].GetProperty("role").GetString());
            Assert.IsTrue(requestDocument.RootElement.GetProperty("stream").GetBoolean());
            Assert.IsTrue(requestDocument.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.IsTrue(requestDocument.RootElement.GetProperty("max_tokens").GetInt32() is >= 512 and <= 8192);
            ThinkingType = requestDocument.RootElement.GetProperty("thinking").GetProperty("type").GetString();
            ReasoningEffort = requestDocument.RootElement.TryGetProperty("reasoning_effort", out var reasoningEffort)
                ? reasoningEffort.GetString()
                : null;
            using var inputDocument = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            var inputItems = inputDocument.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => new
                {
                    Id = item.GetProperty("id").GetInt32(),
                    Text = item.GetProperty("text").GetString()
                })
                .ToArray();

            var sse = new StringBuilder();
            foreach (var item in inputItems)
            {
                var line = JsonSerializer.Serialize(new { id = item.Id, text = $"译文：{item.Text}" }) + "\n";
                var chunk = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = line } } }
                });
                sse.Append("data: ").Append(chunk).Append("\n\n");
            }

            var usageChunk = JsonSerializer.Serialize(new
            {
                choices = Array.Empty<object>(),
                usage = new
                {
                    prompt_tokens = 125,
                    prompt_cache_hit_tokens = 100,
                    prompt_cache_miss_tokens = 25,
                    completion_tokens = 50
                }
            });
            sse.Append("data: ").Append(usageChunk).Append("\n\n");
            sse.Append("data: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
