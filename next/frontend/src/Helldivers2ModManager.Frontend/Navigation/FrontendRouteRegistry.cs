using Helldivers2ModManager.Frontend.ViewModels.Pages;

namespace Helldivers2ModManager.Frontend.Navigation;

public static class FrontendRouteRegistry
{
    private static readonly Dictionary<string, FrontendRoute> Routes = CreateRoutes();

    public static IReadOnlyCollection<FrontendRoute> All => Routes.Values.ToArray();

    public static FrontendRoute Get(string key) =>
        Routes.TryGetValue(key, out var route)
            ? route
            : throw new KeyNotFoundException($"Unknown frontend route '{key}'.");

    private static Dictionary<string, FrontendRoute> CreateRoutes() => new(StringComparer.Ordinal)
    {
        ["Library"] = new("Library", "Library", "Nav.Library", "Page.Library.Description", typeof(LibraryPageViewModel)),
        ["Deployment.Order"] = new("Deployment.Order", "Deployment", "Nav.DeploymentOrder", "Page.DeploymentOrder.Description", typeof(DeploymentOrderPageViewModel)),
        ["Deployment.Tasks"] = new("Deployment.Tasks", "Deployment", "Nav.BackgroundTasks", "Page.BackgroundTasks.Description", typeof(BackgroundTasksPageViewModel)),
        ["Tools.Create"] = new("Tools.Create", "Tools", "Nav.Create", "Page.Create.Description", typeof(CreatePageViewModel)),
        ["Tools.Edit"] = new("Tools.Edit", "Tools", "Nav.Edit", "Page.Edit.Description", typeof(EditPageViewModel)),
        ["Tools.Manifest"] = new("Tools.Manifest", "Tools", "Nav.ManifestEdit", "Page.ManifestEdit.Description", typeof(ManifestEditPageViewModel)),
        ["Tools.Tags"] = new("Tools.Tags", "Tools", "Nav.TagManagement", "Page.TagManagement.Description", typeof(TagManagementPageViewModel)),
        ["Tools.AutoTagPairing"] = new("Tools.AutoTagPairing", "Tools", "Nav.AutoTagPairing", "Page.AutoTagPairing.Description", typeof(AutoTagPairingPageViewModel)),
        ["Tools.NexusDownload"] = new("Tools.NexusDownload", "Tools", "Nav.NexusDownload", "Page.NexusDownload.Description", typeof(NexusDownloadPageViewModel)),
        ["Analysis.ResourceViewer"] = new("Analysis.ResourceViewer", "Analysis", "Nav.ResourceViewer", "Page.ResourceViewer.Description", typeof(PatchResourceViewerPageViewModel)),
        ["Analysis.VersionCheck"] = new("Analysis.VersionCheck", "Analysis", "Nav.VersionCheck", "Page.VersionCheck.Description", typeof(VersionCheckPageViewModel)),
        ["Analysis.Conflicts"] = new("Analysis.Conflicts", "Analysis", "Nav.ConflictScan", "Page.ConflictScan.Description", typeof(ConflictScanPageViewModel)),
        ["Analysis.ArmorReuse"] = new("Analysis.ArmorReuse", "Analysis", "Nav.ArmorReuse", "Page.ArmorReuse.Description", typeof(ArmorReusePageViewModel)),
        ["Analysis.ModelPreview"] = new("Analysis.ModelPreview", "Analysis", "Nav.ModelPreview", "Page.ModelPreview.Description", typeof(ModelPreviewPageViewModel)),
        ["Analysis.Bisect"] = new("Analysis.Bisect", "Analysis", "Nav.Bisect", "Page.Bisect.Description", typeof(BisectPageViewModel)),
        ["Diagnostics.BackendTestCenter"] = new("Diagnostics.BackendTestCenter", "Analysis", "Nav.BackendTestCenter", "Page.BackendTestCenter.Description", typeof(DiagnosticsPageViewModel), IsDiagnostic: true),
        ["System.Settings"] = new("System.Settings", "System", "Nav.Settings", "Page.Settings.Description", typeof(SettingsPageViewModel)),
        ["System.Help"] = new("System.Help", "System", "Nav.Help", "Page.Help.Description", typeof(HelpPageViewModel)),
    };
}
