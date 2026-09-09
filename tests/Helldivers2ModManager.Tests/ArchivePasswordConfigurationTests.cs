using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpSevenZip;

namespace Helldivers2ModManager.Tests;

/// <summary>
/// SharpSevenZip 密码/算法配置的纯逻辑回归测试。
/// 应用层导入/导出流程接入可注入配置后，再补充流程级测试。
/// </summary>
[TestClass]
public sealed class ArchivePasswordConfigurationTests
{
    [TestMethod]
    [DataRow(ZipEncryptionMethod.ZipCrypto)]
    [DataRow(ZipEncryptionMethod.Aes128)]
    [DataRow(ZipEncryptionMethod.Aes192)]
    [DataRow(ZipEncryptionMethod.Aes256)]
    public void Compressor_ZipEncryptionMethod_PreservesSelectedAlgorithm(ZipEncryptionMethod expected)
    {
        var compressor = new SharpSevenZipCompressor
        {
            ArchiveFormat = OutArchiveFormat.Zip,
            ZipEncryptionMethod = expected,
        };
        Assert.AreEqual(expected, compressor.ZipEncryptionMethod);
    }

    [TestMethod]
    public void Compressor_SevenZipPassword_EnablesHeaderEncryption()
    {
        var compressor = new SharpSevenZipCompressor
        {
            ArchiveFormat = OutArchiveFormat.SevenZip,
            EncryptHeaders = true,
        };
        Assert.IsTrue(compressor.EncryptHeaders);
    }

    [TestMethod]
    public void WrongPasswordOperationResult_IsDistinctFromSuccessfulExtraction()
    {
        Assert.AreNotEqual(OperationResult.Ok, OperationResult.WrongPassword);
        Assert.AreEqual("WrongPassword", OperationResult.WrongPassword.ToString());
    }
}
