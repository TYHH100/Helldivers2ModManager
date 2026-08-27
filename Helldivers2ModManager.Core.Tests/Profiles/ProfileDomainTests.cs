using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Profiles;

[TestClass]
public sealed class ProfileDomainTests
{
    [TestMethod]
    public void RuntimeState_ShouldRoundTripOldShapeAndPreferredOrder()
    {
        var state = new ModRuntimeState([true, false], [1, 0], [Guid.NewGuid(), Guid.NewGuid()]);
        var json = ProfileStateService.SerializeRuntimeState(state);
        var loaded = ProfileStateService.DeserializeRuntimeState(json);

        Assert.IsTrue(loaded.EnabledOptions[0]);
        Assert.IsFalse(loaded.EnabledOptions[1]);
        Assert.AreEqual(1, loaded.SelectedOptions[0]);
        CollectionAssert.AreEqual(state.TagIds!.ToArray(), loaded.TagIds!.ToArray());

        var guidA = Guid.NewGuid();
        var guidB = Guid.NewGuid();
        var capture = new ProfileCaptureRequest(Guid.NewGuid(), true,
        [
            new(guidA, true, state),
            new(guidB, false, state),
        ], [guidB, guidA]);
        var snapshot = ProfileStateService.Capture(1, capture);

        Assert.AreEqual(guidB, snapshot.Mods[0].ModGuid);
        Assert.AreEqual(guidA, snapshot.Mods[1].ModGuid);
    }

    [TestMethod]
    public async Task GroupRepository_ShouldSaveStatesRenameAndDelete()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hd2mm-profile-{Guid.NewGuid():N}.db");
        try
        {
            using var database = new Database(path);
            await database.InitializeAsync();
            var profiles = new ProfileRepository(database);
            var groups = new GroupRepository(database);
            var service = new ModGroupService(groups);
            var profile = await profiles.GetOrCreateDefaultAsync();
            var group = await service.CreateAsync(profile.Id, "  Armor  ");
            await service.RenameAsync(profile.Id, group, "Weapons");
            var guid = Guid.NewGuid();
            await service.SaveMembersAsync(profile.Id, group.Id,
                [new(guid, true, group.Id, 3, """{"EnabledOptions":[true]}""")]);

            var loadedProfile = await profiles.LoadAsync(profile.Id);
            Assert.IsNotNull(loadedProfile);
            Assert.AreEqual("Weapons", loadedProfile.Groups.Single().Name);
            Assert.AreEqual(guid, loadedProfile.Mods.Single().ModGuid);

            await service.DeleteAsync(profile.Id, group.Id);
            loadedProfile = await profiles.LoadAsync(profile.Id);
            Assert.IsNotNull(loadedProfile);
            Assert.AreEqual(0, loadedProfile.Groups.Count);
            Assert.IsNull(loadedProfile.Mods.Single().GroupId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ProfileSaveCoordinator_ShouldDebounceToLatestSnapshot()
    {
        var saved = new List<ProfileSnapshot>();
        using var loggerFactory = NullLoggerFactory.Instance;
        var coordinator = new ProfileSaveCoordinator(
            snapshot =>
            {
                lock (saved) saved.Add(snapshot);
                return Task.CompletedTask;
            },
            NullLogger<ProfileSaveCoordinator>.Instance);
        var groupId = Guid.NewGuid();
        coordinator.RequestSave(new(groupId, true, [new(Guid.NewGuid(), true, new([], []))]));
        coordinator.RequestSave(new(groupId, true, [new(Guid.NewGuid(), false, new([], []))]));
        await coordinator.FlushAsync();
        await Task.Delay(50);

        lock (saved)
        {
            Assert.AreEqual(1, saved.Count);
            Assert.IsFalse(saved[0].Mods.Single().Enabled);
        }
    }

    [TestMethod]
    public void CreateDeploymentInputs_ShouldUseCapturedOptionsAndOnlyEnabledMods()
    {
        var enabledDirectory = new DirectoryInfo(@"C:\enabled");
        var disabledDirectory = new DirectoryInfo(@"C:\disabled");
        var manifests = new Dictionary<Guid, IModManifest>();
        var discoveries = new List<DiscoveredMod>();
        foreach (var (guid, directory) in new[] { (Guid.NewGuid(), enabledDirectory), (Guid.NewGuid(), disabledDirectory) })
        {
            var manifest = new LegacyModManifest { Guid = guid, Name = directory.Name, Description = string.Empty };
            manifests[guid] = manifest;
            discoveries.Add(new(directory, manifest));
        }

        var discovery = new ModDiscoveryResult(discoveries, []);
        var enabledId = discoveries[0].Manifest.Guid;
        var disabledId = discoveries[1].Manifest.Guid;
        var snapshot = ProfileStateService.Capture(7, new(Guid.NewGuid(), true,
        [
            new(enabledId, true, new([true], [0])),
            new(disabledId, false, new([false], [0])),
        ]));
        var inputs = ProfileStateService.CreateDeploymentInputs(discovery, snapshot).ToArray();

        Assert.AreEqual(1, inputs.Length);
        Assert.AreEqual(enabledId, inputs[0].Guid);
        Assert.IsTrue(inputs[0].EnabledOptions.Single());
    }
}
