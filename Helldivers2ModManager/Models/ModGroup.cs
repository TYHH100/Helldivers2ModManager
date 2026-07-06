using System.Collections.ObjectModel;

namespace Helldivers2ModManager.Models;

internal sealed class ModGroup
{
	public static readonly Guid DefaultGroupId = Guid.Parse("00000000-0000-0000-0000-000000000001");

	public required Guid Id { get; init; }

	public required string Name { get; set; }

	public required DateTime CreatedAtUtc { get; init; }

	public int DisplayIndex { get; set; }

	public ObservableCollection<Guid> ModGuids { get; init; } = [];

	public bool IsDefault => Id == DefaultGroupId;

	public override string ToString()
	{
		return Name;
	}
}
