using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
using System.Collections.ObjectModel;

namespace NewAxis.Services;

public class LocalizationService : INotifyPropertyChanged
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    private string _currentLanguage = "en-US";
    private readonly Dictionary<string, Dictionary<string, string>> _translations = new();

    // Fallback dictionary (English) embedded in code to ensure app works even if JSONs are missing
    private readonly Dictionary<string, string> _embeddedFallback = new()
    {
        { "Start", "Start" }, { "Depth", "Depth" }, { "Popout", "Popout" },
        { "InstalledAt", "Installed at:" }, { "By", "by" }, { "SelectMod", "Select Mod" },
        { "Settings", "Settings" }, { "Language", "Language" },
        { "InstallModTemp", "Install mod temporarily" }, { "Hotkeys", "Hotkeys" },
        { "DisableDLSS", "Disable DLSS/FrameGen" },
        { "DepthInc", "Increase Depth" }, { "DepthDec", "Decrease Depth" },
        { "PopoutInc", "Increase Popout" }, { "PopoutDec", "Decrease Popout" },
        { "ResetDefaults", "Reset Defaults" }, { "Apply", "Apply" },
        { "ActiveMod", "Active Mod:" }, { "UnknownCreator", "Unknown Creator" },
        { "BrowseFolder", "Browse Folder" }, { "RemoveGame", "Remove from List" },
        { "ToggleUninstalled", "Toggle Uninstalled Games" }, { "OpenSettings", "Settings" },
        { "SelectGamePrompt", "Select a game to begin" },
        { "Open3DViewer", "Open 3D Viewer" }, { "Open3DVideoPlayer", "Open 3D Video Player" },
        { "Viewer3DWindowTitle", "3D Viewer" }, { "Viewer3DTitle", "3D Viewer" },
        { "ViewerControls", "Controls" }, { "LoadModel", "Load Model" }, { "Scale", "Scale" },
        { "Parallax", "Parallax" }, { "AutoRotate", "Auto Rotate" }, { "ResetView", "Reset View" },
        { "DitheredTransparency", "Dithered Transparency" },
        { "Open3DModel", "Open 3D Model" }, { "Models3D", "3D Models" },
        { "ObjFiles", "OBJ Files" }, { "GlbFiles", "GLB Files" },
        { "VideoPlayerWindowTitle", "3D Video Player" }, { "SelectVideoFile", "Select Video File" },
        { "VideoFiles", "Video Files" }, { "AllFiles", "All Files" },
        { "VideoAudio", "Audio" }, { "VideoSubs", "Subs" },
        { "VideoTrackTypeAudio", "Audio" }, { "VideoTrackTypeSubtitle", "Subtitle" },
        { "VideoStereoOn", "3D ON" }, { "VideoStereoOff", "3D OFF" },
        { "VideoTrackNone", "None" },
        { "VideoStatusReady", "Ready - Open a video file to begin" },
        { "VideoStatusOpenFileFirst", "Open a video file first" },
        { "VideoStatusPlaying", "Playing" },
        { "VideoStatusPlayerNotReady", "Player not ready" },
        { "VideoStatusPaused", "Paused" },
        { "VideoStatusStopped", "Stopped" },
        { "VideoStatusNoFilePicker", "Error: no file picker provider" },
        { "VideoStatusNoFileSelected", "No file selected" },
        { "VideoStatusPlayerNotInitialized", "Player not initialized" },
        { "VideoStatusFileNotFound", "Video file not found" },
        { "VideoStatusLoadFailed", "Failed to load video" },
        { "VideoStatusPlayingFile", "Playing ({0}): {1}" },
        { "VideoStatus3DEnabled", "3D enabled ({0})" },
        { "VideoStatus3DDisabled", "3D disabled" },
        { "PlayerDepsUnavailable", "Video player dependencies unavailable: {0}." },
        { "PlayerDepsRepoHint", "Download failed from '{0}'. Check internet/RepoOverride and try again." },
        { "PlayerDepsUnknownFiles", "unknown files" },
        { "PreparingData", "Preparing" },
        { "VideoStatusLoopEnabled", "Loop Enabled" },
        { "VideoStatusLoopDisabled", "Loop Disabled" },
        { "VideoStereoAuto", "Auto" },
        { "VideoStereoFullSbs", "Full SBS" },
        { "VideoStereoHalfSbs", "Half SBS" }
    };

    public ObservableCollection<string> AvailableLanguages { get; } = new ObservableCollection<string>();

    private LocalizationService()
    {
        LoadLanguages();

        // Ensure at least English is active if nothing loaded or found
        if (!AvailableLanguages.Contains("en-US"))
        {
            _translations["en-US"] = _embeddedFallback;
            AvailableLanguages.Add("en-US");
        }

        // If we have other languages but en-US was added via fallback, ensure it's in translations
        if (!_translations.ContainsKey("en-US"))
        {
            _translations["en-US"] = _embeddedFallback;
        }

        // Default to System Language if supported, otherwise en-US
        DetectSystemLanguage();
    }

    private void DetectSystemLanguage()
    {
        try
        {
            var sysName = System.Globalization.CultureInfo.CurrentUICulture.Name;

            // 1. Exact match (e.g. pt-BR)
            if (AvailableLanguages.Contains(sysName))
            {
                _currentLanguage = sysName;
                return;
            }

            // 2. Primary language match (e.g. pt)
            var primary = sysName.Split('-')[0];
            var match = AvailableLanguages.FirstOrDefault(l => l.StartsWith(primary, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _currentLanguage = match;
                return;
            }
        }
        catch { }

        // 3. Fallback
        _currentLanguage = AvailableLanguages.Contains("en-US") ? "en-US" : (AvailableLanguages.FirstOrDefault() ?? "en-US");
    }

    private void LoadLanguages()
    {
        AvailableLanguages.Clear();
        _translations.Clear();

        var languagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages");
        if (Directory.Exists(languagesDir))
        {
            var files = Directory.GetFiles(languagesDir, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString);

                    if (dict != null)
                    {
                        _translations[code] = dict;
                        AvailableLanguages.Add(code);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Failed to load language file {file}: {ex.Message}");
                }
            }
        }
    }

    public string this[string key]
    {
        get
        {
            // 1. Try current language
            if (_translations.ContainsKey(_currentLanguage) && _translations[_currentLanguage].ContainsKey(key))
            {
                return _translations[_currentLanguage][key];
            }

            // 2. Try embedded fallback (en-US)
            if (_embeddedFallback.ContainsKey(key))
            {
                return _embeddedFallback[key];
            }

            // 3. Return key as last resort
            return key;
        }
    }

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                OnPropertyChanged(nameof(CurrentLanguage));
                OnPropertyChanged("Item");
                OnPropertyChanged("Item[]");
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
