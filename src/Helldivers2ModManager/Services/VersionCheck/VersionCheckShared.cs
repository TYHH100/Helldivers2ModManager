using System;
using System.Threading;

namespace Helldivers2ModManager.Services;

/// <summary>
/// VersionCheck 拆分后各子服务共享的补丁格式常量与修复写入闸门。
/// 常量定义参考 hd2-repatcher 与 HD2SDK-CommunityEdition 的补丁结构研究。
/// </summary>
internal static class VersionCheckShared
{
    /// <summary>Unit 资源类型 ID。</summary>
    public const long UnitTypeId = unchecked((long)16187218042980615487UL);

    /// <summary>补丁文件头魔数（0xF0000011）。</summary>
    public const int PatchHeaderMagic = unchecked((int)0xF0000011);

    /// <summary>Unit 版本阈值：低于此值时需要检查旧版 Layout Format 格式。</summary>
    public const uint VersionThresholdForLayoutCheck = 0xA4CD36u;

    public const int HeaderSize = 72;
    public const int TypeEntrySize = 32;
    public const int FileEntrySize = 80;

    /// <summary>修复/备份/恢复写入的串行闸门（原 VersionCheckService._repairSemaphore）。</summary>
    public static readonly SemaphoreSlim RepairGate = new(1, 1);

public static bool IsMainPatchFile(string name)
    {
        return name.Contains(".patch_", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(".hd2mm-repair-", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(".hd2mm-backup", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase);
    }
}
