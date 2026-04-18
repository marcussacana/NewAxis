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
using System.IO;
using System.Text.Json;

namespace NewAxis.ViewModels;

public class MainViewModel : ViewModelBase
{
    public string UPDATE_REPO_BASE = "https://raw.githubusercontent.com/marcussacana/NewAxis/refs/heads/updater/";
    public LocalizationService Localization => LocalizationService.Instance;

    public string LauncherVersion
    {
        get
        {
            double version = Program.CurrentVersion;
            int wholePart = (int)version;
            int v1 = wholePart / 10;
            int v2 = wholePart % 10;
            int v3 = (int)Math.Round((version - wholePart) * 10);

            if (v3 > 0)
                return $"{v1}.{v2}.{v3}";

            return $"{v1}.{v2}";
        }
    }

    public string WindowTitle => $"NewAxis 3D Manager v{LauncherVersion}";

    private const string DEFAULT_MOD_REPO = "https://raw.githubusercontent.com/marcussacana/NewAxisData/refs/heads/master/";
    private string _appliedRepoUrl = DEFAULT_MOD_REPO;
    private readonly bool _isRepoForcedByArgs;

    private string _modRepoUrl = DEFAULT_MOD_REPO;
    public string MOD_REPO_BASE
    {
        get => _modRepoUrl == DEFAULT_MOD_REPO ? "" : _modRepoUrl;
        set
        {
            var newVal = string.IsNullOrWhiteSpace(value) ? DEFAULT_MOD_REPO : value;
            SetField(ref _modRepoUrl, newVal);
        }
    }

    public ObservableCollection<Game> Games { get; } = new();

