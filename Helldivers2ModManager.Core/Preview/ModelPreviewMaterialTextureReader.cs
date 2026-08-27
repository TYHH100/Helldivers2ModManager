namespace Helldivers2ModManager.Core.Preview;

public static class MaterialTextureReader
{
    public static ModelPreviewMaterialTextures? TryReadMaterialTextures(
        byte[] data,
        IReadOnlySet<ulong> availableTextureIds)
    {
        const int textureCountOffset = 0x40;
        const int textureTableOffset = 0x88;
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(availableTextureIds);
        if (data.Length < textureTableOffset || availableTextureIds.Count == 0)
            return null;

        var textureCount = ReadInt32(data, textureCountOffset);
        if (textureCount <= 0 || textureCount > 4096)
            return null;

        var semanticBytes = (long)textureCount * sizeof(uint);
        var textureIdsOffset = (long)textureTableOffset + semanticBytes;
        if (!IsRangeInBounds(textureTableOffset, semanticBytes, data.Length) ||
            !IsRangeInBounds(textureIdsOffset, (long)textureCount * sizeof(ulong), data.Length))
            return null;

        var textureIds = new List<ulong>();
        var texturesByRole = new Dictionary<ModelPreviewTextureRole, List<ulong>>();
        var inputs = new List<ModelPreviewMaterialInput>();
        ulong? colorTextureId = null;
        for (var index = 0; index < textureCount; index++)
        {
            var semanticId = ReadUInt32(data, textureTableOffset + index * sizeof(uint));
            var textureId = ReadUInt64(data, checked((int)(textureIdsOffset + index * sizeof(ulong))));
            if (!availableTextureIds.Contains(textureId))
                continue;

            textureIds.Add(textureId);
            var role = GetTextureRole(semanticId);
            inputs.Add(new ModelPreviewMaterialInput(semanticId, textureId, role));
            if (!texturesByRole.TryGetValue(role, out var roleIds))
                texturesByRole[role] = roleIds = [];
            roleIds.Add(textureId);
            if (colorTextureId is null && role == ModelPreviewTextureRole.BaseColor)
                colorTextureId = textureId;
        }

        return textureIds.Count > 0
            ? new ModelPreviewMaterialTextures(
                textureIds,
                colorTextureId,
                texturesByRole.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<ulong>)pair.Value),
                inputs)
            : null;
    }

    private static ModelPreviewTextureRole GetTextureRole(uint semanticId) => semanticId switch
    {
        0xE67AC0C7 or 0xAC652E43 or 0xFAEE8CB2 or 0x604318CD or 0x608D8147 or
        0x848BA63B or 0xFF2C91CC or 0x3AA8B87E => ModelPreviewTextureRole.BaseColor,
        0x7668E94B or 0xF5C97D31 or 0x2B33D35F or 0x5A3BC7C0 or
        0xCAED6CD6 or 0x1D57DCF3 => ModelPreviewTextureRole.Normal,
        0xE97A4617 or 0x85C8629F or 0x204EB619 or 0xE6E80465 or 0xE58FF005 or
        0x756F6FA6 or 0xCBDE381B => ModelPreviewTextureRole.Mask,
        0x12A0F5C0 or 0x4DC19F08 or 0x3E6E30E7 or 0xCA6F2CF1 => ModelPreviewTextureRole.Emissive,
        _ => ModelPreviewTextureRole.Unknown
    };

    private static bool IsRangeInBounds(long offset, long size, long total) =>
        offset >= 0 && size >= 0 && offset <= total && size <= total - offset;

    private static int ReadInt32(byte[] data, long offset) =>
        BitConverter.ToInt32(data, checked((int)offset));

    private static uint ReadUInt32(byte[] data, long offset) =>
        BitConverter.ToUInt32(data, checked((int)offset));

    private static ulong ReadUInt64(byte[] data, long offset) =>
        BitConverter.ToUInt64(data, checked((int)offset));
}
