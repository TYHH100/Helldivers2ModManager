# Helldivers2ModManager 开发指引

只记录长期约定和已验证的易错点。具体字段、格式、阈值以当前源码、测试和 Schema 为准；新增提醒用“触发条件 → 正确做法”，合并到对应章节，不追加长篇排障记录。

## 1. 工作原则

- 中文回复，简洁沟通，不过度审计。Git 提交和推送说明遵守 Conventional Commits。
- Windows / WPF / .NET 10，默认 PowerShell。先看真实代码、调用边界和样例，再判断或修改。
- 修改前后检查 `git status --short`，保留用户已有改动；文本编辑用 `apply_patch`，禁止用重置或覆盖清理用户改动。
- 解析、部署、修复先校验路径、范围、哈希和备份；用户 Mod、游戏资源及样例默认只读，不提交到仓库。
- 删除前确认最终绝对路径在目标目录内，优先可恢复操作。测试副本和临时产物用完清理。
- 必要时才联网，优先可用的 AnySearch 技能；引用外部资料时提供来源链接，保留代码中的出处声明。
- 移除功能须清理设置、菜单、服务、资源和文档的完整链路；共享服务、样式和解析器变更须检查调用方。

## 2. 项目与架构

- 解决方案：`Helldivers2ModManager.sln`。应用项目在 `src/`，MSTest 在 `tests/`，Schema 与资料在 `docs/`；`archive/` 是未跟踪的历史归档。
- 主程序 `Helldivers2ModManager` 承载领域、服务与 UI；主程序、测试、`Purger` 使用 `net10.0-windows`。
- `Helldivers2PatchTool` 使用 `net10.0-windows7.0`，引用主程序时经 `SetTargetFramework` 固定为自包含、win-x64；不要破坏此配置导致 NETSDK1150。
- 依赖：前端 → 领域服务 → 基础设施/持久化。`Services`、`Models` 不得引用 `ViewModels`；`Services/Infrastructure/` 下的 Settings/Localization 服务不得依赖 UI（独立工具也使用）。
- 领域入口：`Services/{VersionCheck,Parsing,ModManagement,ModelPreview,Search}/`；设施：`Services/{Persistence,Infrastructure,Nexus}/`；UI：`Views/`、`ViewModels/`、`Components/`、`Stores/`、`Resources/`。
- `App.xaml.cs` 扫描 `[RegisterService]` 注册服务。默认类型为 `internal`；共享状态用 Singleton，页面 VM 用 Transient；接口与实现共用实例时用 `Contract`。
- 新页面必须齐备 VM 注册、`MainWindow.xaml` 的 DataTemplate 和 View。导航走 `NavigationStore.Navigate<T>` 的独立 scope，禁止根容器直接解析页面 VM 导致退出后仍持有资源。

## 3. WPF、异步与本地化

