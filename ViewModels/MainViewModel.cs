using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Input;
using NewAxis.Models;
using NewAxis.Services;
using System;
using SixLabors.ImageSharp.Processing;
using System.Threading.Tasks;
using System.Diagnostics;

namespace NewAxis.ViewModels;

public class MainViewModel : ViewModelBase
{
    public const string REPO_BASE = "https://raw.githubusercontent.com/marcussacana/NewAxisData/refs/heads/master/";
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

                _ = LoadGameBannerAsync(_selectedGame);

                LoadGameConfig(_selectedGame);
            }
        }
    }

    private async Task LoadGameBannerAsync(Game? game)
    {
        if (game == null || _repoClient == null) return;

        try
        {
            if (game.Tag is GameIndexEntry indexEntry && indexEntry.Images != null)
            {
                if (!string.IsNullOrEmpty(indexEntry.Images.Wallpaper))
                {
                    Debug.WriteLine($"Downloading banner for {game.Name}");
                    game.BannerImage = await LoadImageAsync(indexEntry.Images.Wallpaper);
                }

                if (!string.IsNullOrEmpty(indexEntry.Images.Logo))
                {
                    Debug.WriteLine($"Downloading logo for {game.Name}");
                    var logoImg = await LoadImageAsync(indexEntry.Images.Logo, autoCropTransparency: true, gameInstance: game);
                    game.LogoImage = logoImg;
                }

                Debug.WriteLine($"Images loaded for {game.Name}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading images: {ex.Message}");
        }
    }

    private async Task<Avalonia.Media.Imaging.Bitmap?> LoadImageAsync(string url, bool autoCropTransparency = false, Game? gameInstance = null)
    {
        try
        {
            var imageBytes = await _repoClient!.DownloadImageAsync(url);

            return await Task.Run(() =>
            {
                Debug.WriteLine($"Loading image for {url}");
                using (var inputStream = new System.IO.MemoryStream(imageBytes))
                using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(inputStream))
                {
                    if (autoCropTransparency)
                    {
                        AutoCropTransparency(image);
                    }

                    if (gameInstance != null)
                    {
                        AnalyzeImageBrightness(image, gameInstance);
                    }

                    using (var outputStream = new System.IO.MemoryStream())
                    {
                        image.Save(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                        outputStream.Position = 0;
                        return new Avalonia.Media.Imaging.Bitmap(outputStream);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading image from {url}: {ex.Message}");
            return null;
        }
    }

    private void AutoCropTransparency(SixLabors.ImageSharp.Image image)
    {
        try
        {
            var rgba32Image = image as SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
            if (rgba32Image == null) return;

            int width = rgba32Image.Width;
            int height = rgba32Image.Height;

            int top = 0;
            for (int y = 0; y < height; y++)
            {
                bool hasOpaque = false;
                for (int x = 0; x < width; x++)
                {
                    if (rgba32Image[x, y].A > 10)
                    {
                        hasOpaque = true;
                        break;
                    }
                }
                if (hasOpaque)
                {
                    top = y;
                    break;
                }
            }

            int bottom = height - 1;
            for (int y = height - 1; y >= 0; y--)
            {
                bool hasOpaque = false;
                for (int x = 0; x < width; x++)
                {
                    if (rgba32Image[x, y].A > 10)
                    {
                        hasOpaque = true;
                        break;
                    }
                }
                if (hasOpaque)
                {
                    bottom = y;
                    break;
                }
            }


            if (top > 0 || bottom < height - 1)
            {
                int newHeight = bottom - top + 1;
                if (newHeight > 0)
                {
                    var cropRect = new SixLabors.ImageSharp.Rectangle(0, top, width, newHeight);
                    rgba32Image.Mutate(x => x.Crop(cropRect));
                    Debug.WriteLine($"Logo cropped: removed {top}px top, {height - bottom - 1}px bottom");
                }

            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error autocropping transparency: {ex.Message}");
        }
    }

    private void AnalyzeImageBrightness(SixLabors.ImageSharp.Image image, Game game)
    {
        try
        {
            var rgba32Image = image as SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
            if (rgba32Image == null)
            {
                Debug.WriteLine("Failed to analyze image brightness: Image is null");
                return;
            }


            rgba32Image.ProcessPixelRows(accessor =>
            {
                int darkPixels = 0;
                int totalPixels = 0;

                for (int y = 0; y < accessor.Height; y++)
                {
                    var rowSpan = accessor.GetRowSpan(y);
                    for (int x = 0; x < rowSpan.Length; x++)
                    {
                        var pixel = rowSpan[x];
                        if (pixel.A > 40)
                        {
                            float lum = 0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B;

                            if (lum < 110)
                            {
                                darkPixels++;
                            }
                            totalPixels++;
                        }
                    }
                }

                if (totalPixels > 0)
                {
                    double darkRatio = (double)darkPixels / totalPixels;
                    if (darkRatio > 0.7)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            game.LogoShadowEffect = new Avalonia.Media.DropShadowEffect
                            {
                                Color = Avalonia.Media.Colors.White,
                                BlurRadius = 10,
                                Opacity = 1,
                                OffsetX = 0,
                                OffsetY = 0
                            };
                        });
                        Debug.WriteLine($"Logo analyzed as DARK (Ratio: {darkRatio:P1}). Shadow set to White.");
                    }
                    else
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            game.LogoShadowEffect = new Avalonia.Media.DropShadowEffect
                            {
                                Color = Avalonia.Media.Colors.Black,
                                BlurRadius = 10,
                                Opacity = 1,
                                OffsetX = 0,
                                OffsetY = 0
                            };
                        });
                        Debug.WriteLine($"Logo analyzed as BRITE (Ratio: {1 - darkRatio:P1}). Shadow set to Black.");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error analyzing brightness: {ex.Message}");
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

    private bool _isProgressOverlayVisible;
    public bool IsProgressOverlayVisible
    {
        get => _isProgressOverlayVisible;
        set => SetField(ref _isProgressOverlayVisible, value);
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
                RefreshGamesList();
        }
    }

    private string _progressOverlayMessage = string.Empty;
    public string ProgressOverlayMessage
    {
        get => _progressOverlayMessage;
        set => SetField(ref _progressOverlayMessage, value);
    }

    private bool _isProgressOverlayError;
    public bool IsProgressOverlayError
    {
        get => _isProgressOverlayError;
        set => SetField(ref _isProgressOverlayError, value);
    }

    private bool _installModTemporarily = true;
    public bool InstallModTemporarily
    {
        get => _installModTemporarily;
        set => SetField(ref _installModTemporarily, value);
    }

    private bool _disableDLSS = true;
    public bool DisableDLSS
    {
        get => _disableDLSS;
        set => SetField(ref _disableDLSS, value);
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
                OnPropertyChanged(nameof(Localization));
                Debug.WriteLine($"Language changed to {value}");
            }
        }
    }

    private readonly IniFileParser _iniParser = new();
    private const string CONFIG_PATH = "config.ini";
    private GameRepositoryClient? _repoClient;

    public MainViewModel()
    {
        _allGames = new List<Game>();
        _repoClient = new GameRepositoryClient(REPO_BASE);

        LoadConfig();
        RefreshGamesList();

        _ = LoadGamesFromRepositoryAsync();

        StartGameCommand = new RelayCommand(ExecuteStartGame);
        BrowseCommand = new RelayCommand(ExecuteBrowse);
        RemoveGameCommand = new RelayCommand(ExecuteRemoveGame);
        SelectModCommand = new RelayCommand(ExecuteSelectMod);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleHiddenGamesCommand = new RelayCommand(ExecuteToggleHiddenGames);
        ApplySettingsCommand = new RelayCommand(ExecuteApplySettings);
        ResetDefaultsCommand = new RelayCommand(ExecuteResetDefaults);

        AcceptUpdateCommand = new RelayCommand(ExecuteAcceptUpdate);
        DeclineUpdateCommand = new RelayCommand(_ => ShowUpdatePrompt = false);

        CheckForUpdates();
    }

    private async void CheckForUpdates()
    {
        try
        {
            if (_repoClient == null) _repoClient = new GameRepositoryClient(REPO_BASE);

            var checker = new UpdateChecker(_repoClient);
            var info = await checker.CheckForUpdatesAsync();

            if (info != null && info.Version > Program.CurrentVersion)
            {
                PendingUpdateUrl = info.DownloadUrl;
                ShowUpdatePrompt = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check error: {ex.Message}");
        }
    }

    private string? PendingUpdateUrl { get; set; }

    private bool _showUpdatePrompt;
    public bool ShowUpdatePrompt
    {
        get => _showUpdatePrompt;
        set => SetField(ref _showUpdatePrompt, value);
    }

    public ICommand AcceptUpdateCommand { get; }
    public ICommand DeclineUpdateCommand { get; }

    private async void ExecuteAcceptUpdate(object? obj)
    {
        if (string.IsNullOrEmpty(PendingUpdateUrl)) return;

        ShowUpdatePrompt = false;

        try
        {
            if (_repoClient == null) _repoClient = new GameRepositoryClient(REPO_BASE);

            ProgressOverlayMessage = Localization["DownloadingUpdate"];
            IsProgressOverlayVisible = true;

            await UpdateManager.PerformUpdateAsync(PendingUpdateUrl, _repoClient);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update failed: {ex.Message}");
        }
    }

    private bool _isLoadingGames;
    public bool IsLoadingGames
    {
        get => _isLoadingGames;
        set
        {
            if (_isLoadingGames != value)
            {
                _isLoadingGames = value;
                OnPropertyChanged();
            }
        }
    }

    private async Task LoadGamesFromRepositoryAsync()
    {
        try
        {
            IsLoadingGames = true;
            RefreshGamesList();

            var index = await _repoClient.GetGameIndexAsync();

            Debug.WriteLine($"Loaded {index.TotalGames} games from repository");

            var notFoundGames = new List<Game>();

            if (index.Games != null)
            {
                var gamesData = _iniParser.GetSection("Games");
                if (gamesData != null)
                {
                    foreach (var kvp in gamesData)
                    {
                        var gameName = kvp.Key;
                        var installPath = kvp.Value;

                        var gameInfo = index.Games.FirstOrDefault(g => g.GameName == gameName);
                        if (gameInfo != null && !string.IsNullOrEmpty(installPath))
                        {
                            var game = await LoadGame(gameInfo, gameName);
                            _allGames.Add(game);
                        }

                    }
                }

                RefreshGamesList();

                await Task.Run(async () =>
                {
                    foreach (var gameEntry in index.Games)
                    {
                        try
                        {
                            var gameName = gameEntry.GameName ?? "Unknown";

                            var existingGame = _allGames.FirstOrDefault(g => g.Name == gameName);
                            if (existingGame != null)
                            {
                                continue;
                            }

                            Game game = await LoadGame(gameEntry, gameName);

                            if (!string.IsNullOrEmpty(game.InstallPath) && game.SupportedMods.Count > 0)
                            {
                                _allGames.Add(game);

                                // Incremental UI Update
                                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                {
                                    RefreshGamesList();
                                });
                            }
                            else
                            {
                                notFoundGames.Add(game);
                            }

                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error processing game {gameEntry.GameName}: {ex.Message}");
                        }
                    }
                });
            }

            _allGames.AddRange(notFoundGames);

            IsLoadingGames = false;
            RefreshGamesList();
            SaveConfig();

            if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading games from repository: {ex.Message}");

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProgressOverlayMessage = Localization["ConnectionError"];
                IsProgressOverlayError = true;
                IsProgressOverlayVisible = true;
            });

            await Task.Delay(3000);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsProgressOverlayVisible = false;
                IsProgressOverlayError = false;
            });

            RefreshGamesList();
            if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();
        }
    }

    private async Task<Game> LoadGame(GameIndexEntry gameEntry, string gameName)
    {
        List<ModType> mods = new List<ModType>();
        if (!string.IsNullOrEmpty(gameEntry.ShaderMod) && !string.IsNullOrEmpty(gameEntry.MigotoPath)) mods.Add(ModType.ThreeDUltra);
        if (!string.IsNullOrEmpty(gameEntry.ReshadePath)) mods.Add(ModType.ThreeDPlus);

        var game = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => new Game(gameName, "", mods) { Tag = gameEntry });
        if (gameEntry.Creator != null) game.Creator = gameEntry.Creator;
        if (!string.IsNullOrEmpty(gameEntry.DirectoryName))
        {
            var detectedPath = GamePathScanner.FindGameDirectory(
                gameEntry.DirectoryName,
                gameEntry.ExecutablePath,
                gameEntry.RelativeExecutablePath);

            if (!string.IsNullOrEmpty(detectedPath))
            {
                game.InstallPath = detectedPath;
                Debug.WriteLine($"Auto-detected path for {game.Name}: {detectedPath}");
            }
        }

        return game;
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

            string? disableDlss = _iniParser.GetValue("Settings", "DisableDLSS");
            if (bool.TryParse(disableDlss, out bool bDisableDlss)) DisableDLSS = bDisableDlss;

            LoadHotkey("DepthInc", (d, k, m) => { HotkeyDepthInc = d; KeyDepthInc = k; ModDepthInc = m; });
            LoadHotkey("DepthDec", (d, k, m) => { HotkeyDepthDec = d; KeyDepthDec = k; ModDepthDec = m; });
            LoadHotkey("PopoutInc", (d, k, m) => { HotkeyPopoutInc = d; KeyPopoutInc = k; ModPopoutInc = m; });
            LoadHotkey("PopoutDec", (d, k, m) => { HotkeyPopoutDec = d; KeyPopoutDec = k; ModPopoutDec = m; });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading config: {ex.Message}");
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
            _iniParser.SetValue("Settings", "DisableDLSS", DisableDLSS.ToString());

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
            Debug.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    private void ExecuteApplySettings(object? obj)
    {
        IsSettingsOpen = false;
        SaveConfig();
    }

    private List<Game> _allGames;

    private async Task LoadGameIconAsync(Game game)
    {
        if (game == null || _repoClient == null || game.IconImage != null) return;

        try
        {
            if (game.Tag is GameIndexEntry indexEntry && !string.IsNullOrEmpty(indexEntry.Images?.Icon))
            {
                game.IconImage = await LoadImageAsync(indexEntry.Images.Icon);
            }
        }
        catch { }
    }

    private void RefreshGamesList()
    {
        var currentSelection = SelectedGame;
        Games.Clear();

        var filteredGames = _allGames
            .Where(g => (g.SupportedModTypes.Count > 0 || !string.IsNullOrEmpty(g.InstallPath)) &&
                        (ShowUninstalledGames || g.IsInstalled) &&
                        (string.IsNullOrEmpty(SearchQuery) || g.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g.Name);

        foreach (var game in filteredGames)
        {
            Games.Add(game);
            _ = LoadGameIconAsync(game);
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
        IsSettingsOpen = true;
        SelectedLanguage = "en-US";
        InstallModTemporarily = true;
        DisableDLSS = true;
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

    private bool _isGameSessionActive;
    public bool IsGameSessionActive
    {
        get => _isGameSessionActive;
        set => SetField(ref _isGameSessionActive, value);
    }

    public bool ShutdownRequested { get; set; }

    public Action? RequestShutdownAction { get; set; }

    private async void ExecuteStartGame(object? obj)
    {
        if (IsGameSessionActive) return;

        if (SelectedGame == null || string.IsNullOrEmpty(SelectedGame.InstallPath))
        {
            Debug.WriteLine("No game selected or game not installed");
            return;
        }

        if (!(SelectedGame.Tag is GameIndexEntry gameEntry))
        {
            Debug.WriteLine("Game metadata not available");
            return;
        }

        try
        {
            IsGameSessionActive = true;

            if (!string.IsNullOrEmpty(SelectedMod) && _repoClient != null)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProgressOverlayMessage = Localization["DownloadingData"];
                    IsProgressOverlayVisible = true;
                });

                var settings = new ModInstallationSettings
                {
                    Depth = Depth,
                    Popout = Popout,
                    DisableBlacklistedDlls = DisableDLSS,
                    DepthInc = new HotkeyDefinition { Key = KeyDepthInc, Modifiers = ModDepthInc },
                    DepthDec = new HotkeyDefinition { Key = KeyDepthDec, Modifiers = ModDepthDec },
                    PopoutInc = new HotkeyDefinition { Key = KeyPopoutInc, Modifiers = ModPopoutInc },
                    PopoutDec = new HotkeyDefinition { Key = KeyPopoutDec, Modifiers = ModPopoutDec }
                };

                if (ModTypeExtensions.FromDescription(SelectedMod) is ModType modType)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ProgressOverlayMessage = Localization["PreparingData"];
                    });

                    await ModInstaller.InstallModAsync(
                        SelectedGame,
                        modType,
                        _repoClient,
                        settings);
                }
                else
                {
                    Debug.WriteLine($"Unknown mod type selected: {SelectedMod}");
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsProgressOverlayVisible = false;
                });
            }


            SyncTrueGameIni(SelectedGame);


            var exePath = System.IO.Path.Combine(
                SelectedGame.InstallPath,
                gameEntry.RelativeExecutablePath ?? "",
                gameEntry.ExecutablePath ?? "");

            if (!System.IO.File.Exists(exePath))
            {
                Debug.WriteLine($"Executable not found: {exePath}");
                IsGameSessionActive = false;
                return;
            }

            Debug.WriteLine($"Launching: {exePath}");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath),
                UseShellExecute = true
            });

            if (process != null)
            {

                await Task.Run(() => process.WaitForExit());

                var gameDir = SelectedGame.InstallPath;
                var allExes = System.IO.Directory.GetFiles(gameDir!, "*.exe", System.IO.SearchOption.AllDirectories);

                DateTime gameStartTime = DateTime.Now;

                while (true)
                {
                    var runningTime = DateTime.Now - gameStartTime;
                    await Task.Delay(runningTime > TimeSpan.FromMinutes(3) ? 1000 : 10000);

                    // Detect any running process from the game folder
                    var childs = allExes
                        .Select(path => System.IO.Path.GetFileNameWithoutExtension(path))
                        .Where(name => !string.IsNullOrEmpty(name))
                        .SelectMany(name => Process.GetProcessesByName(name!))
                        .ToList();

                    if (!childs.Any())
                        break;

                    await Task.WhenAll(childs.Select(x => x.WaitForExitAsync()));
                }

                if (InstallModTemporarily && !string.IsNullOrEmpty(SelectedMod))
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ProgressOverlayMessage = Localization["RestoringData"];
                        IsProgressOverlayVisible = true;
                    });

                    await ModInstaller.UninstallModAsync(SelectedGame.InstallPath, deleteBackups: false);
                    Debug.WriteLine("Temporary mod uninstalled");

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsProgressOverlayVisible = false;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting game: {ex.Message}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsProgressOverlayVisible = false;
            });
        }
        finally
        {
            IsGameSessionActive = false;

            if (ShutdownRequested)
            {
                Debug.WriteLine("Shutdown was requested during game session. Exiting now.");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RequestShutdownAction?.Invoke();
                });
            }
        }
    }

    public event Func<Task<string?>>? RequestBrowseFolder;

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
        Debug.WriteLine($"Browsing for {SelectedGame?.Name}");
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

    private void LoadGameConfig(Game? game)
    {
        if (game == null || string.IsNullOrEmpty(game.InstallPath)) return;

        try
        {
            string dirPath = game.InstallPath;
            if (game.Tag is GameIndexEntry gameEntry && !string.IsNullOrEmpty(gameEntry.RelativeExecutablePath))
            {
                dirPath = System.IO.Path.Combine(game.InstallPath, gameEntry.RelativeExecutablePath);
            }

            var iniPath = System.IO.Path.Combine(dirPath, "truegame.ini");
            if (System.IO.File.Exists(iniPath))
            {
                var parser = new Services.IniFileParser();
                parser.Load(iniPath);

                var depthStr = parser.GetValue("DEPTH", "Depth");
                if (double.TryParse(depthStr, out double d))
                {
                    Depth = d;
                }

                var popoutStr = parser.GetValue("DEPTH", "Popout");
                if (double.TryParse(popoutStr, out double p))
                {
                    Popout = p;
                }

                Debug.WriteLine($"Loaded config for {game.Name}: Depth={Depth}, Popout={Popout}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading game config: {ex.Message}");
        }
    }

    private void SyncTrueGameIni(Game? game)
    {
        if (game == null || string.IsNullOrEmpty(game.InstallPath)) return;

        try
        {
            string dirPath = game.InstallPath;
            if (game.Tag is GameIndexEntry gameEntry && !string.IsNullOrEmpty(gameEntry.RelativeExecutablePath))
            {
                dirPath = System.IO.Path.Combine(game.InstallPath, gameEntry.RelativeExecutablePath);
            }

            var iniPath = System.IO.Path.Combine(dirPath, "truegame.ini");
            if (!System.IO.File.Exists(iniPath)) return;

            var parser = new Services.IniFileParser();
            parser.Load(iniPath);

            // Helper to format: Code,Alt,Ctrl,Shift
            string FormatKey(Key key, KeyModifiers mods)
            {
                int vk = GetWin32KeyCode(key);
                int alt = mods.HasFlag(KeyModifiers.Alt) ? 1 : 0;
                int ctrl = mods.HasFlag(KeyModifiers.Control) ? 1 : 0;
                int shift = mods.HasFlag(KeyModifiers.Shift) ? 1 : 0;
                return $"{vk},{alt},{ctrl},{shift}";
            }

            parser.SetValue("INPUT", "IncreaseDepth", FormatKey(KeyDepthInc, ModDepthInc));
            parser.SetValue("INPUT", "DecreaseDepth", FormatKey(KeyDepthDec, ModDepthDec));
            parser.SetValue("INPUT", "IncreasePopout", FormatKey(KeyPopoutInc, ModPopoutInc));
            parser.SetValue("INPUT", "DecreasePopout", FormatKey(KeyPopoutDec, ModPopoutDec));


            parser.SetValue("DEPTH", "Depth", ((int)Depth).ToString());
            parser.SetValue("DEPTH", "Popout", ((int)Popout).ToString());

            parser.Save(iniPath);
            Debug.WriteLine("Synced config to truegame.ini");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to sync truegame.ini: {ex.Message}");
        }
    }

    private int GetWin32KeyCode(Key key)
    {
        if (key >= Key.F1 && key <= Key.F24)
            return 112 + (key - Key.F1);


        if (key >= Key.A && key <= Key.Z)
            return 65 + (key - Key.A);


        if (key >= Key.D0 && key <= Key.D9)
            return 48 + (key - Key.D0);


        switch (key)
        {
            case Key.Left: return 37;
            case Key.Up: return 38;
            case Key.Right: return 39;
            case Key.Down: return 40;
            case Key.Insert: return 45;
            case Key.Delete: return 46;
            case Key.Home: return 36;
            case Key.End: return 35;
            case Key.PageUp: return 33;
            case Key.PageDown: return 34;
            case Key.Space: return 32;
            case Key.Enter: return 13;
            case Key.Escape: return 27;
            case Key.Tab: return 9;
            case Key.Back: return 8;
        }


        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return 96 + (key - Key.NumPad0);

        return 0;
    }
}
