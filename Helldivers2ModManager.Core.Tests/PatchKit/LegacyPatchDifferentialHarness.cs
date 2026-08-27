using System.Collections;
using System.Globalization;
using System.Reflection;
using Helldivers2ModManager.Core.PatchKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Core.Tests.PatchKit;

internal static class LegacyPatchDifferentialHarness
{
    private const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;

    public static IReadOnlyList<FileInfo> GetMainPatchFiles(DirectoryInfo root) =>
        root.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => file.Name.Contains(".patch_", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.Contains(".hd2mm-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => Path.GetRelativePath(root.FullName, file.FullName), StringComparer.Ordinal)
            .ToArray();

    public static async Task<IReadOnlyList<PatchParseResult>> ParseWithCoreAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PatchParseResult>();
        foreach (var file in GetMainPatchFiles(root))
        {
            results.Add(await new PatchFileParser().ParseFileAsync(
                file,
                PatchKitOptions.Default,
                cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public static async Task<IReadOnlyList<LegacyTocEntry>> InspectLegacyTocAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.Load(new AssemblyName("Helldivers2ModManager"));
        var serviceType = RequireType(assembly, "Helldivers2ModManager.Services.PatchResourceInspectionService");
        var service = Activator.CreateInstance(serviceType) ?? throw new InvalidOperationException("Unable to create legacy inspection service.");
        var task = InvokeAsync(service, "InspectAsync", root, cancellationToken);
        await task.ConfigureAwait(false);
        return ToObjects(GetProperty(GetResult(task), "TocEntries")).Select(CreateLegacyTocEntry).ToArray();
    }

    public static async Task<IReadOnlyList<LegacyGpuStream>> InspectLegacyGpuStreamsAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.Load(new AssemblyName("Helldivers2ModManager"));
        var serviceType = RequireType(assembly, "Helldivers2ModManager.Services.PatchResourceInspectionService");
        var service = Activator.CreateInstance(serviceType) ?? throw new InvalidOperationException("Unable to create legacy inspection service.");
        var task = InvokeAsync(service, "InspectAsync", root, cancellationToken);
        await task.ConfigureAwait(false);
        return ToObjects(GetProperty(GetResult(task), "GpuStreams")).Select(CreateLegacyGpuStream).ToArray();
    }

    public static async Task<IReadOnlyList<LegacyPatchStructure>> AnalyzeLegacyStructureAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.Load(new AssemblyName("Helldivers2ModManager"));
        var versionType = RequireType(assembly, "Helldivers2ModManager.Services.VersionCheckService");
        var settingsType = RequireType(assembly, "Helldivers2ModManager.Services.SettingsService");
        var localizationType = RequireType(assembly, "Helldivers2ModManager.Services.LocalizationService");
        var constructor = versionType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(item => item.GetParameters().Length == 3);
        var arguments = constructor.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == settingsType) return Activator.CreateInstance(settingsType, CreateNullLogger(settingsType))!;
            if (parameter.ParameterType == localizationType) return Activator.CreateInstance(localizationType, CreateNullLogger(localizationType))!;
            var serviceType = parameter.ParameterType.IsGenericType ? parameter.ParameterType.GetGenericArguments()[0] : parameter.ParameterType;
            return CreateNullLogger(serviceType);
        }).ToArray();
        var versionService = constructor.Invoke(arguments) ?? throw new InvalidOperationException("Unable to create legacy version-check service.");

