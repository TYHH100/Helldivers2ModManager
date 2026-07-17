
# Helldivers2ModManager - 开发和维护指南

本文档记录了项目的所有重要规范、架构说明、开发要点和维护指导。

---

## 1. 项目概述

### 1.1 项目简介
Helldivers2ModManager 是一个用于 Helldivers 2 游戏的模组管理器，采用 WPF 和 .NET 8.0 开发。

### 1.2 技术栈
- **框架**: .NET 8.0 Windows (WPF)
- **依赖注入**: Microsoft.Extensions.Hosting 10.0.8 / Microsoft.Extensions.DependencyInjection
- **MVVM**: CommunityToolkit.Mvvm 8.4.2
- **日志**: Microsoft.Extensions.Logging
- **压缩**: SharpSevenZip 2.0.77（基于原生 7z.dll，支持大字典 LZMA）
- **拖拽**: gong-wpf-dragdrop 4.0.0
- **数据库**: Microsoft.Data.Sqlite 9.0.8
- **缓存**: Microsoft.Extensions.Caching.Memory 10.0.8
- **通用工具**: CommunityToolkit.Common 8.4.2

### 1.3 当前版本
- 版本: 2.0.0

---

## 2. 项目架构

### 2.1 目录结构
```
Helldivers2ModManager/
├── .github/                        # GitHub Actions
│   ├── workflows/ci.yml
│   └── workflows/build.yml
├── hd2mmt_nexus-download-interceptor/  # 浏览器扩展
│   ├── _locales/zh/messages.json
│   ├── icons/
│   │   ├── icon16.png
│   │   ├── icon32.png
│   │   ├── icon48.png
│   │   └── icon128.png
│   ├── background.js
│   ├── generate-icons.js
│   ├── manifest.json
│   ├── popup.html
│   ├── popup.js
│   ├── LICENSE
│   └── README.md
├── Helldivers2ModManager/          # 主应用程序
│   ├── Components/                 # UI组件
│   │   ├── MessageBox.xaml
│   │   └── MessageBox.xaml.cs
│   ├── Exceptions/                 # 自定义异常
│   │   ├── EndOfLifeException.cs
│   │   ├── UnknownManifestVersionException.cs
│   │   └── Nexus/                  # Nexus Mods API异常
│   │       ├── NexusApiException.cs
│   │       └── NexusPremiumRequiredException.cs
│   ├── Extensions/                 # 扩展方法
│   │   ├── IOExtensions.cs
│   │   ├── JsonElementExtensions.cs
│   │   └── TypeExtension.cs
│   ├── Models/                     # 数据模型
│   │   ├── BackgroundTaskItem.cs   # 后台任务状态模型
│   │   ├── DownloadTask.cs         # 下载任务模型
│   │   ├── EnabledData.cs
│   │   ├── IJsonInplaceSerializable.cs
│   │   ├── IJsonSerializable.cs
│   │   ├── IModManifest.cs
│   │   ├── LegacyModManifest.cs
│   │   ├── ManifestVersion.cs
│   │   ├── ModData.cs
│   │   ├── ModGroup.cs
│   │   ├── ModManifest.cs
│   │   ├── ModOption.cs
│   │   ├── ModProblem.cs
│   │   ├── ModSubOption.cs
│   │   ├── ModTag.cs               # Mod标签类
│   │   ├── TagSelectionItem.cs     # 标签选择项
│   │   ├── V1ModManifest.cs
│   │   ├── VersionCheckStatus.cs   # 版本检测相关模型
│   │   └── Nexus/                  # Nexus Mods数据模型
│   │       ├── HelperModels.cs
│   │       ├── Mod.cs
│   │       ├── ModFile.cs
│   │       ├── ModFileUpdateGroup.cs
│   │       └── NexusEnums.cs
│   ├── Resources/                  # 资源文件
│   │   ├── Fonts/
│   │   │   ├── Blockletter.otf
│   │   │   ├── FS Sinclair Bold.otf
│   │   │   ├── FS Sinclair Medium.otf
│   │   │   └── FS Sinclair Regular.otf
│   │   ├── Images/
│   │   │   ├── check.png
│   │   │   ├── discord_icon.png
│   │   │   ├── download.png
│   │   │   ├── error.png
│   │   │   ├── github_icon.png
│   │   │   ├── icon.ico
│   │   │   ├── icon.png
│   │   │   ├── loading.png
│   │   │   ├── logo.png
│   │   │   ├── logo_icon.png
│   │   │   ├── logo_splash.png
│   │   │   └── remove.png
│   │   ├── Native/                 # 原生库（7z.dll，Content 复制到输出目录）
│   │   │   └── 7z.dll
│   │   └── Styles/
│   │       ├── FluentAnimations.xaml
│   │       ├── FluentControls.xaml
│   │       ├── FluentDesignTokens.xaml
│   │       └── FluentWindows.xaml
│   ├── Services/                   # 业务服务
│   │   ├── BackgroundTaskService.cs    # 后台任务状态管理服务
│   │   ├── BrowserExtensionService.cs  # 浏览器扩展通信服务
│   │   ├── DatabaseService.cs      # SQLite数据库服务
│   │   ├── EnabledDataRepository.cs    # EnabledData仓储
│   │   ├── ModHashService.cs       # Mod 文件哈希/指纹计算服务
│   │   ├── ModService.cs
│   │   ├── ProfileService.cs
│   │   ├── SettingsService.cs      # Singleton生命周期
│   │   ├── VersionCheckService.cs  # 版本兼容性检测服务
│   │   └── Nexus/                  # Nexus Mods服务
│   │       ├── INexusCacheService.cs
│   │       ├── INexusHttpClient.cs
│   │       ├── INexusModsService.cs
│   │       ├── NexusCacheService.cs
│   │       ├── NexusHttpClient.cs
│   │       └── NexusModsService.cs
│   ├── Stores/                     # 状态存储
│   │   ├── EditModStore.cs
│   │   └── NavigationStore.cs
│   ├── ViewModels/                 # 视图模型
│   │   ├── Create/
│   │   │   ├── ChoosePageViewModel.cs
│   │   │   ├── CreateModOptionViewModel.cs
│   │   │   ├── CreateModSubOptionViewModel.cs
│   │   │   └── IntroPageViewModel.cs
│   │   ├── BackgroundTasksPageViewModel.cs # 后台任务页ViewModel
│   │   ├── CreatePageViewModel.cs
│   │   ├── DashboardPageViewModel.cs
│   │   ├── DownloadProgressViewModel.cs  # 下载进度页ViewModel
│   │   ├── EditPageViewModel.cs
│   │   ├── HelpPageViewModel.cs
│   │   ├── MainViewModel.cs
│   │   ├── ManifestEditPageViewModel.cs
│   │   ├── ModOptionViewModel.cs
│   │   ├── ModSubOptionViewModel.cs
│   │   ├── ModViewModel.cs
│   │   ├── NexusDownloadPageViewModel.cs # Nexus下载页ViewModel
│   │   ├── PageViewModelBase.cs
│   │   ├── SettingsPageViewModel.cs
│   │   ├── TagManagementPageViewModel.cs
│   │   └── WizardViewModelBase.cs
│   ├── Views/                      # 视图
│   │   ├── Create/
│   │   │   ├── ChoosePageView.xaml
│   │   │   ├── ChoosePageView.xaml.cs
│   │   │   ├── IncludeDirectoryPicker.xaml
│   │   │   ├── IncludeDirectoryPicker.xaml.cs
│   │   │   ├── IntroPageView.xaml
│   │   │   └── IntroPageView.xaml.cs
│   │   ├── BackgroundTasksPageView.xaml    # 后台任务页面
│   │   ├── BackgroundTasksPageView.xaml.cs
│   │   ├── CreatePageView.xaml
│   │   ├── CreatePageView.xaml.cs
│   │   ├── DashboardPageView.xaml
│   │   ├── DashboardPageView.xaml.cs
│   │   ├── DownloadProgressView.xaml      # 下载进度页
│   │   ├── DownloadProgressView.xaml.cs
│   │   ├── EditPageView.xaml
│   │   ├── EditPageView.xaml.cs
│   │   ├── HelpPageView.xaml
│   │   ├── HelpPageView.xaml.cs
│   │   ├── ManifestEditPageView.xaml
│   │   ├── ManifestEditPageView.xaml.cs
│   │   ├── NexusDownloadPageView.xaml     # Nexus下载页
│   │   ├── NexusDownloadPageView.xaml.cs
│   │   ├── SettingsPageView.xaml
│   │   ├── SettingsPageView.xaml.cs
│   │   ├── TagManagementPageView.xaml
│   │   └── TagManagementPageView.xaml.cs
│   ├── App.xaml
│   ├── App.xaml.cs
│   ├── AssemblyInfo.cs
│   ├── ComboBoxScrollBehavior.cs
│   ├── Converters.cs               # 数据转换器
│   ├── FileLogger.cs
│   ├── GroupItemConverter.cs
│   ├── RegisterServiceAttribute.cs
│   ├── MainWindow.xaml             # 主窗口
│   ├── MainWindow.xaml.cs
│   ├── app.manifest
│   └── Helldivers2ModManager.csproj
├── Purger/                         # Purger工具
│   ├── MainForm.Designer.cs
│   ├── MainForm.cs
│   ├── MainForm.resx
│   ├── Program.cs
│   └── Purger.csproj
├── .gitattributes
├── .gitignore
├── AGENTS.md                       # 开发和维护指南
├── Helldivers2ModManager.sln
├── LICENSE.txt
├── README.md
└── mod_manifest_v1-schema.json     # Mod清单JSON Schema
```

