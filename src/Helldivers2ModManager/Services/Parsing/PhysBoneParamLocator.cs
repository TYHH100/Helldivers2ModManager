using System.IO;

namespace Helldivers2ModManager.Services;

/// <summary>
/// HD2PhysBone 参数集的磁盘布局探测：同时包含 hd2_spring_rig.bin、hd2_ib_needle.bin、lua_units.txt
/// 的目录视为一个参数集（模组目录内递归查找，逐集独立）。
/// 供部署参数生命周期（ModService）、模组类型识别（ModTypeDetectionService）与部署排序共用。
/// </summary>
internal static class PhysBoneParamLocator
{
	internal const string RigFileName = "hd2_spring_rig.bin";
	internal const string NeedleFileName = "hd2_ib_needle.bin";
	internal const string LuaUnitsFileName = "lua_units.txt";

	/// <summary>add-on 额外读取的可选文件。</summary>
	internal const string GroundFileName = "ground.txt";

	/// <summary>返回模组目录下所有参数集目录；目录内必须三件齐全才算。</summary>
	public static List<DirectoryInfo> FindParamSetDirectories(DirectoryInfo modDirectory)
	{
		var sets = new List<DirectoryInfo>();
		if (!modDirectory.Exists)
			return sets;

		foreach (var rig in modDirectory.EnumerateFiles(RigFileName, System.IO.SearchOption.AllDirectories))
		{
			var dir = rig.Directory;
			if (dir is null)
				continue;
			if (File.Exists(Path.Combine(dir.FullName, NeedleFileName)) &&
				File.Exists(Path.Combine(dir.FullName, LuaUnitsFileName)))
				sets.Add(dir);
		}
		return sets;
	}

	public static bool HasParamSet(DirectoryInfo modDirectory)
	{
		return FindParamSetDirectories(modDirectory).Count > 0;
	}
}
