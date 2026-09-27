# Jalium.UI 迁移进度

此目录基于当前 `test` 分支迁移现有程序。完成全部页面、命令、业务流程和人工视觉验收前，不替换正式 WPF 入口。Jalium Host 全部页面和覆盖层使用原生 Jalium 控件及原生渲染面，不引入 WebView 或浏览器内核；外部链接仅交给系统默认浏览器。

| 范围 | 状态 | 验证 |
| --- | --- | --- |
| Legacy / V1 清单解析、序列化及启动解析缓存 | 原源码链接进独立 Core | 原清单版本、GUID、描述、真实样例及缓存测试复用 |
| Dashboard 模组搜索匹配 | 原源码链接进独立 Core | 原拼音、大小写、混合查询测试复用 |
| 模组状态快照与部署顺序 | 原源码链接进独立 Core | 原启用状态、排序、PhysBone 顺序测试复用 |
| 设置、本地化与部署顺序编辑器 | 原设置和语言服务接入 Core；编辑状态可持久化与回退 | 原设置及语言测试、隔离路径和顺序操作测试 |
| Dashboard 模组目录和配置加载 | 原清单模型与 SQLite 仓储接入 Core；目录解析、路径检查、重复 GUID、解析缓存、启用状态和排序已接通 | 隔离目录加载、缓存失效、旧 enabled.json 迁移及 SQLite 回归测试 |
| Dashboard 配置分组 | 原分组服务与仓储接入 Core；WPF ViewModel 过滤移至 UI 层 | 自定义组成员顺序及重启后恢复所选分组测试 |
| 原窗口 | 隔离 Host 已能启动；单层标题栏、尺寸、导航容器、背景图、文件拖放导入、浮动音乐播放器和 12 步首次使用引导已接；图片预览、版本诊断、冲突明细、消息框、引导和 Toast 均占用原版对应图层。普通提示、错误、确认、导入密码、带长度/校验的文本输入、单选、带预选与失败重试的多选、带标题/描述的清单选择，以及导入、部署、批量修复、二分部署、导出和更新进度已用原生 Jalium 共享覆盖层；业务页面仍有少量文件选择器和视觉验收待办 | 窗口图层、引导、图片预览、取消删除、密码输入、文本校验、单/多选队列与取消、清单标题/描述、进度更新测试；图片预览、确认框、密码框、文本输入和单/多选/清单/进度真实窗口截图已核对（`qa/text-input-prompt.png`、`qa/message-multiselect.png`、`qa/message-single-select.png`、`qa/message-checklist.png`、`qa/message-progress.png`），后续高亮步骤、拖放、播放器、文件选择器和完整视觉验收待办 |
| Dashboard | 真实模组库列表、搜索、选择、启用状态、旧版选项和配置分组已接线；多配置文件选择可添加或重设成员；卡片拖放与置顶/置底/指定位置排序、Ctrl+A 全选、Esc 取消选择、Ctrl/Shift 卡片多选与范围追加及 Steam 启动入口已接入；模组图标后台最多 4 路解码，列表缩略图限制 128px，点击预览和悬停提示按需解码到 1024px，缺图回退默认图标；批量标签和配置组选择已迁入共享覆盖层，保留预选、保存失败时留窗重试和标签色块；卡片操作菜单已接预览、目录、标签、配置移除、名称/描述/图片/链接/清单编辑、导出、更新、文件删除；批量删除在配置视图移除成员，在模组库视图删除文件并清理关联数据；分隔线支持新建、改名、改色和删除；导入、导出、更新、部署、清理、批量修复和二分部署均接入原服务；GitHub 单图标菜单保留原仓库和分支仓库入口；冲突扫描支持配置变更、缓存恢复、卡片标记与明细覆盖层 | Host 快捷键、卡片选择、批量移除/删除、运行时扫描、缓存、卡片状态、分隔线排序、消息多选取消/提交失败重试及删除测试；导入、部署、批量修复、二分部署、导出/更新均使用共享 Jalium 进度覆盖层并保留任务中心进度，文件选择器、拖放、卡片菜单和完整视觉验收待办 |
| 创建模组页 | 已接原页面的名称、描述、图标、源目录、V1/Legacy、选项与子选项编辑、Include 目录选择和排序；外部图片经临时副本导入，源目录保持只读；源目录选择使用原生 Jalium 目录浏览器，支持上级目录和新建文件夹；成功后加入默认配置并刷新模组库 | V1/Legacy 创建及重启后配置成员测试 3/3、Host 导航测试、真实窗口表单和自动选项检查；完整视觉与图片/拖拽交互人工验收待办 |
| 部署顺序页 | 已有页面和真实编辑器，Host 已接导航；仍需窗口交互及视觉核对 | Host 布局与双语测试，Core 操作测试 |
| 设置页 | 已接主窗口导航；路径、部署、模组、日志、工具、主页与外观七组设置可编辑，保存/取消后重新加载模组库与配置分组；游戏、存储和临时目录选择使用原生 Jalium 目录浏览器，支持上级目录和新建文件夹；自定义背景与卡片透明度已接渲染。工具页已接 AI 翻译配置、重放引导、重算哈希、强制清理补丁与确认后重置设置；工具使用已保存路径，避免未保存编辑误指向清理目标 | Host 工具控件与已保存路径测试、Core 清理范围/取消测试、路径/主页/外观页实机检查；工具实机交互与完整视觉验收待办 |
| 自动标签配对页 | 从设置主页可进入；已有标签、新建标签、映射保存和返回已接通；检测结果已接 Dashboard 自动打标签和标签持久化 | 配对编辑测试、设置重载兼容测试、Dashboard 保存/取消/失败回退测试；页面导航与布局实机检查 |
| 标签管理页与 Dashboard 批量标签 | 创建、改名、改色、删除、返回和批量设置已接通；自定义分组只更新全局标签字段 | 标签设置持久化、只读/失败回退、自定义分组和模组库回归；页面和弹窗实机检查，批量交互仍待实机验收 |
| 编辑页 AI 翻译 | 复用原 AiTranslationService，支持本地缓存、批处理、进度、服务端用量及选项/子选项译文显示；选项图片点击打开原生预览 | AiTranslationService 测试；真实 API 交互需配置用户自己的端点和密钥，图片点击待人工验收 |
| 帮助页 | 标题栏帮助入口已接真实页面、返回和外部链接；原 WPF 引用的 Help.md 在当前仓库不存在，Jalium 使用双语资源提供操作内容 | Host 导航测试；完整视觉验收待办 |
| 后台任务中心 | 原任务模型与生命周期服务已接 Core，Jalium 页面支持进度、步骤、单项移除和清除完成；其他工作流的任务生产者待接入 | 后台派发顺序和前台任务过滤测试；空状态、按钮和返回实机检查 |
| 二分排查页 | Dashboard 入口、原页面三段布局、临时分组、逐轮部署和报告、单候选复验、剩余候选再验证、放弃恢复与完成总结已接入；使用原 BisectService 和 ModService。报告单选、开始/中止确认及结果提示使用共享 Jalium 消息覆盖层 | Core 二分逻辑与隔离目录真实补丁部署、原分组恢复及嫌疑禁用落盘测试；单选覆盖层真实窗口截图 `qa/message-single-select.png`；真实游戏交互和窗口视觉验收待办 |
| 护甲复用页 | Dashboard 入口已接；原版四项统计、结果列表、空状态、重新扫描和后台任务生命周期已迁移；扫描直接复用原 ArmorReuseService、VersionCheckService 和 Patch 解析源码，离开页面取消未完成扫描 | Host 结果/双语/取消测试；真实游戏扫描和视觉验收待办 |
| Patch 资源查看器 | Dashboard 入口、左侧模组列表与 TOC/GPU/纹理/音频/字幕/Lua 六类内容已接；音频保留多选项纯音频模组跳过规则及按需播放，Lua 支持报告复制和静态提取，Lua 导出目录使用原生 Jalium 目录浏览器；各类内容读取当前部署选项所选 Patch，切换和退出取消加载及播放。音频/字幕用虚拟化列表中的显式组标题行恢复原布局；纹理点击放大、滚轮缩放、拖动、重置与关闭已接入 | 合成数据的真实窗口截图已确认分组标题、音频按钮和纹理放大层，鼠标交互、真实 Patch 播放/提取、列表性能及完整视觉验收待办 |
| Nexus 下载页 | Dashboard 原下载入口已接；链接解析、API Key 加密保存、模组信息与主文件选择、下载、复用原导入/脚本确认流程、结果提示、隔离临时文件清理和返回已接通；换链接后旧图片不会回写，改 Key 后查询会同步新 Key | Core 假服务 URL/下载/清理测试 3/3、Host 导入链与图片竞态测试；合成数据真实窗口截图已检查布局及选中态，真实 API/Premium 下载与人工验收待办 |
| 版本检查与批量修复 | 原 VersionCheckService 和 SQLite 仓储已接入；手动全量检查、启动时按模组/游戏变更自动检查、卡片状态点、按需单模组诊断已接；原生 Jalium 详情覆盖层已接状态统计、问题、报告、修复与备份入口。LOD 修复策略只显示可执行方案；逐 Unit 清单保留名称、Patch 数、LOD 尺寸与 File ID 说明，使用原生共享覆盖层。仍需验证真实补丁的修复/回滚、取消行为和完整视觉布局 | Host 版本扫描、诊断问题分组/数量、回滚预估文件数测试；合成诊断截图 `qa/version-detail.png` 与清单覆盖层截图 `qa/message-checklist.png` 已核对；真实补丁修复/回滚及人工视觉验收待办 |
| 其余页面 | 版本诊断原覆盖层的高级修复、备份回滚和完整技术报告已接入原生控件，但流程与布局仍待对照验证 | 对照现有 XAML、VM、命令和人工视觉验收 |
| 模型预览 | 原三栏布局、选项/体型/护甲/网格筛选、材质贴图、地面网格、相机操作、动画选择/时间轴/恢复绑定姿态已迁移；Jalium 包的 `Viewport3D` 当前只提供场景树，视口改用隔离的原生 `HwndHost` OpenGL surface，仍保持 Jalium 页面布局 | 合成模型帧缓冲、贴图、旋转/缩放、动画蒙皮与 10,000 条虚拟化动画列表探针通过；真实 Patch、完整材质变体、人工视觉验收待办 |
| 其余领域服务与持久化 | 版本检查、游戏 Unit 索引、Patch/音频/字幕/Lua、Nexus 服务及 ModelPreviewBackend 源码已接入 Core；诊断详情的流程和布局仍待验收 | Host 43/43；Core 115/115；主解决方案构建通过 |
| Zig 计算热点 | 大纹理 Alpha 通道转换已接 Zig 0.13.0 DLL，4,194,304 像素起启用；RGB/RGBA 继续使用 C#，缺 DLL 时自动回退。模型预览阶段测量显示法线计算每模型 5.66-7.45 ms，未选为目标 | 随机像素 RGB/RGBA/Alpha 等价与输入不变性测试通过；两轮 Release 基准在 4096² Alpha 下 C# 39.15/41.44 ms、Zig 23.70/24.25 ms，均分配 64 MiB；真实 Patch 和人工视觉验收待办 |

