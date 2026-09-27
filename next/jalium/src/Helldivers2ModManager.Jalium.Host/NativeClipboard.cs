using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Helldivers2ModManager.Jalium.Host;

internal static class NativeClipboard
{
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint ClipboardUnicodeText = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);

    public static void SetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var memory = GlobalAlloc(GlobalMemoryMoveable, (nuint)bytes.Length);
        if (memory == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == nint.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }

            if (!OpenClipboard(nint.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!EmptyClipboard() || SetClipboardData(ClipboardUnicodeText, memory) == nint.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                memory = nint.Zero;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (memory != nint.Zero)
                GlobalFree(memory);
        }
    }
}
