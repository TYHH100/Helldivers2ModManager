// Ignore Spelling: Helldivers

using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

internal sealed class ChecklistSelectionItem
{
    public required long Value { get; init; }
    public string? Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool IsSelected { get; set; }
}

internal partial class MessageBox : UserControl
{
    private static WeakReference<MessageBox>? s_current;

    internal static MessageBox? Current =>
        s_current is not null && s_current.TryGetTarget(out var current) ? current : null;

    public static bool IsRegistered { get; private set; }

    public static event EventHandler? Registered;

    internal static LocalizationService? LocalizationService { get; private set; }

    private Action<string>? _inputAction;
    private Action? _abortAction;
    private Action? _confirmAction;
    private Action<object>? _selectionAction;
    private Action<IEnumerable<Models.TagSelectionItem>>? _tagSelectionAction;
    private Action<IReadOnlyList<ChecklistSelectionItem>>? _checklistAction;
    private Action<string>? _colorPickerAction;
    private Action? _acknowledgeAction;
    private string? _selectedColor;

    public MessageBox()
    {
        InitializeComponent();
        s_current = new WeakReference<MessageBox>(this);

        if (!IsRegistered)
        {
            IsRegistered = true;
            Registered?.Invoke(this, EventArgs.Empty);

        }
    }

    internal void Configure(LocalizationService localizationService) =>
        LocalizationService = localizationService;

