using Avalonia.Controls;
using NewAxis.Models;
using NewAxis.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;

namespace NewAxis;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel();
        DataContext = vm;

        //vm.PropertyChanged += OnPropertyChanged;

        vm.RequestBrowseFolder += async () =>
        {
            if (!StorageProvider.CanPickFolder) return null;

            var result = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Game Folder",
                AllowMultiple = false
            });

            return result.Count > 0 ? result[0].Path.LocalPath : null;
        };

        vm.RequestShutdownAction = () =>
        {
            vm.ShutdownRequested = false; // Reset flag to avoid loop if we needed to check it again
            Close();
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsGameSessionActive)
        {
            e.Cancel = true;
            vm.ShutdownRequested = true;
            Hide();
            return; // Don't call base or continue
        }

        base.OnClosing(e);
    }

    private void OnHotkeyKeyDown(object sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is TextBox textBox && DataContext is MainViewModel viewModel)
        {
            e.Handled = true;

            // Build hotkey string
            var modifiers = e.KeyModifiers != Avalonia.Input.KeyModifiers.None ? $"{e.KeyModifiers}+" : "";
            modifiers = modifiers.Replace("Control", "Ctrl");
            string hotkey;

            // If the pressed key is a modifier itself, use KeyModifiers to represent the combo state
            if (e.Key == Avalonia.Input.Key.LeftCtrl || e.Key == Avalonia.Input.Key.RightCtrl ||
                e.Key == Avalonia.Input.Key.LeftAlt || e.Key == Avalonia.Input.Key.RightAlt ||
                e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift ||
                e.Key == Avalonia.Input.Key.LWin || e.Key == Avalonia.Input.Key.RWin)
            {
                // KeyModifiers contains the full state (e.g., Control | Shift)
                if (e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
                {
                    // Fallback for edge cases (e.g. just pressed and system hasn't updated modifiers yet, though rare)
                    if (e.Key.ToString().Contains("Shift")) hotkey = "Shift";
                    else if (e.Key.ToString().Contains("Ctrl")) hotkey = "Ctrl";
                    else if (e.Key.ToString().Contains("Alt")) hotkey = "Alt";
                    else hotkey = e.Key.ToString();
                }
                else
                {
                    hotkey = e.KeyModifiers.ToString().Replace(", ", "+").Replace("Control", "Ctrl");
                }
            }
            else
            {
                var keyStr = FormatKey(e.Key);
                hotkey = $"{modifiers}{keyStr}";
            }

            // Update ViewModel based on Tag
            if (textBox.Tag is string tag)
            {
                viewModel.UpdateHotkey(tag, e.Key, e.KeyModifiers, hotkey);
            }
        }
    }

    private string FormatKey(Avalonia.Input.Key key)
    {
        // Handle number keys
        if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9)
            return ((int)key - (int)Avalonia.Input.Key.D0).ToString();

        // Handle numpad keys
        if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9)
            return ((int)key - (int)Avalonia.Input.Key.NumPad0).ToString();

        // Handle specific OEM keys
        switch (key)
        {
            case Avalonia.Input.Key.OemComma: return ",";
            case Avalonia.Input.Key.OemPeriod: return ".";
            case Avalonia.Input.Key.OemMinus: return "-";
            case Avalonia.Input.Key.OemPlus: return "+";
            case Avalonia.Input.Key.OemQuestion: return "/";
            case Avalonia.Input.Key.OemQuotes: return "'";
            case Avalonia.Input.Key.OemSemicolon: return ";";
            case Avalonia.Input.Key.OemOpenBrackets: return "[";
            case Avalonia.Input.Key.OemCloseBrackets: return "]";
            case Avalonia.Input.Key.OemPipe: return "\\";
            case Avalonia.Input.Key.OemTilde: return "`";
            // Add more mappings as needed
            default: return key.ToString();
        }
    }
}