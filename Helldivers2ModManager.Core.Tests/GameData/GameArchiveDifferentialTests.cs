using System.Reflection;
using Helldivers2ModManager.Core.GameData;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.GameData;

[TestClass]
public sealed class GameArchiveDifferentialTests
{
    [TestMethod]
    public async Task RealGameData_ShouldMatchLegacyUnitReferences()
    {
        var dataDirectory = GameArchiveDifferentialHarness.FindGameDataDirectory();
        if (dataDirectory is null)
        {
            Assert.Inconclusive("Configured Helldivers 2 game data is unavailable.");
            return;
        }

        var (legacyService, legacyIndex) = GameArchiveDifferentialHarness.BuildLegacyIndex(dataDirectory);
        try
        {
            var unitIds = GameArchiveDifferentialHarness.SelectUnitIds(legacyIndex);
            unitIds = [.. unitIds, 0x123456789ABCDEF0L];
            var legacyLookup = Invoke(legacyService, "ResolveGameUnitReferences", legacyIndex, unitIds)!;
            using var coreService = new GameArchiveService(Microsoft.Extensions.Logging.Abstractions.NullLogger<GameArchiveService>.Instance);
            var coreLookup = await coreService.ResolveUnitsAsync(dataDirectory, unitIds);

            CompareLookups(legacyLookup, coreLookup, unitIds);
        }
        finally
        {
            legacyIndex.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(legacyIndex, null);
            (legacyService as IDisposable)?.Dispose();
        }
    }

    private static void CompareLookups(object legacyObject, GameUnitReferenceLookup core, IReadOnlyList<long> unitIds)
    {
        Assert.IsNull(core.ErrorMessage, core.ErrorMessage);
        Assert.IsNull(GetProperty<string?>(legacyObject, "ErrorMessage"));

        var legacyReferences = GetDictionary<object>(legacyObject, "References");
        Assert.AreEqual(legacyReferences.Count, core.References.Count);
        foreach (var (unitId, legacyItem) in legacyReferences)
        {
            Assert.IsTrue(core.References.TryGetValue(unitId, out var reference), $"Missing reference {unitId:X16}");
            var legacyReference = GameArchiveDifferentialHarness.NormalizeLegacyReference(legacyItem!);
            Assert.AreEqual(legacyReference.FileId, reference!.FileId, $"FileId mismatch on {unitId:X16}");
            Assert.AreEqual(legacyReference.Version, reference.Version, $"Version mismatch on {unitId:X16}");
            Assert.IsTrue(legacyReference.LodGroupData.AsSpan().SequenceEqual(reference.LodGroupData), $"LodGroup mismatch on {unitId:X16}");
            Assert.IsTrue(legacyReference.MeshIds.AsSpan().SequenceEqual(reference.MeshIds), $"Mesh IDs mismatch on {unitId:X16}");
            Assert.AreEqual((ulong)legacyReference.GpuSize, reference.GpuSize, $"GPU size mismatch on {unitId:X16}");
            Assert.AreEqual(legacyReference.PackageName, reference.PackageName, $"Package mismatch on {unitId:X16}");
            Assert.AreEqual(legacyReference.Version.ToString("X8") + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyReference.LodGroupData)), reference.Signature);
        }

        var legacyPackageNames = GetDictionary<IReadOnlyList<string>>(legacyObject, "PackageNames");
        Assert.AreEqual(legacyPackageNames.Count, core.PackageNames.Count);
        foreach (var (unitId, names) in legacyPackageNames)
        {
            Assert.IsTrue(core.PackageNames.TryGetValue(unitId, out var coreNames));
            Assert.IsTrue(names.ToArray().SequenceEqual(coreNames!, StringComparer.OrdinalIgnoreCase), $"Package names mismatch on {unitId:X16}");
        }

        CollectionAssert.AreEquivalent(
            GetSet(legacyObject, "MissingUnitIds").ToArray(),
            core.MissingUnitIds.ToArray());
        CollectionAssert.AreEquivalent(
            GetSet(legacyObject, "AmbiguousUnitIds").ToArray(),
            core.AmbiguousUnitIds.ToArray());
        CollectionAssert.AllItemsAreUnique(unitIds.ToArray());
    }

    private static object? Invoke(object instance, string methodName, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(exception.InnerException.Message, exception.InnerException);
        }
    }

    private static T? GetProperty<T>(object instance, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return (T?)instance.GetType().GetProperty(name, flags)?.GetValue(instance);
    }

    private static Dictionary<long, TValue> GetDictionary<TValue>(object instance, string name) 
        { var raw = (System.Collections.IDictionary)GetProperty<object>(instance, name)!; return raw.Keys.Cast<long>().ToDictionary(key => key, key => (TValue)raw[key]!); }

    private static HashSet<long> GetSet(object instance, string name) =>
        [.. ((System.Collections.IEnumerable)GetProperty<System.Collections.IEnumerable>(instance, name)!).Cast<long>()];
}
