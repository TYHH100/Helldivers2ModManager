# M10 切换审计（已完成）

> 十个里程碑的结构切换和自动化验证已完成；人工验收已于 2026-08-26 通过。

## 1. 当前已切到新后端

| 消费面 | 旧路径 | 新路径 | 当前证据/状态 |
|---|---|---|---|
| Nexus API | `Services/Nexus/NexusModsService` + `NexusHttpClient` | `Core.Nexus.NexusApiClient` + `NexusModsServiceAdapter` | UI 注入的 `INexusModsService` 已在 `App.AddServices` 末尾覆盖为适配器；V3 端点、全局 Mod ID、文件映射、1h/4h 缓存、网络重试、更新失败容错和单文件回退有回归测试。 |
| Mod ViewModel identity-map | `ModService.GetOrCreateModViewModel` / `_modViewModelCache` | `Adapters.ModViewModelFactory` | Dashboard 两处创建入口已改用 `GetOrCreate`；旧服务缓存成员已移除，工厂生命周期有测试。 |
| 类型检测与自动打标应用 | `Services.ModTypeDetectionService` 本地扫描/标签合并/创建 | `Core.Mods.ModTypeDetectionService` + `Core.Mods.AutoTaggingService` | Dashboard 扫描、别名复用、手动映射、缺失标签创建和内置标签合并均已切 Core；算法行为有守护测试。 |
| 部署顺序构建 | `Services.DeploymentOrderHelper` 本地排序分支 | `Core.Deployment.DeploymentOrderBuilder` 泛型排序 | Dashboard 与 Bisect 共用 Facade 已调用 Core；显式顺序、补齐和方向反转有守护测试。 |
| 文件哈希计算/缓存 | `Services/FileHashRepository` + 旧 `file_hashes` 表 | `Core.Mods.FileHashService` + Core `file_hashes` | 单文件、目录、缓存命中、失效、陈旧记录替换和删除 Mod 清理有守护测试；统一库位于程序目录。 |
| 部署执行与二分部署执行 | `Services.ModService.DeployAsync` 本地计划/复制/符号链接 | `Core.Deployment.DeploymentService.CreatePlan` + `DeployPlanAsync` | `ModService.DeployAsync` 已映射清单、选项、SkipList 和按 Mod 分步回调；Core 计划/清理/占位/复制/大文件进度有测试。 |
| 删除 Mod 后的已部署文件清理 | `Services.ModService.CleanupDeployedFilesForModAsync` 本地枚举/冲突判断 | `Core.Deployment.DeploymentService.CleanupDeployedFilesAsync` | 旧服务已映射清单和选项；Core 按部署计划识别共享目标，独占主 patch/GPU/stream 删除，共享文件保留有守护测试。 |
| 压缩包导入 | `Services.ModService.TryAddModFromArchiveAsync` 本地提取/嵌套导入 | `Core.Mods.ModArchiveService.ImportArchiveAsync` + `Adapters.CoreModMapper` | 提取、根目录展平、嵌套包、清单推断/校验、同名替换和临时目录清理走 Core；反向清单映射和问题映射有测试。 |
| 删除 Mod 目录 | `FileSystem.DeleteDirectory` 直接调用 | `Core.Mods.ModDirectoryService.DeleteAsync` + `Win32RecycleBinAdapter` | 存储根路径守卫、回收站/永久删除和 Core 哈希记录清理走 Core；失败结果会抛回原任务错误链路。 |
| Mod 导出 | `DashboardPageViewModel.DoExportAsync` 本地 ZIP/7z 压缩 | `Core.Mods.ModArchiveService.ExportAsync` + `Adapters.CoreArchiveFormatMapper` | 五种压缩挡位、排除备份/归档、大小估算、进度回调和输出目录创建走 Core；格式映射有测试，UI 保留大字典警告与完成定位。 |
| Mod 更新 | `Services.ModService.UpdateModFromArchiveAsync` 本地提取/哈希/增量复制 | `Core.Mods.ModArchiveService.PrepareUpdateSourceAsync` + `Core.Mods.ModDirectoryService.UpdateFromDirectoryAsync` + 清单映射器 | 解压展平、清单身份保留/净化、缓存哈希、差异比较、增量复制、删除清理和阶段进度走 Core；旧服务只映射进度、恢复运行时状态并同步旧哈希库。 |
| 护甲复用扫描 | `Services.ArmorReuseService` 本地扫描/归档解析 | `Core.Analysis.ArmorReuseService` + 清单/结果映射器 | UI Facade 已切 Core，并传入用户配置游戏数据目录；清单选项映射、结果映射和无游戏目录行为有测试。 |
| 冲突扫描 | `Services.ModConflictService` 本地扫描/归档解析 | `Core.Analysis.ModConflictService` + 结果映射器 | UI Facade 已切 Core，并保留历史缓存键；显式游戏目录、无目录扫描、结果映射和胜者语义有测试。 |
| 文本搜索匹配算法 | `Services/FuzzySearchMatcher` | `Core.Search.FuzzySearchMatcher` | `SearchFilterService.cs:4` 使用类型转发别名；拼音/首字母/子序列测试在新测试项目。 |
| 运行时语言字符串 | 手工解析 JSON 字典 | `Core.Localization.StringResources` resx 卫星资源 | `LocalizationService.ApplyLanguage` 从 ResourceManager 载入字符串；JSON 仅保留语言元数据扫描。 |
| 设置与启动配置 | `settings.json` 手工 JSON 读写字段 | `BootConfigurationStore` + `PreferenceRepository.AppSettings` | 主应用启动/保存已读 `boot.json` 与 Core 偏好库；旧 `settings.json` 保留不读写、不删除。PatchTool 的只读默认初始化保留兼容构造。 |
| 版本检测 | `VersionCheckService` 本地补丁扫描/游戏归档索引 | `Core.Versioning.PatchStructureAnalyzer` + `Core.Versioning.VersionCheckService` | 批量/单 Mod 检测和 Unit 提取走 Core；结果和详细分析通过 `CoreVersionCheckMapper` 映射。 |
| 元数据修复 | 本地安全元数据计划/写入 | `Core.Repair.MetadataRepairService` | 计划、执行和返回模型已映射；旧路径保留为显式兼容回退。 |
| 辅助 LOD/材质修复 | 本地辅助修复计划/写入 | `Core.Repair.AssistedRepairService` | 普通、Mixed、Automatic 计划和修复入口走 Core；动作、枚举和 FriendlyName 映射保留。 |
| 伴生恢复 | 本地精确复制/游戏配方重建 | `Core.Repair.CompanionRecoveryService` | 模组级计划和恢复走 Core；旧结果模型保留 UI 兼容。 |
| 备份历史/恢复/回滚 | `VersionCheckService` 私有备份扫描与恢复 | `Core.Repair.BackupService` 详细备份能力 | 历史、定点恢复、整模组回滚、删除保护和清理走 Core；结构校验和 PreRestore 回滚语义已上移。 |
| 批量修复编排 | 本地三段修复循环 | `Core.Repair.BatchRepairService` | Core 按“伴生 → 元数据 → 辅助”计划/执行，并保留有限并发；UI Facade 只映射状态、计数、消息和进度。 |
| 资源查看器 | `PatchResourceInspectionService` 本地 TOC/GPU/纹理解析 | `Core.Preview.PatchResourceInspector` | 页面直接注入 Core Inspector；TOC、GPU Stream、纹理列表和预览数据使用 Core 模型。 |
| 模型预览 | `PatchResourceInspectionService` + `VersionCheckService` 私有游戏索引 | `Core.Preview.PatchResourceInspector` + `Core.GameData.GameArchiveService` | 几何/材质/纹理解码走 Core；包名归属和 Helldiver 动画库由 Core 归档服务提供，UI 与 GPU skinning 使用类型别名消费 Core 模型。 |
| 后台任务执行 | `BackgroundTaskService` 内部 `Task.Run` + 终态样板 | `Core.Common.IBackgroundTaskRunner` | UI 集合、前台任务和步骤弹窗语义保留；RunAsync 执行与终态结果由 Core Runner 返回后映射。 |
| 持久化与分组状态 | `DatabaseService` + `mod_groups`/`group_enabled_mods` | Core `ProfileRepository`、`GroupRepository` 和 `mod_group_members` | 默认启用状态继续由 `EnabledStateRepository` 写入 `mod_states`；自定义分组成员迁移到 Core 统一库，默认虚拟分组不写入成员外键表。 |

