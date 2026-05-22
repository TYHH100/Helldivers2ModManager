
# Helldivers2ModManager - 开发和维护指南

本文档记录了项目的所有重要规范、架构说明、开发要点和维护指导。

---

## 1. 项目概述

### 1.1 项目简介
Helldivers2ModManager 是一个用于 Helldivers 2 游戏的模组管理器，采用 WPF 和 .NET 8.0 开发。

### 1.2 技术栈
- **框架**: .NET 8.0 Windows (WPF)
- **依赖注入**: Microsoft.Extensions.DependencyInjection
- **MVVM**: CommunityToolkit.Mvvm
- **日志**: Microsoft.Extensions.Logging
- **压缩**: SharpCompress
- **拖拽**: gong-wpf-dragdrop
- **Markdown**: MdXaml

### 1.3 当前版本
- 版本: 1.4.0.2

---

## 2. 项目架构

### 2.1 目录结构
```
Helldivers2ModManager/
├── Helldivers2ModManager/          # 主应用程序
│   ├── Components/                 # UI组件
│   │   ├── MessageBox.xaml
│   │   └── MessageBox.xaml.cs
│   ├── Exceptions/                 # 自定义异常
│   │   ├── EndOfLifeException.cs
│   │   └── UnknownManifestVersionException.cs
│   ├── Extensions/                 # 扩展方法
│   │   ├── IOExtensions.cs
│   │   ├── JsonElementExtensions.cs
│   │   └── TypeExtension.cs
│   ├── Models/                     # 数据模型
│   │   ├── EnabledData.cs
│   │   ├── IJsonSerializable.cs
│   │   ├── IJsonInplaceSerializable.cs
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
│   │   └── V1ModManifest.cs
│   ├── Resources/                  # 资源文件
│   │   ├── Fonts/
│   │   ├── Images/
│   │   └── Styles/
│   ├── Services/                   # 业务服务
│   │   ├── ModService.cs
│   │   ├── ProfileService.cs
│   │   └── SettingsService.cs      # Singleton生命周期
│   ├── Stores/                     # 状态存储
│   │   ├── EditModStore.cs
│   │   └── NavigationStore.cs
│   ├── ViewModels/                 # 视图模型
│   │   ├── Create/
│   │   │   ├── ChoosePageViewModel.cs
│   │   │   └── IntroPageViewModel.cs
│   │   ├── CreatePageViewModel.cs
│   │   ├── DashboardPageViewModel.cs
│   │   ├── EditPageViewModel.cs
│   │   ├── HelpPageViewModel.cs
│   │   ├── MainViewModel.cs
│   │   ├── ModOptionViewModel.cs
│   │   ├── ModSubOptionViewModel.cs
│   │   ├── ModViewModel.cs
│   │   ├── PageViewModelBase.cs
│   │   ├── SettingsPageViewModel.cs
│   │   ├── TagManagementPageViewModel.cs
│   │   └── WizardViewModelBase.cs
│   ├── Views/                      # 视图
│   │   ├── Create/
│   │   │   ├── ChoosePageView.xaml
│   │   │   ├── ChoosePageView.xaml.cs
│   │   │   ├── IntroPageView.xaml
│   │   │   └── IntroPageView.xaml.cs
│   │   ├── CreatePageView.xaml
│   │   ├── CreatePageView.xaml.cs
│   │   ├── DashboardPageView.xaml
│   │   ├── DashboardPageView.xaml.cs
│   │   ├── EditPageView.xaml
│   │   ├── EditPageView.xaml.cs
│   │   ├── HelpPageView.xaml
│   │   ├── HelpPageView.xaml.cs
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
│   ├── app.manifest
│   └── Helldivers2ModManager.csproj
├── Purger/                         # Purger工具
│   ├── MainForm.Designer.cs
│   ├── MainForm.cs
│   ├── MainForm.resx
│   ├── Program.cs
│   └── Purger.csproj
├── .github/workflows/main.yml      # GitHub Actions工作流
├── mod_manifest_v1-schema.json     # Mod清单JSON Schema
├── AGENTS.md
├── README.md
└── Helldivers2ModManager.sln
```

### 2.2 架构模式
- **MVVM**: 分离UI和业务逻辑
- **依赖注入**: 通过 `RegisterServiceAttribute` 自动注册服务
- **服务定位器**: 从 `App.Host.Services` 获取服务

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
| `Groups` | `ObservableCollection<ModGroup>` | `[]` | Mod分组列表 |
| `Tags` | `ObservableCollection<ModTag>` | `[]` | 标签列表 |

### 5.2 设置验证规则

- `GameDirectory`: 必须存在且包含 `data`、`tools`、`bin` 文件夹，且 `bin/helldivers2.exe` 存在
- `StorageDirectory`: 不存在时自动创建
- `TempDirectory`: 不存在时自动创建
- `Opacity`: 自动限制在 0.4-1.0 范围内
- `SkipList`: 元素必须为16字符长度

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
- 触发条件: 推送到 `zh-cn_Translations` 分支或创建PR
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
- 当前版本: 1.3.0.1 (EOL)
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
- **CLAUDE.md**: AI编码指南
- **README.md**: 项目简介
- **mod_manifest_v1-schema.json**: Mod清单JSON Schema
- **.github/workflows/main.yml**: CI/CD工作流

---

## 11. 变更记录

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

*文档版本: 1.2.0*
*最后更新: 2026-04-30*
