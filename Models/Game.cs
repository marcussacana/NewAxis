using System.Collections.Generic;
using System.Linq;

namespace NewAxis.Models;

using System.ComponentModel;
using System.Runtime.CompilerServices;

public class Game : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;

    private Avalonia.Media.Imaging.Bitmap? _bannerImage;
    public Avalonia.Media.Imaging.Bitmap? BannerImage
    {
        get => _bannerImage;
        set
        {
            if (_bannerImage != value)
            {
                _bannerImage = value;
                OnPropertyChanged();
            }
        }
    }

    private string _creator = string.Empty;
    public string Creator
    {
        get => _creator;
        set
        {
            if (_creator != value)
            {
                _creator = value;
                OnPropertyChanged();
            }
        }
    }

    private Avalonia.Media.Imaging.Bitmap? _logoImage;
    public Avalonia.Media.Imaging.Bitmap? LogoImage
    {
        get => _logoImage;
        set
        {
            if (_logoImage != value)
            {
                _logoImage = value;
                OnPropertyChanged();
            }
        }
    }

    private Avalonia.Media.Effect _logoShadowEffect = new Avalonia.Media.DropShadowEffect
    {
        Color = Avalonia.Media.Colors.Black,
        BlurRadius = 10,
        Opacity = 1,
        OffsetX = 0,
        OffsetY = 0
    };
    public Avalonia.Media.Effect LogoShadowEffect
    {
        get => _logoShadowEffect;
        set
        {
            if (_logoShadowEffect != value)
            {
                _logoShadowEffect = value;
                OnPropertyChanged();
            }
        }
    }

    private Avalonia.Media.Imaging.Bitmap? _iconImage;
    public Avalonia.Media.Imaging.Bitmap? IconImage
    {
        get => _iconImage;
        set
        {
            if (_iconImage != value)
            {
                _iconImage = value;
                OnPropertyChanged();
            }
        }
    }

    private string _installPath = string.Empty;
    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (_installPath != value)
            {
                _installPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInstalled));
            }
        }
    }

    public bool IsInstalled => !string.IsNullOrEmpty(InstallPath);

    private List<ModType> _supportedModTypes = new();
    public List<ModType> SupportedModTypes
    {
        get => _supportedModTypes;
        set
        {
            _supportedModTypes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SupportedMods));
        }
    }

    // Adapter for UI binding (read-only view)
    public List<string> SupportedMods => SupportedModTypes.Select(type => type.GetDescription()).ToList();

    // Stores additional metadata (GameIndexEntry)
    public object? Tag { get; set; }

    public string Initials => Name.Length > 0 ? Name.Substring(0, 1) : "?";

    public Game(string name, string installPath, List<ModType> mods)
    {
        Name = name;
        InstallPath = installPath;
        SupportedModTypes = mods;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
