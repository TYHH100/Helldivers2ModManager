using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models.Nexus
{
    internal sealed class ModFileUpdateGroup
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool? IsActive { get; set; }

        [JsonPropertyName("last_file_uploaded_at")]
        public DateTime? LastFileUploadedAt { get; set; }

        [JsonPropertyName("versions_count")]
        public int? VersionsCount { get; set; }

        [JsonPropertyName("archived_count")]
        public int? ArchivedCount { get; set; }

        [JsonPropertyName("removed_count")]
        public int? RemovedCount { get; set; }
    }

    internal sealed class ModFileUpdateGroupVersion
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        public string Position { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public ModFile? File { get; set; }
    }

    internal sealed class UpdateGroupsWrapper
    {
        [JsonPropertyName("data")]
        public UpdateGroupsData? Data { get; set; }
    }

    internal sealed class UpdateGroupsData
    {
        [JsonPropertyName("groups")]
        public List<ModFileUpdateGroup>? Groups { get; set; } = new List<ModFileUpdateGroup>();
    }

    internal sealed class UpdateGroupVersionsWrapper
    {
        [JsonPropertyName("data")]
        public UpdateGroupVersionsData? Data { get; set; }
    }

    internal sealed class UpdateGroupVersionsData
    {
        [JsonPropertyName("versions")]
        public List<ModFileUpdateGroupVersion>? Versions { get; set; } = new List<ModFileUpdateGroupVersion>();
    }
}