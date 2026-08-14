// Ignore Spelling: Helldivers

using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helldivers2ModManager.Components;

internal sealed class MessageBoxInfoMessage
{
	public required string Message { get; init; }
}

internal sealed class MessageBoxWarningMessage
{
	public required string Message { get; init; }
}

internal sealed class MessageBoxErrorMessage
{
	public required string Message { get; init; }
}

internal sealed class MessageBoxProgressMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public string? Step { get; init; }
}

/// <summary>
/// 导出进度初始显示消息（带确定进度条 + 信息面板）
/// </summary>
internal sealed class MessageBoxExportProgressMessage
{
	public required string Title { get; init; }
}

/// <summary>
/// 导出进度更新消息（实时更新进度/速度/压缩率）
/// </summary>
internal sealed class MessageBoxExportProgressUpdateMessage
{
	public double Progress { get; init; }               // 0.0 ~ 1.0
	public string? CurrentFile { get; init; }            // 当前正在压缩的文件
	public string? SpeedText { get; init; }              // e.g. "速度: 15.2 MB/s"
	public string? RatioText { get; init; }              // e.g. "压缩率: 65%"
	public bool IsCompleted { get; init; }               // 是否已完成
}

internal sealed class MessageBoxHideMessage { }

/// <summary>
/// 模组更新进度消息 - 初始化更新进度显示
/// </summary>
internal sealed class MessageBoxUpdateProgressMessage
{
    public required string Title { get; init; }
    public required string ModName { get; init; }
}

/// <summary>
/// 模组更新进度更新消息 - 实时更新哈希计算和文件更新进度
/// </summary>
internal sealed class MessageBoxUpdateProgressUpdateMessage
{
    /// <summary>当前阶段描述文本</summary>
    public string? PhaseText { get; init; }
    /// <summary>进度 0.0 ~ 1.0</summary>
    public double Progress { get; init; }
    /// <summary>当前正在处理的文件相对路径</summary>
    public string? CurrentFile { get; init; }
    /// <summary>已检查/已更新的文件数</summary>
    public int ProcessedCount { get; init; }
    /// <summary>总文件数</summary>
    public int TotalCount { get; init; }
    /// <summary>需要更新的文件总数</summary>
    public int NeedUpdateCount { get; init; }
    /// <summary>缓存命中的文件数（跳过SHA-256计算的文件）</summary>
    public int CacheHits { get; init; }
    /// <summary>是否已完成所有操作</summary>
    public bool IsCompleted { get; init; }
}

internal sealed class MessageBoxInputMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required Action<string> Confirm { get; init; }

	public int MaxLength { get; init; } = -1;

	public string InitialText { get; init; } = string.Empty;
}

internal sealed class MessageBoxConfirmMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required Action Confirm { get; init; }

	public Action? Abort { get; init; }
}

internal sealed class MessageBoxSelectionMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required IEnumerable<object> Options { get; init; }

	public required Action<object> Confirm { get; init; }

	/// <summary>用户点取消按钮（不选择任何选项）时调用；可选。</summary>
	public Action? Abort { get; init; }
}

internal sealed class MessageBoxTagSelectionMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required List<Models.TagSelectionItem> Tags { get; init; }

	public required Action<IEnumerable<Models.TagSelectionItem>> Confirm { get; init; }
}

internal sealed class MessageBoxGroupSelectionMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required List<Models.ModGroupSelectionItem> Groups { get; init; }

	public required Action<IEnumerable<Models.ModGroupSelectionItem>> Confirm { get; init; }
}

internal sealed class ChecklistSelectionItem
{
    public required long Value { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool IsSelected { get; set; }
}

internal sealed class MessageBoxChecklistMessage
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required IReadOnlyList<ChecklistSelectionItem> Items { get; init; }
    public required Action<IReadOnlyList<ChecklistSelectionItem>> Confirm { get; init; }
}

internal sealed class MessageBoxColorPickerMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required string CurrentColor { get; init; }

	public required Action<string> Confirm { get; init; }
}

