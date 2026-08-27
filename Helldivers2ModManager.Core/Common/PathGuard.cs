namespace Helldivers2ModManager.Core.Common;

public static class PathGuard
{
    public static Result<string> EnsureInside(string rootPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Result.Fail<string>(Error.Create(CoreErrorCode.InvalidInput, "Root path is empty."));
        }

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return Result.Fail<string>(Error.Create(CoreErrorCode.InvalidInput, "Candidate path is empty."));
        }

        try
        {
            if (!Path.IsPathFullyQualified(rootPath) || !Path.IsPathFullyQualified(candidatePath))
            {
                return Result.Fail<string>(Error.Create(CoreErrorCode.InvalidInput, "Paths must be fully qualified."));
            }

            var root = Path.GetFullPath(rootPath);
            var candidate = Path.GetFullPath(candidatePath);
            var rootDirectory = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail<string>(Error.Create(
                    CoreErrorCode.PathOutsideRoot,
                    $"The path is outside the permitted root: {candidate}"));
            }

            return Result.Success(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.IOException)
        {
            return Result.Fail<string>(Error.Create(CoreErrorCode.InvalidInput, exception.Message));
        }
    }
}
