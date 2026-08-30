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
    private async Task SetModsToGroupsAsync(ModGroup[] groups, ModViewModel[] selected)
    {
        try
        {
            var mods = selected.Select(static vm => vm.Data).ToArray();
            await _modGroupService.RemoveModsFromAllGroupsAsync(selected.Select(static vm => vm.Guid).ToList());
            foreach (var group in groups)
                await _modGroupService.AddModsToGroupAsync(group.Id, mods);
            GroupSidebar.RefreshSelectionProperties();
            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
            {
                Message = _localizationService["ModGroup.GroupsUpdated"].Replace("{count}", selected.Length.ToString())
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置分组失败");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = ex.Message });
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
        async Task Add(string? filePath = null)
        {
            // 支持单文件路径传入（如拖拽场景）或批量文件选择
            List<string> selectedFiles = [];

            if (filePath is not null)
            {
                selectedFiles.Add(filePath);
            }
            else
            {
                var dialog = new OpenFileDialog
                {
                    CheckFileExists = true,
                    CheckPathExists = true,
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
                    Filter = _localizationService["Common.FileFilterArchive"],
                    Multiselect = true,
                    Title = _localizationService["DashboardPage.AddModDialogTitle"]
                };

                if (!(dialog.ShowDialog() ?? false))
                    return;

                selectedFiles.AddRange(dialog.FileNames);
            }

            if (selectedFiles.Count == 0)
                return;

            await AddFilesCoreAsync(selectedFiles);
        }

        /// <summary>
        /// 批量导入命令：接收多个压缩包路径（主窗口拖拽入口，支持一次拖入多个文件）。
        /// </summary>
        [RelayCommand(AllowConcurrentExecutions = false)]
        async Task AddFiles(string[]? files)
        {
            if (files is null || files.Length == 0)
                return;

            await AddFilesCoreAsync(files);
        }

        /// <summary>
        /// 批量导入核心逻辑：显示进度弹窗并逐个导入压缩包（含嵌套压缩包进度回调）。
        /// </summary>
        private async Task AddFilesCoreAsync(IReadOnlyList<string> selectedFiles)
        {
            // 单文件时使用原有提示文案，多文件时显示进度
            var isBatch = selectedFiles.Count > 1;
            var totalFiles = selectedFiles.Count;
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
            {
                Title = isBatch ? _localizationService["DashboardPage.BatchAddProgressTitle"].Replace("{current}", "0").Replace("{total}", totalFiles.ToString()) : _localizationService["DashboardPage.AddSingleProgress"],
                Message = isBatch ? _localizationService["DashboardPage.BatchAddWaitMsg"].Replace("{total}", totalFiles.ToString()) : _localizationService["SettingsPage.PleaseWait"]
            });

            var backgroundTask = _backgroundTaskService.Add(
                _localizationService["BackgroundTasksPage.TaskTypeImport"],
                isBatch ? _localizationService["DashboardPage.BatchAddWaitMsg"].Replace("{total}", totalFiles.ToString()) : Path.GetFileName(selectedFiles[0]),
                isForeground: true);
            _backgroundTaskService.Update(backgroundTask, progress: 0, isIndeterminate: false);

            try
            {
                var allProblems = new List<ModProblem>();
                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < selectedFiles.Count; i++)
                {
                    // 批量模式下更新进度提示（含剩余数量）
                    if (isBatch)
                    {
                        var remainingCount = totalFiles - i - 1;
                        var description = remainingCount > 0
                            ? _localizationService["DashboardPage.BatchAddProcessing"].Replace("{file}", Path.GetFileName(selectedFiles[i])).Replace("{remaining}", remainingCount.ToString())
                            : _localizationService["DashboardPage.BatchAddProcessing"].Replace("{file}", Path.GetFileName(selectedFiles[i])).Replace("{remaining}", "?");
                        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                        {
                            Title = _localizationService["DashboardPage.BatchAddProgressTitle"].Replace("{current}", (i + 1).ToString()).Replace("{total}", totalFiles.ToString()),
                            Message = description
                        });
                        _backgroundTaskService.Update(backgroundTask, description, (double)i / totalFiles, false);
                    }
                    else
                    {
                        _backgroundTaskService.Update(backgroundTask, Path.GetFileName(selectedFiles[i]), (double)i / totalFiles, false);
                    }

                    // 创建嵌套压缩包处理进度回调，用于在处理嵌套压缩包时更新UI进度显示
                    var currentBatchIndex = i;
                    var currentFileName = selectedFiles[i];
                    Action<int, int, string> nestedProgress = (nestedIndex, nestedTotal, nestedFileName) =>
                    {
                        var nestedDescription = _localizationService["DashboardPage.BatchAddProcessing"].Replace("{file}", nestedFileName).Replace("{remaining}", (nestedTotal - nestedIndex - 1).ToString());
                        // 根据是否为批量导入模式，组合显示外层批量进度和内层嵌套进度
                        if (isBatch)
                        {
                            // 批量导入 + 嵌套处理：显示双层进度
                            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                            {
                                Title = _localizationService["DashboardPage.BatchAddNestedTitle"].Replace("{current}", (currentBatchIndex + 1).ToString()).Replace("{total}", totalFiles.ToString()).Replace("{nested}", (nestedIndex + 1).ToString()).Replace("{nestedTotal}", nestedTotal.ToString()),
                                Message = nestedDescription
                            });
                        }
                        else
                        {
                            // 单文件 + 嵌套处理：显示嵌套进度
                            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
                            {
                                Title = _localizationService["DashboardPage.BatchAddNestedProgress"].Replace("{current}", (nestedIndex + 1).ToString()).Replace("{total}", nestedTotal.ToString()),
                                Message = nestedDescription
                            });
                        }

                        var outerProgress = (double)currentBatchIndex / totalFiles;
                        var nestedRatio = nestedTotal > 0 ? (double)(nestedIndex + 1) / nestedTotal : 0;
                        _backgroundTaskService.Update(backgroundTask, nestedDescription, outerProgress + nestedRatio / totalFiles, false);
                    };

                    try
                    {
                        var problems = await _modService.TryAddModFromArchiveAsync(new FileInfo(selectedFiles[i]), nestedProgress);
                        if (problems.Length > 0)
                        {
                            allProblems.AddRange(problems);
                            if (problems.Any(static p => p.IsError))
                                failCount++;
                            else
                                successCount++;
                        }
                        else
                        {
                            successCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add mod: {File}", selectedFiles[i]);
                        // 使用 CantReadArchive 表示读取/解压失败，ExtraData 存储异常信息
                        allProblems.Add(new ModProblem
                        {
                            Directory = new DirectoryInfo(Path.GetDirectoryName(selectedFiles[i]) ?? ""),
                            Kind = ModProblemKind.CantReadArchive,
                            ExtraData = $"{Path.GetFileName(selectedFiles[i])}: {ex.Message}"
                        });
                        failCount++;
                    }
                }

                _backgroundTaskService.Complete(backgroundTask, _localizationService["BackgroundTasksPage.ImportComplete"].Replace("{success}", successCount.ToString()).Replace("{fail}", failCount.ToString()));

                // 汇总结果
                if (isBatch)
                {
                    if (failCount == 0 && allProblems.Count == 0)
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
                        {
                            Message = _localizationService["DashboardPage.BatchAddSuccess"].Replace("{count}", successCount.ToString())
                        });
                    }
                    else if (allProblems.Count > 0)
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                        var error = allProblems.Any(static p => p.IsError);
                        var prefix = error
                            ? _localizationService["DashboardPage.BatchAddDoneErrors"].Replace("{success}", successCount.ToString()).Replace("{fail}", failCount.ToString())
                            : _localizationService["DashboardPage.BatchAddDoneWarnings"].Replace("{count}", successCount.ToString());
                        ShowProblems([.. allProblems], prefix, error);
                    }
                }
                else
                {
                    // 单文件模式保持原有行为
                    if (allProblems.Count > 0)
                    {
                        var error = allProblems.Any(static p => p.IsError);
                        var prefix = error
                            ? _localizationService["DashboardPage.AddSingleError"]
                            : _localizationService["DashboardPage.AddSingleWarning"];
                        ShowProblems([.. allProblems], prefix, error);
                    }
                    else
                        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add mod");
                _backgroundTaskService.Fail(backgroundTask, ex.Message);
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = ex.Message
                });
            }
        }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task UpdateMod(ModViewModel vm)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
            Filter = _localizationService["Common.FileFilterArchive"],
            Multiselect = false,
            Title = $"{_localizationService["DashboardPage.UpdateModDialogPrefix"]}{vm.Name}{_localizationService["DashboardPage.UpdateModDialogSuffix"]}"
        };

        if (!(dialog.ShowDialog() ?? false))
            return;

        // 发送初始进度消息，显示更新进度UI
        WeakReferenceMessenger.Default.Send(new MessageBoxUpdateProgressMessage
        {
            Title = _localizationService["DashboardPage.UpdateModProgress"],
            ModName = vm.Name
        });

        var backgroundTask = _backgroundTaskService.Add(
            _localizationService["DashboardPage.UpdateMod"],
            vm.Name,
            isForeground: true);

        try
        {
            // 创建进度报告回调，将服务层进度映射为UI消息
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                if (info.IsCompleted)
                {
                    // 更新完成，发送完成消息
                    WeakReferenceMessenger.Default.Send(new MessageBoxUpdateProgressUpdateMessage
                    {
                        IsCompleted = true
                    });

                    // 显示统计信息
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
                    {
                        Message = info.Message ?? _localizationService["DashboardPage.UpdateModDone"]
                    });
                    _backgroundTaskService.Complete(backgroundTask, info.Message ?? _localizationService["DashboardPage.UpdateModDone"]);
                }
                else
                {
                    var taskProgress = info.TotalCount > 0
                        ? (double)info.ProcessedCount / info.TotalCount
                        : 0;
                    WeakReferenceMessenger.Default.Send(new MessageBoxUpdateProgressUpdateMessage
                    {
                        PhaseText = info.Message,
                        CurrentFile = info.CurrentFile,
                        ProcessedCount = info.ProcessedCount,
                        TotalCount = info.TotalCount,
                        NeedUpdateCount = info.NeedUpdateCount,
                        CacheHits = info.CacheHits,
                        Progress = taskProgress
                    });
                    _backgroundTaskService.Update(backgroundTask, info.Message, taskProgress, info.TotalCount <= 0);
                }
            });

            await _modService.UpdateModFromArchiveAsync(vm.Data, new FileInfo(dialog.FileName), progress);

            // 更新后保存状态到数据库，确保 EnabledOptions/SelectedOptions 与新清单同步
            await SaveProfileNowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update mod \"{}\"", vm.Name);
            _backgroundTaskService.Fail(backgroundTask, ex.Message);
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"{_localizationService["DashboardPage.UpdateModFailed"]}{ex.Message}"
            });
        }
    }

    // void Browse()
    // {
    //     throw new NotImplementedException();
    // }
}
