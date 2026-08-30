using System.IO;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// DatabaseService 多存储目录回归测试：
/// 同进程内先后对两个不同 storageDirectory 调用 OpenConnection 时，
/// 必须各自初始化并连接各自的 mod_manager.db，
/// 而不是首次调用后把路径钉死、后续传参被静默忽略（历史缺陷）。
/// </summary>
[TestClass]
public class DatabaseServiceMultiPathTests
{
    [TestMethod]
    public void OpenConnection_TwoStorageDirectories_GetIndependentDatabases()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Error));
        var service = new DatabaseService(loggerFactory.CreateLogger<DatabaseService>());
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-dbtest-" + Guid.NewGuid().ToString("N"));
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");

        try
        {
            // A 库保持默认 user_version=0；B 库标记为 7。
            // 若路径被钉死，B 的写入会落到 A 库，A 读到的将是 7。
            using (var connA = service.OpenConnection(dirA))
            {
            }
            using (var connB = service.OpenConnection(dirB))
            using (var cmd = connB.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version = 7;";
                cmd.ExecuteNonQuery();
            }

            using (var connA = service.OpenConnection(dirA))
            using (var cmd = connA.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                var versionA = (long)cmd.ExecuteScalar()!;
                Assert.AreEqual(0L, versionA, "A 库不应被 B 库的写入影响（路径被钉死时会读到 7）");
            }

            Assert.IsTrue(File.Exists(Path.Combine(dirA, "mod_manager.db")), "A 目录应有独立数据库文件");
            Assert.IsTrue(File.Exists(Path.Combine(dirB, "mod_manager.db")), "B 目录应有独立数据库文件");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
