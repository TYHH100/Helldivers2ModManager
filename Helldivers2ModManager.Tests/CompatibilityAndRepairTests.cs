using System.Security.Cryptography;
using System.Diagnostics;
using System.Buffers.Binary;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Infrastructure.Compatibility;
using Helldivers2ModManager.Infrastructure.Settings;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class CompatibilityAndRepairTests
{
    [Fact]
    public void MissingGameReferenceAlwaysProducesUnknownInsteadOfModMajorityGuess()
    {
        var evaluator = new CompatibilityEvaluator();
        var scan = new PatchScanResult(
            [new PatchUnitObservation(1, 42, "mod.patch_0", 100, 64)],
            []);
        var unavailable = new GameReferenceSnapshot(ReferenceSource.Unavailable, null,
            new Dictionary<long, GameUnitReference>(), "GameData.Unavailable");

        var result = evaluator.Evaluate(scan, unavailable);

        Assert.Equal(CompatibilityState.Unknown, result.State);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void CurrentGameUnitVersionIsTheAuthoritativeCompatibilitySource()
    {
        var evaluator = new CompatibilityEvaluator();
        var reference = new GameReferenceSnapshot(
            ReferenceSource.CurrentGameFiles,
            "fingerprint",
            new Dictionary<long, GameUnitReference> { [1] = new(1, 43, "unit") });
        var scan = new PatchScanResult(
            [new PatchUnitObservation(1, 42, "mod.patch_0", 100, 64)],
            []);

        var result = evaluator.Evaluate(scan, reference);

        Assert.Equal(CompatibilityState.Incompatible, result.State);
        Assert.Equal(ReferenceSource.CurrentGameFiles, result.ReferenceSource);
        Assert.Single(result.VersionIssues);
        Assert.Same(scan.Units, result.Observations);
        Assert.Single(result.ReferenceVersions!);
        Assert.Equal((uint)43, result.ReferenceVersions![1]);
    }

    [Fact]
    public async Task RepairExecutorStreamsToVerifiedBackupAndAtomicallyReplacesSource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "sample.patch_0");
        var data = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(data);
        await File.WriteAllBytesAsync(sourcePath, data);
        var expected = data.AsSpan(4_000_000, 4).ToArray();
        var replacement = new byte[] { 1, 2, 3, 4 };
        var expectedSourceHash = Convert.ToHexString(SHA256.HashData(data));
        data.AsSpan(4_000_000, 4).Clear();
        replacement.CopyTo(data, 4_000_000);
        var expectedOutputHash = Convert.ToHexString(SHA256.HashData(data));
        var plan = new RepairPlan(
            Guid.NewGuid(),
            sourcePath,
            expectedSourceHash,
            [new BinaryRepairAction(4_000_000, expected, replacement)],
            expectedOutputHash);
        var executor = new TransactionalBinaryRepairExecutor(new FileSystemBackupStore());

        var result = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        await using var repairedStream = File.OpenRead(sourcePath);
        Assert.Equal(expectedOutputHash, Convert.ToHexString(await SHA256.HashDataAsync(repairedStream)));
        Assert.Single(Directory.EnumerateFiles(temporaryDirectory.Path, "*.hd2mm-backup"));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.tmp"));
    }

    [Fact]
    public async Task RepairExecutorRefusesChangedSourceWithoutCreatingBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(temporaryDirectory.Path, "changed.patch_0");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
        var executor = new TransactionalBinaryRepairExecutor(new FileSystemBackupStore());
        var plan = new RepairPlan(Guid.NewGuid(), sourcePath, new string('0', 64), []);

        var result = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal("Repair.SourceChanged", result.ErrorCode);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.hd2mm-backup"));
    }

    [Fact]
    public async Task BatchRepairRollsBackEarlierFilesWhenLaterCommitFails()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var firstPath = Path.Combine(temporaryDirectory.Path, "first.bin");
        var secondPath = Path.Combine(temporaryDirectory.Path, "second.bin");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(secondPath, [5, 6, 7, 8]);
        var plans = new[]
        {
            new RepairPlan(
                Guid.NewGuid(),
                firstPath,
                Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])),
                [new BinaryRepairAction(0, [1], [9])]),
            new RepairPlan(
                Guid.NewGuid(),
                secondPath,
                Convert.ToHexString(SHA256.HashData([5, 6, 7, 8])),
                [new BinaryRepairAction(0, [5], [10])])
        };
        await using var commitBlocker = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var executor = new TransactionalBinaryRepairExecutor(new FileSystemBackupStore());

        var result = await executor.ExecuteBatchAsync(plans, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(firstPath));
        Assert.Equal([5, 6, 7, 8], await File.ReadAllBytesAsync(secondPath));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TwoGiBSparsePatchRepairKeepsWorkingSetBounded()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporaryDirectory.Path, "sparse.patch_0");
        const long fileLength = 2L * 1024 * 1024 * 1024 + 4096;
        var actionOffset = fileLength - 32;
        await using (var stream = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(fileLength);
            stream.Position = actionOffset;
            await stream.WriteAsync(new byte[] { 10, 20, 30, 40 });
        }

        string sourceHash;
        await using (var stream = File.OpenRead(sourcePath))
            sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));

        var baseline = Process.GetCurrentProcess().WorkingSet64;
        var maximum = baseline;
        var progress = new InlineProgress<Helldivers2ModManager.Core.Operations.OperationProgress>(_ =>
            maximum = Math.Max(maximum, Process.GetCurrentProcess().WorkingSet64));
        var plan = new RepairPlan(
            Guid.NewGuid(),
            sourcePath,
            sourceHash,
            [new BinaryRepairAction(actionOffset, [10, 20, 30, 40], [40, 30, 20, 10])]);
        var executor = new TransactionalBinaryRepairExecutor(new FileSystemBackupStore());

        var result = await executor.ExecuteAsync(plan, progress, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(fileLength, new FileInfo(sourcePath).Length);
        await using var repaired = File.OpenRead(sourcePath);
        repaired.Position = actionOffset;
        var bytes = new byte[4];
        await repaired.ReadExactlyAsync(bytes);
        Assert.Equal([40, 30, 20, 10], bytes);
        Assert.True(maximum - baseline < 256L * 1024 * 1024,
            $"Working set grew by {(maximum - baseline) / (1024 * 1024)} MiB.");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.tmp"));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    [Fact]
    public async Task PlannerConvertsLegacyAnalysisIntoVerifiedTransactionalActions()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var patchPath = Path.Combine(temporaryDirectory.Path, "metadata.patch_0");
        var patch = new byte[200];
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(0, 4), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(80, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(88, 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(96, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(100, 4), 16);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(104, 8), 123);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(112, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(120, 8), 184);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(160, 4), 16);
        await File.WriteAllBytesAsync(patchPath, patch);

        using var settingsStore = new AtomicJsonSettingsStore(Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsStore);
        settings.InitDefault();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance);
        var legacyAnalyzer = new VersionCheckService(
            NullLogger<VersionCheckService>.Instance,
            settings,
            localization);
        var planner = new RepairPlanner(legacyAnalyzer);

        var planning = await planner.PlanAsync(patchPath, CancellationToken.None);

        Assert.True(planning.IsSuccess, planning.ErrorMessage);
        var plan = Assert.IsType<RepairPlan>(planning.Value);
        var action = Assert.Single(plan.Actions);
        Assert.Equal(180, action.Offset);
        Assert.Equal([0, 0, 0, 0], action.ExpectedBytes);
        Assert.Equal([1, 0, 0, 0], action.ReplacementBytes);

        var executor = new TransactionalBinaryRepairExecutor(new FileSystemBackupStore());
        var execution = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.True(execution.IsSuccess, execution.ErrorMessage);
        var repaired = await File.ReadAllBytesAsync(patchPath);
        Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(180, 4)));
        var history = await legacyAnalyzer.GetBackupHistoryAsync(new DirectoryInfo(temporaryDirectory.Path));
        Assert.Single(history.Entries);
    }

    [Fact]
    public async Task CompanionRecoveryReturnsPredictableFailureForMissingModDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var settingsStore = new AtomicJsonSettingsStore(Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsStore);
        settings.InitDefault();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance);
        var legacyRecovery = new VersionCheckService(
            NullLogger<VersionCheckService>.Instance,
            settings,
            localization);
        var recovery = new CompanionRecoveryService(legacyRecovery);

        var result = await recovery.RecoverAsync(
            Path.Combine(temporaryDirectory.Path, "missing"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Recovery.ModDirectoryNotFound", result.ErrorCode);
    }

    [Fact]
    public async Task BatchCoordinatorExecutesMetadataRepairThroughTransactionalExecutor()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var patchPath = Path.Combine(temporaryDirectory.Path, "batch.patch_0");
        var patch = new byte[200];
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(0, 4), unchecked((int)0xF0000011));
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(80, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(88, 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(96, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(100, 4), 16);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(104, 8), 123);
        BinaryPrimitives.WriteInt64LittleEndian(patch.AsSpan(112, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(120, 8), 184);
        BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(160, 4), 16);
        await File.WriteAllBytesAsync(patchPath, patch);

        using var settingsStore = new AtomicJsonSettingsStore(Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsStore);
        settings.InitDefault();
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance);
        var legacy = new VersionCheckService(NullLogger<VersionCheckService>.Instance, settings, localization);
        var planner = new RepairPlanner(legacy);
        var executor = new TransactionalBinaryRepairExecutor(new FileSystemBackupStore());
        var coordinator = new BatchRepairCoordinator(
            NullLogger<BatchRepairCoordinator>.Instance,
            localization,
            legacy,
            planner,
            executor,
            new CompanionRecoveryService(legacy));
        var item = new BatchModRepairItem
        {
            ModName = "Synthetic",
            ModDirectory = temporaryDirectory.Path,
            State = BatchModRepairState.Repairable
        };

        var result = await coordinator.ExecuteAsync(
            new BatchModRepairPlan { Items = [item] },
            null,
            CancellationToken.None);

        Assert.Same(item, Assert.Single(result.Items));
        Assert.Equal(BatchModRepairState.Repaired, item.State);
        Assert.Equal(1, item.MetadataActionCount);
        var repaired = await File.ReadAllBytesAsync(patchPath);
        Assert.Equal((uint)1, BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(180, 4)));
        Assert.Single(Directory.EnumerateFiles(temporaryDirectory.Path, "*.hd2mm-backup"));
    }
}
