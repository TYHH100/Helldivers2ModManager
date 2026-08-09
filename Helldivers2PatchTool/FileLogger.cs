using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;

namespace Helldivers2PatchTool;

/// <summary>
/// 独立工具的文件日志：把扫描/修复整个流程写入 logs 目录，
/// 内置自动清理，最多保留 <see cref="MaxLogFiles"/> 个日志文件。
/// 不能直接复用主程序的 FileLogger（它依赖主程序 App.Current.LogLevel）。
/// </summary>
internal static class PatchToolLogging
{
    /// <summary>logs 目录中保留的最大日志文件数量。</summary>
    public const int MaxLogFiles = 5;

    /// <summary>创建带文件输出的日志工厂（默认记录 Debug 及以上，输出非常详细的扫描/修复流程）。</summary>
    public static LoggerFactory CreateFactory(LogLevel minLevel = LogLevel.Debug)
    {
        return new LoggerFactory(new ILoggerProvider[]
        {
            new PatchToolFileLoggerProvider("PatchTool", minLevel)
        });
    }

    /// <summary>
    /// 自动清理：logs 目录只保留最新的 <paramref name="maxFiles"/> 个 .log 文件。
    /// 清理失败不影响工具运行。
    /// </summary>
    public static void CleanExcessLogs(int maxFiles = MaxLogFiles)
    {
        try
        {
            var logDir = new DirectoryInfo("logs");
            if (!logDir.Exists)
                return;

            var filesToDelete = logDir
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ThenByDescending(static file => file.CreationTimeUtc)
                .Skip(maxFiles)
                .ToArray();

            foreach (var file in filesToDelete)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // 日志文件可能正被其他实例占用，忽略即可。
                }
            }
        }
        catch
        {
            // 日志清理是辅助功能，失败不应中断工具。
        }
    }
}

internal sealed class PatchToolFileLoggerProvider : ILoggerProvider
{
    private readonly FileStream _fileStream;
    private readonly StreamWriter _stream;
    private readonly object _lock = new();
    private readonly LogLevel _minLevel;

    public PatchToolFileLoggerProvider(string name, LogLevel minLevel)
    {
        _minLevel = minLevel;

        if (!Directory.Exists("logs"))
            Directory.CreateDirectory("logs");

        _fileStream = new FileStream(
            Path.Combine("logs", $"{name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        _stream = new StreamWriter(_fileStream)
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new PatchToolFileLogger(categoryName, _stream, _lock, _minLevel);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

internal sealed class PatchToolFileLogger(
    string name,
    StreamWriter stream,
    object lockObj,
    LogLevel minLevel) : ILogger
{
    private readonly string _name = name;
    private readonly StreamWriter _stream = stream;
    private readonly object _lock = lockObj;
    private readonly LogLevel _minLevel = minLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= _minLevel;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message))
            return;

        var builder = new StringBuilder();
        builder.Append('[');
        builder.Append(DateTime.Now.ToString("HH:mm:ss"));
        builder.Append("] ");
        builder.Append(_name);
        builder.Append(" -> ");
        builder.Append(logLevel.ToString());
        builder.Append(": ");
        builder.Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append('\t');
            builder.Append(exception.GetType().Name);
            builder.Append(": ");
            builder.Append(exception.Message);
            if (exception.StackTrace is not null)
            {
                builder.AppendLine();
                builder.Append("\t\t");
                builder.Append(exception.StackTrace.ReplaceLineEndings($"{Environment.NewLine}\t\t"));
            }
        }

        lock (_lock)
        {
            _stream.WriteLine(builder.ToString());
        }
    }
}
