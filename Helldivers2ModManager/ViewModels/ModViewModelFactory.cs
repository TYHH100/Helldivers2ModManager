using Helldivers2ModManager.Models;
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModViewModelFactory(
    ILogger<ModViewModel> logger,
    SettingsService settingsService,
    INexusModsService nexusModsService,
    LocalizationService localizationService,
    VersionCheckService versionCheckService,
    IDialogService dialogService)
{
    public ModViewModel Create(ModData mod) =>
        new(
            mod,
            logger,
            settingsService,
            nexusModsService,
            localizationService,
            versionCheckService,
            dialogService);
}
