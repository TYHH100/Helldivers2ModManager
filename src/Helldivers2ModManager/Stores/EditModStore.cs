using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Stores;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class EditModStore
{
    public ModViewModel? CurrentMod { get; set; }
}