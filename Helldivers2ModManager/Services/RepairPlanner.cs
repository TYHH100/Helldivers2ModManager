using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Core.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton, Contract = typeof(IRepairPlanner))]
internal sealed class RepairPlanner(VersionCheckService legacyAnalyzer) : IRepairPlanner
{
    public async Task<OperationResult<RepairPlan>> PlanAsync(
        string patchPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(patchPath);
        if (!File.Exists(fullPath))
            return OperationResult.Failure<RepairPlan>("Repair.SourceNotFound");

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
            return OperationResult.Failure<RepairPlan>("Repair.SourceDirectoryMissing");

        var legacyPlan = await legacyAnalyzer
            .CreateRepairPlanAsync(new DirectoryInfo(directory), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (legacyPlan.BlockingReasons.Count > 0)
        {
            return OperationResult.Failure<RepairPlan>(
                "Repair.Blocked",
                string.Join(Environment.NewLine, legacyPlan.BlockingReasons));
        }

        var actions = legacyPlan.Actions
            .Where(action => string.Equals(
                Path.GetFullPath(action.PatchFilePath),
                fullPath,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static action => action.Offset)
            .Select(static action => new BinaryRepairAction(
                action.Offset,
                GetLittleEndianBytes(action.OldValue, action.Width),
                GetLittleEndianBytes(action.NewValue, action.Width)))
            .ToArray();
        if (actions.Length == 0)
            return OperationResult.Failure<RepairPlan>("Repair.NoActions");

        await using var source = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
        return OperationResult.Success(new RepairPlan(
            Guid.NewGuid(),
            fullPath,
            sourceHash,
            actions));
    }

    private static byte[] GetLittleEndianBytes(ulong value, int width)
    {
        if (width is not (1 or 2 or 4 or 8))
            throw new InvalidDataException($"Unsupported fixed-width repair value: {width} bytes.");

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes[..width].ToArray();
    }
}
