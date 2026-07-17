using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Authentication;
using System.Text;
using Helldivers2ModManager.Core.BrowserIntegration;
using Helldivers2ModManager.Core.Security;
using System.Diagnostics.CodeAnalysis;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class BrowserExtensionService : IDisposable
{
    private const int MaximumRequestBodyBytes = 64 * 1024;
    private readonly HttpListener _httpListener;
    private readonly ILogger<BrowserExtensionService> _logger;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly BackgroundTaskService _backgroundTaskService;
    private readonly ISafePathPolicy _safePathPolicy;
    private BrowserPairingCoordinator? _pairingCoordinator;
    private Task? _listenerTask;
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, CancellationTokenSource> _downloadCancellations = new();
    private readonly Dictionary<string, BackgroundTaskItem> _backgroundDownloadTasks = new();
    private readonly SemaphoreSlim _requestSemaphore = new(10, 10); // 限制最大并发请求数为10

    /// <summary>
    /// 下载任务持久化文件路径
    /// </summary>
    private string DownloadTasksFilePath => Path.Combine(_settingsService.StorageDirectory, "download_tasks.json");

    public bool IsListening { get; private set; }

    public event Action<DownloadTask>? DownloadStarted;
    public event Action<DownloadTask>? DownloadProgressChanged;
    public event Action<DownloadTask>? DownloadCompleted;
    public event Action<DownloadTask>? DownloadFailed;

    public ObservableCollection<DownloadTask> DownloadTasks { get; } = new();

    public BrowserExtensionService(
        ILogger<BrowserExtensionService> logger,
        SettingsService settingsService,
        LocalizationService localizationService,
        BackgroundTaskService backgroundTaskService,
        ISafePathPolicy safePathPolicy)
    {
        _logger = logger;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _backgroundTaskService = backgroundTaskService;
        _safePathPolicy = safePathPolicy;
        _httpListener = new HttpListener();

        // 加载持久化的下载任务
        LoadDownloadTasks();
    }

    public void Start()
    {
        if (IsListening)
            return;

        var port = _settingsService.ExtensionPort;

        if (port is < 1 or > 65535)
        {
            _logger.LogError("Browser extension service failed to start: Invalid port number {Port}", port);
            throw new InvalidOperationException($"Invalid port number: {port}. Must be between 1 and 65535");
        }

        _pairingCoordinator = new BrowserPairingCoordinator(
            _settingsService.BrowserExtensionTokenHash,
            _settingsService.BrowserExtensionOrigin);
        var ipv4Prefix = $"http://127.0.0.1:{port}/";
        var ipv6Prefix = $"http://[::1]:{port}/";
        _httpListener.Prefixes.Clear();
        _httpListener.Prefixes.Add(ipv4Prefix);
        _httpListener.Prefixes.Add(ipv6Prefix);

        _cts = new CancellationTokenSource();

        try
        {
            _httpListener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Failed to start loopback HttpListener. Error code: {ErrorCode}", ex.ErrorCode);

            if (ex.ErrorCode == 87)
            {
                throw new InvalidOperationException("Failed to start the loopback browser extension service.", ex);
            }

            throw;
        }

        IsListening = true;
        _logger.LogInformation("Browser extension service started on IPv4 and IPv6 loopback at port {Port}", port);

        _listenerTask = ListenAsync(_cts.Token);
    }

    public string GeneratePairingCode()
    {
        EnsurePairingCoordinator();
        return _pairingCoordinator.GeneratePairingCode(DateTimeOffset.UtcNow);
    }

    public async Task UnpairAsync(CancellationToken cancellationToken = default)
    {
        EnsurePairingCoordinator();
        _pairingCoordinator.Unpair();
        _settingsService.BrowserExtensionTokenHash = string.Empty;
        _settingsService.BrowserExtensionOrigin = string.Empty;
        await _settingsService.SaveAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Stop()
    {
        if (!IsListening)
            return;

        _cts?.Cancel();
        _httpListener.Stop();
        IsListening = false;
        _logger.LogInformation("Browser extension service stopped");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();

                // 使用信号量限制并发请求数
                await _requestSemaphore.WaitAsync(cancellationToken);

                ProcessRequestAsync(context, cancellationToken).Observe(
                    ex => _logger.LogError(ex, "Unhandled exception in ProcessRequestAsync"),
                    () => _requestSemaphore.Release());
            }
            catch (HttpListenerException ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "HttpListener exception");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception in listener");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        var request = context.Request;
        var origin = request.Headers["Origin"];
        try
        {
            if (request.RemoteEndPoint?.Address is { } remoteAddress && !remoteAddress.IsLocalAddress())
            {
                await SendJsonResponse(response, new { error = "Access denied" }, HttpStatusCode.Forbidden, origin);
                return;
            }

            var path = request.Url?.AbsolutePath ?? string.Empty;
            _logger.LogDebug("Received request: {Path}", path);

            if (request.HttpMethod == "OPTIONS")
            {
                HandlePreflight(response, path, origin);
                return;
            }

            if (path == "/api/v2/pair" && request.HttpMethod == "POST")
            {
                await HandlePairRequestAsync(context, cancellationToken);
                return;
            }

            if (path == "/api/v2/health" && request.HttpMethod == "GET")
            {
                if (!TryAuthenticateRequest(request, out var authError))
                {
                    await SendJsonResponse(response, new { error = authError }, HttpStatusCode.Unauthorized, origin);
                    return;
                }
                await SendJsonResponse(
                    response,
                    new { status = "ok", hasActiveDownloads = DownloadTasks.Any(t => t.Status == DownloadStatus.Downloading) },
                    HttpStatusCode.OK,
                    origin);
                return;
            }

            if (path == "/api/v2/downloads" && request.HttpMethod == "POST")
            {
                if (!TryAuthenticateRequest(request, out var authError))
                {
                    await SendJsonResponse(response, new { error = authError }, HttpStatusCode.Unauthorized, origin);
                    return;
                }
                await HandleDownloadRequest(context, cancellationToken);
                return;
            }

            if (path == "/api/v2/unpair" && request.HttpMethod == "POST")
            {
                if (!TryAuthenticateRequest(request, out var authError))
                {
                    await SendJsonResponse(response, new { error = authError }, HttpStatusCode.Unauthorized, origin);
                    return;
                }

                await UnpairAsync(cancellationToken);
                await SendJsonResponse(response, new { success = true }, HttpStatusCode.OK, origin);
                return;
            }

            await SendJsonResponse(response, new { error = "Not found" }, HttpStatusCode.NotFound, origin);
        }
        catch (RequestBodyTooLargeException)
        {
            await SendJsonResponse(response, new { error = "Request body too large" }, HttpStatusCode.RequestEntityTooLarge, origin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");
            if (response.OutputStream.CanWrite)
                await SendJsonResponse(response, new { error = "Internal server error" }, HttpStatusCode.InternalServerError, origin);
        }
    }

    private void HandlePreflight(HttpListenerResponse response, string path, string? origin)
    {
        EnsurePairingCoordinator();
        var isPairingOrigin = path == "/api/v2/pair" &&
            BrowserPairingCoordinator.TryNormalizeExtensionOrigin(origin, out _);
        var isPairedOrigin = BrowserPairingCoordinator.TryNormalizeExtensionOrigin(origin, out var normalizedOrigin) &&
            string.Equals(normalizedOrigin, _pairingCoordinator.PairedOrigin, StringComparison.Ordinal);
        if (!isPairingOrigin && !isPairedOrigin)
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            response.Close();
            return;
        }

        AddCorsHeaders(response, origin!);
        response.StatusCode = (int)HttpStatusCode.NoContent;
        response.Close();
    }

    private async Task HandlePairRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        EnsurePairingCoordinator();
        var origin = context.Request.Headers["Origin"];
        var body = await ReadLimitedBodyAsync(context.Request, cancellationToken);
        var request = JsonSerializer.Deserialize<PairRequest>(body);
        if (request is null || string.IsNullOrWhiteSpace(request.Code))
        {
            await SendJsonResponse(context.Response, new { error = "Pair.InvalidRequest" }, HttpStatusCode.BadRequest, origin);
            return;
        }

        var result = _pairingCoordinator.Pair(request.Code, origin, DateTimeOffset.UtcNow);
        if (!result.IsSuccess)
        {
            var status = result.ErrorCode == "Pair.RateLimited" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized;
            await SendJsonResponse(context.Response, new { error = result.ErrorCode }, status, origin);
            return;
        }

        _settingsService.BrowserExtensionTokenHash = _pairingCoordinator.TokenHash ?? string.Empty;
        _settingsService.BrowserExtensionOrigin = _pairingCoordinator.PairedOrigin ?? string.Empty;
        await _settingsService.SaveAsync();
        await SendJsonResponse(context.Response, new { token = result.BearerToken }, HttpStatusCode.OK, origin);
    }

    private bool TryAuthenticateRequest(HttpListenerRequest request, out string? errorCode)
    {
        EnsurePairingCoordinator();
        var authorization = request.Headers["Authorization"];
        var token = authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
            ? authorization["Bearer ".Length..]
            : null;
        if (!long.TryParse(request.Headers["X-Timestamp"], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var timestampMilliseconds) ||
            !Guid.TryParse(request.Headers["X-Request-Id"], out var requestId))
        {
            errorCode = "Auth.InvalidHeaders";
            return false;
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            errorCode = "Auth.InvalidHeaders";
            return false;
        }
        return _pairingCoordinator.Authenticate(
            token,
            request.Headers["Origin"],
            timestamp,
            requestId,
            DateTimeOffset.UtcNow,
            out errorCode);
    }

    private async Task HandleDownloadRequest(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var origin = request.Headers["Origin"];
            var body = await ReadLimitedBodyAsync(request, cancellationToken);

            var downloadRequest = JsonSerializer.Deserialize<DownloadRequest>(body);

            if (downloadRequest == null || string.IsNullOrWhiteSpace(downloadRequest.Url))
            {
                _logger.LogWarning("Invalid download request: null={IsNull}, url='{Url}'",
                    downloadRequest == null, downloadRequest?.Url ?? "null");
                await SendJsonResponse(response, new { error = "Invalid request" }, HttpStatusCode.BadRequest, origin);
                return;
            }

            // 安全校验：验证下载 URL 协议和域名
            if (!Uri.TryCreate(downloadRequest.Url, UriKind.Absolute, out var uri) || !NexusDownloadUrlPolicy.IsAllowed(uri))
            {
                _logger.LogWarning("Rejected browser download URL host {Host}", uri?.Host ?? "invalid");
                await SendJsonResponse(response, new { error = "Download.UrlNotAllowed" }, HttpStatusCode.BadRequest, origin);
                return;
            }

            _logger.LogInformation("Received download request for {Filename}: {Url}", downloadRequest.Filename, RedactUriForLog(uri));

            var downloadTask = new DownloadTask
            {
                Filename = downloadRequest.Filename,
                Url = downloadRequest.Url,
                Status = DownloadStatus.Pending
            };

            DownloadTasks.Add(downloadTask);
            DownloadStarted?.Invoke(downloadTask);
            SaveDownloadTasks();

            await SendJsonResponse(response, new { success = true, taskId = downloadTask.Id }, HttpStatusCode.Accepted, origin);

            // 启动下载任务，并记录未处理的异常
            ProcessDownloadAsync(downloadTask, cancellationToken).Observe(
                ex =>
                {
                    _logger.LogError(ex, "Unhandled exception in ProcessDownloadAsync for task {TaskId}", downloadTask.Id);
                    if (downloadTask.Status != DownloadStatus.Failed)
                    {
                        downloadTask.Status = DownloadStatus.Failed;
                        downloadTask.ErrorMessage = _localizationService["BrowserExt.DownloadError"];
                        downloadTask.Speed = 0;
                        downloadTask.EstimatedTimeRemaining = TimeSpan.Zero;
                        DownloadFailed?.Invoke(downloadTask);
                        SaveDownloadTasks();
                    }
                });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error");
            await SendJsonResponse(response, new { error = "Invalid JSON" }, HttpStatusCode.BadRequest, request.Headers["Origin"]);
        }
    }

    private async Task ProcessDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        var backgroundTask = CreateOrGetDownloadBackgroundTask(task);
        // 为每个下载任务创建独立的取消令牌，关联到服务级别的取消令牌
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _downloadCancellations[task.Id] = downloadCts;
        string? partialPath = null;

        try
        {
            task.Status = DownloadStatus.Downloading;
            task.MarkDownloadStarted();
            _backgroundTaskService.Update(backgroundTask, task.Filename, 0, false);

            // 安全校验：防止路径遍历
            var safeFilename = Path.GetFileName(task.Filename);
            if (string.IsNullOrWhiteSpace(safeFilename))
            {
                _logger.LogError("Invalid filename after sanitization: {Filename}", task.Filename);
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = _localizationService["BrowserExt.InvalidFilename"];
                _backgroundTaskService.Fail(backgroundTask, task.ErrorMessage);
                DownloadFailed?.Invoke(task);
                return;
            }

            var pendingDirectory = Path.Combine(_settingsService.StorageDirectory, "PendingImports");
            Directory.CreateDirectory(pendingDirectory);
            var queuedName = $"{task.Id}_{safeFilename}";
            var finalPath = _safePathPolicy.ResolveUnderRoot(pendingDirectory, queuedName);
            partialPath = _safePathPolicy.ResolveUnderRoot(pendingDirectory, queuedName + ".part");

            await DownloadFileWithProgressAsync(task.Url, partialPath, task, backgroundTask, downloadCts.Token);
            File.Move(partialPath, finalPath, overwrite: false);
            partialPath = null;
            task.LocalFilePath = finalPath;
            task.Status = DownloadStatus.AwaitingImport;
            task.Speed = 0;
            task.EstimatedTimeRemaining = TimeSpan.Zero;
            _logger.LogInformation("Browser download queued for explicit import confirmation: {Filename}", task.Filename);
            DownloadCompleted?.Invoke(task);
            _backgroundTaskService.Complete(backgroundTask, _localizationService["BrowserExt.DownloadQueued"]);

            SaveDownloadTasks();
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.Cancelled;
            task.ErrorMessage = _localizationService["BrowserExt.DownloadCancelled"];
            task.Speed = 0;
            task.EstimatedTimeRemaining = TimeSpan.Zero;
            _logger.LogInformation("Download cancelled: {Filename}", task.Filename);
            _backgroundTaskService.Cancel(backgroundTask, _localizationService["BrowserExt.DownloadCancelled"]);
            SaveDownloadTasks();
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.Speed = 0;
            task.EstimatedTimeRemaining = TimeSpan.Zero;
            _logger.LogError(ex, "Failed to download or import mod");
            DownloadFailed?.Invoke(task);
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            SaveDownloadTasks();
        }
        finally
        {
            if (partialPath is not null)
                CleanupTempFile(partialPath);
            _downloadCancellations.Remove(task.Id);
        }
    }

    private void CleanupTempFile(string tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try
            {
                File.Delete(tempPath);
                _logger.LogInformation("Cleaned up temporary download file: {Path}", tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temporary download file: {Path}", tempPath);
            }
        }
    }

    private async Task DownloadFileWithProgressAsync(string url, string savePath, DownloadTask task, BackgroundTaskItem backgroundTask, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" },
                { "Accept", "*/*" },
                { "Referer", "https://www.nexusmods.com/" }
            }
        };

        using var response = await SendWithValidatedRedirectsAsync(httpClient, new Uri(url), cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        task.UpdateProgress(0, totalBytes);

        await using var fileStream = new FileStream(
            savePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesRead += read;
            task.UpdateProgress(bytesRead, totalBytes);
            task.UpdateSpeed(bytesRead, totalBytes);
            DownloadProgressChanged?.Invoke(task);
            _backgroundTaskService.Update(
                backgroundTask,
                task.Filename,
                totalBytes > 0 ? (double)bytesRead / totalBytes : null,
                totalBytes <= 0);
        }
        await fileStream.FlushAsync(cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpClient httpClient,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!NexusDownloadUrlPolicy.IsAllowed(currentUri))
                throw new AuthenticationException("A download redirect targeted a disallowed URL.");

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if ((int)response.StatusCode is < 300 or >= 400)
                return response;

            if (redirects == 5 || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HttpRequestException("Download redirect limit exceeded or redirect location was missing.");
            }

            currentUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(currentUri, response.Headers.Location);
            response.Dispose();
        }

        throw new HttpRequestException("Download redirect limit exceeded.");
    }

    private BackgroundTaskItem CreateOrGetDownloadBackgroundTask(DownloadTask task)
    {
        if (_backgroundDownloadTasks.TryGetValue(task.Id, out var backgroundTask))
            return backgroundTask;

        backgroundTask = _backgroundTaskService.Add(
            _localizationService["BackgroundTasksPage.TaskTypeDownload"],
            task.Filename);
        _backgroundDownloadTasks[task.Id] = backgroundTask;
        return backgroundTask;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static async Task<string> ReadLimitedBodyAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength64 > MaximumRequestBodyBytes)
            throw new RequestBodyTooLargeException();

        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumRequestBodyBytes)
                throw new RequestBodyTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    [MemberNotNull(nameof(_pairingCoordinator))]
    private void EnsurePairingCoordinator()
    {
        _pairingCoordinator ??= new BrowserPairingCoordinator(
            _settingsService.BrowserExtensionTokenHash,
            _settingsService.BrowserExtensionOrigin);
    }

    private static void AddCorsHeaders(HttpListenerResponse response, string origin)
    {
        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers["Vary"] = "Origin";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Request-Id, X-Timestamp";
    }

    private static async Task SendJsonResponse(
        HttpListenerResponse response,
        object data,
        HttpStatusCode statusCode,
        string? origin)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        if (BrowserPairingCoordinator.TryNormalizeExtensionOrigin(origin, out var normalizedOrigin))
            AddCorsHeaders(response, normalizedOrigin!);

        var json = JsonSerializer.Serialize(data, s_jsonOptions);
        var buffer = System.Text.Encoding.UTF8.GetBytes(json);

        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    public void Dispose()
    {
        Stop();
        // 取消所有正在进行的下载
        foreach (var cts in _downloadCancellations.Values)
        {
            cts.Cancel();
        }
        _downloadCancellations.Clear();
        (_httpListener as IDisposable)?.Dispose();
        _cts?.Dispose();
    }

    /// <summary>
    /// 取消指定下载任务
    /// </summary>
    public bool CancelDownload(string taskId)
    {
        if (_downloadCancellations.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();
            _logger.LogInformation("Cancelling download task: {TaskId}", taskId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 重试失败的下载任务
    /// </summary>
    public async Task RetryDownloadAsync(DownloadTask task, CancellationToken cancellationToken = default)
    {
        if (task.Status != DownloadStatus.Failed && task.Status != DownloadStatus.Cancelled)
            return;

        // 移除旧任务
        DownloadTasks.Remove(task);

        // 创建新任务重新下载
        var newTask = new DownloadTask
        {
            Filename = task.Filename,
            Url = task.Url,
            Status = DownloadStatus.Pending
        };

        DownloadTasks.Add(newTask);
        DownloadStarted?.Invoke(newTask);

        await ProcessDownloadAsync(newTask, cancellationToken);
    }

    /// <summary>
    /// 移除指定的下载任务（仅限非下载中的任务）
    /// </summary>
    public bool RemoveDownloadTask(DownloadTask task)
    {
        if (task.Status == DownloadStatus.Downloading)
            return false;

        var removed = DownloadTasks.Remove(task);
        if (_backgroundDownloadTasks.Remove(task.Id, out var backgroundTask))
            _backgroundTaskService.Remove(backgroundTask);
        if (removed)
            SaveDownloadTasks();
        return removed;
    }

    /// <summary>
    /// 清除所有已完成、失败或取消的下载任务
    /// </summary>
    public void ClearCompletedTasks()
    {
        var completedTasks = DownloadTasks
            .Where(t => t.Status == DownloadStatus.Completed ||
                        t.Status == DownloadStatus.Failed ||
                        t.Status == DownloadStatus.Cancelled)
            .ToList();

        foreach (var task in completedTasks)
        {
            DownloadTasks.Remove(task);
            if (_backgroundDownloadTasks.Remove(task.Id, out var backgroundTask))
                _backgroundTaskService.Remove(backgroundTask);
        }

        SaveDownloadTasks();
    }

    /// <summary>
    /// 手动添加下载任务（通过 URL）
    /// </summary>
    /// <param name="url">下载链接（支持任意 HTTPS URL）</param>
    /// <returns>是否成功添加</returns>
    public bool AddManualDownload(string url)
    {
        // 验证 URL 格式
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Manual download rejected: URL is empty");
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("Manual download rejected: invalid URL format");
            return false;
        }

        // 安全校验：仅允许 HTTPS（防止明文传输泄露敏感信息）
        if (!NexusDownloadUrlPolicy.IsAllowed(uri))
        {
            _logger.LogWarning("Manual download rejected by URL policy: {Url}", RedactUriForLog(uri));
            return false;
        }

        // 从 URL 中提取文件名
        var filename = ExtractFilenameFromUrl(uri);

        _logger.LogInformation("Manual download added: {Filename} - {Url}", filename, RedactUriForLog(uri));

        var downloadTask = new DownloadTask
        {
            Filename = filename,
            Url = url.Trim(),
            Status = DownloadStatus.Pending
        };

        DownloadTasks.Add(downloadTask);
        DownloadStarted?.Invoke(downloadTask);
        SaveDownloadTasks();

        // 启动下载任务
        ProcessDownloadAsync(downloadTask, _cts?.Token ?? default).Observe(
            ex =>
            {
                _logger.LogError(ex, "Unhandled exception in manual download for task {TaskId}", downloadTask.Id);
                if (downloadTask.Status != DownloadStatus.Failed)
                {
                    downloadTask.Status = DownloadStatus.Failed;
                    downloadTask.ErrorMessage = _localizationService["BrowserExt.DownloadError"];
                    downloadTask.Speed = 0;
                    downloadTask.EstimatedTimeRemaining = TimeSpan.Zero;
                    DownloadFailed?.Invoke(downloadTask);
                    SaveDownloadTasks();
                }
            });

        return true;
    }

    /// <summary>
    /// 从 URL 中提取文件名
    /// </summary>
    private static string ExtractFilenameFromUrl(Uri uri)
    {
        // 尝试从路径中获取文件名
        var pathSegments = uri.Segments;
        if (pathSegments.Length > 0)
        {
            var lastSegment = pathSegments[^1];
            if (!string.IsNullOrWhiteSpace(lastSegment) && lastSegment.Contains('.'))
            {
                return Uri.UnescapeDataString(lastSegment);
            }
        }

        // 如果无法从路径提取，使用查询参数或生成默认文件名
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var fileId = query["id"] ?? query["file_id"] ?? Guid.NewGuid().ToString("N")[..8];
        return $"manual_download_{fileId}.zip";
    }

    private static string RedactUriForLog(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    /// <summary>
    /// 将下载任务保存到 JSON 文件，实现持久化
    /// </summary>
    private void SaveDownloadTasks()
    {
        try
        {
            var directory = Path.GetDirectoryName(DownloadTasksFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = DownloadTasks.Select(t => new DownloadTaskData
            {
                Id = t.Id,
                Filename = t.Filename,
                Url = t.Url,
                Status = t.Status,
                BytesDownloaded = t.BytesDownloaded,
                TotalBytes = t.TotalBytes,
                Progress = t.Progress,
                ErrorMessage = t.ErrorMessage,
                LocalFilePath = t.LocalFilePath
            }).ToList();

            var json = JsonSerializer.Serialize(data, s_jsonOptions);
            File.WriteAllText(DownloadTasksFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save download tasks");
        }
    }

    /// <summary>
    /// 从 JSON 文件加载下载任务
    /// </summary>
    private void LoadDownloadTasks()
    {
        try
        {
            if (!File.Exists(DownloadTasksFilePath))
                return;

            var json = File.ReadAllText(DownloadTasksFilePath);
            var data = JsonSerializer.Deserialize<List<DownloadTaskData>>(json, s_jsonOptions);

            if (data == null)
                return;

            foreach (var item in data)
            {
                // 重新打开时，之前正在下载/等待的任务标记为取消（无法恢复中断的下载）
                var status = item.Status;
                if (status == DownloadStatus.Downloading || status == DownloadStatus.Pending)
                {
                    status = DownloadStatus.Cancelled;
                }

                var task = new DownloadTask
                {
                    Id = item.Id,
                    Filename = item.Filename,
                    Url = item.Url,
                    Status = status,
                    BytesDownloaded = item.BytesDownloaded,
                    TotalBytes = item.TotalBytes,
                    Progress = item.Progress,
                    ErrorMessage = status == DownloadStatus.Cancelled ? _localizationService["BrowserExt.AppRestarted"] : item.ErrorMessage,
                    LocalFilePath = item.LocalFilePath
                };

                DownloadTasks.Add(task);
            }

            _logger.LogInformation("Loaded {Count} download tasks from storage", DownloadTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load download tasks");
        }
    }

    private sealed class DownloadRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    private sealed class PairRequest
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;
    }

    private sealed class RequestBodyTooLargeException : Exception
    {
    }

    /// <summary>
    /// 下载任务持久化数据模型，用于 JSON 序列化/反序列化
    /// </summary>
    private sealed class DownloadTaskData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        [JsonPropertyName("status")]
        public DownloadStatus Status { get; set; }
        [JsonPropertyName("bytesDownloaded")]
        public long BytesDownloaded { get; set; }
        [JsonPropertyName("totalBytes")]
        public long TotalBytes { get; set; }
        [JsonPropertyName("progress")]
        public double Progress { get; set; }
        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
        [JsonPropertyName("localFilePath")]
        public string? LocalFilePath { get; set; }
    }
}
