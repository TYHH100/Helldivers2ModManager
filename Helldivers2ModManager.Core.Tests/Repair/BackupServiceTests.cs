using Helldivers2ModManager.Core.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Core.Tests.Repair;

[TestClass]
public sealed class BackupServiceTests
{
    [TestMethod]
    public async Task Metadata_ShouldDescribeBackup_AndRestoreExactBytes()
    {
        var root = Directory.CreateTempSubdirectory("hd2mm-backup-");
        try
        {
            var mod = root.CreateSubdirectory("mod");
            var original = Path.Combine(mod.FullName, "nested.patch_0");
            var backup = original + ".20260826-000000.hd2mm-backup";
            await File.WriteAllBytesAsync(original, (byte[])[1, 2, 3]);
            await File.WriteAllBytesAsync(backup, (byte[])[1, 2, 3]);
            var repaired = Path.Combine(root.FullName, "repaired.patch_0");
            await File.WriteAllBytesAsync(repaired, (byte[])[4, 5, 6]);

            Assert.IsTrue(await BackupService.TryWriteMetadataAsync(mod, backup, repaired, original, ModBackupRepairKind.SafeMetadata, 2));
            var service = new BackupService();
            var history = await service.GetHistoryAsync(mod);
            var metadata = history.Backups.Single();
            Assert.AreEqual(1, metadata.SchemaVersion);
            Assert.AreEqual("nested.patch_0", metadata.OriginalRelativePath);
            Assert.AreEqual(2, metadata.ActionCount);
            var backupHash = await BackupService.ComputeSha256Async(backup);
            var repairedHash = await BackupService.ComputeSha256Async(repaired);
            Assert.AreEqual(backupHash, metadata.BackupSha256);
            Assert.AreEqual(repairedHash, metadata.RepairedSha256);

            await File.WriteAllBytesAsync(original, (byte[])[9, 9]);
            var restored = await service.RestoreLatestAsync(mod, original);
            Assert.IsTrue(restored.Success, restored.ErrorMessage);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original));
        }
        finally { root.Delete(true); }
    }
}
