# Helldivers2ModManager 开发指引

这份文件只记录会影响开发、排障和交付的约定。具体行为以当前源码、测试、资源和 Schema 为准；不要在这里复制会频繁变化的字段清单。

## 1. 工作原则

- 项目运行在 Windows，主程序是 WPF/.NET 10。默认使用 PowerShell。
- 先检查真实代码路径、实际运行边界和样例文件，再判断原因或修改方案。
- 工作树可能已有用户改动。修改前查看 `git status --short`，只触碰当前任务需要的文件，保留无关改动。
- 文件解析、部署和修复属于高风险操作：先校验路径、范围、哈希或备份信息，再写入；能只读完成的检查不要改动源文件。
- 测试需要临时复制文件时，测试结束必须删除临时副本，避免目录堆积。
- 需要联网查资料时优先使用 AnySearch 技能；没有必要时不要联网。
- 使用 `apply_patch` 编辑文本文件；不要用重置、覆盖或递归删除命令清理用户改动。

## 2. 项目边界

| 项目 | 作用 | 目标框架 |
|---|---|---|
| `Helldivers2ModManager` | 主 WPF 应用 | `net10.0-windows` |
| `Helldivers2ModManager.Tests` | MSTest 测试 | `net10.0-windows` |
| `Purger` | 独立清理工具 | `net10.0-windows` |
| `Helldivers2PatchTool` | 独立补丁检测/修复工具 | `net10.0-windows7.0` |

解决方案文件是 `Helldivers2ModManager.sln`。主应用的服务、模型、ViewModel、View 和资源分别位于同名目录；补丁解析与模型/纹理预览的关键实现集中在 `Services/`、`Models/` 和对应的 `ViewModels/` 中。

版本以 `Helldivers2ModManager/App.xaml.cs` 和主项目文件为准，不在本文件维护版本号。

## 3. 架构约定

### 服务注册

服务由 `RegisterServiceAttribute` 配合 `App.xaml.cs` 反射注册：

```csharp
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class MyService
{
}
```

- 默认使用 `internal`，仅必要时公开类型。
- 长期共享的状态或服务使用 `Singleton`；页面 ViewModel 通常使用 `Transient`。
- 需要接口和实现类共用实例时使用 `Contract`，参考 `Services/Nexus/` 中的写法。
- 新增页面必须同时完成三件事：ViewModel 注册、`MainWindow.xaml` 的 `DataTemplate`、对应 View。缺少模板会导致导航空白或渲染错误。

### MVVM 与 WPF

- ViewModel 继承项目已有的基类或 `ObservableObject`，使用 CommunityToolkit 的 `[ObservableProperty]` 和 `[RelayCommand]`。
- 业务逻辑放在 ViewModel/Service，不在 code-behind 中堆积。
- 公共控件样式集中在 `Resources/Styles/FluentControls.xaml` 等共享资源中；引用前先确认资源键确实存在。
- 后台线程不得直接修改 WPF 集合或绑定属性；通过 `BackgroundTaskService` 或 Dispatcher 切回 UI 线程。

### 本地化

- 运行时本地化由 `Services/LocalizationService.cs` 提供，资源在 `Resources/Language/*.json`。
- XAML 使用 `Extensions/LocExtension.cs`；代码使用注入的 `LocalizationService`。
- 新字符串按 `Section.Key` 命名，并同步更新 `zh-CN.json`、`en-US.json`；不要在 View 中硬编码用户可见文本。

## 4. 数据和文件解析硬约束

### Mod 清单

- 清单格式以 `mod_manifest_v1-schema.json` 和 `Models/` 中的实现为准。
- 必须保持 Legacy 与 V1 的兼容行为；不支持的版本应沿用现有异常和提示链路。
- 修改清单保存、选项或部署逻辑时，要验证 `manifest.json`、备份和恢复行为，不要只验证页面显示。
- Dashboard/Profile 中的启用状态是部署输入的来源；不要用临时 View 状态或扫描结果覆盖用户选择。

### Patch、GPU 和 Stream

补丁文件通常包括：

```text
{16 位小写十六进制}.patch_{索引}
{同名}.patch_{索引}.gpu_resources
{同名}.patch_{索引}.stream
```

