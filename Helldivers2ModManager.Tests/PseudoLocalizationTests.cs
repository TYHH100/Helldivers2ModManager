using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helldivers2ModManager.Tests;

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariableSerialGroup
{
}

[Collection("EnvironmentVariables")]
public sealed class PseudoLocalizationTests
{
    [Fact]
    public void PseudoLocalizerExpandsTextAndPreservesNamedPlaceholdersAndNewlines()
    {
        const string source = "Delete {modName}?\nContinue";

        var result = PseudoLocalizer.Transform(source);

        Assert.StartsWith("⟦ ", result, StringComparison.Ordinal);
        Assert.EndsWith(" ⟧", result, StringComparison.Ordinal);
        Assert.Contains("{modName}", result, StringComparison.Ordinal);
        Assert.Contains('\n', result);
        Assert.True(result.Length >= source.Length * 1.8);
    }

    [Fact]
    public void RuntimePseudoLocaleRaisesChangeAndUsesFallbackFormattingCulture()
    {
        var previous = Environment.GetEnvironmentVariable("HD2MM_ENABLE_PSEUDO_LOCALIZATION");
        try
        {
            Environment.SetEnvironmentVariable("HD2MM_ENABLE_PSEUDO_LOCALIZATION", "1");
            var localizer = new LocalizationService(NullLogger<LocalizationService>.Instance);
            var changed = false;
            localizer.LocaleChanged += (_, _) => changed = true;

            localizer.SelectedLanguage = "qps-ploc";

            Assert.True(changed);
            Assert.Equal("qps-ploc", localizer.CurrentLocale);
            Assert.Contains("qps-ploc", localizer.InstalledLocales.Select(static locale => locale.Locale));
            Assert.StartsWith("⟦ ", localizer["MainWindow.Help"], StringComparison.Ordinal);
            Assert.Equal("en-US", System.Globalization.CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HD2MM_ENABLE_PSEUDO_LOCALIZATION", previous);
        }
    }
}
