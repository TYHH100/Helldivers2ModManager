using System.IO;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Core.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton, Contract = typeof(ICompanionRecoveryService))]
internal sealed class CompanionRecoveryService(VersionCheckService legacyRecovery) : ICompanionRecoveryService
{
    public async Task<OperationResult<int>> RecoverAsync(
        string modDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        var directory = new DirectoryInfo(Path.GetFullPath(modDirectory));
        if (!directory.Exists)
            return OperationResult.Failure<int>("Recovery.ModDirectoryNotFound");

        var result = await legacyRecovery
            .RecoverCompanionFilesAsync(directory, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? OperationResult.Success(result.RecoveredCount)
            : OperationResult.Failure<int>(
                "Recovery.Failed",
                result.ErrorMessage);
    }
}
