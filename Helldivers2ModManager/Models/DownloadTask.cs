using CommunityToolkit.Mvvm.ComponentModel;

namespace Helldivers2ModManager.Models;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Completed,
    Failed
}

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

    public double ProgressPercent => Progress * 100;

    public void UpdateProgress(long bytesDownloaded, long totalBytes)
    {
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        Progress = totalBytes > 0 ? (double)bytesDownloaded / totalBytes : 0;
        OnPropertyChanged(nameof(ProgressPercent));
    }
}