using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Input;
using NewAxis.Models;
using NewAxis.Services;
using System;
using Avalonia.Controls;

namespace NewAxis.ViewModels;

public class MainViewModel : ViewModelBase
{
    public LocalizationService Localization => LocalizationService.Instance;

    public ObservableCollection<Game> Games { get; } = new();

    private Game? _selectedGame;
    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetField(ref _selectedGame, value))
            {
                OnPropertyChanged(nameof(SelectedMod));
                if (_selectedGame != null && _selectedGame.SupportedMods.Count > 0)
                {
                    SelectedMod = _selectedGame.SupportedMods[0];
                }
            }
        }
    }

    private string? _selectedMod;
    public string? SelectedMod
    {
        get => _selectedMod;
        set => SetField(ref _selectedMod, value);
    }

    private double _depth = 50;
    public double Depth
    {
        get => _depth;
        set => SetField(ref _depth, value);
    }

    private double _popout = 50;
    public double Popout
    {
        get => _popout;
        set => SetField(ref _popout, value);
    }

    public ICommand StartGameCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand SelectModCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ToggleHiddenGamesCommand { get; }
    public ICommand ApplySettingsCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetField(ref _isSettingsOpen, value);
    }

    private bool _showUninstalledGames = true;
    public bool ShowUninstalledGames
    {
        get => _showUninstalledGames;
        set => SetField(ref _showUninstalledGames, value);
    }

    private bool _installModTemporarily = true;
    public bool InstallModTemporarily
    {
        get => _installModTemporarily;
        set => SetField(ref _installModTemporarily, value);
    }

    private string _hotkeyDepthInc = "Ctrl+Up";
    public string HotkeyDepthInc
    {
        get => _hotkeyDepthInc;
        set => SetField(ref _hotkeyDepthInc, value);
    }
    public Key KeyDepthInc { get; set; } = Key.Up;
    public KeyModifiers ModDepthInc { get; set; } = KeyModifiers.Control;

    private string _hotkeyDepthDec = "Ctrl+Down";
    public string HotkeyDepthDec
    {
        get => _hotkeyDepthDec;
        set => SetField(ref _hotkeyDepthDec, value);
    }
    public Key KeyDepthDec { get; set; } = Key.Down;
    public KeyModifiers ModDepthDec { get; set; } = KeyModifiers.Control;


    private string _hotkeyPopoutInc = "Ctrl+Right";
    public string HotkeyPopoutInc
    {
        get => _hotkeyPopoutInc;
        set => SetField(ref _hotkeyPopoutInc, value);
    }
    public Key KeyPopoutInc { get; set; } = Key.Right;
    public KeyModifiers ModPopoutInc { get; set; } = KeyModifiers.Control;

    private string _hotkeyPopoutDec = "Ctrl+Left";
    public string HotkeyPopoutDec
    {
        get => _hotkeyPopoutDec;
        set => SetField(ref _hotkeyPopoutDec, value);
    }
    public Key KeyPopoutDec { get; set; } = Key.Left;
    public KeyModifiers ModPopoutDec { get; set; } = KeyModifiers.Control;

    public ObservableCollection<string> AvailableLanguages => Localization.AvailableLanguages;

    public string SelectedLanguage
    {
        get => Localization.CurrentLanguage;
        set
        {
            if (Localization.CurrentLanguage != value)
            {
                Localization.CurrentLanguage = value;
                OnPropertyChanged(nameof(SelectedLanguage));
                OnPropertyChanged(nameof(Localization)); // Force re-binding of the Localization object
                System.Diagnostics.Debug.WriteLine($"Language changed to {value}");
            }
        }
    }

    private readonly IniFileParser _iniParser = new();
    private const string CONFIG_PATH = "config.ini";

    public MainViewModel()
    {
        _allGames = new List<Game>
        {
            new Game("Cyberpunk 2077", "C:\\Games\\Cyberpunk 2077", new() { "Cinematic Realism", "Ultra Performance" }),
            new Game("The Witcher 3", "C:\\Games\\The Witcher 3", new() { "HD Reworked", "Lighting Mod" }),
            new Game("Elden Ring", "C:\\Games\\Elden Ring", new() { "Easy Mode", "FPS Unlocker" })
        };

        LoadConfig();
        RefreshGamesList();

        if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();

        StartGameCommand = new RelayCommand(ExecuteStartGame);
        BrowseCommand = new RelayCommand(ExecuteBrowse);
        RemoveGameCommand = new RelayCommand(ExecuteRemoveGame);
        SelectModCommand = new RelayCommand(ExecuteSelectMod);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleHiddenGamesCommand = new RelayCommand(ExecuteToggleHiddenGames);
        ApplySettingsCommand = new RelayCommand(ExecuteApplySettings);
        ResetDefaultsCommand = new RelayCommand(ExecuteResetDefaults);
    }

    private void LoadConfig()
    {
        try
        {
            _iniParser.Load(CONFIG_PATH);

            string? lang = _iniParser.GetValue("Settings", "Language");
            if (!string.IsNullOrEmpty(lang)) SelectedLanguage = lang;

            string? installTemp = _iniParser.GetValue("Settings", "InstallModTemporarily");
            if (bool.TryParse(installTemp, out bool bInstallTemp)) InstallModTemporarily = bInstallTemp;

            string? showUninstall = _iniParser.GetValue("Settings", "ShowUninstalledGames");
            if (bool.TryParse(showUninstall, out bool bShowUninstall)) ShowUninstalledGames = bShowUninstall;

            LoadHotkey("DepthInc", (d, k, m) => { HotkeyDepthInc = d; KeyDepthInc = k; ModDepthInc = m; });
            LoadHotkey("DepthDec", (d, k, m) => { HotkeyDepthDec = d; KeyDepthDec = k; ModDepthDec = m; });
            LoadHotkey("PopoutInc", (d, k, m) => { HotkeyPopoutInc = d; KeyPopoutInc = k; ModPopoutInc = m; });
            LoadHotkey("PopoutDec", (d, k, m) => { HotkeyPopoutDec = d; KeyPopoutDec = k; ModPopoutDec = m; });

            foreach (var game in _allGames)
            {
                var path = _iniParser.GetValue("Games", game.Name);
                if (path != null)
                {
                    game.InstallPath = path;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
        }
    }
    void LoadHotkey(string prefix, Action<string, Key, KeyModifiers> setter)
    {
        var display = _iniParser.GetValue("Hotkeys", prefix + "Display");
        var keyStr = _iniParser.GetValue("Hotkeys", prefix + "Key");
        var modStr = _iniParser.GetValue("Hotkeys", prefix + "Mod");

        if (!string.IsNullOrEmpty(display) &&
            Enum.TryParse(keyStr, true, out Key key) &&
            Enum.TryParse(modStr, true, out KeyModifiers mod))
        {
            setter(display, key, mod);
        }
    }

    private void SaveConfig()
    {
        try
        {
            _iniParser.SetValue("Settings", "Language", SelectedLanguage);
            _iniParser.SetValue("Settings", "InstallModTemporarily", InstallModTemporarily.ToString());
            _iniParser.SetValue("Settings", "ShowUninstalledGames", ShowUninstalledGames.ToString());

            void SaveHotkey(string prefix, string display, Key key, KeyModifiers mod)
            {
                _iniParser.SetValue("Hotkeys", prefix + "Display", display);
                _iniParser.SetValue("Hotkeys", prefix + "Key", key.ToString());
                _iniParser.SetValue("Hotkeys", prefix + "Mod", mod.ToString());
            }

            SaveHotkey("DepthInc", HotkeyDepthInc, KeyDepthInc, ModDepthInc);
            SaveHotkey("DepthDec", HotkeyDepthDec, KeyDepthDec, ModDepthDec);
            SaveHotkey("PopoutInc", HotkeyPopoutInc, KeyPopoutInc, ModPopoutInc);
            SaveHotkey("PopoutDec", HotkeyPopoutDec, KeyPopoutDec, ModPopoutDec);

            foreach (var game in _allGames)
            {
                _iniParser.SetValue("Games", game.Name, game.InstallPath);
            }

            _iniParser.Save(CONFIG_PATH);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    private void ExecuteApplySettings(object? obj)
    {
        IsSettingsOpen = false;
        SaveConfig();
    }

    private List<Game> _allGames;

    private void RefreshGamesList()
    {
        var currentSelection = SelectedGame;
        Games.Clear();
        foreach (var game in _allGames)
        {
            if (ShowUninstalledGames || game.IsInstalled)
            {
                Games.Add(game);
            }
        }

        if (currentSelection != null && Games.Contains(currentSelection))
        {
            SelectedGame = currentSelection;
        }
        else
        {
            SelectedGame = Games.FirstOrDefault();
        }
    }

    private void ExecuteToggleHiddenGames(object? obj)
    {
        ShowUninstalledGames = !ShowUninstalledGames;
        RefreshGamesList();
        SaveConfig();
    }

    private void ExecuteResetDefaults(object? obj)
    {
        SelectedLanguage = "en-US";
        InstallModTemporarily = true;
        ShowUninstalledGames = true;
        HotkeyDepthInc = "Ctrl+Up";
        KeyDepthInc = Key.Up; ModDepthInc = KeyModifiers.Control;

        HotkeyDepthDec = "Ctrl+Down";
        KeyDepthDec = Key.Down; ModDepthDec = KeyModifiers.Control;

        HotkeyPopoutInc = "Ctrl+Right";
        KeyPopoutInc = Key.Right; ModPopoutInc = KeyModifiers.Control;

        HotkeyPopoutDec = "Ctrl+Left";
        KeyPopoutDec = Key.Left; ModPopoutDec = KeyModifiers.Control;

        SaveConfig();
    }

    private void ExecuteStartGame(object? obj)
    {
        System.Diagnostics.Debug.WriteLine($"Starting {SelectedGame?.Name} with mod {SelectedMod}");
    }

    public event Func<System.Threading.Tasks.Task<string?>>? RequestBrowseFolder;

    private async void ExecuteBrowse(object? obj)
    {
        if (RequestBrowseFolder != null && SelectedGame != null)
        {
            var path = await RequestBrowseFolder.Invoke();
            if (!string.IsNullOrEmpty(path))
            {
                SelectedGame.InstallPath = path;
                RefreshGamesList();
                SaveConfig();
            }
        }
        System.Diagnostics.Debug.WriteLine($"Browsing for {SelectedGame?.Name}");
    }

    private void ExecuteRemoveGame(object? obj)
    {
        if (SelectedGame != null)
        {
            SelectedGame.InstallPath = string.Empty;
            RefreshGamesList();
            SaveConfig();
        }
    }

    public void UpdateHotkey(string tag, Key key, KeyModifiers modifiers, string displayString)
    {
        switch (tag)
        {
            case "DepthInc":
                KeyDepthInc = key;
                ModDepthInc = modifiers;
                HotkeyDepthInc = displayString;
                break;
            case "DepthDec":
                KeyDepthDec = key;
                ModDepthDec = modifiers;
                HotkeyDepthDec = displayString;
                break;
            case "PopoutInc":
                KeyPopoutInc = key;
                ModPopoutInc = modifiers;
                HotkeyPopoutInc = displayString;
                break;
            case "PopoutDec":
                KeyPopoutDec = key;
                ModPopoutDec = modifiers;
                HotkeyPopoutDec = displayString;
                break;
        }
    }

    private void ExecuteSelectMod(object? modName)
    {
        if (modName is string mod)
        {
            SelectedMod = mod;
        }
    }
}
