namespace Helldivers2ModManager.Core.Profiles;

public sealed record ModRuntimeState(
    IReadOnlyList<bool> EnabledOptions,
    IReadOnlyList<int> SelectedOptions,
    IReadOnlyList<Guid>? TagIds = null);

public sealed record ProfileModCapture(
    Guid ModGuid,
    bool Enabled,
    ModRuntimeState RuntimeState,
    Guid? GroupId = null);

public sealed record ProfileCaptureRequest(
    Guid GroupId,
    bool IsDefaultGroup,
    IReadOnlyList<ProfileModCapture> Mods,
    IReadOnlyList<Guid>? PreferredOrder = null);
