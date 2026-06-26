using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Helldivers2ModManager.Models;

public sealed class ModTag : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    private string _name = string.Empty;
    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }
    }

    private string _color = "#FF3B82F6";
    [JsonPropertyName("color")]
    public string Color
    {
        get => _color;
        set
        {
            if (_color != value)
            {
                _color = value;
                OnPropertyChanged(nameof(Color));
                OnPropertyChanged(nameof(Brush));
            }
        }
    }

    public SolidColorBrush Brush => GetColorBrush();

    public SolidColorBrush Foreground => GetForegroundBrush();

    public ModTag(string name, string color = "#FF3B82F6")
    {
        Id = Guid.NewGuid();
        Name = name;
        Color = color;
    }

    [JsonConstructor]
    public ModTag(Guid id, string name, string color)
    {
        Id = id;
        Name = name;
        Color = color;
    }

    public SolidColorBrush GetColorBrush()
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Color);
            return new SolidColorBrush(c);
        }
        catch
        {
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(98, 32, 238));
        }
    }

    public SolidColorBrush GetForegroundBrush()
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Color);
            var brightness = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            return brightness > 128 ? new SolidColorBrush(Colors.Black) : new SolidColorBrush(Colors.White);
        }
        catch
        {
            return new SolidColorBrush(Colors.White);
        }
    }

    public override string ToString()
    {
        return Name;
    }
}