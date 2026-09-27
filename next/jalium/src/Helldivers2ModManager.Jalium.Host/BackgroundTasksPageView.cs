using System.Collections.Specialized;
using System.ComponentModel;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class BackgroundTasksPageView : Grid, IDisposable
{
    private readonly BackgroundTaskService _tasks;
    private readonly LocalizationService _localization;
    private readonly StackPanel _list = new();
    private readonly List<TaskRow> _rows = [];
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly Button _back = new();
    private readonly Button _clear = new();

    public BackgroundTasksPageView(BackgroundTaskService tasks, LocalizationService localization, Action back)
    {
        _tasks = tasks;
        _localization = localization;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _back.Width = 36;
        _back.Height = 36;
        _back.Margin = new Thickness(4, 3, 12, 4);
        _back.Content = IconContent("\uE72B", 16, 0xFFFFFF);
        _back.Click += (_, _) => back();
        header.Children.Add(_back);

        var heading = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        _title.FontSize = 20;
        _title.Foreground = Paint(0xFF, 0xFF, 0xFF);
        _subtitle.FontSize = 12;
        _subtitle.Foreground = Paint(0x88, 0x88, 0x88);
        _subtitle.Margin = new Thickness(0, 2, 0, 0);
        heading.Children.Add(_title);
        heading.Children.Add(_subtitle);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);

        _clear.Width = 36;
        _clear.Height = 36;
        _clear.Margin = new Thickness(0, 3, 4, 4);
        _clear.Content = IconContent("\uE74D", 14, 0xFF8080);
        _clear.Click += (_, _) => _tasks.ClearCompleted();
        Grid.SetColumn(_clear, 2);
        header.Children.Add(_clear);
        Children.Add(new Border { Child = header,
            BorderBrush = Paint(0x33, 0x33, 0x33), BorderThickness = new Thickness(0, 0, 0, 1) });

        var scroll = new ScrollViewer { Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        _tasks.Tasks.CollectionChanged += OnTasksChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
        RenderTasks();
    }

    private void RefreshTexts()
    {
        _title.Text = _localization["DashboardPage.BackgroundTasks"];
        _subtitle.Text = _localization["BackgroundTasksPage.Subtitle"];
        _back.ToolTip = _localization["Common.Back"];
        _clear.ToolTip = _localization["BackgroundTasksPage.ClearCompleted"];
        foreach (var row in _rows)
            row.RefreshTexts();
    }

    private void RenderTasks()
    {
        foreach (var row in _rows)
            row.Dispose();
        _rows.Clear();
        _list.Children.Clear();
        foreach (var task in _tasks.Tasks.Where(static task => !task.IsForeground))
        {
            var row = new TaskRow(task, _tasks, _localization, RefreshClear);
            _rows.Add(row);
            _list.Children.Add(row.Container);
        }
        if (_rows.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0) };
            empty.Children.Add(new TextBlock { Text = _localization["BackgroundTasksPage.EmptyHint"],
                Foreground = Paint(0x88, 0x88, 0x88), FontSize = 14 });
            empty.Children.Add(new TextBlock { Text = _localization["BackgroundTasksPage.EmptyDesc"],
                Foreground = Paint(0x77, 0x77, 0x77), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            _list.Children.Add(empty);
        }
        RefreshClear();
    }

    private void RefreshClear() => _clear.IsEnabled = _rows.Any(row => row.Task.IsFinished);
    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => RenderTasks();
    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTexts();
        if (_rows.Count == 0)
            RenderTasks();
    }

    public void Dispose()
    {
        _tasks.Tasks.CollectionChanged -= OnTasksChanged;
        _localization.PropertyChanged -= OnLocalizationChanged;
        foreach (var row in _rows)
            row.Dispose();
        _rows.Clear();
    }

    private static TextBlock Glyph(string icon, double size, uint rgb) => new()
    {
        Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size,
        Foreground = Paint((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb),
    };

    private static StackPanel IconContent(string icon, double size, uint rgb)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph(icon, size, rgb));
        return content;
    }

    private static Brush Paint(byte r, byte g, byte b)
        => new SolidColorBrush(Color.FromRgb(r, g, b));

    private sealed class TaskRow : IDisposable
    {
        private readonly BackgroundTaskService _service;
        private readonly LocalizationService _localization;
        private readonly Action _statusChanged;
        private readonly TextBlock _name = new();
        private readonly TextBlock _description = new();
        private readonly TextBlock _progressText = new();
        private readonly TextBlock _error = new();
        private readonly TextBlock _status = new();
        private readonly ProgressBar _progress = new();
        private readonly StackPanel _steps = new();
        private readonly Button _remove = new();

        public BackgroundTaskItem Task { get; }
        public Border Container { get; }

        public TaskRow(BackgroundTaskItem task, BackgroundTaskService service,
            LocalizationService localization, Action statusChanged)
        {
            Task = task;
            _service = service;
            _localization = localization;
            _statusChanged = statusChanged;
            var root = new StackPanel();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Glyph("\uE9F5", 22, 0x6A9FD8));
            var details = new StackPanel { Margin = new Thickness(12, 0, 16, 0) };
            _name.FontSize = 14;
            _name.Foreground = Paint(0xFF, 0xFF, 0xFF);
            _name.Margin = new Thickness(0, 0, 0, 4);
            details.Children.Add(_name);
            _progress.Height = 6;
            _progress.Minimum = 0;
            _progress.Maximum = 1;
            _progress.Margin = new Thickness(0, 0, 0, 4);
            details.Children.Add(_progress);
            var descriptionLine = new StackPanel { Orientation = Orientation.Horizontal };
            _description.FontSize = 12;
            _description.Foreground = Paint(0xAA, 0xAA, 0xAA);
            descriptionLine.Children.Add(_description);
            _progressText.FontSize = 12;
            _progressText.Foreground = Paint(0xAA, 0xAA, 0xAA);
            _progressText.Margin = new Thickness(12, 0, 0, 0);
            descriptionLine.Children.Add(_progressText);
            details.Children.Add(descriptionLine);
            _error.FontSize = 12;
            _error.Foreground = Paint(0xF4, 0x43, 0x36);
            _error.Margin = new Thickness(0, 4, 0, 0);
            details.Children.Add(_error);
            Grid.SetColumn(details, 1);
            grid.Children.Add(details);

            var meta = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            _status.FontSize = 12;
            _status.Foreground = Paint(0xAA, 0xAA, 0xAA);
            _status.HorizontalAlignment = HorizontalAlignment.Right;
            meta.Children.Add(_status);
            meta.Children.Add(new TextBlock { Text = task.StartedAt.ToString("HH:mm:ss"), FontSize = 11,
                Foreground = Paint(0x88, 0x88, 0x88), HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0) });
            _remove.Width = 28;
            _remove.Height = 28;
            _remove.Margin = new Thickness(0, 6, 0, 0);
            _remove.Content = IconContent("\uE74D", 13, 0xFF8080);
            _remove.Click += (_, _) => _service.Remove(Task);
            meta.Children.Add(_remove);
            Grid.SetColumn(meta, 2);
            grid.Children.Add(meta);
            root.Children.Add(grid);

            var stepScroll = new ScrollViewer { Content = _steps, MaxHeight = 140,
                Margin = new Thickness(0, 8, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(stepScroll);
            Container = new Border { Child = root, Background = Paint(0x2A, 0x2A, 0x4A),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8) };

            Task.PropertyChanged += OnTaskChanged;
            Task.Steps.CollectionChanged += OnStepsChanged;
            foreach (var step in Task.Steps)
                step.PropertyChanged += OnStepChanged;
            RefreshTexts();
            RenderSteps();
        }

        public void RefreshTexts()
        {
            _name.Text = Task.Name;
            _description.Text = Task.Description;
            _progressText.Text = Task.ProgressText;
            _progress.Value = Task.Progress;
            _progress.IsIndeterminate = Task.IsIndeterminate;
            _error.Text = Task.ErrorMessage ?? string.Empty;
            _error.Visibility = string.IsNullOrEmpty(Task.ErrorMessage)
                ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = _localization[$"Converters.TaskStatus{Task.Status}"];
            _remove.ToolTip = _localization["BackgroundTasksPage.RemoveTask"];
            _remove.Visibility = Task.IsFinished ? Visibility.Visible : Visibility.Collapsed;
            _statusChanged();
        }

        private void RenderSteps()
        {
            _steps.Children.Clear();
            foreach (var step in Task.Steps)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2) };
                var marker = step.Status switch
                {
                    TaskStepStatus.Running => "\u25B6 ",
                    TaskStepStatus.Completed => "\u2713 ",
                    _ => "\u2717 ",
                };
                line.Children.Add(new TextBlock { Text = marker,
                    Foreground = step.Status == TaskStepStatus.Failed
                        ? Paint(0xF4, 0x43, 0x36) : Paint(0x88, 0x88, 0x88) });
                var details = new StackPanel();
                details.Children.Add(new TextBlock { Text = step.Text,
                    Foreground = Paint(0xCC, 0xCC, 0xCC), TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(step.Detail))
                    details.Children.Add(new TextBlock { Text = step.Detail, FontSize = 11,
                        Foreground = Paint(0x88, 0x88, 0x88), TextTrimming = TextTrimming.CharacterEllipsis });
                line.Children.Add(details);
                _steps.Children.Add(line);
            }
        }

        private void OnTaskChanged(object? sender, PropertyChangedEventArgs e) => RefreshTexts();
        private void OnStepChanged(object? sender, PropertyChangedEventArgs e) => RenderSteps();
        private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
                foreach (TaskStepItem step in e.OldItems)
                    step.PropertyChanged -= OnStepChanged;
            if (e.NewItems is not null)
                foreach (TaskStepItem step in e.NewItems)
                    step.PropertyChanged += OnStepChanged;
            RenderSteps();
        }

        public void Dispose()
        {
            Task.PropertyChanged -= OnTaskChanged;
            Task.Steps.CollectionChanged -= OnStepsChanged;
            foreach (var step in Task.Steps)
                step.PropertyChanged -= OnStepChanged;
        }
    }
}
