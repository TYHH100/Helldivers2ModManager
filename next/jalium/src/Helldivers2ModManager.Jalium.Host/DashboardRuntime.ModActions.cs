using System.Diagnostics;
using System.IO.Compression;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using SharpSevenZip;
using SharpCompressionLevel = SharpSevenZip.CompressionLevel;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    public async Task ExecuteSeparatorAsync(DashboardAction action, ModSeparator separator)
    {
        switch (action)
        {
            case DashboardAction.RenameSeparator:
            {
                var value = await _messageBoxOverlay!.PromptAsync(
                    localization["DashboardPage.RenameSeparatorTitle"],
                    localization["DashboardPage.RenameSeparatorMsg"], separator.Name, 64,
                    text => string.IsNullOrWhiteSpace(text)
                        ? localization["DashboardPage.RenameSeparatorEmptyError"] : null);
                if (value is not null)
                    await workspace.RenameSeparatorAsync(separator, value);
                break;
            }
            case DashboardAction.ChangeSeparatorColor:
            {
                var value = await _messageBoxOverlay!.PromptAsync(
                    localization["DashboardPage.ChangeSeparatorColorTitle"],
                    localization["DashboardPage.ChangeSeparatorColorPrefix"] + separator.Name
                        + localization["DashboardPage.ChangeSeparatorColorSuffix"],
                    separator.Color, 9,
                    text => IsColorCode(text) ? null : localization["JaliumMigration.InvalidTagColor"]);
                if (value is not null)
                    await workspace.SetSeparatorColorAsync(separator, value);
                break;
            }
            case DashboardAction.DeleteSeparator:
                if (await _messageBoxOverlay!.ConfirmAsync(
                        localization["DashboardPage.DeleteSeparatorHint"],
                        localization["DashboardPage.DeleteSeparatorPrefix"] + separator.Name
                            + localization["DashboardPage.DeleteSeparatorSuffix"]))
                    await workspace.DeleteSeparatorAsync(separator);
                break;
        }
    }

    private static bool IsColorCode(string value)
        => value.Length == 9 && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _);

    private async Task ExecuteModActionAsync(DashboardAction action, ModData mod)
    {
        switch (action)
        {
            case DashboardAction.MoveToTop:
                await workspace.MoveToTopAsync(mod);
                break;
            case DashboardAction.MoveToBottom:
                await workspace.MoveToBottomAsync(mod);
                break;
            case DashboardAction.MoveToPosition:
            {
                var count = workspace.Rows.OfType<ModData>().Count();
                var value = await _messageBoxOverlay!.PromptAsync(
                    localization["DashboardPage.MoveToPositionTitle"],
                    localization["DashboardPage.MoveToPositionMsg"].Replace("{count}", count.ToString()),
                    workspace.GetPosition(mod).ToString(), 8,
                    text => int.TryParse(text, out var position) && position >= 1 && position <= count
                        ? null : localization["DashboardPage.MoveToPositionInvalid"].Replace("{count}", count.ToString()));
                if (value is not null)
                    await workspace.MoveToPositionAsync(mod, int.Parse(value));
                break;
            }
            case DashboardAction.OpenFileLocation:
                OpenFileLocation(mod);
                break;
            case DashboardAction.EditTags:
                await OpenBatchTagsAsync([mod]);
                break;
            case DashboardAction.RemoveFromGroup:
                if (await _messageBoxOverlay!.ConfirmAsync(localization["DashboardPage.DeleteConfirmTitle"],
                    localization["DashboardPage.DeleteConfirmPrefix"] + mod.Manifest.Name
                    + localization["DashboardPage.DeleteConfirmSuffix"]
                    + localization["ModGroup.RemoveFromGroup"]))
                    await workspace.RemoveFromCurrentGroupAsync(mod);
                break;
            case DashboardAction.EditName:
                await EditManifestTextAsync(mod, true);
                break;
            case DashboardAction.EditDescription:
                await EditManifestTextAsync(mod, false);
                break;
            case DashboardAction.EditImage:
                await EditImageAsync(mod);
                break;
            case DashboardAction.EditLink:
                await EditLinkAsync(mod);
                break;
            case DashboardAction.OpenModLink:
                OpenModLink(mod);
                break;
            case DashboardAction.AddToGroup:
                await OpenGroupSelectionAsync(mod);
                break;
            case DashboardAction.EditManifest:
                OpenManifestEdit(mod);
                break;
            case DashboardAction.ExportMod:
                await ExportModAsync(mod);
                break;
            case DashboardAction.UpdateMod:
                await UpdateModAsync(mod);
                break;
            case DashboardAction.DeleteMod:
                await DeleteModAsync(mod);
                break;
            case DashboardAction.PreviewModel:
                OpenModelPreview(mod);
                break;
        }
    }

    private void OpenFileLocation(ModData mod)
    {
        try
        {
            Process.Start(new ProcessStartInfo(mod.Directory.FullName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportError(new InvalidOperationException(
                localization["DashboardPage.OpenFileLocationFailed"] + ex.Message, ex));
        }
    }

    private async Task EditManifestTextAsync(ModData mod, bool name)
    {
        var value = await _messageBoxOverlay!.PromptAsync(
            localization[name ? "DashboardPage.EditNameTitle" : "DashboardPage.EditDescTitle"],
            localization[name ? "DashboardPage.EditNameMsg" : "DashboardPage.EditDescMsg"],
            name ? mod.Manifest.Name : mod.Manifest.Description, name ? 64 : 1024,
            name ? text => string.IsNullOrWhiteSpace(text)
                ? localization["DashboardPage.EditNameEmptyError"] : null : null);
        if (value is null)
            return;
        if (name) mod.UpdateManifestName(value);
        else mod.UpdateManifestDescription(value);
        workspace.RefreshTags();
    }

    private async Task EditImageAsync(ModData mod)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = localization["Common.FileFilterImage"],
            Title = localization["DashboardPage.EditImageDialog"],
        };
        if (dialog.ShowDialog() != true)
            return;
        var fileName = Path.GetFileName(dialog.FileName);
        var destination = Path.Combine(mod.Directory.FullName, fileName);
        await using (var source = File.OpenRead(dialog.FileName))
        await using (var target = File.Create(destination))
            await source.CopyToAsync(target);
        mod.UpdateManifestIconPath(fileName);
        workspace.RefreshTags();
    }

    private async Task EditLinkAsync(ModData mod)
    {
        if (settings.IsReadonly)
        {
            _messageBoxOverlay!.ShowError(localization["ModViewModel.ModLinkReadonly"]);
            return;
        }
        var repository = new ModLinkRepository(NullLogger<ModLinkRepository>.Instance, database);
        var current = repository.GetLink(settings.StorageDirectory, mod.Manifest.Guid) ?? string.Empty;
        var value = await _messageBoxOverlay!.PromptAsync(
            localization["ModViewModel.ModLinkEditTitle"], localization["ModViewModel.ModLinkEditMsg"],
            current, 2048);
        if (value is null)
            return;
        await repository.SaveLinkAsync(settings.StorageDirectory, mod.Manifest.Guid,
            string.IsNullOrWhiteSpace(value) ? null : value);
        _messageBoxOverlay!.ShowInfo(localization["ModViewModel.ModLinkUpdated"]);
    }

    private void OpenModLink(ModData mod)
    {
        try
        {
            var repository = new ModLinkRepository(NullLogger<ModLinkRepository>.Instance, database);
            var link = repository.GetLink(settings.StorageDirectory, mod.Manifest.Guid);
            if (string.IsNullOrWhiteSpace(link))
            {
                _ = EditLinkAsync(mod);
                return;
            }
            if (!link.Contains("://", StringComparison.Ordinal))
                link = "https://" + link;
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task ExportModAsync(ModData mod)
    {
        var layout = _layout;
        var tasks = _tasks;
        var messageBox = _messageBoxOverlay;
        if (layout is null || tasks is null || messageBox is null)
            return;
        var selection = ExportSettingsDialog.Show(layout.Window, localization);
        if (selection is null)
            return;
        var is7z = selection.Format.Contains("7z", StringComparison.OrdinalIgnoreCase);
        var extension = is7z ? "7z" : "zip";
        var save = new SaveFileDialog
        {
            Title = localization["DashboardPage.ExportSaveDialog"],
            FileName = $"{mod.Manifest.Name}.{extension}",
            Filter = is7z ? localization["Common.FileFilter7z"] : localization["Common.FileFilterZip"],
        };
        if (save.ShowDialog() != true)
            return;

        var level = selection.Level.Contains("Fast", StringComparison.OrdinalIgnoreCase)
            ? SharpCompressionLevel.Fast
            : selection.Level.Contains("High", StringComparison.OrdinalIgnoreCase)
                ? SharpCompressionLevel.High
                : selection.Level.Contains("Ultra", StringComparison.OrdinalIgnoreCase)
                    ? SharpCompressionLevel.Ultra : SharpCompressionLevel.Normal;
        var dict = level switch
        {
            SharpCompressionLevel.Fast => "8m",
            SharpCompressionLevel.High => "64m",
            SharpCompressionLevel.Ultra => "128m",
            _ => "32m",
        };
        var files = mod.Directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !IsExcludedArchiveFile(file)).ToArray();
        var totalSize = files.Sum(file => file.Length);
        if (level is SharpCompressionLevel.High or SharpCompressionLevel.Ultra
            && totalSize > 1024L * 1024 * 1024)
        {
            var sizeText = $"{totalSize / (1024.0 * 1024 * 1024):F2} GB";
            var dictText = level == SharpCompressionLevel.Ultra ? "128MB" : "64MB";
            var message = localization["DashboardPage.ExportMemoryMsgPrefix"] + sizeText
                + localization["DashboardPage.ExportMemoryMsgMid"] + selection.Level
                + localization["DashboardPage.ExportMemoryMsgCompression"] + dictText
                + localization["DashboardPage.ExportMemoryMsgSuffix"];
            if (!await messageBox.ConfirmAsync(localization["DashboardPage.ExportMemoryWarning"], message))
                return;
        }

        var task = tasks.Add(localization["DashboardPage.ExportSaveDialog"], mod.Manifest.Name, true);
        messageBox.ShowProgress(localization["DashboardPage.ExportSaveDialog"] + " - " + mod.Manifest.Name,
            localization["SettingsPage.PleaseWait"]);
        try
        {
            var progress = new Progress<ExportProgress>(value =>
            {
                messageBox.UpdateProgress(value.FileName, value.Ratio);
                tasks.Update(task, value.FileName, value.Ratio, false);
            });
            var encryption = ParseZipEncryption(selection.Encryption);
            await Task.Run(() => ExportArchive(mod.Directory, save.FileName, is7z, level, dict,
                selection.UsePassword ? selection.Password : null, encryption, progress));
            tasks.Complete(task, localization["BackgroundTasksPage.ExportComplete"].Replace("{name}", mod.Manifest.Name));
            messageBox.CloseProgress();
            messageBox.ShowInfo(localization["BackgroundTasksPage.ExportComplete"].Replace("{name}", mod.Manifest.Name));
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{save.FileName}\"") { UseShellExecute = true }); }
            catch { }
        }
        catch (Exception ex)
        {
            tasks.Fail(task, ex.Message);
            messageBox.CloseProgress();
            ReportError(new InvalidOperationException(localization["DashboardPage.ExportError"] + ex.Message, ex));
        }
    }

    private async Task UpdateModAsync(ModData mod)
    {
        var layout = _layout;
        var tasks = _tasks;
        var messageBox = _messageBoxOverlay;
        if (layout is null || tasks is null || messageBox is null)
            return;
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Filter = localization["Common.FileFilterArchive"],
            Multiselect = false,
            Title = localization["DashboardPage.UpdateModDialogPrefix"] + mod.Manifest.Name
                + localization["DashboardPage.UpdateModDialogSuffix"],
        };
        if (dialog.ShowDialog() != true)
            return;
        var task = tasks.Add(localization["DashboardPage.UpdateModProgress"], mod.Manifest.Name, true);
        messageBox.ShowProgress(localization["DashboardPage.UpdateModProgress"] + " - " + mod.Manifest.Name,
            localization["SettingsPage.PleaseWait"]);
        try
        {
            var service = CreateModService();
            await Task.Run(() => service.Init(settings));
            await service.HashMigrationTask;
            var progress = new Progress<UpdateProgressInfo>(info =>
            {
                var ratio = info.TotalCount > 0
                    ? (double)info.ProcessedCount / info.TotalCount : (double?)null;
                messageBox.UpdateProgress(info.Message ?? info.CurrentFile ?? string.Empty, ratio);
                tasks.Update(task, info.Message, info.TotalCount > 0
                    ? ratio : null, info.TotalCount <= 0);
            });
            await Task.Run(() => service.UpdateModFromArchiveAsync(mod, new FileInfo(dialog.FileName), progress));
            await workspace.RefreshAsync();
            tasks.Complete(task, localization["DashboardPage.UpdateModDone"]);
            messageBox.CloseProgress();
            messageBox.ShowInfo(localization["DashboardPage.UpdateModDone"]);
        }
        catch (Exception ex)
        {
            tasks.Fail(task, ex.Message);
            messageBox.CloseProgress();
            ReportError(new InvalidOperationException(localization["DashboardPage.UpdateModFailed"] + ex.Message, ex));
        }
    }

    private static bool IsExcludedArchiveFile(FileInfo file)
        => file.Extension is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz"
            || file.Name.EndsWith(".hd2mm-backup", StringComparison.OrdinalIgnoreCase)
            || file.Name.EndsWith(".hd2mm-backup.json", StringComparison.OrdinalIgnoreCase);

    private ZipEncryptionMethod ParseZipEncryption(string value)
        => value == localization["DashboardPage.ExportZipCrypto"] ? ZipEncryptionMethod.ZipCrypto
            : value == localization["DashboardPage.ExportAes128"] ? ZipEncryptionMethod.Aes128
            : value == localization["DashboardPage.ExportAes192"] ? ZipEncryptionMethod.Aes192
            : ZipEncryptionMethod.Aes256;

    private static void ExportArchive(DirectoryInfo directory, string outputPath, bool is7z,
        SharpCompressionLevel level, string dictSize, string? password, ZipEncryptionMethod encryption,
        IProgress<ExportProgress> progress)
    {
        var files = directory.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !IsExcludedArchiveFile(file)).ToArray();
        var total = files.Sum(file => file.Length);
        long processed = 0;
        void Report(string file, double ratio) => progress.Report(new ExportProgress(file, Math.Clamp(ratio, 0, 1)));
        if (is7z)
        {
            var native = Path.Combine(AppContext.BaseDirectory, "7z.dll");
            if (!File.Exists(native))
                throw new FileNotFoundException("7z.dll", native);
            SharpSevenZipBase.SetLibraryPath(native);
            var compressor = new SharpSevenZipCompressor
            {
                ArchiveFormat = OutArchiveFormat.SevenZip,
                CompressionMethod = CompressionMethod.Lzma2,
                CompressionLevel = level,
                DirectoryStructure = true,
                PreserveDirectoryRoot = false,
            };
            compressor.CustomParameters.Add("d", dictSize);
            string current = string.Empty;
            compressor.FileCompressionStarted += (_, args) => current = Path.GetFileName(args.FileName);
            compressor.Compressing += (_, args) => Report(current,
                Math.Clamp((int)args.PercentDone, 0, 100) / 100.0);
            var rootLength = directory.FullName.Length
                + (directory.FullName.EndsWith(Path.DirectorySeparatorChar) ? 0 : 1);
            if (string.IsNullOrEmpty(password))
                compressor.CompressFiles(outputPath, rootLength, files.Select(file => file.FullName).ToArray());
            else
            {
                compressor.EncryptHeaders = true;
                compressor.CompressFilesEncrypted(outputPath, rootLength, password,
                    files.Select(file => file.FullName).ToArray());
            }
        }
        else if (string.IsNullOrEmpty(password))
        {
            using var stream = new FileStream(outputPath, FileMode.Create);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                archive.CreateEntryFromFile(file.FullName,
                    Path.GetRelativePath(directory.FullName, file.FullName),
                    System.IO.Compression.CompressionLevel.Optimal);
                processed += file.Length;
                Report(file.Name, total == 0 ? 1 : (double)processed / total);
            }
        }
        else
        {
            var compressor = new SharpSevenZipCompressor
            {
                ArchiveFormat = OutArchiveFormat.Zip,
                CompressionMethod = CompressionMethod.Deflate,
                DirectoryStructure = true,
                PreserveDirectoryRoot = false,
                ZipEncryptionMethod = encryption,
            };
            string current = string.Empty;
            compressor.FileCompressionStarted += (_, args) => current = Path.GetFileName(args.FileName);
            compressor.Compressing += (_, args) => Report(current,
                Math.Clamp((int)args.PercentDone, 0, 100) / 100.0);
            var rootLength = directory.FullName.Length
                + (directory.FullName.EndsWith(Path.DirectorySeparatorChar) ? 0 : 1);
            compressor.CompressFilesEncrypted(outputPath, rootLength, password,
                files.Select(file => file.FullName).ToArray());
        }
        Report(string.Empty, 1);
    }

    private sealed record ExportProgress(string FileName, double Ratio);
}
