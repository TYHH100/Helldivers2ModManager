namespace Helldivers2ModManager.Core.Settings;

public interface ISettingsStore
{
    Task<AppSettingsSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettingsSnapshot snapshot, CancellationToken cancellationToken);
}
