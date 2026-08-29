# Helldivers2ModManager Frontend Next

这是新前端隔离工作区。`Helldivers2ModManager.Frontend` 只允许引用 `Helldivers2ModManager.Core`；临时 Host 只允许组合 Core 模块和 Frontend。

## Commands

```powershell
dotnet build Helldivers2ModManager.Frontend.sln --configuration Debug -m:1
dotnet test tests/Helldivers2ModManager.Frontend.Tests/Helldivers2ModManager.Frontend.Tests.csproj --configuration Debug
```

临时启动器：

```powershell
src/Helldivers2ModManager.Frontend.Host/bin/Debug/net10.0-windows/Helldivers2ModManager.Frontend.Host.exe
```

隔离数据目录是 `%LOCALAPPDATA%\Helldivers2ModManagerNext\data`。禁止读写旧程序目录中的 `settings.json` 和数据库。

## Architecture Gates

- Frontend 项目引用必须只有 Core。
- 禁止使用旧主程序的 Services、ViewModels、Models 或 Views 命名空间。
- 用户可见文本必须进入 Core 本地化资源，并同步提供 en-US 和 zh-CN。
- 全局导航采用两级模型：顶部显示模块，侧栏只显示当前模块的子页面；对象级行为保留在页面上下文菜单。
- 页面 ViewModel 必须由独立 DI Scope 创建；事件订阅必须配对退订。
- 耗时操作必须通过 Core `IBackgroundTaskRunner` 执行，UI 只消费状态并在 Dispatcher 更新。

## Acceptance Checklist

### Foundation

- [x] Independent solution builds with zero warnings.
- [x] Architecture tests enforce the Core-only project boundary.
- [x] All routes have unique keys and localized titles/descriptions in zh-CN and en-US.
- [x] Navigation creates pages through scoped resolution and replaces the current page.
- [x] Temporary Host starts against Core only and shows the shell.
- [ ] Manual review passes for window resize, maximize, keyboard focus, navigation, and drag overlay.
      （冒烟已过：Host 启动、模块切换、设置页语言双向切换即时生效；窗口尺寸/键盘/拖拽覆盖层仍需人工过目。）

### Feature Parity

- [x] Mod library import, scan, selection, enable/disable, grouping, tags, and search.
      （分组：新建/重命名/删除/按组过滤/行内归属下拉；标签：库内展示与手动赋值面板；
      搜索：`EnableFuzzySearch` 接入 `FuzzySearchMatcher`，名称拼音后台预热缓存。）
- [x] Deployment order, copy/symlink deployment, purge, progress, cancellation, and rollback reporting.
- [x] Create/edit/manifest/tag/auto-tag/Nexus tools.
- [x] Resource viewer/model preview/armor reuse/bisect diagnostics.
- [x] Settings/help/first-run experience and language switching.
      （保存设置即应用 `CurrentUICulture` 并刷新主壳标题与当前页头；页面静态文本随页面重建刷新；
      游戏目录未配置时库页显示首启引导。）

功能清单全部勾选并完成人工验收前，禁止把 `next/frontend/src/Helldivers2ModManager.Frontend` 迁出隔离目录或设为主程序默认 UI。
当前剩余门槛：上方 Foundation 人工验收项 + `ACCEPTANCE.md` 逐项签署。
