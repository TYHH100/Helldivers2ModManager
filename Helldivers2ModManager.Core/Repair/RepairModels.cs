using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Core.Repair;

public enum PatchRepairKind
{
    UnitTocSize,
    EntryIndex,
    TypeResourceCount,
    ResourceTypeId,
    TypeAlignment,
    MainDataOffset,
}

public sealed record PatchRepairAction(
    PatchRepairKind Kind,
    string PatchFilePath,
    long Offset,
    int Width,
    ulong OldValue,
    ulong NewValue,
    int EntryIndex = 0,
    long FileId = 0)
{
    public static PatchRepairAction Empty { get; } = new(default, string.Empty, 0, 0, 0, 0);
}

public sealed record ModRepairPlan(
    IReadOnlyList<PatchRepairAction> Actions,
    IReadOnlyList<string> BlockingReasons)
{
    public int ActionCount => Actions.Count;
    public bool CanRepair => Actions.Count > 0 && BlockingReasons.Count == 0;
}

public sealed record ModRepairResult(
    bool Success,
    int AppliedActionCount = 0,
    IReadOnlyList<string>? BackupPaths = null,
    string? ErrorMessage = null)
{
    public static ModRepairResult Failed(string message) => new(false, ErrorMessage: message);
}

public enum ModBackupRepairKind
{
    Unknown,
    SafeMetadata,
    PreRestore,
    PreserveModLod,
    UseGameLod,
    MixedLod,
    AutomaticLod,
}

public sealed record BackupMetadata(
    int SchemaVersion,
    DateTime CreatedUtc,
    [property: JsonPropertyName("OriginalFileName")] string OriginalFileName,
    [property: JsonPropertyName("OriginalRelativePath")] string OriginalRelativePath,
    ModBackupRepairKind RepairKind,
    int ActionCount,
    [property: JsonPropertyName("BackupSha256")] string BackupSha256,
    [property: JsonPropertyName("RepairedSha256")] string RepairedSha256,
    string? SourceBackupFileName = null)
{
    [JsonIgnore]
    public string BackupPath { get; init; } = string.Empty;
}

public sealed record ModBackupHistory(string OriginalPath, IReadOnlyList<BackupMetadata> Backups);

public sealed record ModBackupOperationResult(bool Success, string? ErrorMessage = null)
{
    public static ModBackupOperationResult Failed(string message) => new(false, message);
}

public sealed record ValidatedBackupEntry(
    string BackupPath,
    string OriginalPath,
    DateTime CreatedLocal,
    long BackupSize,
    string BackupSha256,
    string CurrentSha256,
    ModBackupRepairKind RepairKind,
    int ActionCount,
    bool HasMetadata,
    bool MetadataMatchesFile,
    bool CurrentExists,
    bool CurrentMatchesBackup,
    bool CanRestore,
    Core.Versioning.PatchHealthStatus HealthStatus,
    string ValidationMessage)
{
    public string BackupFileName => Path.GetFileName(BackupPath);
    public string OriginalFileName => Path.GetFileName(OriginalPath);
    public string MetadataPath => BackupPath + ".json";
}

public sealed record DetailedBackupHistory(IReadOnlyList<ValidatedBackupEntry> Entries)
{
    public int RestorableCount => Entries.Count(entry => entry.CanRestore);
    public int InvalidCount => Entries.Count(entry => !entry.CanRestore);
}

public sealed record DetailedBackupOperationResult(
    bool Success,
    string? RestoredPath = null,
    string? RollbackBackupPath = null,
    int DeletedCount = 0,
    int RestoredCount = 0,
    int SkippedCount = 0,
    IReadOnlyList<string>? FailedItems = null,
    string? ErrorMessage = null)
{
    public static DetailedBackupOperationResult Failed(string message) => new(false, ErrorMessage: message);
}






public enum AssistedMaterialRepairKind
{
    ParentReference,
    LegacyEmissiveSchema,
}

public sealed record AssistedMaterialRepairAction(
    string PatchFilePath,
    int EntryIndex,
    long FileId,
    AssistedMaterialRepairKind Kind,
    ulong OldParentMaterialId,
    ulong NewParentMaterialId);



