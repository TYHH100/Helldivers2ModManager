namespace Helldivers2ModManager.Core.Security;

public sealed class SafePathViolationException : IOException
{
    public SafePathViolationException(string message)
        : base(message)
    {
    }
}
