using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed partial class LocalizationResourceTests
{
    [Fact]
    public void AllReleaseLocalesHaveIdenticalNonEmptyKeysAndPlaceholders()
    {
        var languageDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "Language");
        var files = Directory.EnumerateFiles(languageDirectory, "*.json").ToArray();
        Assert.Contains(files, static file => file.EndsWith("en-US.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, static file => file.EndsWith("zh-CN.json", StringComparison.OrdinalIgnoreCase));

        var locales = files.Select(ReadLocale).ToArray();
        var fallback = Assert.Single(locales, static locale => locale.Locale == "en-US");
        foreach (var locale in locales)
        {
            Assert.Equal(
                fallback.Strings.Keys.Order(StringComparer.Ordinal),
                locale.Strings.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, value) in locale.Strings)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{locale.Locale}:{key} is empty.");
                Assert.Equal(
                    Placeholders(fallback.Strings[key]),
                    Placeholders(value));
            }
        }
    }

    private static LocaleResource ReadLocale(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var locale = root.GetProperty("locale").GetString();
        Assert.False(string.IsNullOrWhiteSpace(locale));
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("strings").EnumerateObject())
        {
            Assert.True(strings.TryAdd(property.Name, property.Value.GetString() ?? string.Empty),
                $"Duplicate localization key {property.Name} in {path}.");
        }
        return new LocaleResource(locale!, strings);
    }

    private static string[] Placeholders(string value) => PlaceholderRegex()
        .Matches(value)
        .Select(static match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    [GeneratedRegex(@"(?<!\{)\{([A-Za-z_][A-Za-z0-9_]*)\}(?!\})", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    private sealed record LocaleResource(string Locale, IReadOnlyDictionary<string, string> Strings);
}
