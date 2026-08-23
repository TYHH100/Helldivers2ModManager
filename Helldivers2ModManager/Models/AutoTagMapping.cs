namespace Helldivers2ModManager.Models;

/// <summary>
/// 手动指定的「自动识别类型 → 标签」配对。
/// 在自动打标签时优先使用该配对，其次按名称匹配已有标签。
/// </summary>
public sealed class AutoTagMapping
{
    public ModType Type { get; set; }

    public Guid TagId { get; set; }
}
