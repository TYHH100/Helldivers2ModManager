using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Helldivers2ModManager.Core.Preview;

public enum ModelPreviewAnimationChannel
{
    Position,
    Rotation,
    Scale
}

public readonly record struct ModelPreviewBonePose(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public static ModelPreviewBonePose Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One);
}

public readonly record struct ModelPreviewAnimationKeyframe(
    int BoneIndex,
    float TimeSeconds,
    ModelPreviewAnimationChannel Channel,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale);

public readonly record struct ModelPreviewAnimationEvent(
    uint EventId,
    float TimeSeconds);

public sealed class ModelPreviewAnimationClip
{
    public required ulong AnimationId { get; init; }
    public required int BoneCount { get; init; }
    public required float LengthSeconds { get; init; }
    public required bool IsAdditive { get; init; }
    public required IReadOnlyList<ModelPreviewBonePose> InitialPoses { get; init; }
    public required IReadOnlyList<ModelPreviewAnimationKeyframe> Keyframes { get; init; }
    public required IReadOnlyList<ModelPreviewAnimationEvent> Events { get; init; }
}

/// <summary>
/// Decodes the Stingray Animation TOC payload used by Helldivers 2.
/// Animation entries are sparse: the initial TRS pose is followed by per-channel
/// updates, rather than a complete pose for every frame.
/// </summary>
public static class ModelPreviewAnimationParser
{
    private const int MaxBones = 4096;
    private const int MaxHashCount = 4096;
    private const float PositionQuantizationScale = 10f / 32767f;
    private const float RotationQuantizationScale = 0.75f;

