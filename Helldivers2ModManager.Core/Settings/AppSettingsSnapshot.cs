using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Core.Settings;

public sealed record AppSettingsSnapshot
{
    public int SchemaVersion { get; init; } = 2;

    public string GameDirectory { get; init; } = string.Empty;

    public string StorageDirectory { get; init; } = string.Empty;

    public string TempDirectory { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Theme { get; init; } = "System";

    public bool EnableAnimations { get; init; } = true;

    public bool EnableBrowserIntegration { get; init; }

    public bool EnableExperimentalRepair { get; init; }

    [JsonConverter(typeof(LogLevelSnapshotConverter))]
    public string LogLevel { get; init; } = "Trace";

    public float Opacity { get; init; } = 0.8f;

    public IReadOnlyList<string> SkipList { get; init; } = [];

    public IReadOnlyList<string> OrganizationalFolderNames { get; init; } = ["Models", "Model"];

    public bool CaseSensitiveSearch { get; init; }

    public bool UseSymbolicLinks { get; init; }

    public bool DeleteToRecycleBin { get; init; } = true;

    public bool AutoRemoveMissingMods { get; init; }

    public bool EnableSorting { get; init; }

    public bool DeployBottomToTop { get; init; }

    public bool AutoCheckVersionOnStartup { get; init; }

    public bool EnableBatchRepair { get; init; }

    public bool RepairDisclaimerAccepted { get; init; }

    public bool AutoCleanLogs { get; init; } = true;

    public bool ShowSeparator { get; init; } = true;

    public int LogRetentionDays { get; init; } = 7;

    public IReadOnlyList<SeparatorSettingsSnapshot> Separators { get; init; } = [];

    public IReadOnlyList<TagSettingsSnapshot> Tags { get; init; } = [];

    public string? NexusApiKey { get; init; }

    public string ExtensionHost { get; init; } = "localhost";

    public int ExtensionPort { get; init; } = 7456;

    public string BrowserExtensionTokenHash { get; init; } = string.Empty;

    public string BrowserExtensionOrigin { get; init; } = string.Empty;

    public bool UseDeploymentOrder { get; init; }

    public IReadOnlyList<Guid> DeploymentOrderGuids { get; init; } = [];

    public IReadOnlyList<OptionOrderSettingsSnapshot> OptionOrders { get; init; } = [];

    public IReadOnlyList<SubOptionOrderSettingsSnapshot> SubOptionOrders { get; init; } = [];
}

public sealed record SeparatorSettingsSnapshot(
    Guid Id,
    string Name,
    string Color,
    bool IsExpanded,
    int DisplayIndex,
    IReadOnlyList<Guid> ModGuids);

public sealed record TagSettingsSnapshot(Guid Id, string Name, string Color);

public sealed record OptionOrderSettingsSnapshot(Guid Key, IReadOnlyList<int> Value);

public sealed record SubOptionOrderSettingsSnapshot(
    Guid Key,
    IReadOnlyDictionary<int, IReadOnlyList<int>> Value);
