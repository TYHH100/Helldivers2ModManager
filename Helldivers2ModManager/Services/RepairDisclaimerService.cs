using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class RepairDisclaimerService(
    ILogger<RepairDisclaimerService> logger,
    SettingsService settingsService,
    LocalizationService localizationService)
{
    private bool _acceptancePending;

    public bool ContinueOrRequest(Action continuation)
    {
        if (_acceptancePending)
            return false;

        if (!settingsService.Initialized)
        {
            ShowSaveError(localizationService["VersionCheckDisclaimer.SettingsUnavailable"]);
            return false;
        }

        if (settingsService.RepairDisclaimerAccepted)
            return true;

        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = localizationService["VersionCheckDisclaimer.Title"],
            Message = localizationService["VersionCheckDisclaimer.Message"],
            Confirm = () => _ = AcceptAndContinueAsync(continuation)
        });
        return false;
    }

    private async Task AcceptAndContinueAsync(Action continuation)
    {
        if (_acceptancePending)
            return;

        _acceptancePending = true;
        var accepted = false;
        try
        {
            settingsService.RepairDisclaimerAccepted = true;
            await settingsService.SaveAsync();
            accepted = true;
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

            ShowSaveError(ex.Message);
        }
        finally
        {
            _acceptancePending = false;
        }

        if (accepted)
            continuation();
    }

    private void ShowSaveError(string message)
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
        {
            Message = localizationService["VersionCheckDisclaimer.SaveFailed"]
                .Replace("{message}", message)
        });
    }
}