- VM 使用已有基类/ObservableObject 与 CommunityToolkit；业务逻辑在 VM/Service。大型类按流水线拆 partial，功能写入对应文件。
- 公共样式放 `Resources/Styles/`，引用前核对资源键。改 XAML 后检查 code-behind 的旧名称、模板、导航入口及深色主题。
- Jalium.UI 列表显示对象类型名而非卡片时 → 核查当前包的 `ItemTemplateSelector` 实机行为；已验证 `ItemTemplate` 可渲染，模板测试之外还要用真实窗口截图确认。
- Jalium.UI 列表的 `Items.Groups` 有数据却不显示组标题时 → 用虚拟化 ListBox 中的显式标题行，并用真实窗口截图核对；当前包即使把 `GroupDescriptions` 配在 `ListBox.Items` 上，也可能只渲染平铺条目。
- Jalium.UI ListBox 的条目只占左侧窄列或默认内边距过大时 → 为 ListBoxItem 设置零内边距、横向拉伸的 ContentPresenter 模板；需要选择的列表再补选中态 Trigger，并用窗口截图核对文字与元数据不重叠。
- Jalium.UI 中部分 Segoe Fluent Icons/MDL2 私用区字符显示为方框时 → 先用真实窗口确认，再对受影响按钮选用 Segoe UI Symbol 中的通用符号，并保留本地化 ToolTip。
- Jalium 主窗口新增覆盖层时 → 保持原版从下到上的图片预览、版本诊断、冲突明细、消息框、引导、Toast 顺序；确认框必须能盖住发起它的诊断覆盖层，并测试取消会解除等待。
- Jalium.UI 窗口出现双层标题栏时 → 自定义标题栏配合 `IsShowTitleBar = false`，运行窗口确认后再验收。
- Jalium.UI 选图时 `Jalium.UI.Controls.OpenFileDialog` 不可访问 → 用当前包公开的 `Microsoft.Win32.OpenFileDialog`；不要为此引入 WinForms 隐式引用造成控件名冲突。
- Jalium.UI `Viewport3D` 只完成场景树/命中测试而未接入当前桌面绘制链时 → 不把空白视口当作已完成；隔离 Host 使用 `HwndHost` 承载原生渲染面，并用帧缓冲像素、鼠标旋转/滚轮和贴图探针验证。
- Jalium 迁移要求完全原生时 → 页面和覆盖层使用 Jalium 控件或原生 `HwndHost`，禁止引入 WebView、浏览器内核或 WebView 依赖；外部网页只通过系统默认浏览器打开。
- Jalium 页面含万级动画条目时 → 保留只物化可视行的选择器；不要直接用 `ComboBox.ItemsSource`，并验证搜索、滚动和源索引回传。
- Jalium Core 链接原版解析服务时缺类型或运行时名称表 → 同步链接对应非 UI 模型，并在 Host 显式复制所需 `Resources/Data`；构建成功后还要检查输出目录中的数据文件。
- Jalium 音乐播放器收到 `BackgroundMusicService` 的播放/曲目事件时 → 通过创建窗口时捕获的 Jalium `Dispatcher` 更新控件；音频停止回调可能来自后台线程，不能直接改 UI。
- WPF 集合和绑定属性只在 UI 线程更新。大列表先构造普通 List 再整体替换，成员判断用 HashSet；音频/文本列表用虚拟化 ListBox + ListCollectionView，分组时也启用虚拟化。
- 动画选择必须保留 `VirtualizedTextPicker`：只物化可视行、回传源索引、按偏移定位选中项。不要换回 ComboBox 或修改全局 FluentComboBox（BringIntoView 会线性生成大量容器）。
- 耗时业务统一用 `BackgroundTaskService.RunAsync`；work 内只做后台计算，通过 `BackgroundTaskContext.Report` 更新进度，结果回 UI 应用。`await` 和单独 Add/Update 不会把 CPU 工作移出 UI 线程。
- 有专属进度弹窗的任务用 `isForeground: true`；静默任务用默认后台模式。保留任务注册和弹窗步骤；终态 Complete/Fail/Cancel 必须经 `QueueOnUiThread` 排队，避免抢在步骤更新前完成。
- 有总量用 0..1 进度，否则 IsIndeterminate。正常取消不当作故障；切换请求/退出时取消旧任务，并用代次检查防止旧结果回写。
- TCS 桥接 `MessageBoxSelectionMessage` 必须提供 Abort 回调，否则点击取消后任务挂起；会话结束会清空状态时，先捕获总结所需对象。
- 本地化：XAML 用 LocExtension，代码用注入的 LocalizationService；新增 `Section.Key` 同步写入 `Resources/Language/{zh-CN,en-US}.json`。删改键后检查旧引用和双语缺失键，禁止硬编码用户文本。

## 4. 设置、配置与导入

### 设置

- 新设置须同时完成：字段默认值与 Guard 属性；`CreateJsonModel / ReadAsyncFallback / ResetInternal`；SettingsPageViewModel 双向属性及 Update 通知；设置页卡片；双语资源。缺字段时回落默认值。
- Jalium 设置页在未保存编辑期间运行重算哈希或强制清理时 → 从已保存的设置文件读取游戏和存储路径；不要直接用编辑中的 SettingsService 路径执行文件操作。
- `settings.json` 在程序目录；读取的 FileStream/JsonDocument 必须 using 释放。路径设置校验必要游戏文件；部署默认复制，符号链接仅在用户开启且权限满足时使用。
- 音乐播放器：EnableMusicPlayer 控制启用/显示（默认 true），AutoPlayBackgroundMusic 只控制启动播放（默认 false）；启用时确保 Music 目录存在。
- 播放模式为 Sequential（末尾停止）、Loop、Shuffle（多首时不连续重复）；模式、音量、曲目合并保存，退出强制落盘。曲目只存 Music 下相对路径，缺失回退第一首，空库清空。

