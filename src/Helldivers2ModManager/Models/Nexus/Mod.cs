using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models.Nexus
{
    internal sealed class Mod
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("game_scoped_id")]
        public string GameScopedId { get; set; } = string.Empty;

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; set; }

        [JsonPropertyName("mod_page_url")]
        public string ModPageUrl { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ModStatus? Status { get; set; }

        [JsonPropertyName("adult_content")]
        public bool? AdultContent { get; set; }

        [JsonPropertyName("created_timestamp")]
        public DateTime? CreatedTimestamp { get; set; }

        [JsonPropertyName("updated_timestamp")]
        public DateTime? UpdatedTimestamp { get; set; }

        [JsonPropertyName("endorsements")]
        public int? Endorsements { get; set; }

        [JsonPropertyName("downloads")]
        public int? Downloads { get; set; }
    }

    internal sealed class ModWrapper
    {
        [JsonPropertyName("data")]
        public Mod Data { get; set; } = null!;
    }
}