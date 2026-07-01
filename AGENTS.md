
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
- **Markdown**: MdXaml 1.27.0
- **数据库**: Microsoft.Data.Sqlite 9.0.8
- **缓存**: Microsoft.Extensions.Caching.Memory 10.0.8
- **通用工具**: CommunityToolkit.Common 8.4.2

### 1.3 当前版本
- 版本: 1.4.1.0

---

## 2. 项目架构

### 2.1 目录结构
```
Helldivers2ModManager/
├── .github/                        # GitHub Actions
│   └── workflows/main.yml
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
│   │   ├── BrowserExtensionService.cs  # 浏览器扩展通信服务
│   │   ├── DatabaseService.cs      # SQLite数据库服务
│   │   ├── EnabledDataRepository.cs    # EnabledData仓储
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

当前已注册的页面 DataTemplate 包括：DashboardPageView、SettingsPageView、EditPageView、ManifestEditPageView、CreatePageView、TagManagementPageView、NexusDownloadPageView、DownloadProgressView。

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
| V2 | `ManifestVersion.V2` | 不支持 | 已废弃，抛出 `EndOfLifeException` |

### 4.2 V1清单格式 (推荐)

```json
{
  "Version": 1,
  "Guid": "550e8400-e29b-41d4-a716-446655440000",
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
  ],
  "NexusData": {
    "ModId": 12345,
    "Version": "1.0.0"
  }
}
```

### 4.3 字段说明

#### 必填字段
- `Version`: 必须为1（V1格式）
- `Guid`: 全局唯一标识符（UUID）
- `Name`: Mod名称
- `Description`: Mod描述

#### 可选字段
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

GitHub Actions 工作流 (`.github/workflows/main.yml`)：
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
- 当前版本: 1.4.1.0（`.csproj` 和 `App.xaml.cs` 一致）
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

---

## 10. 相关文件参考

- **AGENTS.md**: 本文件，开发和维护指南
- **README.md**: 项目简介
- **mod_manifest_v1-schema.json**: Mod清单JSON Schema
- **.github/workflows/main.yml**: CI/CD工作流

---

## 11. 变更记录

### v1.4.1.0 更新内容

#### 新增文件和目录

**新目录**:
- `Exceptions/Nexus/` - Nexus Mods API 异常类
- `Models/Nexus/` - Nexus Mods 数据模型
- `Services/Nexus/` - Nexus Mods 服务层

**新文件**:
- `Models/DownloadTask.cs` - 下载任务模型（进度跟踪、速度计算、持久化）
- `Models/VersionCheckStatus.cs` - 版本兼容性检测相关模型（`ModVersionStatus` 枚举、`ModVersionCheckResult` 等）
- `Exceptions/Nexus/NexusApiException.cs` - Nexus API 异常（含 4 个子类：Not Found、API Key Invalid、Rate Limit、Validation）
- `Exceptions/Nexus/NexusPremiumRequiredException.cs` - 需要 Nexus Premium 会员权限异常
- `Services/VersionCheckService.cs` - 版本兼容性检测服务（解析补丁文件二进制头部，提取 Unit 资源版本号）
- `Services/BrowserExtensionService.cs` - 浏览器扩展通信服务（HttpListener 接收下载请求，管理下载队列）
- `Services/DatabaseService.cs` - SQLite 数据库服务（WAL 模式，自动迁移）
- `Services/EnabledDataRepository.cs` - EnabledData 的 SQLite 仓储
- `Services/Nexus/INexusHttpClient.cs` / `NexusHttpClient.cs` - Nexus Mods API HTTP 客户端
- `Services/Nexus/INexusModsService.cs` / `NexusModsService.cs` - Nexus Mods 高层服务
- `Services/Nexus/INexusCacheService.cs` / `NexusCacheService.cs` - Nexus API 缓存服务
- `ViewModels/DownloadProgressViewModel.cs` - 下载进度页 ViewModel
- `ViewModels/NexusDownloadPageViewModel.cs` - Nexus 下载页 ViewModel
- `ViewModels/ManifestEditPageViewModel.cs` - 清单编辑页 ViewModel（右键菜单"编辑模组"）
- `ViewModels/Create/CreateModOptionViewModel.cs` - 创建页面选项编辑 ViewModel
- `ViewModels/Create/CreateModSubOptionViewModel.cs` - 创建页面子选项编辑 ViewModel
- `Views/DownloadProgressView.xaml` / `.xaml.cs` - 下载进度页面
- `Views/NexusDownloadPageView.xaml` / `.xaml.cs` - Nexus 下载页面
- `Views/ManifestEditPageView.xaml` / `.xaml.cs` - 清单编辑页面
- `Views/Create/IncludeDirectoryPicker.xaml` / `.xaml.cs` - 包含目录选择器组件
- `MainWindow.xaml` / `MainWindow.xaml.cs` - 主窗口

#### 新增功能

1. **Nexus Mods 集成**: 支持从 Nexus Mods 下载并导入 Mod，包括 Mod 信息浏览、文件选择、下载进度显示
2. **浏览器扩展支持**: 通过 `BrowserExtensionService` 接收浏览器扩展的下载请求
3. **版本兼容性检测**: 扫描 Mod 补丁文件的二进制头部，对比游戏版本，自动标记兼容/不兼容状态
4. **Dashboard 排序功能**: 支持按名称（A-Z/Z-A）、启用状态（已启用优先/已禁用优先）排序
5. **批量操作**: 支持全选/取消全选、批量删除、批量启用/禁用
6. **原位编辑**: 支持在 Dashboard 中直接编辑 Mod 名称、描述、图片
7. **标签编辑**: 支持在 Dashboard 中为 Mod 添加/移除标签
8. **SQLite 数据库**: 引入 `DatabaseService` 和 `EnabledDataRepository`，将 EnabledData 持久化到 SQLite
9. **自动版本检查**: 启动时可自动检查所有 Mod 的版本兼容性（每会话仅执行一次）
10. **Nexus API Key 加密存储**: 使用 `ProtectedData` 加密存储 API Key
11. **游戏路径自动检测**: 设置页面支持通过注册表和 `libraryfolders.vdf` 自动检测 Steam 游戏路径
12. **退出时清理**: 应用退出时自动清理 `hd2mm_*` 临时目录
13. **清单编辑页**: 支持通过右键菜单"编辑模组"打开清单编辑页面，直接编辑 Mod 基本信息及选项/子选项

#### 设置更新
- 新增 `EnableSorting` 设置项（排序功能开关）
- 新增 `AutoCheckVersionOnStartup` 设置项（启动自动版本检查）
- 新增 `AutoCleanLogs` 设置项（自动清理过期日志）
- 新增 `LogRetentionDays` 设置项（日志保留天数）
- 新增 `ExtensionHost` / `ExtensionPort` 设置项（浏览器扩展地址配置）
- 新增 `NexusApiKey` 设置项（Nexus API Key，加密存储）

#### 框架更新
- 新增 NuGet 包：`Microsoft.Data.Sqlite`、`Microsoft.Extensions.Caching.Memory`、`CommunityToolkit.Common`、`SharpSevenZip` 2.0.77
- **压缩库替换**: 移除 `SharpCompress` 0.48.1，替换为 `SharpSevenZip` 2.0.77
  - `SharpSevenZip` 基于原生 7z.dll，完整支持所有 LZMA/LZMA2 字典大小
  - 解决 SharpCompress 纯托管实现对**大字典 LZMA 压缩文件**的兼容性问题
  - `Resources/Native/7z.dll` 作为 `Content`（`CopyToOutputDirectory`）随程序分发
  - `App.xaml.cs` 在 `OnStartup` 中通过 `SetLibraryPath()` 初始化路径
  - 支持所有格式（7z/zip/rar/tar 等），按归档签名自动检测格式
- `RegisterServiceAttribute` 新增 `Contract` 属性，支持接口/实现类同时注册
- `HostApplicationBuilder` 模式替代直接创建 `Host`
- 注册 `IMemoryCache` 缓存服务
- `App.xaml.cs` 启动后延迟 1 秒启动 `BrowserExtensionService`

### v1.3.0.1 更新内容

#### 新增文件
- `Models/ModTag.cs` - Mod标签数据模型
- `Models/TagSelectionItem.cs` - 标签选择项封装
- `Converters.cs` - 数据绑定转换器集合
- `ViewModels/TagManagementPageViewModel.cs` - 标签管理页面视图模型
- `Views/TagManagementPageView.xaml` - 标签管理页面视图
- `AssemblyInfo.cs` - 程序集信息
- `ComboBoxScrollBehavior.cs` - 组合框滚动行为
- `GroupItemConverter.cs` - 分组项转换器

#### 新增功能
1. **标签系统**: 支持为Mod添加自定义标签
2. **标签搜索**: 在Dashboard中使用 `@标签名` 语法筛选Mod
3. **标签管理**: 独立的标签管理页面，支持创建、编辑、删除标签
4. **颜色定制**: 每个标签可自定义显示颜色
5. **删除方式设置**: 支持移动到回收站或直接删除
6. **自动清理**: 自动删除不存在的模组条目

#### 设置更新
- `SettingsService` 改为 `Singleton` 生命周期
- 新增 `DeleteToRecycleBin` 设置项
- 新增 `AutoRemoveMissingMods` 设置项
- 新增 `Tags` 集合存储标签数据

---

*文档版本: 1.4.1*
*最后更新: 2026-06-17*
