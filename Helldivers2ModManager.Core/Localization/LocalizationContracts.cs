using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Helldivers2ModManager.Core.Localization;

public sealed record LocaleDescriptor(
    string Locale,
    string DisplayName,
    string SuggestedFontFamily,
    string? FallbackLocale,
    bool IsPseudoLocale = false);

public interface ILocalizer
{
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Get is the fixed public localization contract.")]
    string Get(string key);

    string Format(string key, object arguments);

    string CurrentLocale { get; }

    event EventHandler? LocaleChanged;
}

public interface ILocaleCatalog
{
    IReadOnlyList<LocaleDescriptor> InstalledLocales { get; }

    LocaleDescriptor? Find(string locale);
}

public static class PseudoLocalizer
{
    private static readonly Dictionary<char, char> s_accents = new()
    {
        ['a'] = 'á',
        ['b'] = 'ƀ',
        ['c'] = 'ç',
        ['d'] = 'ď',
        ['e'] = 'é',
        ['f'] = 'ƒ',
        ['g'] = 'ğ',
        ['h'] = 'ħ',
        ['i'] = 'í',
        ['j'] = 'ĵ',
        ['k'] = 'ķ',
        ['l'] = 'ľ',
        ['m'] = 'ɱ',
        ['n'] = 'ñ',
        ['o'] = 'ó',
        ['p'] = 'þ',
        ['q'] = 'ɋ',
        ['r'] = 'ř',
        ['s'] = 'š',
        ['t'] = 'ť',
        ['u'] = 'ú',
        ['v'] = 'ṽ',
        ['w'] = 'ŵ',
        ['x'] = 'ẋ',
        ['y'] = 'ý',
        ['z'] = 'ž'
    };

    public static string Transform(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "⟦ ⟧";

        var builder = new System.Text.StringBuilder(value.Length * 2 + 4);
        builder.Append("⟦ ");
        var inPlaceholder = false;
        foreach (var character in value)
        {
            if (character == '{')
                inPlaceholder = true;
            if (!inPlaceholder && s_accents.TryGetValue(char.ToLowerInvariant(character), out var accented))
            {
                builder.Append(char.IsUpper(character) ? char.ToUpperInvariant(accented) : accented);
            }
            else
            {
                builder.Append(character);
            }
            if (character == '}')
                inPlaceholder = false;
        }

        builder.Append(' ');
        var targetLength = checked((int)Math.Ceiling(value.Length * 1.9)) + 4;
        builder.Append('~', Math.Max(2, targetLength - builder.Length - 2));
        builder.Append(" ⟧");
        return builder.ToString();
    }

    public static CultureInfo FormattingCulture => CultureInfo.GetCultureInfo("en-US");
}
