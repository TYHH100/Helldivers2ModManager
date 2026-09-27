using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class MessageBoxOverlayTests
{
    [TestMethod]
    public async Task Confirmation_CancelKeepsLaterErrorVisible()
    {
        var overlay = CreateOverlay();
        var confirmation = overlay.ConfirmAsync("Delete mod", "Remove this mod?");
        overlay.ShowError("Later error");
        Assert.IsTrue(overlay.IsOpen);
        Assert.AreEqual("Delete mod", overlay.Title);
        Assert.AreEqual("Remove this mod?", overlay.Message);

        ClickButton(overlay, 0);

        Assert.IsFalse(await confirmation);
        Assert.AreEqual("Later error", overlay.Message);
        ClickButton(overlay, 1);
        Assert.IsFalse(overlay.IsOpen);
        overlay.Dispose();
    }

    [TestMethod]
    public async Task WindowClose_CancelsActiveAndQueuedConfirmations()
    {
        var overlay = CreateOverlay();
        var active = overlay.ConfirmAsync("First", "First message");
        var queued = overlay.ConfirmAsync("Second", "Second message");

        overlay.Dispose();

        Assert.IsFalse(await active);
        Assert.IsFalse(await queued);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public void Progress_UpdatesAndReturnsToMessageQueue()
    {
        using var overlay = CreateOverlay();
        overlay.ShowProgress("Export", "Preparing files");
        Assert.IsTrue(overlay.IsOpen);
        Assert.AreEqual("Export", overlay.Title);

        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var progressPanel = (StackPanel)layout.Children[7];
        Assert.AreEqual("Preparing files", ((TextBlock)progressPanel.Children[0]).Text);
        var progressBar = (ProgressBar)progressPanel.Children[1];
        Assert.IsTrue(progressBar.IsIndeterminate);

        overlay.UpdateProgress("file.zip", 0.4);
        Assert.AreEqual("file.zip", ((TextBlock)progressPanel.Children[0]).Text);
        Assert.AreEqual(0.4, progressBar.Value, 0.001);
        Assert.IsFalse(progressBar.IsIndeterminate);

        overlay.CloseProgress();
        Assert.IsFalse(overlay.IsOpen);
        overlay.ShowInfo("Finished");
        Assert.IsTrue(overlay.IsOpen);
        Assert.AreEqual("Finished", overlay.Message);
    }

    [TestMethod]
    public async Task PasswordPrompt_ReturnsInputAndClearsItBeforeNextPrompt()
    {
        using var overlay = CreateOverlay();
        var first = overlay.PromptPasswordAsync("First archive", "Password required");
        var second = overlay.PromptPasswordAsync("Second archive", "Password required");
        var input = GetPasswordBox(overlay);
        input.Password = "first-password";

        ClickButton(overlay, 1);

        Assert.AreEqual("first-password", await first);
        Assert.AreEqual("Second archive", overlay.Title);
        Assert.AreEqual(string.Empty, input.Password);
        input.Password = "second-password";
        ClickButton(overlay, 0);
        Assert.IsNull(await second);
        Assert.AreEqual(string.Empty, input.Password);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task WindowClose_CancelsActiveAndQueuedPasswordPrompts()
    {
        var overlay = CreateOverlay();
        var active = overlay.PromptPasswordAsync("First", "Password required");
        var queued = overlay.PromptPasswordAsync("Second", "Password required");
        GetPasswordBox(overlay).Password = "private";

        overlay.Dispose();

        Assert.IsNull(await active);
        Assert.IsNull(await queued);
        Assert.AreEqual(string.Empty, GetPasswordBox(overlay).Password);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task TextPrompt_ValidatesAndPreservesQueuedRequests()
    {
        using var overlay = CreateOverlay();
        var first = overlay.PromptAsync("Rename", "Enter a name", "old", 16,
            value => string.IsNullOrWhiteSpace(value) ? "Name is required" : null);
        var second = overlay.PromptAsync("Link", "Enter a link", "https://example.com", 64);
        var input = GetTextBox(overlay);

        Assert.AreEqual("old", input.Text);
        Assert.AreEqual(16, input.MaxLength);
        input.Text = "   ";
        ClickButton(overlay, 1);
        Assert.IsTrue(overlay.IsOpen);
        Assert.AreEqual("Rename", overlay.Title);
        Assert.AreEqual("Name is required", GetInputError(overlay).Text);
        Assert.IsFalse(first.IsCompleted);

        input.Text = "new name";
        ClickButton(overlay, 1);
        Assert.AreEqual("new name", await first);
        Assert.AreEqual("Link", overlay.Title);
        Assert.AreEqual("https://example.com", input.Text);
        ClickButton(overlay, 0);
        Assert.IsNull(await second);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task WindowClose_CancelsActiveAndQueuedTextPrompts()
    {
        var overlay = CreateOverlay();
        var active = overlay.PromptAsync("First", "Enter text");
        var queued = overlay.PromptAsync("Second", "Enter text");

        overlay.Dispose();

        Assert.IsNull(await active);
        Assert.IsNull(await queued);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task MultiSelection_PreservesInitialChoicesAndQueueOrder()
    {
        using var overlay = CreateOverlay();
        var first = overlay.SelectManyAsync("Tags", "Choose tags",
            [new MessageBoxSelectionOption("Alpha", Detail: "First option"), "Beta", "Gamma"], [1]);
        var second = overlay.SelectManyAsync("Groups", "Choose groups", ["Default", "Custom"], [0]);
        var checks = GetSelectionChecks(overlay);

        Assert.AreEqual(3, checks.Count);
        Assert.IsFalse(checks[0].IsChecked == true);
        Assert.IsTrue(checks[1].IsChecked == true);
        var optionContent = (StackPanel)checks[0].Content!;
        var optionDescription = (StackPanel)optionContent.Children[0];
        Assert.AreEqual("Alpha", ((TextBlock)optionDescription.Children[0]).Text);
        Assert.AreEqual("First option", ((TextBlock)optionDescription.Children[1]).Text);
        checks[0].IsChecked = true;
        ClickButton(overlay, 1);

        CollectionAssert.AreEqual(new[] { 0, 1 }, (await first)!.ToArray());
        Assert.AreEqual("Groups", overlay.Title);
        Assert.IsTrue(GetSelectionChecks(overlay)[0].IsChecked == true);
        ClickButton(overlay, 0);
        Assert.IsNull(await second);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task WindowClose_CancelsActiveAndQueuedMultiSelections()
    {
        var overlay = CreateOverlay();
        var active = overlay.SelectManyAsync("First", "Choose", ["A"]);
        var queued = overlay.SelectManyAsync("Second", "Choose", ["B"]);

        overlay.Dispose();

        Assert.IsNull(await active);
        Assert.IsNull(await queued);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task MultiSelection_CommitFailureKeepsChoicesForRetry()
    {
        using var overlay = CreateOverlay();
        var attempts = 0;
        var result = overlay.SelectManyAsync("Groups", "Choose groups", ["Default", "Custom"], [1], _ =>
            Task.FromResult(++attempts == 1 ? "Save failed" : null));

        ClickButton(overlay, 1);

        Assert.IsTrue(overlay.IsOpen);
        Assert.AreEqual("Save failed", GetSelectionError(overlay).Text);
        Assert.IsTrue(GetSelectionChecks(overlay)[1].IsChecked == true);
        ClickButton(overlay, 1);
        CollectionAssert.AreEqual(new[] { 1 }, (await result)!.ToArray());
        Assert.AreEqual(2, attempts);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task SingleSelection_ReturnsIndexAndCancelReturnsNull()
    {
        using var overlay = CreateOverlay();
        var first = overlay.ChooseOneAsync("Report", "Did the game crash?", ["Crashed", "Fine", "Cancel"]);
        var second = overlay.ChooseOneAsync("Confirm", "Continue?", ["No", "Yes"], initialIndex: 1);
        var combo = GetSingleSelection(overlay);

        Assert.AreEqual(0, combo.SelectedIndex);
        combo.SelectedIndex = 2;
        ClickButton(overlay, 1);
        Assert.AreEqual(2, await first);
        Assert.AreEqual("Confirm", overlay.Title);
        Assert.AreEqual(1, GetSingleSelection(overlay).SelectedIndex);
        ClickButton(overlay, 0);
        Assert.IsNull(await second);
        Assert.IsFalse(overlay.IsOpen);
    }

    [TestMethod]
    public async Task WindowClose_CancelsActiveAndQueuedSingleSelections()
    {
        var overlay = CreateOverlay();
        var active = overlay.ChooseOneAsync("First", "Choose", ["A"]);
        var queued = overlay.ChooseOneAsync("Second", "Choose", ["B"]);

        overlay.Dispose();

        Assert.IsNull(await active);
        Assert.IsNull(await queued);
        Assert.IsFalse(overlay.IsOpen);
    }

    private static void ClickButton(MessageBoxOverlay overlay, int index)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var buttons = (StackPanel)layout.Children[2];
        ((Button)buttons.Children[index]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static PasswordBox GetPasswordBox(MessageBoxOverlay overlay)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        return (PasswordBox)layout.Children[3];
    }

    private static TextBox GetTextBox(MessageBoxOverlay overlay)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var panel = (StackPanel)layout.Children[4];
        return (TextBox)panel.Children[0];
    }

    private static TextBlock GetInputError(MessageBoxOverlay overlay)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var panel = (StackPanel)layout.Children[4];
        return (TextBlock)panel.Children[1];
    }

    private static IReadOnlyList<CheckBox> GetSelectionChecks(MessageBoxOverlay overlay)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var panel = (StackPanel)layout.Children[5];
        var scroll = (ScrollViewer)panel.Children[0];
        var items = (StackPanel)scroll.Content!;
        return items.Children.OfType<CheckBox>().ToArray();
    }

    private static TextBlock GetSelectionError(MessageBoxOverlay overlay)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var panel = (StackPanel)layout.Children[5];
        return (TextBlock)panel.Children[1];
    }

    private static ComboBox GetSingleSelection(MessageBoxOverlay overlay)
    {
        var dialog = (Border)overlay.Children[0];
        var layout = (Grid)dialog.Child!;
        var panel = (StackPanel)layout.Children[6];
        return (ComboBox)panel.Children[0];
    }

    private static MessageBoxOverlay CreateOverlay() => new(new LocalizationService(
        NullLogger<LocalizationService>.Instance, GetLanguageDirectory()));

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null;
             current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Language resources were not found.");
    }
}