### 配置与排序

- `ModGuids` 是分组成员权威，默认组也不是天然包含全部模组。默认组排序来自 `_mods / enabled_mods.SortOrder`；非默认组按 ModGuids 顺序输出，禁止兜底追加外部成员。
- 非默认组部署序随显示序。非 Dashboard 保存时用 `ProfileSaveCoordinator.GetCurrentOrder()` 过滤成员作为 Capture 的 preferredOrder，取不到才回退 ModService 顺序。
- 二分排查在 Jalium 等独立 UI 中使用不同的 ModData 实例时 → 部署快照从会话的 AllMods 构建；开始切到临时组前记录原分组顺序，继续排查前重新启用尚未确认的候选并保存临时组状态。
- 导入自动入组（默认关闭）只在 `AddFilesCoreAsync` 本轮导入期间临时订阅 ModAdded，finally 解绑并加入本轮新增 GUID；不要放入通用 ModAdded 处理器（刷新库也会触发）。
- 自动入组后重新请求配置保存；导入结束用 `SaveProfileNowAsync(showProgress: false)` 立即落盘，不能只依赖防抖或退出保存。自动入组失败记 Warning，不阻断导入，提示用 Toast。
- 当前配置存 `app_state.last_selected_group_id`：初始化恢复，切换/删除当前组时立即保存，缺失或失效回退默认组。会话状态复用 app_state，不塞设置页、不在全量重插的 mod_groups 上加列。

### 文件操作

- 保持 Legacy/V1 清单兼容，以 `docs/mod_manifest_v1-schema.json` 为准。V1 的 `Options: []` 与无选项均回退根目录补丁，区别于“关闭”占位选项。
- 部署输入来自 Dashboard/Profile；预览选项和扫描结果不得覆盖部署选择。改清单/选项/部署须验证 manifest、备份和恢复。
- 批量复制用有限并发 + `File.Copy`，保留符号链接分支；避免无界 Task.WhenAll 和无界哈希。
- 嵌套解压的共享锁可能来自杀毒扫描。仅对 0x80070020/0x80070021 有界重试；密码、CRC、缺文件等保持原失败语义。

## 5. Patch 解析与修复

- 以 VersionCheckService、PatchResourceInspectionService 为准。补丁族为 `{16位hex}.patch_{索引}` 及同名 `.gpu_resources / .stream`。
- GPU 不得整体读入内存；大型文件有界随机读取。偏移/长度用 long/ulong，读前验证溢出、范围、对齐和声明尺寸，同时限制单文件与合并后总 Mesh/顶点/索引、并发和缓存。
- 明确偏移基址：MeshInfo 材料/Section 偏移相对 MeshInfo，GPU 偏移叠加正确 Unit/Stream 基址。诊断输出用相对路径，避免多选项同名文件混淆。
- Legacy 类型表允许数量为 0 的空槽，按类型值双向比较，不能只比较字典数量。大小差异需区分截断和合法填充。
- “0 个 GPU Stream”先查 Unit 版本门槛；版本 1 走旧顶点格式。当前 Unit 版本从同 File ID 的游戏引用读取，不能全部硬编码成 10800438。
- 未知结构仅警告，不猜测损坏或修复。已知可用 Mod 先排除检测器误报、检查 `.hd2mm-backup.json`；修复先保存备份元数据，失败恢复并记录原因。
- 批量修复只处理明确支持的类型；音频等不支持类型必须跳过。版本检查、冲突、护甲关系扫描保持独立，关系证据来自已启用 Mod 的真实 Patch。
- 已验证旧角色材质迁移沿用现有签名和测试：只改父模板时不臆造结构；发生 0x54AE→0x8F66 迁移的 patch，符合条件的全部旧 Unit 统一使用当前游戏 LOD，保留 Mod GPU/纹理（见 RequiresCurrentGameLodForLegacyCharacterPack）。
- 自定义角色按 CustomizationSlot 分组并结合 Mesh 签名保留 Mod LOD，不能仅看单 Mesh ID/GPU 大小；验证 Slim/Stocky 和实际玩家动态场景。

