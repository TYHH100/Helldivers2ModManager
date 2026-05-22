using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

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

    public bool IsListening { get; private set; }

    public event Action<ModData>? ModDownloaded;

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
                _ = ProcessRequestAsync(context, cancellationToken);
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

            _logger.LogInformation("Received download request for {Filename}: {Url}", downloadRequest.Filename, downloadRequest.Url);

            var downloadTask = new DownloadTask
            {
                Filename = downloadRequest.Filename,
                Url = downloadRequest.Url,
                Status = DownloadStatus.Pending
            };

            DownloadTasks.Add(downloadTask);
            DownloadStarted?.Invoke(downloadTask);

            await SendJsonResponse(response, new { success = true, taskId = downloadTask.Id }, HttpStatusCode.OK);

            _ = ProcessDownloadAsync(downloadTask, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON parsing error");
            await SendJsonResponse(response, new { error = "Invalid JSON", details = ex.Message }, HttpStatusCode.BadRequest);
        }
    }

    private async Task ProcessDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        try
        {
            task.Status = DownloadStatus.Downloading;
            
            var tempPath = Path.Combine(_settingsService.TempDirectory, task.Filename);
            
            await DownloadFileWithProgressAsync(task.Url, tempPath, task, cancellationToken);
            
            var fileInfo = new FileInfo(tempPath);
            var problems = await _modService.TryAddModFromArchiveAsync(fileInfo);
            
            var hasOnlyNoManifestIssue = problems.Length == 1 && problems[0].Kind == ModProblemKind.NoManifestFound;
            
            if (problems.Length == 0 || hasOnlyNoManifestIssue)
            {
                task.Status = DownloadStatus.Completed;
                
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
                _logger.LogWarning("Mod import completed with issues: {Errors}", string.Join(", ", errorMessages));
                DownloadFailed?.Invoke(task);
            }
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to download or import mod");
            DownloadFailed?.Invoke(task);
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
        (_httpListener as IDisposable)?.Dispose();
        _cts?.Dispose();
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
}