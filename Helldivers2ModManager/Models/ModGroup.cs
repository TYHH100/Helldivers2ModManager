using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models;

public sealed class ModGroup
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    public ModGroup(string name)
    {
        Id = Guid.NewGuid();
        Name = name;
    }

    [JsonConstructor]
    public ModGroup(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public override string ToString()
    {
        return Name;
    }
}