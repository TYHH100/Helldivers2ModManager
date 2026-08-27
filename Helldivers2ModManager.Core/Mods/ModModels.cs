using Helldivers2ModManager.Core.Common;

namespace Helldivers2ModManager.Core.Mods;

public sealed record DiscoveredMod(DirectoryInfo Directory, IModManifest Manifest);

public sealed record ModDiscoveryResult(
    IReadOnlyList<DiscoveredMod> Mods,
    IReadOnlyList<Error> Problems);

public sealed record ModUpdateResult(
    IModManifest Manifest,
    HashComparison Comparison,
    int DeletedFiles,
    bool FilesChanged);

public enum ModUpdateStage
{
    HashingCurrent,
    HashingNew,
    Comparing,
    Updating,
}

public sealed record ModUpdateProgress(
    ModUpdateStage Stage,
    string? CurrentFile,
    int ProcessedCount,
    int TotalCount,
    int CacheHits,
    int ChangedCount);

public sealed record PatchFileInfo(string Name, string BaseName, int Index, PatchFileKind Kind);

public enum PatchFileKind
{
    Unknown,
    Main,
    Stream,
    GpuResources,
}

public enum ArchiveExportFormat
{
    Zip,
    SevenZipFast,
    SevenZipStandard,
    SevenZipHigh,
    SevenZipUltra,
}

public enum ArchiveImportProblemKind
{
    CannotReadArchive,
    NoManifestFound,
    Duplicate,
    EmptyOptions,
    EmptySubOptions,
    EmptyIncludes,
    InvalidPath,
}

public sealed record ArchiveImportProblem(
    string ArchivePath,
    ArchiveImportProblemKind Kind,
    string Detail);

public sealed record ArchiveImportResult(
    IReadOnlyList<DiscoveredMod> ImportedMods,
    IReadOnlyList<ArchiveImportProblem> Problems);

public interface IRecycleBinAdapter
{
    Task SendDirectoryToRecycleBinAsync(string directoryPath, CancellationToken cancellationToken = default);
}
