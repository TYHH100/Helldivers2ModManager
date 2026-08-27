using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Stores;

internal sealed class EditModStore
{
    public ModViewModel? CurrentMod { get; set; }
}
