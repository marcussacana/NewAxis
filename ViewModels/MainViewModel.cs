using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Input;
using NewAxis.Models;
using NewAxis.Services;
using System;
using Avalonia.Controls;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Advanced;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia.Controls.Shapes;

namespace NewAxis.ViewModels;

public class MainViewModel : ViewModelBase
{
    public const string REPO_BASE = null;
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

                // Carrega banner automaticamente
                _ = LoadGameBannerAsync(_selectedGame);

                // Carrega config do jogo (truegame.ini)
                LoadGameConfig(_selectedGame);
            }
        }
    }

    private async System.Threading.Tasks.Task LoadGameBannerAsync(Game? game)
    {
        if (game == null || _repoClient == null) return;

        try
        {
            // Pega o GameIndexEntry do Tag
            if (game.Tag is GameIndexEntry indexEntry && indexEntry.Images != null)
            {
                // Carrega Banner (Wallpaper)
                if (!string.IsNullOrEmpty(indexEntry.Images.Wallpaper))
                {
                    System.Diagnostics.Debug.WriteLine($"Downloading banner for {game.Name}");
                    game.BannerImage = await LoadImageAsync(indexEntry.Images.Wallpaper);
                }

                // Carrega Logo
                if (!string.IsNullOrEmpty(indexEntry.Images.Logo))
                {
                    System.Diagnostics.Debug.WriteLine($"Downloading logo for {game.Name}");
                    var logoImg = await LoadImageAsync(indexEntry.Images.Logo, autoCropTransparency: true, gameInstance: game);
                    game.LogoImage = logoImg;
                }

                // Icon is now eager-loaded in RefreshGamesList via LoadGameIconAsync

                System.Diagnostics.Debug.WriteLine($"Images loaded for {game.Name}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading images: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task<Avalonia.Media.Imaging.Bitmap?> LoadImageAsync(string url, bool autoCropTransparency = false, Game? gameInstance = null)
    {
        try
        {
            // Baixa a imagem
            var imageBytes = await _repoClient!.DownloadImageAsync(url);

            // Offload CPU-intensive image operations to a background thread
            return await System.Threading.Tasks.Task.Run(() =>
            {
                using (var inputStream = new System.IO.MemoryStream(imageBytes))
                using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(inputStream))
                {
                    // Autocrop transparência se solicitado (para logos)
                    if (autoCropTransparency)
                    {
                        AutoCropTransparency(image);
                    }

                    // Analyze brightness for dynamic shadow if it's a logo (gameInstance provided)
                    if (gameInstance != null)
                    {
                        AnalyzeImageBrightness(image, gameInstance);
                    }

                    using (var outputStream = new System.IO.MemoryStream())
                    {
                        // Converte para PNG em memória
                        image.Save(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                        outputStream.Position = 0;

                        // Cria Bitmap do Avalonia diretamente da memória
                        // Note: Avalonia Bitmaps must presumably be created on UI thread usually, 
                        // but creating from stream is thread-safe as long as it's not attached to visual tree yet.
                        return new Avalonia.Media.Imaging.Bitmap(outputStream);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading image from {url}: {ex.Message}");
            return null;
        }
    }

    private void AutoCropTransparency(SixLabors.ImageSharp.Image image)
    {
        try
        {
            // Converte para Image<Rgba32> para acessar pixels
            var rgba32Image = image as SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
            if (rgba32Image == null) return;

            int width = rgba32Image.Width;
            int height = rgba32Image.Height;

            // Encontra primeira linha não transparente (top)
            int top = 0;
            for (int y = 0; y < height; y++)
            {
                bool hasOpaque = false;
                for (int x = 0; x < width; x++)
                {
                    if (rgba32Image[x, y].A > 10) // threshold de alpha
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

            // Encontra última linha não transparente (bottom)
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

            // Faz crop se encontrou bordas transparentes
            if (top > 0 || bottom < height - 1)
            {
                int newHeight = bottom - top + 1;
                if (newHeight > 0)
                {
                    var cropRect = new SixLabors.ImageSharp.Rectangle(0, top, width, newHeight);
                    rgba32Image.Mutate(x => x.Crop(cropRect));
                    System.Diagnostics.Debug.WriteLine($"Logo cropped: removed {top}px top, {height - bottom - 1}px bottom");
                }

            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error autocropping transparency: {ex.Message}");
        }
    }

    private void AnalyzeImageBrightness(SixLabors.ImageSharp.Image image, Game game)
    {
        try
        {
            var rgba32Image = image as SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
            if (rgba32Image == null)
            {
                System.Diagnostics.Debug.WriteLine("Failed to analyze image brightness: Image is null");
                return;
            }

            // Use ProcessPixelRows for efficient access to pixel data
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
                        if (pixel.A > 40) // Ignore transparent pixels
                        {
                            // Calculate relative luminance
                            float lum = 0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B;

                            // Threshold 110: Below this consider "dark"
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
                    // If more than 70% of opaque pixels are dark, consider it a dark logo
                    // Increased from 50% to 70% to avoid false positives on metallic logos like Skyrim (which was ~56% dark)
                    if (darkRatio > 0.7)
                    {
                        // Update the entire effect object to trigger notification
                        // Must be dispatched to UI thread since we are in Task.Run
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
                        System.Diagnostics.Debug.WriteLine($"Logo analyzed as DARK (Ratio: {darkRatio:P1}). Shadow set to White.");
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
                        System.Diagnostics.Debug.WriteLine($"Logo analyzed as BRITE (Ratio: {1 - darkRatio:P1}). Shadow set to Black.");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error analyzing brightness: {ex.Message}");
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

        LoadConfig();

        // Carrega jogos async
        _ = LoadGamesFromRepositoryAsync();

        StartGameCommand = new RelayCommand(ExecuteStartGame);
        BrowseCommand = new RelayCommand(ExecuteBrowse);
        RemoveGameCommand = new RelayCommand(ExecuteRemoveGame);
        SelectModCommand = new RelayCommand(ExecuteSelectMod);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleHiddenGamesCommand = new RelayCommand(ExecuteToggleHiddenGames);
        ApplySettingsCommand = new RelayCommand(ExecuteApplySettings);
        ResetDefaultsCommand = new RelayCommand(ExecuteResetDefaults);
    }

    private async Task LoadGamesFromRepositoryAsync()
    {
        try
        {
            _repoClient = new GameRepositoryClient(REPO_BASE);
            var index = await _repoClient.GetGameIndexAsync();

            Debug.WriteLine($"Loaded {index.TotalGames} games from repository");

            _allGames.Clear();

            if (index.Games != null)
            {
                foreach (var gameEntry in index.Games)
                {
                    var mods = new List<string>();

                    // Detect 3D Ultra mod (requires both ShaderMod and MigotoPath)
                    if (!string.IsNullOrEmpty(gameEntry.ShaderMod) && !string.IsNullOrEmpty(gameEntry.MigotoPath))
                    {
                        mods.Add("3D Ultra");
                    }

                    // Detect 3D+ mod (requires ReshadePath)
                    if (!string.IsNullOrEmpty(gameEntry.ReshadePath))
                    {
                        mods.Add("3D+");
                    }

                    var game = new Game(
                        gameEntry.GameName ?? "Unknown",
                        "", // InstallPath será carregado do config.ini
                        mods
                    )
                    {
                        // Armazena metadados adicionais
                        Tag = gameEntry // Podemos acessar depois para baixar assets
                    };

                    // Check if game already exists in _allGames
                    bool isNew = !_allGames.Any(g => g.Name == game.Name);

                    if (isNew)
                    {
                        if (gameEntry.Creator != null)
                        {
                            game.Creator = gameEntry.Creator;
                        }

                        // Try to auto-detect install path if not loaded from config (since it's new)
                        // Or scanned right now
                        if (string.IsNullOrEmpty(game.InstallPath) && !string.IsNullOrEmpty(gameEntry.DirectoryName))
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

                        _allGames.Add(game);
                    }
                }
            }

            RefreshGamesList();

            if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading games from repository: {ex.Message}");
            RefreshGamesList();
            if (SelectedGame == null) SelectedGame = Games.FirstOrDefault();
        }
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

    private async System.Threading.Tasks.Task LoadGameIconAsync(Game game)
    {
        if (game == null || _repoClient == null || game.IconImage != null) return;

        try
        {
            if (game.Tag is GameIndexEntry indexEntry && !string.IsNullOrEmpty(indexEntry.Images?.Icon))
            {
                // System.Diagnostics.Debug.WriteLine($"Downloading icon for {game.Name}");
                game.IconImage = await LoadImageAsync(indexEntry.Images.Icon);
            }
        }
        catch { }
    }

    private void RefreshGamesList()
    {
        var currentSelection = SelectedGame;
        Games.Clear();
        foreach (var game in _allGames)
        {
            if (ShowUninstalledGames || game.IsInstalled)
            {
                Games.Add(game);
                // Eager load icons (fire and forget)
                _ = LoadGameIconAsync(game);
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

    private async void ExecuteStartGame(object? obj)
    {
        if (SelectedGame == null || string.IsNullOrEmpty(SelectedGame.InstallPath))
        {
            System.Diagnostics.Debug.WriteLine("No game selected or game not installed");
            return;
        }

        if (!(SelectedGame.Tag is GameIndexEntry gameEntry))
        {
            System.Diagnostics.Debug.WriteLine("Game metadata not available");
            return;
        }

        try
        {
            // Install mod if one is selected
            if (!string.IsNullOrEmpty(SelectedMod) && _repoClient != null)
            {
                await ModInstaller.InstallModAsync(
                    SelectedGame.InstallPath,
                    gameEntry.ExecutablePath ?? "",
                    gameEntry.RelativeExecutablePath ?? "",
                    SelectedMod,
                    _repoClient,
                    gameEntry,
                    Depth,
                    Popout);
            }

            // Sync truegame.ini configuration
            SyncTrueGameIni(SelectedGame);

            // Launch game
            var exePath = System.IO.Path.Combine(
                SelectedGame.InstallPath,
                gameEntry.RelativeExecutablePath ?? "",
                gameEntry.ExecutablePath ?? "");

            if (!System.IO.File.Exists(exePath))
            {
                Debug.WriteLine($"Executable not found: {exePath}");
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
                // Wait for game to exit
                await Task.Run(() => process.WaitForExit());

                await Task.Delay(6000);

                var childs = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(exePath));

                await Task.WhenAll(childs.Select(x => x.WaitForExitAsync()));

                // Uninstall mod if temporary installation is enabled
                if (InstallModTemporarily && !string.IsNullOrEmpty(SelectedMod))
                {
                    await ModInstaller.UninstallModAsync(SelectedGame.InstallPath, deleteBackups: false);
                    Debug.WriteLine("Temporary mod uninstalled");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting game: {ex.Message}");
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

            // Sync current slider values too
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
        // Simple mapping for common keys used in game mods
        // Function keys
        if (key >= Key.F1 && key <= Key.F24)
            return 112 + (key - Key.F1);

        // Standard keys A-Z
        if (key >= Key.A && key <= Key.Z)
            return 65 + (key - Key.A);

        // Numbers 0-9
        if (key >= Key.D0 && key <= Key.D9)
            return 48 + (key - Key.D0);

        // Arrow keys
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

        // Fallback for Numpad
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return 96 + (key - Key.NumPad0);

        // If unknown, return 0 or maybe try to cast if values align (they often don't exactly match Win32 VK)
        return 0;
    }
}
