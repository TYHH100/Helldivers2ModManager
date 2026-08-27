using System.IO.Compression;
using System.Text;
using Helldivers2ModManager.Core.Common;
using Microsoft.Extensions.Logging;
using SharpSevenZip;

namespace Helldivers2ModManager.Core.Mods;

using SevenZipCompressionLevel = SharpSevenZip.CompressionLevel;

public sealed record ArchiveProgress(double Progress, string CurrentFile, long BytesProcessed);

public sealed record NestedArchiveProgress(int Index, int TotalCount, string ArchiveName);

public sealed class ModArchiveService(
    ModDirectoryService modDirectoryService,
    ILogger<ModArchiveService> logger)
{
    private static readonly string[] NestedArchiveExtensions = [".zip", ".7z", ".rar", ".tar"];
    private static readonly string[] ExcludedArchiveExtensions = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"];
    private static int initialized;

    public async Task<ArchiveImportResult> ImportArchiveAsync(
        FileInfo archive,
        DirectoryInfo storageDirectory,
        DirectoryInfo tempDirectory,
        bool deleteExistingToRecycleBin = false,
        Action<NestedArchiveProgress>? nestedProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.Exists)
        {
            return Failure(archive.FullName, ArchiveImportProblemKind.CannotReadArchive, "Archive does not exist.", []);
        }

        var extractionDirectory = CreateTemporaryDirectory(archive, tempDirectory);
        try
        {
            await ExtractAsync(archive, extractionDirectory, cancellationToken).ConfigureAwait(false);
            FlattenSingleRootDirectory(extractionDirectory);

            var manifestFile = new FileInfo(Path.Combine(extractionDirectory.FullName, "manifest.json"));
            if (!manifestFile.Exists)
            {
                var nestedArchives = extractionDirectory
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(file => NestedArchiveExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(file => file.FullName, StringComparer.Ordinal)
                    .ToArray();
                if (nestedArchives.Length > 0)
                {
                    var importedMods = new List<DiscoveredMod>();
                    var problems = new List<ArchiveImportProblem>();
                    for (var index = 0; index < nestedArchives.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        nestedProgress?.Invoke(new(index, nestedArchives.Length, nestedArchives[index].Name));
                        try
                        {
                            var nestedResult = await ImportArchiveAsync(
                                nestedArchives[index],
                                storageDirectory,
                                tempDirectory,
                                deleteExistingToRecycleBin,
                                nestedProgress,
                                cancellationToken).ConfigureAwait(false);
                            importedMods.AddRange(nestedResult.ImportedMods);
                            problems.AddRange(nestedResult.Problems);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            logger.LogError(exception, "Failed to import nested archive {Archive}", nestedArchives[index].FullName);
                            problems.Add(new(nestedArchives[index].FullName, ArchiveImportProblemKind.CannotReadArchive, exception.Message));
                        }
                    }

                    return new(importedMods, problems);
                }
            }

            var hadManifest = manifestFile.Exists;
            var manifest = hadManifest
                ? ModManifest.DeserializeFromFile(manifestFile, logger)
                : ModManifest.InferFromDirectory(extractionDirectory, logger);
            var validationProblems = ValidateManifestPaths(manifest, extractionDirectory);
            if (validationProblems.Count > 0)
            {
                return new([], validationProblems);
            }

            if (!manifestFile.Exists)
            {
                ModManifest.SaveToFile(manifest, extractionDirectory);
            }

            var imported = await modDirectoryService.ImportDirectoryAsync(
                extractionDirectory,
                storageDirectory,
                replaceExisting: true,
                deleteExistingToRecycleBin: deleteExistingToRecycleBin,
                mutateSourceManifest: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            List<ArchiveImportProblem> importProblems = hadManifest
                ? []
                : [new(archive.FullName, ArchiveImportProblemKind.NoManifestFound, "manifest.json was missing and was inferred.")];
            return imported.Succeeded
                ? new([imported.Value!], importProblems)
                : imported.Error.Code == CoreErrorCode.Conflict
                    ? new([], [new(archive.FullName, ArchiveImportProblemKind.Duplicate, imported.Error.Message)])
                    : new([], [new(archive.FullName, ArchiveImportProblemKind.InvalidPath, imported.Error.Message)]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to import archive {Archive}", archive.FullName);
            return Failure(archive.FullName, ArchiveImportProblemKind.CannotReadArchive, exception.Message, []);
        }
        finally
        {
            TryDelete(extractionDirectory);
        }
    }

    public async Task ExtractAsync(
        FileInfo archive,
        DirectoryInfo destination,
        CancellationToken cancellationToken = default)
    {
        EnsureNativeLibrary();
        await Task.Run(() =>
        {
            using var extractor = new SharpSevenZipExtractor(archive.FullName);
            extractor.ExtractArchive(destination.FullName);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DirectoryInfo> PrepareUpdateSourceAsync(
        FileInfo archive,
        DirectoryInfo destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Exists)
        {
            destination.Delete(true);
        }

        destination.Create();
        await ExtractAsync(archive, destination, cancellationToken).ConfigureAwait(false);
        FlattenSingleRootDirectory(destination);
        return destination;
    }

    public static bool IsExcludedFromExport(FileInfo file) =>
        ExcludedArchiveExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".hd2mm-backup", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".hd2mm-backup.json", StringComparison.OrdinalIgnoreCase);

    public static long CalculateExportSize(DirectoryInfo directory)
    {
        return directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(static file => !IsExcludedFromExport(file))
            .Sum(static file => file.Length);
    }

    public async Task ExportAsync(
        DirectoryInfo modDirectory,
        string outputPath,
        ArchiveExportFormat format,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!modDirectory.Exists)
        {
            throw new DirectoryNotFoundException($"Mod directory does not exist: {modDirectory.FullName}");
        }

        var outputFullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var files = modDirectory.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(static file => !IsExcludedFromExport(file))
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();
        var totalBytes = files.Sum(static file => file.Length);

        if (format == ArchiveExportFormat.Zip)
        {
            await ExportZipAsync(modDirectory, outputFullPath, files, totalBytes, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExportSevenZipAsync(modDirectory, outputFullPath, format, files, totalBytes, progress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new(1d, string.Empty, totalBytes));
    }

    private static async Task ExportZipAsync(
        DirectoryInfo modDirectory,
        string outputPath,
        IReadOnlyList<FileInfo> files,
        long totalBytes,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        long writtenBytes = 0;
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(modDirectory.FullName, file.FullName).Replace('\\', '/');
            await using var entryStream = archive.CreateEntry(relativePath, System.IO.Compression.CompressionLevel.Optimal).Open();
            await using var sourceStream = file.OpenRead();
            await sourceStream.CopyToAsync(entryStream, 81920, cancellationToken).ConfigureAwait(false);
            writtenBytes += file.Length;
            progress?.Report(new(totalBytes > 0 ? Math.Min((double)writtenBytes / totalBytes, 1d) : 1d, file.Name, writtenBytes));
        }
    }

    private static async Task ExportSevenZipAsync(
        DirectoryInfo modDirectory,
        string outputPath,
        ArchiveExportFormat format,
        IReadOnlyList<FileInfo> files,
        long totalBytes,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureNativeLibrary();
        var (compressionLevel, dictionarySize) = format switch
        {
            ArchiveExportFormat.SevenZipFast => (SevenZipCompressionLevel.Fast, "8m"),
            ArchiveExportFormat.SevenZipHigh => (SevenZipCompressionLevel.High, "64m"),
            ArchiveExportFormat.SevenZipUltra => (SevenZipCompressionLevel.Ultra, "128m"),
            _ => (SevenZipCompressionLevel.Normal, "32m"),
        };
        var rootLength = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modDirectory.FullName)).Length + 1;
        var currentFile = string.Empty;

        await Task.Run(() =>
        {
            var compressor = new SharpSevenZipCompressor
            {
                ArchiveFormat = OutArchiveFormat.SevenZip,
                CompressionMethod = CompressionMethod.Lzma2,
                CompressionLevel = compressionLevel,
                DirectoryStructure = true,
                PreserveDirectoryRoot = false,
            };
            compressor.CustomParameters.Add("d", dictionarySize);
            compressor.FileCompressionStarted += (_, args) => currentFile = Path.GetFileName(args.FileName);
            compressor.Compressing += (_, args) =>
            {
                var ratio = Math.Clamp(args.PercentDone / 100d, 0d, 1d);
                progress?.Report(new(ratio, currentFile, (long)(totalBytes * ratio)));
            };
            compressor.CompressFiles(outputPath, rootLength, files.Select(static file => file.FullName).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<ArchiveImportProblem> ValidateManifestPaths(IModManifest manifest, DirectoryInfo directory)
    {
        var problems = new List<ArchiveImportProblem>();
        switch (manifest)
        {
            case LegacyModManifest { Options: { } options }:
                ValidateOptionList(options, directory, problems);
                break;
            case V1ModManifest { Options: { } options }:
                ValidateOptionList(options.Select(static option => option.Name), directory, problems);
                if (options.Any(static option => option.SubOptions is { Count: 0 }))
                {
                    problems.Add(new(directory.FullName, ArchiveImportProblemKind.EmptySubOptions, "A V1 option contains an empty sub-options array."));
                }
                if (options.Any(static option => option.SubOptions?.Any(static subOption => subOption.Include.Count == 0) ?? false))
                {
                    problems.Add(new(directory.FullName, ArchiveImportProblemKind.EmptyIncludes, "A V1 sub-option contains an empty includes array."));
                }
                foreach (var include in options.SelectMany(static option => option.Include ?? []))
                {
                    ValidateInclude(include, directory, problems);
                }
                foreach (var subOption in options.SelectMany(static option => option.SubOptions ?? []))
                {
                    foreach (var include in subOption.Include)
                    {
                        ValidateInclude(include, directory, problems);
                    }
                }
                break;
        }

        return problems;
    }

    private void ValidateOptionList(IEnumerable<string> names, DirectoryInfo directory, List<ArchiveImportProblem> problems)
    {
        var values = names.ToArray();
        if (values.Length == 0)
        {
            problems.Add(new(directory.FullName, ArchiveImportProblemKind.EmptyOptions, "The manifest options array is empty."));
        }

        foreach (var name in values.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            ValidateInclude(name, directory, problems);
        }
    }

    private static void ValidateInclude(string path, DirectoryInfo directory, List<ArchiveImportProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var resolved = ModManifestSanitizer.TryResolveManifestRelativePath(directory, path, out _);
        if (!resolved)
        {
            problems.Add(new(directory.FullName, ArchiveImportProblemKind.InvalidPath, $"Unsafe manifest path: {path}"));
        }
    }

    private static ArchiveImportResult Failure(
        string archivePath,
        ArchiveImportProblemKind kind,
        string detail,
        List<DiscoveredMod> mods) => new(mods, [new(archivePath, kind, detail)]);

    private static void FlattenSingleRootDirectory(DirectoryInfo directory)
    {
        var directories = directory.GetDirectories();
        if (directories.Length != 1 || directory.GetFiles().Length != 0)
        {
            return;
        }

        foreach (var file in directories[0].EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory.FullName, Path.GetRelativePath(directories[0].FullName, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file.FullName, target, true);
        }

        directories[0].Delete(true);
    }

    private static DirectoryInfo CreateTemporaryDirectory(FileInfo archive, DirectoryInfo tempDirectory)
    {
        tempDirectory.Create();
        var stem = Path.GetFileNameWithoutExtension(archive.Name);
        foreach (var character in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(character, '_');
        }

        return tempDirectory.CreateSubdirectory($"{stem}_{Guid.NewGuid():N}");
    }

    private void TryDelete(DirectoryInfo directory)
    {
        try
        {
            if (directory.Exists)
            {
                directory.Delete(true);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remove temporary extraction directory {Directory}", directory.FullName);
        }
    }

    private static void EnsureNativeLibrary()
    {
        if (Interlocked.Exchange(ref initialized, 1) == 1)
        {
            return;
        }

        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDirectory, "7z.dll"),
                Path.Combine(baseDirectory, "x64", "7z.dll"),
                Path.Combine(baseDirectory, "x86", "7z.dll"),
            };
            var library = candidates.FirstOrDefault(File.Exists);
            if (library is not null)
            {
                SharpSevenZipBase.SetLibraryPath(library);
            }
        }
        catch
        {
            Interlocked.Exchange(ref initialized, 0);
            throw;
        }
    }
}
