using Microsoft.Extensions.Logging;
using System.Runtime.Serialization;
using System.Text.Json;

namespace Helldivers2ModManager.Models;

internal readonly struct EnabledData : IJsonSerializable<EnabledData>
{
	public required Guid Guid { get; init; }

	public required bool Enabled { get; init; }

	public required bool[] Toggled { get; init; }

	public required int[] Selected { get; init; }

	public Guid? GroupId { get; init; }

	public List<Guid>? TagIds { get; init; }

	public static EnabledData Deserialize(JsonElement root, ILogger? logger = null)
	{
		var guid = Guid.Parse(root.GetProperty(nameof(Guid)).GetString()!);

		var enabled = root.GetProperty(nameof(Enabled)).GetBoolean();

		var prop = root.GetProperty(nameof(Toggled));
		if (prop.ValueKind != JsonValueKind.Array)
			throw new SerializationException($"Expected property `{nameof(Toggled)}` to be of type `array`!");
		var toggled = new bool[prop.GetArrayLength()];
		var arr = prop.EnumerateArray().ToArray();
		for (int i = 0; i < arr.Length; i++)
			toggled[i] = arr[i].GetBoolean();

		prop = root.GetProperty(nameof(Selected));
		if (prop.ValueKind != JsonValueKind.Array)
			throw new SerializationException($"Expected property `{nameof(Selected)}` to be of type `array`!");
		var selected = new int[prop.GetArrayLength()];
		arr = prop.EnumerateArray().ToArray();
		for (int i = 0; i < arr.Length; i++)
			selected[i] = arr[i].GetInt32();

		Guid? groupId = null;
		if (root.TryGetProperty(nameof(GroupId), out var groupIdProp) && groupIdProp.ValueKind != JsonValueKind.Null)
		{
			try
			{
				groupId = Guid.Parse(groupIdProp.GetString()!);
			}
			catch (Exception ex)
			{
				logger?.LogWarning(ex, "Failed to parse GroupId, defaulting to null");
			}
		}

		List<Guid>? tagIds = null;
		if (root.TryGetProperty(nameof(TagIds), out var tagIdsProp) && tagIdsProp.ValueKind == JsonValueKind.Array)
		{
			tagIds = [];
			foreach (var tagIdElm in tagIdsProp.EnumerateArray())
			{
				if (tagIdElm.ValueKind == JsonValueKind.String)
				{
					try
					{
						tagIds.Add(Guid.Parse(tagIdElm.GetString()!));
					}
					catch (Exception ex)
					{
						logger?.LogWarning(ex, "Failed to parse TagId, skipping");
					}
				}
			}
		}

		return new EnabledData
		{
			Guid = guid,
			Enabled = enabled,
			Toggled = toggled,
			Selected = selected,
			GroupId = groupId,
			TagIds = tagIds,
		};
	}

	public void Serialize(Utf8JsonWriter writer)
	{
		writer.WriteStartObject();
		writer.WriteString(nameof(Guid), Guid.ToString());
		writer.WriteBoolean(nameof(Enabled), Enabled);
		writer.WriteStartArray(nameof(Toggled));
		foreach (var elm in Toggled)
			writer.WriteBooleanValue(elm);
		writer.WriteEndArray();
		writer.WriteStartArray(nameof(Selected));
		foreach (var elm in Selected)
			writer.WriteNumberValue(elm);
		writer.WriteEndArray();
		if (GroupId.HasValue)
		{
			writer.WriteString(nameof(GroupId), GroupId.Value.ToString());
		}
		if (TagIds != null && TagIds.Count > 0)
		{
			writer.WriteStartArray(nameof(TagIds));
			foreach (var tagId in TagIds)
				writer.WriteStringValue(tagId.ToString());
			writer.WriteEndArray();
		}
		writer.WriteEndObject();
	}

	public override string ToString()
	{
		return $"{{ {nameof(Guid)} = \"{{{Guid}}}\", {nameof(Enabled)} = {Enabled}, {nameof(Toggled)} = {string.Join(", ", Toggled)}, {nameof(Selected)} = {string.Join(", ", Selected)}, {nameof(GroupId)} = {(GroupId.HasValue ? $"\"{{{GroupId.Value}}}\"" : "null")} }}";
	}
}
