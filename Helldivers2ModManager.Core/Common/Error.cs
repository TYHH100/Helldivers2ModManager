namespace Helldivers2ModManager.Core.Common;

public readonly record struct Error(CoreErrorCode Code, string Message)
{
    public static Error Create(CoreErrorCode code, string message) => new(code, message);
}
