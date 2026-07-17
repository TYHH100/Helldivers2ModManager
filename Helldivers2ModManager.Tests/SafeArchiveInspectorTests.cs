using System.IO.Compression;
using Helldivers2ModManager.Core.Archives;
using Helldivers2ModManager.Infrastructure.Archives;
using Helldivers2ModManager.Infrastructure.Security;
using SharpSevenZip;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class SafeArchiveInspectorTests
{
    private static readonly object s_initializationLock = new();
    private static bool s_initialized;

    [Fact]
    public async Task TraversalEntryIsRejectedBeforeAnyFileIsExtracted()
    {
        EnsureSharpSevenZipInitialized();
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = System.IO.Path.Combine(temporaryDirectory.Path, "malicious.zip");
        CreateZip(archivePath, ("../escape.txt", "escaped"));
        var destination = System.IO.Path.Combine(temporaryDirectory.Path, "extract");
        Directory.CreateDirectory(destination);
        var inspector = new SafeArchiveInspector(new SafePathPolicy());

        var result = await inspector.PlanExtractionAsync(
            archivePath,
            destination,
            ArchiveSafetyLimits.Default,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(System.IO.Path.Combine(temporaryDirectory.Path, "escape.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task EntryAndExpandedSizeLimitsAreEnforcedDuringPlanning()
    {
        EnsureSharpSevenZipInitialized();
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = System.IO.Path.Combine(temporaryDirectory.Path, "limited.zip");
        CreateZip(archivePath, ("one.txt", "12345"), ("two.txt", "67890"));
        var destination = System.IO.Path.Combine(temporaryDirectory.Path, "extract");
        Directory.CreateDirectory(destination);
        var inspector = new SafeArchiveInspector(new SafePathPolicy());

        var entryResult = await inspector.PlanExtractionAsync(
            archivePath,
            destination,
            ArchiveSafetyLimits.Default with { MaximumEntries = 1, RequiredFreeSpaceReserveBytes = 0 },
            CancellationToken.None);
        var sizeResult = await inspector.PlanExtractionAsync(
            archivePath,
            destination,
            ArchiveSafetyLimits.Default with { MaximumExpandedBytes = 9, RequiredFreeSpaceReserveBytes = 0 },
            CancellationToken.None);

        Assert.Equal("Archive.EntryLimitExceeded", entryResult.ErrorCode);
        Assert.Equal("Archive.ExpandedSizeLimitExceeded", sizeResult.ErrorCode);
    }

    [Fact]
    public async Task ValidArchiveIsExtractedEntryByEntryFromValidatedPlan()
    {
        EnsureSharpSevenZipInitialized();
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = System.IO.Path.Combine(temporaryDirectory.Path, "valid.zip");
        CreateZip(archivePath, ("folder/file.txt", "content"));
        var destination = System.IO.Path.Combine(temporaryDirectory.Path, "extract");
        Directory.CreateDirectory(destination);
        var inspector = new SafeArchiveInspector(new SafePathPolicy());
        var limits = ArchiveSafetyLimits.Default with { RequiredFreeSpaceReserveBytes = 0 };

        var plan = await inspector.PlanExtractionAsync(
            archivePath,
            destination,
            limits,
            CancellationToken.None);
        Assert.True(plan.IsSuccess);
        var result = await inspector.ExtractAsync(plan.Value!, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            "content",
            await File.ReadAllTextAsync(
                System.IO.Path.Combine(destination, "folder", "file.txt"),
                CancellationToken.None));
    }

    private static void EnsureSharpSevenZipInitialized()
    {
        lock (s_initializationLock)
        {
            if (s_initialized)
                return;
            SharpSevenZipBase.SetLibraryPath(System.IO.Path.Combine(AppContext.BaseDirectory, "7z.dll"));
            s_initialized = true;
        }
    }

    private static void CreateZip(string archivePath, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
