using System.IO;
using Helldivers2ModManager.Core.Mods;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record CreatedMod(Guid Id, string Name, DirectoryInfo Directory);

public sealed record CreateModRequest(
    DirectoryInfo SourceDirectory,
    string Name,
    string Description,
    string? IconPath,
    bool UseV1Manifest,
    IReadOnlyList<CreateModOption> Options);

public sealed record CreateModOption(
    string Name,
    string Description,
    IReadOnlyList<string> IncludePaths,
    string? ImagePath,
    IReadOnlyList<CreateModSubOption> SubOptions);

public sealed record CreateModSubOption(
    string Name,
    string Description,
    IReadOnlyList<string> IncludePaths,
    string? ImagePath = null);

public sealed class ModCreationService(ApplicationSettingsService settings)
{
    public CreateModRequest CreateRequest(
        string sourceDirectory,
        string name,
        string description,
        string? iconPath,
        bool useV1Manifest,
        IReadOnlyList<CreateModOption> options)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException("Source directory does not exist.");
        }

        return new(
            new DirectoryInfo(sourceDirectory),
            name,
            description,
            iconPath,
            useV1Manifest,
            options);
    }

    public async Task<CreatedMod> CreateAsync(
        CreateModRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync().ConfigureAwait(false);
        var storageRoot = Path.GetFullPath(settings.Current.StorageDirectory);
        var modsRoot = Path.Combine(storageRoot, "Mods");
        var safeName = Path.GetFileName(request.Name);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new ArgumentException("Mod name cannot be converted to a directory name.", nameof(request));
        }

        var destination = Path.GetFullPath(Path.Combine(modsRoot, safeName));
        if (!destination.StartsWith(modsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(destination))
        {
            throw new IOException($"Mod directory cannot be created: {destination}");
        }

        Directory.CreateDirectory(destination);
        await CopyDirectoryAsync(request.SourceDirectory, new DirectoryInfo(destination), cancellationToken)
            .ConfigureAwait(false);

        var iconFileName = await CopyExternalImageAsync(request.IconPath, request.SourceDirectory, new DirectoryInfo(destination), cancellationToken)
            .ConfigureAwait(false);
        var options = new List<CreateModOption>();
        foreach (var option in request.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Name))
            {
                continue;
            }

            var imageFileName = await CopyExternalImageAsync(
                option.ImagePath,
                request.SourceDirectory,
                new DirectoryInfo(destination),
                cancellationToken).ConfigureAwait(false);
            options.Add(option with
            {
                Name = option.Name.Trim(),
                IncludePaths = [.. option.IncludePaths.Where(path => !string.IsNullOrWhiteSpace(path))],
                ImagePath = imageFileName,
            });
        }

        IModManifest manifest = request.UseV1Manifest
            ? new V1ModManifest
            {
                Guid = Guid.NewGuid(),
                Name = request.Name,
                Description = request.Description,
                IconPath = iconFileName,
                Options = options.Count > 0
                    ? options.Select(option => new ModOption(
                        option.Name,
                        option.Description,
                        option.IncludePaths.Count > 0 ? option.IncludePaths : null,
                        string.IsNullOrWhiteSpace(option.ImagePath) ? null : Path.GetFileName(option.ImagePath),
                        option.SubOptions.Count > 0
                            ? option.SubOptions.Select(sub => new ModSubOption(
                                sub.Name,
                                sub.Description,
                                sub.IncludePaths,
                                null)).ToArray()
                            : null)).ToArray()
                    : null,
            }
            : new LegacyModManifest
            {
                Guid = Guid.NewGuid(),
                Name = request.Name,
                Description = request.Description,
                IconPath = iconFileName,
                Options = options.Count > 0 ? [.. options.Select(option => option.Name)] : null,
            };

        ModManifest.SaveToFile(manifest, new DirectoryInfo(destination));
        return new(manifest.Guid, manifest.Name, new DirectoryInfo(destination));
    }

    private static async Task<string?> CopyExternalImageAsync(
        string? imagePath,
        DirectoryInfo sourceDirectory,
        DirectoryInfo destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(imagePath, sourceDirectory.FullName);
        if (!fullPath.StartsWith(sourceDirectory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(fullPath);
        var target = Path.Combine(destinationDirectory.FullName, fileName);
        if (!string.Equals(fullPath, target, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => File.Copy(fullPath, target, true), cancellationToken).ConfigureAwait(false);
        }

        return fileName;
    }

    private async Task EnsureStorageAsync()
    {
        Directory.CreateDirectory(settings.Current.StorageDirectory);
        Directory.CreateDirectory(Path.Combine(settings.Current.StorageDirectory, "Mods"));
    }

    private static async Task CopyDirectoryAsync(
        DirectoryInfo source,
        DirectoryInfo destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination.FullName);
        var files = source.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
        }, (file, token) =>
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(destination.FullName, Path.GetRelativePath(source.FullName, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.FullName, target, true);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }
}
