# Changelog

## [1.5.0] - 2026-06-23

### 新增

- **搜索过滤服务**：新增 `SearchFilterService`，支持 Dashboard 中模组的关键词搜索与标签筛选（`@标签名` 语法）
- **排序服务**：新增 `SortService`，支持按名称（A-Z / Z-A）、启用状态（已启用优先 / 已禁用优先）对模组列表进行排序
- **文件哈希系统**：新增 `FileHashUtils`、`FileHashRepository`、`ModHashService`，实现模组文件哈希计算与缓存机制，支持增量检测文件变更
- **版本检查仓储**：新增 `VersionCheckRepository`，将版本兼容性检测结果持久化到 SQLite 数据库，避免重复扫描
- **版本检查页面**：新增 `VersionCheckViewModel`，提供独立的版本兼容性检查结果展示界面
- **图片预览基类**：新增 `ModImageViewModelBase`，统一创建/编辑页面的图片选择与预览逻辑
- **拖拽自动滚动**：新增 `DragDropAutoScrollBehavior` 附加行为，拖拽到列表边缘时自动滚动，提升大量项的拖拽排序体验
- **更新进度弹窗**：优化下载/更新过程中的进度提示弹窗体验
- **Fluent 设计样式**：新增 `FluentControls.xaml` 与 `FluentWindows.xaml` 样式资源，进一步统一 UI 视觉风格
- **自动化发布流程**：配置 `release-please` 实现基于 GitHub Tag 的自动化版本发布与 Changelog 生成
- **完整构建工作流**：新增 `build.yml` 工作流，发布时自动构建主程序与 Purger 工具并打包

### 变更

- **移除模组分组功能**：删除 `ModGroup` 模型与 `GroupItemConverter` 转换器，简化数据结构（分组管理机制已重构为独立模块化方案）
- **Dashboard 重构**：大幅重构 `DashboardPageViewModel`，分离搜索、排序、状态管理职责，代码结构更清晰
- **创建页面重构**：重构 `CreatePageViewModel` 及选项/子选项 ViewModel，统一图片处理流程，优化选项管理交互
- **清单编辑页优化**：改进 `ManifestEditPageView` 的选项/子选项编辑布局与交互体验
- **设置页面优化**：重新组织 `SettingsPageView` 的设置项布局，提升可读性与操作便利性
- **消息框组件升级**：重构 `MessageBox` 组件，支持更丰富的自定义内容与样式
- **下载任务优化**：改进 `DownloadTask` 的线程安全处理与进度计算逻辑
- **浏览器扩展服务优化**：增强 `BrowserExtensionService` 的请求处理稳定性
- **Nexus HTTP 客户端优化**：改进 `NexusHttpClient` 的请求头处理与错误响应解析
- **数据库服务增强**：扩展 `DatabaseService` 支持更多表结构与迁移场景
- **目录遍历安全修复**：增强 `IOExtensions` 中目录遍历的异常处理，防止路径遍历问题
- **日志输出优化**：调整日志格式，避免敏感信息（如 API Key、本地路径）泄露到日志中
- **依赖清理**：移除不再使用的转换器引用与冗余代码

### 修复

- 修复图片加载异常导致的崩溃问题
- 修复部分场景下模组列表刷新不同步的问题
- 修复设置页面某些配置项保存后未即时生效的问题

## [1.4.1.0] - 2026-06-17

### 新增

- Nexus Mods 集成：支持从 Nexus Mods 浏览、下载并导入 Mod
- 浏览器扩展支持：通过 BrowserExtensionService 接收浏览器扩展的下载请求
- 版本兼容性检测：扫描 Mod 补丁文件的二进制头部，对比游戏版本，自动标记兼容/不兼容状态
- Dashboard 排序功能：支持按名称、启用状态排序
- 批量操作：全选/取消全选、批量删除、批量启用/禁用
- 原位编辑：在 Dashboard 中直接编辑 Mod 名称、描述、图片
- 标签编辑：在 Dashboard 中为 Mod 添加/移除标签
- SQLite 数据库：引入 DatabaseService 和 EnabledDataRepository
- 自动版本检查：启动时可自动检查所有 Mod 的版本兼容性
- Nexus API Key 加密存储：使用 ProtectedData 加密存储
- 游戏路径自动检测：通过注册表和 libraryfolders.vdf 自动检测 Steam 游戏路径
- 清单编辑页：右键菜单"编辑模组"打开清单编辑页面
- 退出时清理：应用退出时自动清理临时目录

### 变更

- 压缩库替换：SharpCompress → SharpSevenZip（支持大字典 LZMA）
- RegisterServiceAttribute 新增 Contract 属性
- HostApplicationBuilder 模式替代直接创建 Host

[1.5.0]: https://github.com/TYHH10/Helldivers2ModManager/compare/v1.4.1.0...v1.5.0.0
[1.4.1.0]: https://github.com/TYHH10/Helldivers2ModManager/releases/tag/v1.4.1.0
