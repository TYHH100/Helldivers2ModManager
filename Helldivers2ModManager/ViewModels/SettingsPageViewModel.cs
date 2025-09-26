﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class SettingsPageViewModel : PageViewModelBase
{
	public override string Title => "Settings";

	public string GameDir
	{
		get => _settingsService.Initialized ? _settingsService.GameDirectory : string.Empty;
		set
		{
			OnPropertyChanging();
			_settingsService.GameDirectory = value;
			OnPropertyChanged();
		}
	}

	public string TempDir
	{
		get => _settingsService.Initialized ? _settingsService.TempDirectory : string.Empty;
		set
		{
			OnPropertyChanging();
			_settingsService.TempDirectory = value;
			OnPropertyChanged();
		}
	}

	public string StorageDir
	{
		get => _settingsService.Initialized ? _settingsService.StorageDirectory : string.Empty;
		set
		{
			OnPropertyChanging();
			_settingsService.StorageDirectory = value;
			OnPropertyChanged();
		}
	}

	public LogLevel LogLevel
	{
		get => _settingsService.Initialized ? _settingsService.LogLevel : LogLevel.Warning;
		set
		{
			OnPropertyChanging();
			_settingsService.LogLevel = value;
			OnPropertyChanged();
		}
	}

	public float Opacity
	{
		get => _settingsService.Initialized ? _settingsService.Opacity : 0.8f;
		set
		{
			OnPropertyChanging();
			_settingsService.Opacity = value;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<string> SkipList => _settingsService.Initialized ? _settingsService.SkipList : [];

	public bool CaseSensitiveSearch
	{
		get => _settingsService.Initialized ? _settingsService.CaseSensitiveSearch : false;
		set
		{
			OnPropertyChanging();
			_settingsService.CaseSensitiveSearch = value;
			OnPropertyChanged();
		}
	}

	private readonly ILogger<SettingsPageViewModel> _logger;
	private readonly NavigationStore _navStore;
	private readonly SettingsService _settingsService;
	[ObservableProperty]
	private int _selectedSkip = -1;

	public SettingsPageViewModel(ILogger<SettingsPageViewModel> logger, NavigationStore navStore, SettingsService settingsService)
	{
		_logger = logger;
		_navStore = navStore;
		_settingsService = settingsService;

		SkipList.CollectionChanged += SkipList_CollectionChanged;

		if (MessageBox.IsRegistered)
			_ = Init();
		else
			MessageBox.Registered += (_, _) => _ = Init();
	}

	private static bool ValidateGameDir(DirectoryInfo dir, [NotNullWhen(false)] out string? error)
	{
		if (!dir.Exists)
		{
			error = "选择的Helldivers 2文件夹不存在!";
			return false;
		}

		if (dir is not DirectoryInfo { Name: "Helldivers 2" })
		{
			error = "选择的Helldivers 2文件夹并不在有效目录中!";
			return false;
		}

		var subDirs = dir.EnumerateDirectories();
		if (!subDirs.Any(static d => d.Name == "data"))
		{
			error = "选择的Helldivers 2根目录中没有名为 \"data\" 文件夹!";
			return false;
		}
		if (!subDirs.Any(static d => d.Name == "tools"))
		{
			error = "选择的Helldivers 2根目录中没有名为 \"tools\" 文件夹!";
			return false;
		}
		if (subDirs.FirstOrDefault(static d => d.Name == "bin") is not DirectoryInfo binDir)
		{
			error = "选择的Helldivers 2根目录中没有名为 \"bin\" 文件夹!";
			return false;
		}
		if (!binDir.GetFiles("helldivers2.exe").Any())
		{
			error = "选定的Helldivers 2文件路径中,在 \"bin\" 文件夹中没有 \"helldivers2.exe\" 文件!";
			return false;
		}

		error = null;
		return true;
	}

	protected override void OnPropertyChanged(PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SelectedSkip))
			RemoveSkipCommand.NotifyCanExecuteChanged();

		base.OnPropertyChanged(e);
	}

	private bool ValidateSettings()
	{
		if (string.IsNullOrEmpty(GameDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "游戏目录不能为空!"
            });
			return false;
		}

		if (string.IsNullOrEmpty(StorageDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "存储目录不能为空!"
            });
			return false;
		}

		if (string.IsNullOrEmpty(TempDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "临时目录不能为空!"
            });
			return false;
		}

		return true;
	}

	private async Task Init()
	{
		_logger.LogInformation("Loading settings...");
		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "Loading settings",
			Message = "Please wait democratically.",
		});
		try
		{
			if (!await _settingsService.InitAsync())
				_settingsService.InitDefault();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Loading settings failed");
			WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
			{
				Title = "Loading settings failed!",
				Message = "Do you want to reset your settings?",
				Confirm = () =>
				{
					_settingsService.InitDefault();
					Update();
				},
			});
			return;
		}
		_logger.LogInformation("Settings loaded successfully");
		WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
	}

	private void Update()
	{
		OnPropertyChanged(nameof(GameDir));
		OnPropertyChanged(nameof(TempDir));
		OnPropertyChanged(nameof(StorageDir));
		OnPropertyChanged(nameof(LogLevel));
		OnPropertyChanged(nameof(Opacity));
		OnPropertyChanged(nameof(SkipList));
		OnPropertyChanged(nameof(CaseSensitiveSearch));
	}

	private void SkipList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		RemoveSkipCommand.NotifyCanExecuteChanged();
	}

	[RelayCommand]
	async Task Ok()
	{
		if (!ValidateSettings())
			return;

		if (!_settingsService.Validate())
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "Invalid settings!",
			});
			return;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "Saving Settings",
			Message = "Please wait democratically."
		});
		try
		{
			await _settingsService.SaveAsync();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to save settings");
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = $"Failed to save settings!\n\n{ex.Message}",
			});
			return;
		}
		WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	void Reset()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
		{
			Title = "重置?",
			Message = "您真的要重置设置?",
			Confirm = () =>
			{
				_settingsService.Reset();
				Update();
			}
		});
	}

	[RelayCommand]
	void BrowseGame()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			Title = "请选择您的Helldivers 2文件夹..."
        };

		if (dialog.ShowDialog() ?? false)
		{
			var newDir = new DirectoryInfo(dialog.FolderName);

			if (newDir.Parent is DirectoryInfo { Name: "Helldivers 2" })
			{
				newDir = newDir.Parent;
			}

			if (ValidateGameDir(newDir, out var error))
				GameDir = newDir.FullName;
			else
				WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
				{
					Message = error
				});
		}
	}

	[RelayCommand]
	void BrowseStorage()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			ValidateNames = true,
			Title = "选择您想要模组管理器|存放模组的文件夹..."
        };

		if (dialog.ShowDialog() ?? false)
			StorageDir = dialog.FolderName;
	}

	[RelayCommand]
	void BrowseTemp()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			ValidateNames = true,
			Title = "选择您想要模组管理器|存放临时文件的文件夹..."
        };

		if (dialog.ShowDialog() ?? false)
			TempDir = dialog.FolderName;
	}

	[RelayCommand]
	void HardPurge()
	{
		_logger.LogInformation("Hard purging patch files");
		
		var path = Path.Combine(_settingsService.StorageDirectory, "installed.txt");
		if (File.Exists(path))
			File.Delete(path);

		var dataDir = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
		
		var files = dataDir.EnumerateFiles("*.patch_*").ToArray();
		_logger.LogDebug("Found {} patch files", files.Length);

		foreach (var file in files)
		{
			_logger.LogTrace("Deleting \"{}\"", file.Name);
			file.Delete();
		}

		_logger.LogInformation("Hard purge complete");
	}

	[RelayCommand]
	void AddSkip()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
		{
			Title = "文件名?",
			Message = "Please enter the 16 character name of an archive file you want to skip patch 0 for.",
			MaxLength = 16,
			Confirm = (str) =>
			{
				if (str.Length == 16)
					SkipList.Add(str);
				else
					WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
					{
						Message = "Mod文件名的长度只能为 16 字符串."
                    });
			}
		});
	}

	bool CanRemoveSkip()
	{
		return SelectedSkip != -1;
	}

	[RelayCommand(CanExecute = nameof(CanRemoveSkip))]
	void RemoveSkip()
	{
		SkipList.RemoveAt(SelectedSkip);
	}

	[RelayCommand]
	async Task DetectGame()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "查找游戏",
			Message = "请民主官耐心等待."
		});

		var (result, path) = await Task.Run<(bool, string?)>(static () =>
		{
			foreach(var drive in Environment.GetLogicalDrives())
			{
				string path;
				if (drive == "C:\\")
				{
					path = Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2");
					if (ValidateGameDir(new DirectoryInfo(path), out _))
						return (true, path);
				}

				path = Path.Combine(drive, "Steam", "steamapps", "common", "Helldivers 2");
				if (ValidateGameDir(new DirectoryInfo(path), out _))
					return (true, path);

				path = Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Helldivers 2");
				if (ValidateGameDir(new DirectoryInfo(path), out _))
					return (true, path);
			}

			return (false, null);
		});

        if (result && path != null)
        {
            GameDir = path;
			WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
        else
			WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
			{
				Message = "无法自动找到Helldivers 2游戏,请手动设置."
			});
	}
}
