using System.Text.RegularExpressions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed partial class ArchitectureGuardTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CoreAndInfrastructureDoNotReferenceWpf()
    {
        foreach (var file in SourceFiles("Helldivers2ModManager.Core", "Helldivers2ModManager.Infrastructure"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("System.Windows", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PresentationFramework", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PatchAndRepairCodeDoesNotLoadWholeFiles()
    {
        var serviceDirectory = Path.Combine(RepositoryRoot, "Helldivers2ModManager", "Services");
        var files = Directory.EnumerateFiles(serviceDirectory, "VersionCheck*.cs", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("ReadAllBytes", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new MemoryStream", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionArchiveCodeDoesNotUseBlindExtraction()
    {
        foreach (var file in SourceFiles("Helldivers2ModManager", "Helldivers2ModManager.Infrastructure"))
            Assert.DoesNotContain("ExtractArchive(", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ViewsModelsAndViewModelsDoNotLocateServices()
    {
        var roots = new[]
        {
            Path.Combine("Helldivers2ModManager", "Views"),
            Path.Combine("Helldivers2ModManager", "Components"),
            Path.Combine("Helldivers2ModManager", "Models"),
            Path.Combine("Helldivers2ModManager", "ViewModels")
        };
        foreach (var file in SourceFiles(roots))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("IServiceProvider", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetRequiredService", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetService(", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LocalizedSentencesUseNamedFormattingInsteadOfManualReplacement()
    {
        foreach (var file in SourceFiles("Helldivers2ModManager"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotMatch(LocalizerReplaceRegex(), source);
        }
    }

    [Fact]
    public void NonViewEventHandlersDoNotUseAsyncVoid()
    {
        foreach (var file in SourceFiles("Helldivers2ModManager").Where(static file =>
                     !file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) &&
                     !file.EndsWith("App.xaml.cs", StringComparison.OrdinalIgnoreCase)))
            Assert.DoesNotMatch(AsyncVoidRegex(), File.ReadAllText(file));

        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "Helldivers2ModManager", "App.xaml.cs"));
        Assert.Matches(@"protected\s+override\s+async\s+void\s+OnStartup\s*\(", appSource);
        Assert.Single(AsyncVoidRegex().Matches(appSource).Cast<Match>());
    }

    [Fact]
    public void DialogServiceDoesNotPublishLegacyMessengerMessages()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Services",
            "WpfUiServices.cs"));

        Assert.DoesNotContain("WeakReferenceMessenger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxConfirmMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPageDoesNotUseLegacyConfirmationMessages()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "ViewModels",
            "SettingsPageViewModel.cs"));

        Assert.DoesNotContain("MessageBoxConfirmMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MigratedViewModelsDoNotPublishLegacyDialogMessages()
    {
        var relativeFiles = new[]
        {
            "ViewModels/TagManagementPageViewModel.cs",
            "ViewModels/ModGroupSidebarViewModel.cs",
            "ViewModels/NexusDownloadPageViewModel.cs"
        };
        foreach (var relativeFile in relativeFiles)
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot, "Helldivers2ModManager", relativeFile));
            Assert.DoesNotContain("WeakReferenceMessenger", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxErrorMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxInfoMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxInputMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxColorPickerMessage", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MessageBoxDoesNotEmbedChineseUserVisibleFallbacks()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Components",
            "MessageBox.xaml.cs"));
        Assert.DoesNotContain("导出完成", source, StringComparison.Ordinal);
        Assert.DoesNotContain("?? \"正在压缩", source, StringComparison.Ordinal);
        Assert.DoesNotContain("模组更新完成", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageBoxUpdateStatusUsesCompleteLocalizedSentences()
    {
        var source = string.Join('\n', SourceFiles("Helldivers2ModManager").Select(File.ReadAllText));
        var forbiddenFragments = new[]
        {
            "MessageBox.UpdatingModPrefix",
            "MessageBox.UpdatingModSuffix",
            "MessageBox.ProcessedPrefix",
            "MessageBox.ProcessedSuffix",
            "MessageBox.CacheHitPrefix",
            "MessageBox.CacheHitSuffix",
            "MessageBox.NeedUpdatePrefix",
            "MessageBox.NeedUpdateSuffix"
        };

        foreach (var fragment in forbiddenFragments)
            Assert.DoesNotContain(fragment, source, StringComparison.Ordinal);

        foreach (var locale in new[] { "en-US.json", "zh-CN.json" })
        {
            var resource = File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "Helldivers2ModManager",
                "Resources",
                "Language",
                locale));
            Assert.Contains("\"MessageBox.ProcessedWithCache\"", resource, StringComparison.Ordinal);
            Assert.Contains("{processed}", resource, StringComparison.Ordinal);
            Assert.Contains("{total}", resource, StringComparison.Ordinal);
            Assert.Contains("{cacheHits}", resource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VersionDetailOverlayUsesDialogServiceForDialogs()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Components",
            "VersionCheckDetailOverlay.xaml.cs"));
        Assert.DoesNotContain("MessageBoxErrorMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxSelectionMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxChecklistMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Replace(\"{", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchRepairUsesDialogServiceAndDoesNotShortCircuitExecution()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "ViewModels",
            "DashboardPageViewModel.BatchRepair.cs"));
        Assert.DoesNotContain("WeakReferenceMessenger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxProgressMessage", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"if\s*\(IsBatchRepairing\)\s*return;", source);
    }

    [Fact]
    public void ModViewModelUsesDialogServiceForUserMessages()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "ViewModels",
            "ModViewModel.cs"));
        Assert.DoesNotContain("MessageBoxInfoMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxErrorMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViewModelUsesDialogServiceForUserMessages()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "ViewModels",
            "SettingsPageViewModel.cs"));
        Assert.DoesNotContain("WeakReferenceMessenger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxProgressMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxErrorMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupServiceIsSeparatedFromVersionCheckPartial()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Services",
            "VersionCheckBackupService.cs"));
        Assert.Contains("class ModBackupService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("partial class VersionCheckService", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionCheckServiceIsACompositionRootInsteadOfAPartialGodClass()
    {
        var serviceRoot = Path.Combine(RepositoryRoot, "Helldivers2ModManager", "Services");
        foreach (var file in Directory.EnumerateFiles(serviceRoot, "*.cs", SearchOption.TopDirectoryOnly))
            Assert.DoesNotContain("partial class VersionCheckService", File.ReadAllText(file), StringComparison.Ordinal);

        Assert.Contains(
            "class GameUnitReferenceService",
            File.ReadAllText(Path.Combine(serviceRoot, "GameUnitReferenceService.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "class GameCompanionRecoveryReader",
            File.ReadAllText(Path.Combine(serviceRoot, "GameCompanionRecoveryReader.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "class PatchRepairService",
            File.ReadAllText(Path.Combine(serviceRoot, "PatchRepairService.cs")),
            StringComparison.Ordinal);
        Assert.Contains(
            "class AssistedRepairService",
            File.ReadAllText(Path.Combine(serviceRoot, "AssistedRepairService.cs")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardInteractiveDialogsUseDialogService()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "ViewModels",
            "DashboardPageViewModel.cs"));
        Assert.DoesNotContain("MessageBoxInputMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxSelectionMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxTagSelectionMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxColorPickerMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NonDashboardViewModelsDoNotUseLegacyDialogMessages()
    {
        var viewModelRoot = Path.Combine(RepositoryRoot, "Helldivers2ModManager", "ViewModels");
        foreach (var file in Directory.EnumerateFiles(viewModelRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(static file => !file.EndsWith("DashboardPageViewModel.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("MessageBoxInfoMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxWarningMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxErrorMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxProgressMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxInputMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxSelectionMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxTagSelectionMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxChecklistMessage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBoxColorPickerMessage", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionCodeDoesNotDefineOrPublishLegacyConfirmationMessages()
    {
        foreach (var file in SourceFiles("Helldivers2ModManager"))
            Assert.DoesNotContain("MessageBoxConfirmMessage", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void MessageBoxUsesOnlyDialogServiceRequests()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Components",
            "MessageBox.xaml.cs"));

        Assert.DoesNotContain("WeakReferenceMessenger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IRecipient<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Receive(", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"MessageBox[A-Za-z]+Message", source);
    }

    [Fact]
    public void ProductionTasksAreExplicitlyObserved()
    {
        foreach (var file in SourceFiles("Helldivers2ModManager"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotMatch(@"\b_\s*=\s*[A-Za-z_][A-Za-z0-9_.]*Async\s*\(", source);
        }
    }

    [Fact]
    public void DpiWorkflowRunsTheFullMatrixForEverySupportedScale()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "dpi-ui.yml"));
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "run-dpi-ui-test.ps1"));

        Assert.Contains("-FullMatrix", workflow, StringComparison.Ordinal);
        Assert.Contains("dpi-${{ inputs.dpi_scale }}", workflow, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet(1, 1.25, 1.5, 2)]", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$FullMatrix", script, StringComparison.Ordinal);
        Assert.Contains("HD2MM_EXPECTED_DPI_SCALE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UiFeaturesDoNotBypassDialogOrClipboardServices()
    {
        var productionRoot = Path.Combine(RepositoryRoot, "Helldivers2ModManager");
        foreach (var file in Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(productionRoot, file).Replace('\\', '/');
            if (relative is "App.xaml.cs" or "Services/WpfUiServices.cs")
                continue;

            var source = File.ReadAllText(file);
            Assert.DoesNotContain("MessageBox.Show", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Clipboard.SetText(", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PurgerReusesCoreSafePathPolicy()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "Purger", "Purger.csproj"));
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "Purger", "MainForm.cs"));
        Assert.Contains("Helldivers2ModManager.Core.csproj", project, StringComparison.Ordinal);
        Assert.Contains("SharedSafePathPolicy", source, StringComparison.Ordinal);
        Assert.Contains("ResolveUnderRoot", source, StringComparison.Ordinal);
        Assert.Contains("IsUnderRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ModServiceDoesNotCreateOrCacheViewModels()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Services",
            "ModService.cs"));

        Assert.DoesNotContain("ModViewModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModels", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticModHashWorkUsesTheBackgroundTaskRunner()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "Helldivers2ModManager",
            "Services",
            "ModHashService.cs"));

        Assert.Contains("IBackgroundTaskRunner", source, StringComparison.Ordinal);
        Assert.Contains("_backgroundTaskRunner.RunAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("void ComputeAndStoreForModAsync", source, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles(params string[] relativeRoots)
    {
        foreach (var relativeRoot in relativeRoots)
        {
            var root = Path.Combine(RepositoryRoot, relativeRoot);
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Helldivers2ModManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"_localizationService\s*\[[^\]]+\]\s*\.Replace\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizerReplaceRegex();

    [GeneratedRegex(@"\basync\s+void\b", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncVoidRegex();
}