解析规则以 `VersionCheckService` 和 `PatchResourceInspectionService` 为准：

- 按单文件评估内存；普通小文件可以走内存快路径，但不能成为唯一路径。
- `.gpu_resources` 不得整体读入内存。大型或不确定大小的文件使用 `FileStream` 随机读取必要结构。
- 偏移和长度使用 `long`/`ulong` 计算；每次读取前检查 `offset + size` 未溢出且在对应流长度内。
- 同时验证主 patch、GPU companion、stream companion 的范围、对齐、Unit/LOD 和声明尺寸。
- 未知格式或无法确认的结构标为警告并记录原因，不凭猜测当作损坏或自动修复。
- 版本检查、冲突分析和修复流程要保持边界清晰；批量修复前确认目标类型确实支持自动修复，并保留备份/回滚信息。

## 5. 模型和纹理预览

- 预览必须依据真实 Mod 中的 MeshInfo、资源表、偏移、顶点布局和变换解析；几何异常应修正解析链路，不用形状猜测或硬编码补丁掩盖问题。
- GPU/纹理数据读取要有上限、取消和缓存策略；避免把整套资源或所有贴图一次性载入内存。
- 使用有限并发处理 patch 级 I/O；新的选择触发时取消旧任务，重建请求需要合并，避免并发重建和过期结果回写。
- 纹理预览要根据实际通道统计判断用途，并保留 `RGB`、`RGBA`、`A` 等明确显示模式；不要默认把 Alpha 当作模型不透明度。
- 预览相关改动至少覆盖：资源边界、MeshInfo/变换、材质贴图匹配、纹理格式/通道、取消和缓存行为。

### 材质变体去重与纯黑占位材质（特例模型黑色预览问题）

某些特例模型（如角色装甲"白银之城-侦探-CW9"）在预览中整体显示为黑色，但游戏内显示正常。根因是模型同时包含高分辨率正常材质和低分辨率纯黑占位材质，预览工具渲染了全部变体导致纯黑覆盖。修复分两层：

1. **材质变体去重（同一几何体的不同材质）**：同一 MeshInfo 内，`VertexOffset`/`VertexCount`/`IndexCount` 相同但 `IndexOffset` 不同的 section 引用的是不同索引存储位置但同一组三角形（材质变体）。去重分组键必须使用 `(MeshInfoIndex, VertexOffset, VertexCount, IndexCount)`，**不能包含 `IndexOffset`**，否则变体不会被分到同一组。同组内保留 Albedo 纹理像素数最大的变体（高分辨率正常材质 > 低分辨率纯黑占位）。

2. **纯黑占位材质检测（独立几何体区域的纯黑材质）**：部分 section 使用 BC7 编码的纯黑占位材质（所有块解码后像素为 `(0,0,0,255)`），几何体与正常 section 不同，去重无法处理。先从顶层纹理的开头、四分位、中间和末尾等至少 5 个位置有界采样 BC7 块；再解码受限尺寸的缩略图，只有每个 BGRA 像素都是 `(0,0,0,255)` 才能判为纯黑。**不能只检查前 64 字节**：真实贴图的起始块可能稀疏或空白，而脸部等有效内容在后续区域。跳过纯黑 section 时，**只在 stream 中存在非纯黑 section 时才跳过**，避免移除该 stream 的全部几何体导致模型缺少部分。

3. **语义 hash 识别**：纹理语义 hash 使用 murmur64 高 32 位算法（`h32(name.lower())`）。`0xCAED6CD6` = h32("normal")，`0x756F6FA6` = h32("mra")，需在 `GetTextureRole` 中正确分类为 `Normal` 和 `Mask`，否则材质贴图匹配会失败。

4. **稀疏 section 的预览容量**：section 的顶点窗口可能覆盖共享缓冲中的大量未引用顶点。全局预览容量统计前，必须按三角形实际引用的索引压缩 position/UV；局部高精度 section 的限制可以高于普通部件，但仍由全模型的顶点和索引总上限兜底。不能把完整顶点窗口直接计入容量，否则角色主体或附件会被错误跳过。

