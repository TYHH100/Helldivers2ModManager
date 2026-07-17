using Helldivers2ModManager.Core.Compatibility;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton, Contract = typeof(IGameReferenceProvider))]
internal sealed class GameReferenceProvider(VersionCheckService legacyReader) : IGameReferenceProvider
{
    public Task<GameReferenceSnapshot> GetReferencesAsync(
        string gameDataDirectory,
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken) =>
        legacyReader.GetCoreGameReferencesAsync(gameDataDirectory, unitIds, cancellationToken);
}
