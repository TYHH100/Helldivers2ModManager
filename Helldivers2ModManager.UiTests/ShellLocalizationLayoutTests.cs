using System.Diagnostics;
using System.Text.Json;
using Xunit;
using System.Runtime.InteropServices;

namespace Helldivers2ModManager.UiTests;

public sealed partial class ShellLocalizationLayoutTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TheoryData<string, string, double, double, string, string?> ShellCases => new()
    {
        { "zh-CN", "Dark", 800, 600, "Dashboard", null },
        { "zh-CN", "Light", 1000, 700, "Settings", null },
        { "zh-CN", "Dark", 1366, 768, "Create", null },
        { "zh-CN", "System", 1920, 1080, "Downloads", null },
        { "zh-CN", "Light", 2560, 1440, "Tasks", null },
        { "en-US", "Dark", 800, 600, "Dashboard", null },
        { "en-US", "Light", 1000, 700, "Settings", null },
        { "en-US", "Dark", 1366, 768, "Create", null },
        { "en-US", "System", 1920, 1080, "Downloads", null },
        { "en-US", "Light", 2560, 1440, "Tasks", null },
        { "qps-ploc", "Dark", 800, 600, "Dashboard", null },
        { "qps-ploc", "Light", 1000, 700, "Settings", null },
        { "qps-ploc", "Dark", 1366, 768, "Create", null },
        { "qps-ploc", "System", 1920, 1080, "Downloads", null },
        { "qps-ploc", "Light", 2560, 1440, "Tasks", null },
        { "zh-CN", "System", 800, 600, "Help", null },
        { "en-US", "Dark", 1000, 700, "Help", null },
        { "qps-ploc", "Light", 1366, 768, "Help", null },
        { "zh-CN", "Dark", 800, 600, "Tags", null },
        { "en-US", "Light", 1000, 700, "Tags", null },
        { "qps-ploc", "System", 1366, 768, "Tags", null },
        { "zh-CN", "Light", 1000, 700, "DeploymentOrder", null },
        { "en-US", "Dark", 1366, 768, "DeploymentOrder", null },
        { "qps-ploc", "Light", 1920, 1080, "DeploymentOrder", null },
        { "zh-CN", "HighContrast", 800, 600, "Dashboard", null },
        { "en-US", "HighContrast", 1000, 700, "Settings", null },
        { "qps-ploc", "HighContrast", 1366, 768, "Help", null },
        { "zh-CN", "Dark", 800, 600, "ManifestEditor", null },
        { "en-US", "Light", 1000, 700, "ManifestEditor", null },
        { "qps-ploc", "System", 1366, 768, "ManifestEditor", null },
        { "zh-CN", "Light", 800, 600, "VersionDetail", null },
        { "en-US", "Dark", 1000, 700, "VersionDetail", null },
        { "qps-ploc", "HighContrast", 1366, 768, "VersionDetail", null },
        { "zh-CN", "Dark", 800, 600, "Dialog", null },
        { "en-US", "Light", 1000, 700, "Dialog", null },
        { "qps-ploc", "System", 800, 600, "Dialog", null },
        { "zh-CN", "Dark", 800, 600, "ContextMenu", null },
        { "en-US", "Light", 1000, 700, "ContextMenu", null },
        { "qps-ploc", "System", 1366, 768, "ContextMenu", null },
        { "zh-CN", "Dark", 800, 600, "Tooltip", null },
        { "en-US", "Light", 1000, 700, "Tooltip", null },
        { "qps-ploc", "System", 1366, 768, "Tooltip", null },
        { "zh-CN", "Light", 1000, 700, "Settings", "en-US" },
        { "en-US", "Dark", 1366, 768, "Create", "qps-ploc" },
        { "qps-ploc", "Dark", 800, 600, "Dashboard", "zh-CN" }
    };

    [Theory]
    [MemberData(nameof(ShellCases))]
    public void ShellNavigationRemainsReachableAcrossLocalesThemesAndBreakpoints(
        string locale,
        string theme,
        double width,
        double height,
        string page,
        string? targetLocale)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal))
            return;
        var pageFilter = Environment.GetEnvironmentVariable("HD2MM_UI_PAGE_FILTER");
        if (!string.IsNullOrWhiteSpace(pageFilter) &&
            !string.Equals(pageFilter, page, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var themeFilter = Environment.GetEnvironmentVariable("HD2MM_UI_THEME_FILTER");
        if (!string.IsNullOrWhiteSpace(themeFilter) &&
            !string.Equals(themeFilter, theme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var dpiCaseFilter = Environment.GetEnvironmentVariable("HD2MM_UI_DPI_CASE");
        if (!string.IsNullOrWhiteSpace(dpiCaseFilter) &&
            !string.Equals($"{page}@{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}x{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}", dpiCaseFilter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceExecutable = Environment.GetEnvironmentVariable("HD2MM_APP_PATH")
            ?? throw new InvalidOperationException("HD2MM_APP_PATH must point to the Release executable.");
        var testRoot = Path.Combine(Path.GetTempPath(), "Helldivers2ModManager.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        Process? application = null;
        try
        {
            CopyDirectory(Path.GetDirectoryName(sourceExecutable)!, testRoot);
            CreateIsolatedSettings(testRoot, locale, theme, page);
            var executable = Path.Combine(testRoot, Path.GetFileName(sourceExecutable));
            var startInfo = new ProcessStartInfo(executable) { WorkingDirectory = testRoot };
            startInfo.Environment["HD2MM_ENABLE_PSEUDO_LOCALIZATION"] = "1";
            startInfo.Environment["HD2MM_RUN_UI_TESTS"] = "1";
            startInfo.Environment["HD2MM_TEST_WINDOW_WIDTH"] = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["HD2MM_TEST_WINDOW_HEIGHT"] = height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["HD2MM_TEST_PAGE"] = page;
            if (theme == "HighContrast")
                startInfo.Environment["HD2MM_TEST_FORCE_HIGH_CONTRAST"] = "1";
            var metricsPath = Path.Combine(testRoot, "window-metrics.txt");
            var layoutReportPath = Path.Combine(testRoot, "layout-report.json");
            var switchLayoutReportPath = Path.Combine(testRoot, "switch-layout-report.json");
            var visualTreePath = Path.Combine(testRoot, "visual-tree.json");
            var screenshotPath = Path.Combine(testRoot, "screenshot.png");
            startInfo.Environment["HD2MM_TEST_WINDOW_METRICS_PATH"] = metricsPath;
            startInfo.Environment["HD2MM_TEST_LAYOUT_REPORT_PATH"] = layoutReportPath;
            startInfo.Environment["HD2MM_TEST_VISUAL_TREE_PATH"] = visualTreePath;
            startInfo.Environment["HD2MM_TEST_SCREENSHOT_PATH"] = screenshotPath;
            if (!string.IsNullOrWhiteSpace(targetLocale))
            {
                startInfo.Environment["HD2MM_TEST_SWITCH_LOCALE"] = targetLocale;
                startInfo.Environment["HD2MM_TEST_SWITCH_LAYOUT_REPORT_PATH"] = switchLayoutReportPath;
            }
            if (width < 900)
                startInfo.Environment["HD2MM_TEST_OPEN_NAVIGATION"] = "1";
            application = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The application process could not be started.");
            Thread.Sleep(750);
            Assert.False(application.HasExited);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(metricsPath), TimeSpan.FromSeconds(10)));
            var metrics = ReadAllTextWithRetry(metricsPath).Split('|');
            Assert.Equal(2, metrics.Length);
            var actualWidth = double.Parse(metrics[0], System.Globalization.CultureInfo.InvariantCulture);
            var actualHeight = double.Parse(metrics[1], System.Globalization.CultureInfo.InvariantCulture);
            var expectedWidth = Math.Min(width, GetSystemMetrics(59));
            var expectedHeight = Math.Min(height, GetSystemMetrics(60));
            Assert.InRange(actualWidth, expectedWidth - 4, expectedWidth + 4);
            Assert.InRange(actualHeight, expectedHeight - 4, expectedHeight + 4);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(layoutReportPath), TimeSpan.FromSeconds(20)));
            var report = JsonSerializer.Deserialize<UiTestLayoutReport>(
                ReadAllTextWithRetry(layoutReportPath),
                s_jsonOptions)
                ?? throw new InvalidDataException("The UI layout report is invalid.");
            Assert.True(report.Issues.Count == 0, string.Join(Environment.NewLine, report.Issues));
            Assert.Equal(ExpectedSurface(page), report.Surface);
            var expectedDpi = Environment.GetEnvironmentVariable("HD2MM_EXPECTED_DPI_SCALE");
            if (double.TryParse(expectedDpi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var expectedDpiScale))
            {
                Assert.InRange(report.DpiState.ScaleX, expectedDpiScale - 0.01, expectedDpiScale + 0.01);
                Assert.InRange(report.DpiState.ScaleY, expectedDpiScale - 0.01, expectedDpiScale + 0.01);
            }
            if (theme == "HighContrast")
            {
                Assert.Equal("HighContrast", report.ThemeState.EffectiveTheme);
                Assert.False(report.ThemeState.AnimationsEnabled);
            }

            var shellControls = new[]
            {
                "Navigation.Back",
                "Navigation.Home",
                "Navigation.Create",
                "Navigation.Downloads",
                "Navigation.Tasks",
                "Navigation.Settings",
                "Navigation.Help",
                "Window.ReportBug",
                "Window.Help",
                "Window.Minimize",
                "Window.Maximize",
                "Window.Close"
            };
            var controlsById = report.Controls.ToDictionary(static control => control.AutomationId);
            foreach (var automationId in shellControls)
            {
                Assert.True(controlsById.TryGetValue(automationId, out var control), $"Missing {automationId}.");
                Assert.False(string.IsNullOrWhiteSpace(control.Name));
                Assert.True(control.Width > 1);
                Assert.True(control.Height > 1);
                if (control.IsEnabled)
                    Assert.True(control.IsFocusable);
            }
            if (width < 900)
                Assert.Contains("Navigation.Toggle", controlsById);

            if (!string.IsNullOrWhiteSpace(targetLocale))
            {
                Assert.True(
                    SpinWait.SpinUntil(() => File.Exists(switchLayoutReportPath), TimeSpan.FromSeconds(20)),
                    $"The layout report after switching from {locale} to {targetLocale} was not produced.");
                var switchedReport = JsonSerializer.Deserialize<UiTestLayoutReport>(
                    ReadAllTextWithRetry(switchLayoutReportPath),
                    s_jsonOptions)
                    ?? throw new InvalidDataException("The switched UI layout report is invalid.");
                Assert.True(
                    switchedReport.Issues.Count == 0,
                    string.Join(Environment.NewLine, switchedReport.Issues));
                var state = switchedReport.RuntimeState
                    ?? throw new InvalidDataException("The switched UI report is missing runtime state.");
                Assert.Equal(locale, state.SourceLocale, ignoreCase: true);
                Assert.Equal(targetLocale, state.RequestedLocale, ignoreCase: true);
                Assert.Equal(targetLocale, state.EffectiveLocale, ignoreCase: true);
                var expectedCulture = targetLocale == "qps-ploc" ? "en-US" : targetLocale;
                Assert.Equal(expectedCulture, state.CurrentCulture, ignoreCase: true);
                Assert.Equal(expectedCulture, state.CurrentUiCulture, ignoreCase: true);
                Assert.True(state.PageInstancePreserved, $"{state.PageType} was recreated during locale switching.");
                Assert.True(state.BusinessServiceInstancePreserved, "DatabaseService was recreated during locale switching.");
                Assert.True(state.LayoutUpdatedBeforeNextIdle, "Layout did not update before the next idle dispatcher turn.");
            }
        }
        catch
        {
            PersistFailureArtifacts(testRoot, locale, theme, width, height, page);
            throw;
        }
        finally
        {
            if (application is not null)
            {
                if (!application.HasExited)
                {
                    try
                    {
                        application.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                    {
                        // The app may have exited between the state check and close request.
                    }
                }
                try
                {
                    if (!application.WaitForExit(5000))
                    {
                        application.Kill(entireProcessTree: true);
                        application.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // The process already exited and was removed from the process table.
                }
                application.Dispose();
            }
            if (Directory.Exists(testRoot))
                DeleteDirectoryWithRetry(testRoot);
            var parent = Path.GetDirectoryName(testRoot)!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    private static void CreateIsolatedSettings(
        string applicationDirectory,
        string locale,
        string theme,
        string page)
    {
        var game = Path.Combine(applicationDirectory, "TestGame");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        Directory.CreateDirectory(Path.Combine(game, "tools"));
        Directory.CreateDirectory(Path.Combine(game, "bin"));
        File.WriteAllBytes(Path.Combine(game, "bin", "helldivers2.exe"), []);
        var storage = Path.Combine(applicationDirectory, "TestStorage");
        var temporary = Path.Combine(applicationDirectory, "TestTemp");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(temporary);
        if (page is "ManifestEditor" or "ContextMenu")
            CreateUiTestMod(storage);
        var settings = new
        {
            SchemaVersion = 2,
            GameDirectory = game,
            StorageDirectory = storage,
            TempDirectory = temporary,
            Language = locale,
            Theme = theme == "HighContrast" ? "System" : theme,
            EnableAnimations = theme == "HighContrast",
            UseDeploymentOrder = true,
            EnableBrowserIntegration = false,
            EnableExperimentalRepair = false
        };
        File.WriteAllText(
            Path.Combine(applicationDirectory, "settings.json"),
            JsonSerializer.Serialize(settings));
    }

    private static void PersistFailureArtifacts(
        string testRoot,
        string locale,
        string theme,
        double width,
        double height,
        string page)
    {
        var screenshot = Path.Combine(testRoot, "screenshot.png");
        SpinWait.SpinUntil(() => File.Exists(screenshot), TimeSpan.FromSeconds(5));
        var caseName = string.Join(
            '-',
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture),
            locale,
            theme,
            FormattableString.Invariant($"{width}x{height}"),
            page,
            Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(AppContext.BaseDirectory, "TestResults", "UiFailures", caseName);
        Directory.CreateDirectory(destination);
        foreach (var name in new[]
                 {
                     "window-metrics.txt",
                     "layout-report.json",
                     "switch-layout-report.json",
                     "visual-tree.json",
                     "screenshot.png"
                 })
        {
            var source = Path.Combine(testRoot, name);
            if (!File.Exists(source))
                continue;
            try
            {
                File.Copy(source, Path.Combine(destination, name), overwrite: true);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
                File.Copy(source, Path.Combine(destination, name), overwrite: true);
            }
        }
    }

    private static void CreateUiTestMod(string storageDirectory)
    {
        var modDirectory = Path.Combine(storageDirectory, "Mods", "UiTestMod");
        Directory.CreateDirectory(Path.Combine(modDirectory, "files"));
        Directory.CreateDirectory(Path.Combine(modDirectory, "optional", "high_resolution_assets"));
        File.WriteAllText(Path.Combine(modDirectory, "files", "sample.patch_0"), string.Empty);
        var manifest = new
        {
            Version = 1,
            Guid = "fd469f54-68bb-4a8d-a1ca-6ca5b4b4e052",
            Name = "A long manifest editor sample mod name for multilingual layout verification",
            Description = "A deliberately long description that verifies editor labels, hints, validation messages, and option cards can grow vertically without clipping translated text.",
            Options = new[]
            {
                new
                {
                    Name = "Optional high resolution appearance package with an intentionally long title",
                    Description = "Enables additional appearance assets while preserving the complete translated description in narrow windows.",
                    Include = new[] { "files" },
                    SubOptions = new[]
                    {
                        new
                        {
                            Name = "High resolution assets and extended effects",
                            Description = "Uses the complete optional asset set for systems with sufficient graphics memory.",
                            Include = new[] { "optional/high_resolution_assets" }
                        }
                    }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(modDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static string ExpectedSurface(string page) => page switch
    {
        "Dashboard" => "DashboardPageViewModel",
        "Settings" => "SettingsPageViewModel",
        "Create" => "CreatePageViewModel",
        "Downloads" => "DownloadProgressViewModel",
        "Tasks" => "BackgroundTasksPageViewModel",
        "Help" => "HelpPageViewModel",
        "Tags" => "TagManagementPageViewModel",
        "DeploymentOrder" => "DeploymentOrderPageViewModel",
        "ManifestEditor" => "ManifestEditPageViewModel",
        "VersionDetail" => "VersionDetail",
        "Dialog" => "Dialog",
        "ContextMenu" => "ContextMenu",
        "Tooltip" => "Tooltip",
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown UI test page.")
    };

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath);
        }
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        const int attempts = 20;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (
                attempt < attempts &&
                ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string ReadAllTextWithRetry(string path)
    {
        const int attempts = 40;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (
                attempt < attempts &&
                ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed record UiTestControlReport(
        string AutomationId,
        string Name,
        bool IsEnabled,
        bool IsFocusable,
        double X,
        double Y,
        double Width,
        double Height);

    private sealed record UiTestLayoutReport(
        IReadOnlyList<UiTestControlReport> Controls,
        IReadOnlyList<string> Issues,
        UiTestRuntimeState? RuntimeState,
        UiTestThemeState ThemeState,
        string Surface,
        UiTestDpiState DpiState);

    private sealed record UiTestThemeState(
        string EffectiveTheme,
        bool AnimationsEnabled);

    private sealed record UiTestDpiState(
        double ScaleX,
        double ScaleY,
        double PixelsPerDip);

    private sealed record UiTestRuntimeState(
        string SourceLocale,
        string RequestedLocale,
        string EffectiveLocale,
        string CurrentCulture,
        string CurrentUiCulture,
        string PageType,
        bool PageInstancePreserved,
        bool BusinessServiceInstancePreserved,
        bool LayoutUpdatedBeforeNextIdle);
}
