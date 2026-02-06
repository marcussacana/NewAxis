using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NewAxis;

class Program
{
    public static double CurrentVersion = 12;
    public static string? CustomRepoPath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        bool logEnabled = args?.Any(x => x.TrimStart('-', '/', '\\').Equals("log", StringComparison.InvariantCultureIgnoreCase)) ?? false;

        if (logEnabled)
        {
            Trace.Listeners.Add(new TextWriterTraceListener(File.CreateText("NewAxis.log")));
            Trace.AutoFlush = true;
        }

        // Parse --repo-path argument
        var repoPathArg = args?.FirstOrDefault(x => x.StartsWith("--repo-path=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(repoPathArg))
        {
            CustomRepoPath = repoPathArg.Substring("--repo-path=".Length);
            Trace.WriteLine($"Custom repository path from args: {CustomRepoPath}");
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
