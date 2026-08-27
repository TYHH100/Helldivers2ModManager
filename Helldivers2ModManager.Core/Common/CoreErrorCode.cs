namespace Helldivers2ModManager.Core.Common;

public enum CoreErrorCode
{
    None,
    Unknown,
    InvalidInput,
    PathOutsideRoot,
    PathNotFound,
    InvalidFormat,
    ResourceNotFound,
    ResourceTooLarge,
    Conflict,
    OperationCanceled,
    IoError,
}