当前 Core 不引用 WPF 或原主程序项目。它直接编译原业务源码；新目录解析器沿用原清单和缓存格式，并按当前 ModService 的路径规则校验。启动扫描时仍会删除缺失 manifest.json 的模组子目录，删除前额外核对其父目录和重解析点。原程序继续作为行为和布局基线。Host 使用 `%LOCALAPPDATA%/Helldivers2ModManagerJalium/` 保存独立设置和数据；未迁移的命令会明确提示，不会执行。这个隔离 Host 不能替代原程序。导入、部署和清理已复用原 `ModService` 流水线；其余业务迁移仍需保留路径校验、任务步骤、备份与回滚行为。

当前 `Jalium.UI.Interop 26.10.9` 包含可选的 `WebView2Loader.dll` 与 `jalium.native.browser.dll`。Host 在 NuGet 资产和包自带的复制目标中排除浏览器组件，构建和发布还会检查残留。Debug 构建、Release 发布目录及其 `.deps.json` 均无浏览器组件，原生窗口截图已验证正常渲染。

## UI 对照范围

以原 `MainWindow.xaml` 的 14 个 DataTemplate 为基线：Dashboard、部署顺序、设置、编辑、清单编辑、创建、标签管理、自动标签配对、Nexus 下载、后台任务、护甲复用、Patch 资源查看器、模型预览和二分排查。每个页面迁移时须核对原 XAML 布局、对应 ViewModel 的命令及状态、导航入口、双语资源和失败/取消行为。主窗口的标题栏、背景、覆盖层、音乐播放器及拖拽导入也属于迁移范围。未实现的页面不能用占位视图代替并计作完成。

