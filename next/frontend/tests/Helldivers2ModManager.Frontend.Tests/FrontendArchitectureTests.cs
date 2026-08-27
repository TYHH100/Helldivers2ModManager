using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Frontend.Tests;

[TestClass]
public sealed class FrontendArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [TestMethod]
    public void Frontend_ProjectReferencesOnlyCore()
    {
        var project = LoadProject("src", "Helldivers2ModManager.Frontend");
        var references = project.Root!.Elements("ItemGroup")
            .SelectMany(group => group.Elements("ProjectReference"))
            .Select(element => element.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "../../../../Helldivers2ModManager.Core/Helldivers2ModManager.Core.csproj" },
            references);
    }

    [TestMethod]
    public void Frontend_SourceDoesNotReferenceLegacyApplicationLayers()
    {
        AssertSourceContainsNone(new[]
        {
            "src/Helldivers2ModManager.Frontend",
        }, new[]
        {
            "using Helldivers2ModManager.Services;",
            "using Helldivers2ModManager.ViewModels;",
            "using Helldivers2ModManager.Models;",
            "using Helldivers2ModManager.Adapters;",
            "clr-namespace:Helldivers2ModManager.Services",
            "clr-namespace:Helldivers2ModManager.ViewModels",
            "clr-namespace:Helldivers2ModManager.Views",
        });
    }

    [TestMethod]
    public void Host_DoesNotRegisterLegacyServices()
    {
        AssertSourceContainsNone(new[]
        {
            "src/Helldivers2ModManager.Frontend.Host",
        }, new[]
        {
            "Helldivers2ModManager.Services",
            "RegisterService",
        });
    }

    private static XDocument LoadProject(string area, string projectName)
    {
        var path = Path.Combine(
            RepositoryRoot,
            "next/frontend",
            area,
            projectName,
            $"{projectName}.csproj");
        return XDocument.Load(path);
    }

    private static void AssertSourceContainsNone(string[] relativeDirectories, string[] forbiddenText)
    {
        var violations = new List<string>();
        foreach (var relativeDirectory in relativeDirectories)
        {
            var directory = Path.Combine(RepositoryRoot, "next/frontend", relativeDirectory);
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
            foreach (var file in files)
            {
                var content = File.ReadAllText(file);
                foreach (var text in forbiddenText.Where(content.Contains))
                {
                    violations.Add($"{Path.GetRelativePath(RepositoryRoot, file)}: {text}");
                }
            }
        }

        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "next/frontend/Helldivers2ModManager.Frontend.sln");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Could not locate the isolated frontend solution.");
    }
}