## 2. 明确仍待切换

- 护甲污染：显式放弃切换；当前功能已在 UI 中临时禁用（`ArmorPollutionPageViewModel` 未注册），M10 保留禁用现状，不为其恢复旧扫描链路。
- `Helldivers2PatchTool`：按计划暂不改造；已移出解决方案构建，源码保留待专项适配。

## 3. 反射注册状态

反射扫描机制已移除：`RegisterServiceAttribute` 已删除，主应用组合根改为显式注册应用服务、页面 ViewModel 和 Core 后端；旧 Nexus 实现已删除，接口与 Core 适配器保留。  
`Helldivers2PatchTool` 已移出解决方案构建，源码按计划保留待专项适配。  
Core 与 Core 测试项目已从 `next/` 移到仓库根目录。

## 4. 完成门槛

1. 显式放弃项均有记录：护甲污染保持禁用，PatchTool 移出解决方案。
2. `rg` 抽查确认主应用不再引用旧 `DatabaseService`、反射注册和已删除 Nexus 基础设施接口。
3. 解决方案 Debug 构建 0 警告 / 0 错误；Core 测试 167/167 通过；主应用守护测试 184/184 通过。
4. 用户已于 2026-08-26 完成人工验收并确认通过。
5. 结构收尾完成：Core 项目移到根目录、反射注册删除、旧数据库服务和旧 Nexus 实现删除；旧 `settings.json` 与 `mod_manager.db` 按约定继续保留现场。

