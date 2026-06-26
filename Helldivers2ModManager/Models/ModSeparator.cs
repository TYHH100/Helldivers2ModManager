using System.Text.Json.Serialization;

namespace Helldivers2ModManager.Models;

/// <summary>
/// 模组列表分隔符，用于在 Dashboard 中对模组进行分类和分组
/// </summary>
internal sealed class ModSeparator
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = GetDefaultName();

    private static string GetDefaultName()
    {
        try
        {
            if (System.Windows.Application.Current is App app)
                return (app.Host?.Services?.GetService(typeof(Services.LocalizationService)) as Services.LocalizationService)?["ModSeparator.DefaultName"] ?? "新分隔符";
        }
        catch { }
        return "新分隔符";
    }

    public string Color { get; set; } = "#FF3B82F6";

    public bool IsExpanded { get; set; } = true;

    /// <summary>
    /// 归入此分隔符的模组 GUID 列表（按显示顺序）
    /// </summary>
    public List<Guid> ModGuids { get; set; } = [];

    /// <summary>
    /// 在模组列表中的显示位置索引（-1 表示追加到末尾）
    /// </summary>
    public int DisplayIndex { get; set; } = -1;
}
