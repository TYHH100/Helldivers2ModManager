# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Helldivers 2 Mod Manager is a WPF application for managing game mods. It handles mod import, deployment, conflict detection, version compatibility checking, and provides preview features for models, audio, textures, and Lua scripts.

**Technology Stack**: C# / .NET 10 / WPF / MVVM (CommunityToolkit.Mvvm) / SQLite / SharpSevenZip

**Target Platform**: Windows only (`net10.0-windows` / `net10.0-windows7.0`)

## Build and Test Commands

All commands run from repository root:

```powershell
# Build solution
dotnet build Helldivers2ModManager.sln --configuration Debug

# Run tests
dotnet test tests/Helldivers2ModManager.Tests/Helldivers2ModManager.Tests.csproj --configuration Debug

# Publish main application (self-contained single file)
dotnet publish src/Helldivers2ModManager/Helldivers2ModManager.csproj `
  --configuration Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o publish

# Publish Purger utility
dotnet publish src/Purger/Purger.csproj --configuration Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o publish
```

**Note on Helldivers2PatchTool**: This standalone tool has special build requirements due to cross-referencing the main project. Its `ProjectReference` to Helldivers2ModManager uses `SetTargetFramework` to force `net10.0-windows` + `win-x64` (avoiding NETSDK1150 errors). Always verify publishing using the actual VS Publish Profile, not just CLI commands.

## Repository Structure

```
src/
  ├── Helldivers2ModManager/       # Main WPF application (all domain/service/UI code)
  ├── Helldivers2PatchTool/        # Standalone patch repair tool (references main project)
  └── Purger/                      # Standalone cleanup utility
tests/
  └── Helldivers2ModManager.Tests/ # MSTest unit tests
