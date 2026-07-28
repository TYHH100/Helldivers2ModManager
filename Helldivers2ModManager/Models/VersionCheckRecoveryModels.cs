using System.IO;

namespace Helldivers2ModManager.Models;

internal enum ModBackupRepairKind
{
    Unknown,
    SafeMetadata,
    AutomaticLod,
    PreserveModLod,
    UseGameLod,
    MixedLod,
    PreRestore
}

internal sealed class ModBackupMetadata
{
    public int SchemaVersion { get; init; } = 1;
    public DateTime CreatedUtc { get; init; }
    public string OriginalFileName { get; init; } = string.Empty;
    public ModBackupRepairKind RepairKind { get; init; }
    public int ActionCount { get; init; }
    public string BackupSha256 { get; init; } = string.Empty;
    public string RepairedSha256 { get; init; } = string.Empty;
    public string? SourceBackupFileName { get; init; }
}

internal sealed class ModBackupEntry
{
    public required string BackupPath { get; init; }
    public required string OriginalPath { get; init; }
    public required DateTime CreatedLocal { get; init; }
    public required long BackupSize { get; init; }
    public required string BackupSha256 { get; init; }
    public string CurrentSha256 { get; init; } = string.Empty;
    public ModBackupRepairKind RepairKind { get; init; }
    public int ActionCount { get; init; }
    public bool HasMetadata { get; init; }
    public bool MetadataMatchesFile { get; init; } = true;
    public bool CurrentExists { get; init; }
    public bool CurrentMatchesBackup { get; init; }
    public bool CanRestore { get; init; }
    public PatchHealthStatus HealthStatus { get; init; }
    public string ValidationMessage { get; init; } = string.Empty;
    public string BackupFileName => Path.GetFileName(BackupPath);
    public string OriginalFileName => Path.GetFileName(OriginalPath);
    public string MetadataPath => BackupPath + ".json";
}

internal sealed class ModBackupHistory
{
    public List<ModBackupEntry> Entries { get; init; } = [];
    public int RestorableCount => Entries.Count(entry => entry.CanRestore);
    public int InvalidCount => Entries.Count(entry => !entry.CanRestore);
}

internal sealed class ModBackupOperationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RestoredPath { get; init; }
    public string? RollbackBackupPath { get; init; }
    public int DeletedCount { get; init; }
}

internal enum CompanionRecoverySourceKind
{
    None,
    ExactPatchCopy,
    CurrentGameBundles
}

internal sealed class CompanionRecoveryItem
{
    public required string PatchPath { get; init; }
    public required string CompanionPath { get; init; }
    public required string Suffix { get; init; }
    public bool IsRequired { get; init; }
    public bool IsMissing { get; init; }
    public bool CanRecover { get; init; }
    public CompanionRecoverySourceKind SourceKind { get; init; }
    public string? SourcePath { get; init; }
    public string Reason { get; init; } = string.Empty;
}

internal sealed class CompanionRecoveryPlan
{
    public List<CompanionRecoveryItem> Items { get; init; } = [];
    public int MissingCount => Items.Count(item => item.IsMissing && item.IsRequired);
    public int RecoverableCount => Items.Count(item => item.CanRecover);
    public int UnrecoverableCount => Items.Count(item => item.IsMissing && item.IsRequired && !item.CanRecover);
    public bool CanRecover => RecoverableCount > 0 && UnrecoverableCount == 0;
}

internal sealed class CompanionRecoveryResult
{
    public bool Success { get; init; }
    public int RecoveredCount { get; init; }
    public List<string> RecoveredPaths { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

internal enum BatchModRepairState
{
    NoAction,
    Repairable,
    SkippedUnsupported,
    Blocked,
    Repaired,
    Failed
}

internal sealed class BatchModRepairItem
{
    public required string ModName { get; init; }
    public required string ModDirectory { get; init; }
    public BatchModRepairState State { get; set; }
    public int MetadataActionCount { get; set; }
    public int AssistedActionCount { get; set; }
    public int CompanionRecoveryCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class BatchModRepairPlan
{
    public List<BatchModRepairItem> Items { get; init; } = [];
    public int RepairableCount => Items.Count(item => item.State == BatchModRepairState.Repairable);
    public int UnsupportedCount => Items.Count(item => item.State == BatchModRepairState.SkippedUnsupported);
    public int BlockedCount => Items.Count(item => item.State == BatchModRepairState.Blocked);
    public int NoActionCount => Items.Count(item => item.State == BatchModRepairState.NoAction);
}

internal sealed class BatchModRepairResult
{
    public List<BatchModRepairItem> Items { get; init; } = [];
    public int RepairedCount => Items.Count(item => item.State == BatchModRepairState.Repaired);
    public int FailedCount => Items.Count(item => item.State == BatchModRepairState.Failed);
    public int SkippedCount => Items.Count(item => item.State is BatchModRepairState.NoAction or BatchModRepairState.SkippedUnsupported or BatchModRepairState.Blocked);
}
