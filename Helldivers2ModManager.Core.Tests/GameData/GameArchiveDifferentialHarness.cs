using System.Reflection;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Core.Tests.GameData;

internal static class GameArchiveDifferentialHarness
{
    public readonly record struct LegacyUnitReference(
        long FileId,
        uint Version,
        byte[] LodGroupData,
        uint[] MeshIds,
        uint GpuSize,
        string PackageName);

    public static DirectoryInfo? FindGameDataDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "settings.json")))
            current = current.Parent;

        if (current is null) return null;
        var json = File.ReadAllText(Path.Combine(current.FullName, "settings.json"));
        var match = System.Text.RegularExpressions.Regex.Match(
            json,
            "\"GameDirectory\"\\s*:\\s*\"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value)) return null;

        var gameDirectory = Uri.UnescapeDataString(new Uri(match.Groups[1].Value).LocalPath);
        var dataDirectory = new DirectoryInfo(Path.Combine(gameDirectory, "data"));
        return dataDirectory.Exists && File.Exists(Path.Combine(dataDirectory.FullName, "bundles.nxa"))
            ? dataDirectory
            : null;
    }

    public static (object Service, object Index) BuildLegacyIndex(DirectoryInfo dataDirectory)
    {
        var assembly = Assembly.Load(new AssemblyName("Helldivers2ModManager"));
        var serviceType = RequireType(assembly, "Helldivers2ModManager.Services.VersionCheckService");
        var settingsType = RequireType(assembly, "Helldivers2ModManager.Services.SettingsService");
        var localizationType = RequireType(assembly, "Helldivers2ModManager.Services.LocalizationService");
        var constructor = serviceType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(item => item.GetParameters().Length == 3);
        var arguments = constructor.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == settingsType)
                return Activator.CreateInstance(settingsType, CreateLogger(settingsType))!;
            if (parameter.ParameterType == localizationType)
                return Activator.CreateInstance(localizationType, CreateLogger(localizationType))!;
            var serviceTypeArgument = parameter.ParameterType.IsGenericType
                ? parameter.ParameterType.GetGenericArguments()[0]
                : parameter.ParameterType;
            return CreateLogger(serviceTypeArgument);
        }).ToArray();
        var service = constructor.Invoke(arguments);
        var index = Invoke(service, "BuildGameUnitReferenceIndex", dataDirectory, Guid.NewGuid().ToString("N"));
        return (service, index!);
    }

    public static IReadOnlyList<long> SelectUnitIds(object index)
    {
        var locators = (System.Collections.IDictionary)GetProperty(index, "UnitLocators")!;
        var unitIds = locators.Keys.Cast<long>().OrderBy(id => id).ToArray();
        if (unitIds.Length <= 96) return unitIds;

        var selected = new List<long>(96);
        for (var index2 = 0; index2 < 96; index2++)
            selected.Add(unitIds[index2 * (unitIds.Length - 1) / 95]);
        return selected.Distinct().ToList();
    }

    public static LegacyUnitReference NormalizeLegacyReference(object item) => new(
        GetInt64(item, "FileId"),
        GetUInt32(item, "Version"),
        (byte[])GetProperty(item, "LodGroupData")!,
        ((uint[])GetProperty(item, "MeshIds")!).ToArray(),
        GetUInt32(item, "GpuSize"),
        GetString(item, "PackageName"));

    private static object? Invoke(object instance, string methodName, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method is null) throw new MissingMethodException(instance.GetType().FullName, methodName);
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(exception.InnerException.Message, exception.InnerException);
        }
    }

    private static object? GetProperty(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static long GetInt64(object item, string name) => Convert.ToInt64(GetPropertyOrField(item, name));
    private static uint GetUInt32(object item, string name) => Convert.ToUInt32(GetPropertyOrField(item, name));
    private static string GetString(object item, string name) => Convert.ToString(GetPropertyOrField(item, name), CultureInfo.InvariantCulture)!;

    private static object GetPropertyOrField(object item, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return item.GetType().GetProperty(name, flags)?.GetValue(item)
               ?? item.GetType().GetField(name, flags)?.GetValue(item)
               ?? throw new MissingMemberException(item.GetType().FullName, name);
    }

    private static Type RequireType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName) ?? throw new InvalidOperationException($"Unable to load {fullName}.");

    private static object CreateLogger(Type serviceType)
    {
        var loggerType = typeof(NullLogger<>).MakeGenericType(serviceType);
        var field = loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (field is not null) return field.GetValue(null)!;
        return loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
               ?? throw new MissingMemberException(loggerType.FullName, "Instance");
    }
}
