using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models.Nexus
{
    internal sealed class UpdateGroupVersion
    {
        [JsonPropertyName("position")]
        public string Position { get; set; } = string.Empty;

        [JsonPropertyName("group_id")]
        public string GroupId { get; set; } = string.Empty;
    }

    internal sealed class TrendingMod
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; set; }

        [JsonPropertyName("mod_page_url")]
        public string ModPageUrl { get; set; } = string.Empty;
    }

    internal sealed class TrendingModsWrapper
    {
        [JsonPropertyName("data")]
        public TrendingModsData? Data { get; set; }
    }

    internal sealed class TrendingModsData
    {
        [JsonPropertyName("mods")]
        public List<TrendingMod>? Mods { get; set; } = new List<TrendingMod>();
    }

    internal sealed class ProblemDetails
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("instance")]
        public string? Instance { get; set; }

        [JsonPropertyName("errors")]
        public List<ValidationError>? Errors { get; set; }
    }

    internal sealed class ValidationError
    {
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("pointer")]
        public string? Pointer { get; set; }
    }

    internal sealed class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public ModFile? LatestModFile { get; set; }
    }
}