Jalium.UI 的控件和 JALXAML API 以 [官方仓库](https://github.com/VeryJokerJal/Jalium.UI) 为准；Zig 工具链及 ABI 以 [官方文档](https://ziglang.org/documentation/) 为准。

验证命令：

```powershell
dotnet test next/jalium/tests/Helldivers2ModManager.Jalium.Tests/Helldivers2ModManager.Jalium.Tests.csproj --configuration Debug
next/jalium/native/build.ps1 -ZigExecutable <zig-0.13.0.exe 的绝对路径>
dotnet test next/jalium/tests/Helldivers2ModManager.Jalium.Host.Tests/Helldivers2ModManager.Jalium.Host.Tests.csproj --configuration Debug
dotnet run --configuration Release --project next/jalium/benchmarks/Helldivers2ModManager.Jalium.Benchmarks.csproj -- --texture
dotnet run --configuration Release --project next/jalium/benchmarks/Helldivers2ModManager.Jalium.Benchmarks.csproj -- <patch-file>
```

性能基线使用仓库 `Test/Mods` 中 VRC_Tell 的 `9ba626afa44a3aa3.patch_43` 与 816.7 MiB GPU 伴生文件；程序只读取样例。三次独立进程测量用于初筛，不代表其他模组或游戏数据的耗时。
