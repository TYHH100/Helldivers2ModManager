namespace Helldivers2ModManager.Models;

internal readonly struct GroupedEnabledData
{
    public required Guid GroupId { get; init; }

    public required Guid Guid { get; init; }

    public required bool Enabled { get; init; }

    public required bool[] Toggled { get; init; }

    public required int[] Selected { get; init; }

    public int SortOrder { get; init; }
}
