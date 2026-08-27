using System.Runtime.InteropServices;

namespace Helldivers2ModManager.Core.Mods;

public sealed class Win32RecycleBinAdapter : IRecycleBinAdapter
{
    private const uint FileOperationToRecycleBin = 3;
    private const ushort AllowUndo = 0x40;
    private const ushort NoConfirmation = 0x10;
    private const ushort Quiet = 0x4;

    public Task SendDirectoryToRecycleBinAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = new SHFILEOPSTRUCTW
        {
            wFunc = FileOperationToRecycleBin,
            pFrom = directoryPath + "\0\0",
            pTo = "\0\0",
            fFlags = (ushort)(AllowUndo | NoConfirmation | Quiet),
        };
        var result = SHFileOperationW(ref operation);
        return result == 0 && !operation.fAnyOperationsAborted
            ? Task.CompletedTask
            : throw new IOException($"Windows could not move \"{directoryPath}\" to the Recycle Bin (result={result}).");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }
}
