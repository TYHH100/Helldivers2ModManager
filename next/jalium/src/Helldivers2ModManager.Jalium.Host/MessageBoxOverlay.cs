using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed record MessageBoxSelectionOption(string Text, string? Color = null, string? Detail = null)
{
    public static implicit operator MessageBoxSelectionOption(string text) => new(text);
}

internal sealed class MessageBoxOverlay : Grid, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly Queue<DialogRequest> _pending = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _message = new();
    private readonly ProgressBar _progressBar = new();
    private readonly TextBlock _progressMessage = new();
    private readonly StackPanel _progressPanel = new();
    private readonly ScrollViewer _progressStepScroll = new();
    private readonly StackPanel _progressSteps = new();
    private TextBlock? _currentProgressStep;
    private string _progressTitle = string.Empty;
    private string _progressText = string.Empty;
    private readonly ScrollViewer _messageScroll = new();
    private readonly StackPanel _buttonPanel = new();
    private readonly PasswordBox _password = new();
    private readonly TextBox _textInput = new();
    private readonly TextBlock _inputError = new();
    private readonly StackPanel _inputPanel = new();
    private readonly ScrollViewer _selectionScroll = new();
    private readonly StackPanel _selectionList = new();
    private readonly StackPanel _selectionPanel = new();
    private readonly TextBlock _selectionError = new();
    private readonly List<CheckBox> _selectionChecks = [];
    private readonly ComboBox _singleSelection = new();
    private readonly StackPanel _singleSelectionPanel = new();
    private readonly Button _accept = new();
    private readonly Button _cancel = new();
    private DialogRequest? _active;
    private bool _disposed;

    internal bool IsOpen => Visibility == Visibility.Visible;
    internal bool IsDisposed => _disposed;
    internal string Title => _title.Text;
    internal string Message => _message.Text;

    public MessageBoxOverlay(LocalizationService localization)
    {
        _localization = localization;
        Visibility = Visibility.Collapsed;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        KeyDown += OnKeyDown;

        var dialog = new Border
        {
            MinWidth = 320, MaxWidth = 640, MaxHeight = 520,
            Margin = new Thickness(24), Padding = new Thickness(24),
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x46)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.SemiBold;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.Margin = new Thickness(0, 0, 0, 16);
        layout.Children.Add(_title);
        _message.FontSize = 14;
        _message.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        _message.TextWrapping = TextWrapping.Wrap;
        _messageScroll.Content = _message;
        _messageScroll.MaxHeight = 360;
        _messageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _messageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Grid.SetRow(_messageScroll, 1);
        layout.Children.Add(_messageScroll);

        _buttonPanel.Orientation = Orientation.Horizontal;
        _buttonPanel.HorizontalAlignment = HorizontalAlignment.Right;
        _buttonPanel.Margin = new Thickness(0, 20, 0, 0);
        _cancel.MinWidth = 84;
        _cancel.Height = 36;
        _cancel.Click += (_, _) => Finish(false);
        _buttonPanel.Children.Add(_cancel);
        _accept.MinWidth = 84;
        _accept.Height = 36;
        _accept.Margin = new Thickness(8, 0, 0, 0);
        _accept.Click += (_, _) => Finish(true);
        _buttonPanel.Children.Add(_accept);
        Grid.SetRow(_buttonPanel, 3);
        layout.Children.Add(_buttonPanel);
        _password.Height = 36;
        _password.Margin = new Thickness(0, 12, 0, 0);
        _password.Visibility = Visibility.Collapsed;
        Grid.SetRow(_password, 2);
        layout.Children.Add(_password);
        _textInput.Height = 36;
        _inputError.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x54, 0x43));
        _inputError.Margin = new Thickness(0, 6, 0, 0);
        _inputPanel.Children.Add(_textInput);
        _inputPanel.Children.Add(_inputError);
        _inputPanel.Visibility = Visibility.Collapsed;
        Grid.SetRow(_inputPanel, 2);
        layout.Children.Add(_inputPanel);
        _selectionScroll.Content = _selectionList;
        _selectionScroll.MaxHeight = 300;
        _selectionScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _selectionError.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x54, 0x43));
        _selectionError.Margin = new Thickness(0, 6, 0, 0);
        _selectionPanel.Children.Add(_selectionScroll);
        _selectionPanel.Children.Add(_selectionError);
        _selectionPanel.Visibility = Visibility.Collapsed;
        Grid.SetRow(_selectionPanel, 2);
        layout.Children.Add(_selectionPanel);
        _singleSelection.Height = 36;
        _singleSelectionPanel.Children.Add(_singleSelection);
        _singleSelectionPanel.Visibility = Visibility.Collapsed;
        Grid.SetRow(_singleSelectionPanel, 2);
        layout.Children.Add(_singleSelectionPanel);
        _progressMessage.TextWrapping = TextWrapping.Wrap;
        _progressMessage.Margin = new Thickness(0, 0, 0, 16);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 1;
        _progressBar.Height = 8;
        _progressPanel.Children.Add(_progressMessage);
        _progressPanel.Children.Add(_progressBar);
        _progressStepScroll.Content = _progressSteps;
        _progressStepScroll.MaxHeight = 220;
        _progressStepScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _progressStepScroll.Margin = new Thickness(0, 12, 0, 0);
        _progressPanel.Children.Add(_progressStepScroll);
        _progressPanel.Visibility = Visibility.Collapsed;
        Grid.SetRow(_progressPanel, 1);
        layout.Children.Add(_progressPanel);
        dialog.Child = layout;
        Children.Add(dialog);
    }

    public void ShowError(string message) => Enqueue(new DialogRequest(
        _localization["MessageBox.Error"], message, DialogKind.Error, null));

    public void ShowError(string title, string message) => Enqueue(new DialogRequest(
        title, message, DialogKind.Error, null));

    public void ShowInfo(string message) => Enqueue(new DialogRequest(
        _localization["MessageBox.Info"], message, DialogKind.Info, null));

    public void ShowInfo(string title, string message) => Enqueue(new DialogRequest(
        title, message, DialogKind.Info, null));

    public Task<bool> ConfirmAsync(string title, string message)
    {
        if (_disposed) return Task.FromResult(false);
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new DialogRequest(title, message, DialogKind.Confirm, result));
        return result.Task;
    }

    public Task<string?> PromptPasswordAsync(string title, string message)
    {
        if (_disposed) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new DialogRequest(title, message, DialogKind.Password, null, result));
        return result.Task;
    }

    public Task<string?> PromptAsync(string title, string message, string initial = "",
        int maxLength = 2048, Func<string, string?>? validate = null)
    {
        if (_disposed) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new DialogRequest(title, message, DialogKind.Input, null,
            TextResult: result, InitialText: initial, MaxLength: maxLength, Validate: validate));
        return result.Task;
    }

    public Task<IReadOnlyList<int>?> SelectManyAsync(string title, string message,
        IReadOnlyList<MessageBoxSelectionOption> options, IReadOnlyCollection<int>? initiallySelected = null,
        Func<IReadOnlyList<int>, Task<string?>>? validateSelection = null)
    {
        if (_disposed) return Task.FromResult<IReadOnlyList<int>?>(null);
        var result = new TaskCompletionSource<IReadOnlyList<int>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new DialogRequest(title, message, DialogKind.MultiSelection, null,
            SelectionResult: result, Options: options,
            InitiallySelected: initiallySelected ?? Array.Empty<int>(),
            ValidateSelection: validateSelection));
        return result.Task;
    }

    public Task<int?> ChooseOneAsync(string title, string message, IReadOnlyList<string> options,
        int initialIndex = 0)
    {
        if (_disposed) return Task.FromResult<int?>(null);
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new DialogRequest(title, message, DialogKind.SingleSelection, null,
            SingleSelectionResult: result, Options: options.Select(option => new MessageBoxSelectionOption(option)).ToArray(),
            InitiallySelected: [initialIndex]));
        return result.Task;
    }

    public void ShowProgress(string title, string message)
    {
        if (_disposed || _active is not null || _pending.Count > 0)
            throw new InvalidOperationException("Cannot show progress while another message is active.");
        _progressTitle = title;
        _progressText = message;
        _title.Text = title;
        _title.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
        _progressMessage.Text = message;
        _progressBar.IsIndeterminate = true;
        _progressBar.Value = 0;
        _progressSteps.Children.Clear();
        _currentProgressStep = null;
        SetProgressVisible(true);
        Visibility = Visibility.Visible;
    }

    public void UpdateProgress(string message, double? ratio)
    {
        if (_progressPanel.Visibility != Visibility.Visible)
            return;
        _progressText = message;
        _progressMessage.Text = message;
        _progressBar.IsIndeterminate = ratio is null;
        if (ratio is { } value)
            _progressBar.Value = Math.Clamp(value, 0, 1);
    }

    public void CloseProgress()
    {
        if (_progressPanel.Visibility != Visibility.Visible)
            return;
        SetProgressVisible(false);
        _progressSteps.Children.Clear();
        _currentProgressStep = null;
        _progressTitle = string.Empty;
        _progressText = string.Empty;
        Visibility = Visibility.Collapsed;
    }

    public void PauseProgress()
    {
        if (_progressPanel.Visibility == Visibility.Visible)
            Visibility = Visibility.Collapsed;
    }

    public void ResumeProgress()
    {
        if (_progressTitle.Length == 0 || _disposed)
            return;
        _title.Text = _progressTitle;
        _progressMessage.Text = _progressText;
        SetProgressVisible(true);
        Visibility = Visibility.Visible;
    }

    public void ReportProgressStep(string text)
    {
        if (_progressPanel.Visibility != Visibility.Visible)
            return;
        _currentProgressStep = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _progressSteps.Children.Add(_currentProgressStep);
    }

    public void CompleteProgressStep()
    {
        if (_currentProgressStep is not null)
            _currentProgressStep.Text = "\u2713 " + _currentProgressStep.Text;
        _currentProgressStep = null;
    }

    public void FailProgressStep()
    {
        if (_currentProgressStep is not null)
            _currentProgressStep.Text = "\u2717 " + _currentProgressStep.Text;
        _currentProgressStep = null;
    }

    private void Enqueue(DialogRequest request)
    {
        if (_disposed) return;
        _pending.Enqueue(request);
        ShowNext();
    }

    private void ShowNext()
    {
        if (_active is not null || _pending.Count == 0 || _disposed) return;
        SetProgressVisible(false);
        _finishing = false;
        _active = _pending.Dequeue();
        _title.Text = _active.Title;
        _title.Foreground = new SolidColorBrush(_active.Kind == DialogKind.Error
            ? Color.FromRgb(0xE5, 0x54, 0x43) : Color.FromRgb(0xF2, 0xF2, 0xF2));
        _message.Text = _active.Message;
        var password = _active.Kind == DialogKind.Password;
        var input = _active.Kind == DialogKind.Input;
        var selection = _active.Kind == DialogKind.MultiSelection;
        var singleSelection = _active.Kind == DialogKind.SingleSelection;
        var confirm = _active.Kind == DialogKind.Confirm || password || input || selection || singleSelection;
        _password.Password = string.Empty;
        _password.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        _inputPanel.Visibility = input ? Visibility.Visible : Visibility.Collapsed;
        _selectionPanel.Visibility = selection ? Visibility.Visible : Visibility.Collapsed;
        _singleSelectionPanel.Visibility = singleSelection ? Visibility.Visible : Visibility.Collapsed;
        _singleSelection.ItemsSource = singleSelection
            ? _active.Options.Select(option => option.Text).ToArray() : null;
        _singleSelection.SelectedIndex = singleSelection && _active.Options.Count > 0
            ? Math.Clamp(_active.InitiallySelected.FirstOrDefault(), 0, _active.Options.Count - 1) : -1;
        _selectionList.Children.Clear();
        _selectionChecks.Clear();
        _selectionError.Text = string.Empty;
        _accept.IsEnabled = true;
        _cancel.IsEnabled = true;
        if (selection)
        {
            var selected = _active.InitiallySelected.ToHashSet();
            for (var index = 0; index < _active.Options.Count; index++)
            {
                var check = new CheckBox
                {
                    Content = CreateSelectionContent(_active.Options[index]),
                    IsChecked = selected.Contains(index), MinHeight = 38,
                };
                _selectionChecks.Add(check);
                _selectionList.Children.Add(check);
            }
        }
        _textInput.Text = input ? _active.InitialText : string.Empty;
        _textInput.MaxLength = input ? Math.Max(0, _active.MaxLength) : 0;
        _inputError.Text = string.Empty;
        _cancel.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = password || input || selection || singleSelection
            ? _localization["Common.Cancel"] : _localization["MessageBox.No"];
        _accept.Content = password || input || selection || singleSelection ? _localization["Common.Confirm"]
            : confirm ? _localization["MessageBox.Yes"] : _localization["Common.OK"];
        Visibility = Visibility.Visible;
        if (password) _password.Focus();
        else if (input) _textInput.Focus();
        else if (selection) _selectionScroll.Focus();
        else if (singleSelection) _singleSelection.Focus();
        else Focus();
    }

    private void SetProgressVisible(bool visible)
    {
        var standard = visible ? Visibility.Collapsed : Visibility.Visible;
        _messageScroll.Visibility = standard;
        _password.Visibility = visible ? Visibility.Collapsed : _password.Visibility;
        _inputPanel.Visibility = visible ? Visibility.Collapsed : _inputPanel.Visibility;
        _selectionPanel.Visibility = visible ? Visibility.Collapsed : _selectionPanel.Visibility;
        _singleSelectionPanel.Visibility = visible ? Visibility.Collapsed : _singleSelectionPanel.Visibility;
        _buttonPanel.Visibility = standard;
        _progressPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _finishing;

    private static UIElement CreateSelectionContent(MessageBoxSelectionOption option)
    {
        if (option.Color is null && option.Detail is null)
            return new TextBlock { Text = option.Text, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (option.Color is not null && ParseSelectionColor(option.Color) is { } color)
            content.Children.Add(new Border { Width = 16, Height = 16,
                CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        if (option.Detail is null)
            content.Children.Add(new TextBlock { Text = option.Text, VerticalAlignment = VerticalAlignment.Center });
        else
        {
            var description = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            description.Children.Add(new TextBlock { Text = option.Text, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis });
            description.Children.Add(new TextBlock { Text = option.Detail, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(description);
        }
        return content;
    }

    private static Color? ParseSelectionColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var argb))
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return null;
    }

    private async void Finish(bool accepted)
    {
        if (_active is not { } request || _finishing) return;
        var text = accepted && request.Kind == DialogKind.Input ? _textInput.Text.Trim() : null;
        if (text is not null && request.Validate?.Invoke(text) is { } error)
        {
            _inputError.Text = error;
            return;
        }
        IReadOnlyList<int>? selected = accepted && request.Kind == DialogKind.MultiSelection
            ? _selectionChecks.Select((check, index) => (check, index))
                .Where(item => item.check.IsChecked == true).Select(item => item.index).ToArray()
            : null;
        int? selectedIndex = accepted && request.Kind == DialogKind.SingleSelection
            && _singleSelection.SelectedIndex >= 0 ? _singleSelection.SelectedIndex : null;
        if (selected is not null && request.ValidateSelection is { } validateSelection)
        {
            _finishing = true;
            _accept.IsEnabled = false;
            _cancel.IsEnabled = false;
            try
            {
                var selectionError = await validateSelection(selected);
                if (_disposed || !ReferenceEquals(_active, request))
                    return;
                if (selectionError is not null)
                {
                    _selectionError.Text = selectionError;
                    _finishing = false;
                    _accept.IsEnabled = true;
                    _cancel.IsEnabled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                if (_disposed || !ReferenceEquals(_active, request))
                    return;
                _selectionError.Text = ex.Message;
                _finishing = false;
                _accept.IsEnabled = true;
                _cancel.IsEnabled = true;
                return;
            }
        }
        var password = accepted ? _password.Password : null;
        _password.Password = string.Empty;
        _textInput.Text = string.Empty;
        _inputError.Text = string.Empty;
        _selectionList.Children.Clear();
        _selectionChecks.Clear();
        _singleSelection.ItemsSource = null;
        _active = null;
        Visibility = Visibility.Collapsed;
        request.Result?.TrySetResult(accepted);
        request.PasswordResult?.TrySetResult(password);
        request.TextResult?.TrySetResult(text);
        request.SelectionResult?.TrySetResult(selected);
        request.SingleSelectionResult?.TrySetResult(selectedIndex);
        ShowNext();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Finish(false);
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KeyDown -= OnKeyDown;
        _active?.Result?.TrySetResult(false);
        _active?.PasswordResult?.TrySetResult(null);
        _active?.TextResult?.TrySetResult(null);
        _active?.SelectionResult?.TrySetResult(null);
        _active?.SingleSelectionResult?.TrySetResult(null);
        _active = null;
        while (_pending.TryDequeue(out var request))
        {
            request.Result?.TrySetResult(false);
            request.PasswordResult?.TrySetResult(null);
            request.TextResult?.TrySetResult(null);
            request.SelectionResult?.TrySetResult(null);
            request.SingleSelectionResult?.TrySetResult(null);
        }
        _password.Password = string.Empty;
        _textInput.Text = string.Empty;
        Visibility = Visibility.Collapsed;
    }

    private enum DialogKind { Info, Error, Confirm, Password, Input, MultiSelection, SingleSelection }
    private sealed record DialogRequest(string Title, string Message, DialogKind Kind,
        TaskCompletionSource<bool>? Result, TaskCompletionSource<string?>? PasswordResult = null,
        TaskCompletionSource<string?>? TextResult = null, string InitialText = "", int MaxLength = 2048,
        Func<string, string?>? Validate = null, TaskCompletionSource<IReadOnlyList<int>?>? SelectionResult = null,
        IReadOnlyList<MessageBoxSelectionOption> Options = default!, IReadOnlyCollection<int> InitiallySelected = default!,
        Func<IReadOnlyList<int>, Task<string?>>? ValidateSelection = null,
        TaskCompletionSource<int?>? SingleSelectionResult = null);
}
