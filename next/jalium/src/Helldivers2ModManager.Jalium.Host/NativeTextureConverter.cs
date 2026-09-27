using System.Runtime.InteropServices;

namespace Helldivers2ModManager.Jalium.Host;

internal static unsafe class NativeTextureConverter
{
    private const int MinimumPixels = 4_194_304;
    private static bool _unavailable;

    [DllImport("jalium_texture", EntryPoint = "jalium_texture_convert",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void Convert(byte* source, byte* target, nuint pixels, uint mode);

    internal static bool TryConvert(byte[] source, byte[] target, PatchTextureChannel channel,
        bool force = false)
    {
        if (_unavailable || source.Length == 0 || source.Length != target.Length
            || source.Length % 4 != 0
            || (!force && (channel != PatchTextureChannel.Alpha
                || source.Length / 4 < MinimumPixels)))
            return false;
        try
        {
            fixed (byte* sourcePointer = source)
            fixed (byte* targetPointer = target)
                Convert(sourcePointer, targetPointer, (nuint)(source.Length / 4), (uint)channel);
            return true;
        }
        catch (DllNotFoundException) { _unavailable = true; }
        catch (EntryPointNotFoundException) { _unavailable = true; }
        catch (BadImageFormatException) { _unavailable = true; }
        return false;
    }
}
