using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class DatabaseReadOnlyModeTests
{
    [Fact]
    public async Task ReadOnlyRecoveryModeAllowsReadsAndRejectsEveryRepositoryMutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        using (database.OpenConnection(temporaryDirectory.Path))
        {
        }

        EnterReadOnlyMode(database);

        var enabledRepository = new EnabledDataRepository(
            NullLogger<EnabledDataRepository>.Instance,
            database);
        var groupRepository = new ModGroupRepository(
            NullLogger<ModGroupRepository>.Instance,
            database);
        var hashRepository = new FileHashRepository(
            NullLogger<FileHashRepository>.Instance,
            database);
        var versionRepository = new VersionCheckRepository(
            NullLogger<VersionCheckRepository>.Instance,
            database);

        Assert.Empty(enabledRepository.LoadAll(temporaryDirectory.Path));
        Assert.Empty(groupRepository.LoadGroups(temporaryDirectory.Path));
        Assert.Empty(hashRepository.GetAllForMod(temporaryDirectory.Path, Guid.NewGuid()));
        Assert.Empty(versionRepository.LoadAll(temporaryDirectory.Path));

        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            enabledRepository.SaveAllAsync(temporaryDirectory.Path, []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            enabledRepository.DeleteByGuidsAsync(temporaryDirectory.Path, []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            groupRepository.SaveGroupsAsync(temporaryDirectory.Path, []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            groupRepository.SaveStatesAsync(temporaryDirectory.Path, Guid.NewGuid(), []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            groupRepository.DeleteGroupAsync(temporaryDirectory.Path, Guid.NewGuid()));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            groupRepository.DeleteStatesByGuidsAsync(temporaryDirectory.Path, []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            hashRepository.UpsertModHashesAsync(temporaryDirectory.Path, Guid.NewGuid(), []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            hashRepository.DeleteForModAsync(temporaryDirectory.Path, Guid.NewGuid()));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            versionRepository.SaveAllAsync(temporaryDirectory.Path, []));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            versionRepository.DeleteByGuidAsync(temporaryDirectory.Path, Guid.NewGuid()));
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() =>
            versionRepository.UpdateGameExeLastWriteTimeAsync(temporaryDirectory.Path, DateTime.UtcNow));
    }

    [Fact]
    public async Task GroupInitializationRemainsUsableWithoutAttemptingRecoveryWrites()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        using (database.OpenConnection(temporaryDirectory.Path))
        {
        }
        EnterReadOnlyMode(database);

        using var settingsStore = new AtomicJsonSettingsStore(
            Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsStore);
        settings.InitDefault();
        settings.StorageDirectory = temporaryDirectory.Path;
        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance);
        var repository = new ModGroupRepository(
            NullLogger<ModGroupRepository>.Instance,
            database);
        var service = new ModGroupService(
            NullLogger<ModGroupService>.Instance,
            repository,
            localization,
            database);

        await service.InitAsync(settings, []);

        Assert.Single(service.Groups);
        Assert.True(service.SelectedGroup.IsDefault);
        await Assert.ThrowsAsync<DatabaseReadOnlyException>(() => service.CreateGroupAsync("Blocked"));
        Assert.Single(service.Groups);
    }

    private static void EnterReadOnlyMode(DatabaseService database)
    {
        var method = typeof(DatabaseService).GetMethod(
            "SetReadOnly",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(database, [true]);
        Assert.True(database.IsReadOnly);
    }
}