internal partial class MessageBox : UserControl, IRecipient<MessageBoxInfoMessage>, IRecipient<MessageBoxWarningMessage>, IRecipient<MessageBoxErrorMessage>, IRecipient<MessageBoxProgressMessage>, IRecipient<MessageBoxExportProgressMessage>, IRecipient<MessageBoxExportProgressUpdateMessage>, IRecipient<MessageBoxUpdateProgressMessage>, IRecipient<MessageBoxUpdateProgressUpdateMessage>, IRecipient<MessageBoxHideMessage>, IRecipient<MessageBoxInputMessage>, IRecipient<MessageBoxConfirmMessage>, IRecipient<MessageBoxSelectionMessage>, IRecipient<MessageBoxTagSelectionMessage>, IRecipient<MessageBoxGroupSelectionMessage>, IRecipient<MessageBoxChecklistMessage>, IRecipient<MessageBoxColorPickerMessage>
{
	public static bool IsRegistered { get; private set; }

	public static event EventHandler? Registered;

	internal static LocalizationService? LocalizationService { get; private set; }
	
	private Action<string>? _inputAction;
	private Action? _abortAction;
	private Action? _confirmAction;
	private Action<object>? _selectionAction;
	private Action? _selectionAbortAction;
	private Action<IEnumerable<Models.TagSelectionItem>>? _tagSelectionAction;
	private Action<IEnumerable<Models.ModGroupSelectionItem>>? _groupSelectionAction;
	private Action<IReadOnlyList<ChecklistSelectionItem>>? _checklistAction;
	private Action<string>? _colorPickerAction;
	private string? _selectedColor;

	public MessageBox()
	{
		InitializeComponent();

		WeakReferenceMessenger.Default.Register<MessageBoxInfoMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxWarningMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxErrorMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxProgressMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxExportProgressMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxExportProgressUpdateMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxUpdateProgressMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxUpdateProgressUpdateMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxHideMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxInputMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxConfirmMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxSelectionMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxTagSelectionMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxGroupSelectionMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxChecklistMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxColorPickerMessage>(this);

		if (!IsRegistered)
		{
			IsRegistered = true;
			Registered?.Invoke(this, EventArgs.Empty);

			// 初始化本地化服务
			if (Application.Current is App app && app.Host?.Services?.GetService(typeof(LocalizationService)) is LocalizationService locService)
			{
				LocalizationService = locService;
			}
		}
	}

	public void Receive(MessageBoxInfoMessage message)
	{
		Reset();

		title.Text = LocalizationService?["MessageBox.Info"] ?? "信息";
		this.message.Text = message.Message;

		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxWarningMessage message)
	{
		Reset();

		title.Text = LocalizationService?["MessageBox.Warning"] ?? "警告";
		brush.Color = Colors.Yellow;
		this.message.Text = message.Message;

		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxErrorMessage message)
	{
		Reset();

		title.Text = LocalizationService?["MessageBox.Error"] ?? "错误";
		brush.Color = Colors.Red;
		this.message.Text = message.Message;

		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxProgressMessage message)
	{
		Reset();

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		progressStep.Text = message.Step ?? message.Title;

		progressPanel.Visibility = Visibility.Visible;
		progress.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxExportProgressMessage message)
	{
		Reset();

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = LocalizationService?["MessageBox.Compressing"] ?? "正在压缩...";

		// Switch progress bar to determinate mode
		progress.IsIndeterminate = false;
		progress.Value = 0;
		progressPanel.Visibility = Visibility.Visible;
		progressStep.Text = this.message.Text;
		progress.Visibility = Visibility.Visible;

		// Show export info panel with all elements visible
		exportProgressPanel.Visibility = Visibility.Visible;
		exportCurrentFile.Visibility = Visibility.Visible;
		exportSpeedText.Visibility = Visibility.Visible;
		exportCurrentFile.Text = "";
		exportSpeedText.Text = "";
		exportRatioText.Text = "";

		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxExportProgressUpdateMessage message)
	{
		if (message.IsCompleted)
		{
			// 完成状态：隐藏进度条/当前文件/速度，保留压缩率，显示确认按钮
			progressPanel.Visibility = Visibility.Hidden;
			progress.Visibility = Visibility.Hidden;
			exportCurrentFile.Visibility = Visibility.Collapsed;
			exportSpeedText.Visibility = Visibility.Collapsed;
			this.message.Text = "导出完成";
			okButton.Visibility = Visibility.Visible;
		}
		else
		{
			progress.Value = Math.Clamp(message.Progress * 100, 0, 100);
			exportCurrentFile.Text = message.CurrentFile ?? "";
			exportSpeedText.Text = message.SpeedText ?? "";
			exportRatioText.Text = message.RatioText ?? "";
		}
	}