docs/                              # Documentation and mod_manifest_v1-schema.json
```

Within `Helldivers2ModManager/`:
- `Models/` - Domain models (ModData, Manifests, ModelPreview data structures)
- `Services/` - Business logic layer (subdivided by concern)
- `ViewModels/` - MVVM view models (large VMs split into `.partial.cs` files)
- `Views/` - XAML views
- `Components/` - Reusable WPF controls and overlays
- `Resources/` - Styles, localization JSON, fonts, data tables

## Architecture Principles

### Three-Layer Architecture

**Core (Domain Logic)** - No UI dependencies:
- `Models/` - Data structures and domain entities
- `Services/ModManagement/` - Mod lifecycle (import, deploy, update, conflict detection)
- `Services/VersionCheck/` - Game version compatibility checking and repair
- `Services/Parsing/` - Patch file parsing, resource inspection, Lua decompilation
- `Services/ModelPreview/` - 3D model preview backend and GPU skinning
- `Services/Search/` - Fuzzy search and filtering

**Infrastructure** - Persistence and cross-cutting concerns:
- `Services/Persistence/` - SQLite repositories (`DatabaseService` + `*Repository` classes)
- `Services/Infrastructure/` - Background tasks, game process detection, logging, settings
- `Services/Nexus/` - NexusMods API client

**Frontend** - UI layer:
- `Views/` + `ViewModels/` - Pages and their view models
- `Components/` - Custom WPF controls
- `Stores/` - Navigation and edit session state
- `Behaviors/`, `Converters/`, `Resources/`

**Dependency Direction**: Frontend → Domain Services → Infrastructure. Services and Models never reference ViewModels.

### Service Registration

Services use `[RegisterService(ServiceLifetime)]` attribute for automatic DI registration via reflection in `App.xaml.cs`:

```csharp
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class MyService { }
```

- Use `Singleton` for shared state/services; `Transient` for page ViewModels
- For interface + implementation sharing, use `Contract = typeof(IInterface)`
- See `Services/Nexus/` for contract registration examples

### Adding New Pages

**Three required steps** (missing any causes blank navigation):
1. ViewModel with `[RegisterService]` attribute
2. `DataTemplate` in `MainWindow.xaml` mapping ViewModel → View
3. Corresponding View (`.xaml`) file

**Memory Management**: Navigate via `NavigationStore.Navigate<T>()` which creates an `IServiceScope` per page. Never use injected `IServiceProvider.GetRequiredService<PageVM>()` + manual `Navigate(page)` - this causes the root DI container to hold `IDisposable` ViewModels (and their loaded models/textures) until process exit.

### MVVM and WPF Conventions

- ViewModels inherit from `ObservableObject` or project base classes
- Use CommunityToolkit's `[ObservableProperty]` and `[RelayCommand]` source generators
- Large ViewModels split into `.partial.cs` files by concern (e.g., `DashboardPageViewModel.{Import,Deploy,Scans}.cs`)
- Business logic in ViewModels/Services, not code-behind
- Background threads must never directly modify WPF collections or bound properties - use `BackgroundTaskService` or `Dispatcher`
- **Large lists need virtualization**: Never use bare `StackPanel` as items host with thousands of items (ComboBox drop-down templates are non-virtualized). Audio/text lists use virtualized ListBox + ListCollectionView. Animation list uses custom `Components/VirtualizedTextPicker` (code-driven virtualization, not ItemsControl-based).

### Localization

- Runtime localization via `LocalizationService` (injected in code)
- XAML uses `{loc:Loc Key}` markup extension
- Resources in `Resources/Language/*.json` (zh-CN, en-US)
- Key naming: `Section.Key` pattern
- Always update both language files when adding strings

### Settings Extension Protocol

Adding a new setting requires **four synchronized changes**:

1. **SettingsService**: Add `[JsonInclude] private` field with default value, public property with `GuardInitialized`/`GuardReadonly`, register in `CreateJsonModel`, `ReadAsyncFallback`, and `ResetInternal`
2. **SettingsPageViewModel.Properties.cs**: Add validated property, call `OnPropertyChanged` in `Update()`
3. **Views/SettingsPageView.xaml**: Add `FluentSettingsCard` in appropriate Tab
4. **Resources/Language/{zh-CN,en-US}.json**: Add localized strings

**Common pitfall**: Forgetting `ReadAsyncFallback` registration causes old users to always see `false` default after upgrade.

## Mod Data Flow

### Mod Storage and Profiles

- **Mod Library**: All imported mods stored in `{StorageDirectory}/Mods/`, each in its own directory with `manifest.json`
- **Profiles (Groups)**: Named collections that reference mods by GUID. Each profile tracks enabled state, option selections, and deployment order.
- **Default vs Custom Profiles**: Default profile's `ModGuids` is also dynamically persisted (not "all mods by default"). Both profile types filter display by `ModGuids` membership.

### Display Order and Persistence

**Display order authority**: `ModGroupService.FilterMods/FilterModViewModels` output

- **Default profile**: Order = `_mods` list (from `enabled_mods.SortOrder`). Drag-drop reorders `_mods` and persists via snapshot.
- **Custom profiles**: Drag-drop rebuilds `SelectedGroup.ModGuids` order. `FilterMods` **must output members in `ModGuids` order** for custom profiles (not `_mods` order, which would cause display to "reset" on any list rebuild).
- **Deployment order**: Non-default profiles use profile's display order as deployment order.

**Persistence timing**: After import (including auto-add to profile), call `SaveProfileNowAsync(showProgress: false)` immediately rather than relying on 300ms debounce. Crash/force-kill can lose pending snapshots, causing newly imported mods to appear at end in file-system enumeration order on restart.

### Current Profile Selection Persistence

**Storage**: `app_state` table (key `last_selected_group_id`) managed by `ModGroupRepository`

**Update points** (all three must save immediately):
1. `ModGroupService.InitAsync` - restores last selection (falls back to default if missing/invalid)
2. `SelectGroupAsync` - saves after switching profiles
3. `DeleteGroupAsync` - saves after deleting current profile (falls back to default)

Do not store this in `SettingsService` (not a settings-page concern, shouldn't be cleared by "reset settings"). Do not add columns to `mod_groups` (uses DELETE+full-reinsert pattern, causes read-modify-write race).

## Data Parsing Constraints

### Patch File Structure

Typical mod patch files:
```
{16-char-lowercase-hex}.patch_{index}
{same-name}.patch_{index}.gpu_resources
{same-name}.patch_{index}.stream
```

**Critical constraints**:
- `.gpu_resources` files are multi-GB - never read entirely into memory
- Use `FileStream` with random access for bounded structure reads
- All offsets use `long`/`ulong`; check `offset + size` for overflow and bounds before every read
- Unknown/unverifiable structures log warnings with reasons; never guess or auto-fix without evidence

### Manifest Versions

- **Legacy**: Single option dropdown (mutually exclusive choices)
- **V1**: Multiple independent options with enable/disable toggles
- Must maintain compatibility for both formats
- Empty options array `"Options": []` in V1 manifests means "deploy root directory patches" (same as no options) - do not treat as zero patches

### Model Preview Data

- Preview must parse real MeshInfo, resource tables, offsets, vertex layouts from mod patches
- Geometry issues require fixing the parse chain, not shape guessing or hardcoded patches
- GPU/texture reads need limits, cancellation, and caching
- Texture preview: detect usage from actual channel statistics; preserve RGB/RGBA/Alpha display modes
- **Material variants**: Dedupe sections with same `(MeshInfoIndex, VertexOffset, VertexCount, IndexCount)` - do NOT include `IndexOffset` in deduplication key (different IndexOffset = same geometry, different material)
- **Pure black placeholder materials**: Sample compressed blocks from multiple positions (start, quartiles, mid, end), decode limited thumbnail, confirm all pixels are `(0,0,0,255)`. Skip only when non-black sections exist in same stream.

### Audio/Text Mod Previews

- **Audio**: Parse Wwise bank structure (BKHD/DIDX/DATA chunks). Never load entire DATA chunk or all media into memory. Decode with Ww2Ogg.Core using **AoTuV codebook first** (HD2 audio), fall back to Default. Audio lists must use virtualized ListBox + ListCollectionView (not ItemsControl).
- **Text**: Parse TEXT_BANK resources. Entire text banks can be cached (1-2MB vs audio's GBs).
- Both rely on `GameAudioBaseline` for "original/replaced/new" status via SHA-256 comparison (metadata only, not full media arrays).

### Lua Script Static Restoration

- **Security red line**: Pure static parsing only (bytes→structure→text). Never load/compile/execute/eval Lua. No Lua VM bindings.
- Format: LuaJIT dump (magic `1B 4C 4A`). Nested dumps hide in string constants - recursively extract.
- Output: Faithful decompilation (best-effort) + authoritative listing (luajit -bl style) + string summary + safety declaration
- Extract writes `.bin`, `.luajit`, `.decompiled.lua`, `.listing.txt`, `.strings.txt`, `report.txt` + README

## Background Tasks

Use `BackgroundTaskService.RunAsync(...)` for all long-running CPU-intensive operations:

```csharp
var result = await _backgroundTaskService.RunAsync(
    "TaskName",
    "Description",
    async (task, context, ct) =>
    {
        // Work runs on background thread (inside Task.Run)
        // Update progress: context.Report(progress, "status");
        return resultData;
    },
    isForeground: false  // true = has dedicated progress dialog, auto-removed on completion
);
// Apply result on UI thread here
```

**Key points**:
- `RunAsync` handles background thread execution (`Task.Run` internally) and manages task lifecycle (`Add` → `Complete`/`Fail`/`Cancel`)
- Work delegate runs on background thread - never directly modify WPF collections or bound properties inside it
- Use `context.Report(...)` to update task description/progress (auto-dispatches to UI thread)
- Apply results after `await` on UI thread
- `isForeground: true` for operations with dedicated progress dialogs (deploy, import, delete, repair) - not shown in task page, auto-removed when complete
- `isForeground: false` (default) for silent background operations (hash calculation, version check, conflict scan) - shown in task page

**Do not** manually write `Add` + `Task.Run` + `Complete/Fail` boilerplate. `await` async methods only yields on I/O; synchronous CPU-intensive work (LZ4 decode, SHA-256, compression, large file parsing loops) still blocks the calling thread.

## High-Risk Operations and Safety

### Game Process Guard

Deployment and cleanup write to game directory - blocked if `helldivers2.exe` is running via `GameProcessService.IsGameRunning()` check. Deletion uses `RequiresGameClosedForRemoval()` per-mod conditional guard.

### Destructive Operations

- Mod deletion: Move to recycle bin when possible (`FileSystem.DeleteDirectory(..., UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`)
- Version check repairs: Save backup metadata before modifying, restore on failure
- Never commit temporary files, publish directories, or test artifacts

### File Operations Constraints

- Large file copies use bounded parallel (`Parallel.ForEachAsync` / `Parallel.ForEach` with `MaxDegreeOfParallelism = Math.Clamp(ProcessorCount/2, 2, 4)`)
- Use Windows kernel `File.Copy(..., true)` (CopyFile2) not managed `CopyToAsync` (slower, higher overhead)
- Symlink deployment branch uses `File.CreateSymbolicLink`
- Manual stream copy buffer: 81920 bytes

### Nested Archive Import and AV Locks

**Windows Defender / AV real-time scanning** can hold brief exclusive locks on files just created by SharpSevenZip. Symptoms: `IOException` 0x80070020 (sharing violation) during nested archive extraction or temp cleanup, especially when "outer.zip contains inner.zip with same name".

Fix: `ModService.ExtractArchiveAsync` and `TryDeleteTemporaryDirectory` retry **only** `ERROR_SHARING_VIOLATION (0x80070020)` and `ERROR_LOCK_VIOLATION (0x80070021)` with linear backoff (extract: 6 retries/≤3s total, cleanup: 4 retries/≤0.6s). Other errors (password, CRC, file not found) fail immediately.

## Testing Guidance

### Regression Tests

- `ModelPreviewArmorSelectionTests` - armor set merging (body+helmet with same name)
- `ModelPreviewAttachedSkinningTests` - attached parts skinning (non-BoneIndex meshes)
- `LuaScriptInspectionServiceTests` - Lua decompilation with real Bingus fixture
- `ModGroupServiceLastSelectedGroupTests` - profile selection persistence

### WPF UI Tests in MSTest

MSTest doesn't load WPF default theme styles. UI tests must:
- Manually construct explicit `ControlTemplate` using `FrameworkElementFactory`
- Call `Measure(Size)` and `Arrange(Rect)` to establish visual parent-child relationships
- Access template-scoped elements via `Template.FindName("name", this)` not `x:Name` fields

### Diagnostic Commands

After sensitive changes:
```powershell
# Check for leftover references after deletion
rg "OldClassName"

# Verify localization key usage
rg '{loc:Loc SomeKey}'
rg '_localizationService\["SomeKey"\]'

# Ensure no temporary files committed
git status --short
```

## Common Pitfalls

1. **Profile order "resets itself"**: Custom profiles must filter output in `ModGuids` order, not `_mods` order
2. **New page shows blank**: Missing `DataTemplate` in `MainWindow.xaml`
3. **Settings toggle doesn't work**: Forgot to register in `ReadAsyncFallback` or `ResetInternal`
4. **Current profile not restored**: Not persisting `last_selected_group_id` to `app_state` table
5. **Preview shows black model**: Material variant deduplication includes `IndexOffset` in key (should exclude it)
6. **Audio lists freeze UI**: Using ItemsControl+StackPanel instead of virtualized ListBox
7. **GPU reads cause OOM**: Reading entire `.gpu_resources` into memory instead of bounded FileStream access
8. **WEM decode fails silently**: Using Default codebook instead of AoTuV for HD2 audio
9. **Import fails with sharing violation**: AV locks on extracted files - add retry logic for 0x80070020
10. **Page doesn't dispose**: Navigating via root `IServiceProvider` instead of `NavigationStore.Navigate<T>()`

## File Locations Reference

- **Mod manifests**: `{StorageDirectory}/Mods/{ModGuid}/manifest.json`
- **Settings**: Program directory `settings.json`
- **Database**: `{StorageDirectory}/state.db` (profiles, hashes, links, version checks)
- **Logs**: Program directory `logs/` (auto-cleaned per `MaxLogFiles`)
- **Archive extraction temp**: `{TempDirectory}/_extract_{guid}/`
- **Localization**: `Resources/Language/zh-CN.json`, `en-US.json`
- **Data tables**: `Resources/Data/armor-names.json`, `helmet-names.json`, `animation-names.json`, etc.

## Additional Documentation

See `AGENTS.md` for detailed development guidance covering:
- Patch/GPU/Stream parsing rules and Unit version handling
- Model preview architecture (orientation detection, animation binding, skinning, materials)
- Audio/text/Lua preview constraints
- Version check and repair workflows
- Profile/group management implementation details
- Comprehensive pitfall catalog with 60+ failure modes