### 2.2 架构模式
- **MVVM**: 分离UI和业务逻辑
- **依赖注入**: 通过 `RegisterServiceAttribute` 自动注册服务

---

## 3. 开发规范

### 3.1 代码风格
- **可空引用类型**: 启用 (`Nullable enable`)
- **隐式 using**: 启用 (`ImplicitUsings enable`)
- **文件组织**: 按功能领域分文件夹
- **访问修饰符**: 默认使用 `internal`，仅必要时使用 `public`

### 3.2 服务注册规范

使用 `RegisterServiceAttribute` 标注服务类：

```csharp
[RegisterService(ServiceLifetime.Singleton)]  // Singleton/Transient/Scoped
internal sealed class MyService
{
    // 实现...
}
```

**生命周期说明**:
- `Singleton`: 单例模式（如 `SettingsService`）
- `Transient`: 每次请求创建新实例（如 ViewModel）
- `Scoped`: 作用域内共享（本项目较少使用）

**Contract 属性**（v1.4.0+）:
支持通过 `Contract` 参数将接口和实现类同时注册为同一单例：

```csharp
[RegisterService(ServiceLifetime.Singleton, Contract = typeof(INexusModsService))]
internal sealed class NexusModsService : INexusModsService
{
    // 实现...
}
```

使用时可通过接口或实现类两种方式注入，`App.xaml.cs` 中的注册逻辑会自动识别 `Contract` 属性。

