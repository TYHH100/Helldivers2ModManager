using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 游戏进程检测。部署、清理、删除等写游戏目录的操作执行前必须确认游戏未运行：
/// 游戏启动时整段读取补丁链，运行中增删 <c>data</c> 下的补丁文件会与之冲突（句柄占用、补丁链读到一半）。
/// 只做检测与拦截，绝不主动结束游戏进程——用户可能正在游戏中，强制关闭会丢进度。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class GameProcessService
{
	private const string GameProcessName = "helldivers2";

	private readonly ILogger<GameProcessService> _logger;

	public GameProcessService(ILogger<GameProcessService> logger)
	{
		_logger = logger;
	}

	public bool IsGameRunning()
	{
		return Process.GetProcessesByName(GameProcessName).Length > 0;
	}
}
