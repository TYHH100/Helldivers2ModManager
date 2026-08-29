using System.Net.Http;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Analysis;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Preview;
using Helldivers2ModManager.Core.Repair;
using Helldivers2ModManager.Core.Versioning;
using Helldivers2ModManager.Frontend.Navigation;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.ViewModels;
using Helldivers2ModManager.Frontend.ViewModels.Pages;
using Helldivers2ModManager.Frontend.Views;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Frontend;

internal static class FrontendComposition
{
    public static IServiceCollection AddFrontend(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var localization = new LocalizationCatalog();
        LocalizationSource.Catalog = localization;
        services.AddSingleton(localization);
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<ModLibraryService>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<DeploymentServiceFacade>();
        services.AddSingleton<ModCreationService>();
        services.AddSingleton<ModSelectionStore>();
        services.AddSingleton<ModManifestEditorService>();
        services.AddSingleton<TagManagementService>();
        services.AddSingleton<AutoTagPairingService>();
        services.AddSingleton<AutoTaggingFacade>();
        services.AddSingleton(new HttpClient
        {
            BaseAddress = new Uri("https://api.nexusmods.com/v3/"),
            Timeout = TimeSpan.FromSeconds(300),
        });
        services.AddSingleton<NexusDownloadService>();
        services.AddSingleton<PatchStructureAnalyzer>();
        services.AddSingleton(sp => new VersionCheckService(
            sp.GetRequiredService<PatchStructureAnalyzer>(),
            () => sp.GetRequiredService<VersionCheckFacade>().ResolveGameDataDirectory(),
            sp.GetRequiredService<GameArchiveService>()));
        services.AddSingleton<VersionCheckFacade>();
        services.AddSingleton<ConflictAnalysisFacade>();
        services.AddSingleton<LibraryDeploymentService>();
        services.AddSingleton<BisectService>();
        services.AddSingleton<ArmorReuseService>();
        services.AddSingleton<ArmorReuseFacade>();
        services.AddSingleton<PatchResourceInspector>();
        services.AddSingleton<MetadataRepairService>();
        services.AddSingleton<AssistedRepairService>();
        services.AddSingleton<CompanionRecoveryService>();
        services.AddSingleton<DiagnosticsFacade>();
        services.AddSingleton<INavigationStore, NavigationStore>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        services.AddTransient<LibraryPageViewModel>();
        services.AddTransient<DeploymentOrderPageViewModel>();
        services.AddTransient<BackgroundTasksPageViewModel>();
        services.AddTransient<CreatePageViewModel>();
        services.AddTransient<EditPageViewModel>();
        services.AddTransient<ManifestEditPageViewModel>();
        services.AddTransient<TagManagementPageViewModel>();
        services.AddTransient<AutoTagPairingPageViewModel>();
        services.AddTransient<NexusDownloadPageViewModel>();
        services.AddTransient<PatchResourceViewerPageViewModel>();
        services.AddTransient<VersionCheckPageViewModel>();
        services.AddTransient<ConflictScanPageViewModel>();
        services.AddTransient<ArmorReusePageViewModel>();
        services.AddTransient<ModelPreviewPageViewModel>();
        services.AddTransient<BisectPageViewModel>();
        services.AddTransient<DiagnosticsPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<HelpPageViewModel>();

        return services;
    }
}
