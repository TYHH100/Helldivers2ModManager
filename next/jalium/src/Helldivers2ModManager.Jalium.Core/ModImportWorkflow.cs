using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed record ModImportProgress(int FileIndex, int FileCount, string FileName,
    int? NestedIndex = null, int? NestedCount = null, string? NestedFileName = null);

internal sealed record ModImportResult(int Succeeded, int Failed,
    IReadOnlyList<ModProblem> Problems, IReadOnlyCollection<Guid> AddedGuids);

internal sealed class ModImportWorkflow(ModService modService)
{
    public async Task<ModImportResult> ImportAsync(
        IReadOnlyList<string> archivePaths,
        Func<string, Task<string?>> passwordProvider,
        Func<Task<bool>> scriptSecurityConfirmation,
        Action<ModImportProgress>? progress = null)
    {
        var problems = new List<ModProblem>();
        var added = new HashSet<Guid>();
        var succeeded = 0;
        var failed = 0;
        string? batchPassword = null;

        void TrackAdded(ModData mod) => added.Add(mod.Manifest.Guid);
        modService.ModAdded += TrackAdded;
        try
        {
            for (var index = 0; index < archivePaths.Count; index++)
            {
                var path = archivePaths[index];
                var fileName = Path.GetFileName(path);
                progress?.Invoke(new ModImportProgress(index, archivePaths.Count, fileName));
                try
                {
                    var archive = new FileInfo(path);
                    if (!archive.Exists || archive.Extension.ToLowerInvariant() is not (".zip" or ".7z" or ".rar" or ".tar"))
                        throw new InvalidDataException("Archive does not exist or has an unsupported extension.");

                    async Task<string?> RequestPassword()
                    {
                        if (!string.IsNullOrEmpty(batchPassword))
                            return batchPassword;
                        var password = await passwordProvider(fileName);
                        if (!string.IsNullOrEmpty(password))
                            batchPassword = password;
                        return password;
                    }

                    var fileProblems = await modService.TryAddModFromArchiveAsync(archive,
                        (nestedIndex, nestedCount, nestedName) => progress?.Invoke(
                            new ModImportProgress(index, archivePaths.Count, fileName,
                                nestedIndex, nestedCount, nestedName)),
                        RequestPassword, scriptSecurityCallback: scriptSecurityConfirmation);
                    problems.AddRange(fileProblems);
                    if (fileProblems.Any(problem => problem.IsError))
                        failed++;
                    else
                        succeeded++;
                }
                catch (Exception ex)
                {
                    problems.Add(new ModProblem
                    {
                        Directory = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty),
                        Kind = ModProblemKind.CantReadArchive,
                        ExtraData = $"{fileName}: {ex.Message}",
                    });
                    failed++;
                }
            }
        }
        finally
        {
            modService.ModAdded -= TrackAdded;
        }

        return new ModImportResult(succeeded, failed, problems,
            added.Where(guid => modService.GetModByGuid(guid) is not null).ToArray());
    }
}
