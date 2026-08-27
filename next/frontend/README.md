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

### Feature Parity

- [ ] Mod library import, scan, selection, enable/disable, grouping, tags, and search.
- [ ] Deployment order, copy/symlink deployment, purge, progress, cancellation, and rollback reporting.
- [ ] Create/edit/manifest/tag/auto-tag/Nexus tools.
- [ ] Resource viewer/model preview/armor reuse/bisect diagnostics.
- [ ] Settings/help/first-run experience and language switching.

功能清单全部勾选并完成人工验收前，禁止把 `next/frontend/src/Helldivers2ModManager.Frontend` 迁出隔离目录或设为主程序默认 UI。
