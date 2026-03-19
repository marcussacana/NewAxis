using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using NewAxis.Controls;
using NewAxis.Services;

namespace NewAxis.ViewModels
{
    public class VideoPlayerViewModel : ViewModelBase
    {
        private VideoPlayerControl? _playerControl;
        private bool _isPlaying;
        private string _currentFile = "";
        private TimeSpan _position;
        private TimeSpan _duration;
        private string _statusMessage = string.Empty;
        private bool _isControlsVisible = true;
        private bool _isTrackMenuOpen;
        private string _currentTrackType = "";
        private readonly DispatcherTimer _autoHideTimer;
        private readonly DispatcherTimer _statusTimer;
        private string _lastMainStatus = L("VideoStatusStopped");
        private readonly DispatcherTimer _syncTimer;
        private bool _is3DEnabled;
        private int _stereoToggleVersion;
        private bool _dependenciesReady = true;
        private bool _isDependencyCheckInProgress;
        private bool _isLoopEnabled;
        private double _volume = 100;
        private string _dependencyStatusMessage = string.Empty;
        private PlayerDependencyService.DependencyCheckResult _lastDependencyResult =
            new(true, "OK");

        public bool IsLoopEnabled
        {
            get => _isLoopEnabled;
            set => SetField(ref _isLoopEnabled, value);
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetField(ref _isPlaying, value);
        }

        public string CurrentFile
        {
            get => _currentFile;
            set => SetField(ref _currentFile, value);
        }

        public TimeSpan Position
        {
            get => _position;
            set => SetField(ref _position, value);
        }