        var task = InvokeAsync(versionService, "AnalyzePatchDirectoryAsync", root);
        await task.ConfigureAwait(false);
        return ToObjects(GetProperty(GetResult(task), "PatchFiles")).Select(CreateLegacyStructure).ToArray();
    }

    private static LegacyTocEntry CreateLegacyTocEntry(object item) => new(
        GetString(item, "PatchFile"),
        GetInt32(item, "EntryIndex"),
        GetUInt64(item, "FileId"),
        GetUInt64(item, "TypeId"),
        GetUInt64(item, "MainOffset"),
        GetUInt64(item, "StreamOffset"),
        GetUInt64(item, "GpuOffset"),
        GetUInt32(item, "MainSize"),
        GetUInt32(item, "StreamSize"),
        GetUInt32(item, "GpuSize"));

    private static LegacyGpuStream CreateLegacyGpuStream(object item) => new(
        GetString(item, "PatchFile"),
        GetInt32(item, "TocEntryIndex"),
        GetUInt64(item, "UnitId"),
        GetUInt32(item, "UnitVersion"),
        GetInt32(item, "StreamIndex"),
        GetUInt32(item, "VertexCount"),
        GetUInt32(item, "VertexStride"),
        GetUInt32(item, "IndexCount"),
        GetString(item, "IndexFormat"),
        GetString(item, "Components"),
        GetString(item, "VertexBuffer"),
        GetString(item, "IndexBuffer"));

    private static LegacyPatchStructure CreateLegacyStructure(object item) => new(
        GetString(item, "FileName"),
        GetInt32(item, "NumTypes"),
        GetInt32(item, "NumFiles"),
        GetInt32(item, "EntryIndexIssueCount"),
        GetInt32(item, "TypeDistributionIssueCount"),
        GetInt32(item, "MainDataIssueCount"),
        GetInt32(item, "GpuResourceIssueCount"),
        GetInt32(item, "GpuAlignmentIssueCount"),
        GetInt32(item, "StreamIssueCount"),
        GetInt32(item, "StreamAlignmentIssueCount"));

    public static PatchTocComparison CreateCoreToc(PatchFileSnapshot snapshot, PatchTocEntry entry, string relativePath) => new(
        relativePath,
        entry.Index,
        entry.FileId,
        entry.TypeId,
        entry.MainOffset,
        entry.StreamOffset,
        entry.GpuOffset,
        entry.MainSize,
        entry.StreamSize,
        entry.GpuSize);

    public static PatchTocComparison NormalizeLegacyToc(LegacyTocEntry item) => new(
        item.RelativePath,
        item.EntryIndex,
        item.FileId,
        item.TypeId,
        item.MainOffset,
        item.StreamOffset,
        item.GpuOffset,
        item.MainSize,
        item.StreamSize,
        item.GpuSize);

    public static PatchGpuComparison NormalizeLegacyGpuStream(LegacyGpuStream item) => new(
        item.RelativePath,
        item.TocEntryIndex,
        item.UnitId,
        item.UnitVersion,
        item.StreamIndex,
        item.VertexCount,
        item.VertexStride,
        item.IndexCount,
        item.IndexFormat,
        item.Components,
        item.VertexBuffer,
        item.IndexBuffer);

    public static PatchGpuComparison CreateCoreGpuStream(string relativePath, PatchUnitSnapshot unit, PatchGpuStreamSnapshot stream)
    {
        var components = string.Join(" | ", stream.Components.Select(component =>
            $"{SemanticName(component.Semantic)}[{component.Index}]: {FormatName(component.Format)}"));
        return new(
            relativePath,
            unit.TocEntryIndex,
            unit.UnitId,
            unit.Version,
            stream.StreamIndex,
            stream.VertexCount,
            stream.VertexStride,
            stream.IndexCount,
            IndexTypeName(stream.IndexType),
            components,
            FormatBuffer(stream.VertexBufferOffset, stream.VertexBufferSize),
            FormatBuffer(stream.IndexBufferOffset, stream.IndexBufferSize));
    }

    public static string SemanticName(uint semantic) => semantic switch
    {
        0 => "Position",
        1 => "Normal",
        2 => "Tangent",
        3 => "Bitangent",
        4 => "UV",
        5 => "Color",
        6 => "BoneIndex",
        7 => "BoneWeight",
        _ => $"Type 0x{semantic:X}",
    };

    public static string FormatName(uint format) => format switch
    {
        0 => "float",
        1 => "float2",
        2 => "float3",
        4 => "RGBA8",
        20 => "uint32x4 (legacy)",
        24 => "uint8x4 (legacy)",
        26 => "oct-normal (legacy)",
        28 => "uint8x4",
        29 => "half2 (legacy)",
        30 => "oct-normal",
        31 => "half4 (legacy)",
        33 => "half2",
        35 => "half4",
        _ => $"Format 0x{format:X}",
    };

    public static string IndexTypeName(uint indexType) => indexType switch
    {
        0 => "uint16",
        1 => "uint32",
        _ => $"unknown ({indexType})",
    };

    public static string FormatBuffer(uint offset, uint size)
    {
        var number = size.ToString("N0", CultureInfo.InvariantCulture);
        return $"0x{offset:X} + {number}";
    }

    private static object CreateNullLogger(Type serviceType)
    {
        var loggerType = typeof(NullLogger<>).MakeGenericType(serviceType);
        var field = loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field is not null) return field.GetValue(null)!;
        var property = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return property?.GetValue(null) ?? throw new MissingMemberException(loggerType.FullName, "Instance");
    }

    private static Type RequireType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: true)!;

    private static Task InvokeAsync(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(method => method.Name == methodName &&
                              method.GetParameters().Length == arguments.Length &&
                              arguments.Select(argument => argument.GetType()).Zip(
                                  method.GetParameters(),
                                  static (argumentType, parameter) => parameter.ParameterType.IsAssignableFrom(argumentType)).All(static matched => matched));
        return (Task)(method.Invoke(target, arguments) ?? throw new InvalidOperationException($"{methodName} returned null."));
    }

    private static object GetResult(Task task)
    {
        var property = task.GetType().GetProperty("Result") ?? throw new InvalidOperationException("Task has no Result.");
        return property.GetValue(task) ?? throw new InvalidOperationException("Task returned null.");
    }

    private static object GetStaticProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException(type.FullName, name);

    private static object GetProperty(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? throw new MissingMemberException(target.GetType().FullName, name);

    private static IReadOnlyList<object> ToObjects(object value) =>
        value is IEnumerable collection ? collection.Cast<object>().ToArray() : [];

    private static string GetString(object target, string name) => (string)GetProperty(target, name);
    private static int GetInt32(object target, string name) => (int)GetProperty(target, name);
    private static uint GetUInt32(object target, string name) => (uint)GetProperty(target, name);
    private static ulong GetUInt64(object target, string name) => (ulong)GetProperty(target, name);
}