	public void Receive(MessageBoxUpdateProgressMessage message)
	{
		Reset();

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = $"{LocalizationService?["MessageBox.UpdatingModPrefix"]}{message.ModName}{LocalizationService?["MessageBox.UpdatingModSuffix"]}";

		// 切换到确定进度条模式，显示更新进度面板
		progress.IsIndeterminate = false;
		progress.Value = 0;
		progressPanel.Visibility = Visibility.Visible;
		progressStep.Text = LocalizationService?["MessageBox.ComputingHashes"] ?? "正在计算文件哈希...";
		progress.Visibility = Visibility.Visible;

		// 显示更新进度信息面板
		updateProgressPanel.Visibility = Visibility.Visible;
		updatePhaseText.Text = progressStep.Text;
		updateCurrentFile.Text = "";
		updateFileCount.Text = "";
		updateNeedUpdateCount.Text = "";

		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxUpdateProgressUpdateMessage message)
	{
		if (message.IsCompleted)
		{
			// 完成状态：隐藏进度条和信息面板，显示完成消息
			progressPanel.Visibility = Visibility.Hidden;
			progress.Visibility = Visibility.Hidden;
			updateProgressPanel.Visibility = Visibility.Collapsed;
			this.message.Text = LocalizationService?["DashboardPage.UpdateModDone"] ?? "模组更新完成";
			okButton.Visibility = Visibility.Visible;
		}
		else
		{
			progress.Value = Math.Clamp(message.Progress * 100, 0, 100);

			if (!string.IsNullOrEmpty(message.PhaseText))
			{
				updatePhaseText.Text = message.PhaseText;
				progressStep.Text = message.PhaseText;
			}

			if (!string.IsNullOrEmpty(message.CurrentFile))
				updateCurrentFile.Text = message.CurrentFile;
			else
				updateCurrentFile.Text = "";

			updateFileCount.Text = message.CacheHits > 0
				? $"{LocalizationService?["MessageBox.ProcessedPrefix"]}{message.ProcessedCount}{LocalizationService?["MessageBox.ProcessedSep"]}{message.TotalCount}{LocalizationService?["MessageBox.NeedUpdateSuffix"]} ({LocalizationService?["MessageBox.CacheHitPrefix"]}{message.CacheHits}{LocalizationService?["MessageBox.CacheHitSuffix"]})"
				: $"{LocalizationService?["MessageBox.ProcessedPrefix"]}{message.ProcessedCount}{LocalizationService?["MessageBox.ProcessedSep"]}{message.TotalCount}{LocalizationService?["MessageBox.NeedUpdateSuffix"]}";

			if (message.NeedUpdateCount > 0)
				updateNeedUpdateCount.Text = $"{LocalizationService?["MessageBox.NeedUpdatePrefix"]}{message.NeedUpdateCount}{LocalizationService?["MessageBox.NeedUpdateSuffix"]}";
			else
				updateNeedUpdateCount.Text = "";
		}
	}

	public void Receive(MessageBoxHideMessage message)
	{
		Visibility = Visibility.Hidden;
	}