## 6. 模型、动画与材质预览

- 按真实 MeshInfo/Section/Transform 解码；失败可观测、可测试，不用整 Stream 回退或球体/方盒猜测掩盖错误。稀疏 section 先压缩实际引用顶点再算容量。
- 护甲按显示名合并本体/头盔 archive，过滤用 `option.Ids`，未知/共享网格保留；默认第一个具体套装，无套装才回退“全部”。名称表变更同时检查 ArmorReuseService 和 ModelPreviewBackend。
- 加载后 `SelectedAnimation = null`，保持绑定姿态；仅手动播放/拖进度应用动画，不自动挑 idle/呼吸片段。
- 朝向使用全量网格：可信 torso/legs 质心差优先，不显著则回退顶点标准差与 Z 轴先验；不依赖 bounds，不恢复 torso/remainder 反转或无标注 X 翻转。改规则重跑 RealLibraryOrientationScan、FullLibraryOrientationScan，并人工审查人物与武器/载具/VFX。
- 源骨架可用时采用世界差量 `S⁻¹·A`，无源骨架才退回局部差量。根骨骼保持绑定姿态，additive 以 rest 叠加；兼容性判定统一改 ModelPreviewAnimationCompatibility。
- 无 BoneIndex/BoneWeight 或全部权重无效时，可用合法 TransformIndex 做单骨全权重挂接；不能以 palette 非空判定顶点流有蒙皮字段，也不能因 palette 空丢弃骨架层级。
- 游戏动画全量登记、clip 惰性解码；消费者用 `ResolveClip()`，只能后台调用，失败也缓存；UI 只读 `CachedLengthSeconds`。骨数不兼容仍保留列表项，不可播放时返回 null；不要恢复 256 条截断或直接读 Option.Clip。
- 动画名称英文打底、中文覆盖，中文 Unknown 不覆盖；名称表键为 16 位 hex，十进制转换用脚本，校验源 ID、跳过元数据、重复 ID 取先到者。
- 纹理保留 RGB/RGBA/A 模式，不默认用 Alpha 作不透明度。材质变体按 `(MeshInfoIndex, VertexOffset, VertexCount, IndexCount)` 去重，不能包含 IndexOffset；优先高分辨率 Albedo。
- 纯黑占位须多点采样并解码验证，不能只读头部；仅在同 stream 有正常 section 时剔除。缺失贴图灰模回退，不猜测材质内容。
- 流光仍参与 BaseColor 回退；Alpha 非均匀时不当流光强度，使用静态高光，禁止动态扫光。纯 Emissive 材质压暗 Diffuse 并使用自发光；动画画刷在 UI 线程创建、不冻结。
- 材质缓存键包含组合形态；手动单贴图模式不叠加高光/发光/动画。相关回归见 ModelPreviewArmorSelection、AnimationBinding、AnimationOption、AttachedSkinning、MaterialTexture、MaterialVariantSelector 测试。

## 7. Patch 资源查看器

- 音频、字幕、Lua 位于 Patch 资源查看器；模型页负责几何、材质、动画，并可携带当前模组跳转。UI 在 `PatchResourceViewerPageViewModel.{Loading,Audio,Text,Lua}.cs`。
- TOC/纹理/音频/字幕/脚本及提取统一用临时选项选出的 patch 集合；切换、刷新、退出取消旧加载，停止播放同时使未完成试听失效。

### 音频与字幕

- hd2-audio-modder 为 ARR：只参考公开结构/常量，禁止复制源码，保留 README 和代码出处。
- 音频检查只读 TOC、chunk 头、DIDX 与有界 WEM 头探针；媒体按需切片，不整包加载。Bank 有 16 字节前缀，TEXT_BANK 没有；具体布局看 AudioBankInspectionService / TextBankFormat。
- WEM 转换 AoTuV 优先、Default 兜底，并以 VorbisReader 构造校验，不能只看转换未抛异常。截断预取媒体禁止播放；dep 关联失败回退 Bank ID，单 bank 的 stream 合并显示。
- V1 多选项且主类型为 Audio 的模组直接跳过音频预览（用户决定），保留提示。
- GameAudioBaseline 只缓存音频 TOC 与 SHA-256，不缓存媒体字节；先比尺寸再流式哈希，有条数/字节预算，超预算标未知。句柄缓存逐出时释放，不可播放条目不比对。
- 字幕基线惰性解析，支持空库；资源缺失标新增，不能假设字符串 ID 连续。回归：AudioBankInspectionServiceTests、TextBankInspectionServiceTests、PatchResourceViewerLifecycleTests。

