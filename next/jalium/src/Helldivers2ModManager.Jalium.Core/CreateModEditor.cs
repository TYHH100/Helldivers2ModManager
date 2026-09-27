using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class CreateModEditor(SettingsService settings, LocalizationService localization)
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceDirectory { get; private set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string? IconSourceFile { get; private set; }
    public bool IsV1Manifest { get; set; } = true;
    public List<CreateOptionDraft> Options { get; } = [];

    public void SetSourceDirectory(string path)
    {
        SourceDirectory = path;
        foreach (var option in Options)
            option.SourceDirectory = path;
        if (!Directory.Exists(path))
            return;

        if (string.IsNullOrWhiteSpace(Name))
            Name = new DirectoryInfo(path).Name;
        if (string.IsNullOrWhiteSpace(IconPath))
            IconPath = FindFirstImageFile(path) ?? string.Empty;
        if (Options.Count == 0)
            GenerateOptions(path);
    }

    public void SetIconFromFile(string file)
    {
        IconSourceFile = file;
        IconPath = Path.GetFileName(file);
    }

    public void SetIconPath(string path)
    {
        IconSourceFile = null;
        IconPath = path;
    }

    public CreateOptionDraft AddOption()
    {
        var option = new CreateOptionDraft { SourceDirectory = SourceDirectory };
        Options.Add(option);
        return option;
    }

    public void AddLegacyDirectories(IEnumerable<string> relativePaths)
    {
        foreach (var path in relativePaths)
            Options.Add(new CreateOptionDraft
            {
                SourceDirectory = SourceDirectory,
                Name = path,
                IncludePaths = path,
            });
    }

    public List<ModOption> BuildOptions() => Options.Select(option => new ModOption
    {
        Name = string.IsNullOrWhiteSpace(option.Name)
            ? localization["CreateModOption.DefaultName"] : option.Name,
        Description = option.Description,
        Include = CreateOptionDraft.ParsePaths(option.IncludePaths) is { Count: > 0 } includes
            ? includes : null,
        Image = string.IsNullOrWhiteSpace(option.ImagePath) ? null : option.ImagePath,
        SubOptions = option.SubOptions.Count == 0 ? null : option.SubOptions.Select(sub => new ModSubOption
        {
            Name = string.IsNullOrWhiteSpace(sub.Name)
                ? localization["CreateSubOption.DefaultName"] : sub.Name,
            Description = sub.Description,
            Include = CreateOptionDraft.ParsePaths(sub.IncludePaths),
            Image = string.IsNullOrWhiteSpace(sub.ImagePath) ? null : sub.ImagePath,
        }).ToArray(),
    }).ToList();

    public async Task<ModProblem[]> CreateAsync(ModService service)
    {
        if (string.IsNullOrWhiteSpace(Name) || !Directory.Exists(SourceDirectory))
            throw new InvalidOperationException(localization["CreatePage.SetSourceDirFirst"]);

        var source = new DirectoryInfo(SourceDirectory);
        var images = CollectExternalImages().ToArray();
        string? staging = null;
        try
        {
            if (images.Length > 0)
            {
                var stagingRoot = Path.GetFullPath(Path.Combine(settings.TempDirectory, "JaliumCreate"));
                staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                await Task.Run(() => source.CopyTo(staging));
                foreach (var (imageSource, imageName) in images)
                {
                    if (!File.Exists(imageSource))
                        throw new FileNotFoundException(imageSource);
                    var destination = Path.Combine(staging, imageName);
                    if (!File.Exists(destination))
                        File.Copy(imageSource, destination);
                }
                source = new DirectoryInfo(staging);
            }

            return await service.TryAddModFromDirectoryAsync(source, Name, Description,
                BuildOptions(), IconPath, IsV1Manifest ? ManifestVersion.V1 : ManifestVersion.Legacy);
        }
        finally
        {
            if (staging is not null)
            {
                var expectedRoot = Path.GetFullPath(Path.Combine(settings.TempDirectory, "JaliumCreate"));
                var resolved = Path.GetFullPath(staging);
                if (!resolved.StartsWith(expectedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Create staging path escaped its dedicated root.");
                if (Directory.Exists(resolved))
                    Directory.Delete(resolved, recursive: true);
                if (Directory.Exists(expectedRoot)
                    && !Directory.EnumerateFileSystemEntries(expectedRoot).Any())
                    Directory.Delete(expectedRoot);
            }
        }
    }

    private IEnumerable<(string Source, string Name)> CollectExternalImages()
    {
        if (IconSourceFile is not null)
            yield return (IconSourceFile, Path.GetFileName(IconSourceFile));
        foreach (var option in Options)
        {
            if (option.ImageSourceFile is not null)
                yield return (option.ImageSourceFile, Path.GetFileName(option.ImageSourceFile));
            foreach (var sub in option.SubOptions)
                if (sub.ImageSourceFile is not null)
                    yield return (sub.ImageSourceFile, Path.GetFileName(sub.ImageSourceFile));
        }
    }

    private void GenerateOptions(string sourceDirectory)
    {
        var organizational = new HashSet<string>(settings.OrganizationalFolderNames,
            StringComparer.OrdinalIgnoreCase) { "Models", "Model" };
        try
        {
            foreach (var directory in new DirectoryInfo(sourceDirectory).EnumerateDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (organizational.Contains(directory.Name))
                {
                    foreach (var inner in directory.EnumerateDirectories())
                        if ((inner.Attributes & FileAttributes.ReparsePoint) == 0)
                            AddDirectoryOption(inner, directory.Name + "\\");
                }
                else
                    AddDirectoryOption(directory, string.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The source remains usable when optional directory inference is unavailable.
        }
    }

    private void AddDirectoryOption(DirectoryInfo directory, string prefix)
    {
        var relative = prefix + directory.Name;
        var option = new CreateOptionDraft
        {
            SourceDirectory = SourceDirectory,
            Name = directory.Name,
            IncludePaths = relative,
            ImagePath = CombineImagePath(relative, directory.FullName),
        };
        foreach (var subDirectory in directory.EnumerateDirectories())
        {
            if ((subDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var subRelative = relative + "\\" + subDirectory.Name;
            option.SubOptions.Add(new CreateSubOptionDraft
            {
                Name = subDirectory.Name,
                IncludePaths = subRelative,
                ImagePath = CombineImagePath(subRelative, subDirectory.FullName),
            });
        }
        Options.Add(option);
    }

    private static string CombineImagePath(string relative, string directory)
    {
        var image = FindFirstImageFile(directory);
        return image is null ? string.Empty : relative + "\\" + image;
    }

    private static string? FindFirstImageFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .FirstOrDefault(file => file is not null
                    && ImageExtensions.Contains(Path.GetExtension(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class CreateOptionDraft
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IncludePaths { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string? ImageSourceFile { get; private set; }
    public List<CreateSubOptionDraft> SubOptions { get; } = [];

    public void SetImageFromFile(string file)
    {
        ImageSourceFile = file;
        ImagePath = Path.GetFileName(file);
    }

    public void SetImagePath(string path)
    {
        ImageSourceFile = null;
        ImagePath = path;
    }

    public static List<string> ParsePaths(string paths) => paths.Split(';',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
}

internal sealed class CreateSubOptionDraft
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IncludePaths { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string? ImageSourceFile { get; private set; }

    public void SetImageFromFile(string file)
    {
        ImageSourceFile = file;
        ImagePath = Path.GetFileName(file);
    }

    public void SetImagePath(string path)
    {
        ImageSourceFile = null;
        ImagePath = path;
    }
}
