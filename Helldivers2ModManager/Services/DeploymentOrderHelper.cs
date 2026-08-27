using Helldivers2ModManager.Models;

using Helldivers2ModManager.Core.Deployment;

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
		var orderedMods = Core.Deployment.DeploymentOrderBuilder.Build(
			enabledMods,
			static mod => mod.Guid,
			preferredOrder: null,
			useDeploymentOrder,
			deploymentOrderGuids,
			deployBottomToTop);

		return [.. orderedMods.Select(static mod => mod.CreateDeploymentMod())];
	}
}