### 3.2.1 ViewModel 与视图绑定规范 ⚠️

**重要**: 使用 `[RegisterService]` 注册 ViewModel 后，还需要在 `MainWindow.xaml` 的 `Window.Resources` 中添加对应的 `DataTemplate`，否则 WPF 无法正确渲染该页面。

```xml
<Window.Resources>
    <!-- 其他 DataTemplate... -->
    <DataTemplate DataType="{x:Type vms:MyNewViewModel}">
        <views:MyNewView/>
    </DataTemplate>
</Window.Resources>
```

当前已注册的页面 DataTemplate 包括：DashboardPageView、SettingsPageView、EditPageView、ManifestEditPageView、CreatePageView、TagManagementPageView、NexusDownloadPageView、DownloadProgressView、BackgroundTasksPageView。

**常见错误**: 如果只添加了 `[RegisterService]` 但没有在 XAML 中添加 DataTemplate，导航到该页面时会显示空白或错误。

### 3.3 ViewModel规范
- 继承 `PageViewModelBase` 或 `ObservableObject`
- 使用 `[ObservableProperty]` 标记可观察属性
- 使用 `[RelayCommand]` 标记命令
- 使用 `CommunityToolkit.Mvvm` 提供的特性

### 3.4 初始化模式
服务通常采用两阶段初始化：
1. 构造函数注入依赖
2. `Init()` 或 `InitAsync()` 方法进行完整初始化
3. 使用 `Initialized` 属性和 `GuardInitialized()` 检查初始化状态

### 3.4.1 后台任务系统

后台任务系统用于展示长耗时、非阻塞 UI 的操作进度，例如 Mod 文件哈希/指纹计算、下载导入、批量导入、更新、导出、部署、清理、删除和版本兼容性检查等。

**核心组件**:
- `Models/BackgroundTaskItem.cs` — 后台任务状态模型，包含任务名称、描述、状态、进度、错误信息、开始时间和完成时间
- `Services/BackgroundTaskService.cs` — 单例任务状态管理服务，统一维护后台任务集合并负责 UI 线程切换
- `ViewModels/BackgroundTasksPageViewModel.cs` — 后台任务页面 ViewModel，提供返回、清理已完成任务、移除已结束任务等命令
- `Views/BackgroundTasksPageView.xaml` — 后台任务页面，显示任务列表、状态、进度和错误信息

**使用规范**:
1. 在需要展示后台操作的服务或 ViewModel 中通过 DI 注入 `BackgroundTaskService`
2. 使用 `Add(name, description)` 创建任务，并保存返回的 `BackgroundTaskItem` 引用
3. 后台执行期间使用 `Update(task, description, progress, isIndeterminate)` 更新描述和进度
4. 成功时调用 `Complete(task, description)`，失败时调用 `Fail(task, errorMessage)`，用户取消时调用 `Cancel(task, description)`
5. 不要直接从业务代码修改 `Tasks` 集合，应通过 `BackgroundTaskService` 提供的方法操作

