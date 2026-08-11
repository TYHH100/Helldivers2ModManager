using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 部署顺序构建的共享实现：Dashboard 部署与二分排查共用同一排序逻辑，
/// 避免两处行为分叉。
/// </summary>
internal static class DeploymentOrderHelper
{
	/// <summary>
	/// 根据设置获取按部署顺序排列的快照模组。
	/// 如果 useDeploymentOrder 启用且存在部署顺序，按 DeploymentOrderGuids 顺序；
	/// 否则按快照顺序；最后应用部署方向设置（deployBottomToTop 时反转）。
	/// </summary>
	public static ModData[] BuildDeploymentMods(
		ProfileSnapshot snapshot,
		bool useDeploymentOrder,
		IReadOnlyList<Guid> deploymentOrderGuids,
		bool deployBottomToTop)
	{
		var enabledMods = snapshot.Mods.Where(static mod => mod.Enabled).ToArray();
		if (useDeploymentOrder && deploymentOrderGuids.Count > 0)
		{
			var enabledGuids = enabledMods.Select(static mod => mod.Guid).ToArray();
			var enabledSet = enabledGuids.ToHashSet();
			var modsByGuid = enabledMods.ToDictionary(static mod => mod.Guid);
			var result = new List<ModData>();

			foreach (var guid in deploymentOrderGuids)
			{
				if (enabledSet.Contains(guid))
				{
					result.Add(modsByGuid[guid].CreateDeploymentMod());
					enabledSet.Remove(guid);
				}
			}

			// 添加不在 DeploymentOrderGuids 中的已启用模组（防御性）
			result.AddRange(enabledGuids
				.Where(enabledSet.Contains)
				.Select(guid => modsByGuid[guid].CreateDeploymentMod()));

			if (deployBottomToTop)
				result.Reverse();

			return result.ToArray();
		}
		else
		{
			var mods = enabledMods.Select(static mod => mod.CreateDeploymentMod()).ToArray();

			if (deployBottomToTop)
				Array.Reverse(mods);

			return mods;
		}
	}
}
