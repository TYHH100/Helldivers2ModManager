using Helldivers2ModManager.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;

namespace Helldivers2ModManager.Core.Tests.Preview;

[TestClass]
public sealed class ModelPreviewAnimationLibraryParserTests
{
    [TestMethod]
    public void ParseBoneHashes_ReadsHashesAfterLodTable()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x87654321);

        var hashes = ModelPreviewAnimationLibraryParser.ParseBoneHashes(data);

        CollectionAssert.AreEqual(new uint[] { 0x12345678, 0x87654321 }, hashes.ToArray());
    }

    [TestMethod]
    public void ParseStateMachineAnimations_PreservesStateAndLayer()
    {
        var data = new byte[212];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 76);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(76), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(80), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(84), 0xAABBCCDDEEFF0011UL);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(96), 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(100), 0xAABBCCDDEEFF0011.GetHashCode());
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(112), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(116), 20);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(120), 0x1111222233334444UL);

        var references = ModelPreviewAnimationLibraryParser.ParseStateMachineAnimations(data);

        Assert.AreEqual(1, references.Count);
        Assert.AreEqual(0x1111222233334444UL, references[0].AnimationId);
        Assert.AreEqual(0, references[0].LayerIndex);
    }

    [TestMethod]
    public void ParseStateMachineAnimations_RejectsAnimationTableBeyondResource()
    {
        var data = new byte[124];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 76);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(76), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(80), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(84), 1UL);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(96), 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(112), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(116), 20);

        Assert.ThrowsException<InvalidDataException>(
            () => ModelPreviewAnimationLibraryParser.ParseStateMachineAnimations(data));
    }
}


