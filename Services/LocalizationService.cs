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
        { "SelectGamePrompt", "Select a game to begin" }
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

        // Default to en-US if current not found, or first available
        if (!AvailableLanguages.Contains(_currentLanguage))
        {
            _currentLanguage = AvailableLanguages.FirstOrDefault() ?? "en-US";
        }
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
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                    if (dict != null)
                    {
                        _translations[code] = dict;
                        AvailableLanguages.Add(code);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load language file {file}: {ex.Message}");
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
