// Ignore Spelling: Helldivers

using CommunityToolkit.Mvvm.Messaging;
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
}

internal sealed class MessageBoxTagSelectionMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required List<Models.TagSelectionItem> Tags { get; init; }

	public required Action<IEnumerable<Models.TagSelectionItem>> Confirm { get; init; }
}

internal sealed class MessageBoxColorPickerMessage
{
	public required string Title { get; init; }

	public required string Message { get; init; }

	public required string CurrentColor { get; init; }

	public required Action<string> Confirm { get; init; }
}

internal partial class MessageBox : UserControl, IRecipient<MessageBoxInfoMessage>, IRecipient<MessageBoxWarningMessage>, IRecipient<MessageBoxErrorMessage>, IRecipient<MessageBoxProgressMessage>, IRecipient<MessageBoxExportProgressMessage>, IRecipient<MessageBoxExportProgressUpdateMessage>, IRecipient<MessageBoxHideMessage>, IRecipient<MessageBoxInputMessage>, IRecipient<MessageBoxConfirmMessage>, IRecipient<MessageBoxSelectionMessage>, IRecipient<MessageBoxTagSelectionMessage>, IRecipient<MessageBoxColorPickerMessage>
{
	public static bool IsRegistered { get; private set; }

	public static event EventHandler? Registered;
	
	private Action<string>? _inputAction;
	private Action? _abortAction;
	private Action? _confirmAction;
	private Action<object>? _selectionAction;
	private Action<IEnumerable<Models.TagSelectionItem>>? _tagSelectionAction;
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
		WeakReferenceMessenger.Default.Register<MessageBoxHideMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxInputMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxConfirmMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxSelectionMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxTagSelectionMessage>(this);
		WeakReferenceMessenger.Default.Register<MessageBoxColorPickerMessage>(this);

		if (!IsRegistered)
		{
			IsRegistered = true;
			Registered?.Invoke(this, EventArgs.Empty);
		}
	}

	public void Receive(MessageBoxInfoMessage message)
	{
		Reset();

			title.Text = "信息";
			this.message.Text = message.Message;

		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxWarningMessage message)
	{
		Reset();

			title.Text = "警告";
			brush.Color = Colors.Yellow;
			this.message.Text = message.Message;

		okButton.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxErrorMessage message)
	{
		Reset();

			title.Text = "错误";
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

		progress.Visibility = Visibility.Visible;
		Visibility = Visibility.Visible;
	}

	public void Receive(MessageBoxExportProgressMessage message)
	{
		Reset();

		title.Text = message.Title;
		brush.Color = Colors.White;
		this.message.Text = "正在压缩...";

		// Switch progress bar to determinate mode
		progress.IsIndeterminate = false;
		progress.Value = 0;
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
		}
	}

	private void SelectColor(string colorCode)
	{
		_selectedColor = colorCode;
		UpdateColorPreview();
	}

	private void Reset()
	{
		_inputAction = null;
		_confirmAction = null;
		_selectionAction = null;
		_tagSelectionAction = null;
		_colorPickerAction = null;
		_selectedColor = null;

		title.Visibility = Visibility.Visible;
		brush.Color = Colors.White;
		message.Visibility = Visibility.Visible;
		message.Margin = new Thickness(0);
		input.Visibility = Visibility.Collapsed;
		selectionComboBox.Visibility = Visibility.Collapsed;
		tagSelectionList.Visibility = Visibility.Collapsed;
		tagSelectionList.ItemsSource = null;
		colorPickerPanel.Visibility = Visibility.Collapsed;
		cancelButton.Visibility = Visibility.Hidden;
		okButton.Visibility = Visibility.Hidden;
		yesNoStack.Visibility = Visibility.Hidden;
		progress.IsIndeterminate = true;
		progress.Visibility = Visibility.Hidden;
		exportProgressPanel.Visibility = Visibility.Collapsed;
		exportCurrentFile.Visibility = Visibility.Visible;
		exportSpeedText.Visibility = Visibility.Visible;
		exportCurrentFile.Text = "";
		exportSpeedText.Text = "";
		exportRatioText.Text = "";
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
	}

	private void ColorBorder_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border border && border.Tag is string colorCode)
		{
			SelectColor(colorCode);
		}
	}
}