## 5. 最近验证

- 最新验证（预览与后台任务桥接后复验）：解决方案 Debug 构建 0 警告 / 0 错误；新 Core 测试 167/167 通过；旧守护测试 232/232 通过（2026-08-26）。
- 人工验收产物：主程序 Release/win-x64/self-contained 单文件发布通过，输出 `publish/Helldivers2ModManager.exe`；SHA-256 为 `F67455E981F59805C3349F3E29B094057894522B5B96906E040E50FBBA3C9935`。已修复启动期 Core `IFileHashRepository` 缺注册和测试中心 resx 缺失，并通过 Release 实机启动检查。
- 切换残留抽查：主应用内无直接调用旧 `PatchResourceInspectionService.PreviewModelAsync`、旧动画库查找或旧 Unit 包名解析的代码；资源查看器与模型预览消费面已切至 Core。
- 新增“验收测试中心”（标题栏扳手或 F12 进入）：集中提供导入压缩包、重扫、版本检查、冲突扫描、护甲复用、部署、日志和相关页面入口；中英文资源已同步。带测试中心的 Release 已重新发布。
- 已修复测试中心后台线程直接更新日志集合和绑定状态导致“重新扫描模组”崩溃的问题；解决方案 Debug 构建、Core 测试 167/167、旧守护测试 232/232 复验通过，Release 已重新发布并通过实机启动存活检查。
- 结构收尾复验：Core 项目迁移后 Debug 构建通过；显式 DI 替换反射后 Core 测试 167/167 通过，旧守护测试删除重复模糊搜索/类型检测用例后为 187/187 通过；Debug 实机启动能进入 Dashboard 并完成设置加载。
- 最终结构收尾复验（2026-08-26）：分组仓储切到 Core 后 Debug 构建 0 警告 / 0 错误；修复 Core schema v2 的 `mod_group_members` 复合外键并提供 v1 错误结构重建；Core 测试 167/167 通过，旧守护测试 184/184 通过。Release/win-x64/self-contained 单文件发布通过，SHA-256 为 `D6C0865E846FD0E02810EFABDF1275061692314F5A63CD31F8B607DB322CEDB5`；发布程序隐藏启动 8 秒存活，退出后最新日志无 Error/Critical。



