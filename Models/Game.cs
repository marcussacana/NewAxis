using System.Collections.Generic;

namespace NewAxis.Models;

using System.ComponentModel;
using System.Runtime.CompilerServices;

public class Game : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public string BannerPath { get; set; } = string.Empty;

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

    public List<string> SupportedMods { get; set; } = new();

    public string Initials => Name.Length > 0 ? Name.Substring(0, 1) : "?";

    public Game(string name, string installPath, List<string> mods, string bannerPath = "")
    {
        Name = name;
        InstallPath = installPath;
        SupportedMods = mods;
        BannerPath = bannerPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
