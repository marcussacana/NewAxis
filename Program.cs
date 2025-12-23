using Avalonia;
using System;

namespace NewAxis;

class Program
{
    public const int CurrentVersion = 5;

    [STAThread]
    public static void Main(string[] args)
    {
        if (Services.UpdateManager.HandleUpdateArgs(args))
        {
            return;
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