    internal void ShowUiTestConfirmation()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HD2MM_RUN_UI_TESTS"), "1", StringComparison.Ordinal))
            throw new InvalidOperationException("UI test dialogs are only available in UI test mode.");
        Reset();
        title.Text = LocalizationService?["UiTest.DialogTitle"] ?? "⟦Missing.UiTest.DialogTitle⟧";
        message.Text = LocalizationService?["UiTest.DialogMessage"] ?? "⟦Missing.UiTest.DialogMessage⟧";
        yesButton.Content = LocalizationService?["UiTest.DialogConfirm"] ?? "⟦Missing.UiTest.DialogConfirm⟧";
        noButton.Content = LocalizationService?["UiTest.DialogCancel"] ?? "⟦Missing.UiTest.DialogCancel⟧";
        yesNoStack.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        Focus();
    }

    internal Task<bool> ShowConfirmationAsync(DialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Reset();
        _confirmAction = () => completion.TrySetResult(true);
        _abortAction = () => completion.TrySetResult(false);
        title.Text = request.Title;
        brush.Color = Colors.Yellow;
        message.Text = request.Message;
        yesNoStack.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        return completion.Task;
    }

    internal Task<string?> ShowSelectionAsync(SelectionDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Reset();
        _selectionAction = selected => completion.TrySetResult(selected?.ToString());
        _abortAction = () => completion.TrySetResult(null);
        title.Text = request.Title;
        brush.Color = Colors.White;
        message.Text = request.Message;
        selectionComboBox.ItemsSource = request.Options;
        selectionComboBox.SelectedIndex = Math.Clamp(request.SelectedIndex, 0, Math.Max(0, request.Options.Count - 1));
        selectionComboBox.Visibility = Visibility.Visible;
        cancelButton.Visibility = Visibility.Visible;
        okButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        return completion.Task;
    }

    internal Task<IReadOnlyList<string>?> ShowChecklistAsync(ChecklistDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Reset();
        _checklistAction = selected => completion.TrySetResult(
            selected.Select(item => item.Key ?? item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        _abortAction = () => completion.TrySetResult(null);
        title.Text = request.Title;
        brush.Color = Colors.White;
        message.Text = request.Message;
        message.TextWrapping = TextWrapping.Wrap;
        message.Margin = new Thickness(0, 0, 0, 8);
        checklistSelectionList.ItemsSource = request.Options
            .Select((option, index) => new ChecklistSelectionItem
            {
                Value = index,
                Key = option.Key,
                Title = option.Title,
                Description = option.Description,
                IsSelected = option.IsSelected
            })
            .ToArray();
        checklistSelectionList.Visibility = Visibility.Visible;
        cancelButton.Visibility = Visibility.Visible;
        okButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        return completion.Task;
    }

    internal void ShowProgress(ProgressDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Reset();
        title.Text = request.Title;
        brush.Color = Colors.White;
        message.Text = request.Message;
        progressStep.Text = request.Step ?? request.Title;
        progressPanel.Visibility = Visibility.Visible;
        progress.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    internal void HideDialog() => Visibility = Visibility.Hidden;

    internal Task ShowMessageAsync(MessageDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Reset();
        _acknowledgeAction = () => completion.TrySetResult();
        title.Text = request.Title;
        brush.Color = request.Severity switch
        {
            MessageDialogSeverity.Warning => Colors.Yellow,
            MessageDialogSeverity.Error => Colors.Red,
            _ => Colors.White
        };
        message.Text = request.Message;
        okButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        return completion.Task;
    }

    internal Task<string?> ShowInputAsync(InputDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Reset();
        _inputAction = value => completion.TrySetResult(value);
        _abortAction = () => completion.TrySetResult(null);
        title.Text = request.Title;
        message.Text = request.Message;
        input.MaxLength = request.MaxLength;
        input.Text = request.InitialText;
        input.Visibility = Visibility.Visible;
        cancelButton.Visibility = Visibility.Visible;
        okButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        return completion.Task;
    }

    internal Task<string?> ShowColorPickerAsync(ColorDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Reset();
        _colorPickerAction = value => completion.TrySetResult(value);
        _abortAction = () => completion.TrySetResult(null);
        _selectedColor = request.CurrentColor;
        title.Text = request.Title;
        message.Text = request.Message;
        colorPickerPanel.Visibility = Visibility.Visible;
        UpdateColorPreview();
        cancelButton.Visibility = Visibility.Visible;
        okButton.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        return completion.Task;
    }

    private void UpdateColorPreview()
    {
        if (!string.IsNullOrEmpty(_selectedColor) && ColorConverter.ConvertFromString(_selectedColor) is Color color)
        {
            colorPreviewBorder.Background = new SolidColorBrush(color);
            colorCodeText.Text = _selectedColor;
            colorInputBox.Text = _selectedColor;
            colorInputPreview.Background = new SolidColorBrush(color);
        }
    }

    private void SelectColor(string colorCode)
    {
        _selectedColor = colorCode;
        UpdateColorPreview();
    }

    private void ColorInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = colorInputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text);
            _selectedColor = text;
            colorPreviewBorder.Background = new SolidColorBrush(color);
            colorCodeText.Text = text;
            colorInputPreview.Background = new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            colorInputPreview.Background = null;
        }
    }

    private void Reset()
    {
        _inputAction = null;
        _abortAction = null;
        _confirmAction = null;
        _selectionAction = null;
        _tagSelectionAction = null;
        _checklistAction = null;
        _colorPickerAction = null;
        _acknowledgeAction = null;
        _selectedColor = null;

        title.Visibility = Visibility.Visible;
        brush.Color = Colors.White;
        message.Visibility = Visibility.Visible;
        message.Margin = new Thickness(0);
        message.TextWrapping = TextWrapping.Wrap;
        input.Visibility = Visibility.Collapsed;
        input.Text = string.Empty;
        selectionComboBox.Visibility = Visibility.Collapsed;
        tagSelectionList.Visibility = Visibility.Collapsed;
        tagSelectionList.ItemsSource = null;
        checklistSelectionList.Visibility = Visibility.Collapsed;
        checklistSelectionList.ItemsSource = null;
        colorPickerPanel.Visibility = Visibility.Collapsed;
        colorInputBox.Text = string.Empty;
        colorInputPreview.Background = null;
        cancelButton.Visibility = Visibility.Hidden;
        okButton.Visibility = Visibility.Hidden;
        yesNoStack.Visibility = Visibility.Hidden;
        progress.IsIndeterminate = true;
        progressPanel.Visibility = Visibility.Hidden;
        progress.Visibility = Visibility.Hidden;
        progressStep.Text = "";
        updateProgressPanel.Visibility = Visibility.Collapsed;
        updatePhaseText.Text = "";
        updateCurrentFile.Text = "";
        updateFileCount.Text = "";
        updateNeedUpdateCount.Text = "";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        HideDialog();

        if (_inputAction != null)
        {
            _inputAction(input.Text);
        }
        else if (_selectionAction != null)
        {
            _selectionAction(selectionComboBox.SelectedItem);
        }
        else if (_tagSelectionAction != null)
        {
            var selectedTags = tagSelectionList.ItemsSource.Cast<Models.TagSelectionItem>().Where(t => t.IsSelected).ToList();
            _tagSelectionAction(selectedTags);
        }
        else if (_checklistAction != null)
        {
            var selectedItems = checklistSelectionList.ItemsSource
                .Cast<ChecklistSelectionItem>()
                .Where(item => item.IsSelected)
                .ToList();
            _checklistAction(selectedItems);
        }
        else if (_colorPickerAction != null && _selectedColor != null)
        {
            _colorPickerAction(_selectedColor);
        }
        else
        {
            _acknowledgeAction?.Invoke();
        }
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        HideDialog();

        _abortAction?.Invoke();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        HideDialog();

        _confirmAction?.Invoke();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        HideDialog();
        _abortAction?.Invoke();
    }

    private void ColorBorder_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string colorCode)
        {
            SelectColor(colorCode);
        }
    }
}