**进度规则**:
- `Progress` 使用 `0-1` 的小数值，`0` 表示 0%，`1` 表示 100%
- `IsIndeterminate = true` 表示不确定进度，适合无法预估总量的任务
- 有明确总量时应设置 `IsIndeterminate = false`，并按 `已完成数量 / 总数量` 更新 `Progress`

**线程规则**:
- `BackgroundTaskService` 内部会通过 WPF Dispatcher 切回 UI 线程
- 后台线程可以安全调用 `Add`、`Update`、`Complete`、`Fail`、`Cancel`、`Remove`、`ClearCompleted`
- 不要从后台线程直接修改 `BackgroundTaskItem` 属性或 `Tasks` 集合

**接入范围**:
- 应接入：下载/导入、批量导入、Mod 更新、导出、部署、清理、删除、版本兼容性检查、文件哈希/指纹计算
- 可选接入：启动加载 Mod、自动检测游戏目录等低频辅助长操作
- 不建议接入：设置保存、标签保存、语言初始化、HTTP 监听循环等短操作或常驻服务
- 下载任务保留 `DownloadTask` 作为业务和持久化模型，同时镜像到 `BackgroundTaskItem` 作为后台任务总览

**本地化规则**:
- 任务名称和描述应优先使用 `LocalizationService`
- 后台任务页面相关键使用 `BackgroundTasksPage.*`
- 业务任务描述按服务名组织，例如 `ModHashService.*`

**示例**:
```csharp
var backgroundTask = _backgroundTaskService.Add(
    _localizationService["BackgroundTasksPage.TaskTypeHash"],
    _localizationService["ModHashService.FingerprintSingleProgress"].Replace("{name}", mod.Manifest.Name));

try
{
    await DoLongRunningWorkAsync();
    _backgroundTaskService.Complete(backgroundTask, _localizationService["ModHashService.FingerprintSingleReady"].Replace("{name}", mod.Manifest.Name));
}
catch (Exception ex)
{
    _backgroundTaskService.Fail(backgroundTask, ex.Message);
}
```

### 3.5 日志规范
- 使用 `Microsoft.Extensions.Logging.ILogger<T>`
- 日志级别选择：
  - `Trace`: 详细调试信息
  - `Debug`: 调试信息
  - `Information`: 一般信息
  - `Warning`: 警告信息
  - `Error`: 错误信息
  - `Critical`: 严重错误

### 3.6 JSON处理规范
- 使用 `System.Text.Json`
- 配置 `JsonDocumentOptions`:
  - `AllowTrailingCommas = true`
  - `CommentHandling = JsonCommentHandling.Skip`

### 3.7 数据转换器规范

项目使用 `Converters.cs` 集中管理数据绑定转换器：

| 转换器名称 | 功能 | 说明 |
|-----------|------|------|
| `StringToColorBrushConverter` | 字符串转颜色画刷 | 支持十六进制颜色格式（如 `#FF6200EE`） |
| `BoolToVisibilityConverter` | 布尔值转可见性 | `true` → `Visible`, `false` → `Collapsed` |
| `TagsToStringConverter` | 标签集合转字符串 | 将多个标签用逗号分隔 |
| `ContainsConverter` | 字符串包含检测 | 参数为子串时返回 `true`/`false` |
| `NullToVisibilityConverter` | 非 null → Visible | null → Collapsed |
| `InverseNullToVisibilityConverter` | null → Visible | 非 null → Collapsed |
| `NullToBoolConverter` | 非 null → true | null → false |
| `InverseBoolConverter` | 布尔值取反 | — |
| `StringToVisibilityConverter` | 非空字符串 → Visible | 空字符串 → Collapsed |
| `BytesToSizeConverter` | 字节数转可读大小 | B/KB/MB/GB/TB 自动换算 |
| `DownloadStatusToStringConverter` | 下载状态枚举转中文文本 | 等待中/下载中/已完成/失败/已取消 |
| `DownloadStatusToVisibilityConverter` | 下载中 → Visible | 其他状态 → Collapsed |
| `ProgressWidthConverter` | 进度百分比 × 可用宽度 | `IMultiValueConverter`，需进度值和最大宽度 |
| `SpeedToReadableConverter` | 下载速度转可读文本 | 如 `1.5 MB/s` |
| `VersionStatusToColorConverter` | 版本状态转颜色画刷 | 兼容(绿)/不兼容(红)/未知(黄)/检查中(蓝)/错误(橙) |
| `VersionStatusToTextConverter` | 版本状态转中文文本 | 兼容/不兼容/无法确认/检查中/检查失败 |
| `SortModeConverter` | 排序模式转中文文本 | 默认顺序/名称 A-Z/名称 Z-A/已启用优先/已禁用优先 |

