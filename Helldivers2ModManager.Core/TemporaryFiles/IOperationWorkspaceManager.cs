namespace Helldivers2ModManager.Core.TemporaryFiles;

public interface IOperationWorkspace : IDisposable
{
    Guid OperationId { get; }

    string DirectoryPath { get; }
}

public interface IOperationWorkspaceManager
{
    IOperationWorkspace Create(string rootDirectory, string purpose);

    int CleanupAbandoned(string rootDirectory);
}
