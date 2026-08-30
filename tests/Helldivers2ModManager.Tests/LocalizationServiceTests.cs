using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Reflection;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class LocalizationServiceTests
{
    private string? _tempDir;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-locale-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (_tempDir is not null)
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);

            // 顺带清理本测试专用的空父目录，避免 %TEMP% 下残留空壳。
            var parent = Path.GetDirectoryName(_tempDir);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent, recursive: false);
        }
    }

    private static string RepositoryLanguageDirectory()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("repository root not found");
    }

    private void CopyLanguageFiles()
    {
        foreach (var file in Directory.EnumerateFiles(RepositoryLanguageDirectory(), "*.json"))
            File.Copy(file, Path.Combine(_tempDir!, Path.GetFileName(file)));
    }

    private LocalizationService CreateService() =>
        new(NullLogger<LocalizationService>.Instance, _tempDir!);

    private static int LoadedLocaleCacheCount(LocalizationService service)
    {
        var field = typeof(LocalizationService).GetField("_localeCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field);
        return ((System.Collections.IDictionary)field.GetValue(service)!).Count;
    }

    [TestMethod]
    public void LoadsAvailableLanguagesAndAppliesDetectedLanguage()
    {
        CopyLanguageFiles();
        var service = CreateService();

        Assert.IsTrue(service.AvailableLanguages.Count >= 3, "应包含 Auto Detect + 至少两个语言");
        Assert.AreEqual("Auto Detect", service.AvailableLanguages[0].DisplayName);
        Assert.AreEqual("", service.AvailableLanguages[0].LocaleCode);
        Assert.IsTrue(service.AvailableLanguages.Any(static l => l.LocaleCode == "zh-CN"));
        Assert.IsTrue(service.AvailableLanguages.Any(static l => l.LocaleCode == "en-US"));
        Assert.IsFalse(string.IsNullOrEmpty(service.CurrentLanguage));
        Assert.IsFalse(string.IsNullOrEmpty(service.CurrentLanguageName));
        Assert.IsFalse(string.IsNullOrEmpty(service["DashboardPage.Title"]));
        Assert.IsFalse(service["DashboardPage.Title"].StartsWith("[", StringComparison.Ordinal), "不应返回占位符");
    }

    [TestMethod]
    public void SwitchingLanguageUpdatesStrings()
    {
        CopyLanguageFiles();
        var service = CreateService();

        service.SelectedLanguage = "zh-CN";
        Assert.AreEqual("zh-CN", service.CurrentLanguage);
        Assert.AreEqual("中文(简体)", service.CurrentLanguageName);
        var zhTitle = service["DashboardPage.Title"];
        Assert.IsFalse(string.IsNullOrEmpty(zhTitle));

        service.SelectedLanguage = "en-US";
        Assert.AreEqual("en-US", service.CurrentLanguage);
        Assert.AreEqual("English (US)", service.CurrentLanguageName);
        var enTitle = service["DashboardPage.Title"];
        Assert.IsFalse(string.IsNullOrEmpty(enTitle));
        Assert.AreNotEqual(zhTitle, enTitle, "不同语言应返回不同翻译");
    }

    [TestMethod]
    public void UnknownLanguageFallsBackToEnglish()
    {
        CopyLanguageFiles();
        var service = CreateService();

        service.SelectedLanguage = "xx-XX";
        Assert.AreEqual("en-US", service.CurrentLanguage);
        Assert.IsFalse(string.IsNullOrEmpty(service["DashboardPage.Title"]));
    }

    [TestMethod]
    public void InvalidJsonFileIsSkipped()
    {
        CopyLanguageFiles();
        File.WriteAllText(Path.Combine(_tempDir!, "broken.json"), "{ invalid json !!");
        var service = CreateService();

        Assert.IsFalse(service.AvailableLanguages.Any(static l => l.LocaleCode == "broken"));
        Assert.IsFalse(string.IsNullOrEmpty(service["DashboardPage.Title"]));
    }

    [TestMethod]
    public void StringsAreParsedLazilyUntilLanguageIsUsed()
    {
        CopyLanguageFiles();
        var service = CreateService();

        // 构造时只完整解析当前检测到的语言；另一个语言保持未解析。
        Assert.AreEqual(1, LoadedLocaleCacheCount(service), "构造后应只解析当前语言");

        var other = service.CurrentLanguage == "en-US" ? "zh-CN" : "en-US";
        service.SelectedLanguage = other;
        Assert.AreEqual(2, LoadedLocaleCacheCount(service), "切换后才解析第二个语言");
        Assert.AreEqual(other, service.CurrentLanguage);
    }
}
