using Helldivers2ModManager.Services;
using Helldivers2ModManager.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class SafetyDefaultsTests
{
    [Fact]
    public void NewSettingsKeepDangerousFeaturesDisabled()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var store = new AtomicJsonSettingsStore(Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store);

        settings.InitDefault();

        Assert.False(settings.EnableBrowserIntegration);
        Assert.False(settings.EnableExperimentalRepair);
        Assert.Equal("localhost", settings.ExtensionHost);
    }

    [Fact]
    public void ResetClearsPersistedSecurityAndPresentationState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var store = new AtomicJsonSettingsStore(Path.Combine(temporaryDirectory.Path, "settings.json"));
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store);
        settings.InitDefault();
        settings.EnableBrowserIntegration = true;
        settings.EnableExperimentalRepair = true;
        settings.BrowserExtensionTokenHash = "hash";
        settings.BrowserExtensionOrigin = "chrome-extension://old";
        settings.Language = "zh-CN";
        settings.Theme = "Dark";
        settings.EnableAnimations = false;
        settings.AutoCleanLogs = false;
        settings.LogRetentionDays = 30;
        settings.DeployBottomToTop = true;

        settings.Reset();

        Assert.False(settings.EnableBrowserIntegration);
        Assert.False(settings.EnableExperimentalRepair);
        Assert.Empty(settings.BrowserExtensionTokenHash);
        Assert.Empty(settings.BrowserExtensionOrigin);
        Assert.Empty(settings.Language);
        Assert.Equal("System", settings.Theme);
        Assert.True(settings.EnableAnimations);
        Assert.True(settings.AutoCleanLogs);
        Assert.Equal(7, settings.LogRetentionDays);
        Assert.False(settings.DeployBottomToTop);
    }
}
