using Jalium.UI;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private void StartTutorial()
    {
        if (_tutorialOverlay is null || _layout is null)
            return;
        _layout.MusicLayer.Visibility = Visibility.Collapsed;
        _tutorialOverlay.Start();
    }

    private async Task CompleteTutorialAsync()
    {
        try
        {
            if (settings.Initialized && !settings.IsReadonly)
            {
                settings.FirstRunTutorialCompleted = true;
                await settings.SaveAsync();
            }
        }
        finally
        {
            if (_layout is not null && _musicPlayer is not null)
                _layout.MusicLayer.Visibility = Visibility.Visible;
        }
    }
}