### 3.8 Mod标签系统

**ModTag 类结构**:
```csharp
public sealed class ModTag
{
    public Guid Id { get; set; }        // 标签唯一标识
    public string Name { get; set; }     // 标签名称
    public string Color { get; set; }    // 标签颜色（十六进制）
}
```

**标签搜索语法**:
- 使用 `@标签名` 在Dashboard中筛选带有特定标签的Mod
- 示例: `@Graphics` 将显示所有带有 "Graphics" 标签的Mod

### 3.9 拖拽自动滚动行为

`DragDropAutoScrollBehavior` 是一个附加行为（Attached Behavior），为支持拖拽的 `ItemsControl` 提供边界自动滚动能力。当拖拽到列表上下边缘区域时，自动滚动父级 `ScrollViewer`，提升大量项拖拽排序的体验。

**文件位置**: `Helldivers2ModManager/DragDropAutoScrollBehavior.cs`

**使用方式** — 在 `ItemsControl` 上附加属性即可启用：

```xml
xmlns:local="clr-namespace:Helldivers2ModManager"

<ItemsControl local:DragDropAutoScrollBehavior.IsEnabled="True"
              dd:DragDrop.IsDragSource="True"
              dd:DragDrop.IsDropTarget="True">
    ...
</ItemsControl>
```

**行为说明**:
- 自动从可视化树向上查找第一个 `ScrollViewer` 作为滚动目标，无需额外配置
- 上下边缘各 40px 为触发区域，检测到拖拽悬停时自动滚动
- 滚动速度为 12px/次（约 60fps）
- `DragLeave` 或 `Drop` 时自动停止滚动
- `ItemsControl` 卸载时自动清理计时器资源

**当前使用页面**:
- `DashboardPageView` — Mod 列表拖拽排序（通过 `xmlns:local` 引用）
- `CreatePageView` — 选项列表、子选项列表拖拽排序（通过 `xmlns:bhv` 引用）

---

### 3.10 版本检查大文件读取策略

`VersionCheckService` 在解析 `.patch_*` 文件时采用动态读取策略，避免大文件全量进入内存。

**策略规则**:
- 优先按单个文件判断，不按整个 Mod 或压缩包大小判断
- `.gpu_resources` 不允许全量读入内存，仅允许按需读取必要结构或使用低内存路径
- 普通 `.patch_*` 文件在内存充足且小于安全上限时可使用内存快路径
- 当可用内存不足、文件过大或文件大小超过 `int.MaxValue` 时，自动切换到 `FileStream` 随机读取路径

