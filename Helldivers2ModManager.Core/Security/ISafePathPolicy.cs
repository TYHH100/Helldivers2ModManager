namespace Helldivers2ModManager.Core.Security;

public interface ISafePathPolicy
{
    string ResolveUnderRoot(string root, string relativePath);

    bool IsUnderRoot(string root, string candidate);
}
