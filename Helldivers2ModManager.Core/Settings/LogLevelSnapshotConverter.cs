using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Core.Settings;

/// <summary>
/// Reads the numeric LogLevel format written by v1.x and the string format used by v2.
/// </summary>
public sealed class LogLevelSnapshotConverter : JsonConverter<string>
{
    private static readonly string[] s_names =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() ?? "Trace";

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue) &&
            numericValue >= 0 && numericValue < s_names.Length)
        {
            return s_names[numericValue];
        }

        throw new JsonException("LogLevel must be a string or a valid numeric log level.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
