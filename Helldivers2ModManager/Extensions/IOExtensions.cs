using System.IO;
using System.Net;

namespace Helldivers2ModManager.Extensions;

internal static class IOExtensions
{
	public static void CopyTo(this DirectoryInfo info, string destDirName)
	{
		Directory.CreateDirectory(destDirName);

		var files = info.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();

		// 有界并行复制：大 Mod（数百文件、数 GB）导入时显著缩短耗时。
		// File.Copy 是 Windows 内核态复制（CopyFile2），worker 内先建目录再复制，
		// Directory.CreateDirectory 幂等，语义与串行实现一致（目标已存在时 File.Copy 抛异常）。
		Parallel.ForEach(
			files,
			new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) },
			file =>
			{
				var targetFileName = file.FullName.Replace(info.FullName, destDirName);
				var targetDirName = Path.GetDirectoryName(targetFileName)!;

				Directory.CreateDirectory(targetDirName);

				File.Copy(file.FullName, targetFileName);
			});
	}

	/// <summary>
	/// 检查 IP 地址是否为本地地址（localhost/127.0.0.1/::1）
	/// </summary>
	public static bool IsLocalAddress(this IPAddress address)
	{
		return IPAddress.IsLoopback(address);
	}
}
