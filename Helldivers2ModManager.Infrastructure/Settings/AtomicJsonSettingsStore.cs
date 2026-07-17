using System.Text.Json;
using Helldivers2ModManager.Core.Settings;

namespace Helldivers2ModManager.Infrastructure.Settings;

public sealed class AtomicJsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly string? _legacySettingsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public AtomicJsonSettingsStore(string settingsPath, string? legacySettingsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _legacySettingsPath = string.IsNullOrWhiteSpace(legacySettingsPath)
            ? null
            : Path.GetFullPath(legacySettingsPath);
    }

    public async Task<AppSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MigrateLegacyFileIfNeeded();
            if (!File.Exists(_settingsPath))
                return new AppSettingsSnapshot();

            try
            {
                return await ReadDocumentAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) when (File.Exists(BackupPath))
            {
                return await ReadDocumentAsync(BackupPath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("The settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            try
            {
                await WriteTemporaryFileAsync(snapshot, cancellationToken).ConfigureAwait(false);
                if (File.Exists(_settingsPath))
                {
                    File.Replace(TemporaryPath, _settingsPath, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(TemporaryPath, _settingsPath);
                    File.Copy(_settingsPath, BackupPath, overwrite: true);
                }
            }
            finally
            {
                File.Delete(TemporaryPath);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _writeLock.Dispose();
        _disposed = true;
    }

    private string TemporaryPath => _settingsPath + ".tmp";

    private string BackupPath => _settingsPath + ".bak";

    private async Task WriteTemporaryFileAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            TemporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, snapshot, s_options, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<AppSettingsSnapshot> ReadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<AppSettingsSnapshot>(stream, s_options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException("The settings document is empty.");
    }

    private void MigrateLegacyFileIfNeeded()
    {
        if (File.Exists(_settingsPath) || _legacySettingsPath is null || !File.Exists(_legacySettingsPath))
            return;

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        File.Copy(_legacySettingsPath, _legacySettingsPath + ".pre-v2.bak", overwrite: false);
        File.Copy(_legacySettingsPath, _settingsPath, overwrite: false);
    }
}
