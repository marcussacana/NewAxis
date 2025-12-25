using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NewAxis;

class Program
{
    public const int CurrentVersion = 6;

    [STAThread]
    public static void Main(string[] args)
    {
        bool logEnabled = args?.Any(x => x.TrimStart('-', '/', '\\').Equals("log", StringComparison.InvariantCultureIgnoreCase)) ?? false;

        if (logEnabled)
        {
            Trace.Listeners.Add(new TextWriterTraceListener(File.CreateText("NewAxis.log")));
            Trace.AutoFlush = true;
        }

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
