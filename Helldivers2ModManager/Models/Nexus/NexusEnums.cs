using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models.Nexus
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum ModStatus
    {
        published,
        not_published,
        hidden,
        under_moderation,
        removed,
        removed_by_staff
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum ModFileCategory
    {
        main,
        update,
        optional,
        old_version,
        miscellaneous,
        removed,
        archived,
        unknown
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum UploadState
    {
        created,
        available
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum NexusApiErrorType
    {
        InvalidRequest,
        Validation,
        Unauthorized,
        NotFound,
        RateLimit,
        ServerError
    }
}