排查类似问题的步骤：先用诊断测试输出每个 mesh 的 `UnitId`/`StreamIndex`/`MeshInfoIndex`/`VertexCount`/`TriangleCount`/`ColorTextureId`；找出相同 Unit/St/MI 但不同 ColorTexId 的成对 mesh；在 `TryReadUnitMaterialSections` 去重逻辑处添加临时 `Console.WriteLine` 输出 section 的 `(VertexOffset, VertexCount, IndexOffset, IndexCount)`，确认变体的 IndexOffset 是否不同；解码关键纹理统计平均颜色确认是否纯黑。

## 6. 长任务、日志和设置

### 后台任务

下载、导入、哈希/指纹、部署、清理、删除、导出、版本检查和批量修复等耗时操作应接入 `BackgroundTaskService`：

```csharp
var task = _backgroundTaskService.Add(name, description);
try
{
    // 长任务
    _backgroundTaskService.Complete(task, readyDescription);
}
catch (OperationCanceledException)
{
    _backgroundTaskService.Cancel(task, canceledDescription);
}
catch (Exception ex)
{
    _backgroundTaskService.Fail(task, ex.Message);
}
```

有总量时进度使用 `0..1`；未知总量使用 `IsIndeterminate`。不要从业务代码直接操作任务集合或后台线程直接改任务属性。

**耗时操作统一走 `BackgroundTaskService.RunAsync(...)`**：它负责后台线程执行（内部 `Task.Run`）并自动管理任务状态生命周期（`Add` → Running → `Complete`/`Fail`/`Cancel`），调用方不要再手写 `Add` + `Task.Run` + `Complete/Fail` 样板。work 委托在后台线程运行，只做计算；需要更新任务页描述/进度时用 `BackgroundTaskContext.Report(...)`（自动切回 UI 线程）；返回结果后由调用方在 UI 线程应用，不要在 work 内直接操作 WPF 集合或绑定属性。`BackgroundTaskService` 单独用 `Add/Update/Complete` 只管理状态、不提供后台线程；`await` 异步方法也不代表 CPU 密集工作离开了 UI 线程——异步 IO 会让出 UI，但同步 CPU 密集代码（LZ4 解码、SHA-256、压缩/解压、大文件解析循环）仍在调用线程（UI）执行并导致界面卡顿。服务内部的 CPU 密集解析（如 `GameUnitReferenceReader` 的索引构建与 Unit 引用解析、`ModService` 的复制/删除/解压）仍应在服务内部后台化，一处修复惠及所有调用方。新增/修改耗时服务方法后，检查所有 UI 入口（`[RelayCommand]`、点击详情等）是否仍会在 UI 线程触发 CPU 密集工作。

新增耗时操作时按"前台/后台"分类注册任务：有专属进度弹窗/对话框的操作（部署、删除、导入、更新、清理、导出、批量修复、二分部署、Init、Rescan）是前台任务，用 `RunAsync(..., isForeground: true)` 或 `Add(..., isForeground: true)`——任务页不显示，进入终态后由服务自动从 `Tasks` 移除；无弹窗的静默后台操作（哈希计算/迁移/重算、版本检查、冲突/护甲扫描等）用默认 `isForeground: false`，在任务页显示。任务页只展示后台任务（`VisibleTasks` 按 `IsForeground` 过滤）。前台任务即使从集合移除，弹窗持有的 `task.Steps` 集合引用依然有效，步骤列表照常更新；不要为"任务页不显示"而删掉前台任务的注册或弹窗步骤机制，否则部署弹窗的步骤列表会失效。

### 日志和设置

