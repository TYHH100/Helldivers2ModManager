using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GongSolutions.Wpf.DragDrop;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SharpSevenZip;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class DashboardPageViewModel
{
    [RelayCommand]
    void OpenFileLocation(ModViewModel modVm)
    {
        try
        {
            Process.Start(new ProcessStartInfo(modVm.Data.Directory.FullName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file location for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"{_localizationService["DashboardPage.OpenFileLocationFailed"]}{ex.Message}"
            });
        }
    }

    [RelayCommand]
    void EditName(ModViewModel modVm)
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
            {
                Title = _localizationService["DashboardPage.EditNameTitle"],
                Message = _localizationService["DashboardPage.EditNameMsg"],
                MaxLength = 64,
                InitialText = modVm.Name,
                Confirm = (newName) =>
                {
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["DashboardPage.EditNameEmptyError"] });
                        return;
                    }

                    modVm.Data.UpdateManifestName(newName);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.EditNameUpdated"] });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod name for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"{_localizationService["DashboardPage.EditNameFailed"]}{ex.Message}"
            });
        }
    }

    [RelayCommand]
    void EditDescription(ModViewModel modVm)
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
            {
                Title = _localizationService["DashboardPage.EditDescTitle"],
                Message = _localizationService["DashboardPage.EditDescMsg"],
                MaxLength = 1024,
                InitialText = modVm.Description,
                Confirm = (newDescription) =>
                {
                    modVm.Data.UpdateManifestDescription(newDescription);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = _localizationService["DashboardPage.EditDescUpdated"] });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod description for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"{_localizationService["DashboardPage.EditDescFailed"]}{ex.Message}"
            });
        }
    }

    [RelayCommand]
    async Task EditImage(ModViewModel modVm)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = _localizationService["Common.FileFilterImage"],
            Title = _localizationService["DashboardPage.EditImageDialog"]
        };

        if (dialog.ShowDialog() ?? false)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
            {
                Title = _localizationService["DashboardPage.EditImageProgress"],
                Message = _localizationService["SettingsPage.PleaseWait"]
            });

            try
            {
                string imageFileName = Path.GetFileName(dialog.FileName);
                string destinationPath = Path.Combine(modVm.Data.Directory.FullName, imageFileName);
                await CopyFileAsync(dialog.FileName, destinationPath, true);

                modVm.Data.UpdateManifestIconPath(imageFileName);

                modVm.LoadIcon();

                await SaveProfileNowAsync();

                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
                {
                    Message = _localizationService["DashboardPage.EditImageSuccess"]
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to edit image for mod {ModName}", modVm.Name);
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = $"{_localizationService["DashboardPage.EditImageFailed"]}{ex.Message}"
                });
            }
        }
    }

    private async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite)
    {
        using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true))
        using (var destinationStream = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
        {
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    [RelayCommand]
    void Edit(ModViewModel vm)
    {
        _editModStore.CurrentMod = vm;
        _navStore.Value.Navigate<EditPageViewModel>();
    }

    [RelayCommand]
    void EditManifest(ModViewModel vm)
    {
        _editModStore.CurrentMod = vm;
        _navStore.Value.Navigate<ManifestEditPageViewModel>();
    }

    /// <summary>
    /// Pack the mod as a zip/7z archive and export to a specified location for distribution.
    /// Supports 5 gears: ZIP standard / 7z Fast / 7z Normal / 7z High / 7z Ultra.
    /// Shows memory usage warning for high-compression options on large mods.
    /// </summary>
    [RelayCommand]
    void ExportMod(ModViewModel vm)
    {
        var modDir = vm.Data.Directory;

        // Step 1: Show format/compression selection dialog (5 gears)
        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = _localizationService["DashboardPage.ExportTitle"],
            Message = _localizationService["DashboardPage.ExportMsg"],
            Options = new List<object>
            {
                _localizationService["DashboardPage.ExportZip"],
                _localizationService["DashboardPage.Export7zFast"],
                _localizationService["DashboardPage.Export7zStandard"],
                _localizationService["DashboardPage.Export7zHigh"],
                _localizationService["DashboardPage.Export7zUltra"]
            },
            Confirm = (selectedOption) =>
            {
                var opt = selectedOption.ToString()!;
                var is7z = opt.StartsWith("7z", StringComparison.OrdinalIgnoreCase);

                // Parse compression level
                SharpSevenZip.CompressionLevel level;
                string dictSize;
                bool isHighMemory;
                string levelName;

                if (opt == _localizationService["DashboardPage.Export7zFast"])    { level = SharpSevenZip.CompressionLevel.Fast;   dictSize = "8m";  isHighMemory = false; levelName = "Fast"; }
                else if (opt == _localizationService["DashboardPage.Export7zHigh"]) { level = SharpSevenZip.CompressionLevel.High;   dictSize = "64m"; isHighMemory = true;  levelName = "High"; }
                else if (opt == _localizationService["DashboardPage.Export7zUltra"])   { level = SharpSevenZip.CompressionLevel.Ultra;  dictSize = "128m"; isHighMemory = true;  levelName = "Ultra"; }
                else                             { level = SharpSevenZip.CompressionLevel.Normal; dictSize = "32m"; isHighMemory = false; levelName = "Normal"; }

                // Step 2: Show save file dialog
                var dialog = new SaveFileDialog
                {
                    Title = _localizationService["DashboardPage.ExportSaveDialog"],
                    FileName = $"{vm.Name}.{(is7z ? "7z" : "zip")}",
                    Filter = is7z ? _localizationService["Common.FileFilter7z"] : _localizationService["Common.FileFilterZip"],
                };

                if (dialog.ShowDialog() != true)
                    return;

                // Step 3: Calculate total mod size
                var excludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"
                };

                bool IsExcludedFile(FileInfo f)
                {
                    if (excludedExtensions.Contains(f.Extension))
                        return true;
                    if (f.Name.EndsWith(".hd2mm-backup", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (f.Name.EndsWith(".hd2mm-backup.json", StringComparison.OrdinalIgnoreCase))
                        return true;
                    return false;
                }

                long totalSize = 0;
                foreach (var f in modDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (!IsExcludedFile(f))
                        totalSize += f.Length;
                }

                // Step 4: Warn if > 1GB and high-memory compression
                if (isHighMemory && totalSize > 1024L * 1024 * 1024)
                {
                    var sizeText = totalSize >= 1024L * 1024 * 1024 * 1024
                        ? $"{totalSize / (1024.0 * 1024 * 1024 * 1024):F2} TB"
                        : $"{totalSize / (1024.0 * 1024 * 1024):F2} GB";

                    var dictDesc = dictSize switch
                    {
                        "64m" => "64MB",
                        "128m" => "128MB",
                        _ => dictSize
                    };

                    WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
                    {
                        Title = _localizationService["DashboardPage.ExportMemoryWarning"],
                        Message = $"{_localizationService["DashboardPage.ExportMemoryMsgPrefix"]}{sizeText}{_localizationService["DashboardPage.ExportMemoryMsgMid"]}{levelName}{_localizationService["DashboardPage.ExportMemoryMsgCompression"]}{dictDesc}{_localizationService["DashboardPage.ExportMemoryMsgSuffix"]}",
                        Confirm = () => DoExport(vm, modDir, dialog.FileName, is7z, level, dictSize, levelName, IsExcludedFile),
                        Abort = () => { }
                    });
                }
                else
                {
                    DoExport(vm, modDir, dialog.FileName, is7z, level, dictSize, levelName, IsExcludedFile);
                }
            }
        });
    }

    /// <summary>
    /// Execute the actual export with the chosen format and settings.
    /// Shows a real-time progress dialog with compression speed and ratio.
    /// </summary>
    private void DoExport(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, Func<FileInfo, bool> isExcludedFile)
    {
        // Show progress dialog on UI thread
        WeakReferenceMessenger.Default.Send(new MessageBoxExportProgressMessage
        {
            Title = $"{_localizationService["DashboardPage.ExportSaveDialog"]} - {vm.Name}"
        });

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["DashboardPage.ExportSaveDialog"],
            vm.Name,
            isForeground: true);
        _backgroundTaskService.Update(backgroundTask, progress: 0, isIndeterminate: false);

        // Run export on background thread to keep UI responsive
        Task.Run(() => DoExportAsync(vm, modDir, outputPath, is7z, level, dictSize, levelName, isExcludedFile, backgroundTask));
    }

    /// <summary>
    /// Background export with real-time progress reporting.
    /// </summary>
    private void DoExportAsync(ModViewModel vm, DirectoryInfo modDir, string outputPath, bool is7z,
        SharpSevenZip.CompressionLevel level, string dictSize, string levelName, Func<FileInfo, bool> isExcludedFile, BackgroundTaskItem backgroundTask)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastUpdateBytes = 0;
        double lastUpdateSec = 0;
        double lastUiUpdate = 0;  // 用于节流 UI 更新

        // Calculate total input size for progress tracking
        long totalInputSize = 0;
        foreach (var f in modDir.EnumerateFiles("*", SearchOption.AllDirectories))
            if (!isExcludedFile(f))
                totalInputSize += f.Length;

        // Helper to send progress updates to UI thread (throttled)
        void ReportProgress(double progress, string? currentFile, long bytesProcessed)
        {
            // 节流：最多每 120ms 更新一次 UI，避免高频 Dispatcher.Invoke 卡死 UI 线程
            var now = sw.Elapsed.TotalSeconds;
            if (now - lastUiUpdate < 0.12 && progress < 1.0)
                return;
            lastUiUpdate = now;

            var elapsed = now;
            var speed = elapsed > 0 ? bytesProcessed / elapsed : 0;

            // Smooth speed calculation over 1-second intervals
            var deltaBytes = bytesProcessed - lastUpdateBytes;
            var deltaSec = elapsed - lastUpdateSec;
            if (deltaSec >= 1.0 || progress >= 1.0)
            {
                lastUpdateBytes = bytesProcessed;
                lastUpdateSec = elapsed;
            }

            var speedText = speed >= 1024 * 1024
                ? $"{_localizationService["DashboardPage.ExportSpeed"]}{speed / (1024.0 * 1024):F1}{_localizationService["DashboardPage.ExportMBS"]}"
                : speed >= 1024
                    ? $"{_localizationService["DashboardPage.ExportSpeed"]}{speed / 1024.0:F0}{_localizationService["DashboardPage.ExportKBS"]}"
                    : $"{_localizationService["DashboardPage.ExportSpeed"]}{speed:F0}{_localizationService["DashboardPage.ExportBS"]}";

            // Read output file size for ratio (if file exists)
            string ratioText = "";
            try
            {
                var outFile = new FileInfo(outputPath);
                if (outFile.Exists && outFile.Length > 0 && totalInputSize > 0)
                {
                    // 压缩率 = (1 - 输出大小/输入大小) * 100，表示压缩了多少
                    var saved = (1.0 - (double)outFile.Length / totalInputSize) * 100;
                    ratioText = $"{_localizationService["DashboardPage.ExportRatio"]}{saved:F1}{_localizationService["DashboardPage.ExportPercent"]}";
                }
            }
            catch { }

            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxExportProgressUpdateMessage
                {
                    Progress = progress,
                    CurrentFile = currentFile,
                    SpeedText = speedText,
                    RatioText = ratioText,
                });
            });
            _backgroundTaskService.Update(backgroundTask, currentFile, progress, false);
        }

        try
        {
            if (is7z)
            {
                // --- 7z export with SharpSevenZipCompressor ---
                var compressor = new SharpSevenZipCompressor
                {
                    ArchiveFormat = OutArchiveFormat.SevenZip,
                    CompressionMethod = CompressionMethod.Lzma2,
                    CompressionLevel = level,
                    DirectoryStructure = true,
                    PreserveDirectoryRoot = false,
                };

                // 根据选择的挡位设置字典大小，控制内存占用
                //   Fast  → 8MB 字典，内存占用低
                //   Normal → 32MB 字典，平衡
                //   High  → 64MB 字典，较高压缩率
                //   Ultra → 128MB 字典，最高压缩率但内存占用高
                compressor.CustomParameters.Add("d", dictSize);

                var files = modDir.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => !isExcludedFile(f))
                    .Select(f => f.FullName)
                    .ToArray();

                var commonRootLength = modDir.FullName.Length;
                if (!modDir.FullName.EndsWith(Path.DirectorySeparatorChar))
                    commonRootLength++;

                // Track current file from event
                string currentFile = "";
                compressor.FileCompressionStarted += (_, args) =>
                {
                    currentFile = Path.GetFileName(args.FileName);
                };
                compressor.Compressing += (_, args) =>
                {
                    // args.PercentDone is int 0-100 from 7z native
                    var pct = Math.Max(0.0, Math.Min(100, (int)args.PercentDone)) / 100.0;
                    var estimatedBytes = (long)(totalInputSize * pct);
                    ReportProgress(pct, currentFile, estimatedBytes);
                };

                // 直接写文件路径而非 Stream，避免内存缓冲整个归档数据
                compressor.CompressFiles(outputPath, commonRootLength, files);
                ReportProgress(1.0, "", totalInputSize);

                _logger.LogInformation("Exported mod \"{Name}\" to {Path} (7z LZMA2 {Level}, dict {Dict})",
                    vm.Name, outputPath, levelName, dictSize);
            }
            else
            {
                // --- ZIP export with manual byte tracking ---
                long totalWritten = 0;
                string currentFile = "";

                using var fileStream = new FileStream(outputPath, FileMode.Create);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

                foreach (var file in modDir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (isExcludedFile(file))
                        continue;

                    currentFile = file.Name;
                    var relativePath = Path.GetRelativePath(modDir.FullName, file.FullName);
                    var entry = archive.CreateEntryFromFile(file.FullName, relativePath, System.IO.Compression.CompressionLevel.Optimal);
                    
                    // Approximate progress by file count / total input size
                    totalWritten += file.Length;
                    var progress = totalInputSize > 0 ? Math.Min((double)totalWritten / totalInputSize, 1.0) : 0;
                    ReportProgress(progress, currentFile, totalWritten);
                }

                ReportProgress(1.0, "", totalInputSize);

                _logger.LogInformation("Exported mod \"{Name}\" to {Path} (ZIP standard)", vm.Name, outputPath);
            }

            // Signal completion - keep final stats visible with OK button
            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxExportProgressUpdateMessage { IsCompleted = true });
            });
            _backgroundTaskService.Complete(backgroundTask, _localizationService["BackgroundTasksPage.ExportComplete"].Replace("{name}", vm.Name));
            // Don't auto-close - user clicks OK to dismiss and see final ratio/speed

            // 导出成功后自动打开压缩包所在文件夹并选中文件
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outputPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open export folder after export completed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export mod");
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            Application.Current.Dispatcher.Invoke(() =>
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
                {
                    Message = $"{_localizationService["DashboardPage.ExportError"]}{ex.Message}"
                });
            });
        }
    }

    bool CanClearSearch()
    {
        return !IsSearchEmpty;
    }

    [RelayCommand(CanExecute = nameof(CanClearSearch))]
    void ClearSearch()
    {
        SearchText = string.Empty;
    }
}