### Lua

- 全程静态 bytes→结构→文本，禁止加载、编译、执行或求值脚本，不引入 Lua VM。
- 子原型先于父原型，KGC CHILD 按栈关联；KNUM 的 33 位 ULEB 不得误用于 KGC/KTAB；比较指令成立才执行紧跟的 JMP，还原 if 注意反转条件。
- 递归检查字符串中的嵌套 dump；保留 CALL 副作用，不能验证的原型回退权威指令清单，子闭包失败用标注占位。
- 提取保留原始载荷、字节码、还原源码、指令清单、字符串及报告，只写文件不执行。改解析规则跑 LuaScriptInspectionServiceTests，并与 ljd 输出人工对照。

## 8. 其他易错点

- OLE 拖拽滚轮走 UI 线程 WH_MOUSE_LL，处理后吞掉消息；用 DispatcherTimer 看门狗清理钩子，不只依赖 Rendering。合成 gong 排序刷新用冒泡 DragOver；SendInput 滚轮可能改变按键状态，不能当作真实拖拽等价验证。
- 文件导入在 Window 的 PreviewDragOver/PreviewDrop 拦截 FileDrop；排序 VM 防御性排除 string[]/FileDrop，避免 gong 当作可排序项。ScrollViewer 查找兼顾视觉后代。
- ControlTemplate 内命名元素用 OnApplyTemplate/Template.FindName；不要用 Window.Content 的 Grid/ContentControl 包裹 Page，避免破坏 Window/Frame 父级要求。
- 闪屏在 MainWindow.ContentRendered 后关闭，Rendering 是帧提交前事件，可能引起黑闪。
- 拼音 API 位于 ToolGood.Words.Pinyin，结果转小写；词库首次加载在后台预热，名称匹配缓存不要在输入热路径反复构建。
- 使用 ILogger<T>；主程序按 AutoCleanLogs/MaxLogFiles 清理日志。PatchTool 保留独立 FileLogger（不依赖 App.Current），Debug 起记，程序目录 logs 下最多保留 5 个日志。

## 9. 验证与交付


```powershell
dotnet build Helldivers2ModManager.sln --configuration Debug /m:1
dotnet test tests/Helldivers2ModManager.Tests/Helldivers2ModManager.Tests.csproj --configuration Debug
```

- 共享 WPF 输出串行构建/测试。主程序或调试器锁定 bin 时不要关闭用户进程，使用 `--artifacts-path <专用系统临时目录>` 隔离产物，验证路径后清理。
- 测试调用 `ModService.Init` 后要释放数据库或清理隔离目录时 → 先等待 `HashMigrationTask`，避免后台哈希迁移仍占用 SQLite 文件。
- 修改后首次验证不要用 --no-build。依赖源码/夹具的测试用 CallerFilePath 定位仓库，不能假设测试当前目录位于仓库；修改全局当前目录的测试标记 DoNotParallelize。
- WPF 测试显式构造 ControlTemplate 并 Measure/Arrange；无头测试通过不代表视觉验收。CS2001 缺生成文件先排除并行构建/旧 wpftmp，仅清理已核实的相关生成目录，不清成功产物。
- 按改动范围验证：解析的边界/截断/未知格式；部署修复的哈希/备份/回滚；预览的取消/缓存/旧结果；UI 的导航/双语/样式。无需每次全库审计。
- 发布使用对应项目及独立输出目录，Release / win-x64 / self-contained / PublishSingleFile；修改 PatchTool 发布配置须同时验证 CLI 和 VS Publish Profile。
- 完成后检查工作树和临时文件，说明验证结果及未完成的人工验收。

