using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Navigation;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed partial class FreezeCandidateTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [AssemblyCleanup]
    public static void CleanupTestArtifacts()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var targets = new[]
        {
            Path.Combine(tempRoot, "Helldivers2ModManagerFrontendSource"),
            Path.Combine(tempRoot, "Helldivers2ModManagerFrontendTests"),
        };
        foreach (var target in targets)
        {
            var resolved = Path.GetFullPath(target);
            if (!resolved.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to clean unexpected path: {resolved}");
            }

            DeleteDirectoryWithRetry(resolved);
        }
    }

    [TestMethod]
    public void EveryRoute_HasExactlyOneRealPageTemplate()
    {
        var mainWindowPath = Path.Combine(
            RepositoryRoot,
            "next/frontend/src/Helldivers2ModManager.Frontend/Views/MainWindow.xaml");
        var markup = File.ReadAllText(mainWindowPath);
        var templates = DataTemplateRegex().Matches(markup);
        var routes = FrontendRouteRegistry.All.ToArray();

        Assert.AreEqual(routes.Length, templates.Count);
        StringAssert.DoesNotMatch(markup, new Regex("PlaceholderPageView"));

        foreach (var route in routes)
        {
            var viewModelName = route.ViewModelType.Name;
            var expectedViewName = viewModelName[..^"ViewModel".Length] + "View";
            var template = templates.FirstOrDefault(match => match.Groups["ViewModel"].Value == viewModelName);

            Assert.IsNotNull(template, $"Missing page template for {route.Key}.");
            Assert.AreEqual(1, templates.Count(match => match.Groups["ViewModel"].Value == viewModelName),
                $"Duplicate page template for {route.Key}.");
            Assert.AreEqual(expectedViewName, template.Groups["View"].Value,
                $"Unexpected page view for {route.Key}.");

            var viewPath = Path.Combine(
                RepositoryRoot,
                $"next/frontend/src/Helldivers2ModManager.Frontend/Views/{expectedViewName}.xaml");
            Assert.IsTrue(File.Exists(viewPath), $"Missing view: {expectedViewName}.");
        }
    }

    [TestMethod]
    public void StaticLocalizationReferences_ExistInSupportedCultures()
    {
        var sourceDirectory = Path.Combine(
            RepositoryRoot,
            "next/frontend/src/Helldivers2ModManager.Frontend");
        var cultures = new[]
        {
            CultureInfo.GetCultureInfo("zh-CN"),
            CultureInfo.GetCultureInfo("en-US"),
        };
        var catalog = new LocalizationCatalog();
        HashSet<string> missing = [];

        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            foreach (Match match in LocalizationReferenceRegex().Matches(content))
            {
                var key = match.Groups["Key"].Value;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    missing.UnionWith(cultures.Where(culture => !catalog.Contains(key, culture))
                        .Select(culture => $"{key}:{culture.Name}"));
                }
            }
        }

        Assert.AreEqual(0, missing.Count, string.Join(Environment.NewLine, missing.OrderBy(item => item)));
    }

    [TestMethod]
    public void AcceptanceChecklist_CoversFrozenCandidateGate()
    {
        var acceptancePath = Path.Combine(RepositoryRoot, "next/frontend/ACCEPTANCE.md");
        var content = File.ReadAllText(acceptancePath);

        foreach (var requiredSection in new[]
                 {
                     "模组库与选项部署",
                     "部署与后台任务",
                     "工具链",
                     "分析与修复",
                     "冻结门槛",
                 })
        {
            StringAssert.Contains(content, requiredSection);
        }

        StringAssert.Contains(content, "%LOCALAPPDATA%\\Helldivers2ModManagerNext");
        StringAssert.Contains(content, "不开启护甲污染功能");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "next/frontend/Helldivers2ModManager.Frontend.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Could not locate the isolated frontend solution.");
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
        }
    }

    [GeneratedRegex("<DataTemplate\\s+DataType=\"\\{x:Type pages:(?<ViewModel>[^\"]+)\\}\">" +
                   "<views:(?<View>[^<>/]+)\\s*/></DataTemplate>")]
    private static partial Regex DataTemplateRegex();

    [GeneratedRegex("\\.GetString\\(\"(?<Key>[^\"]+)\"\\)")]
    private static partial Regex LocalizationReferenceRegex();
}
