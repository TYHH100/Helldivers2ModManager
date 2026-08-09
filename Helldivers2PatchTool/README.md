# Helldivers 2 Patch Check & Repair Tool

独立补丁检测与修复工具。无需安装或启动 Helldivers2ModManager，也不读取其模组库、设置或数据库。

使用方式：

1. 运行 `Helldivers2PatchTool.exe`。
2. 选择任意 Mod 管理器管理的 Mod 根目录或补丁目录。
3. 工具会自动检测 Steam 游戏目录；未找到时可点击“自动检测”或手动选择。
4. 点击“开始检测”。
5. 确认游戏目录有效后，点击“一键修复”。工具会按管理器相同的安全顺序处理：先恢复可验证的 `.gpu_resources` / `.stream` companion，再修复 TOC 元数据，最后使用当前游戏参考自动修复 Unit 和材质绑定。
6. 每个写入阶段都会创建补丁备份并复检；非 Unit 模组（例如音频模组）会被跳过，不会创建备份或修改文件。

发布命令：

```powershell
dotnet publish Helldivers2PatchTool\Helldivers2PatchTool.csproj --configuration Release -o Helldivers2PatchTool\publish
```
