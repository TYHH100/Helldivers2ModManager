namespace Helldivers2ModManager.Core.Security;

/// <summary>
/// Shared path boundary policy used by the manager and the standalone Purger tool.
/// It rejects traversal and reparse points before a path is used for file operations.
/// </summary>
public sealed class SharedSafePathPolicy : ISafePathPolicy
{
    private static readonly char[] s_directorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('\\') || relativePath.StartsWith('/'))
            throw new SafePathViolationException("Absolute and UNC paths are not allowed.");

        var segments = relativePath.Split(s_directorySeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new SafePathViolationException("Relative traversal segments are not allowed.");

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        if (!IsLexicallyUnderRoot(canonicalRoot, candidate))
            throw new SafePathViolationException("The resolved path escapes the allowed root.");

        ThrowIfReparsePointInPath(canonicalRoot, candidate);
        return candidate;
    }

    public bool IsUnderRoot(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var canonicalCandidate = Path.GetFullPath(candidate);
            if (!IsLexicallyUnderRoot(canonicalRoot, canonicalCandidate))
                return false;

            ThrowIfReparsePointInPath(canonicalRoot, canonicalCandidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsLexicallyUnderRoot(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
            return true;

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowIfReparsePointInPath(string root, string candidate)
    {
        ThrowIfExistingReparsePoint(root);

        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
            return;

        var current = root;
        foreach (var segment in relative.Split(s_directorySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            ThrowIfExistingReparsePoint(current);
        }
    }

    private static void ThrowIfExistingReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SafePathViolationException("Junctions and symbolic links are not allowed in protected paths.");
    }
}
