using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Core.Persistence;

public sealed record BootConfiguration
{
    public string StorageDirectory { get; init; } = string.Empty;
    public string TempDirectory { get; init; } = string.Empty;
}

public sealed record SeparatorSetting(
    Guid Id,
    string Name,
    string Color,
    bool IsExpanded,
    IReadOnlyList<Guid> ModGuids,
    int DisplayIndex);

public sealed record TagSetting(Guid Id, string Name, string Color);

public sealed record AutoTagMappingSetting(int Type, Guid TagId);

public sealed record AppSettings
{
    public string GameDirectory { get; set; } = string.Empty;
    public string StorageDirectory { get; set; } = string.Empty;
    public string TempDirectory { get; set; } = string.Empty;
    public int LogLevel { get; set; } = 3;
    public float Opacity { get; set; } = 0.8f;
    public List<string> SkipList { get; set; } = [];
    public List<string> OrganizationalFolderNames { get; set; } = [];
    public bool CaseSensitiveSearch { get; set; }
    public bool UseSymbolicLinks { get; set; }
    public bool DeleteToRecycleBin { get; set; }
    public bool AutoRemoveMissingMods { get; set; }
    public bool DeployBottomToTop { get; set; }
    public bool AutoCheckVersionOnStartup { get; set; }
    public bool EnableBatchRepair { get; set; }
    public bool RepairDisclaimerAccepted { get; set; }
    public bool AutoCleanLogs { get; set; } = true;
    public bool ShowSeparator { get; set; }
    public List<SeparatorSetting> Separators { get; set; } = [];
    public int MaxLogFiles { get; set; } = 20;
    public List<TagSetting> Tags { get; set; } = [];
    public string Language { get; set; } = string.Empty;
    public bool FirstRunTutorialCompleted { get; set; }
    public int BackgroundMode { get; set; }
    public string BackgroundImagePath { get; set; } = string.Empty;
    public float BackgroundOpacity { get; set; } = 0.6f;
    public float CardOpacity { get; set; } = 0.7f;
    public string? NexusApiKey { get; set; }
    public bool EnableFuzzySearch { get; set; } = true;
    public bool DeployBottomToTopSetting => DeployBottomToTop;
    public bool UseDeploymentOrder { get; set; }
    public List<Guid> DeploymentOrderGuids { get; set; } = [];
    public Dictionary<Guid, int[]> OptionOrders { get; set; } = [];
    public Dictionary<Guid, Dictionary<int, int[]>> SubOptionOrders { get; set; } = [];
    public bool EnableAutoTagging { get; set; }
    public bool AutoTagCreateMissingTags { get; set; }
    public List<AutoTagMappingSetting> AutoTagMappings { get; set; } = [];

    [JsonIgnore]
    public bool IsInitialized => !string.IsNullOrWhiteSpace(StorageDirectory);
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(BootConfiguration))]
[JsonSerializable(typeof(Dictionary<Guid, int[]>))]
[JsonSerializable(typeof(Dictionary<Guid, Dictionary<int, int[]>>))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;
