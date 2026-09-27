using System.Text;
using System.Text.Json;
using Helldivers2ModManager.Exceptions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed record ModCatalogResult(IReadOnlyList<ModData> Mods, IReadOnlyList<ModProblem> Problems);

internal sealed class ModCatalogService(ILogger<ModCatalogService> logger)
{
    private sealed record ParsedDirectory(DirectoryInfo Directory, ModData? Mod, ModProblem[] Problems,
        string? ManifestJson, int? FailureKind);

    public Task<ModCatalogResult> LoadAsync(string storageDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        return Task.Run(() => Load(storageDirectory, cancellationToken), cancellationToken);
    }

    private ModCatalogResult Load(string storageDirectory, CancellationToken cancellationToken)
    {
        var modsDirectory = new DirectoryInfo(Path.Combine(storageDirectory, "Mods"));
        if (!modsDirectory.Exists)
            modsDirectory.Create();

        var directories = modsDirectory.GetDirectories();
        var cache = ManifestParseCache.Load(storageDirectory, logger);
        var parsed = new ParsedDirectory[directories.Length];
        Parallel.For(0, directories.Length, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
        }, index => parsed[index] = ParseCached(directories[index], modsDirectory.FullName, cache));

        var mods = new List<ModData>(parsed.Length);
        var problems = new List<ModProblem>();
        var seen = new HashSet<Guid>();
        foreach (var item in parsed)
        {
            problems.AddRange(item.Problems);
            if (item.Mod is null)
                continue;
            if (!seen.Add(item.Mod.Manifest.Guid))
            {
                problems.Add(new ModProblem { Directory = item.Directory, Kind = ModProblemKind.Duplicate });
                continue;
            }
            mods.Add(item.Mod);
        }
        cache.Save(storageDirectory);
        return new ModCatalogResult(mods, problems);
    }

    private ParsedDirectory ParseCached(DirectoryInfo directory, string modsRoot, ManifestParseCache cache)
    {
        if (!File.Exists(Path.Combine(directory.FullName, "manifest.json")))
        {
            lock (cache)
                cache.Remove(directory.Name);
            return Parse(directory, modsRoot);
        }

        string fingerprint;
        try
        {
            fingerprint = ManifestParseCache.ComputeFingerprint(directory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot fingerprint {Directory}; parsing the manifest directly", directory.FullName);
            return Parse(directory, modsRoot);
        }

        CachedModEntry? entry;
        lock (cache)
            cache.TryGet(directory.Name, fingerprint, out entry);
        if (entry is not null)
        {
            try
            {
                var problems = entry.Problems.Select(problem => new ModProblem
                {
                    Directory = directory,
                    Kind = (ModProblemKind)problem.Kind,
                    ExtraData = problem.ExtraData,
                }).ToArray();
                if (entry.ManifestJson is null)
                    return new ParsedDirectory(directory, null, problems, null, entry.FailureKind);
                using var document = JsonDocument.Parse(entry.ManifestJson);
                var manifest = ModManifest.DeserializeFromDocument(document);
                var mod = entry.Succeeded ? new ModData(directory, manifest) : null;
                return new ParsedDirectory(directory, mod, problems, entry.ManifestJson, entry.FailureKind);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Invalid cached manifest for {Directory}; parsing again", directory.FullName);
            }
        }

        var parsed = Parse(directory, modsRoot);
        if (parsed.ManifestJson is null && parsed.FailureKind is null)
            return parsed;
        lock (cache)
            cache.Store(directory.Name, new CachedModEntry
            {
                Fingerprint = fingerprint,
                ManifestJson = parsed.ManifestJson,
                Succeeded = parsed.Mod is not null,
                FailureKind = parsed.FailureKind,
                Problems = parsed.Problems.Select(problem => new CachedModProblem
                {
                    Kind = (int)problem.Kind,
                    ExtraData = problem.ExtraData as string,
                }).ToList(),
            });
        return parsed;
    }

    private ParsedDirectory Parse(DirectoryInfo directory, string modsRoot)
    {
        var manifestFile = new FileInfo(Path.Combine(directory.FullName, "manifest.json"));
        if (!manifestFile.Exists)
        {
            // Match ModService.Init: a child without a manifest is removed during startup.
            var parent = directory.Parent?.FullName;
            if (parent is null || !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(modsRoot),
                    StringComparison.OrdinalIgnoreCase) || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to delete an unsafe mod directory: {directory.FullName}");
            directory.Delete(recursive: true);
            return new ParsedDirectory(directory, null,
                [new ModProblem { Directory = directory, Kind = ModProblemKind.NoManifestFound }], null, null);
        }

        IModManifest manifest;
        try
        {
            manifest = ModManifest.DeserializeFromFile(manifestFile);
        }
        catch (UnknownManifestVersionException)
        {
            return Failure(ModProblemKind.UnknownManifestVersion);
        }
        catch (EndOfLifeException)
        {
            return Failure(ModProblemKind.OutOfSupportManifest);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot parse manifest in {Directory}", directory.FullName);
            return Failure(ModProblemKind.CantParseManifest);
        }

        var problems = new List<ModProblem>();
        var valid = ManifestPathValidator.Check(manifest, directory, problems);
        var json = SerializeCanonical(manifest);
        return new ParsedDirectory(directory, valid ? new ModData(directory, manifest) : null,
            problems.ToArray(), json, null);

        ParsedDirectory Failure(ModProblemKind kind) => new(directory, null,
            [new ModProblem { Directory = directory, Kind = kind }], null, (int)kind);
    }

    private static string SerializeCanonical(IModManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true }))
            manifest.Serialize(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