- 使用 `ILogger<T>`，按实际严重程度选择 `Trace`、`Debug`、`Information`、`Warning`、`Error`、`Critical`。
- 设置持久化路径为程序目录下的 `settings.json`，具体字段和迁移逻辑以 `SettingsService.cs` 为准。
- 日志清理由 `AutoCleanLogs` 和 `MaxLogFiles` 控制，按数量保留最新日志，适合低频打开应用的场景；修改时同步设置页面、本地化和加载兼容逻辑。
- Helldivers2PatchTool（独立工具）的文件日志由 Helldivers2PatchTool/FileLogger.cs 提供：写入程序目录 logs/，每次启动最多保留最新 5 个 .log（PatchToolLogging.CleanExcessLogs）；最低记录级别为 Debug，扫描/修复全流程输出每个补丁与每个 Unit 的明细。不能直接复用主程序 FileLogger（它依赖主程序 App.Current.LogLevel）。修改独立工具日志时同步检查 MainWindow.xaml.cs 的 _loggerFactory 初始化与流程日志。
- 路径设置必须验证目录和必要游戏文件；部署默认复制文件，符号链接仅在用户明确开启且权限满足时使用。

## 7. 安全和变更边界

- 删除 Mod 或部署文件前确认最终绝对路径在预期目录内；优先使用回收站、备份或可恢复操作。
- 修复流程必须先保存备份元数据，失败时恢复原文件，并在日志中说明跳过、失败和回滚原因。
- 不把游戏资源、用户 Mod 或样例文件提交到仓库；源文件默认只读检查。
- 移除功能时删除完整连接链路，并用 `rg` 做残留引用扫描，确认设置、菜单、服务、资源和文档没有孤立入口。
- 修改公共样式、解析器、部署源数据或共享服务后，优先检查所有调用方，而不是只验证当前页面。

## 8. 高频错误提醒

以下问题在本项目中已经多次造成误判、回归或返工，改代码前应逐项确认：

