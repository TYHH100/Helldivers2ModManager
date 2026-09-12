namespace Helldivers2ModManager.Models;

/// <summary>
/// One restored Lua script finding inside a mod patch: either LuaJIT bytecode (decoded to a
/// readable listing) or plain-text Lua source. The <see cref="Report"/> is the finished,
/// display-ready text; building it never executes any Lua (static parsing only).
/// </summary>
internal sealed record LuaScriptEntry(
    string PatchRelativePath,
    ulong ResourceFileId,
    ulong ResourceTypeId,
    /// <summary>资源在补丁内的数据偏移（仅定位用途）。</summary>
    long ResourceOffset,
    uint ResourceSize,
    /// <summary>字节码块（含嵌套）数量；明文源码恒为 1。</summary>
    int BlockCount,
    int TotalPrototypeCount,
    /// <summary>面向用户的来源描述（补丁 + 资源 + 块序号）。</summary>
    string Title,
    /// <summary>完成排版、可直接展示/复制的静态还原文本。</summary>
    string Report,
    /// <summary>bytecode / source / failed。</summary>
    string Kind)
{
    public bool IsBytecode => Kind == "bytecode";
}

/// <summary>One patch's script findings.</summary>
internal sealed record LuaScriptPatchGroup(
    string PatchRelativePath,
    IReadOnlyList<LuaScriptEntry> Entries);

internal sealed record LuaScriptInventoryResult(
    IReadOnlyList<LuaScriptPatchGroup> Groups,
    int PatchCount,
    string? Error)
{
    public static readonly LuaScriptInventoryResult Empty = new([], 0, null);
}