	public void Receive(MessageBoxInputMessage message)
	{
		Reset();

		_inputAction = message.Confirm;

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		input.MaxLength = message.MaxLength;
		input.Visibility = Visibility.Visible;
		input.Text = message.InitialText;
		cancelButton.Visibility = Visibility.Visible;
		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxConfirmMessage message)
	{
		Reset();

		_confirmAction = message.Confirm;
		_abortAction = message.Abort;

		title.Text = message.Title;
		brush.Color = Colors.Yellow;
		this.message.Text = message.Message;
		yesNoStack.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxSelectionMessage message)
	{
		Reset();

		_selectionAction = message.Confirm;
		_selectionAbortAction = message.Abort;

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		selectionComboBox.ItemsSource = message.Options;
		selectionComboBox.SelectedIndex = 0;
		selectionComboBox.Visibility = Visibility.Visible;
		cancelButton.Visibility = Visibility.Visible;
		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxTagSelectionMessage message)
	{
		Reset();

		_tagSelectionAction = message.Confirm;

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		this.message.Margin = new Thickness(0, 0, 0, 8);
		tagSelectionList.ItemsSource = message.Tags;
		tagSelectionList.Visibility = Visibility.Visible;
		cancelButton.Visibility = Visibility.Visible;
		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxGroupSelectionMessage message)
	{
		Reset();

		_groupSelectionAction = message.Confirm;

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		this.message.Margin = new Thickness(0, 0, 0, 8);
		groupSelectionList.ItemsSource = message.Groups;
		groupSelectionList.Visibility = Visibility.Visible;
		cancelButton.Visibility = Visibility.Visible;
		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxChecklistMessage message)
	{
		Reset();

		_checklistAction = message.Confirm;
		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		this.message.TextWrapping = TextWrapping.Wrap;
		this.message.Margin = new Thickness(0, 0, 0, 8);
		checklistSelectionList.ItemsSource = message.Items;
		checklistSelectionList.Visibility = Visibility.Visible;
		cancelButton.Visibility = Visibility.Visible;
		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxColorPickerMessage message)
	{
		Reset();

		_colorPickerAction = message.Confirm;
		_selectedColor = message.CurrentColor;

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = message.Message;
		colorPickerPanel.Visibility = Visibility.Visible;
		UpdateColorPreview();
		cancelButton.Visibility = Visibility.Visible;
		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
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
		if (!string.IsNullOrEmpty(text))
		{
			try
			{
				var color = (Color)ColorConverter.ConvertFromString(text);
				_selectedColor = text;
				colorPreviewBorder.Background = new SolidColorBrush(color);
				colorCodeText.Text = text;
				colorInputPreview.Background = new SolidColorBrush(color);
			}
			catch
			{
				// 输入无效颜色时不做响应，预览保持原样
				colorInputPreview.Background = null;
			}
		}
	}

	private void Reset()
	{
		_inputAction = null;
		_abortAction = null;
		_confirmAction = null;
		_selectionAction = null;
		_selectionAbortAction = null;
		_tagSelectionAction = null;
		_groupSelectionAction = null;
		_checklistAction = null;
		_colorPickerAction = null;
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
		groupSelectionList.Visibility = Visibility.Collapsed;
		groupSelectionList.ItemsSource = null;
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
		exportProgressPanel.Visibility = Visibility.Collapsed;
		exportCurrentFile.Visibility = Visibility.Visible;
		exportSpeedText.Visibility = Visibility.Visible;
		exportCurrentFile.Text = "";
		exportSpeedText.Text = "";
		exportRatioText.Text = "";
		updateProgressPanel.Visibility = Visibility.Collapsed;
		updatePhaseText.Text = "";
		updateCurrentFile.Text = "";
		updateFileCount.Text = "";
		updateNeedUpdateCount.Text = "";
	}

	private void OkButton_Click(object sender, RoutedEventArgs e)
	{
		Receive(new MessageBoxHideMessage());

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
		else if (_groupSelectionAction != null)
		{
			var selectedGroups = groupSelectionList.ItemsSource.Cast<Models.ModGroupSelectionItem>().Where(g => g.IsSelected).ToList();
			_groupSelectionAction(selectedGroups);
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
	}

	private void NoButton_Click(object sender, RoutedEventArgs e)
	{
		Receive(new MessageBoxHideMessage());

		_abortAction?.Invoke();
	}

	private void YesButton_Click(object sender, RoutedEventArgs e)
	{
		Receive(new MessageBoxHideMessage());

		_confirmAction?.Invoke();
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		Receive(new MessageBoxHideMessage());
		_selectionAbortAction?.Invoke();
	}

	private void ColorBorder_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border border && border.Tag is string colorCode)
		{
			SelectColor(colorCode);
		}
	}
}
