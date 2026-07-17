using Helldivers2ModManager.Core.Compatibility;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class VersionCheckCoordinatorTests
{
    [Fact]
    public async Task CoordinatorUsesDistinctScannedUnitIdsAndCurrentGameDirectory()
    {
        var scan = new PatchScanResult(
            [
                new PatchUnitObservation(10, 1, "mod.patch_0", 100, 20),
                new PatchUnitObservation(10, 1, "mod.patch_0", 200, 20),
                new PatchUnitObservation(20, 2, "mod.patch_0", 300, 20)
            ],
            []);
        var scanner = new StubScanner(scan);
        var provider = new RecordingReferenceProvider(new GameReferenceSnapshot(
            ReferenceSource.CurrentGameFiles,
            "game-fingerprint",
            new Dictionary<long, GameUnitReference>()));
        var evaluator = new RecordingEvaluator(new CompatibilityResult(
            CompatibilityState.Unknown,
            ReferenceSource.CurrentGameFiles,
            "game-fingerprint",
            [],
            [],
            0));
        var coordinator = new VersionCheckCoordinator(scanner, provider, evaluator);

        var result = await coordinator.CheckAsync("mod.patch_0", "game-data", CancellationToken.None);

        Assert.Equal(CompatibilityState.Unknown, result.State);
        Assert.Equal("game-data", provider.GameDataDirectory);
        Assert.Equal([10, 20], provider.UnitIds);
        Assert.Same(scan, evaluator.Scan);
        Assert.Same(provider.Snapshot, evaluator.Reference);
    }

    [Fact]
    public async Task CoordinatorPreservesUnknownWhenNoAuthoritativeReferenceExists()
    {
        var scanner = new StubScanner(new PatchScanResult(
            [new PatchUnitObservation(10, 1, "mod.patch_0", 100, 20)],
            []));
        var provider = new RecordingReferenceProvider(new GameReferenceSnapshot(
            ReferenceSource.Unavailable,
            null,
            new Dictionary<long, GameUnitReference>(),
            "Reference.Unavailable"));
        var coordinator = new VersionCheckCoordinator(scanner, provider, new CompatibilityEvaluator());

        var result = await coordinator.CheckAsync("mod.patch_0", "game-data", CancellationToken.None);

        Assert.Equal(CompatibilityState.Unknown, result.State);
        Assert.Equal(ReferenceSource.Unavailable, result.ReferenceSource);
        Assert.Equal(0, result.Confidence);
    }

    private sealed class StubScanner(PatchScanResult result) : IPatchScanner
    {
        public Task<PatchScanResult> ScanAsync(string patchPath, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class RecordingReferenceProvider(GameReferenceSnapshot snapshot) : IGameReferenceProvider
    {
        public GameReferenceSnapshot Snapshot { get; } = snapshot;
        public string? GameDataDirectory { get; private set; }
        public IReadOnlyCollection<long> UnitIds { get; private set; } = [];

        public Task<GameReferenceSnapshot> GetReferencesAsync(
            string gameDataDirectory,
            IReadOnlyCollection<long> unitIds,
            CancellationToken cancellationToken)
        {
            GameDataDirectory = gameDataDirectory;
            UnitIds = unitIds;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class RecordingEvaluator(CompatibilityResult result) : ICompatibilityEvaluator
    {
        public PatchScanResult? Scan { get; private set; }
        public GameReferenceSnapshot? Reference { get; private set; }

        public CompatibilityResult Evaluate(PatchScanResult scan, GameReferenceSnapshot reference)
        {
            Scan = scan;
            Reference = reference;
            return result;
        }
    }
}
