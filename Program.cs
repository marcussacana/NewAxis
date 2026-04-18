using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using NewAxis.Services;

namespace NewAxis;

class Program
{
    public static double CurrentVersion = 15;
    public static string? CustomRepoPath { get; private set; }
    public static string? StartupVideoPath { get; private set; }
    public static bool StartVideoPlayerMode { get; private set; }
    public static bool StartMeshViewerMode { get; private set; }
    public static bool LogEnabled { get; private set; }
    public static string? StartupPipeName { get; private set; }
    private static int _startupReadySignaled;

    [STAThread]
    public static void Main(string[] args)
    {
        bool logEnabled = args?.Any(x => x.TrimStart('-', '/', '\\').Equals("log", StringComparison.InvariantCultureIgnoreCase)) ?? false;
        LogEnabled = logEnabled;

        if (logEnabled)
        {
            Trace.Listeners.Add(new TextWriterTraceListener(File.CreateText("NewAxis.log")));
            Trace.AutoFlush = true;
        }

        Trace.WriteLine("Program", $"Args: {string.Join(" ", args ?? Array.Empty<string>())}");

        // Parse --repo-path argument
        var repoPathArg = args?.FirstOrDefault(x => x.StartsWith("--repo-path=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(repoPathArg))
        {
            CustomRepoPath = repoPathArg.Substring("--repo-path=".Length);
            Trace.WriteLine("Program", $"Custom repository path from args: {CustomRepoPath}");
        }

        StartupVideoPath = GetArgValue(args, "--player-file=", "--video-file=");
        StartVideoPlayerMode = (args?.Contains("--player") == true) || !string.IsNullOrWhiteSpace(StartupVideoPath);

        var childMode = GetArgValue(args, "--child-mode=");
        if (string.Equals(childMode, "player", StringComparison.OrdinalIgnoreCase))
        {
            StartVideoPlayerMode = true;
        }
        else if (string.Equals(childMode, "viewer", StringComparison.OrdinalIgnoreCase))
        {
            StartMeshViewerMode = true;
            StartVideoPlayerMode = false;
        }

        StartupPipeName = GetArgValue(args, "--startup-pipe=");
        if (!string.IsNullOrWhiteSpace(StartupVideoPath))
        {

            bool exists = File.Exists(StartupVideoPath);
            Trace.Write("Program", $"Startup video path: {StartupVideoPath} | exists={exists}");
        }

        string? mpvProbePath = GetArgValue(args, "--mpv-probe=");
        if (!string.IsNullOrWhiteSpace(mpvProbePath))
        {
            int rc = RunMpvProbe(mpvProbePath);
            Environment.ExitCode = rc;
            return;
        }

        if (Services.UpdateManager.HandleUpdateArgs(args))
        {
            return;
        }

        if ((args?.Contains("--viewer") == true) && !StartMeshViewerMode)
        {
            NewAxis.Graphics.StandaloneMeshViewer.Run();
            return;
        }


#if DEBUG
        NewAxis.Services.Debug.Attach();
#endif

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Wgl }
            })
            .WithInterFont()
            .LogToTrace();

    private static string? GetArgValue(string[]? args, params string[] prefixes)
    {
        if (args == null || args.Length == 0)
        {
            return null;
        }

        foreach (var arg in args)
        {
            foreach (var prefix in prefixes)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length).Trim().Trim('"');
                }
            }
        }

        return null;
    }

    private static int RunMpvProbe(string filePath)
    {
        const string src = "MPV-PROBE";
        try
        {
            Trace.WriteLine(src, $"Starting probe for: {filePath}");
            if (!File.Exists(filePath))
            {
                Trace.WriteLine(src, "File does not exist.");
                return 2;
            }

            var deps = PlayerDependencyService.EnsureNativeDependencies();
            if (!deps.Success)
            {
                Trace.WriteLine(src, deps.Message);
                return 3;
            }

            using var mpv = new MpvContext();
            mpv.FileLoaded += () => Trace.WriteLine(src, "FILE_LOADED event received.");
            mpv.Initialize("--vo=null", "--hwdec=no", "--wid=0", "--msg-level=all=v");
            mpv.LoadFile(filePath);

            for (int i = 0; i < 40; i++)
            {
                mpv.PollEvents();
                if (i % 4 == 0)
                {
                    double duration = mpv.GetPropertyDouble("duration");
                    double width = mpv.GetPropertyDouble("width");
                    double height = mpv.GetPropertyDouble("height");
                    string? path = mpv.GetPropertyString("path");
                    Trace.Write(src, $"poll={i} duration={duration:0.00}s size={width:0}x{height:0} path={path ?? "<null>"}");
                }
                Thread.Sleep(250);
            }

            Trace.Write(src, "Probe finished without process crash.");
            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[{src}] Probe failed: {ex}");
            return 1;
        }
    }

    public static void NotifyChildReadyIfNeeded()
    {
        if (Interlocked.Exchange(ref _startupReadySignaled, 1) == 1)
        {
            return;
        }

        var pipeName = StartupPipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(2000);

                using var writer = new StreamWriter(pipeClient, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                await writer.WriteLineAsync("READY");
                Trace.WriteLine("Program", $"Child READY signal sent via pipe {pipeName}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Program] Failed to send READY signal to pipe {pipeName}: {ex.Message}");
            }
        });
    }
}

