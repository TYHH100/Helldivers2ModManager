using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Core.Mods;

public static partial class PatchFileRules
{
    public static bool IsPatchFile(string fileName) => AnyPatchFileRegex().IsMatch(fileName);

    public static bool IsMainPatchFile(string fileName) => MainPatchFileRegex().IsMatch(fileName);

    public static bool TryParse(string fileName, out PatchFileInfo patchFile)
    {
        var match = PatchIndexRegex().Match(fileName);
        if (!match.Success)
        {
            patchFile = default!;
            return false;
        }

        var kind = fileName.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
            ? PatchFileKind.GpuResources
            : fileName.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
                ? PatchFileKind.Stream
                : PatchFileKind.Main;
        patchFile = new PatchFileInfo(fileName, fileName[..16], int.Parse(match.Groups[2].Value), kind);
        return true;
    }

    [GeneratedRegex(@"^[a-z0-9]{16}\.patch_[0-9]+(\.(stream|gpu_resources))?$")]
    private static partial Regex AnyPatchFileRegex();

    [GeneratedRegex(@"^[a-z0-9]{16}\.patch_[0-9]+$")]
    private static partial Regex MainPatchFileRegex();

    [GeneratedRegex(@"^([a-z0-9]{16}\.patch_)([0-9]+)(?:\.(?:stream|gpu_resources))?$")]
    private static partial Regex PatchIndexRegex();
}
