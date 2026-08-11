using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 二分分割与报告合并的纯逻辑，便于单元测试。
/// </summary>
internal static class BisectCore
{
	/// <summary>
	/// 本轮启用的测试集：候选集的前一半（候选集 ≥2 时保证非空）。
	/// </summary>
	public static IReadOnlyList<Guid> SplitTestedGuids(IReadOnlyList<Guid> candidates)
	{
		var half = candidates.Count / 2;
		return candidates.Take(half).ToList();
	}

	/// <summary>
	/// 根据报告合并候选集：崩溃则嫌疑在测试集内，未崩溃则在禁用的一半内。
	/// </summary>
	public static IReadOnlyList<Guid> ApplyReport(
		IReadOnlyList<Guid> candidates,
		IReadOnlyList<Guid> testedGuids,
		bool crashed)
	{
		if (crashed)
			return testedGuids.ToList();

		var tested = testedGuids.ToHashSet();
		return candidates.Where(guid => !tested.Contains(guid)).ToList();
	}
}

/// <summary>
/// 二分排查会话与核心逻辑：创建临时分组承载排查状态（原分组零改动），
/// 每轮启用一半候选模组部署并记录用户报告，收敛到嫌疑模组后可迭代排查多个。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class BisectService
{
	public sealed class BisectSession
	{
		public required Guid OriginalGroupId { get; init; }

		public required string OriginalGroupName { get; init; }

		public required ModGroup TempGroup { get; init; }

		public required IReadOnlyList<ModData> AllMods { get; init; }

		public required IReadOnlyList<ModData> InitialEnabledMods { get; init; }

		/// <summary>原分组的用户自定义顺序（保存原分组状态时保持，避免覆盖拖拽排序）。</summary>
		public required IReadOnlyList<Guid> OriginalOrder { get; init; }

		public List<Guid> Candidates { get; set; } = [];

		public List<Guid> Suspects { get; } = [];

		public List<BisectRoundRecord> Rounds { get; } = [];

		/// <summary>
		/// 会话内是否出现过崩溃报告（含迭代验证部署的崩溃）。
		/// 全程未崩溃时收敛结果不能作为嫌疑依据。
		/// </summary>
		public bool HasCrashed { get; set; }
	}

	public sealed class BisectRoundRecord
	{
		public required int RoundIndex { get; init; }

		public required IReadOnlyList<string> TestedModNames { get; init; }

		public required bool Crashed { get; init; }
	}

	public sealed class BisectRound
	{
		public required IReadOnlyList<ModData> TestedMods { get; init; }
	}

	private readonly ILogger<BisectService> _logger;
	private readonly ModGroupService _modGroupService;
	private readonly ModService _modService;
	private readonly SettingsService _settingsService;
	private readonly ProfileSaveCoordinator _profileSaveCoordinator;
	private readonly LocalizationService _localizationService;

	public BisectService(
		ILogger<BisectService> logger,
		ModGroupService modGroupService,
		ModService modService,
		SettingsService settingsService,
		ProfileSaveCoordinator profileSaveCoordinator,
		LocalizationService localizationService)
	{
		_logger = logger;
		_modGroupService = modGroupService;
		_modService = modService;
		_settingsService = settingsService;
		_profileSaveCoordinator = profileSaveCoordinator;
		_localizationService = localizationService;
	}

	public BisectSession? Current { get; private set; }

	public string TempGroupName => _localizationService["Bisect.TempGroupName"];

	private string TempGroupPrefix => _localizationService["Bisect.TempGroupPrefix"];

	/// <summary>
	/// 检测上次排查中途退出遗留的临时分组（本地化名称前缀匹配，名称变化也能兜底识别）。
	/// </summary>
	public IReadOnlyList<ModGroup> FindStaleTempGroups()
	{
		var prefix = TempGroupPrefix;
		return _modGroupService.Groups
			.Where(static group => !group.IsDefault)
			.Where(group => group.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.ToList();
	}

	/// <summary>
	/// 开始新会话：创建临时分组、复制当前启用状态（含选项状态）、切换到临时分组。
	/// </summary>
	public async Task<BisectSession> StartAsync(ModGroup originalGroup, IReadOnlyList<ModData> allMods)
	{
		if (Current is not null)
			throw new InvalidOperationException("A bisect session is already in progress.");

		var tempGroup = await _modGroupService.CreateGroupAsync(TempGroupName);
		var enabledMods = _modGroupService.FilterMods(allMods)
			.Where(static mod => mod.Enabled)
			.ToList();
		await _modGroupService.AddModsToGroupWithCurrentStateAsync(tempGroup.Id, enabledMods);
		await _modGroupService.SelectGroupAsync(tempGroup.Id, allMods);

		// 记录原分组顺序：优先取用户最近一次保存的顺序（导航前 Dashboard 已保存），
		// 过滤出当前分组仍存在的成员；取不到时退回 ModService 加载顺序。
		var groupMemberGuids = _modGroupService.FilterMods(allMods)
			.Select(static mod => mod.Manifest.Guid)
			.ToHashSet();
		var originalOrder = (_profileSaveCoordinator.GetCurrentOrder() ?? [])
			.Where(groupMemberGuids.Contains)
			.ToArray();
		if (originalOrder.Length == 0)
			originalOrder = _modGroupService.FilterMods(allMods).Select(static mod => mod.Manifest.Guid).ToArray();

		var session = new BisectSession
		{
			OriginalGroupId = originalGroup.Id,
			OriginalGroupName = originalGroup.Name,
			TempGroup = tempGroup,
			AllMods = allMods,
			InitialEnabledMods = enabledMods,
			OriginalOrder = originalOrder,
			Candidates = enabledMods.Select(static mod => mod.Manifest.Guid).ToList(),
		};
		Current = session;
		_logger.LogInformation("Bisect session started: temp group {TempGroup}, {count} candidate mods", tempGroup.Name, session.Candidates.Count);
		return session;
	}

	/// <summary>
	/// 准备本轮：候选集前一半启用、其余禁用，并保存临时分组状态。候选集必须 ≥2。
	/// </summary>
	public async Task<BisectRound> PrepareRoundAsync()
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		if (session.Candidates.Count < 2)
			throw new InvalidOperationException("Not enough candidates for a bisect round.");

		var testedGuids = BisectCore.SplitTestedGuids(session.Candidates);
		var testedSet = testedGuids.ToHashSet();
		var members = session.TempGroup.ModGuids.ToHashSet();

		foreach (var mod in session.AllMods)
		{
			if (members.Contains(mod.Manifest.Guid))
				mod.Enabled = testedSet.Contains(mod.Manifest.Guid);
		}

		await _modGroupService.SaveSelectedGroupStateAsync(session.AllMods);

		var testedMods = session.AllMods
			.Where(mod => testedSet.Contains(mod.Manifest.Guid))
			.ToArray();
		return new BisectRound { TestedMods = testedMods };
	}

	/// <summary>
	/// 应用用户报告：崩溃则嫌疑在测试集内，未崩溃则在禁用的一半内。
	/// </summary>
	public void ApplyResult(bool crashed, BisectRound round)
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		var testedGuids = round.TestedMods.Select(static mod => mod.Manifest.Guid).ToArray();
		session.Candidates = BisectCore.ApplyReport(session.Candidates, testedGuids, crashed).ToList();

		if (crashed)
			session.HasCrashed = true;

		session.Rounds.Add(new BisectRoundRecord
		{
			RoundIndex = session.Rounds.Count + 1,
			TestedModNames = round.TestedMods.Select(static mod => mod.Manifest.Name).ToArray(),
			Crashed = crashed,
		});
	}

	/// <summary>
	/// 记录迭代验证部署（剩余模组全量部署）发生崩溃，成为后续二分的崩溃证据。
	/// </summary>
	public void RecordVerificationCrashed()
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		session.HasCrashed = true;
	}

	/// <summary>
	/// 收敛后把唯一候选标记为嫌疑（临时分组内禁用并保存）。
	/// </summary>
	public async Task DisableSuspectAsync()
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		if (session.Candidates.Count != 1)
			throw new InvalidOperationException("Bisect candidates have not converged.");

		var suspectGuid = session.Candidates[0];
		if (session.Suspects.Contains(suspectGuid))
			return;

		var suspect = session.AllMods.FirstOrDefault(mod => mod.Manifest.Guid == suspectGuid);
		if (suspect is null)
			return;

		suspect.Enabled = false;
		session.Suspects.Add(suspectGuid);
		await _modGroupService.SaveSelectedGroupStateAsync(session.AllMods);
		_logger.LogInformation("Bisect suspect disabled in temp group: {ModName}", suspect.Manifest.Name);
	}

	/// <summary>
	/// 临时分组内当前仍启用的模组（用于迭代排查剩余候选）。
	/// </summary>
	public IReadOnlyList<ModData> GetRemainingEnabledMods()
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		var members = session.TempGroup.ModGuids.ToHashSet();
		return session.AllMods
			.Where(mod => members.Contains(mod.Manifest.Guid) && mod.Enabled)
			.ToList();
	}

	/// <summary>
	/// 迭代排查：以剩余启用模组作为新的候选集（调用前需先部署验证仍崩溃）。
	/// </summary>
	public void ContinueWithRemaining(IReadOnlyList<ModData> remaining)
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		session.Candidates = remaining.Select(static mod => mod.Manifest.Guid).ToList();
	}

	/// <summary>
	/// 按当前临时分组状态构建并部署（内部先清理旧文件）。
	/// </summary>
	public Task DeployAsync()
	{
		var group = _modGroupService.SelectedGroup;
		var groupMods = _modGroupService.FilterMods(_modService.Mods).ToList();
		var order = groupMods.Select(static mod => mod.Manifest.Guid).ToArray();
		var snapshot = _profileSaveCoordinator.Capture(groupMods, order, group.Id, group.IsDefault);
		var deploymentMods = DeploymentOrderHelper.BuildDeploymentMods(
			snapshot,
			_settingsService.UseDeploymentOrder,
			_settingsService.DeploymentOrderGuids,
			_settingsService.DeployBottomToTop);
		return _modService.DeployAsync(deploymentMods);
	}

	/// <summary>
	/// 结束会话：切回原分组，可选在原分组禁用全部嫌疑模组，删除临时分组。
	/// 默认组走 ProfileSaveCoordinator（enabled_mods 是默认组权威来源），非默认组走分组缓存。
	/// </summary>
	public async Task FinishAsync(bool disableSuspectsInOriginalGroup)
	{
		var session = Current ?? throw new InvalidOperationException("No bisect session.");
		try
		{
			await _modGroupService.SelectGroupAsync(session.OriginalGroupId, session.AllMods);

			if (disableSuspectsInOriginalGroup && session.Suspects.Count > 0)
			{
				var suspectSet = session.Suspects.ToHashSet();
				foreach (var mod in session.AllMods)
				{
					if (suspectSet.Contains(mod.Manifest.Guid))
						mod.Enabled = false;
				}
				await SaveOriginalGroupStateAsync(session);
				_logger.LogInformation("Bisect finished: disabled {count} suspect mods in group {GroupName}", session.Suspects.Count, session.OriginalGroupName);
			}

			await _modGroupService.DeleteGroupAsync(session.TempGroup.Id);
		}
		finally
		{
			Current = null;
		}
	}

	private async Task SaveOriginalGroupStateAsync(BisectSession session)
	{
		if (_settingsService.IsReadonly)
			return;

		var group = _modGroupService.SelectedGroup;
		var groupMods = _modGroupService.FilterMods(session.AllMods).ToList();
		var snapshot = _profileSaveCoordinator.Capture(groupMods, session.OriginalOrder, group.Id, group.IsDefault);
		await _profileSaveCoordinator.SaveNowAsync(snapshot);
	}
}