        public TimeSpan Duration
        {
            get => _duration;
            set => SetField(ref _duration, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public string PlaybackBackendLabel => "MPV";

        public bool Is3DEnabled
        {
            get => _is3DEnabled;
            set
            {
                if (SetField(ref _is3DEnabled, value))
                {
                    OnPropertyChanged(nameof(StereoButtonLabel));
                }
            }
        }

        public string StereoButtonLabel => Is3DEnabled ? L("VideoStereoOn") : L("VideoStereoOff");
        public string AudioButtonLabel => L("VideoAudio");
        public string SubsButtonLabel => L("VideoSubs");
        public string CurrentTrackTypeLabel => CurrentTrackType == "Audio" ? L("VideoTrackTypeAudio") : L("VideoTrackTypeSubtitle");

        public double PositionSeconds
        {
            get => _position.TotalSeconds;
            set => _playerControl?.Seek(value);
        }

        public double Volume
        {
            get => _volume;
            set
            {
                if (SetField(ref _volume, value))
                {
                    _playerControl?.SetVolume(value);
                }
            }
        }

        public double DurationSeconds => _duration.TotalSeconds;

        private float _playbackSpeed = 1.0f;
        public float PlaybackSpeed
        {
            get => _playbackSpeed;
            set => SetField(ref _playbackSpeed, value);
        }

        public bool IsTrackMenuOpen
        {
            get => _isTrackMenuOpen;
            set => SetField(ref _isTrackMenuOpen, value);
        }

        public string CurrentTrackType
        {
            get => _currentTrackType;
            set
            {
                if (SetField(ref _currentTrackType, value))
                {
                    OnPropertyChanged(nameof(CurrentTrackTypeLabel));
                }
            }
        }

        public bool IsControlsVisible
        {
            get => _isControlsVisible;
            set => SetField(ref _isControlsVisible, value);
        }

        public bool IsDependencyCheckInProgress
        {
            get => _isDependencyCheckInProgress;
            set
            {
                if (SetField(ref _isDependencyCheckInProgress, value))
                {
                    OnPropertyChanged(nameof(IsDependencyOverlayVisible));
                    OnPropertyChanged(nameof(IsDependencyErrorVisible));
                    OnPropertyChanged(nameof(DependencyOverlayMessage));
                }
            }
        }

        public bool IsDependencyOverlayVisible => _isDependencyCheckInProgress || !_dependenciesReady;

        public bool IsDependencyErrorVisible => !_isDependencyCheckInProgress && !_dependenciesReady;

        public string DependencyOverlayMessage
        {
            get
            {
                if (_isDependencyCheckInProgress)
                {
                    return L("PreparingData");
                }

                if (_dependenciesReady)
                {
                    return string.Empty;
                }

                return string.IsNullOrWhiteSpace(_dependencyStatusMessage)
                    ? string.Format(L("PlayerDepsUnavailable"), L("PlayerDepsUnknownFiles"))
                    : _dependencyStatusMessage;
            }
        }

        private bool _hasAudioTracks;
        public bool HasAudioTracks
        {
            get => _hasAudioTracks;
            set => SetField(ref _hasAudioTracks, value);
        }

        private bool _hasSubtitleTracks;
        public bool HasSubtitleTracks
        {
            get => _hasSubtitleTracks;
            set => SetField(ref _hasSubtitleTracks, value);
        }

        public ObservableCollection<VideoPlayerTrack> AudioTracks { get; } = new();
        public ObservableCollection<VideoPlayerTrack> SubtitleTracks { get; } = new();

        public Func<Task<string?>>? RequestOpenFile { get; set; }
        public event EventHandler? OnRequestFullscreenToggle;
        public event Action<bool>? OnRequestFullscreenMode;

        public ICommand PlayCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand TogglePlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand OpenFileCommand { get; }
        public ICommand SkipForwardCommand { get; }
        public ICommand SkipBackwardCommand { get; }
        public ICommand SkipForwardPercentCommand { get; }
        public ICommand SkipBackwardPercentCommand { get; }
        public ICommand SpeedUpCommand { get; }
        public ICommand SpeedDownCommand { get; }
        public ICommand ToggleAudioMenuCommand { get; }
        public ICommand ToggleSubtitleMenuCommand { get; }
        public ICommand ToggleFullscreenCommand { get; }
        public ICommand CloseTrackMenuCommand { get; }
        public ICommand SelectTrackCommand { get; }
        public ICommand Toggle3DCommand { get; }
        public ICommand ToggleLoopCommand { get; }

        public VideoPlayerViewModel()
        {
            _statusMessage = L("VideoStatusReady");
            _lastMainStatus = _statusMessage;
            LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

            PlayCommand = new RelayCommand(_ => Play());
            PauseCommand = new RelayCommand(_ => Pause());
            TogglePlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
            StopCommand = new RelayCommand(_ => Stop());
            OpenFileCommand = new RelayCommand(_ => OpenFile());
            SkipForwardCommand = new RelayCommand(_ => Skip(5));
            SkipBackwardCommand = new RelayCommand(_ => Skip(-5));
            SkipForwardPercentCommand = new RelayCommand(_ => SkipPercentage(0.05));
            SkipBackwardPercentCommand = new RelayCommand(_ => SkipPercentage(-0.05));
            SpeedUpCommand = new RelayCommand(_ => AdjustSpeed(0.1f));
            SpeedDownCommand = new RelayCommand(_ => AdjustSpeed(-0.1f));
            ToggleAudioMenuCommand = new RelayCommand(_ => OpenTrackMenu("Audio"));
            ToggleSubtitleMenuCommand = new RelayCommand(_ => OpenTrackMenu("Subtitle"));
            ToggleFullscreenCommand = new RelayCommand(_ => ToggleFullscreen());
            CloseTrackMenuCommand = new RelayCommand(_ =>
            {
                IsTrackMenuOpen = false;
                ShowControls();
            });
            SelectTrackCommand = new RelayCommand(track =>
            {
                if (track is VideoPlayerTrack t)
                {
                    SelectTrack(t);
                }
            });
            Toggle3DCommand = new RelayCommand(_ => Toggle3D());
            ToggleLoopCommand = new RelayCommand(_ => ToggleLoop());

            _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _autoHideTimer.Tick += (_, _) =>
            {
                if (!IsTrackMenuOpen)
                {
                    IsControlsVisible = false;
                }
                _autoHideTimer.Stop();
            };
            _autoHideTimer.Start();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, _) =>
            {
                StatusMessage = _lastMainStatus;
                _statusTimer.Stop();
            };

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _syncTimer.Tick += SyncPlaybackInfo;
            _syncTimer.Start();
        }

        public void SetPlayerControl(VideoPlayerControl control)
        {
            _playerControl = control;
            if (!_dependenciesReady)
            {
                Trace.Write("VideoPlayerVM", $"Player control attached with dependency error: {_dependencyStatusMessage}");
                return;
            }

            _playerControl.SetPlaybackBackend(PlaybackBackend.Mpv);
            _playerControl.SetSbs3DEnabled(Is3DEnabled);
            _playerControl.FileLoaded += OnFileLoaded;
            Trace.Write("VideoPlayerVM", "Player control attached.");
        }

        public void SetDependencyStatus(PlayerDependencyService.DependencyCheckResult result)
        {
            _lastDependencyResult = result;
            _dependenciesReady = result.Success;
            _dependencyStatusMessage = BuildDependencyStatusMessage(result);
            OnPropertyChanged(nameof(IsDependencyOverlayVisible));
            OnPropertyChanged(nameof(IsDependencyErrorVisible));
            OnPropertyChanged(nameof(DependencyOverlayMessage));

            if (result.Success)
            {
                SetStatus(L("VideoStatusReady"), temporary: false);
                return;
            }

            IsControlsVisible = true;
            _autoHideTimer.Stop();
            SetStatus(_dependencyStatusMessage, temporary: false);
        }

        public void SetDependencyStatus(bool ready, string message)
        {
            SetDependencyStatus(new PlayerDependencyService.DependencyCheckResult(ready, message));
        }

        public void SetDependencyLoading(bool isLoading)
        {
            IsDependencyCheckInProgress = isLoading;

            if (!isLoading)
            {
                return;
            }

            IsControlsVisible = true;
            _autoHideTimer.Stop();
            SetStatus(L("PreparingData"), temporary: false);
        }

        private string BuildDependencyStatusMessage(PlayerDependencyService.DependencyCheckResult result)
        {
            if (result.Success)
            {
                return string.Empty;
            }

            string files = (result.MissingRequiredFiles != null && result.MissingRequiredFiles.Length > 0)
                ? string.Join(", ", result.MissingRequiredFiles)
                : L("PlayerDepsUnknownFiles");

            return string.Format(L("PlayerDepsUnavailable"), files);
        }

        private static string L(string key) => LocalizationService.Instance[key];

        private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizationService.CurrentLanguage) || e.PropertyName == "Item[]" || e.PropertyName == "Item")
            {
                OnPropertyChanged(nameof(StereoButtonLabel));
                OnPropertyChanged(nameof(AudioButtonLabel));
                OnPropertyChanged(nameof(SubsButtonLabel));
                OnPropertyChanged(nameof(CurrentTrackTypeLabel));

                if (_isDependencyCheckInProgress)
                {
                    OnPropertyChanged(nameof(DependencyOverlayMessage));
                    SetStatus(L("PreparingData"), temporary: false);
                }
                else if (!_dependenciesReady)
                {
                    _dependencyStatusMessage = BuildDependencyStatusMessage(_lastDependencyResult);
                    OnPropertyChanged(nameof(DependencyOverlayMessage));
                    SetStatus(_dependencyStatusMessage, temporary: false);
                }
            }
        }

        private bool EnsureDependenciesReady()
        {
            if (_isDependencyCheckInProgress)
            {
                SetStatus(L("PreparingData"), temporary: true);
                ShowControls();
                return false;
            }

            if (_dependenciesReady)
            {
                return true;
            }

            string msg = string.IsNullOrWhiteSpace(_dependencyStatusMessage)
                ? string.Format(L("PlayerDepsUnavailable"), L("PlayerDepsUnknownFiles"))
                : _dependencyStatusMessage;

            SetStatus(msg, temporary: true);
            ShowControls();
            return false;
        }

        private void SyncPlaybackInfo(object? sender, EventArgs e)
        {
            if (_playerControl == null || !IsPlaying)
            {
                return;
            }

            Position = TimeSpan.FromSeconds(_playerControl.GetPosition());
            Duration = TimeSpan.FromSeconds(_playerControl.GetDuration());
            PlaybackSpeed = (float)_playerControl.GetSpeed();
            Volume = _playerControl.GetVolume();
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(DurationSeconds));
        }

        private void Skip(double seconds)
        {
            if (_playerControl == null)
            {
                return;
            }

            double currentPos = _playerControl.GetPosition();
            double duration = _playerControl.GetDuration();
            double newPos = Math.Clamp(currentPos + seconds, 0, duration);
            _playerControl.Seek(newPos);
            ShowControls();
        }

        private void SkipPercentage(double percent)
        {
            if (_playerControl == null)
            {
                return;
            }

            double duration = _playerControl.GetDuration();
            if (duration <= 0)
            {
                return;
            }

            double skipSeconds = duration * percent;
            double currentPos = _playerControl.GetPosition();
            double newPos = Math.Clamp(currentPos + skipSeconds, 0, duration);
            _playerControl.Seek(newPos);
            ShowControls();
        }

        private void AdjustSpeed(float delta)
        {
            if (_playerControl == null)
            {
                return;
            }

            float newSpeed = (float)Math.Round(PlaybackSpeed + delta, 1);
            if (newSpeed < 0.1f) newSpeed = 0.1f;
            if (newSpeed > 4.0f) newSpeed = 4.0f;

            _playerControl.SetSpeed(newSpeed);
            PlaybackSpeed = newSpeed;
            ShowControls();
        }

        private void OpenTrackMenu(string type)
        {
            CurrentTrackType = type;
            IsTrackMenuOpen = true;
            ShowControls();
            FetchTracks();
        }

        private void FetchTracks()
        {
            AudioTracks.Clear();
            SubtitleTracks.Clear();
            HasAudioTracks = false;
            HasSubtitleTracks = false;

            if (_playerControl == null)
            {
                return;
            }

            IReadOnlyList<MediaTrackInfo> audioTracks = _playerControl.GetTracks(MediaTrackType.Audio);
            IReadOnlyList<MediaTrackInfo> subtitleTracks = _playerControl.GetTracks(MediaTrackType.Subtitle);

            foreach (MediaTrackInfo track in audioTracks)
            {
                AudioTracks.Add(new VideoPlayerTrack
                {
                    Id = track.Id,
                    Title = track.Title,
                    Language = track.Language,
                    IsSelected = track.IsSelected
                });
            }

            if (_playerControl.IsMpvBackendActive)
            {
                SubtitleTracks.Add(new VideoPlayerTrack
                {
                    Id = 0,
                    Title = L("VideoTrackNone"),
                    Language = "none",
                    IsSelected = subtitleTracks.All(t => !t.IsSelected)
                });
            }

            foreach (MediaTrackInfo track in subtitleTracks)
            {
                SubtitleTracks.Add(new VideoPlayerTrack
                {
                    Id = track.Id,
                    Title = track.Title,
                    Language = track.Language,
                    IsSelected = track.IsSelected
                });
            }

            HasAudioTracks = AudioTracks.Count > 0;
            HasSubtitleTracks = SubtitleTracks.Count > (_playerControl.IsMpvBackendActive ? 1 : 0);
        }

        private void SelectTrack(VideoPlayerTrack track)
        {
            if (_playerControl == null)
            {
                return;
            }

            var source = CurrentTrackType == "Audio" ? AudioTracks : SubtitleTracks;
            foreach (var t in source)
            {
                t.IsSelected = ReferenceEquals(t, track);
            }

            MediaTrackType trackType = CurrentTrackType == "Audio" ? MediaTrackType.Audio : MediaTrackType.Subtitle;
            _playerControl.SelectTrack(trackType, track.Id);
            IsTrackMenuOpen = false;
        }

        public void ShowControls()
        {
            if (!IsControlsVisible)
            {
                IsControlsVisible = true;
            }

            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        public void SetStatus(string msg, bool temporary = false)
        {
            StatusMessage = msg;
            if (temporary)
            {
                _statusTimer.Stop();
                _statusTimer.Start();
            }
            else
            {
                _lastMainStatus = msg;
                _statusTimer.Stop();
            }
        }

        private void OnFileLoaded()
        {
            Dispatcher.UIThread.Post(() =>
            {
                FetchTracks();
                string? path = _playerControl?.CurrentFile;
                if (!string.IsNullOrEmpty(path))
                {
                    CurrentFile = path;
                    SetStatus(string.Format(L("VideoStatusPlayingFile"), PlaybackBackendLabel, Path.GetFileName(path)));
                }
            });
        }

        private void ToggleFullscreen()
        {
            OnRequestFullscreenToggle?.Invoke(this, EventArgs.Empty);
        }

        private void Play()
        {
            if (!EnsureDependenciesReady())
            {
                return;
            }

            if (_playerControl == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_playerControl.CurrentFile))
            {
                SetStatus(L("VideoStatusOpenFileFirst"), true);
                ShowControls();
                return;
            }

            try
            {
                _playerControl.Play();
                IsPlaying = _playerControl.IsPlaying;
                SetStatus(IsPlaying ? L("VideoStatusPlaying") : L("VideoStatusPlayerNotReady"), !IsPlaying);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VideoPlayerVM] Play failed: {ex}");
                IsPlaying = false;
                SetStatus(L("VideoStatusPlayerNotReady"), true);
            }
            ShowControls();
        }

        private void TogglePlayPause()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        private void Pause()
        {
            if (!EnsureDependenciesReady())
            {
                return;
            }

            if (_playerControl == null)
            {
                return;
            }

            _playerControl.Pause();
            IsPlaying = false;
            SetStatus(L("VideoStatusPaused"));
            ShowControls();
        }

        private void Stop()
        {
            if (!EnsureDependenciesReady())
            {
                return;
            }

            if (_playerControl == null)
            {
                return;
            }

            _playerControl.Stop();
            IsPlaying = false;
            SetStatus(L("VideoStatusStopped"));
            ShowControls();
        }

        private async void OpenFile()
        {
            if (!EnsureDependenciesReady())
            {
                return;
            }

            if (RequestOpenFile == null)
            {
                SetStatus(L("VideoStatusNoFilePicker"), true);
                return;
            }

            string? file = await RequestOpenFile.Invoke();
            if (string.IsNullOrWhiteSpace(file))
            {
                SetStatus(L("VideoStatusNoFileSelected"), true);
                return;
            }

            LoadFileFromPath(file);
        }

        public bool LoadFileFromPath(string filePath)
        {
            if (!EnsureDependenciesReady())
            {
                return false;
            }

            if (_playerControl == null)
            {
                SetStatus(L("VideoStatusPlayerNotInitialized"), true);
                Trace.Write("VideoPlayerVM", $"LoadFileFromPath rejected: player control is null. file={filePath}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                SetStatus(L("VideoStatusFileNotFound"), true);
                Trace.Write("VideoPlayerVM", $"LoadFileFromPath rejected: file not found. file={filePath}");
                return false;
            }

            try
            {
                var fi = new FileInfo(filePath);
                Trace.Write("VideoPlayerVM", $"Loading file: {fi.FullName} ({fi.Length} bytes)");

                _playerControl.LoadFile(filePath);
                _playerControl.SetSbs3DEnabled(Is3DEnabled);
                CurrentFile = filePath;
                IsPlaying = true;
                SetStatus(string.Format(L("VideoStatusPlayingFile"), PlaybackBackendLabel, Path.GetFileName(filePath)));
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VideoPlayerVM] LoadFileFromPath failed for {filePath}: {ex}");
                SetStatus(L("VideoStatusLoadFailed"), true);
                return false;
            }
        }

        private async void Toggle3D()
        {
            if (!EnsureDependenciesReady())
            {
                return;
            }

            bool enable3D = !Is3DEnabled;
            int toggleVersion = ++_stereoToggleVersion;
            Is3DEnabled = enable3D;

            if (enable3D)
            {
                OnRequestFullscreenMode?.Invoke(true);
                await Task.Delay(200);
                if (toggleVersion != _stereoToggleVersion)
                {
                    return;
                }
            }

            if (_playerControl != null)
            {
                _playerControl.SetSbs3DEnabled(enable3D);
            }

            if (enable3D)
            {
                string mode = _playerControl?.GetDetectedSbsLayoutLabel() ?? L("VideoStereoAuto");
                SetStatus(string.Format(L("VideoStatus3DEnabled"), mode), true);
            }
            else
            {
                SetStatus(L("VideoStatus3DDisabled"), true);
            }

            ShowControls();
        }

        private void ToggleLoop()
        {
            IsLoopEnabled = !IsLoopEnabled;
            _playerControl?.SetLoop(IsLoopEnabled);

            string status = IsLoopEnabled ? L("VideoStatusLoopEnabled") : L("VideoStatusLoopDisabled");
            SetStatus(status, true);
            ShowControls();
        }
    }

    public class VideoPlayerTrack : ViewModelBase
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Language { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }
    }

    public class BoolToOpacityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b) return b ? 1.0 : 0.0;
            return 0.0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is double d && d > 0;
        }
    }

    public class StringEqualsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value?.ToString() == parameter?.ToString();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    public class BoolToOpacityActiveConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b) return b ? 1.0 : 0.6;
            return 0.6;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToAccentIconBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                // #D6A23A is the accent gold color used in sliders
                return new SolidColorBrush(Color.Parse("#D6A23A"));
            }
            return Brushes.White;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