    public static bool TryParse(
        ReadOnlySpan<byte> data,
        ulong animationId,
        out ModelPreviewAnimationClip? clip,
        out string? error)
    {
        clip = null;
        error = null;
        try
        {
            clip = Parse(data, animationId);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static ModelPreviewAnimationClip Parse(ReadOnlySpan<byte> data, ulong animationId)
    {
        var reader = new AnimationReader(data);
        _ = reader.ReadUInt32("unknown header");
        var boneCount = reader.ReadInt32("bone count");
        if (boneCount < 0 || boneCount > MaxBones)
            throw new InvalidDataException($"Animation bone count {boneCount} is outside the supported range.");

        var lengthSeconds = reader.ReadSingle("animation length");
        if (!float.IsFinite(lengthSeconds) || lengthSeconds < 0)
            throw new InvalidDataException("Animation length is not a finite non-negative value.");

        _ = reader.ReadUInt32("file size");
        var hashesCount = reader.ReadInt32("hash count");
        var hashes2Count = reader.ReadInt32("secondary hash count");
        if (hashesCount < 0 || hashesCount > MaxHashCount || hashes2Count < 0 || hashes2Count > MaxHashCount)
            throw new InvalidDataException("Animation hash count is outside the supported range.");

        reader.Skip(checked((hashesCount + hashes2Count) * sizeof(ulong)), "hash tables");
        _ = reader.ReadUInt16("animation flags");

        var flagByteCount = checked((3 * boneCount + 7) / 8);
        if ((flagByteCount & 1) != 0)
            flagByteCount++;
        var compressionFlags = reader.ReadBytes(flagByteCount, "compression flags");

        var initialPoses = new ModelPreviewBonePose[boneCount];
        var isAdditive = false;
        for (var boneIndex = 0; boneIndex < boneCount; boneIndex++)
        {
            var positionCompressed = GetFlag(compressionFlags, boneIndex * 3);
            var rotationCompressed = GetFlag(compressionFlags, boneIndex * 3 + 1);
            var scaleCompressed = GetFlag(compressionFlags, boneIndex * 3 + 2);

            var position = positionCompressed
                ? ReadCompressedVector(ref reader, "initial position")
                : ReadVector3(ref reader, "initial position");
            var rotation = rotationCompressed
                ? ReadCompressedRotation(ref reader)
                : ReadQuaternion(ref reader, "initial rotation");
            var scale = scaleCompressed
                ? ReadCompressedVector(ref reader, "initial scale")
                : ReadVector3(ref reader, "initial scale");

            if (!IsFinite(position) || !IsFinite(rotation) || !IsFinite(scale))
                throw new InvalidDataException($"Initial pose for bone {boneIndex} contains a non-finite value.");

            initialPoses[boneIndex] = new ModelPreviewBonePose(position, rotation, scale);
            if (scale.X <= 0.00001f)
                isAdditive = true;
        }

        reader.Skip(checked(hashesCount * sizeof(float)), "hash float table");

        var keyframes = new List<ModelPreviewAnimationKeyframe>();
        var events = new List<ModelPreviewAnimationEvent>();
        while (true)
        {
            var marker = reader.PeekUInt16("animation entry marker");
            if (marker == 3)
            {
                _ = reader.ReadUInt16("animation terminator");
                break;
            }

            var header = reader.ReadBytes(4, "animation entry header");
            var type = (header[1] & 0xC0) >> 6;
            if (type == 0)
            {
                var subtype = BinaryPrimitives.ReadUInt16LittleEndian(header[..2]);
                // The SDK peeks four bytes to classify the entry, then seeks back
                // four bytes before reading the uncompressed subtype payload.
                reader.Rewind(4, "uncompressed entry header");
                _ = reader.ReadUInt16("uncompressed entry subtype");
                var entryId = reader.ReadUInt32("uncompressed entry identifier");
                var rawTimeSeconds = reader.ReadSingle("uncompressed entry time");
                if (!float.IsFinite(rawTimeSeconds) || rawTimeSeconds < 0)
                    throw new InvalidDataException("Animation entry time is not a finite non-negative value.");

                switch (subtype)
                {
                    case 2:
                        events.Add(new ModelPreviewAnimationEvent(entryId, rawTimeSeconds));
                        break;
                    case 4:
                        var positionBoneIndex = ValidateBoneIndex(entryId, boneCount);
                        keyframes.Add(new ModelPreviewAnimationKeyframe(
                            positionBoneIndex,
                            rawTimeSeconds,
                            ModelPreviewAnimationChannel.Position,
                            ReadVector3(ref reader, "position keyframe"),
                            Quaternion.Identity,
                            Vector3.One));
                        break;
                    case 5:
                        var rotationBoneIndex = ValidateBoneIndex(entryId, boneCount);
                        keyframes.Add(new ModelPreviewAnimationKeyframe(
                            rotationBoneIndex,
                            rawTimeSeconds,
                            ModelPreviewAnimationChannel.Rotation,
                            Vector3.Zero,
                            ReadQuaternion(ref reader, "rotation keyframe"),
                            Vector3.One));
                        break;
                    case 6:
                        var scaleBoneIndex = ValidateBoneIndex(entryId, boneCount);
                        keyframes.Add(new ModelPreviewAnimationKeyframe(
                            scaleBoneIndex,
                            rawTimeSeconds,
                            ModelPreviewAnimationChannel.Scale,
                            Vector3.Zero,
                            Quaternion.Identity,
                            ReadVector3(ref reader, "scale keyframe")));
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported uncompressed animation subtype {subtype}.");
                }

                continue;
            }

            var compressedBoneIndex = ((header[0] & 0xF0) >> 4) | ((header[1] & 0x3F) << 4);
            var timeMilliseconds = ((header[0] & 0x0F) << 16) | (header[3] << 8) | header[2];
            if (compressedBoneIndex < 0 || compressedBoneIndex >= boneCount)
                throw new InvalidDataException($"Animation entry bone index {compressedBoneIndex} is outside the bone table.");

            var compressedTimeSeconds = timeMilliseconds / 1000f;
            switch (type)
            {
                case 1:
                    keyframes.Add(new ModelPreviewAnimationKeyframe(
                        compressedBoneIndex,
                        compressedTimeSeconds,
                        ModelPreviewAnimationChannel.Scale,
                        Vector3.Zero,
                        Quaternion.Identity,
                        ReadCompressedVector(ref reader, "scale keyframe")));
                    break;
                case 2:
                    keyframes.Add(new ModelPreviewAnimationKeyframe(
                        compressedBoneIndex,
                        compressedTimeSeconds,
                        ModelPreviewAnimationChannel.Position,
                        ReadCompressedVector(ref reader, "position keyframe"),
                        Quaternion.Identity,
                        Vector3.One));
                    break;
                case 3:
                    keyframes.Add(new ModelPreviewAnimationKeyframe(
                        compressedBoneIndex,
                        compressedTimeSeconds,
                        ModelPreviewAnimationChannel.Rotation,
                        Vector3.Zero,
                        ReadCompressedRotation(ref reader),
                        Vector3.One));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported compressed animation type {type}.");
            }
        }

        keyframes.Sort(static (left, right) =>
        {
            var time = left.TimeSeconds.CompareTo(right.TimeSeconds);
            if (time != 0)
                return time;
            var bone = left.BoneIndex.CompareTo(right.BoneIndex);
            return bone != 0 ? bone : left.Channel.CompareTo(right.Channel);
        });

        return new ModelPreviewAnimationClip
        {
            AnimationId = animationId,
            BoneCount = boneCount,
            LengthSeconds = lengthSeconds,
            IsAdditive = isAdditive,
            InitialPoses = initialPoses,
            Keyframes = keyframes,
            Events = events
        };
    }

    private static bool GetFlag(ReadOnlySpan<byte> flags, int bitIndex) =>
        (flags[bitIndex / 8] & (1 << (bitIndex % 8))) != 0;

    private static int ValidateBoneIndex(uint value, int boneCount)
    {
        if (value >= boneCount)
            throw new InvalidDataException($"Animation entry bone index {value} is outside the bone table.");
        return (int)value;
    }

    private static Vector3 ReadCompressedVector(ref AnimationReader reader, string description)
    {
        var x = reader.ReadUInt16($"{description} X");
        var y = reader.ReadUInt16($"{description} Y");
        var z = reader.ReadUInt16($"{description} Z");
        return new Vector3(
            (x - 32767f) * PositionQuantizationScale,
            (y - 32767f) * PositionQuantizationScale,
            (z - 32767f) * PositionQuantizationScale);
    }

    private static Quaternion ReadCompressedRotation(ref AnimationReader reader)
    {
        var value = reader.ReadUInt32("compressed rotation");
        var first = (((value & 0xFFC) >> 2) - 512f) / 512f * RotationQuantizationScale;
        var second = (((value & 0x3FF000) >> 12) - 512f) / 512f * RotationQuantizationScale;
        var third = (((value & 0xFFC00000) >> 22) - 512f) / 512f * RotationQuantizationScale;
        var largestIndex = (int)(value & 0x3);
        var remainder = 1f - first * first - second * second - third * third;
        if (remainder < -0.001f)
            throw new InvalidDataException($"Compressed rotation has an invalid quaternion length at offset {reader.Offset}: 0x{value:X8} (remainder {remainder}).");

        var largest = MathF.Sqrt(MathF.Max(0, remainder));
        var result = largestIndex switch
        {
            0 => new Quaternion(largest, first, second, third),
            1 => new Quaternion(third, largest, first, second),
            2 => new Quaternion(second, third, largest, first),
            3 => new Quaternion(first, second, third, largest),
            _ => throw new InvalidDataException("Compressed rotation has an invalid largest-component index.")
        };
        return NormalizeQuaternion(result);
    }

    private static Vector3 ReadVector3(ref AnimationReader reader, string description) =>
        new(
            reader.ReadSingle($"{description} X"),
            reader.ReadSingle($"{description} Y"),
            reader.ReadSingle($"{description} Z"));

    private static Quaternion ReadQuaternion(ref AnimationReader reader, string description) =>
        NormalizeQuaternion(new Quaternion(
            reader.ReadSingle($"{description} X"),
            reader.ReadSingle($"{description} Y"),
            reader.ReadSingle($"{description} Z"),
            reader.ReadSingle($"{description} W")));

    private static Quaternion NormalizeQuaternion(Quaternion value)
    {
        if (!IsFinite(value))
            throw new InvalidDataException("Animation quaternion is not finite.");
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.000001f)
            throw new InvalidDataException("Animation quaternion has no usable length.");
        return Quaternion.Normalize(value);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private ref struct AnimationReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public AnimationReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public uint ReadUInt32(string description)
        {
            Ensure(sizeof(uint), description);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
            _offset += sizeof(uint);
            return value;
        }

        public int ReadInt32(string description)
        {
            Ensure(sizeof(int), description);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data[_offset..]);
            _offset += sizeof(int);
            return value;
        }

        public ushort ReadUInt16(string description)
        {
            Ensure(sizeof(ushort), description);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
            _offset += sizeof(ushort);
            return value;
        }

        public float ReadSingle(string description)
        {
            var bits = ReadUInt32(description);
            return BitConverter.UInt32BitsToSingle(bits);
        }

        public ReadOnlySpan<byte> ReadBytes(int count, string description)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            Ensure(count, description);
            var value = _data.Slice(_offset, count);
            _offset += count;
            return value;
        }

        public ushort PeekUInt16(string description)
        {
            Ensure(sizeof(ushort), description);
            return BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
        }

        public void Skip(int count, string description) => _ = ReadBytes(count, description);

        public void Rewind(int count, string description)
        {
            if (count < 0 || count > _offset)
                throw new InvalidDataException($"Animation {description} rewinds before the resource start.");
            _offset -= count;
        }

        public int Offset => _offset;

        private void Ensure(int count, string description)
        {
            if (count < 0 || _offset < 0 || count > _data.Length - _offset)
                throw new InvalidDataException($"Animation {description} exceeds the resource boundary.");
        }
    }
}

