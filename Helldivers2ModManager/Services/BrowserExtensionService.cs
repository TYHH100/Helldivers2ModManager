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

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class BrowserExtensionService : IDisposable
{
    private readonly HttpListener _httpListener;
    private readonly ILogger<BrowserExtensionService> _logger;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private Task? _listenerTask;
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, CancellationTokenSource> _downloadCancellations = new();
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

    public BrowserExtensionService(ILogger<BrowserExtensionService> logger, ModService modService, SettingsService settingsService)
    {
        _logger = logger;
        _modService = modService;
        _settingsService = settingsService;
        _httpListener = new HttpListener();

        // 加载持久化的下载任务
        LoadDownloadTasks();
    }

    public void Start()
    {
        if (IsListening)
            return;

        var host = _settingsService.ExtensionHost;
        var port = _settingsService.ExtensionPort;

        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogError("Browser extension service failed to start: ExtensionHost is empty");
            throw new InvalidOperationException("ExtensionHost cannot be empty");
        }

        if (port is < 1 or > 65535)
        {
            _logger.LogError("Browser extension service failed to start: Invalid port number {Port}", port);
            throw new InvalidOperationException($"Invalid port number: {port}. Must be between 1 and 65535");
        }

        if (host.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"', '\'' }) >= 0)
        {
            _logger.LogError("Browser extension service failed to start: ExtensionHost contains invalid characters");
            throw new InvalidOperationException("ExtensionHost contains invalid characters");
        }

        var prefix = $"http://{host}:{port}/";
        _httpListener.Prefixes.Clear();
        _httpListener.Prefixes.Add(prefix);

        _cts = new CancellationTokenSource();
        
        try
        {
            _httpListener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Failed to start HttpListener on {Prefix}. Error code: {ErrorCode}", prefix, ex.ErrorCode);
            
            if (ex.ErrorCode == 87)
            {
                throw new InvalidOperationException($"Failed to start browser extension service: Invalid prefix format '{prefix}'. Please check your ExtensionHost and ExtensionPort settings.", ex);
            }
            
            throw;
        }
        
        IsListening = true;
        _logger.LogInformation("Browser extension service started on {Prefix}", prefix);
        
        _listenerTask = ListenAsync(_cts.Token);
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
                
                _ = ProcessRequestAsync(context, cancellationToken)
                    .ContinueWith(t =>
                    {
                        _requestSemaphore.Release();
                        if (t.IsFaulted && t.Exception != null)
                        {
                            _logger.LogError(t.Exception, "Unhandled exception in ProcessRequestAsync");
                        }
                    });
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
        try
        {
            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            _logger.LogDebug("Received request: {Path}", path);

            if (path == "/api/download/health")
            {
                await SendJsonResponse(response, new { status = "ok", hasActiveDownloads = DownloadTasks.Any(t => t.Status == DownloadStatus.Downloading) }, HttpStatusCode.OK);
                return;
            }

            if (path == "/api/download/tasks")
            {
                var tasks = DownloadTasks.Select(t => new
                {
                    t.Id,
                    t.Filename,
                    t.Status,
                    t.Progress,
                    t.BytesDownloaded,
                    t.TotalBytes
                }).ToList();
                await SendJsonResponse(response, tasks, HttpStatusCode.OK);
                return;
            }

            if (path == "/api/download" && context.Request.HttpMethod == "POST")
            {
                await HandleDownloadRequest(context, cancellationToken);
                return;
            }

            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            response.Close();
        }
    }

    private async Task HandleDownloadRequest(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // 安全校验：验证请求来源（仅允许本地请求）
            var origin = request.Headers["Origin"];
            var referer = request.Headers["Referer"];
            var remoteEndPoint = request.RemoteEndPoint?.Address;
            
            // 检查是否为本地请求（localhost/127.0.0.1/::1）
            if (remoteEndPoint != null && !remoteEndPoint.IsLocalAddress())
            {
                _logger.LogWarning("Rejected non-local download request from {RemoteEndPoint}", remoteEndPoint);
                await SendJsonResponse(response, new { error = "Access denied" }, HttpStatusCode.Forbidden);
                return;
            }

            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            
            _logger.LogDebug("Request body: {Body}", body);
            
            var downloadRequest = JsonSerializer.Deserialize<DownloadRequest>(body);
            
            if (downloadRequest == null || string.IsNullOrWhiteSpace(downloadRequest.Url))
            {
                _logger.LogWarning("Invalid download request: null={IsNull}, url='{Url}'", 
                    downloadRequest == null, downloadRequest?.Url ?? "null");
                await SendJsonResponse(response, new { error = "Invalid request" }, HttpStatusCode.BadRequest);
                return;
            }

            // 安全校验：验证下载 URL 协议和域名
            if (!Uri.TryCreate(downloadRequest.Url, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Invalid download URL format: {Url}", downloadRequest.Url);
                await SendJsonResponse(response, new { error = "Invalid URL format" }, HttpStatusCode.BadRequest);
                return;
            }

            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Rejected non-HTTPS download URL: {Url}", downloadRequest.Url);
                await SendJsonResponse(response, new { error = "Only HTTPS URLs are allowed" }, HttpStatusCode.BadRequest);
                return;
            }

            // 限制域名为 Nexus Mods 相关域名
            var allowedHosts = new[] { "nexusmods.com", "www.nexusmods.com", "delivery.nexusmods.com" };
            if (!allowedHosts.Any(host => uri.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Rejected download URL from non-allowed host: {Host}", uri.Host);
                await SendJsonResponse(response, new { error = "Only Nexus Mods URLs are allowed" }, HttpStatusCode.BadRequest);
                return;
            }

            _logger.LogInformation("Received download request for {Filename}: {Url}", downloadRequest.Filename, downloadRequest.Url);

            var downloadTask = new DownloadTask
            {
                Filename = downloadRequest.Filename,
                Url = downloadRequest.Url,
                Status = DownloadStatus.Pending
            };

            DownloadTasks.Add(downloadTask);
            DownloadStarted?.Invoke(downloadTask);
            SaveDownloadTasks();

            await SendJsonResponse(response, new { success = true, taskId = downloadTask.Id }, HttpStatusCode.OK);

            // 启动下载任务，并记录未处理的异常
            _ = ProcessDownloadAsync(downloadTask, cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        _logger.LogError(t.Exception, "Unhandled exception in ProcessDownloadAsync for task {TaskId}", downloadTask.Id);
                        if (downloadTask.Status != DownloadStatus.Failed)
                        {
                            downloadTask.Status = DownloadStatus.Failed;
                            downloadTask.ErrorMessage = "下载过程中发生未预期的错误";
                            downloadTask.Speed = 0;
                            downloadTask.EstimatedTimeRemaining = TimeSpan.Zero;
                            DownloadFailed?.Invoke(downloadTask);
                            SaveDownloadTasks();
                        }
                    }
                });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error");
            await SendJsonResponse(response, new { error = "Invalid JSON", details = ex.Message }, HttpStatusCode.BadRequest);
        }
    }

    private async Task ProcessDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        // 为每个下载任务创建独立的取消令牌，关联到服务级别的取消令牌
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _downloadCancellations[task.Id] = downloadCts;

        try
        {
            task.Status = DownloadStatus.Downloading;
            task.MarkDownloadStarted();
            
            // 安全校验：防止路径遍历
            var safeFilename = Path.GetFileName(task.Filename);
            if (string.IsNullOrWhiteSpace(safeFilename))
            {
                _logger.LogError("Invalid filename after sanitization: {Filename}", task.Filename);
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Invalid filename";
                DownloadFailed?.Invoke(task);
                return;
            }
            
            var tempPath = Path.Combine(_settingsService.TempDirectory, safeFilename);
            
            // 验证最终路径是否在临时目录内
            var tempBasePath = Path.GetFullPath(_settingsService.TempDirectory);
            var tempFilePath = Path.GetFullPath(tempPath);
            if (!tempFilePath.StartsWith(tempBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && tempFilePath != tempBasePath)
            {
                _logger.LogError("Path traversal attempt detected in filename: {Filename}", task.Filename);
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Path traversal not allowed";
                DownloadFailed?.Invoke(task);
                return;
            }
            
            await DownloadFileWithProgressAsync(task.Url, tempPath, task, downloadCts.Token);
            
            var fileInfo = new FileInfo(tempPath);
            var problems = await _modService.TryAddModFromArchiveAsync(fileInfo);
            
            // 清理临时下载文件
            CleanupTempFile(tempPath);
            
            var hasOnlyNoManifestIssue = problems.Length == 1 && problems[0].Kind == ModProblemKind.NoManifestFound;
            
            if (problems.Length == 0 || hasOnlyNoManifestIssue)
            {
                task.Status = DownloadStatus.Completed;
                task.Speed = 0;
                task.EstimatedTimeRemaining = TimeSpan.Zero;
                
                if (hasOnlyNoManifestIssue)
                {
                    task.ErrorMessage = "档案文件已自动生成";
                    _logger.LogInformation("Mod downloaded and imported successfully (manifest auto-generated): {Filename}", task.Filename);
                }
                else
                {
                    _logger.LogInformation("Mod downloaded and imported successfully: {Filename}", task.Filename);
                }
                
                DownloadCompleted?.Invoke(task);
            }
            else
            {
                var errorMessages = problems.Select(p => p.Kind.ToString()).ToArray();
                task.ErrorMessage = string.Join(", ", errorMessages);
                task.Status = DownloadStatus.Failed;
                task.Speed = 0;
                task.EstimatedTimeRemaining = TimeSpan.Zero;
                _logger.LogWarning("Mod import completed with issues: {Errors}", string.Join(", ", errorMessages));
                DownloadFailed?.Invoke(task);
            }

            SaveDownloadTasks();
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.Cancelled;
            task.ErrorMessage = "下载已取消";
            task.Speed = 0;
            task.EstimatedTimeRemaining = TimeSpan.Zero;
            _logger.LogInformation("Download cancelled: {Filename}", task.Filename);
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
            SaveDownloadTasks();
        }
        finally
        {
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

    private async Task DownloadFileWithProgressAsync(string url, string savePath, DownloadTask task, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var httpClient = new HttpClient 
        { 
            Timeout = TimeSpan.FromMinutes(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" },
                { "Accept", "*/*" },
                { "Referer", "https://www.nexusmods.com/" }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Referer", "https://www.nexusmods.com/");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        task.UpdateProgress(0, totalBytes);

        using var fileStream = File.Create(savePath);
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
            bytesRead += read;
            task.UpdateProgress(bytesRead, totalBytes);
            task.UpdateSpeed(bytesRead, totalBytes);
            DownloadProgressChanged?.Invoke(task);
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private async Task SendJsonResponse(HttpListenerResponse response, object data, HttpStatusCode statusCode)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        
        var json = JsonSerializer.Serialize(data, s_jsonOptions);
        var buffer = System.Text.Encoding.UTF8.GetBytes(json);
        
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
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
            _logger.LogWarning("Manual download rejected: Invalid URL format: {Url}", url);
            return false;
        }

        // 安全校验：仅允许 HTTPS（防止明文传输泄露敏感信息）
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Manual download rejected: Non-HTTPS URL: {Url}", url);
            return false;
        }

        // 从 URL 中提取文件名
        var filename = ExtractFilenameFromUrl(uri);
        
        _logger.LogInformation("Manual download added: {Filename} - {Url}", filename, url);

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
        _ = ProcessDownloadAsync(downloadTask, _cts?.Token ?? default)
            .ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.LogError(t.Exception, "Unhandled exception in manual download for task {TaskId}", downloadTask.Id);
                    if (downloadTask.Status != DownloadStatus.Failed)
                    {
                        downloadTask.Status = DownloadStatus.Failed;
                        downloadTask.ErrorMessage = "下载过程中发生未预期的错误";
                        downloadTask.Speed = 0;
                        downloadTask.EstimatedTimeRemaining = TimeSpan.Zero;
                        DownloadFailed?.Invoke(downloadTask);
                        SaveDownloadTasks();
                    }
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
                ErrorMessage = t.ErrorMessage
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
                    ErrorMessage = status == DownloadStatus.Cancelled ? "应用重启，下载已中断" : item.ErrorMessage
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
    }
}