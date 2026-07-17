using System.IO;
using System.Net;

namespace Helldivers2ModManager.Extensions;

internal static class IOExtensions
{
    public static void CopyTo(this DirectoryInfo info, string destDirName)
    {
        Directory.CreateDirectory(destDirName);

        foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var targetFileName = file.FullName.Replace(info.FullName, destDirName);
            var targetDirName = Path.GetDirectoryName(targetFileName)!;

            if (!Directory.Exists(targetDirName))
                Directory.CreateDirectory(targetDirName);

            File.Copy(file.FullName, targetFileName);
        }
    }

    /// <summary>
    /// 检查 IP 地址是否为本地地址（localhost/127.0.0.1/::1）
    /// </summary>
    public static bool IsLocalAddress(this IPAddress address)
    {
        return IPAddress.IsLoopback(address);
    }
}
