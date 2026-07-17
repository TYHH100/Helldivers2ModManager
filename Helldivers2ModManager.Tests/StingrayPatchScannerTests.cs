using System.Buffers.Binary;
using Helldivers2ModManager.Infrastructure.Compatibility;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class StingrayPatchScannerTests
{
    private const long UnitTypeId = unchecked((long)16187218042980615487UL);

    [Fact]
    public async Task ScannerReadsUnitVersionAndOffsetsWithoutLoadingWholePatch()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var patchPath = Path.Combine(temporaryDirectory.Path, "sample.patch_0");
        var bytes = new byte[72 + 80 + 64];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(72, 8), 1234);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80, 8), UnitTypeId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88, 8), 152);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(128, 4), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(152 + 0x2C, 4), 42);
        await File.WriteAllBytesAsync(patchPath, bytes);

        var result = await new StingrayPatchScanner().ScanAsync(patchPath, CancellationToken.None);

        Assert.Empty(result.StructuralIssues);
        var unit = Assert.Single(result.Units);
        Assert.Equal(1234, unit.FileId);
        Assert.Equal((uint)42, unit.Version);
        Assert.Equal(152, unit.DataOffset);
        Assert.Equal(64, unit.DataSize);
    }

    [Fact]
    public async Task ScannerReportsOutOfBoundsFileTable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var patchPath = Path.Combine(temporaryDirectory.Path, "broken.patch_0");
        var header = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), 1);
        await File.WriteAllBytesAsync(patchPath, header);

        var result = await new StingrayPatchScanner().ScanAsync(patchPath, CancellationToken.None);

        Assert.Empty(result.Units);
        Assert.Equal(["Patch.FileTableOutOfBounds"], result.StructuralIssues);
    }
}
