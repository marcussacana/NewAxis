using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia;
using NewAxis.Controls;
using NewAxis.Services;
using NewAxis.ViewModels;
using System.Diagnostics;
using System.ComponentModel;
using System;
using System.Threading.Tasks;

namespace NewAxis;

public partial class VideoPlayerWindow : Window
{
    private string? _startupFilePath;
    private PlayerDependencyService.DependencyCheckResult _dependencyCheckResult = new(true, "OK");
    private bool _dependencyCheckStarted;
    private bool _dependencyCheckCompleted;
    private bool _playerControlAttached;
    private Point _lastPointerPosition;
    private const double PointerMoveThreshold = 5.0;

    public VideoPlayerWindow()
        : this(null)
    {
    }

    public VideoPlayerWindow(string? startupFilePath)
    {
        _startupFilePath = startupFilePath;
        DataContext = new VideoPlayerViewModel();
        InitializeComponent();
        ApplyLocalizedTexts();
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        Closed += (_, _) => LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;

        if (!string.IsNullOrWhiteSpace(_startupFilePath))
        {
            Trace.Write("VideoPlayerWindow", $"Startup file requested: {_startupFilePath}");
        }

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, true);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel, true);

        Opened += OnWindowOpened;
        DataContextChanged += (_, _) =>
        {
            _playerControlAttached = false;
            WireUpViewModel();
        };
        WireUpViewModel();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationService.CurrentLanguage) || e.PropertyName == "Item[]" || e.PropertyName == "Item")
        {
            ApplyLocalizedTexts();
        }
    }

    private void ApplyLocalizedTexts()
    {
        Title = LocalizationService.Instance["VideoPlayerWindowTitle"];
    }

    private void WireUpViewModel()
    {
        var player = this.FindControl<VideoPlayerControl>("VideoPlayer");
        if (DataContext is not VideoPlayerViewModel vm || player == null)
        {
            return;
        }

        vm.SetDependencyLoading(!_dependencyCheckCompleted);

        vm.RequestOpenFile = async () =>
        {
            if (!StorageProvider.CanOpen)
            {
                return null;
            }

            var loc = LocalizationService.Instance;

            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = loc["SelectVideoFile"],
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["VideoFiles"])
                    {
                        Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.mov", "*.m2ts", "*.mpg", "*.mpeg", "*.wmv", "*.mpls", "*.bdmv" }
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["AllFiles"])
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

        vm.OnRequestFullscreenToggle -= Vm_OnRequestFullscreenToggle;
        vm.OnRequestFullscreenToggle += Vm_OnRequestFullscreenToggle;
        vm.OnRequestFullscreenMode -= Vm_OnRequestFullscreenMode;
        vm.OnRequestFullscreenMode += Vm_OnRequestFullscreenMode;

        vm.PropertyChanged -= Vm_PropertyChanged;
        vm.PropertyChanged += Vm_PropertyChanged;

        if (!_dependencyCheckCompleted)
        {
            return;
        }

        vm.SetDependencyStatus(_dependencyCheckResult);

        if (!_dependencyCheckResult.Success)
        {
            return;
        }

        if (!_playerControlAttached)
        {
            vm.SetPlayerControl(player);
            _playerControlAttached = true;
        }

        TryLoadStartupFile(vm);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        _ = EnsureDependenciesAsync();
    }

    private async Task EnsureDependenciesAsync()
    {
        if (_dependencyCheckStarted)
        {
            return;
        }

        _dependencyCheckStarted = true;

        if (DataContext is VideoPlayerViewModel vm)
        {
            vm.SetDependencyLoading(true);
        }

        var dependencyResult = await Task.Run(PlayerDependencyService.EnsureNativeDependencies);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _dependencyCheckResult = dependencyResult;
            _dependencyCheckCompleted = true;

            if (!dependencyResult.Success)
            {
                Trace.Write("VideoPlayerWindow", dependencyResult.Message);
            }

            WireUpViewModel();
        });
    }

    private void TryLoadStartupFile(VideoPlayerViewModel vm)
    {
        if (string.IsNullOrWhiteSpace(_startupFilePath))
        {
            return;
        }

        string fileToOpen = _startupFilePath;
        _startupFilePath = null;
        Dispatcher.UIThread.Post(() =>
        {
            vm.LoadFileFromPath(fileToOpen);
        }, DispatcherPriority.Background);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateCursorVisibility();
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerViewModel.IsControlsVisible))
        {
            UpdateCursorVisibility();
        }
    }

    private void UpdateCursorVisibility()
    {
        if (DataContext is VideoPlayerViewModel vm)
        {
            if (!vm.IsControlsVisible && WindowState == WindowState.FullScreen)
            {
                Cursor = new Cursor(StandardCursorType.None);
            }
            else
            {
                Cursor = null;
            }
        }
    }

    private void Vm_OnRequestFullscreenMode(bool enterFullscreen)
    {
        if (enterFullscreen)
        {
            if (WindowState != WindowState.FullScreen)
            {
                WindowState = WindowState.FullScreen;
            }
            return;
        }

        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void Vm_OnRequestFullscreenToggle(object? sender, System.EventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = WindowState.Normal;
        else
            WindowState = WindowState.FullScreen;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is VideoPlayerViewModel vm)
        {
            vm.ShowControls();

            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                vm.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
        }
    }

    private void ControlPanel_PointerMoved(object? sender, PointerEventArgs e)
    {
        var currentPos = e.GetPosition(this);
        var distance = Math.Sqrt(Math.Pow(currentPos.X - _lastPointerPosition.X, 2) + Math.Pow(currentPos.Y - _lastPointerPosition.Y, 2));

        if (distance > PointerMoveThreshold)
        {
            _lastPointerPosition = currentPos;
            if (DataContext is VideoPlayerViewModel vm)
            {
                vm.ShowControls();
            }
        }
    }

    private void ControlPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is VideoPlayerViewModel vm)
        {
            vm.ShowControls();
        }
    }
}