**动态阈值**:
- 使用 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes - GC.GetTotalMemory(false)` 估算当前可用内存
- 安全读取上限为 `Min(可用内存 / 10, 512MB)`
- 低内存路径只读取头部、类型表、文件表和 Unit 结构所需字段，不读取完整文件内容

**维护要求**:
- 新增版本检查、补丁结构分析或 GPU 资源分析逻辑时，不要直接使用 `File.ReadAllBytesAsync` 读取大型文件
- 需要随机访问补丁结构时优先使用 `FileStream` + 定位读取，并在每次读取前检查 `offset + size <= stream.Length`
- 对 2GB+ 文件必须避免转换为 `int` 偏移，偏移和长度计算优先使用 `long`
- 小文件内存快路径只作为性能优化，不能作为唯一解析路径

---

### 3.11 本地化系统（v1.5.0+）

#### 3.11.1 架构概述

本地化系统采用 **JSON 文件 + 运行时服务** 的轻量方案：

| 组件 | 文件 | 说明 |
|------|------|------|
| 本地化服务 | `Services/LocalizationService.cs` | 单例服务，加载 JSON 并提供索引器访问 |
| 标记扩展 | `Extensions/LocExtension.cs` | XAML 标记扩展 `{loc:Loc Key}` 实现动态绑定 |
| 中文资源 | `Resources/Language/zh-CN.json` | 中文本地化字符串 |
| 英文资源 | `Resources/Language/en-US.json` | 英文本地化字符串 |
| 语言设置 | `SettingsService.Language` | 持久化用户语言偏好 |

#### 3.11.2 JSON 格式

每个 locale 文件格式如下：

```json
{
  "locale": "zh-CN",
  "languageName": "中文",
  "strings": {
    "MainWindow.Title": "Helldivers 2 Mod Manager",
    "DashboardPage.SearchWatermark": "搜索 Mod 名称，使用 @标签名 搜索标签",
    ...
  }
}
```

- `locale`: 语言代码（如 `zh-CN`、`en-US`）
- `languageName`: 语言显示名称（如 `中文`、`English`）
- `strings`: 扁平化的键值对字典，键格式为 `页面/模块名.键名`

#### 3.11.3 LocalizationService

`[RegisterService(ServiceLifetime.Singleton)]` — 在 App 启动时自动注册。

**关键功能**:
- **自动检测**: 使用 `CultureInfo.InstalledUICulture.Name` 自动匹配系统语言
- **回退机制**: 精确匹配 → 语言族匹配（如 `zh` → `zh-CN`）→ 第一个可用语言 → `en-US`
- **运行时切换**: 设置 `SelectedLanguage` 属性即可切换语言，所有绑定自动更新
- **INotifyPropertyChanged**: 切换时触发 `PropertyChanged("Item")`，WPF 绑定自动刷新
- **索引器**: `service["DashboardPage.Title"]` 返回对应字符串，缺失时返回 `[Key]`

**语言列表**:
- 第一个选项为 `Auto Detect`（空字符串表示自动检测）
- 后续为 JSON 文件中的语言

#### 3.11.4 XAML 中使用

在 XAML 文件中添加命名空间：

```xml
xmlns:loc="clr-namespace:Helldivers2ModManager.Extensions"
```

然后使用标记扩展绑定：

```xml
<TextBlock Text="{loc:Loc MainWindow.Title}"/>
<Button Content="{loc:Loc DashboardPage.AddMod}"/>
<Button ToolTip="{loc:Loc MainWindow.Help}"/>
<TextBlock Text="{loc:Loc SettingsPage.Language}"/>
```

`LocExtension` 内部创建 `Binding` 到 `LocalizationService` 的索引器，因此在语言切换时自动更新。

#### 3.11.5 代码中的使用

在 ViewModel 或 Service 中通过 DI 获取 `LocalizationService`：

```csharp
internal sealed class MyViewModel
{
    private readonly LocalizationService _loc;
    
