using Helldivers2ModManager.Core.GameData;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Versioning;

public sealed class VersionCheckService
{
    private readonly PatchStructureAnalyzer _analyzer;
    private readonly Func<DirectoryInfo?> _gameDataDirectoryProvider;
    private readonly GameArchiveService? _gameArchiveService;

    public VersionCheckService(
        PatchStructureAnalyzer analyzer,
        Func<DirectoryInfo?>? gameDataDirectoryProvider = null,
        GameArchiveService? gameArchiveService = null)
    {
        _analyzer = analyzer;
        _gameDataDirectoryProvider = gameDataDirectoryProvider ?? FindHelldivers2Data;
        _gameArchiveService = gameArchiveService;
    }

    public async Task<IReadOnlyDictionary<Guid, ModVersionCheckResult>> CheckAllModsAsync(
        IEnumerable<DiscoveredModInput> mods,
        CancellationToken cancellationToken = default)
    {
        var modList = mods.ToArray();
        if (modList.Length == 0)
        {
            return new Dictionary<Guid, ModVersionCheckResult>();
        }

        var analyses = new Dictionary<Guid, ModPatchAnalysis>(modList.Length);
        foreach (var mod in modList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            analyses[mod.Id] = await _analyzer.AnalyzeAsync(mod.Directory, cancellationToken).ConfigureAwait(false);
        }

        var healthyUnits = analyses.Values
            .Where(analysis => !analysis.HasBlockingStructuralIssues)
            .SelectMany(analysis => analysis.PatchFiles)
            .SelectMany(file => file.UnitDetails)
            .ToArray();
        var lookup = await ResolveGameReferencesAsync(
            healthyUnits.Select(static unit => unit.FileId).Distinct(),
            cancellationToken).ConfigureAwait(false);
        var reference = VersionCompatibilityEvaluator.ResolveBatchReference(lookup, healthyUnits);
        var results = new Dictionary<Guid, ModVersionCheckResult>(modList.Length);
        foreach (var mod in modList)
        {
            var analysis = analyses[mod.Id];
            var units = analysis.PatchFiles.SelectMany(file => file.UnitDetails).ToArray();
            var comparable = reference.GameDataAvailable && lookup is not null
                ? units.Where(unit => lookup.References.ContainsKey(unit.FileId)).Select(unit => unit.Version).ToArray()
                : units.Select(unit => unit.Version).ToArray();
            var status = VersionCompatibilityEvaluator.Evaluate(
                analysis.HasBlockingStructuralIssues,
                comparable,
                reference.ReferenceVersion,
                units.Select(unit => unit.Version).ToArray());
            results[mod.Id] = new(
                mod.Id,
                status,
                reference.ReferenceVersion ?? 0,
                DateTimeOffset.Now,
                units,
                analysis,
                reference.GameDataAvailable && lookup is not null
                    ? units.Where(unit => !lookup.References.ContainsKey(unit.FileId)).Select(unit => unit.FileId).ToHashSet()
                    : new HashSet<long>());
        }

        return results;
    }

    public async Task<ModVersionCheckResult?> CheckSingleModAsync(
        DiscoveredModInput mod,
        uint? fallbackVersion = null,
        bool includeDetailedAnalysis = true,
        CancellationToken cancellationToken = default)
    {
        var analysis = await _analyzer.AnalyzeAsync(mod.Directory, cancellationToken).ConfigureAwait(false);
        var units = analysis.PatchFiles.SelectMany(file => file.UnitDetails).ToArray();
        var lookup = await ResolveGameReferencesAsync(units.Select(unit => unit.FileId).Distinct(), cancellationToken).ConfigureAwait(false);
        var gameAvailable = lookup is not null && string.IsNullOrEmpty(lookup.ErrorMessage) && lookup.References.Count > 0;
        var comparable = gameAvailable
            ? units.Where(unit => lookup!.References.ContainsKey(unit.FileId)).Select(unit => unit.Version).ToArray()
            : units.Select(unit => unit.Version).ToArray();
        var effectiveReference = gameAvailable
            ? VersionCompatibilityEvaluator.MostCommonVersion(lookup!.References.Values.Select(reference => reference.Version))
            : fallbackVersion;
        var status = VersionCompatibilityEvaluator.Evaluate(
            analysis.HasBlockingStructuralIssues,
            comparable,
            effectiveReference,
            units.Select(unit => unit.Version).ToArray());
        var reportedVersion = (effectiveReference ??
            (units.Length != 0 ? VersionCompatibilityEvaluator.MostCommonVersion(units.Select(unit => unit.Version)) : 0u)).GetValueOrDefault();
        return new(
            mod.Id,
            status,
            reportedVersion,
            DateTimeOffset.Now,
            units,
            includeDetailedAnalysis ? analysis : null,
            gameAvailable
                ? units.Where(unit => !lookup!.References.ContainsKey(unit.FileId)).Select(unit => unit.FileId).ToHashSet()
                : new HashSet<long>());
    }

    private async Task<GameUnitReferenceLookup?> ResolveGameReferencesAsync(
        IEnumerable<long> unitIds,
        CancellationToken cancellationToken)
    {
        var ids = unitIds.ToArray();
        if (ids.Length == 0 || _gameDataDirectoryProvider() is not { Exists: true } dataDirectory)
        {
            return null;
        }

        try
        {
            var service = _gameArchiveService ?? new GameArchiveService(NoOpLogger.Instance);
            return await service.ResolveUnitsAsync(
                dataDirectory,
                ids,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return GameUnitReferenceLookup.Empty with { ErrorMessage = "GameDataUnavailable" };
        }
    }

    private static DirectoryInfo? FindHelldivers2Data()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HELLDIVERS2_DATA_PATH"),
            Path.Combine("C:", "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2", "data"),
        };
        return candidates.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new DirectoryInfo(path!))
            .FirstOrDefault(file => file.Exists);
    }
}

public sealed record DiscoveredModInput(Guid Id, string Name, DirectoryInfo Directory);



internal sealed class NoOpLogger : ILogger<GameArchiveService>
{
    public static NoOpLogger Instance { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}
