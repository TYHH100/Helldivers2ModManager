using CommunityToolkit.Mvvm.ComponentModel;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModViewModel : ObservableObject
{
    //public event EventHandler? SaveEnabledRequired;

    private readonly ILogger _logger;

    public Guid Guid => _mod.Manifest.Guid;

    public string Name => _mod.Manifest.Name;

    public string Description => _mod.Manifest.Description;

    public Visibility OptionsVisible
    {
        get
        {
            if (_mod.Manifest.Version == ManifestVersion.Legacy && (_mod.Manifest as LegacyModManifest)!.Options is not null)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }
    }

    public Visibility EditVisible => _mod.Manifest.Version == ManifestVersion.V1 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private ImageSource? _icon;

    public ModData Data => _mod;

    public string[]? LegacyOptions { get; }

    public bool Enabled
    {
        get => _mod.Enabled;

        set
        {
            if (_mod.Enabled == value)
                return;
            OnPropertyChanging();
            _mod.Enabled = value;
            OnPropertyChanged();
        }
    }

    public int LegacySelectedOption
    {
        get => _mod.Manifest.Version == ManifestVersion.Legacy ? _mod.SelectedOptions[0] : -1;

        set
        {
            if (_mod.Manifest.Version != ManifestVersion.Legacy)
                return;
            OnPropertyChanging();
            _mod.SelectedOptions[0] = value;
            OnPropertyChanged();
        }
    }

    public ModOptionViewModel[]? Options { get; }

    private readonly ModData _mod;

    public ModViewModel(ModData mod, ILogger logger)
    {
        _mod = mod;
        _logger = logger;

        switch (_mod.Manifest.Version)
        {
            case ManifestVersion.Legacy:
                LegacyOptions = ((LegacyModManifest)_mod.Manifest).Options?.ToArray();
                break;

            case ManifestVersion.V1:
            {
                var manifest = (V1ModManifest)_mod.Manifest;                    
                if (manifest.Options is null)
                    break;
                Options = new ModOptionViewModel[manifest.Options.Count];
                for (int i = 0; i < manifest.Options.Count; i++)
                    Options[i] = new ModOptionViewModel(this, i);
                break;
            }
            
            case ManifestVersion.V2:
                throw new NotSupportedException();
            
            default:
                throw new NotImplementedException();
        }

        LoadIcon();
    }

    public void LoadIcon()
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        var path = _mod.Manifest.IconPath;
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(path))
            bmp.UriSource = new Uri(@"..\Resources\Images\logo_icon.png", UriKind.Relative);
        else
        {
            bmp.UriSource = new Uri(Path.Combine(_mod.Directory.FullName, path));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
        }
        bmp.EndInit();
        Icon = bmp;
    }
}