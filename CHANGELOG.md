# Changelog

## [1.6.0](https://github.com/TYHH100/Helldivers2ModManager/compare/v1.5.0...v1.6.0) (2026-09-05)


### Features

* **audio:** 模型预览页支持音频模组试听（Wwise bank/WEM 有界解析、AoTuV 解码播放与游戏原版比对） ([548626a](https://github.com/TYHH100/Helldivers2ModManager/commit/548626a44101946b2db87fba828e3805fcfddadd))
* **audio:** 补充 hd2-audio-modder（ARR）出处声明：README 第三方声明、AGENTS.md 约束与音频界面可关闭横幅 ([33cb25d](https://github.com/TYHH100/Helldivers2ModManager/commit/33cb25dc41caf39ff3335dfb9f08220c275aa8a3))
* **auto-tag:** 自动识别模组类型并打标签，含首次使用引导 ([bb9b985](https://github.com/TYHH100/Helldivers2ModManager/commit/bb9b985075ffb516d34d7a20c7ba35fcb4087d44))
* **auto-tag:** 配对页添加返回按钮 ([b257182](https://github.com/TYHH100/Helldivers2ModManager/commit/b2571822ea48bb885d0e08c9abbf98ec4e857e28))
* **backup:** 备份历史按原始文件分组并支持整模组回滚到指定时间点 ([5c8b3b4](https://github.com/TYHH100/Helldivers2ModManager/commit/5c8b3b43518a7241cf820068bfc751e45862bce5))
* **bisect:** 新增二分法排查失效模组功能 ([aa85494](https://github.com/TYHH100/Helldivers2ModManager/commit/aa85494f1cc482bc35b0427dd2df2b193bb5624b))
* **bisect:** 部署前检测游戏运行状态并确认后自动关闭，部署后自动启动游戏 ([54adc57](https://github.com/TYHH100/Helldivers2ModManager/commit/54adc5777d6a2123deb668742887b2a8e3c42900))
* **create:** 创建模组支持选择清单格式，Legacy 选项沿用文件夹规则 ([45876d4](https://github.com/TYHH100/Helldivers2ModManager/commit/45876d4d406e6cf32f8de1ab9b8ff783c0341e34))
* **dashboard:** 新增模组刷新功能，优化部署顺序逻辑 ([343f54e](https://github.com/TYHH100/Helldivers2ModManager/commit/343f54e9cd40ed4dc27410aaa6608513a44a4838))
* **dashboard:** 模组卡片右键菜单支持置顶/置底/移动到指定位置 ([a33b9db](https://github.com/TYHH100/Helldivers2ModManager/commit/a33b9db4c818c9db8b8f8f2d25c8a231ca50a4be))
* **dashboard:** 模组卡片显示列表序号，与右键移动到指定位置编号一致 ([cca9a66](https://github.com/TYHH100/Helldivers2ModManager/commit/cca9a6615320c81a2f565e24f4644fbdd9a19265))
* **deploy:** 兼容 HD2PhysBone 部署：游戏运行护栏、补丁链补洞、参数目录生命周期与置底排序 ([9516825](https://github.com/TYHH100/Helldivers2ModManager/commit/95168254820da9ed86477012ef391a82b8566dfa))
* **export:** 导出完成后自动打开压缩包所在文件夹并选中文件 ([54e7a8f](https://github.com/TYHH100/Helldivers2ModManager/commit/54e7a8fc18ea88b4752eb28b695d1cbbeb922a4d))
* finalize preview and patch tooling changes ([f9839d9](https://github.com/TYHH100/Helldivers2ModManager/commit/f9839d9a3542ea63e2854ce6c18b90683419a28b))
* **i18n:** 实现完整的多语言本地化系统 ([68a68fb](https://github.com/TYHH100/Helldivers2ModManager/commit/68a68fb03cae65743542760f39c2b1f46c0d86be))
* improve armor analysis and startup validation ([d12fff4](https://github.com/TYHH100/Helldivers2ModManager/commit/d12fff404b3caae7998541650380c30c7b4d6bc5))
* improve mod repair and recovery workflows ([039c3af](https://github.com/TYHH100/Helldivers2ModManager/commit/039c3af043bff1b1976971a99d68fe19581f8e0e))
* **mod-group:** 实现模组分组管理功能 ([348cdeb](https://github.com/TYHH100/Helldivers2ModManager/commit/348cdeb6d0d2b1943ce1114e097995be7bd4c1d3))
* **model-preview:** add skeletal animation playback ([9a76977](https://github.com/TYHH100/Helldivers2ModManager/commit/9a7697742486560013b7505aa34c35bf33b0b590))
* **patchtool:** 扫描/修复全流程写入日志文件并自动清理 ([a98e18f](https://github.com/TYHH100/Helldivers2ModManager/commit/a98e18f19395248597dc2029bc02890001a9bfe6))
* **patchtool:** 日志升级为非常详细，输出扫描/修复每项明细 ([a2fa17b](https://github.com/TYHH100/Helldivers2ModManager/commit/a2fa17be557857253653e3bc8bae778dad54dae6))
* **preview:** 字幕文本模组预览与模型流光/发光材质显示支持 ([7047d14](https://github.com/TYHH100/Helldivers2ModManager/commit/7047d14455994e41c6f9ec690c4eadedfb45201a))
* **search:** add pinyin and fuzzy matching to mod name search ([85b3736](https://github.com/TYHH100/Helldivers2ModManager/commit/85b373680b808d233316f86e41cba7ace6c66698))
* **tasks:** split foreground/background tasks with step progress ([f8a813c](https://github.com/TYHH100/Helldivers2ModManager/commit/f8a813c3ac9c35366a4264b38354e8e9aac83055))
* **ui:** add customizable window background image ([d003d59](https://github.com/TYHH100/Helldivers2ModManager/commit/d003d59fe655b619bd56069a666a77fcf52f6e85))
* **ui:** overhaul drag-drop scrolling and add group multi-assign ([08dd588](https://github.com/TYHH100/Helldivers2ModManager/commit/08dd58852c62865757ccb08ce38d18f3b60490dd))
* **ui:** support drag-drop archive import, fix splash flash, enhance multi-select ([d773090](https://github.com/TYHH100/Helldivers2ModManager/commit/d773090569c33cffe8e3e9ef5be457c971dba6cc))
* **ui:** translucent cards with adjustable opacity and appearance polish ([7d47e70](https://github.com/TYHH100/Helldivers2ModManager/commit/7d47e70e8217f51d8edf04ab981ba8ab0fc9e2b2))
* **ui:** 后台任务更名为任务中心，检查/迁移/冲突结果改用自动消失气泡通知 ([9cbc73f](https://github.com/TYHH100/Helldivers2ModManager/commit/9cbc73f80b3733bc195838020b42e0004b513e1c))
* **version-check:** 添加备份与还原管理 ([5a2d554](https://github.com/TYHH100/Helldivers2ModManager/commit/5a2d5542f485171bae0f104ee36fb91f2acc044b))
* **version-check:** 添加自动兼容性检测与模组修复 ([9041439](https://github.com/TYHH100/Helldivers2ModManager/commit/90414398598b9aea14599ae55ecdfaa47d13feb0))
* **versioncheck:** read reference unit version from game archives ([bdc3cc1](https://github.com/TYHH100/Helldivers2ModManager/commit/bdc3cc12c0bbc8d22cc70a2f2ac32cd64772e3a3))
* 新增Nexus Mods下载集成功能 ([bba5eb9](https://github.com/TYHH100/Helldivers2ModManager/commit/bba5eb9fb59d1aa68e5713287e4b2c85098dc647))
* 新增后台任务系统与相关功能 ([1007092](https://github.com/TYHH100/Helldivers2ModManager/commit/10070924fdf264a5fad8847601cfc1fb7cda4868))
* 新增多项功能并优化现有逻辑 ([8118f8d](https://github.com/TYHH100/Helldivers2ModManager/commit/8118f8d67fa80dc353dfa4683cc72b05385677f4))
* 新增模组创建、版本兼容性检测等功能 ([383bef9](https://github.com/TYHH100/Helldivers2ModManager/commit/383bef9da847e1ddb4668d42906f7863b558329c))
* 新增模组排序、多选批量操作与SQLite数据迁移 ([919c57b](https://github.com/TYHH100/Helldivers2ModManager/commit/919c57b2ac8f7ee0e7bd624e350e200002bd8a43))
* 新增模组自定义部署顺序与分隔符功能 ([af18919](https://github.com/TYHH100/Helldivers2ModManager/commit/af18919cadac542a016dfd165a03cf6d1b2b052a))
* 添加标签管理系统和UI改进 ([38a59cc](https://github.com/TYHH100/Helldivers2ModManager/commit/38a59cc190088617c74b2feac1f03bd2afa88ac0))


### Bug Fixes

* **App:** 修复初始化设置时的UI线程死锁问题 ([b02b12d](https://github.com/TYHH100/Helldivers2ModManager/commit/b02b12d42f13dcce399bc405b1d78f8ba5d3d982))
* **bisect:** 全程未崩溃时不标记嫌疑模组，崩溃证据含迭代验证部署 ([35fc6b4](https://github.com/TYHH100/Helldivers2ModManager/commit/35fc6b4da6804f2ba55594d104f29e5860e50892))
* **bisect:** 收敛后先单独验证候选模组再定嫌疑，全程正常时不再强制标记 ([1b52d82](https://github.com/TYHH100/Helldivers2ModManager/commit/1b52d8260ad423183a44313bf5479ccffc7edda8))
* **dashboard:** 修复Mods集合未同步更新的问题 ([7119939](https://github.com/TYHH100/Helldivers2ModManager/commit/7119939b415c815522e3dba731a72988ec9c6941))
* **manifest:** 宽容处理清单版本误用与必填字段缺失 ([858330c](https://github.com/TYHH100/Helldivers2ModManager/commit/858330c85ef860b37b2e3a184de9146cc44af7fc))
* refine bisect, version check and preview pipeline ([686be65](https://github.com/TYHH100/Helldivers2ModManager/commit/686be65b4700f6035d0f2a0630534428718ec357))
* **repair:** update legacy character material LOD bindings ([393f71d](https://github.com/TYHH100/Helldivers2ModManager/commit/393f71d54ec88e9c721a8e1b6b03d43862f3e7a7))
* **repair:** 旧角色材质包内所有 Unit 统一改用游戏 LOD ([8585552](https://github.com/TYHH100/Helldivers2ModManager/commit/85855524dfb7705ba0637d6d769a835f17f57746))
* **settings:** 日志级别保存后重开回退为警告 ([bc038e9](https://github.com/TYHH100/Helldivers2ModManager/commit/bc038e9741f450a8a8bfa6c5a72c5e1919bd2deb))
* **test:** add missing System.Windows.Controls using for Viewport3D ([7ad8b9a](https://github.com/TYHH100/Helldivers2ModManager/commit/7ad8b9ac4b0e304f72e78a7ec8a9f5c5aa495107))
* **ui:** MessageBoxSelectionMessage 支持取消按钮回调避免流程挂起 ([e5842b1](https://github.com/TYHH100/Helldivers2ModManager/commit/e5842b102e3ecabce067fde1ead0b60f0677f96b))
* **ui:** point expander arrow up when expanded ([3870991](https://github.com/TYHH100/Helldivers2ModManager/commit/387099181094953cd9f6d90c1d674b3f9382d62d))
* **ui:** remove async-without-await warning in preview refresh ([f9d19f6](https://github.com/TYHH100/Helldivers2ModManager/commit/f9d19f6cb4ba4437f093a16e77debce8dbe52b48))
* **versioncheck:** restore backups located in nested option folders ([c26c677](https://github.com/TYHH100/Helldivers2ModManager/commit/c26c677228cb3ead31b3c82ec6ca8f0333b181a4))
* **viewmodel, app:** 防御性处理SettingsService未初始化场景 ([1e81c1a](https://github.com/TYHH100/Helldivers2ModManager/commit/1e81c1a6e0dc294f3c8966d3e4a17fc16f0d3755))
* **viewmodel:** 修复跨线程更新UI集合和属性的问题 ([d15fc93](https://github.com/TYHH100/Helldivers2ModManager/commit/d15fc93fe8666cf9649e5ca49260c53c6cfed8ac))
* **view:** 修正模组卡片名称与标签 StackPanel 的闭合标签位置 ([ef8e87d](https://github.com/TYHH100/Helldivers2ModManager/commit/ef8e87d3198db3cd8609686895ebb036f9a65b07))
* **view:** 标签选择列表限制最大高度并启用垂直滚动 ([640a1dd](https://github.com/TYHH100/Helldivers2ModManager/commit/640a1dd61546e752a71b16a50f8518d0aeae2362))
* **view:** 模组右键菜单将移动位置相关项提前到编辑项之前 ([bdd40ac](https://github.com/TYHH100/Helldivers2ModManager/commit/bdd40acaa5c7133a1d84a2a9baccdffe64a52e1f))


### Performance Improvements

* **batchrepair:** repair mods concurrently across mods ([2738cac](https://github.com/TYHH100/Helldivers2ModManager/commit/2738cace5dc1568a4537bd1a279610f7fcc03a3c))
* bounded parallel copies, hash migration and service optimizations ([ec7e131](https://github.com/TYHH100/Helldivers2ModManager/commit/ec7e1317257ea19c87fd2803434ed95dba8d9351))
* **localization:** defer full locale parsing until language is used ([037335d](https://github.com/TYHH100/Helldivers2ModManager/commit/037335d409cb8c3a1dd4fff89cf2ba6630312e6d))
* **service:** 并行解析模组目录清单以缩短启动加载时间 ([afa4ca9](https://github.com/TYHH100/Helldivers2ModManager/commit/afa4ca9fa3d6379e8a746a7143a42c5d66e588a5))
* **versioncheck:** cache patch analysis results ([b6c7788](https://github.com/TYHH100/Helldivers2ModManager/commit/b6c778846834764127a800a96ad8b33adca669e8))
* **versioncheck:** keep result summary responsive during check ([53137ac](https://github.com/TYHH100/Helldivers2ModManager/commit/53137ac6f9a3aee94512f3a52fc20995b7a1a6b4))


### Reverts

* **audio:** 移除界面出处横幅，出处说明仅保留在代码注释/README/AGENTS.md ([522bce9](https://github.com/TYHH100/Helldivers2ModManager/commit/522bce9522678734dcfe9bfbd04fe1a004140070))

## [Unreleased]

### 变更

- **拖拽滚动体验优化**：重写 `DragDropAutoScrollBehavior`，边界自动滚动改为按贴近边缘程度加速（时间积分、与刷新率无关），滚动后合成冒泡 `DragOver` 实时刷新插入指示线；拖拽期间通过 `WH_MOUSE_LL` 钩子支持鼠标滚轮滚动列表（OLE 拖拽循环会吞掉滚轮消息），并修复 ListBox（部署顺序页）因 ScrollViewer 是视觉后代而无法自动滚动的问题

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