internal sealed record LegacyTocEntry(
    string RelativePath,
    int EntryIndex,
    ulong FileId,
    ulong TypeId,
    ulong MainOffset,
    ulong StreamOffset,
    ulong GpuOffset,
    uint MainSize,
    uint StreamSize,
    uint GpuSize);

internal sealed record LegacyGpuStream(
    string RelativePath,
    int TocEntryIndex,
    ulong UnitId,
    uint UnitVersion,
    int StreamIndex,
    uint VertexCount,
    uint VertexStride,
    uint IndexCount,
    string IndexFormat,
    string Components,
    string VertexBuffer,
    string IndexBuffer);

internal sealed record LegacyPatchStructure(
    string FileName,
    int NumTypes,
    int NumFiles,
    int EntryIndexIssueCount,
    int TypeDistributionIssueCount,
    int MainDataIssueCount,
    int GpuResourceIssueCount,
    int GpuAlignmentIssueCount,
    int StreamIssueCount,
    int StreamAlignmentIssueCount);

internal sealed record PatchTocComparison(
    string RelativePath,
    int EntryIndex,
    ulong FileId,
    ulong TypeId,
    ulong MainOffset,
    ulong StreamOffset,
    ulong GpuOffset,
    uint MainSize,
    uint StreamSize,
    uint GpuSize);

internal sealed record PatchGpuComparison(
    string RelativePath,
    int TocEntryIndex,
    ulong UnitId,
    uint UnitVersion,
    int StreamIndex,
    uint VertexCount,
    uint VertexStride,
    uint IndexCount,
    string IndexFormat,
    string Components,
    string VertexBuffer,
    string IndexBuffer);
