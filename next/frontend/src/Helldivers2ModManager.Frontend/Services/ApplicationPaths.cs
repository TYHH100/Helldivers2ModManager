using System.IO;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record ApplicationPaths(string Root)
{
    public string Data => Path.Combine(Root, "data");
    public string Database => Path.Combine(Data, "mod_manager.db");
    public string Boot => Path.Combine(Data, "boot.json");
    public string ModStorage => Path.Combine(Data, "Mods");
    public string Temp => Path.Combine(Data, "temp");
    public string GameData => Path.Combine(Data, "game-data");
}
