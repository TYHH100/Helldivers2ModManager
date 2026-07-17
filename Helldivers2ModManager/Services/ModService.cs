using Helldivers2ModManager.Exceptions;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Core.Archives;
using Helldivers2ModManager.Core.Security;
using Helldivers2ModManager.Core.TemporaryFiles;
using Helldivers2ModManager.Core.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed partial class ModService
{
    private readonly struct PatchFileTriplet
    {
        public FileInfo? Patch { get; init; }

        public FileInfo? GpuResources { get; init; }

        public FileInfo? Stream { get; init; }
    }

    private sealed class ArchiveImportContext
    {
        public int NestedArchiveCount { get; set; }
    }

    [MemberNotNullWhen(true, nameof(_settingsService))]
    public bool Initialized { get; private set; }

    public IReadOnlyList<ModData> Mods => _mods;

    public event Action<ModData>? ModAdded;

    public event Action<ModData>? ModRemoved;

    private readonly ILogger<ModService> _logger;
    private readonly List<ModData> _mods;
    private readonly FileHashRepository _fileHashRepository;
    private readonly ModHashService _modHashService;
    private readonly LocalizationService _localizationService;
    private readonly IArchiveInspector _archiveInspector;
    private readonly ISafePathPolicy _safePathPolicy;
    private readonly IOperationWorkspaceManager _workspaceManager;
    private readonly IModImportService _modImportService;
    private SettingsService? _settingsService;

    public ModService(
        ILogger<ModService> logger,
        FileHashRepository fileHashRepository,
        ModHashService modHashService,
        LocalizationService localizationService,
        IArchiveInspector archiveInspector,
        ISafePathPolicy safePathPolicy,
        IOperationWorkspaceManager workspaceManager,
        IModImportService modImportService)
    {
        _logger = logger;
        _fileHashRepository = fileHashRepository;
        _modHashService = modHashService;
        _localizationService = localizationService;
        _archiveInspector = archiveInspector;
        _safePathPolicy = safePathPolicy;
        _workspaceManager = workspaceManager;
        _modImportService = modImportService;
        _mods = new();
    }

    public async Task<ModProblem[]> InitAsync(SettingsService settings, CancellationToken cancellationToken)
    {
        var problems = await Task.Run(() => InitCore(settings), cancellationToken);
        _modHashService.Init(settings);
        await _modHashService.MigrateExistingModsAsync(_mods, cancellationToken);
        return problems;
    }

    private ModProblem[] InitCore(SettingsService settings)
    {
        if (Initialized)
            return [];

        if (!settings.Validate())
            throw new ArgumentException("Settings are invalid!", nameof(settings));

        var problems = new List<ModProblem>();

        _settingsService = settings;
        _logger.LogInformation("Initializing mod service");

        var modsDir = new DirectoryInfo(Path.Combine(_settingsService.StorageDirectory, "Mods"));
        _modImportService.RecoverInterruptedImportsAsync(modsDir.FullName, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        _logger.LogDebug("Checking \"Mods\" directroy existance");
        if (modsDir.Exists)
            _logger.LogDebug("Found \"Mods\" directory");
        else
        {
            _logger.LogDebug("Creating \"Mods\" directory");
            modsDir.Create();
        }

        var dirs = modsDir.GetDirectories();
        _logger.LogInformation("Found {} folders in \"Mods\" directory", dirs.Length);

        foreach (var dir in dirs)
        {
            _logger.LogDebug("Processing \"{}\"", dir.FullName);

            _logger.LogDebug("Checking for \"manifest.json\"");
            var manifestFile = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
            if (manifestFile.Exists)
            {
                IModManifest manifest;

                try
                {
                    _logger.LogDebug("Parsing manifest");
                    manifest = ModManifest.DeserializeFromFile(manifestFile);
                }
                catch (UnknownManifestVersionException)
                {
                    _logger.LogError("Manifest \"{}\" has unknown", manifestFile.FullName);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.UnknownManifestVersion,
                    });
                    continue; // skip
                }
                catch (EndOfLifeException)
                {
                    _logger.LogError("Manifest \"{}\" is unsupported version 2", manifestFile.FullName);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.OutOfSupportManifest,
                    });
                    continue; // skip
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to parse manifest \"{}\"", manifestFile.FullName);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.CantParseManifest,
                    });
                    continue; // skip
                }

                if (_mods.Any(data => data.Manifest.Guid == manifest.Guid))
                {
                    _logger.LogError("Mod \"{}\" has a duplicate guid of \"{}\"", dir.FullName, manifest.Guid);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.Duplicate,
                    });
                    continue; // skip
                }

                if (!CheckPaths(manifest, problems, dir, manifestFile))
                    continue;

                _mods.Add(new ModData(dir, manifest));
            }
            else
            {
                _logger.LogWarning("No manifest found in \"{}\", deleting", dir.FullName);
                problems.Add(new ModProblem
                {
                    Directory = dir,
                    Kind = ModProblemKind.NoManifestFound,
                });
                dir.Delete(true);
            }
        }

        Initialized = true;
        _logger.LogInformation("Loaded {} mods", _mods.Count);
        _logger.LogInformation("Mod service initialization complete");

        return problems.ToArray();
    }

    /// <summary>
    /// 从源目录创建并添加模组。将源目录内容复制到存储目录，
    /// 根据目录结构自动推断清单格式，并使用用户指定的名称和描述。
    /// </summary>
    /// <param name="sourceDir">源目录，包含模组文件</param>
    /// <param name="modName">模组显示名称</param>
    /// <param name="modDescription">模组描述</param>
    /// <param name="customOptions">用户自定义的选项列表（可选，为 null 时自动推断）</param>
    /// <param name="iconPath">模组图标路径（可选，仅文件名部分会写入清单）</param>
    /// <returns>遇到的问题列表</returns>
    public async Task<ModProblem[]> TryAddModFromDirectoryAsync(
        DirectoryInfo sourceDir, string modName, string modDescription,
        List<ModOption>? customOptions = null, string? iconPath = null)
    {
        GuardInitialized();

        var problems = new List<ModProblem>();

        _logger.LogInformation("Attempting to add mod from directory \"{}\"", sourceDir.FullName);

        if (!sourceDir.Exists)
        {
            problems.Add(new ModProblem
            {
                Directory = sourceDir,
                Kind = ModProblemKind.InvalidPath,
            });
            return problems.ToArray();
        }

        // 根据源目录结构推断清单
        var manifest = ModManifest.InferFromDirectory(sourceDir, _logger);

        // 使用用户输入的名称和描述覆盖推断值
        var finalName = !string.IsNullOrWhiteSpace(modName) ? modName : manifest.Name;
        var finalDescription = !string.IsNullOrWhiteSpace(modDescription) ? modDescription : manifest.Description;

        // 确定图标路径：优先使用用户指定的图标，否则使用推断的图标
        var finalIconPath = !string.IsNullOrWhiteSpace(iconPath)
            ? Path.GetFileName(iconPath)
            : manifest.IconPath;

        // 构建目标目录路径（安全校验：防止路径遍历）
        var safeName = Path.GetFileName(finalName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            _logger.LogError("Invalid mod name after sanitization: {Name}", finalName);
            problems.Add(new ModProblem
            {
                Directory = sourceDir,
                Kind = ModProblemKind.InvalidPath,
                ExtraData = "Invalid mod name",
            });
            return problems.ToArray();
        }

        var modsBasePath = Path.GetFullPath(Path.Combine(_settingsService.StorageDirectory, "Mods"));
        var modDir = new DirectoryInfo(Path.Combine(modsBasePath, safeName));

        // 验证最终路径是否在 Mods 目录内
        if (!modDir.FullName.StartsWith(modsBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && modDir.FullName != modsBasePath)
        {
            _logger.LogError("Path traversal attempt detected: {Path}", modDir.FullName);
            problems.Add(new ModProblem
            {
                Directory = sourceDir,
                Kind = ModProblemKind.InvalidPath,
                ExtraData = "Path traversal not allowed",
            });
            return problems.ToArray();
        }

        if (modDir.Exists)
        {
            _logger.LogWarning("Mod directory already exists: {}", modDir.FullName);
            problems.Add(new ModProblem
            {
                Directory = modDir,
                Kind = ModProblemKind.Duplicate,
            });
            return problems.ToArray();
        }

        // 复制源目录内容到存储目录
        modDir.Parent?.Create();
        await Task.Run(() => sourceDir.CopyTo(modDir.FullName));

        // 创建清单文件：如果用户提供了自定义选项，则使用 V1 格式；否则根据推断结果决定
        IModManifest finalManifest;
        if (customOptions is { Count: > 0 })
        {
            // 用户自定义了选项，使用 V1 格式清单
            finalManifest = new V1ModManifest
            {
                Guid = manifest.Guid,
                Name = finalName,
                Description = finalDescription,
                IconPath = finalIconPath,
                Options = customOptions,
            };
        }
        else
        {
            // 没有自定义选项，根据推断结果决定清单格式
            finalManifest = manifest.Version switch
            {
                ManifestVersion.V1 => new V1ModManifest
                {
                    Guid = manifest.Guid,
                    Name = finalName,
                    Description = finalDescription,
                    IconPath = finalIconPath,
                    Options = (manifest as V1ModManifest)?.Options,
                },
                _ => new LegacyModManifest
                {
                    Guid = manifest.Guid,
                    Name = finalName,
                    Description = finalDescription,
                    IconPath = finalIconPath,
                    Options = (manifest as LegacyModManifest)?.Options,
                },
            };
        }

        ModManifest.SaveToFile(finalManifest, modDir);

        _logger.LogInformation("Adding mod");
        var mod = new ModData(modDir, finalManifest);
        _mods.Add(mod);
        ModAdded?.Invoke(mod);

        _logger.LogInformation("Mod created successfully: {}", finalName);
        return problems.ToArray();
    }

    /// <summary>
    /// 尝试从压缩包添加模组
    /// </summary>
    /// <param name="file">压缩包文件</param>
    /// <param name="nestedProgress">嵌套压缩包处理进度回调：(当前序号(0-based), 总数, 当前文件名)，仅在检测到嵌套压缩包时调用</param>
    public Task<ModProblem[]> TryAddModFromArchiveAsync(
        FileInfo file,
        Action<int, int, string>? nestedProgress = null,
        CancellationToken cancellationToken = default)
    {
        return TryAddModFromArchiveCoreAsync(
            file,
            nestedProgress,
            new ArchiveImportContext(),
            depth: 0,
            cancellationToken);
    }

    private async Task<ModProblem[]> TryAddModFromArchiveCoreAsync(
        FileInfo file,
        Action<int, int, string>? nestedProgress,
        ArchiveImportContext importContext,
        int depth,
        CancellationToken cancellationToken)
    {
        GuardInitialized();

        var problems = new List<ModProblem>();

        _logger.LogInformation("Attempting to add mod from \"{}\"", file.Name);

        using var workspace = _workspaceManager.Create(_settingsService.TempDirectory, "mod-import");
        var tmpDir = new DirectoryInfo(workspace.DirectoryPath);
        _logger.LogInformation("Created owned import workspace \"{}\"", tmpDir.FullName);

        _logger.LogInformation("Extracting archive using SharpSevenZip");
        try
        {
            await ExtractArchiveSafelyAsync(file, tmpDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract archive \"{}\"", file.Name);
            tmpDir.Delete(true);
            problems.Add(new ModProblem
            {
                Directory = tmpDir,
                Kind = ModProblemKind.CantReadArchive,
                ExtraData = ex.Message,
            });
            return problems.ToArray();
        }

        _logger.LogDebug("Checking for unnecessary root folder in extracted archive");
        var rootFolders = tmpDir.GetDirectories();
        var rootFiles = tmpDir.GetFiles();

        if (rootFolders.Length == 1 && rootFiles.Length == 0)
        {
            var rootFolder = rootFolders[0];
            _logger.LogInformation("Detected root folder \"{}\", flattening structure", rootFolder.Name);

            await MoveDirectoryContentsAsync(rootFolder, tmpDir);
            rootFolder.Delete(true);
            _logger.LogDebug("Root folder flattened successfully");
        }

        // 检测嵌套压缩包场景：一级压缩包中未直接包含模组清单文件，但包含其他压缩包（文件夹嵌套结构）
        // 此时应以嵌套压缩包作为主要导入对象，支持批量导入所有符合条件的嵌套压缩包
        var manifestFile = new FileInfo(Path.Combine(tmpDir.FullName, "manifest.json"));
        var nestedArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".zip", ".7z", ".rar", ".tar" };

        if (!manifestFile.Exists)
        {
            // 递归搜索所有嵌套压缩包（支持文件夹嵌套结构中的压缩包）
            var nestedArchives = tmpDir.GetFiles("*", System.IO.SearchOption.AllDirectories)
                .Where(f => nestedArchiveExtensions.Contains(f.Extension))
                .ToArray();

            if (nestedArchives.Length > 0)
            {
                if (depth >= ArchiveSafetyLimits.Default.MaximumNestedDepth ||
                    importContext.NestedArchiveCount + nestedArchives.Length > ArchiveSafetyLimits.Default.MaximumNestedArchives)
                {
                    problems.Add(new ModProblem
                    {
                        Directory = tmpDir,
                        Kind = ModProblemKind.CantReadArchive,
                        ExtraData = "Archive nesting safety limit exceeded."
                    });
                    tmpDir.Delete(true);
                    return problems.ToArray();
                }
                importContext.NestedArchiveCount += nestedArchives.Length;

                _logger.LogInformation("一级压缩包中未发现 manifest.json，但检测到 {Count} 个嵌套压缩包，将以嵌套压缩包作为导入对象进行批量导入", nestedArchives.Length);

                var allNestedProblems = new List<ModProblem>();

                for (int i = 0; i < nestedArchives.Length; i++)
                {
                    var nestedArchive = nestedArchives[i];

                    // 向调用方汇报嵌套导入进度（当前序号, 总数, 当前文件名）
                    nestedProgress?.Invoke(i, nestedArchives.Length, nestedArchive.Name);

                    _logger.LogInformation("开始处理嵌套压缩包 ({Current}/{Total}): {Name}", i + 1, nestedArchives.Length, nestedArchive.Name);
                    try
                    {
                        // 递归处理嵌套压缩包，传递同一进度回调以支持多层嵌套的进度上报
                        var nestedProblems = await TryAddModFromArchiveCoreAsync(
                            nestedArchive,
                            nestedProgress,
                            importContext,
                            depth + 1,
                            cancellationToken);
                        allNestedProblems.AddRange(nestedProblems);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "处理嵌套压缩包失败: {Name}", nestedArchive.Name);
                        allNestedProblems.Add(new ModProblem
                        {
                            Directory = tmpDir,
                            Kind = ModProblemKind.CantReadArchive,
                            ExtraData = $"{nestedArchive.Name}: {ex.Message}",
                        });
                    }
                }

                // 清理包装压缩包的临时目录（嵌套压缩包已被递归提取到各自临时目录并完成导入）
                tmpDir.Delete(true);
                _logger.LogInformation("嵌套压缩包批量导入完成，共处理 {Count} 个", nestedArchives.Length);

                return allNestedProblems.ToArray();
            }
        }

        IModManifest manifest;
        if (manifestFile.Exists)
        {
            manifest = ModManifest.DeserializeFromFile(manifestFile);

            if (!CheckPaths(manifest, problems, tmpDir, manifestFile))
            {
                tmpDir.Delete(true);
                return problems.ToArray();
            }
        }
        else
        {
            problems.Add(new ModProblem
            {
                Directory = tmpDir,
                Kind = ModProblemKind.NoManifestFound,
            });
            manifest = ModManifest.InferFromDirectory(tmpDir);

            using var stream = manifestFile.Open(FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                IndentCharacter = '\t',
                Indented = true,
                IndentSize = 1,
            });
            manifest.Serialize(writer);
            await writer.DisposeAsync();
        }

        _logger.LogInformation("Moving mod to storage");
        var modsBasePath = Path.Combine(_settingsService.StorageDirectory, "Mods");
        var planResult = await _modImportService.PlanImportAsync(
            manifest.Guid,
            manifest.Name,
            tmpDir.FullName,
            modsBasePath,
            cancellationToken);
        if (!planResult.IsSuccess || planResult.Value is null)
        {
            problems.Add(new ModProblem
            {
                Directory = tmpDir,
                Kind = planResult.ErrorCode is "Import.NameConflict" or "Import.UpdateConfirmationRequired"
                    ? ModProblemKind.Duplicate
                    : ModProblemKind.InvalidPath,
                ExtraData = planResult.ErrorCode
            });
            return problems.ToArray();
        }

        var plan = planResult.Value;
        var commitResult = await _modImportService.CommitImportAsync(
            plan,
            updateConfirmed: false,
            progress: null,
            cancellationToken);
        if (!commitResult.IsSuccess || commitResult.Value is null)
        {
            problems.Add(new ModProblem
            {
                Directory = tmpDir,
                Kind = commitResult.ErrorCode is "Import.NameConflict" or "Import.UpdateConfirmationRequired"
                    ? ModProblemKind.Duplicate
                    : ModProblemKind.InvalidPath,
                ExtraData = commitResult.ErrorCode
            });
            return problems.ToArray();
        }

        var existingMod = _mods.FirstOrDefault(item => item.Manifest.Guid == manifest.Guid);
        var existingIndex = existingMod is null ? -1 : _mods.IndexOf(existingMod);
        var modDir = new DirectoryInfo(commitResult.Value.DestinationDirectory);
        var mod = new ModData(modDir, manifest);
        try
        {
            if (existingIndex >= 0)
                _mods[existingIndex] = mod;
            else
                _mods.Add(mod);
            await _modHashService.RecomputeForUpdatedModAsync(mod);
            await _modImportService.CompleteImportAsync(plan, commit: true, cancellationToken);
        }
        catch
        {
            if (existingIndex >= 0 && existingMod is not null)
                _mods[existingIndex] = existingMod;
            else
                _mods.Remove(mod);
            await _modImportService.CompleteImportAsync(plan, commit: false, CancellationToken.None);
            throw;
        }

        if (existingMod is not null)
            ModRemoved?.Invoke(existingMod);
        ModAdded?.Invoke(mod);
        return problems.ToArray();
    }

    public async Task RemoveAsync(ModData mod, CancellationToken cancellationToken = default)
    {
        GuardInitialized();

        _logger.LogInformation("Attempting to remove {}", mod.Manifest.Guid);

        // 使用 GUID 查找而不是引用相等性，避免因 ModData 引用不匹配导致删除失败
        var index = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
        if (index < 0)
        {
            _logger.LogInformation("Removal unsuccessful");
            return;
        }
        var removedMod = _mods[index];
        var modsRoot = Path.Combine(_settingsService.StorageDirectory, "Mods");
        var removalRoot = _safePathPolicy.ResolveUnderRoot(
            modsRoot,
            Path.Combine(".transactions", "removals", $"{removedMod.Manifest.Guid:N}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(Path.GetDirectoryName(removalRoot)!);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(removedMod.Directory.FullName, removalRoot);

        try
        {
            await _modHashService.DeleteForModAsync(removedMod);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            if (!Directory.Exists(removedMod.Directory.FullName) && Directory.Exists(removalRoot))
                Directory.Move(removalRoot, removedMod.Directory.FullName);
            throw;
        }

        _mods.RemoveAt(index);
        ModRemoved?.Invoke(removedMod);

        var recycleOption = _settingsService.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
        await Task.Run(
            () => FileSystem.DeleteDirectory(removalRoot, UIOption.OnlyErrorDialogs, recycleOption),
            CancellationToken.None);

        _logger.LogInformation("Mod {} removed", removedMod.Manifest.Name);
    }

    /// <summary>
    /// 重新扫描 Mods 目录，检测并加载新增的模组（例如从回收站恢复的文件）。
    /// 已存在的模组不会被重新加载。
    /// </summary>
    public ModProblem[] RescanMods()
    {
        GuardInitialized();

        var problems = new List<ModProblem>();
        var addedCount = 0;

        _logger.LogInformation("Rescanning Mods directory...");

        var modsDir = new DirectoryInfo(Path.Combine(_settingsService.StorageDirectory, "Mods"));
        if (!modsDir.Exists)
        {
            _logger.LogInformation("Mods directory does not exist, nothing to rescan");
            return problems.ToArray();
        }

        var dirs = modsDir.GetDirectories();
        _logger.LogInformation("Found {} folders in Mods directory", dirs.Length);

        foreach (var dir in dirs)
        {
            var existingMod = _mods.FirstOrDefault(m => m.Directory.FullName == dir.FullName);
            if (existingMod != null)
            {
                _logger.LogDebug("Mod \"{}\" already loaded, skipping", dir.FullName);
                continue;
            }

            _logger.LogDebug("Processing new folder \"{}\"", dir.FullName);

            var manifestFile = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
            if (manifestFile.Exists)
            {
                IModManifest manifest;

                try
                {
                    manifest = ModManifest.DeserializeFromFile(manifestFile);
                }
                catch (UnknownManifestVersionException)
                {
                    _logger.LogError("Manifest \"{}\" has unknown version", manifestFile.FullName);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.UnknownManifestVersion,
                    });
                    continue;
                }
                catch (EndOfLifeException)
                {
                    _logger.LogError("Manifest \"{}\" is unsupported version 2", manifestFile.FullName);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.OutOfSupportManifest,
                    });
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to parse manifest \"{}\"", manifestFile.FullName);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.CantParseManifest,
                    });
                    continue;
                }

                if (_mods.Any(data => data.Manifest.Guid == manifest.Guid))
                {
                    _logger.LogError("Mod \"{}\" has a duplicate guid of \"{}\"", dir.FullName, manifest.Guid);
                    problems.Add(new ModProblem
                    {
                        Directory = dir,
                        Kind = ModProblemKind.Duplicate,
                    });
                    continue;
                }

                if (!CheckPaths(manifest, problems, dir, manifestFile))
                    continue;

                var mod = new ModData(dir, manifest);
                _mods.Add(mod);
                ModAdded?.Invoke(mod);
                addedCount++;

                _logger.LogInformation("Added recovered mod \"{}\" ({})", manifest.Name, manifest.Guid);

                _modHashService.QueueComputeAndStoreForMod(mod);
            }
            else
            {
                _logger.LogWarning("No manifest found in \"{}\"", dir.FullName);
                problems.Add(new ModProblem
                {
                    Directory = dir,
                    Kind = ModProblemKind.NoManifestFound,
                });
            }
        }

        _logger.LogInformation("Rescan complete, {} new mods added", addedCount);
        return problems.ToArray();
    }

    /// <summary>
    /// 上移模组（在列表中向上移动一位）
    /// </summary>
    public bool MoveModUp(ModData mod)
    {
        GuardInitialized();

        var index = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
        if (index <= 0) return false; // 已经在最上面或找不到

        _mods.RemoveAt(index);
        _mods.Insert(index - 1, mod);

        _logger.LogInformation("Mod {} moved up (index: {} -> {})", mod.Manifest.Name, index, index - 1);
        return true;
    }

    /// <summary>
    /// 下移模组（在列表中向下移动一位）
    /// </summary>
    public bool MoveModDown(ModData mod)
    {
        GuardInitialized();

        var index = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
        if (index < 0 || index >= _mods.Count - 1) return false; // 已经在最下面或找不到

        _mods.RemoveAt(index);
        _mods.Insert(index + 1, mod);

        _logger.LogInformation("Mod {} moved down (index: {} -> {})", mod.Manifest.Name, index, index + 1);
        return true;
    }

    /// <summary>
    /// 将模组移动到指定位置（用于拖拽排序）
    /// </summary>
    public bool MoveModTo(ModData mod, int newIndex)
    {
        GuardInitialized();

        var oldIndex = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
        if (oldIndex < 0) return false;

        // 确保新索引在有效范围内
        newIndex = Math.Max(0, Math.Min(newIndex, _mods.Count - 1));

        if (oldIndex == newIndex) return false;

        _mods.RemoveAt(oldIndex);
        _mods.Insert(newIndex, mod);

        _logger.LogInformation("Mod {} moved to index {}", mod.Manifest.Name, newIndex);
        return true;
    }

    /// <summary>
    /// 交换两个模组的位置
    /// </summary>
    public bool SwapMods(ModData mod1, ModData mod2)
    {
        GuardInitialized();

        var index1 = _mods.FindIndex(m => m.Manifest.Guid == mod1.Manifest.Guid);
        var index2 = _mods.FindIndex(m => m.Manifest.Guid == mod2.Manifest.Guid);

        if (index1 < 0 || index2 < 0) return false;

        (_mods[index1], _mods[index2]) = (_mods[index2], _mods[index1]);

        _logger.LogInformation("Swapped mods: {} <-> {}", mod1.Manifest.Name, mod2.Manifest.Name);
        return true;
    }

    public async Task UpdateModFromArchiveAsync(
        ModData mod,
        FileInfo archive,
        IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GuardInitialized();
        progress?.Report(new UpdateProgressInfo
        {
            Phase = UpdatePhase.Extracting,
            Message = _localizationService["ModService.ExtractingArchive"]
        });
        using var workspace = _workspaceManager.Create(_settingsService.TempDirectory, "mod-update-transaction");
        var extractedDirectory = new DirectoryInfo(workspace.DirectoryPath);
        await ExtractArchiveSafelyAsync(archive, extractedDirectory, cancellationToken);

        var rootFolders = extractedDirectory.GetDirectories();
        var rootFiles = extractedDirectory.GetFiles();
        if (rootFolders.Length == 1 && rootFiles.Length == 0)
        {
            await MoveDirectoryContentsAsync(rootFolders[0], extractedDirectory);
            rootFolders[0].Delete(recursive: true);
        }

        var manifestFile = new FileInfo(Path.Combine(extractedDirectory.FullName, "manifest.json"));
        IModManifest manifest;
        if (manifestFile.Exists)
        {
            manifest = ModManifest.DeserializeFromFile(manifestFile);
        }
        else
        {
            manifest = ModManifest.InferFromDirectory(extractedDirectory);
            manifest = manifest switch
            {
                LegacyModManifest legacy => new LegacyModManifest
                {
                    Guid = mod.Manifest.Guid,
                    Name = legacy.Name,
                    Description = legacy.Description,
                    IconPath = legacy.IconPath,
                    Options = legacy.Options
                },
                _ => manifest
            };
            ModManifest.SaveToFile(manifest, extractedDirectory);
            manifestFile.Refresh();
        }

        if (manifest.Guid != mod.Manifest.Guid)
            throw new InvalidDataException("The update archive GUID does not match the selected mod.");
        var pathProblems = new List<ModProblem>();
        if (!CheckPaths(manifest, pathProblems, extractedDirectory, manifestFile))
            throw new InvalidDataException("The update archive manifest contains unsafe paths.");

        var modsRoot = Path.Combine(_settingsService.StorageDirectory, "Mods");
        var planResult = await _modImportService.PlanImportAsync(
            manifest.Guid,
            manifest.Name,
            extractedDirectory.FullName,
            modsRoot,
            cancellationToken);
        if (!planResult.IsSuccess || planResult.Value is null)
            throw new IOException($"{planResult.ErrorCode}: {planResult.ErrorMessage}");

        var plan = planResult.Value;
        var commitResult = await _modImportService.CommitImportAsync(
            plan,
            updateConfirmed: true,
            progress: null,
            cancellationToken);
        if (!commitResult.IsSuccess)
            throw new IOException($"{commitResult.ErrorCode}: {commitResult.ErrorMessage}");

        var oldManifest = mod.Manifest;
        var oldState = mod.ToEnabledData();
        try
        {
            mod.Manifest = manifest;
            mod.ApplyData(oldState);
            await _modHashService.RecomputeForUpdatedModAsync(mod);
            await _modImportService.CompleteImportAsync(plan, commit: true, cancellationToken);
        }
        catch
        {
            mod.Manifest = oldManifest;
            mod.ApplyData(oldState);
            await _modImportService.CompleteImportAsync(plan, commit: false, CancellationToken.None);
            throw;
        }

        progress?.Report(new UpdateProgressInfo
        {
            Phase = UpdatePhase.Completed,
            IsCompleted = true,
            Message = _localizationService["ModService.TransactionalUpdateComplete"]
        });
    }

    public async Task DeployAsync(IReadOnlyList<ModData> requestedMods, CancellationToken cancellationToken = default)
    {
        GuardInitialized();

        if (requestedMods.Count == 0)
        {
            _logger.LogInformation("No mods enabled, skipping deployment");
            return;
        }

        _logger.LogInformation("Starting deployment of {} dashboard snapshot mods", requestedMods.Count);

        var groups = new Dictionary<string, List<PatchFileTriplet>>();

        void AddFilesFromDir(DirectoryInfo dir)
        {
            if (!dir.Exists)
            {
                _logger.LogWarning("Directory \"{}\" does not exist, skipping", dir.FullName);
                return;
            }
            var files = dir.GetFiles().Where(static f => GetPatchFileRegex().IsMatch(f.Name)).ToArray();

            foreach (var file in files)
                _logger.LogDebug("Adding file \"{}\"", file.FullName);

            var names = new HashSet<string>();
            for (int i = 0; i < files.Length; i++)
                names.Add(files[i].Name[0..16]);

            foreach (var name in names)
            {
                var indexes = new HashSet<int>();
                foreach (var file in files)
                {
                    var match = GetPatchIndexRegex().Match(file.Name);
                    indexes.Add(int.Parse(match.Groups[1].ValueSpan));
                }

                foreach (var index in indexes)
                {
                    FileInfo? patchFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}$"));
                    FileInfo? gpuFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}.gpu_resources$"));
                    FileInfo? streamFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}.stream$"));

                    if (!groups.ContainsKey(name))
                        groups.Add(name, []);
                    groups[name].Add(new PatchFileTriplet
                    {
                        Patch = patchFile,
                        GpuResources = gpuFile,
                        Stream = streamFile
                    });
                }
            }
        }

        _logger.LogInformation("Grouping files");
        foreach (var mod in requestedMods)
        {
            _logger.LogInformation("Working on \"{}\"", mod.Manifest.Name);

            switch (mod.Manifest.Version)
            {
                case ManifestVersion.Legacy:
                    {
                        _logger.LogInformation("Mod \"{}\" has legacy manifest", mod.Manifest.Name);

                        var man = (LegacyModManifest)mod.Manifest;
                        var enabled = mod.EnabledOptions;
                        var selected = mod.SelectedOptions;

                        if (man.Options is not null)
                        {
                            if (selected is not int[] { Length: 1 })
                            {
                                _logger.LogError("Options have the wrong count");
                                continue;
                            }

                            var dir = new DirectoryInfo(_safePathPolicy.ResolveUnderRoot(mod.Directory.FullName, man.Options[selected[0]]));
                            AddFilesFromDir(dir);
                        }
                        else
                            AddFilesFromDir(mod.Directory);
                    }
                    break;

                case ManifestVersion.V1:
                    {
                        _logger.LogInformation("Mod \"{}\" has V1 manifest", mod.Manifest.Name);

                        var man = (V1ModManifest)mod.Manifest;
                        var enabled = mod.EnabledOptions;
                        var selected = mod.SelectedOptions;

                        if (man.Options is not null)
                        {
                            if (enabled.Length != man.Options.Count)
                            {
                                _logger.LogError("Enabled option counts are not equal");
                                continue;
                            }

                            if (selected.Length != man.Options.Count)
                            {
                                _logger.LogError("Selected option counts are not equal");
                                continue;
                            }

                            _logger.LogInformation("Making include list");

                            var optOrder = Enumerable.Range(0, enabled.Length).ToArray();

                            for (int oi = 0; oi < optOrder.Length; oi++)
                            {
                                int i = optOrder[oi];

                                if (!enabled[i])
                                    continue;

                                var opt = man.Options[i];

                                if (opt.Include is { } incs)
                                    foreach (var inc in incs)
                                    {
                                        var dir = new DirectoryInfo(_safePathPolicy.ResolveUnderRoot(mod.Directory.FullName, inc));
                                        _logger.LogInformation("Adding \"{}\"", dir.FullName);
                                        AddFilesFromDir(dir);
                                    }

                                if (opt.SubOptions is { } subs)
                                {
                                    int selectedSubIdx = selected[i];
                                    for (int si = 0; si < subs.Count; si++)
                                    {
                                        if (si != selectedSubIdx)
                                            continue;

                                        var sub = subs[si];
                                        foreach (var inc in sub.Include)
                                        {
                                            var dir = new DirectoryInfo(_safePathPolicy.ResolveUnderRoot(mod.Directory.FullName, inc));
                                            _logger.LogInformation("Adding \"{}\"", dir.FullName);
                                            AddFilesFromDir(dir);
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        else
                            AddFilesFromDir(mod.Directory);
                    }
                    break;

                default:
                    throw new NotSupportedException("Unknown manifest version!");
            }
        }

        _logger.LogInformation("Building transactional deployment plan");
        var deploymentFiles = new List<(FileInfo? Source, string FileName)>();
        foreach (var (name, list) in groups)
        {
            int offset = 0;
            if (_settingsService.SkipList.Contains(name))
                offset = 1;

            for (int i = 0; i < list.Count; i++)
            {
                var triplet = list[i];
                var index = i + offset;

                var newPatchPath = Path.Combine(_settingsService.GameDirectory, "data", $"{name}.patch_{index}");
                deploymentFiles.Add((triplet.Patch, Path.GetFileName(newPatchPath)));

                var newGpuResourcesPath = Path.Combine(_settingsService.GameDirectory, "data", $"{name}.patch_{index}.gpu_resources");
                deploymentFiles.Add((triplet.GpuResources, Path.GetFileName(newGpuResourcesPath)));

                var newStreamPath = Path.Combine(_settingsService.GameDirectory, "data", $"{name}.patch_{index}.stream");
                deploymentFiles.Add((triplet.Stream, Path.GetFileName(newStreamPath)));
            }
        }

        await CommitDeploymentAsync(deploymentFiles, cancellationToken);

        _logger.LogInformation("Deployment success");
    }

    private async Task CommitDeploymentAsync(
        IReadOnlyList<(FileInfo? Source, string FileName)> files,
        CancellationToken cancellationToken)
    {
        var dataRoot = Path.Combine(_settingsService!.GameDirectory, "data");
        RecoverInterruptedDeployments(dataRoot);
        var operationId = Guid.NewGuid();
        var transactionRoot = _safePathPolicy.ResolveUnderRoot(dataRoot, $".hd2mm-deploy-{operationId:N}");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var journalPath = Path.Combine(transactionRoot, "journal.json");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        var newFileNames = files.Select(static file => file.FileName).ToArray();
        var journal = new DeploymentJournal(operationId, DeploymentPhase.Staging, newFileNames);
        WriteDeploymentJournal(journalPath, journal);

        try
        {
            foreach (var (source, fileName) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = _safePathPolicy.ResolveUnderRoot(stagingRoot, fileName);
                if (source is null)
                {
                    await using var empty = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                else
                {
                    await CopyFileAsync(source.FullName, stagedPath, overwrite: false, cancellationToken);
                    if (new FileInfo(stagedPath).Length != source.Length)
                        throw new IOException($"Staged deployment file size mismatch: {fileName}");
                }
            }

            journal = journal with { Phase = DeploymentPhase.MovingOldFiles };
            WriteDeploymentJournal(journalPath, journal);
            foreach (var existingFile in new DirectoryInfo(dataRoot).GetFiles("*.patch_*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backupPath = _safePathPolicy.ResolveUnderRoot(backupRoot, existingFile.Name);
                existingFile.MoveTo(backupPath);
            }

            journal = journal with { Phase = DeploymentPhase.ActivatingNewFiles };
            WriteDeploymentJournal(journalPath, journal);
            foreach (var fileName in newFileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = _safePathPolicy.ResolveUnderRoot(stagingRoot, fileName);
                var destinationPath = _safePathPolicy.ResolveUnderRoot(dataRoot, fileName);
                File.Move(stagedPath, destinationPath);
            }

            journal = journal with { Phase = DeploymentPhase.Activated };
            WriteDeploymentJournal(journalPath, journal);
            Directory.Delete(transactionRoot, recursive: true);
        }
        catch
        {
            RollbackDeployment(dataRoot, transactionRoot, journal);
            throw;
        }
    }

    private void RecoverInterruptedDeployments(string dataRoot)
    {
        foreach (var transactionRoot in Directory.EnumerateDirectories(dataRoot, ".hd2mm-deploy-*"))
        {
            var journalPath = Path.Combine(transactionRoot, "journal.json");
            if (!File.Exists(journalPath))
                continue;
            try
            {
                var journal = JsonSerializer.Deserialize<DeploymentJournal>(File.ReadAllText(journalPath));
                if (journal is not null)
                    RollbackDeployment(dataRoot, transactionRoot, journal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not recover deployment transaction {Directory}", transactionRoot);
            }
        }
    }

    private void RollbackDeployment(string dataRoot, string transactionRoot, DeploymentJournal journal)
    {
        var backupRoot = Path.Combine(transactionRoot, "backup");
        if (journal.Phase is DeploymentPhase.ActivatingNewFiles or DeploymentPhase.Activated)
        {
            foreach (var fileName in journal.NewFileNames)
            {
                var destination = _safePathPolicy.ResolveUnderRoot(dataRoot, fileName);
                if (File.Exists(destination))
                    File.Delete(destination);
            }
        }

        if (Directory.Exists(backupRoot))
        {
            foreach (var backupFile in new DirectoryInfo(backupRoot).GetFiles())
            {
                var destination = _safePathPolicy.ResolveUnderRoot(dataRoot, backupFile.Name);
                if (!File.Exists(destination))
                    backupFile.MoveTo(destination);
            }
        }
        if (Directory.Exists(transactionRoot))
            Directory.Delete(transactionRoot, recursive: true);
    }

    private static void WriteDeploymentJournal(string path, DeploymentJournal journal)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(journal));
        if (File.Exists(path))
            File.Replace(temporaryPath, path, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, path);
    }

    private enum DeploymentPhase
    {
        Staging,
        MovingOldFiles,
        ActivatingNewFiles,
        Activated
    }

    private sealed record DeploymentJournal(
        Guid OperationId,
        DeploymentPhase Phase,
        IReadOnlyList<string> NewFileNames);

    private async Task CopyFileAsync(string sourcePath, string destinationPath)
    {
        GuardInitialized();

        if (_settingsService.UseSymbolicLinks)
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.CreateSymbolicLink(destinationPath, sourcePath);
            await Task.CompletedTask;
        }
        else
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    public async Task PurgeAsync()
    {
        GuardInitialized();

        _logger.LogInformation("Purging mods");

        var dataDir = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));

        var files = dataDir.GetFiles("*.patch_*");
        _logger.LogDebug("Found {} patch files", files.Length);

        var tasks = new List<Task>();
        foreach (var file in files)
        {
            var task = Task.Run(() =>
            {
                _logger.LogTrace("Attempting to delete \"{}\"", file.Name);
                file.Delete();
                _logger.LogTrace("Deleted \"{}\"", file.Name);
            });
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        _logger.LogInformation("Purge complete");
    }

    public ModData? GetModByGuid(Guid guid)
    {
        foreach (var mod in _mods)
            if (mod.Manifest.Guid == guid)
                return mod;
        return null;
    }

    [MemberNotNull(nameof(_settingsService))]
    private void GuardInitialized()
    {
        if (!Initialized)
            throw new InvalidOperationException("Object not initialized!");
    }

    private bool CheckPaths(IModManifest manifest, List<ModProblem> problems, DirectoryInfo dir, FileInfo manifestFile)
    {
        bool error = false;

        _logger.LogDebug("Checking manifest paths");

        switch (manifest)
        {
            case LegacyModManifest { Options: { } opts } man:
                {
                    if (opts.Count == 0)
                    {
                        _logger.LogWarning("Empty Options found in manifest \"{}\"", manifestFile.FullName);
                        problems.Add(new ModProblem
                        {
                            Directory = dir,
                            Kind = ModProblemKind.EmptyOptions,
                        });
                        error = true;
                    }

                    if (man.IconPath is not null)
                    {
                        if (string.IsNullOrEmpty(man.IconPath) || string.IsNullOrWhiteSpace(man.IconPath))
                        {
                            _logger.LogWarning("Manifest \"{}\" contains empty icon path \"{}\"", manifestFile.FullName, man.IconPath);
                            problems.Add(new ModProblem
                            {
                                Directory = dir,
                                Kind = ModProblemKind.EmptyImagePath,
                                ExtraData = man.IconPath,
                            });
                            // 图标路径为空不阻止导入，模组功能不受影响
                        }
                        else if (!TryResolveManifestPath(dir, man.IconPath, out var iconPath) || !File.Exists(iconPath))
                        {
                            _logger.LogWarning("Manifest \"{}\" contains invalid icon path \"{}\"", manifestFile.FullName, man.IconPath);
                            problems.Add(new ModProblem
                            {
                                Directory = dir,
                                Kind = ModProblemKind.InvalidImagePath,
                                ExtraData = man.IconPath,
                            });
                            // Missing optional images remain non-fatal, but unsafe paths reject the manifest.
                            if (iconPath is null)
                                error = true;
                        }
                    }

                    foreach (var opt in opts)
                        if (!TryResolveManifestPath(dir, opt, out var optionPath) || !Directory.Exists(optionPath))
                        {
                            _logger.LogWarning("Manifest \"{}\" contains invalid option directory \"{}\", skipping", manifestFile.FullName, opt);
                            problems.Add(new ModProblem
                            {
                                Directory = dir,
                                Kind = ModProblemKind.InvalidPath,
                                ExtraData = opt,
                            });
                            error = true;
                        }
                    break;
                }

            case V1ModManifest { Options: { } opts } man:
                {
                    if (opts.Count == 0)
                    {
                        _logger.LogWarning("Empty Options found in manifest \"{}\"", manifestFile.FullName);
                        problems.Add(new ModProblem
                        {
                            Directory = dir,
                            Kind = ModProblemKind.EmptyOptions,
                        });
                        error = true;
                    }

                    if (opts.Any(static opt => opt.SubOptions is { Count: 0 }))
                    {
                        _logger.LogWarning("Empty Sub-Options found in manifest \"{}\"", manifestFile.FullName);
                        problems.Add(new ModProblem
                        {
                            Directory = dir,
                            Kind = ModProblemKind.EmptySubOptions,
                        });
                        error = true;
                    }

                    if (opts.Any(static opt => opt.SubOptions?.Any(static sub => sub.Include.Count == 0) ?? false))
                    {
                        _logger.LogWarning("Empty includes found in manifest \"{}\"", manifestFile.FullName);
                        problems.Add(new ModProblem
                        {
                            Directory = dir,
                            Kind = ModProblemKind.EmptyIncludes,
                        });
                        error = true;
                    }

                    if (man.IconPath is not null)
                    {
                        if (string.IsNullOrEmpty(man.IconPath) || string.IsNullOrWhiteSpace(man.IconPath))
                        {
                            _logger.LogWarning("Manifest \"{}\" contains empty icon path", manifestFile.FullName);
                            problems.Add(new ModProblem
                            {
                                Directory = dir,
                                Kind = ModProblemKind.EmptyImagePath,
                                ExtraData = man.IconPath,
                            });
                            // 图标路径为空不阻止导入
                        }
                        else if (!TryResolveManifestPath(dir, man.IconPath, out var iconPath) || !File.Exists(iconPath))
                        {
                            _logger.LogWarning("Manifest \"{}\" contains invalid icon path \"{}\"", manifestFile.FullName, man.IconPath);
                            problems.Add(new ModProblem
                            {
                                Directory = dir,
                                Kind = ModProblemKind.InvalidImagePath,
                                ExtraData = man.IconPath,
                            });
                            if (iconPath is null)
                                error = true;
                        }
                    }

                    foreach (var opt in opts)
                    {
                        if (opt.Image is not null)
                        {
                            if (string.IsNullOrEmpty(opt.Image) || string.IsNullOrWhiteSpace(opt.Image))
                            {
                                _logger.LogWarning("Manifest \"{}\" contains empty option image path", manifestFile.FullName);
                                problems.Add(new ModProblem
                                {
                                    Directory = dir,
                                    Kind = ModProblemKind.EmptyImagePath,
                                });
                                // 选项图片路径为空不阻止导入
                            }
                            else if (!TryResolveManifestPath(dir, opt.Image, out var optionImagePath) || !File.Exists(optionImagePath))
                            {
                                _logger.LogWarning("Manifest \"{}\" contains invalid option image path \"{}\"", manifestFile.FullName, opt.Image);
                                problems.Add(new ModProblem
                                {
                                    Directory = dir,
                                    Kind = ModProblemKind.InvalidImagePath,
                                    ExtraData = opt.Image,
                                });
                                if (optionImagePath is null)
                                    error = true;
                            }
                        }

                        if (opt.Include is not null)
                            foreach (var inc in opt.Include)
                                if (!TryResolveManifestPath(dir, inc, out var includePath) || !Directory.Exists(includePath))
                                {
                                    _logger.LogWarning("Manifest \"{}\" contains invalid include path \"{}\", skipping", manifestFile.FullName, inc);
                                    problems.Add(new ModProblem
                                    {
                                        Directory = dir,
                                        Kind = ModProblemKind.InvalidPath,
                                        ExtraData = inc,
                                    });
                                    error = true;
                                }

                        if (opt.SubOptions is not null)
                            foreach (var sub in opt.SubOptions)
                            {
                                if (sub.Image is not null)
                                {
                                    if (string.IsNullOrEmpty(sub.Image) || string.IsNullOrWhiteSpace(sub.Image))
                                    {
                                        _logger.LogWarning("Manifest \"{}\" contains empty sub-option image path", manifestFile.FullName);
                                        problems.Add(new ModProblem
                                        {
                                            Directory = dir,
                                            Kind = ModProblemKind.EmptyImagePath,
                                        });
                                        // 子选项图片路径为空不阻止导入
                                    }
                                    else if (!TryResolveManifestPath(dir, sub.Image, out var subOptionImagePath) || !File.Exists(subOptionImagePath))
                                    {
                                        _logger.LogWarning("Manifest \"{}\" contains invalid sub-option image path \"{}\"", manifestFile.FullName, sub.Image);
                                        problems.Add(new ModProblem
                                        {
                                            Directory = dir,
                                            Kind = ModProblemKind.InvalidImagePath,
                                            ExtraData = sub.Image,
                                        });
                                        if (subOptionImagePath is null)
                                            error = true;
                                    }
                                }

                                foreach (var inc in sub.Include)
                                    if (!TryResolveManifestPath(dir, inc, out var subOptionIncludePath) || !Directory.Exists(subOptionIncludePath))
                                    {
                                        _logger.LogWarning("Manifest \"{}\" contains invalid sub-option include path \"{}\", skipping", manifestFile.FullName, inc);
                                        problems.Add(new ModProblem
                                        {
                                            Directory = dir,
                                            Kind = ModProblemKind.InvalidPath,
                                            ExtraData = inc,
                                        });
                                        error = true;
                                    }
                            }
                    }
                    break;
                }
        }

        _logger.LogDebug("Path check complete");

        return !error;
    }

    private bool TryResolveManifestPath(DirectoryInfo root, string relativePath, [NotNullWhen(true)] out string? resolvedPath)
    {
        try
        {
            resolvedPath = _safePathPolicy.ResolveUnderRoot(root.FullName, relativePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Rejected unsafe manifest path {Path}", relativePath);
            resolvedPath = null;
            return false;
        }
    }

    private async Task ExtractArchiveSafelyAsync(
        FileInfo archive,
        DirectoryInfo destination,
        CancellationToken cancellationToken)
    {
        var planResult = await _archiveInspector.PlanExtractionAsync(
            archive.FullName,
            destination.FullName,
            ArchiveSafetyLimits.Default,
            cancellationToken);
        if (!planResult.IsSuccess || planResult.Value is null)
            throw new InvalidDataException($"{planResult.ErrorCode}: {planResult.ErrorMessage}");

        var extractionResult = await _archiveInspector.ExtractAsync(
            planResult.Value,
            progress: null,
            cancellationToken);
        if (!extractionResult.IsSuccess)
            throw new InvalidDataException($"{extractionResult.ErrorCode}: {extractionResult.ErrorMessage}");
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destinationMode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            destinationMode,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    [GeneratedRegex(@"^[a-z0-9]{16}\.patch_[0-9]+(\.(stream|gpu_resources))?$")]
    private static partial Regex GetPatchFileRegex();

    [GeneratedRegex(@"\.patch_[0-9]+")]
    private static partial Regex GetPatchRegex();

    [GeneratedRegex(@"^(?:[a-z0-9]{16}\.patch_)([0-9]+)(?:(?:\.(?:stream|gpu_resources))?)$")]
    private static partial Regex GetPatchIndexRegex();

    private static async Task MoveDirectoryContentsAsync(DirectoryInfo source, DirectoryInfo destination)
    {
        foreach (var file in source.GetFiles())
        {
            file.MoveTo(Path.Combine(destination.FullName, file.Name), true);
        }

        foreach (var dir in source.GetDirectories())
        {
            var newDir = Directory.CreateDirectory(Path.Combine(destination.FullName, dir.Name));
            await MoveDirectoryContentsAsync(dir, newDir);
        }
    }

    private static async Task<bool> AreDirectoriesEqualAsync(DirectoryInfo dir1, DirectoryInfo dir2)
    {
        var files1 = dir1.GetFiles("*", System.IO.SearchOption.AllDirectories).OrderBy(f => f.FullName).ToList();
        var files2 = dir2.GetFiles("*", System.IO.SearchOption.AllDirectories).OrderBy(f => f.FullName).ToList();

        if (files1.Count != files2.Count)
            return false;

        for (int i = 0; i < files1.Count; i++)
        {
            var relativePath1 = files1[i].FullName.Substring(dir1.FullName.Length);
            var relativePath2 = files2[i].FullName.Substring(dir2.FullName.Length);

            if (!relativePath1.Equals(relativePath2, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!await AreFilesEqualAsync(files1[i], files2[i]))
                return false;
        }

        return true;
    }

    private static async Task<bool> AreFilesEqualAsync(FileInfo file1, FileInfo file2)
    {
        if (file1.Length != file2.Length)
            return false;

        using var hashAlgorithm = SHA256.Create();

        using var stream1 = file1.OpenRead();
        using var stream2 = file2.OpenRead();

        var hash1 = await hashAlgorithm.ComputeHashAsync(stream1);
        var hash2 = await hashAlgorithm.ComputeHashAsync(stream2);

        return hash1.SequenceEqual(hash2);
    }

    /// <summary>
    /// 清理目录中所有空子目录（自底向上递归遍历，只删除不含任何文件和子目录的空目录）
    /// </summary>
    private static void CleanEmptyDirectories(DirectoryInfo directory)
    {
        foreach (var subDir in directory.GetDirectories())
        {
            // 递归处理子目录
            CleanEmptyDirectories(subDir);

            // 如果子目录处理后为空，删除它
            if (subDir.GetFileSystemInfos().Length == 0)
            {
                subDir.Delete();
            }
        }
    }
}

/// <summary>
/// 模组更新的阶段枚举，用于进度报告
/// </summary>
internal enum UpdatePhase
{
    /// <summary>正在安全解压更新归档</summary>
    Extracting,
    /// <summary>正在计算当前模组文件的哈希值</summary>
    HashingCurrent,
    /// <summary>正在计算新版本文件的哈希值</summary>
    HashingNew,
    /// <summary>正在比对文件差异</summary>
    Comparing,
    /// <summary>正在执行增量文件更新</summary>
    Updating,
    /// <summary>更新已完成</summary>
    Completed,
}

/// <summary>
/// 模组更新的进度信息，通过 IProgress 回调传递给调用方
/// </summary>
internal sealed class UpdateProgressInfo
{
    /// <summary>当前更新阶段</summary>
    public UpdatePhase Phase { get; init; }

    /// <summary>当前正在处理的文件相对路径</summary>
    public string? CurrentFile { get; init; }

    /// <summary>已处理的文件数量</summary>
    public int ProcessedCount { get; init; }

    /// <summary>缓存命中的文件数量（未变化文件跳过SHA-256计算）</summary>
    public int CacheHits { get; init; }

    /// <summary>当前阶段需要处理的文件总数</summary>
    public int TotalCount { get; init; }

    /// <summary>需要更新的文件总数（仅在 Updating 和 Completed 阶段有效）</summary>
    public int NeedUpdateCount { get; init; }

    /// <summary>未变化的文件数量（仅在 Completed 阶段有效）</summary>
    public int UnchangedCount { get; init; }

    /// <summary>已删除的文件数量（仅在 Completed 阶段有效）</summary>
    public int DeletedCount { get; init; }

    /// <summary>可读的进度消息文本</summary>
    public string? Message { get; init; }

    /// <summary>是否已完成所有操作</summary>
    public bool IsCompleted { get; init; }
}
