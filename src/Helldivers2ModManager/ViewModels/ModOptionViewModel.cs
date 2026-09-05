using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.ViewModels;

internal sealed class ModOptionViewModel(ModViewModel vm, int idx) : ObservableObject
{
	public string Name => ((V1ModManifest)_vm.Data.Manifest).Options![_idx].Name;

	public bool Enabled
	{
		get => _vm.Data.EnabledOptions[_idx];

		set
		{
			if (_vm.Data.EnabledOptions[_idx] == value)
				return;
			OnPropertyChanging();
			_vm.Data.EnabledOptions[_idx] = value;
			OnPropertyChanged();
			_vm.OnOptionsChanged();
		}
	}

	public string Description => ((V1ModManifest)_vm.Data.Manifest).Options![_idx].Description;

	public Visibility ImageVisibility => ((V1ModManifest)_vm.Data.Manifest).Options![_idx].Image is not null ? Visibility.Visible : Visibility.Collapsed;

	public ImageSource? Image
	{
		get
		{
			var path = ((V1ModManifest)_vm.Data.Manifest).Options![_idx].Image;
			if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(path))
				return null;
			try
			{
				var fullPath = Path.Combine(_vm.Data.Directory.FullName, path);
				if (!File.Exists(fullPath))
					return null;
				// 按路径缓存解码结果：属性在布局/滚动中被反复读取，每次重解码开销大
				if (_cachedImage is not null && _cachedImagePath == fullPath)
					return _cachedImage;
				var bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.UriSource = new Uri(fullPath);
				bmp.CacheOption = BitmapCacheOption.OnLoad;
				bmp.EndInit();
				bmp.Freeze();
				_cachedImage = bmp;
				_cachedImagePath = fullPath;
				return bmp;
			}
			catch
			{
				return null;
			}
		}
	}

	private BitmapSource? _cachedImage;
	private string? _cachedImagePath;

	public Visibility SubOptionVisibility => ((V1ModManifest)_vm.Data.Manifest).Options![_idx].SubOptions is not null ? Visibility.Visible : Visibility.Collapsed;

	public ModSubOptionViewModel[]? SubOptions => _subs;

	public int SelectedSubOption
	{
		get => _vm.Data.SelectedOptions[_idx];

		set
		{
			if (_vm.Data.SelectedOptions[_idx] == value)
				return;
			OnPropertyChanging();
			_vm.Data.SelectedOptions[_idx] = value;
			OnPropertyChanged();
			_vm.OnOptionsChanged();
		}
	}

	private readonly ModViewModel _vm = vm;
	private readonly int _idx = idx;
	private readonly ModSubOptionViewModel[]? _subs = ((V1ModManifest)vm.Data.Manifest).Options![idx].SubOptions?.Select((_, i) => new ModSubOptionViewModel(vm, idx, i)).ToArray();
}