| 容易犯的错误 | 必须遵守的做法 |
|---|---|
| 在错误的临时目录或旧工作区修改 | 先确认 `Get-Location`、仓库根目录和 `git status --short`；本项目的正式工作区是 `D:\TYHH10-git\Helldivers2ModManager`。 |
| 看到检测异常就直接修复 | 已知能正常进游戏的 Mod 先只读分析真实 Patch、备份和哈希；先排除检测器误报，再决定是否修复。 |
| 用字典数量比较 Patch 类型表 | Legacy Patch 可以保留声明数量为 0 的空类型槽；按类型值双向比较，不能比较字典项数量。 |
| 只显示 `patchFile.Name` | 多选项目录经常包含同名 `.patch_0`；诊断和工具输出必须使用相对路径，不能把同名显示成重复扫描。 |
| 把所有大小不一致都当成损坏 | 区分真正截断（期望数据超出声明范围）和合法填充/警告；提出修复前先读取 `.hd2mm-backup.json`。 |
| 混淆二进制偏移基址 | 每个字段先标明所属记录和相对/绝对关系；MeshInfo 的材料/Section 偏移相对 MeshInfo 起点，GPU 顶点偏移要叠加正确的 Unit/Stream 基址，并用真实样例验证。 |
| 把整份多 GB 文件读入内存或无界哈希 | `.gpu_resources` 只做有界随机读取；使用 64 位偏移、范围检查和有限并发，避免为了诊断复制或扫描整个文件。 |
| 解析失败后静默退回整 Stream 或用球体/方盒掩盖 | 失败必须可观测、可测试；按每个 MeshInfo、Section、Transform 解码，优先修正数据模型，不增加形状猜测规则。 |
| 资源查看器显示 `0 个 GPU Stream` 就认定 GPU 损坏 | 先检查 Unit 版本门槛。版本 `1` 使用与 `10800437` 相同的旧顶点格式（如 `26/29/31/24`），应按旧格式表有界读取；只有实际 StreamInfo、步长、GPU 窗口或顶点样本失败才可判为 GPU 异常。 |
| 只限制每个 Patch，忘记全局容量 | 合并结果时再次检查总 Mesh、顶点和索引上限；并发数、读取上限和缓存上限都要有明确总量。 |
| 用旧快照或数据库覆盖主页选择 | 部署使用用户操作时捕获的 Profile/启用状态快照；单 Mod 刷新后同时更新 `ModViewModel`、主页摘要和缓存。 |
| Manifest 每改一个字段就立即保存 | `Done()` 中组装最终清单并一次保存；保留 `NexusData`，Legacy 修改跨入 V1 后立即重建运行时选项和主页状态。 |
| 批量修复只在按钮或最后一步过滤 | 在 `VersionCheckBatchRepairService` 生成计划前就限制为明确支持的 Unit 类型；音频和其他未支持类型只能跳过并说明原因。 |
| 用 `armornames.txt`、外部映射或冲突缓存替代事实 | Armor 关系/污染检查直接读取已启用 Mod 的 Patch；它是独立扫描，不要自动变成通用冲突或修复流程。 |
| 换甲产物与其他覆盖同一件护甲的模组共同部署 | 一键换甲生成的是“完整替换”产物（包含目标护甲所有 Unit + 自洽的材质/纹理）。若与同样覆盖该护甲的已启用模组（包括原目标护甲模组、污染模组等）共同部署，两者会争夺同一批 FileId/材质引用，极易导致游戏选择护甲时崩溃。UI 需明确提示用户：生成后禁用这些模组。 |
| XAML 重写后只看页面、不查 code-behind | 删除或重命名控件后立即 `rg` 查找旧 `x:Name`；同时检查共享样式、DataTemplate、深色主题默认箭头和所有导航入口。 |
| 把取消异常当成崩溃 | `TaskCanceledException` 可能只是防抖或新请求取消；先确认实际使用的功能和取消来源，再判断是否是真故障。 |
| `await` 长任务或手写 `Add`+`Task.Run`+`Complete/Fail` 样板 | 耗时操作统一走 `BackgroundTaskService.RunAsync(...)`（后台线程 + 状态生命周期一把管，见 §6）；`BackgroundTaskService` 单独用 `Add/Update/Complete` 只管理状态、不提供后台线程，`await` 只让出异步 IO，同步 CPU 密集代码（LZ4 解码、SHA-256、压缩/解压、大文件解析）仍在调用线程（UI）执行。服务内部 CPU 密集解析优先在服务内部后台化（参考 `GameUnitReferenceReader`/`ModService`/`ModHashService`/`PatchResourceInspectionService`），改完后检查所有 UI 入口。 |
| 用过时断言或并行构建验证 | 按当前 MSTest 版本使用 `Assert.AreEqual` 等兼容断言；涉及共享 `obj` 时串行构建/测试，验证生成代码时不要使用 `--no-build`。 |
| 只验证 CLI 发布，不验证 VS 发布 | 修改 `Helldivers2PatchTool` 时复现对应 Publish Profile；独立工具不能直接引用自包含 EXE，且共享主程序构建必须固定 `net10.0-windows` 和 `win-x64`。 |
| 模型预览整体黑色或局部缺失只查材质引用 | 特例模型同时含高分辨率正常材质和 BC7 纯黑占位材质；先按 `(MeshInfoIndex, VO, VC, IC)` 去重材质变体（不含 IO），再以多点 BC7 采样加解码后的全像素纯黑验证过滤占位，不能只看前 64 字节。对稀疏 section，按三角形引用压缩顶点后再做全局容量判断。详见 §5。 |
| 旧角色材质只替换父模板 ID | 先与同一装备的可用 Mod 对照。已验证 DP-00 的 `0x102/1280B/248B` 角色材质在当前游戏仍保留旧结构，只需将父模板 `0x54AE...` 替为 `0x8F66...`；不要凭另一份样例把变量表、结束偏移或材质版本重建。没有同资源证据的 emissive/未知 schema 仅警告，不自动重写。 |
| 旧角色材质包只给“引用 0x54AE 材质的 Unit”改用游戏 LOD | 对已验证的旧角色签名，`Unit=1`、游戏引用为 `0x00A4CD36` 且 Unit 实际引用待迁移的 `0x54AE...` 角色材质时，旧 LOD/Section 材质绑定会导致进舰船崩溃；自动修复必须改用当前游戏 LOD，同时保留 Mod 的 GPU 几何与纹理。未知材质或其他 Unit 版本仍按原有自定义模型策略处理。 |
| 旧角色材质包内不引用 0x54AE 的 Unit（如 Torso 槽位）被 strongCustomSlots 误保留旧 LOD | 同包的其他部位可能只引用游戏材质，`RequiresCurrentGameLodForLegacyCharacterMaterial` 对它们返回 false，自动分类又会因“Torso 槽位存在强自定义信号”而保留其 Mod LOD；修复后进入游戏选择装备崩溃（如七海nana7mi 替换 FS-34 灭绝者）。只要 patch 内存在 `0x54AE→0x8F66` 迁移，就应让该 patch 所有 `Unit=1` 且游戏引用 `0x00A4CD36` 的 Unit 统一改用游戏 LOD（`RequiresCurrentGameLodForLegacyCharacterPack`），保证同包 LOD 一致；与可正常运行的同类 Mod（如嘉然 DP-00：全部 Unit 均为游戏 LOD）对齐。 |
| 自动 Unit 修复只看 Mesh ID 或单个 Unit 的 GPU 大小 | 自定义角色可能沿用原 Mesh ID，并把一个部位拆成 Slim、Stocky 与小型 Any 材质/遮罩层；应按 `CustomizationSlot` 成组保留 Mod LOD，并继续保留同 Mesh 签名联动，不能只用单个 GPU 大小决定修复策略。 |
| 把所有当前 Unit 都当成 `10800438` | 当前游戏的 DP-00 资源实际使用 `0x00A4CD36`，其他资源可能使用 `0x10800438`；应从同 File ID 的游戏引用读取版本，并让 GPU 结构检查同时识别两个已验证版本。 |
| 用根容器直接解析页面 VM 或新增页面后返回主菜单内存不释放 | DI 容器会强引用所有解析过的 `IDisposable`（所有 `PageViewModelBase` 子类）到 `ServiceProviderEngineScope._disposables`，直到根容器/scope 释放；导航页面必须由 `NavigationStore` 通过独立 `IServiceScope` 解析（`Navigate<T>` 内部 `CreateScope`，导航离开时丢弃旧 scope），不要用注入的 `IServiceProvider` 直接 `GetRequiredService<页面VM>` 后手动 `Navigate(page)`，否则该页面及其模型/纹理数据会被容器持有到进程退出。 | 自定义角色可能沿用原 Mesh ID，并把一个部位拆成 Slim、Stocky 与小型 Any 材质/遮罩层；LOD 还承载 MeshInfo/Section 的材质绑定。发现强自定义信号后，必须按 `CustomizationSlot` 成组保留 Mod LOD，并继续保留同 Mesh 签名联动。静态装备页可能只显示 Slim，仍需验证实际玩家的 Stocky/动态渲染。 |
| 批量复制文件用无界 `Task.WhenAll` + 手动 `FileStream.CopyToAsync` | 部署（`ModService.DeployAsync`）、导入（`IOExtensions.CopyTo`）、增量更新（`UpdateAsync`）统一为：收集文件对后用 `Parallel.ForEachAsync`/`Parallel.ForEach` 限制并发（`Math.Clamp(Environment.ProcessorCount / 2, 2, 4)`）+ Windows 内核态 `File.Copy(..., true)`（CopyFile2）。无界并发会让磁盘队列过深反而降吞吐；托管 `CopyToAsync` 比内核态复制慢且每个文件多一份异步状态机/缓冲开销。符号链接部署分支保留 `File.CreateSymbolicLink`。手动流复制的 buffer 统一用 81920，不要用 4096。 |
| `RunAsync` 终态同步执行，抢在排队的步骤更新（BeginInvoke）前把任务标记终态 | `RunAsync` 的 `Complete`/`Fail`/`Cancel` 必须经 `QueueOnUiThread`（无条件 `Dispatcher.BeginInvoke`）排队执行，不能用 `RunOnUiThread`（UI 线程调用时同步执行）。否则 work 期间入队的 `CompleteStep`/`UpdateStep`（如符号链接部署瞬间完成的步骤）会被终态守卫（`task.Status != Running`）拦截，步骤永远停在"正在部署"（蓝色 Running），成功弹窗也显示冻结状态。复制模式部署慢、队列基本排空所以不暴露；符号链接模式必现。 |
| 用 `TaskCompletionSource` 桥接弹窗后不处理用户点“取消”按钮 | `MessageBoxSelectionMessage` 的取消按钮默认只隐藏覆盖层、不触发任何回调；`MessageBoxConfirmMessage` 的“否”按钮才触发 `Abort`。凡是用 TCS 等待弹窗结果的调用方必须给 `MessageBoxSelectionMessage` 传 `Abort` 回调（如 `Abort = () => tcs.TrySetResult(取消值)`），否则用户点取消后流程永久挂起。 | 
| 保存分组状态时覆盖了用户的自定义排序 | `SaveAllAsync`/`SaveStatesAsync` 按快照 `Mods` 顺序写 `SortOrder`；在非 Dashboard 页面保存分组状态时，必须保留原分组顺序：优先用 `ProfileSaveCoordinator.GetCurrentOrder()` 过滤出成员后作为 `preferredOrder` 传入 `Capture`（Dashboard 导航前已保存过用户顺序），取不到时退回 ModService 加载顺序。 |
| 会话结束/取消后仍读取已清空的会话对象 | 结束类方法（如 `FinishAsync`）内部会清空会话（`Current = null`），总结弹窗、结果展示必须在调用结束方法之前捕获会话引用并传入，不能在之后从服务重新读取。 |
| 合并/删除翻译键后不做双向引用验证 | 删除键后必须验证：① 代码中无残留旧键引用（`rg` 旧键名）；② 反向提取代码里所有 `{loc:Loc ...}` 与 `_localizationService["..."]` 引用，逐一确认存在于 zh-CN 和 en-US（能暴露历史拼写错误，如 `NexusDownloadPage.PremiumRequiredMsg` 与 JSON 中的 `NexusDownload.PremiumRequiredMsg` 前缀不一致——本地化服务对缺失键可能静默返回空串，界面只显示空白不会报错）。修改代码引用时，键名必须与 JSON 完全一致，不能凭印象写近似键名。 |
| 以为拖拽期间滚轮消息会正常到达 WPF | OLE 拖拽循环会吞掉 WM_MOUSEWHEEL（WPF 收不到 PreviewMouseWheel）。拖拽中滚轮必须用 WH_MOUSE_LL 低级钩子，钩子直接装在 UI 线程即可（OLE 循环会泵消息，回调在 UI 线程执行）。钩子回调里处理完滚轮要**返回 1 吞掉消息**，不能让滚轮进入 OLE 循环（可能被当作按键状态变化导致拖拽被意外终止）。钩子回调必须 try/catch 且非滚轮消息原样 CallNextHookEx。 |
| 合成拖拽事件刷新插入指示线时用 PreviewDragOver | gong 在 ItemsControl 上默认 `EventType.Auto` 只监听**冒泡**的 `DragOver`（非 ItemsControl 才监听 Preview*）。合成指示线刷新必须 raise `DragDrop.DragOverEvent`（冒泡），PreviewDragOver 不会进入 gong 管线，指示线不会刷新。`DragEventArgs` 带坐标的构造函数是 internal，只能用反射创建（有测试守护签名）。 |
| 只依赖 CompositionTarget.Rendering 检测拖拽结束 | 应用空闲无渲染时 Rendering 会停发，Esc 取消/窗口外释放会留下僵尸状态和钩子。需要 DispatcherTimer 看门狗（300ms）轮询 `GetAsyncKeyState(VK_LBUTTON)` 兜底清理，停用最后一个状态时卸载钩子并取消渲染订阅。行为的所有入口（DragOver/Drop/渲染帧/钩子回调）都要 try/catch——滚动增强绝不允许破坏拖拽本身。 |
| 只向上查找 ScrollViewer | ListBox 的 ScrollViewer 是**视觉后代**（模板内），不是祖先。查找要祖先优先、找不到再递归后代。 |
| 用 SendInput 合成滚轮验证拖拽中滚轮滚动 | SendInput 的 MOUSEEVENTF_WHEEL 不携带真实按键状态，会清空全局异步按键状态（GetAsyncKeyState 返回抬起），导致 OLE QueryContinueDrag 看到 keys=0 提前结束拖拽——自动化测试的假象，真实鼠标滚轮自带 MK_LBUTTON。此类交互验证要区分真实输入与合成输入。 |
| 在 MSTest 里直接 ApplyTemplate 测试 WPF 控件 | MSTest 环境不加载 WPF 默认主题样式（控件的 Template/Style 为 null，新建 Application 也不行）。UI 测试需要手工构造显式 ControlTemplate（FrameworkElementFactory）来搭建视觉树，并用 Measure/Arrange 建立视觉父子链。 |
| 把需要 code-behind 访问的命名元素放进 Window.Style 的 ControlTemplate | 模板内的 `x:Name` 是模板作用域，Window 类不会生成对应字段（编译报 CS0103 "名称不存在"），用 `OnApplyTemplate` 里 `Template.FindName("name", this)` 获取引用。**不要把 Window.Content 改为 Grid 包裹 ContentControl/ContentPresenter 来容纳覆盖层**：`ContentControl.Content` 和显式设置 Content 的 `ContentPresenter` 都会把页面加为逻辑子（`SetLogicalChild`），而本项目的页面视图是 `Page` 类型，`Page.OnVisualParentChanged` 校验逻辑父必须是 Window/Frame，运行时报 XamlParseException "Page 只能具有 Window 或 Frame 父级"（启动即崩）。模板内裸 `<ContentPresenter>`（未设置 Content，隐式呈现 TemplatedParent.Content）不会触发该校验，这是原结构能正常工作的原因。 |
| 主窗口接收文件拖拽时直接挂在 Window 的 DragOver/Drop 上或逐页面防 gong | 文件拖拽（FileDrop）必须用 Window 层的 `PreviewDragOver`/`PreviewDrop`（隧道事件最先到达根）并在识别到文件时 `e.Handled = true`，否则 string[] 会被 gong 的 `DefaultDropHandler.CanAcceptData`（`data is IEnumerable && !(data is string)`）当成排序数据，Drop 时插入 ObservableCollection 抛类型异常。内部拖拽（ModViewModel 等）不是 FileDrop，不受影响。防御性上仍应在实现 `IDropTarget` 的 VM（Dashboard/DeploymentOrder）的 DragOver/Drop 开头识别 `string[]` 或含 FileDrop 的 IDataObject 直接 return。提示层显隐用"DragOver 持续刷新时间戳 + DragLeave 后 300ms 复查"避免子元素间移动时闪烁。 |
| 启动黑闪（LOGO 透明区透出黑底）只查闪屏图片 alpha | WPF 默认 `<SplashScreen>` 项在 `CompositionTarget.Rendering`（**帧渲染前**触发）第一次时就关闭闪屏，此时主窗口首帧还没提交给 DWM，DWM 侧主窗口区域是纯黑的；闪屏 LOGO 透明，黑底就从透明区透出"一闪"。修复：csproj 移除 SplashScreen 项（图片改 `<Resource>`），自实现透明闪屏窗口（`AllowsTransparency` + `Topmost`），在 `MainWindow.ContentRendered`（首帧真正渲染完成后）再 `Close()`。验证：启动进程 + `CopyFromScreen` 连续截屏统计中央区域纯黑帧比例（采样间隔 ≤20ms），修复后应全程为 0。 |

这些提醒不能替代测试；它们的作用是避免沿着已知错误方向继续实现。

## 9. 验证命令

在仓库根目录执行：

```powershell
# 构建解决方案
dotnet build Helldivers2ModManager.sln --configuration Release

# 运行主测试项目
dotnet test Helldivers2ModManager.Tests/Helldivers2ModManager.Tests.csproj --configuration Release

# 主程序发布
dotnet publish Helldivers2ModManager/Helldivers2ModManager.csproj `
  --configuration Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o publish

# 独立工具发布时使用各自项目文件和输出目录
dotnet publish Purger/Purger.csproj --configuration Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o publish
```

行为敏感的修改还应按场景补充验证：

- 部署/修复：比较源文件和备份哈希，验证失败回滚。
- Patch/资源解析：使用真实样例，验证正常、截断、越界和未知结构。
- 预览：验证模型几何、材质匹配、纹理通道、取消和旧结果不会回写。
- UI/本地化：验证页面导航、DataTemplate、语言切换和共享样式。

完成后再次执行 `git status --short`，确认没有临时文件、发布目录或无关改动被留下。

