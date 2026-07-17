using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Infrastructure.Mods;
using Helldivers2ModManager.Infrastructure.Security;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class TransactionalModImportServiceTests
{
    [Fact]
    public async Task NewImportBecomesVisibleOnlyAfterStagingAndCanBeFinalized()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var source = CreateSource(temporaryDirectory.Path, "source", Guid.NewGuid(), "New Mod", "new");
        var modsRoot = System.IO.Path.Combine(temporaryDirectory.Path, "Mods");
        var service = new TransactionalModImportService(new SafePathPolicy());
        var modId = ReadId(source);

        var plan = await service.PlanImportAsync(modId, "New Mod", source, modsRoot, CancellationToken.None);
        Assert.True(plan.IsSuccess);
        var commit = await service.CommitImportAsync(plan.Value!, updateConfirmed: false, null, CancellationToken.None);

        Assert.True(commit.IsSuccess, commit.ErrorMessage);
        Assert.Equal("new", await File.ReadAllTextAsync(
            System.IO.Path.Combine(modsRoot, "New Mod", "content.txt"), CancellationToken.None));
        await service.CompleteImportAsync(plan.Value!, commit: true, CancellationToken.None);
        Assert.False(Directory.Exists(System.IO.Path.Combine(modsRoot, ".transactions", plan.Value!.OperationId.ToString("N"))));
    }

    [Fact]
    public async Task SameGuidUpdateRequiresConfirmationAndRollbackRestoresOldDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modId = Guid.NewGuid();
        var modsRoot = System.IO.Path.Combine(temporaryDirectory.Path, "Mods");
        CreateSource(modsRoot, "Existing", modId, "Existing", "old");
        var source = CreateSource(temporaryDirectory.Path, "update", modId, "Renamed", "new");
        var service = new TransactionalModImportService(new SafePathPolicy());
        var plan = await service.PlanImportAsync(modId, "Renamed", source, modsRoot, CancellationToken.None);

        Assert.Equal(ModImportConflict.SameGuidUpdate, plan.Value!.Conflict);
        var rejected = await service.CommitImportAsync(plan.Value, updateConfirmed: false, null, CancellationToken.None);
        Assert.Equal("Import.UpdateConfirmationRequired", rejected.ErrorCode);
        var committed = await service.CommitImportAsync(plan.Value, updateConfirmed: true, null, CancellationToken.None);
        Assert.True(committed.IsSuccess);
        Assert.Equal("new", await File.ReadAllTextAsync(
            System.IO.Path.Combine(modsRoot, "Existing", "content.txt"), CancellationToken.None));

        await service.CompleteImportAsync(plan.Value, commit: false, CancellationToken.None);

        Assert.Equal("old", await File.ReadAllTextAsync(
            System.IO.Path.Combine(modsRoot, "Existing", "content.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task SameNameWithDifferentGuidIsAConflictAndNeverOverwrites()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modsRoot = System.IO.Path.Combine(temporaryDirectory.Path, "Mods");
        CreateSource(modsRoot, "Collision", Guid.NewGuid(), "Collision", "old");
        var newId = Guid.NewGuid();
        var source = CreateSource(temporaryDirectory.Path, "source", newId, "Collision", "new");
        var service = new TransactionalModImportService(new SafePathPolicy());

        var plan = await service.PlanImportAsync(newId, "Collision", source, modsRoot, CancellationToken.None);
        var result = await service.CommitImportAsync(plan.Value!, updateConfirmed: true, null, CancellationToken.None);

        Assert.Equal(ModImportConflict.NameConflict, plan.Value!.Conflict);
        Assert.Equal("Import.NameConflict", result.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(
            System.IO.Path.Combine(modsRoot, "Collision", "content.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task StartupRecoveryRollsBackActivatedButUnfinalizedImport()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modsRoot = System.IO.Path.Combine(temporaryDirectory.Path, "Mods");
        var modId = Guid.NewGuid();
        CreateSource(modsRoot, "Existing", modId, "Existing", "old");
        var source = CreateSource(temporaryDirectory.Path, "update", modId, "Existing", "new");
        var service = new TransactionalModImportService(new SafePathPolicy());
        var plan = await service.PlanImportAsync(modId, "Existing", source, modsRoot, CancellationToken.None);
        var commit = await service.CommitImportAsync(plan.Value!, updateConfirmed: true, null, CancellationToken.None);
        Assert.True(commit.IsSuccess);

        var restartedService = new TransactionalModImportService(new SafePathPolicy());
        await restartedService.RecoverInterruptedImportsAsync(modsRoot, CancellationToken.None);

        Assert.Equal("old", await File.ReadAllTextAsync(
            System.IO.Path.Combine(modsRoot, "Existing", "content.txt"), CancellationToken.None));
    }

    [Theory]
    [InlineData("Planned")]
    [InlineData("Staged")]
    [InlineData("OldMovedToBackup")]
    [InlineData("NewActivated")]
    public async Task FailureAtEveryCommitPhaseRestoresOriginalMod(string phase)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modsRoot = Path.Combine(temporaryDirectory.Path, "Mods");
        var modId = Guid.NewGuid();
        CreateSource(modsRoot, "Existing", modId, "Existing", "old");
        var source = CreateSource(temporaryDirectory.Path, "update", modId, "Existing", "new");
        var service = new TransactionalModImportService(
            new SafePathPolicy(),
            new ThrowingFaultInjector(phase, new IOException("Injected commit failure.")));
        var plan = await service.PlanImportAsync(modId, "Existing", source, modsRoot, CancellationToken.None);

        var result = await service.CommitImportAsync(plan.Value!, updateConfirmed: true, null, CancellationToken.None);

        Assert.Equal("Import.CommitFailed", result.ErrorCode);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(modsRoot, "Existing", "content.txt")));
        Assert.False(Directory.Exists(Path.Combine(modsRoot, ".transactions", plan.Value!.OperationId.ToString("N"))));
    }

    [Theory]
    [InlineData("OldMovedToBackup")]
    [InlineData("NewActivated")]
    public async Task SimulatedProcessTerminationIsRecoveredOnNextStartup(string phase)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var modsRoot = Path.Combine(temporaryDirectory.Path, "Mods");
        var modId = Guid.NewGuid();
        CreateSource(modsRoot, "Existing", modId, "Existing", "old");
        var source = CreateSource(temporaryDirectory.Path, "update", modId, "Existing", "new");
        var service = new TransactionalModImportService(
            new SafePathPolicy(),
            new ThrowingFaultInjector(phase, new SimulatedProcessTerminationException()));
        var plan = await service.PlanImportAsync(modId, "Existing", source, modsRoot, CancellationToken.None);

        await Assert.ThrowsAsync<SimulatedProcessTerminationException>(() =>
            service.CommitImportAsync(plan.Value!, updateConfirmed: true, null, CancellationToken.None));

        var restarted = new TransactionalModImportService(new SafePathPolicy());
        await restarted.RecoverInterruptedImportsAsync(modsRoot, CancellationToken.None);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(modsRoot, "Existing", "content.txt")));
        Assert.False(Directory.Exists(Path.Combine(modsRoot, ".transactions", plan.Value!.OperationId.ToString("N"))));
    }

    private static string CreateSource(string parent, string folderName, Guid id, string name, string content)
    {
        var directory = System.IO.Path.Combine(parent, folderName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "manifest.json"), $"{{\"Guid\":\"{id}\",\"Name\":\"{name}\"}}");
        File.WriteAllText(System.IO.Path.Combine(directory, "content.txt"), content);
        return directory;
    }

    private static Guid ReadId(string source)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(source, "manifest.json")));
        return document.RootElement.GetProperty("Guid").GetGuid();
    }

    private sealed class ThrowingFaultInjector(string phase, Exception exception) : IModImportFaultInjector
    {
        public Task OnPhaseCompletedAsync(string completedPhase, CancellationToken cancellationToken) =>
            string.Equals(completedPhase, phase, StringComparison.Ordinal)
                ? Task.FromException(exception)
                : Task.CompletedTask;
    }

    private sealed class SimulatedProcessTerminationException : Exception;
}
