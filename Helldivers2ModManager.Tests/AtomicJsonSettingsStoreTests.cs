using System.Text.Json;
using Helldivers2ModManager.Core.Settings;
using Helldivers2ModManager.Infrastructure.Settings;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class AtomicJsonSettingsStoreTests
{
    [Fact]
    public async Task ConcurrentSavesLeaveValidDocumentBackupAndNoTemporaryFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        using var store = new AtomicJsonSettingsStore(settingsPath);

        var saves = Enumerable.Range(0, 100)
            .Select(index => store.SaveAsync(
                new AppSettingsSnapshot { Language = $"test-{index}" },
                CancellationToken.None));
        await Task.WhenAll(saves);

        await using var stream = File.OpenRead(settingsPath);
        var document = await JsonSerializer.DeserializeAsync<AppSettingsSnapshot>(stream);
        Assert.NotNull(document);
        Assert.Equal(2, document.SchemaVersion);
        Assert.True(File.Exists(settingsPath + ".bak"));
        Assert.False(File.Exists(settingsPath + ".tmp"));
    }

    [Fact]
    public async Task LoadFallsBackToBackupWhenPrimaryJsonIsCorrupt()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        using var store = new AtomicJsonSettingsStore(settingsPath);
        await store.SaveAsync(
            new AppSettingsSnapshot { Language = "en-US" },
            CancellationToken.None);
        await File.WriteAllTextAsync(settingsPath, "{broken", CancellationToken.None);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("en-US", result.Language);
    }

    [Fact]
    public async Task LoadMigratesLegacyWorkingDirectoryDocumentOnce()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var targetDirectory = System.IO.Path.Combine(temporaryDirectory.Path, "application");
        var legacyDirectory = System.IO.Path.Combine(temporaryDirectory.Path, "legacy");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(legacyDirectory);
        var settingsPath = System.IO.Path.Combine(targetDirectory, "settings.json");
        var legacyPath = System.IO.Path.Combine(legacyDirectory, "settings.json");
        await File.WriteAllTextAsync(
            legacyPath,
            "{\"SchemaVersion\":1,\"Language\":\"zh-CN\"}",
            CancellationToken.None);
        using var store = new AtomicJsonSettingsStore(settingsPath, legacyPath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("zh-CN", result.Language);
        Assert.True(File.Exists(settingsPath));
        Assert.True(File.Exists(legacyPath + ".pre-v2.bak"));
    }

    [Fact]
    public async Task LoadAcceptsV1NumericLogLevelAndNormalizesItToTheV2Name()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            "{\"SchemaVersion\":1,\"LogLevel\":0}",
            CancellationToken.None);
        using var store = new AtomicJsonSettingsStore(settingsPath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Trace", result.LogLevel);
    }

    [Fact]
    public async Task Version15DocumentLoadsAllUserDataAndRoundTripsThroughTypedSnapshot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        var modId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var separatorId = Guid.NewGuid();
        var legacy = $$"""
		{
		  "GameDirectory": "D:\\Games\\Helldivers 2",
		  "StorageDirectory": "D:\\HD2MM",
		  "TempDirectory": "D:\\HD2MM-Temp",
		  "LogLevel": "Information",
		  "Opacity": 0.9,
		  "SkipList": ["0123456789abcdef"],
		  "OrganizationalFolderNames": ["Models", "Audio"],
		  "DeleteToRecycleBin": false,
		  "EnableSorting": true,
		  "DeployBottomToTop": true,
		  "Language": "en-US",
		  "Theme": "Dark",
		  "EnableAnimations": false,
		  "NexusApiKey": "encrypted-value",
		  "Tags": [{ "id": "{{tagId}}", "name": "Visual", "color": "#FF123456" }],
		  "Separators": [{ "id": "{{separatorId}}", "name": "Weapons", "color": "#FF654321", "isExpanded": false, "displayIndex": 3, "modGuids": ["{{modId}}"] }],
		  "UseDeploymentOrder": true,
		  "DeploymentOrderGuids": ["{{modId}}"],
		  "OptionOrders": [{ "key": "{{modId}}", "value": [2, 0, 1] }],
		  "SubOptionOrders": [{ "key": "{{modId}}", "value": { "2": [1, 0] } }]
		}
		""";
        await File.WriteAllTextAsync(settingsPath, legacy, CancellationToken.None);
        using var store = new AtomicJsonSettingsStore(settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Information", loaded.LogLevel);
        Assert.Equal(["Models", "Audio"], loaded.OrganizationalFolderNames);
        Assert.False(loaded.DeleteToRecycleBin);
        Assert.True(loaded.DeployBottomToTop);
        Assert.Equal("encrypted-value", loaded.NexusApiKey);
        Assert.Equal(tagId, Assert.Single(loaded.Tags).Id);
        Assert.Equal(modId, Assert.Single(Assert.Single(loaded.Separators).ModGuids));
        Assert.Equal([2, 0, 1], Assert.Single(loaded.OptionOrders).Value);
        Assert.Equal([1, 0], Assert.Single(loaded.SubOptionOrders).Value[2]);

        await store.SaveAsync(loaded, CancellationToken.None);
        var roundTripped = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(loaded.GameDirectory, roundTripped.GameDirectory);
        Assert.Equal(loaded.OrganizationalFolderNames, roundTripped.OrganizationalFolderNames);
        Assert.Equal(loaded.DeploymentOrderGuids, roundTripped.DeploymentOrderGuids);
        Assert.Equal(Assert.Single(loaded.Tags), Assert.Single(roundTripped.Tags));
        var expectedSeparator = Assert.Single(loaded.Separators);
        var actualSeparator = Assert.Single(roundTripped.Separators);
        Assert.Equal(expectedSeparator.Id, actualSeparator.Id);
        Assert.Equal(expectedSeparator.Name, actualSeparator.Name);
        Assert.Equal(expectedSeparator.ModGuids, actualSeparator.ModGuids);
        Assert.Equal(Assert.Single(loaded.OptionOrders).Value, Assert.Single(roundTripped.OptionOrders).Value);
        Assert.Equal(Assert.Single(loaded.SubOptionOrders).Value[2], Assert.Single(roundTripped.SubOptionOrders).Value[2]);
    }
}
