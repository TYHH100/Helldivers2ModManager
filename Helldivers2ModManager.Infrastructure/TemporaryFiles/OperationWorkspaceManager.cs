using System.Text.Json;
using Helldivers2ModManager.Core.TemporaryFiles;

namespace Helldivers2ModManager.Infrastructure.TemporaryFiles;

public sealed class OperationWorkspaceManager : IOperationWorkspaceManager
{
    private const string ApplicationId = "Helldivers2ModManager";
    private const string DirectoryPrefix = "hd2mm_";
    private const string OwnerFileName = ".hd2mm-owner.json";
    private const string LockFileName = ".hd2mm.lock";
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public IOperationWorkspace Create(string rootDirectory, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        Directory.CreateDirectory(rootDirectory);
        var operationId = Guid.NewGuid();
        var directory = Path.Combine(rootDirectory, DirectoryPrefix + operationId.ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var owner = new WorkspaceOwner(
                ApplicationId,
                operationId,
                Environment.ProcessId,
                purpose,
                DateTimeOffset.UtcNow);
            File.WriteAllText(
                Path.Combine(directory, OwnerFileName),
                JsonSerializer.Serialize(owner, s_jsonOptions));
            var lockStream = new FileStream(
                Path.Combine(directory, LockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            var payloadDirectory = Path.Combine(directory, "payload");
            Directory.CreateDirectory(payloadDirectory);
            return new OperationWorkspace(operationId, payloadDirectory, directory, lockStream);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public int CleanupAbandoned(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
            return 0;

        var deleted = 0;
        foreach (var directory in Directory.EnumerateDirectories(rootDirectory, DirectoryPrefix + "*"))
        {
            if (!IsOwnedWorkspace(directory))
                continue;

            FileStream? lockProbe = null;
            try
            {
                var lockPath = Path.Combine(directory, LockFileName);
                lockProbe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                lockProbe.Dispose();
                lockProbe = null;
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (IOException)
            {
                // An active process still owns this workspace.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave uncertain ownership/state untouched.
            }
            finally
            {
                lockProbe?.Dispose();
            }
        }
        return deleted;
    }

    private static bool IsOwnedWorkspace(string directory)
    {
        try
        {
            var ownerPath = Path.Combine(directory, OwnerFileName);
            if (!File.Exists(ownerPath) || !File.Exists(Path.Combine(directory, LockFileName)))
                return false;
            var owner = JsonSerializer.Deserialize<WorkspaceOwner>(File.ReadAllText(ownerPath));
            return owner is not null &&
                string.Equals(owner.ApplicationId, ApplicationId, StringComparison.Ordinal) &&
                directory.EndsWith(owner.OperationId.ToString("N"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record WorkspaceOwner(
        string ApplicationId,
        Guid OperationId,
        int ProcessId,
        string Purpose,
        DateTimeOffset CreatedAtUtc);

    private sealed class OperationWorkspace : IOperationWorkspace
    {
        private FileStream? _lockStream;
        private readonly string _workspaceRoot;

        public OperationWorkspace(
            Guid operationId,
            string directoryPath,
            string workspaceRoot,
            FileStream lockStream)
        {
            OperationId = operationId;
            DirectoryPath = directoryPath;
            _workspaceRoot = workspaceRoot;
            _lockStream = lockStream;
        }

        public Guid OperationId { get; }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            if (_lockStream is null)
                return;
            _lockStream.Dispose();
            _lockStream = null;
            if (Directory.Exists(_workspaceRoot))
                Directory.Delete(_workspaceRoot, recursive: true);
        }
    }
}
