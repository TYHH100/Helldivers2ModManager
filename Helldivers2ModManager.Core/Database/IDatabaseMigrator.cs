namespace Helldivers2ModManager.Core.Database;

public interface IDatabaseMigrator
{
    Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken);

    Task MigrateAsync(int targetVersion, CancellationToken cancellationToken);
}
