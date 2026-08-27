namespace Helldivers2ModManager.Core.GameData;

internal static class GameDataExtensions
{
    public static byte[] AsSegment(this byte[] data, int offset, int length) =>
        data.AsSpan(offset, length).ToArray();
}