    public MyViewModel(LocalizationService loc)
    {
        _loc = loc;
        var title = _loc["MyPage.Title"];
    }
}
```

#### 3.11.6 添加新语言

1. 在 `Resources/Language/` 目录下创建 `{localeCode}.json` 文件
2. 按照 JSON 格式编写所有字符串翻译
3. 重新构建项目，系统自动识别新的 locale 文件

所有 locale 文件在构建时自动复制到输出目录（通过 `.csproj` 中的 `Content` 配置）。

#### 3.11.7 语言设置存储

- 用户选择的语言保存在 `settings.json` 的 `Language` 字段
- 空字符串表示自动检测
- 非空值（如 `zh-CN`）表示手动指定的语言
- 设置页面的"主页"选项卡中提供 `ComboBox` 选择语言

#### 3.11.8 字符串键组织规范

键名按 `Section.Key` 格式组织：

| 前缀 | 对应文件 |
|------|---------|
| `MainWindow.*` | MainWindow.xaml |
| `DashboardPage.*` | DashboardPageView.xaml + DashboardPageViewModel.cs |
| `SettingsPage.*` | SettingsPageView.xaml + SettingsPageViewModel.cs |
| `CreatePage.*` | CreatePageView.xaml + CreatePageViewModel.cs |
| `ManifestEditPage.*` | ManifestEditPageView.xaml |
| `EditPage.*` | EditPageView.xaml |
| `NexusDownloadPage.*` | NexusDownloadPageView.xaml |
| `DownloadProgress.*` | DownloadProgressView.xaml |
| `TagManagementPage.*` | TagManagementPageView.xaml |
| `HelpPage.*` | HelpPageView.xaml |
| `DeploymentOrderPage.*` | DeploymentOrderPageView.xaml |
| `CreateWizard.*` | 创建向导各个页面 |
| `MessageBox.*` | Components/MessageBox.xaml + .xaml.cs |
| `VersionCheck.*` | VersionCheckService 相关 |
| `Common.*` | 通用字符串 |

---

## 4. Mod清单规范

### 4.1 清单版本说明

| 版本 | 枚举值 | 状态 | 说明 |
|------|--------|------|------|
| Legacy | `ManifestVersion.Legacy` | 支持 | 旧版格式，无Version字段 |
| V1 | `ManifestVersion.V1` | 支持 | 当前推荐格式 |
| V2 | `ManifestVersion.V2` | 不支持 | 草稿阶段，抛出 `EndOfLifeException` |

### 4.2 V1清单格式 (推荐)

```json
{
  "Version": 1,
  "Guid": "0000000-0000000-0000000-0000000-0000000",
  "Name": "Mod名称",
  "Description": "Mod描述",
  "IconPath": "icon.png",
  "Options": [
    {
      "Name": "选项名称",
      "Description": "选项描述",
      "Image": "option.png",
      "Include": ["path1", "path2"],
      "SubOptions": [
        {
          "Name": "子选项名称",
          "Description": "子选项描述",
          "Image": "suboption.png",
          "Include": ["subpath1", "subpath2"]
        }
      ]
    }
  ]
}
```

### 4.3 字段说明

#### 必填字段
- `Guid`: 全局唯一标识符（UUID）
- `Name`: Mod名称
- `Description`: Mod描述

#### 可选字段
- `Version`: 必须为1（V1格式）
- `IconPath`: 相对于Mod根目录的图标路径
- `Options`: Mod选项数组
  - `Name`: 选项名称（必填）
  - `Description`: 选项描述（必填）
  - `Image`: 选项图片路径
  - `Include`: 要包含的目录路径数组
  - `SubOptions`: 子选项数组
    - `Name`: 子选项名称（必填）
    - `Description`: 子选项描述（必填）
    - `Image`: 子选项图片路径
    - `Include`: 要包含的目录路径数组（必填）
- `NexusData`: Nexus Mods数据，用于自动更新
  - `ModId`: Nexus Mod ID（必填）
  - `Version`: Mod版本（必填）

### 4.4 Legacy清单格式（兼容）

```json
{
  "Guid": "550e8400-e29b-41d4-a716-446655440000",
  "Name": "Mod名称",
  "Description": "Mod描述",
  "IconPath": "icon.png",
  "Options": ["option1", "option2"]
}
```

### 4.5 补丁文件命名规范

补丁文件必须遵循以下格式：
```
{16位十六进制名称}.patch_{索引}
{16位十六进制名称}.patch_{索引}.gpu_resources
{16位十六进制名称}.patch_{索引}.stream
```

正则表达式：`^[a-z0-9]{16}\.patch_[0-9]+(\.(stream|gpu_resources))?$`

---

## 5. 设置配置规范

### 5.1 设置字段说明

`SettingsService` 管理的设置项：

| 设置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `GameDirectory` | `string` | - | Helldivers 2 游戏目录 |
| `StorageDirectory` | `string` | `%LocalAppData%\Helldivers2ModManager` | Mod存储目录 |
| `TempDirectory` | `string` | `%LocalAppData%\Temp\Helldivers2ModManager` | 临时文件目录 |
| `LogLevel` | `LogLevel` | `Trace` | 日志级别 |
| `Opacity` | `float` | `0.8` | 窗口透明度（0.4-1.0） |
| `SkipList` | `ObservableCollection<string>` | `[]` | 跳过的文件名列表（16字符） |
| `CaseSensitiveSearch` | `bool` | `false` | 搜索是否区分大小写 |
| `UseSymbolicLinks` | `bool` | `false` | 是否使用符号链接部署 |
| `DeleteToRecycleBin` | `bool` | `true` | 删除Mod时移动到回收站 |
| `AutoRemoveMissingMods` | `bool` | `false` | 自动删除不存在的模组条目 |
| `EnableSorting` | `bool` | `false` | 是否启用排序功能 |
| `AutoCheckVersionOnStartup` | `bool` | `false` | 启动时自动检查模组版本兼容性 |
| `AutoCleanLogs` | `bool` | `true` | 是否启用自动清理过期日志 |
| `LogRetentionDays` | `int` | `7` | 日志保留天数 |
| `ExtensionHost` | `string` | `"localhost"` | 浏览器扩展监听主机 |
| `ExtensionPort` | `int` | `7456` | 浏览器扩展监听端口 |
| `NexusApiKey` | `string?` | `null` | Nexus Mods API Key（使用 `ProtectedData` 加密存储） |
| `Groups` | `ObservableCollection<ModGroup>` | `[]` | Mod分组列表 |
| `Tags` | `ObservableCollection<ModTag>` | `[]` | 标签列表 |

### 5.2 设置验证规则

- `GameDirectory`: 必须存在且包含 `data`、`tools`、`bin` 文件夹，且 `bin/helldivers2.exe` 存在
- `StorageDirectory`: 不存在时自动创建
- `TempDirectory`: 不存在时自动创建
- `Opacity`: 自动限制在 0.4-1.0 范围内
- `SkipList`: 元素必须为16字符长度
- `ExtensionHost`: 不能为空或空白字符串
- `ExtensionPort`: 必须在 1-65535 范围内
- `NexusApiKey`: 使用 `System.Security.Cryptography.ProtectedData` 加密存储

---

## 6. 构建与部署

### 6.1 构建要求
- .NET 8.0 SDK
- Windows 操作系统

### 6.2 本地构建命令

```powershell
# 构建主程序
cd Helldivers2ModManager
dotnet build Helldivers2ModManager.csproj --configuration Release

