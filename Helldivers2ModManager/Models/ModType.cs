namespace Helldivers2ModManager.Models;

/// <summary>
/// 模组自动识别类型。由 patch TOC 的 TypeId 分布、payload 签名（BKHD/DDS）与
/// 内嵌明文路径字符串（如 content/audio/...）综合判定。
/// </summary>
public enum ModType
{
    /// <summary>未能识别（无 patch、未知结构或证据不足）。</summary>
    Unknown = 0,

    /// <summary>音效模组：Wwise SoundBank（BKHD/DIDX）或 Audio TypeId。</summary>
    Audio,

    /// <summary>UI 模组：仅含纹理资源（图标/界面贴图）。</summary>
    Ui,

    /// <summary>贴图/材质替换包：纹理 + 材质，不含模型。</summary>
    Texture,

    /// <summary>护甲/服装模组：模型 Unit + 纹理 + 材质，无骨骼/状态机。</summary>
    Armor,

    /// <summary>战略配备（静态装备）：Unit + Bones + StateMachine，无动画。</summary>
    Stratagem,

    /// <summary>支援武器/武器模组：Unit + Bones + StateMachine + Animation。</summary>
    SupportWeapon,

    /// <summary>敌人模组：路径提示（vo_bugs 等）指向敌人，覆盖结构分类。</summary>
    Enemy,

    /// <summary>其他模型替换（骨架不完整或证据不足的模型类）。</summary>
    Model,

    /// <summary>主武器模组：路径提示（primary_weapons）指向主武器。</summary>
    PrimaryWeapon,

    /// <summary>脚本/代码模组：Lua 等脚本资源（HUD、功能修改）。</summary>
    Script,

    /// <summary>HD2PhysBone 物理模组：携带参数集（hd2_spring_rig.bin / hd2_ib_needle.bin / lua_units.txt），
    /// 依赖 ReShade add-on 运行时；部署时参数复制到 bin\HD2PhysBone\ 并在部署序列中置底。</summary>
    PhysBone,
}
