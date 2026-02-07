using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace NewAxis;

public partial class App : Application
{
    public override void Initialize()
    {
        // Keep App bootstrap resilient even when App.axaml precompiled metadata is stale/missing.
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Program.StartVideoPlayerMode
                ? new VideoPlayerWindow(Program.StartupVideoPath)
                : Program.StartMeshViewerMode
                    ? new MeshWindow()
                    : new MainWindow();

            desktop.MainWindow.Opened += (_, _) => Program.NotifyChildReadyIfNeeded();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