# 发布单文件可执行程序
dotnet publish Helldivers2ModManager.csproj --configuration Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o publish

# 构建Purger工具
cd ../Purger
dotnet publish Purger.csproj --configuration Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o publish
```

### 6.3 CI/CD流程

GitHub Actions 工作流：持续集成使用 `.github/workflows/ci.yml`，发布构建使用 `.github/workflows/build.yml`：
- 触发条件: 推送匹配 `v*` 的 tag，或通过 `workflow_dispatch` 手动触发
- 构建产物:
  - `Helldivers2ModManager.zip` - 主程序
  - `Purger.zip` - Purger工具
- 自动创建 Draft Release

### 6.4 部署文件说明

主程序发布为单文件可执行程序，包含：
- 所有依赖项内嵌
- 资源文件内嵌
- 自包含运行时（--self-contained true）

---

## 7. 安全注意事项

### 7.1 文件操作安全
- 文件操作前验证路径有效性
- 使用 `GuardInitialized()` 确保服务已初始化
- 使用 `GuardReadonly()` 防止只读模式下的修改
- 删除文件时使用回收站（`FileSystem.DeleteDirectory` with `RecycleOption.SendToRecycleBin`）

### 7.2 异常处理
- 全局异常处理：`App.xaml.cs` 中捕获未处理异常
- 自定义异常：`EndOfLifeException`、`UnknownManifestVersionException`
- 记录所有异常到日志

### 7.3 输入验证
- `SettingsService.Validate()` 验证设置有效性
- `ModService.CheckPaths()` 验证Mod清单路径
- 验证 `SkipList` 元素长度为16字符

### 7.4 符号链接
- 可选使用符号链接功能
- 需用户权限支持
- 默认使用文件复制方式

---

## 8. 维护要点

### 8.1 版本管理
- 当前版本: 2.0.0（`.csproj` 和 `App.xaml.cs` 一致）
- 版本号位置:
  - `App.xaml.cs` - `App.Version`
  - `Helldivers2ModManager.csproj` - `ProductVersion`, `AssemblyVersion`, `FileVersion`

### 8.2 数据存储位置
- **设置文件**: `settings.json` (程序目录)
- **Mod存储**: `%LocalAppData%\Helldivers2ModManager\Mods`
- **临时文件**: `%LocalAppData%\Temp\Helldivers2ModManager`
- **日志文件**: `ModManager*.log` (程序目录)

### 8.3 常见问题处理

#### Mod加载失败
- 检查 `manifest.json` 是否存在
- 验证清单格式和版本
- 检查路径有效性
- 查看日志文件获取详细错误

#### 部署问题
- 确认游戏目录设置正确
- 检查游戏 `data` 目录权限
- 尝试使用 Purger 工具清理

#### 设置丢失
- 检查 `settings.json` 是否存在
- 验证文件权限
- 重置默认设置

#### XAML 资源未找到错误
- **错误信息**: `"System.Windows.Markup.XamlParseException": 无法找到名为 "XXX" 的资源`
- **原因**: 在 XAML 中引用了不存在的样式资源
- **解决方案**:
  - 检查 `Resources/Styles/FluentControls.xaml` 中定义的所有可用样式
  - 按钮样式包括:
    - `FluentButtonBase` - 基础按钮样式
    - `FluentPrimaryButton` - 主要按钮（蓝色强调色）
    - `FluentSecondaryButton` - 次要/轮廓按钮
    - `FluentConfirmButton` - 确认/成功按钮（绿色）
    - `FluentDangerButton` - 危险/取消按钮（红色）
    - `RemoveButton` - 删除按钮
    - `FluentIconButton` - 图标按钮
  - 确保使用正确的样式名称，不要使用不存在的样式名

### 8.4 扩展开发注意事项
- 新增服务使用 `RegisterServiceAttribute` 标注
- 新增 ViewModel 遵循 MVVM 模式
- 修改清单格式需注意版本兼容性
- 更新文档和 JSON Schema

---

## 9. 术语表

| 术语 | 说明 |
|------|------|
| Mod | 游戏模组（Modification） |
| Manifest | Mod清单文件（manifest.json） |
| Patch | 游戏补丁文件 |
| Deployment | 将Mod文件部署到游戏目录的过程 |
| Purge | 清理已部署的Mod文件 |
| Profile | Mod配置方案 |
| Group | Mod分组 |
| Tag | Mod标签，用于分类和筛选 |
| EOL | End of Life，生命周期结束 |
| SkipList | 跳过的文件名列表（16字符名称） |
