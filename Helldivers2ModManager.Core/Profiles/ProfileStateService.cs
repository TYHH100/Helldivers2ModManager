using System.Text;
using System.Text.Json;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Core.Profiles;

public static class ProfileStateService
{
    public static string SerializeRuntimeState(ModRuntimeState state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteArray(writer, nameof(ModRuntimeState.EnabledOptions), state.EnabledOptions, static (target, value) => target.WriteBooleanValue(value));
            WriteArray(writer, nameof(ModRuntimeState.SelectedOptions), state.SelectedOptions, static (target, value) => target.WriteNumberValue(value));
            if (state.TagIds is { } tags)
            {
                writer.WriteStartArray(nameof(ModRuntimeState.TagIds));
                foreach (var tag in tags)
                {
                    writer.WriteStringValue(tag.ToString("D"));
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return EncodingUtf8.GetString(stream.ToArray());
    }

    public static ModRuntimeState DeserializeRuntimeState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(
            ReadArray(root, nameof(ModRuntimeState.EnabledOptions), static element => element.GetBoolean()),
            ReadArray(root, nameof(ModRuntimeState.SelectedOptions), static element => element.GetInt32()),
            ReadTags(root));
    }

    public static Persistence.ProfileSnapshot Capture(long sequence, ProfileCaptureRequest request)
    {
        var captures = request.Mods.ToList();
        var byGuid = captures.ToDictionary(static mod => mod.ModGuid, static mod => mod);
        var ordered = new List<ProfileModCapture>();
        if (request.PreferredOrder is not null)
        {
            foreach (var guid in request.PreferredOrder)
            {
                if (byGuid.Remove(guid, out var capture))
                {
                    ordered.Add(capture);
                }
            }
        }

        ordered.AddRange(byGuid.Values);
        var now = DateTimeOffset.UtcNow;
        return new(
            Guid.NewGuid(),
            "Current",
            request.IsDefaultGroup,
            now,
            now,
            [new(request.GroupId, request.GroupId.ToString("D"), 0, now)],
            ordered.Select((capture, index) => new ProfileModState(
                capture.ModGuid,
                capture.Enabled,
                capture.GroupId ?? request.GroupId,
                index,
                SerializeRuntimeState(capture.RuntimeState))).ToArray());
    }

    public static IReadOnlyList<ModDeploymentInput> CreateDeploymentInputs(
        ModDiscoveryResult discovery,
        Persistence.ProfileSnapshot profile)
    {
        var modsByGuid = new Dictionary<Guid, DiscoveredMod>();
        foreach (var discovered in discovery.Mods)
        {
            modsByGuid[discovered.Manifest.Guid] = discovered;
        }
        var inputs = new List<ModDeploymentInput>();
        foreach (var state in profile.Mods.OrderBy(static item => item.SortOrder))
        {
            if (!state.Enabled || !modsByGuid.TryGetValue(state.ModGuid, out var discovered))
            {
                continue;
            }

            var runtime = DeserializeRuntimeState(state.StateJson);
            inputs.Add(new(state.ModGuid, discovered.Directory, discovered.Manifest, runtime.EnabledOptions, runtime.SelectedOptions));
        }

        return inputs;
    }

    private static void WriteArray<T>(Utf8JsonWriter writer, string name, IReadOnlyList<T> values, Action<Utf8JsonWriter, T> writeValue)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writeValue(writer, value);
        }

        writer.WriteEndArray();
    }

    private static List<T> ReadArray<T>(JsonElement root, string name, Func<JsonElement, T> readValue)
    {
        var result = new List<T>();
        if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(property.EnumerateArray().Select(readValue));
        }

        return result;
    }

    private static List<Guid>? ReadTags(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(ModRuntimeState.TagIds), out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tags = new List<Guid>();
        foreach (var value in property.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    private static readonly Encoding EncodingUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
