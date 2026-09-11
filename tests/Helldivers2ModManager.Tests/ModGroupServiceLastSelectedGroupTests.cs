using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// 「重启后恢复上次选中的配置文件」回归测试。
///
/// 历史缺陷：ModGroupService 只用内存字段 <c>_lastSelectedGroupId</c> 记录当前配置文件，
/// 该字段初始值恒为默认配置文件，重启后 InitAsync 的回落逻辑总是选中默认配置文件，
/// 用户上次切换到的自定义配置文件被静默丢弃。
///
/// 修复方式：把选择写入数据库 app_state 表（键 last_selected_group_id），
/// 切换（SelectGroupAsync）与删除（DeleteGroupAsync）时落盘，InitAsync 时恢复。
/// </summary>
[TestClass]
public sealed class ModGroupServiceLastSelectedGroupTests
{
    [TestMethod]
    public async Task InitAsync_RunAfterRestart_RestoresLastSelectedCustomGroup()
    {
        WithTempStorage(async settings =>
        {
            // 第一次启动：新建自定义配置文件并切换过去
            var first = CreateService(settings);
            await first.InitAsync(settings, Array.Empty<ModData>());
            Assert.IsTrue(first.SelectedGroup.IsDefault, "首次启动应落在默认配置文件");

            var custom = await first.CreateGroupAsync("测试配置");
            await first.SelectGroupAsync(custom.Id, Array.Empty<ModData>());
            Assert.AreEqual(custom.Id, first.SelectedGroup.Id);

            // 第二次启动：全新服务实例 + 同一个存储目录，等价于重新打开软件
            var second = CreateService(settings);
            await second.InitAsync(settings, Array.Empty<ModData>());

            Assert.AreEqual(custom.Id, second.SelectedGroup.Id,
                "重新打开软件后应恢复上次选中的配置文件，而不是回落到默认配置文件");
            Assert.IsFalse(second.SelectedGroup.IsDefault);
        }).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task InitAsync_PersistedGroupNoLongerExists_FallsBackToDefault()
    {
        WithTempStorage(async settings =>
        {
            var first = CreateService(settings);
            await first.InitAsync(settings, Array.Empty<ModData>());
            var custom = await first.CreateGroupAsync("将被删除的配置");
            await first.SelectGroupAsync(custom.Id, Array.Empty<ModData>());

            // 删除当前选中的配置文件：应同步把 app_state 回落为默认配置文件
            await first.DeleteGroupAsync(custom.Id);
            Assert.IsTrue(first.SelectedGroup.IsDefault, "删除当前配置文件后应立即回落到默认配置文件");

            var second = CreateService(settings);
            await second.InitAsync(settings, Array.Empty<ModData>());
            Assert.IsTrue(second.SelectedGroup.IsDefault,
                "被删除的配置文件不应被恢复，需回落到默认配置文件");
        }).GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task InitAsync_StaleGroupIdInDatabase_FallsBackToDefault()
    {
        WithTempStorage(async settings =>
        {
            // 直接写入一个库中不存在的配置文件 Id，模拟记录损坏/手工改库
            var repository = new ModGroupRepository(
                NullLogger<ModGroupRepository>.Instance,
                new DatabaseService(NullLogger<DatabaseService>.Instance));
            await repository.SaveLastSelectedGroupIdAsync(settings.StorageDirectory, Guid.NewGuid());

            var service = CreateService(settings);
            await service.InitAsync(settings, Array.Empty<ModData>());

            Assert.IsTrue(service.SelectedGroup.IsDefault,
                "指向不存在配置文件的记录应被忽略并回落到默认配置文件");
        }).GetAwaiter().GetResult();
    }

    private static ModGroupService CreateService(SettingsService settings)
    {
        var localization = new LocalizationService(
            NullLogger<LocalizationService>.Instance,
            Path.Combine(settings.StorageDirectory, "Language"));
        var repository = new ModGroupRepository(
            NullLogger<ModGroupRepository>.Instance,
            new DatabaseService(NullLogger<DatabaseService>.Instance));
        return new ModGroupService(NullLogger<ModGroupService>.Instance, repository, localization);
    }

    private static async Task WithTempStorage(Func<SettingsService, Task> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hd2mm-group-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = tempDir;

            var settings = new SettingsService(NullLogger<SettingsService>.Instance);
            await settings.InitAsync(false);
            settings.InitDefault(false);
            settings.StorageDirectory = Path.Combine(tempDir, "storage");

            await body(settings);
        }
        finally
        {
            Environment.CurrentDirectory = originalDir;
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不应让测试失败
            }
        }
    }
}
