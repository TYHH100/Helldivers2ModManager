using Helldivers2ModManager.Core.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class RepairDisclaimerService(
    ILogger<RepairDisclaimerService> logger,
    SettingsService settingsService,
    LocalizationService localizationService,
    IDialogService dialogService)
{
    private readonly SemaphoreSlim _acceptanceLock = new(1, 1);

    public async Task<bool> EnsureAcceptedAsync(CancellationToken cancellationToken)
    {
        if (!settingsService.Initialized)
        {
            await ShowSaveErrorAsync(
                localizationService["VersionCheckDisclaimer.SettingsUnavailable"],
                cancellationToken);
            return false;
        }

        if (settingsService.RepairDisclaimerAccepted)
            return true;

        if (!await dialogService.ShowAsync(
            new DialogRequest(
                localizationService["VersionCheckDisclaimer.Title"],
                localizationService["VersionCheckDisclaimer.Message"]),
            cancellationToken))
            return false;

        await _acceptanceLock.WaitAsync(cancellationToken);
        try
        {
            if (settingsService.RepairDisclaimerAccepted)
                return true;

            settingsService.RepairDisclaimerAccepted = true;
            await settingsService.SaveAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist the mod repair disclaimer acceptance");
            try
            {
                if (settingsService.Initialized && !settingsService.IsReadonly)
                    settingsService.RepairDisclaimerAccepted = false;
            }
            catch (Exception rollbackException)
            {
                logger.LogWarning(rollbackException, "Failed to roll back repair disclaimer acceptance");
            }

            await ShowSaveErrorAsync(ex.Message, cancellationToken);
            return false;
        }
        finally
        {
            _acceptanceLock.Release();
        }
    }

    private Task<bool> ShowSaveErrorAsync(string message, CancellationToken cancellationToken) =>
        dialogService.ShowAsync(
            new DialogRequest(
                localizationService["MessageBox.Error"],
                localizationService.Format("VersionCheckDisclaimer.SaveFailed", new { message })),
            cancellationToken);
}
