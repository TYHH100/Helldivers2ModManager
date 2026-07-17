using Helldivers2ModManager.Core.Security;

namespace Helldivers2ModManager.Infrastructure.Security;

public sealed class SafePathPolicy : ISafePathPolicy
{
    private readonly Helldivers2ModManager.Core.Security.SharedSafePathPolicy _inner = new();

    public string ResolveUnderRoot(string root, string relativePath)
    {
        return _inner.ResolveUnderRoot(root, relativePath);
    }

    public bool IsUnderRoot(string root, string candidate)
    {
        return _inner.IsUnderRoot(root, candidate);
    }
}