    private Game? _selectedGame;
    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {

            if (SetField(ref _selectedGame, value))
            {
                if (_selectedGame != null && _selectedGame.SupportedMods.Count > 0)
                {
                    var list = _selectedGame.SupportedMods;

                    SupportedMods.Clear();
                    foreach (var item in list)
                        SupportedMods.Add(item);

                    SelectedMod = list[0];
                }

                OnPropertyChanged(nameof(SelectedMod));
                OnPropertyChanged(nameof(DisplayedCreator));

                _ = LoadGameBannerAsync(_selectedGame);

                LoadGameConfig(_selectedGame);
            }
        }
    }


    private ObservableCollection<string> _supportedMods = new ObservableCollection<string>();
    public ObservableCollection<string> SupportedMods
    {
        get
        {
            return _supportedMods;
        }
        set
        {
            if (_supportedMods == value) return;

            _supportedMods = value;
            OnPropertyChanged(nameof(SupportedMods));
        }
    }

    private async Task LoadGameBannerAsync(Game? game)
    {
        if (game == null || _repoClient == null) return;

        try
        {
            if (game.Tag is GameIndexEntry indexEntry)
            {
                string? wallpaperUrl = indexEntry.Images?.Wallpaper;
                string? logoUrl = indexEntry.Images?.Logo;

                if (!string.IsNullOrEmpty(wallpaperUrl))
                {
                    Trace.WriteLine($"Downloading banner for {game.Name}");
                    game.BannerImage = await LoadImageAsync(wallpaperUrl);
                }

                // Fallback for Wallpaper (if missing or failed to decode)
                if (game.BannerImage == null && !string.IsNullOrEmpty(indexEntry.SteamAppId))
                {
                    string steamUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{indexEntry.SteamAppId}/library_hero.jpg";
                    if (wallpaperUrl != steamUrl)
                    {
                        Trace.WriteLine($"Banner failed or missing, trying Steam fallback for {game.Name}");
                        game.BannerImage = await LoadImageAsync(steamUrl);
                    }
                }

                if (!string.IsNullOrEmpty(logoUrl))
                {
                    Trace.WriteLine($"Downloading logo for {game.Name}");
                    var logoImg = await LoadImageAsync(logoUrl, autoCropTransparency: true, gameInstance: game);
                    game.LogoImage = logoImg;
                }

                // Fallback for Logo (if missing or failed to decode)
                if (game.LogoImage == null && !string.IsNullOrEmpty(indexEntry.SteamAppId))
                {
                    string steamUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{indexEntry.SteamAppId}/logo.png";
                    if (logoUrl != steamUrl)
                    {
                        Trace.WriteLine($"Logo failed or missing, trying Steam fallback for {game.Name}");
                        game.LogoImage = await LoadImageAsync(steamUrl, autoCropTransparency: true, gameInstance: game);
                    }
                }

                Trace.WriteLine($"Images loaded for {game.Name}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading images: {ex.Message}");
        }
    }

    private async Task<Avalonia.Media.Imaging.Bitmap?> LoadImageAsync(string url, bool autoCropTransparency = false, Game? gameInstance = null)
    {
        try
        {
            var imageBytes = await _repoClient!.DownloadImageAsync(url);

            return await Task.Run(() =>
            {
                Trace.WriteLine($"Loading image for {url}");
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
            Trace.WriteLine($"Error loading image from {url}: {ex.Message}");
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
                    Trace.WriteLine($"Logo cropped: removed {top}px top, {height - bottom - 1}px bottom");
                }

            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error autocropping transparency: {ex.Message}");
        }
    }

    private void AnalyzeImageBrightness(SixLabors.ImageSharp.Image image, Game game)
    {
        try
        {
            var rgba32Image = image as SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
            if (rgba32Image == null)
            {
                Trace.WriteLine("Failed to analyze image brightness: Image is null");
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
                        Trace.WriteLine($"Logo analyzed as DARK (Ratio: {darkRatio:P1}). Shadow set to White.");
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
                        Trace.WriteLine($"Logo analyzed as BRITE (Ratio: {1 - darkRatio:P1}). Shadow set to Black.");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error analyzing brightness: {ex.Message}");
        }
    }

    private string? _selectedMod;
    public string? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetField(ref _selectedMod, value))
            {
                OnPropertyChanged(nameof(DisplayedCreator));
            }
        }
    }

    public string DisplayedCreator
    {
        get
        {
            var resolvedCreator = SelectedGame?.ResolveCreator(SelectedMod);
            if (!string.IsNullOrWhiteSpace(resolvedCreator))
            {
                return resolvedCreator;
            }

            if (ResolveSelectedModType() == ModType.ThreeDPlus)
            {
                return "cybereality";
            }

            if (!string.IsNullOrWhiteSpace(SelectedGame?.Creator))
            {
                return SelectedGame.Creator;
            }

            return Localization["UnknownCreator"];
        }
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
    public ICommand ToggleAboutCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand Open3dViewCommand { get; }
    public ICommand OpenVideoPlayerCommand { get; }

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetField(ref _isSettingsOpen, value);
    }

    private bool _isAboutOpen;
    public bool IsAboutOpen
    {
        get => _isAboutOpen;
        set => SetField(ref _isAboutOpen, value);
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
                _ = RefreshGamesListAsync();
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
                OnPropertyChanged(nameof(DisplayedCreator));
                Trace.WriteLine($"Language changed to {value}");
            }
        }
    }

    private readonly IniFileParser _iniParser = new();
    private const string CONFIG_PATH = "config.ini";
    private GameRepositoryClient? _repoClient;

    public MainViewModel()
    {
        LoadConfig();

        _allGames = new List<Game>();

        // Use command-line argument if provided, otherwise use config
        if (!string.IsNullOrEmpty(Program.CustomRepoPath))
        {
            _isRepoForcedByArgs = true;
            _appliedRepoUrl = Program.CustomRepoPath;
            var fullPath = Path.GetFullPath(_appliedRepoUrl);
            Trace.WriteLine($"[MainViewModel] CWD: {Directory.GetCurrentDirectory()}");
            Trace.WriteLine($"[MainViewModel] Custom Repo Relative: {_appliedRepoUrl}");
            Trace.WriteLine($"[MainViewModel] Custom Repo Absolute: {fullPath}");
            Trace.WriteLine($"[MainViewModel] Repo Dir Exists: {Directory.Exists(fullPath)}");
        }
        else
        {
            _appliedRepoUrl = _modRepoUrl;
        }

        _repoClient = new GameRepositoryClient(_appliedRepoUrl);

        _ = LoadGamesFromRepositoryAsync();

        StartGameCommand = new RelayCommand(ExecuteStartGame);
        BrowseCommand = new RelayCommand(ExecuteBrowse);
        RemoveGameCommand = new RelayCommand(ExecuteRemoveGame);
        SelectModCommand = new RelayCommand(ExecuteSelectMod);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleHiddenGamesCommand = new RelayCommand(ExecuteToggleHiddenGames);
        ApplySettingsCommand = new RelayCommand(ExecuteApplySettings);
        ResetDefaultsCommand = new RelayCommand(ExecuteResetDefaults);
        ToggleAboutCommand = new RelayCommand(_ => IsAboutOpen = !IsAboutOpen);
        OpenUrlCommand = new RelayCommand(url =>
        {
            if (url is string s && !string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(s) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Error opening URL: {ex.Message}");
                }
            }
        });

        Open3dViewCommand = new RelayCommand(_ =>
        {
            ChildToolProcessService.Launch(ChildToolMode.Viewer3D);
        });

        OpenVideoPlayerCommand = new RelayCommand(_ =>
        {
            ChildToolProcessService.Launch(ChildToolMode.VideoPlayer);
        });

        AcceptUpdateCommand = new RelayCommand(ExecuteAcceptUpdate);
        DeclineUpdateCommand = new RelayCommand(_ => ShowUpdatePrompt = false);

        CheckForUpdates(false);
        _ = CheckForRepoUpdatesAsync();
    }

    private async Task CheckForRepoUpdatesAsync()
    {
        try
        {
            if (_repoClient == null) return;

            var onlineDate = await _repoClient.GetOnlineRepoDateAsync();
            var localDate = await _repoClient.GetLocalRepoDateAsync();

            Trace.WriteLine($"Repo Update Check - Online: {onlineDate}, Local: {localDate}");

            if (onlineDate != null && localDate != null && onlineDate > localDate)
            {
                PendingRepoUpdate = true;
                ShowRepoUpdatePrompt = true;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Repo update check error: {ex.Message}");
        }
    }

    private async void CheckForUpdates(bool enforce)
    {
        try
        {
            if (ShowUpdatePrompt && enforce)
            {
                ExecuteAcceptUpdate(null);
                return;
            }

            var _repoClient = new GameRepositoryClient(UPDATE_REPO_BASE);

            var checker = new UpdateChecker(_repoClient);
            var info = await checker.CheckForUpdatesAsync();

            if (info != null && info.Version > Program.CurrentVersion)
            {
                PendingUpdateUrl = info.DownloadUrl;
                ShowUpdatePrompt = true;

                if (enforce)
                {
                    ExecuteAcceptUpdate(null);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Update check error: {ex.Message}");
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

    private bool _showRepoUpdatePrompt;
    public bool ShowRepoUpdatePrompt
    {
        get => _showRepoUpdatePrompt;
        set => SetField(ref _showRepoUpdatePrompt, value);
    }

    private bool PendingRepoUpdate { get; set; }

    public ICommand AcceptRepoUpdateCommand => new RelayCommand(async _ =>
    {
        ShowRepoUpdatePrompt = false;
        if (PendingRepoUpdate)
        {
            await ExecuteRepoUpdate();
        }
    });

    public ICommand DeclineRepoUpdateCommand => new RelayCommand(_ => ShowRepoUpdatePrompt = false);

    public ICommand DownloadOfflineRepoCommand => new RelayCommand(async _ => await ExecuteRepoUpdate());

    private async Task ExecuteRepoUpdate()
    {
        if (_repoClient == null) return;

        try
        {
            IsProgressOverlayVisible = true;
            ProgressOverlayMessage = Localization["UpdatingRepository"];

            var progress = new Progress<string>(msg => ProgressOverlayMessage = msg);
            await _repoClient.DownloadEntireRepoAsync(progress);

            IsProgressOverlayVisible = false;
            PendingRepoUpdate = false;

            // Reload games after repo update
            _ = LoadGamesFromRepositoryAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Repo update failed: {ex.Message}");
            SetLoadingOverlay(true, "Update Failed", true);
        }
    }

    private async void ExecuteAcceptUpdate(object? obj)
    {
        if (string.IsNullOrEmpty(PendingUpdateUrl)) return;

        ShowUpdatePrompt = false;

        try
        {
            if (_repoClient == null) _repoClient = new GameRepositoryClient(UPDATE_REPO_BASE);

            ProgressOverlayMessage = Localization["DownloadingUpdate"];
            IsProgressOverlayVisible = true;

            await UpdateManager.PerformUpdateAsync(PendingUpdateUrl, _repoClient);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Update failed: {ex.Message}");
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
        if (_repoClient == null) return;

        try
        {
            IsLoadingGames = true;
            _allGames.Clear();
            _ = RefreshGamesListAsync();

            var index = await _repoClient.GetGameIndexAsync();
            var communityGame = await LoadCommunityGameAsync();

            Trace.WriteLine($"Loaded {index.TotalGames} games from repository");

            var notFoundGames = new List<Game>();

            var gamesData = _iniParser.GetSection("Games");

            if (index.Games != null)
            {
                if (gamesData != null)
                {
                    foreach (var kvp in gamesData)
                    {
                        var gameName = kvp.Key;
                        var installPath = kvp.Value;

                        var gameInfo = index.Games.FirstOrDefault(g => g.GameName == gameName);
                        if (gameInfo != null && !string.IsNullOrEmpty(installPath))
                        {
                            var game = await LoadGame(gameInfo, gameName, installPath);
                            _allGames.Add(game);
                        }

                    }
                }

                _ = RefreshGamesListAsync();

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
                                _ = RefreshGamesListAsync();
                            }
                            else
                            {
                                notFoundGames.Add(game);
                            }

                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"Error processing game {gameEntry.GameName}: {ex.Message}");
                        }
                    }
                });
            }

            if (communityGame != null && _allGames.All(g => !string.Equals(g.Name, communityGame.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (gamesData != null && gamesData.TryGetValue(communityGame.Name, out var savedInstallPath) && !string.IsNullOrWhiteSpace(savedInstallPath))
                {
                    communityGame.InstallPath = savedInstallPath;
                }

                _allGames.Add(communityGame);
            }

            _allGames.AddRange(notFoundGames);

            await RefreshGamesListAsync();
            IsLoadingGames = false;
            SaveConfig();

            if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            Trace.WriteLine("LoadGamesFromRepositoryAsync canceled.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading games from repository: {ex.Message}");

            SetLoadingOverlay(true, Localization["ConnectionError"], true);

            await Task.Delay(3000);

            CheckForUpdates(true);

            SetLoadingOverlay(false, null, false);

            _ = RefreshGamesListAsync();
            if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();
        }
        finally
        {
            IsLoadingGames = false;
        }
    }

    private async Task<Game> LoadGame(GameIndexEntry gameEntry, string gameName, string hintPath = null)
    {
        List<ModType> mods = new List<ModType>();
        if (!string.IsNullOrEmpty(gameEntry.ShaderMod) && !string.IsNullOrEmpty(gameEntry.MigotoPath)) mods.Add(ModType.ThreeDUltra);
        if (!string.IsNullOrEmpty(gameEntry.ReshadePath)) mods.Add(ModType.ThreeDPlus);
        if (!string.IsNullOrEmpty(gameEntry.NativeReshade)) mods.Add(ModType.Native);
        if (!string.IsNullOrEmpty(gameEntry.CommunityModPath)) mods.Add(ModType.Native);

        var game = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => new Game(gameName, "", mods) { Tag = gameEntry });
        if (gameEntry.Creator != null) game.Creator = gameEntry.Creator;
        game.SupportedModsMap = BuildSupportedModsMap(gameEntry, mods);
        game.ModCreditsMap = BuildModCreditsMap(gameEntry, gameEntry.Creator);
        if (!string.IsNullOrEmpty(gameEntry.DirectoryName))
        {
            var detectedPath = GamePathScanner.FindGameDirectory(gameEntry, hintPath);

            if (!string.IsNullOrEmpty(detectedPath))
            {
                game.InstallPath = detectedPath;
                Trace.WriteLine($"Auto-detected path for {game.Name}: {detectedPath}");
            }
        }

        return game;
    }

    private Dictionary<string, ModType> BuildSupportedModsMap(GameIndexEntry gameEntry, List<ModType> mods)
    {
        var map = new Dictionary<string, ModType>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Distinct())
        {
            string label = mod.GetDescription();
            if (mod == ModType.Native && !string.IsNullOrWhiteSpace(gameEntry.CommunityModType))
            {
                label = gameEntry.CommunityModType!;
            }

            map[label] = mod;
        }

        return map;
    }

    private Dictionary<string, string> BuildModCreditsMap(GameIndexEntry gameEntry, string? defaultCreator)
    {
        var credits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(gameEntry.CommunityModType) && !string.IsNullOrWhiteSpace(gameEntry.CommunityCredit))
        {
            credits[gameEntry.CommunityModType!] = gameEntry.CommunityCredit!;
        }

        if (!string.IsNullOrWhiteSpace(defaultCreator))
        {
            credits["3D Ultra"] = defaultCreator!;
            credits["Native"] = defaultCreator!;
        }

        credits["Rendepth"] = "cybereality";
        credits["3D+"] = "cybereality";
        return credits;
    }

    private async Task<Game?> LoadCommunityGameAsync()
    {
        if (_repoClient == null)
        {
            return null;
        }

        string communityPath = Path.Combine(_repoClient.REPO_BASE, "community.json");
        if (!File.Exists(communityPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(communityPath);
            var manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.CommunityModManifest);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.ModPath))
            {
                return null;
            }

            string normalizedModPath = manifest.ModPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string? communityDir = Path.GetDirectoryName(normalizedModPath);
            string inferredGameName = !string.IsNullOrWhiteSpace(communityDir)
                ? Path.GetFileName(communityDir)
                : Path.GetFileNameWithoutExtension(normalizedModPath);
            string gameName = string.IsNullOrWhiteSpace(manifest.GameName) ? inferredGameName : manifest.GameName!;
            var entry = new GameIndexEntry
            {
                GameName = gameName,
                DirectoryName = gameName,
                SteamAppId = manifest.SteamAppId,
                ExecutablePath = manifest.ExecutablePath ?? string.Empty,
                RelativeExecutablePath = manifest.RelativeExecutablePath ?? string.Empty,
                CommunityModPath = manifest.ModPath,
                CommunityReshadeEntryPoint = manifest.ReshadeEntryPoint,
                CommunityModType = string.IsNullOrWhiteSpace(manifest.ModType) ? "Community" : manifest.ModType,
                CommunityCredit = manifest.Credit,
                Images = new ImageUrls
                {
                    Logo = !string.IsNullOrWhiteSpace(communityDir) ? $"{communityDir}/images/logo.png" : null,
                    Wallpaper = !string.IsNullOrWhiteSpace(communityDir) ? $"{communityDir}/images/wallpaper.jpg" : null,
                    Icon = !string.IsNullOrWhiteSpace(communityDir) ? $"{communityDir}/images/icon.jpg" : null
                }
            };

            return await LoadGame(entry, gameName);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading community.json: {ex.Message}");
            return null;
        }
    }

    private ModType? ResolveSelectedModType()
    {
        return SelectedGame?.ResolveModType(SelectedMod) ?? ModTypeExtensions.FromDescription(SelectedMod);
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

            string? repoOverride = _iniParser.GetValue("Settings", "RepoOverride");
            if (!string.IsNullOrEmpty(repoOverride)) _modRepoUrl = repoOverride;
            else _modRepoUrl = DEFAULT_MOD_REPO;

            LoadHotkey("DepthInc", (d, k, m) => { HotkeyDepthInc = d; KeyDepthInc = k; ModDepthInc = m; });
            LoadHotkey("DepthDec", (d, k, m) => { HotkeyDepthDec = d; KeyDepthDec = k; ModDepthDec = m; });
            LoadHotkey("PopoutInc", (d, k, m) => { HotkeyPopoutInc = d; KeyPopoutInc = k; ModPopoutInc = m; });
            LoadHotkey("PopoutDec", (d, k, m) => { HotkeyPopoutDec = d; KeyPopoutDec = k; ModPopoutDec = m; });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading config: {ex.Message}");
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
            _iniParser.SetValue("Settings", "RepoOverride", MOD_REPO_BASE);

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
            Trace.WriteLine($"Error saving config: {ex.Message}");
        }
    }

    private void ExecuteApplySettings(object? obj)
    {
        IsSettingsOpen = false;

        string desiredRepoUrl = _isRepoForcedByArgs && !string.IsNullOrWhiteSpace(Program.CustomRepoPath)
            ? Program.CustomRepoPath
            : _modRepoUrl;

        bool repoChanged = _appliedRepoUrl != desiredRepoUrl;
        if (repoChanged)
        {
            _appliedRepoUrl = desiredRepoUrl;
            _repoClient = new GameRepositoryClient(_appliedRepoUrl);
            _ = LoadGamesFromRepositoryAsync();
        }

        SaveConfig();
    }

    private List<Game> _allGames;
    private System.Threading.CancellationTokenSource? _refreshCts;

    private async Task LoadGameIconAsync(Game game)
    {
        if (game == null || _repoClient == null || game.IconImage != null) return;

        try
        {
            if (game.Tag is GameIndexEntry indexEntry)
            {
                string? iconUrl = indexEntry.Images?.Icon;

                if (!string.IsNullOrEmpty(iconUrl))
                {
                    game.IconImage = await LoadImageAsync(iconUrl);
                }

                // Fallback for Icon (if missing or failed to decode)
                if (game.IconImage == null && !string.IsNullOrEmpty(indexEntry.SteamAppId))
                {
                    string steamUrl = $"https://steamcdn-a.akamaihd.net/steam/apps/{indexEntry.SteamAppId}/library_600x900_2x.jpg";
                    if (iconUrl != steamUrl)
                    {
                        Trace.WriteLine($"Icon failed or missing, trying Steam fallback for {game.Name}");
                        game.IconImage = await LoadImageAsync(steamUrl);
                    }
                }
            }
        }
        catch { }
    }

    private async Task RefreshGamesListAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts = new System.Threading.CancellationTokenSource();
        var token = _refreshCts.Token;

        try
        {
            var currentSelection = SelectedGame;

            var filteredGames = await Task.Run(() =>
            {
                return _allGames
                    .Where(g => (g.SupportedModTypes.Count > 0 || !string.IsNullOrEmpty(g.InstallPath)) &&
                                (ShowUninstalledGames || g.IsInstalled) &&
                                (string.IsNullOrEmpty(SearchQuery) || g.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(g => g.Name)
                    .ToList();
            }, token);

            if (token.IsCancellationRequested) return;

            Games.Clear();

            // Determine batch size based on whether icons are already loaded
            // If most games have icons cached, we can use a larger batch size
            int iconsLoaded = filteredGames.Count(g => g.IconImage != null);
            bool mostIconsCached = filteredGames.Count > 0 && ((double)iconsLoaded / filteredGames.Count) > 0.5;
            int batchSize = mostIconsCached ? 10 : 2;

            for (int i = 0; i < filteredGames.Count; i++)
            {
                if (token.IsCancellationRequested) return;

                Games.Add(filteredGames[i]);
                _ = LoadGameIconAsync(filteredGames[i]);

                if ((i + 1) % batchSize == 0)
                {
                    await Task.Delay(100, token); // Yield to UI thread
                }
            }

            if (currentSelection != null && Games.Contains(currentSelection))
            {
                SelectedGame = currentSelection;
            }
            else if (Games.Count > 0)
            {
                SelectedGame = Games.FirstOrDefault();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer refresh supersedes the current one.
        }
    }

    private void ExecuteToggleHiddenGames(object? obj)
    {
        ShowUninstalledGames = !ShowUninstalledGames;
        _ = RefreshGamesListAsync();
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
            Trace.WriteLine("No game selected or game not installed");
            return;
        }

        if (!(SelectedGame.Tag is GameIndexEntry gameEntry))
        {
            Trace.WriteLine("Game metadata not available");
            return;
        }

        ModType? modType = ResolveSelectedModType();
        var preparingProgress = CreateOverlayProgressReporter(Localization["PreparingData"]);
        IReadOnlySet<string>? pendingInstallFiles = null;

        try
        {
            if (modType != null && _repoClient != null && !string.IsNullOrEmpty(SelectedMod))
            {
                pendingInstallFiles = await ModInstaller.GetPendingInstallFilesAsync(SelectedGame, modType.Value, _repoClient);
            }

            SetLoadingOverlay(true, Localization["PreparingData"]);
            await ModInstaller.UninstallModAsync(SelectedGame.InstallPath, deleteBackups: false, preparingProgress, pendingInstallFiles);

            if (!string.IsNullOrEmpty(SelectedMod) && _repoClient != null)
            {
                SetLoadingOverlay(true, Localization["DownloadingData"]);

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

                if (modType != null)
                {
                    SetLoadingOverlay(true, Localization["PreparingData"]);

                    await ModInstaller.InstallModAsync(
                        SelectedGame,
                        modType!.Value,
                        _repoClient,
                        settings,
                        preparingProgress);
                }
                else
                {
                    Trace.WriteLine($"Unknown mod type selected: {SelectedMod}");
                }
            }

            SyncTrueGameIni(SelectedGame);

            var exePath = ResolveLaunchExecutablePath(SelectedGame.InstallPath, gameEntry);

            var acfPath = Path.Combine(Path.GetFullPath($"..\\..\\appmanifest_{gameEntry.SteamAppId}.acf", SelectedGame.InstallPath));

            bool isSteamGame = File.Exists(acfPath);

            Trace.WriteLine($"Launching: {exePath}, IsSteamGame: {isSteamGame}");

            string? launchArgs = modType switch
            {
                ModType.ThreeDUltra => gameEntry.StartArgsUltra,
                ModType.ThreeDPlus => gameEntry.StartArgsPlus,
                ModType.Native => gameEntry.StartArgsNative,
                _ => null
            };

            if (!string.IsNullOrEmpty(launchArgs))
            {
                Trace.WriteLine($"Using launch arguments: {launchArgs}");
            }

            Process? process;
            if (isSteamGame)
            {
                var steamUrl = "steam://run/" + gameEntry.SteamAppId;

                if (!string.IsNullOrEmpty(launchArgs))
                {
                    steamUrl += "//" + launchArgs;
                }

                Trace.WriteLine($"Launching Steam game: {steamUrl}");

                IsGameSessionActive = true;

                process = Process.Start(new ProcessStartInfo
                {
                    FileName = steamUrl,
                    UseShellExecute = true
                });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    Trace.WriteLine($"Executable not found: {exePath}");
                    IsGameSessionActive = false;
                    return;
                }

                Trace.WriteLine($"Starting game: {exePath}");

                IsGameSessionActive = true;

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = true
                };

                if (!string.IsNullOrEmpty(launchArgs))
                {
                    startInfo.Arguments = launchArgs;
                }

                process = Process.Start(startInfo);
            }

            if (process != null)
            {
                if (!isSteamGame)
                {
                    SetLoadingOverlay(false);
                }

                await process.WaitForExitAsync();

                if (isSteamGame)
                {
                    SetLoadingOverlay(false);
                }

                await WaitGameExit();

                if (InstallModTemporarily && !string.IsNullOrEmpty(SelectedMod))
                {
                    SetLoadingOverlay(true, Localization["RestoringData"]);
                    var restoringProgress = CreateOverlayProgressReporter(Localization["RestoringData"]);
                    await ModInstaller.UninstallModAsync(SelectedGame.InstallPath, deleteBackups: false, restoringProgress);
                    Trace.WriteLine("Temporary mod uninstalled");

                    SetLoadingOverlay(false);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error starting game: {ex.Message}");
            SetLoadingOverlay(false);
        }
        finally
        {
            Trace.WriteLine("Game session ended");
            IsGameSessionActive = false;

            if (ShutdownRequested)
            {
                Trace.WriteLine("Shutdown was requested during game session. Exiting now.");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RequestShutdownAction?.Invoke();
                });
            }
        }
    }

    private void SetLoadingOverlay(bool overlayVisible, string Status = null, bool isError = false)
    {
        Trace.WriteLine($"SetLoadingOverlay: {overlayVisible}, {(Status ?? "NULL")}");
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Status != null)
            {
                ProgressOverlayMessage = Status;
            }

            if (!overlayVisible)
            {
                await Task.Delay(3000);
            }

            IsProgressOverlayVisible = overlayVisible;
            IsProgressOverlayError = isError;
        });
    }

    private IProgress<ModProgressInfo> CreateOverlayProgressReporter(string baseStatus)
    {
        return new Progress<ModProgressInfo>(update =>
        {
            ProgressOverlayMessage = FormatOverlayProgressMessage(baseStatus, update);
        });
    }

    private static string FormatOverlayProgressMessage(string baseStatus, ModProgressInfo? update)
    {
        if (update == null)
        {
            return baseStatus;
        }

        if (update.Total.HasValue && update.Total.Value > 0)
        {
            int percent = (int)Math.Clamp(Math.Round((double)update.Processed * 100 / update.Total.Value), 0, 100);
            return $"{baseStatus} ({percent}%)";
        }

        return baseStatus;
    }

    private async Task WaitGameExit()
    {
        var gameDir = SelectedGame!.InstallPath;
        if (string.IsNullOrEmpty(gameDir)) return;

        var allExes = Directory.GetFiles(gameDir, "*.exe", SearchOption.AllDirectories);
        DateTime gameStartTime = DateTime.Now;

        while (true)
        {
            var runningTime = (DateTime.Now - gameStartTime).TotalMinutes;
            await Task.Delay(runningTime > 1 ? 1000 : 10000);

            // Detect any running process from the game folder
            var processes = allExes
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => !name.Contains("WebHelper", StringComparison.OrdinalIgnoreCase))
                .Where(name => !name.Contains("CrashReport", StringComparison.OrdinalIgnoreCase))
                .SelectMany(name => Process.GetProcessesByName(name!))
                .ToList();

            if (processes.Count == 0)
                break;


            await Task.WhenAll(processes.Select(x => x.WaitForExitAsync()));
        }

        Trace.WriteLine("Game exited");
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
                _ = RefreshGamesListAsync();
                SaveConfig();
            }
        }
        Trace.WriteLine($"Browsing for {SelectedGame?.Name}");
    }

    private void ExecuteRemoveGame(object? obj)
    {
        if (SelectedGame != null)
        {
            SelectedGame.InstallPath = string.Empty;
            _ = RefreshGamesListAsync();
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

    private string? ResolveLaunchExecutablePath(string installPath, GameIndexEntry gameEntry)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(gameEntry.ExecutablePath))
        {
            candidates.Add(Path.Combine(
                installPath,
                gameEntry.RelativeExecutablePath ?? string.Empty,
                gameEntry.ExecutablePath));
            candidates.Add(Path.Combine(installPath, gameEntry.ExecutablePath));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
            .Where(x => !Path.GetFileName(x).Contains("launch", StringComparison.InvariantCultureIgnoreCase))
            .Where(x => !Path.GetFileName(x).Contains("web", StringComparison.InvariantCultureIgnoreCase))
            .Where(x => !Path.GetFileName(x).Contains("crash", StringComparison.InvariantCultureIgnoreCase))
            .Where(x => !Path.GetFileName(x).Contains("install", StringComparison.InvariantCultureIgnoreCase))
            .Where(x => !Path.GetFileName(x).Contains("editor", StringComparison.InvariantCultureIgnoreCase))
            .Where(x => !Path.GetFileName(x).Contains("physx", StringComparison.InvariantCultureIgnoreCase))
            .Where(x => !Path.GetFileName(x).Contains("ENU", StringComparison.InvariantCultureIgnoreCase))
            .OrderByDescending(x => new FileInfo(x).Length)
            .FirstOrDefault();
    }

    private void LoadGameConfig(Game? game)
    {
        if (game == null || string.IsNullOrEmpty(game.InstallPath)) return;

        try
        {
            string dirPath = game.InstallPath;
            if (game.Tag is GameIndexEntry gameEntry && !string.IsNullOrEmpty(gameEntry.RelativeExecutablePath))
            {
                dirPath = Path.Combine(game.InstallPath, gameEntry.RelativeExecutablePath);
            }

            var iniPath = Path.Combine(dirPath, "truegame.ini");
            if (File.Exists(iniPath))
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

                Trace.WriteLine($"Loaded config for {game.Name}: Depth={Depth}, Popout={Popout}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading game config: {ex.Message}");
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
                dirPath = Path.Combine(game.InstallPath, gameEntry.RelativeExecutablePath);
            }

            var iniPath = Path.Combine(dirPath, "truegame.ini");

            if (!File.Exists(iniPath))
            {
                Trace.WriteLine($"truegame.ini not found for {game.Name}");
                return;
            }

            var parser = new IniFileParser();
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
            Trace.WriteLine("Synced config to truegame.ini");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to sync truegame.ini: {ex.Message}");
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

    public string RendepthLicense => @"MIT License

Copyright (c) 2025 Outmode

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";

    public string ReshadeLicense => @"Copyright (c) 2014, Patrick Mours
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS ""AS IS""
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.";

    public string MigotoLicenseHeader => "The source code of 3Dmigoto is released under the GPLv3 license - refer to ";
    public string MigotoLicenseLink => "https://github.com/bo3b/3Dmigoto/blob/master/LICENSE.GPL.txt";
    public string MigotoLicenseFooter => @" for details.

Any shaders distributed along with 3Dmigoto as part of game fixes are not
covered by the license of 3Dmigoto, and are owned by their respective copyright
holders - these are modified and distributed in good faith for the sole purpose
of fixing problems in the original games.

3Dmigoto makes use of the Deviare-InProcess library from Nektra, which is
licensed under the General Public License version 3 - refer to LICENSE.GPL.txt
for details.";
}
