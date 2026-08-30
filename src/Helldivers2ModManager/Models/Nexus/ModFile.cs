using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models.Nexus
{
    internal sealed class ModFile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("game_scoped_id")]
        public string GameScopedId { get; set; } = string.Empty;

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = string.Empty;

        [JsonPropertyName("mod_game_scoped_id")]
        public string ModGameScopedId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("category")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ModFileCategory? Category { get; set; }

        [JsonPropertyName("uploaded_at")]
        public DateTime? UploadedAt { get; set; }

        [JsonPropertyName("size_bytes")]
        public long? SizeBytes { get; set; }

        [JsonPropertyName("is_primary")]
        public bool? IsPrimary { get; set; }

        [JsonPropertyName("allow_mod_manager_download")]
        public bool? AllowModManagerDownload { get; set; }

        [JsonPropertyName("update_group_version")]
        public UpdateGroupVersion? UpdateGroupVersion { get; set; }
    }

    internal sealed class ModFileWrapper
    {
        [JsonPropertyName("data")]
        public ModFile Data { get; set; } = null!;
    }

    internal sealed class ModFilesWrapper
    {
        [JsonPropertyName("data")]
        public ModFile? Data { get; set; }
    }
}