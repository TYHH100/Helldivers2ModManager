using CommunityToolkit.Mvvm.ComponentModel;

namespace Helldivers2ModManager.Models;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// 下载任务模型，记录单个下载任务的状态、进度、速度等信息
/// </summary>
public sealed partial class DownloadTask : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _filename = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private DownloadStatus _status = DownloadStatus.Pending;

    [ObservableProperty]
    private long _bytesDownloaded;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// 当前下载速度（字节/秒）
    /// </summary>
    [ObservableProperty]
    private double _speed;

    /// <summary>
    /// 预估剩余时间
    /// </summary>
    [ObservableProperty]
    private TimeSpan _estimatedTimeRemaining;

    /// <summary>
    /// 下载开始时间，用于计算速度
    /// </summary>
    private DateTime _downloadStartTime;

    /// <summary>
    /// 上次速度采样时的已下载字节数
    /// </summary>
    private long _lastSampleBytes;

    /// <summary>
    /// 上次速度采样时间
    /// </summary>
    private DateTime _lastSampleTime;

    public double ProgressPercent => Progress * 100;

    /// <summary>
    /// 预估剩余时间的可读文本（如 "2分30秒"）
    /// </summary>
    public string EstimatedTimeRemainingText
    {
        get
        {
            if (EstimatedTimeRemaining == TimeSpan.Zero || EstimatedTimeRemaining == TimeSpan.MaxValue)
                return string.Empty;
            if (EstimatedTimeRemaining.TotalHours >= 1)
                return $"{(int)EstimatedTimeRemaining.TotalHours}时{EstimatedTimeRemaining.Minutes}分";
            if (EstimatedTimeRemaining.TotalMinutes >= 1)
                return $"{(int)EstimatedTimeRemaining.TotalMinutes}分{EstimatedTimeRemaining.Seconds}秒";
            return $"{EstimatedTimeRemaining.Seconds}秒";
        }
    }

    public void UpdateProgress(long bytesDownloaded, long totalBytes)
    {
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        Progress = totalBytes > 0 ? (double)bytesDownloaded / totalBytes : 0;
        OnPropertyChanged(nameof(ProgressPercent));
    }

    /// <summary>
    /// 标记下载开始，初始化速度计算相关字段
    /// </summary>
    public void MarkDownloadStarted()
    {
        _downloadStartTime = DateTime.Now;
        _lastSampleTime = DateTime.Now;
        _lastSampleBytes = 0;
        Speed = 0;
        EstimatedTimeRemaining = TimeSpan.Zero;
    }

    /// <summary>
    /// 更新下载速度和预估剩余时间，应在下载进度变化时调用
    /// </summary>
    public void UpdateSpeed(long bytesDownloaded, long totalBytes)
    {
        var now = DateTime.Now;
        var elapsed = now - _lastSampleTime;

        // 每0.5秒采样一次，避免速度波动过大
        if (elapsed.TotalMilliseconds >= 500)
        {
            var bytesDiff = bytesDownloaded - _lastSampleBytes;
            var secondsElapsed = elapsed.TotalSeconds;

            if (secondsElapsed > 0)
            {
                // 使用移动平均平滑速度
                var instantSpeed = bytesDiff / secondsElapsed;
                Speed = Speed > 0 ? Speed * 0.7 + instantSpeed * 0.3 : instantSpeed;

                // 计算预估剩余时间
                if (Speed > 0 && totalBytes > 0)
                {
                    var remainingBytes = totalBytes - bytesDownloaded;
                    var remainingSeconds = remainingBytes / Speed;
                    if (remainingSeconds > 0 && remainingSeconds < 86400) // 不超过24小时
                        EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingSeconds);
                    else
                        EstimatedTimeRemaining = TimeSpan.Zero;
                }
                else
                {
                    EstimatedTimeRemaining = TimeSpan.Zero;
                }

                OnPropertyChanged(nameof(EstimatedTimeRemainingText));
            }

            _lastSampleTime = now;
            _lastSampleBytes = bytesDownloaded;
        }
    }

    /// <summary>
    /// 重置速度相关数据（用于重试下载时）
    /// </summary>
    public void ResetSpeed()
    {
        Speed = 0;
        EstimatedTimeRemaining = TimeSpan.Zero;
        _lastSampleBytes = 0;
        _lastSampleTime = DateTime.Now;
        OnPropertyChanged(nameof(EstimatedTimeRemainingText));
    }
}