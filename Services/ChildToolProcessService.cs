using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace NewAxis.Services;

public enum ChildToolMode
{
    VideoPlayer,
    Viewer3D
}

public static class ChildToolProcessService
{
    private const int StartupTimeoutMs = 12000;
    private const int ReadyMessageTimeoutMs = 4000;
    private const int RestartDelayMs = 1500;
    private const int MaxCrashRestarts = 3;

    private static readonly object Sync = new();
    private static readonly Dictionary<ChildToolMode, Task> Supervisors = new();

    public static void Launch(ChildToolMode mode, string? startupFilePath = null)
    {
        lock (Sync)
        {
            if (Supervisors.TryGetValue(mode, out var runningTask) && !runningTask.IsCompleted)
            {
                Trace.WriteLine($"[ChildToolProcessService] {mode} is already running.");
                return;
            }

            var supervisorTask = RunSupervisorAsync(mode, startupFilePath);
            Supervisors[mode] = supervisorTask;

            _ = supervisorTask.ContinueWith(_ =>
            {
                lock (Sync)
                {
                    if (Supervisors.TryGetValue(mode, out var currentTask) && ReferenceEquals(currentTask, supervisorTask))
                    {
                        Supervisors.Remove(mode);
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    private static async Task RunSupervisorAsync(ChildToolMode mode, string? startupFilePath)
    {
        int restartCount = 0;

        while (true)
        {
            var launch = await StartChildAndWaitReadyAsync(mode, startupFilePath);
            if (!launch.Success || launch.Process == null)
            {
                restartCount++;
                if (restartCount > MaxCrashRestarts)
                {
                    Trace.WriteLine($"[ChildToolProcessService] {mode} failed to start after {MaxCrashRestarts} retries. Last error: {launch.ErrorMessage}");
                    return;
                }

                Trace.WriteLine($"[ChildToolProcessService] {mode} startup failed ({launch.ErrorMessage}). Restarting ({restartCount}/{MaxCrashRestarts})...");
                await Task.Delay(RestartDelayMs);
                continue;
            }

            using var process = launch.Process;
            Trace.WriteLine($"[ChildToolProcessService] {mode} started. PID={process.Id}");

            await process.WaitForExitAsync();

            int exitCode = SafeGetExitCode(process);
            if (exitCode == 0)
            {
                Trace.WriteLine($"[ChildToolProcessService] {mode} closed normally.");
                return;
            }

            restartCount++;
            if (restartCount > MaxCrashRestarts)
            {
                Trace.WriteLine($"[ChildToolProcessService] {mode} crashed with exit code {exitCode}. Restart limit reached.");
                return;
            }

            Trace.WriteLine($"[ChildToolProcessService] {mode} crashed with exit code {exitCode}. Restarting ({restartCount}/{MaxCrashRestarts})...");
            await Task.Delay(RestartDelayMs);
        }
    }

    private static async Task<LaunchAttempt> StartChildAndWaitReadyAsync(ChildToolMode mode, string? startupFilePath)
    {
        var launchTarget = ResolveLaunchTarget();
        if (!launchTarget.Success || string.IsNullOrWhiteSpace(launchTarget.HostPath))
        {
            return LaunchAttempt.Fail(launchTarget.ErrorMessage ?? "Failed to resolve launch target.");
        }

        string pipeName = $"newaxis-start-{mode.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";

        await using var pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var psi = new ProcessStartInfo(launchTarget.HostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };

        if (!string.IsNullOrWhiteSpace(launchTarget.ManagedAppPath))
        {
            // Framework-dependent launch via "dotnet <app>.dll ..."
            psi.ArgumentList.Add(launchTarget.ManagedAppPath);
        }

        psi.ArgumentList.Add($"--child-mode={GetModeArg(mode)}");
        psi.ArgumentList.Add($"--startup-pipe={pipeName}");

        if (!string.IsNullOrWhiteSpace(Program.CustomRepoPath))
        {
            psi.ArgumentList.Add($"--repo-path={Program.CustomRepoPath}");
        }

        if (mode == ChildToolMode.VideoPlayer && !string.IsNullOrWhiteSpace(startupFilePath))
        {
            psi.ArgumentList.Add($"--player-file={startupFilePath}");
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return LaunchAttempt.Fail($"Failed to start child process: {ex.Message}");
        }

        if (process == null)
        {
            return LaunchAttempt.Fail("Process.Start returned null.");
        }

        bool ready = await WaitForReadySignalAsync(pipeServer, process, mode);
        if (!ready)
        {
            TryTerminate(process);
            process.Dispose();
            return LaunchAttempt.Fail("Child did not signal READY.");
        }

        return LaunchAttempt.Ok(process);
    }

    private static async Task<bool> WaitForReadySignalAsync(NamedPipeServerStream pipeServer, Process process, ChildToolMode mode)
    {
        Task waitForPipeTask = pipeServer.WaitForConnectionAsync();
        Task waitForExitTask = process.WaitForExitAsync();
        Task startupTimeoutTask = Task.Delay(StartupTimeoutMs);

        Task completed = await Task.WhenAny(waitForPipeTask, waitForExitTask, startupTimeoutTask);

        if (completed == startupTimeoutTask)
        {
            Trace.WriteLine($"[ChildToolProcessService] {mode} startup timed out while waiting for pipe connection.");
            return false;
        }

        if (completed == waitForExitTask)
        {
            Trace.WriteLine($"[ChildToolProcessService] {mode} exited before startup handshake. ExitCode={SafeGetExitCode(process)}");
            return false;
        }

        try
        {
            await waitForPipeTask;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ChildToolProcessService] {mode} pipe connection failed: {ex.Message}");
            return false;
        }

        using var reader = new StreamReader(pipeServer, Encoding.UTF8, false, 1024, leaveOpen: true);
        Task<string?> readReadyTask = reader.ReadLineAsync();
        Task readTimeoutTask = Task.Delay(ReadyMessageTimeoutMs);
        Task completedRead = await Task.WhenAny(readReadyTask, process.WaitForExitAsync(), readTimeoutTask);

        if (completedRead != readReadyTask)
        {
            if (completedRead == readTimeoutTask)
            {
                Trace.WriteLine($"[ChildToolProcessService] {mode} startup timed out waiting READY message.");
            }
            else
            {
                Trace.WriteLine($"[ChildToolProcessService] {mode} exited before READY message. ExitCode={SafeGetExitCode(process)}");
            }
            return false;
        }

        string? readyMessage = await readReadyTask;
        bool ok = string.Equals(readyMessage?.Trim(), "READY", StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            Trace.WriteLine($"[ChildToolProcessService] {mode} invalid READY message: {readyMessage ?? "<null>"}");
        }

        return ok;
    }

    private static string GetModeArg(ChildToolMode mode) => mode switch
    {
        ChildToolMode.VideoPlayer => "player",
        ChildToolMode.Viewer3D => "viewer",
        _ => "player"
    };

    private static LaunchTarget ResolveLaunchTarget()
    {
        string? hostPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                hostPath = current.MainModule?.FileName;
            }
            catch
            {
                hostPath = null;
            }
        }

        if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
        {
            return LaunchTarget.Fail($"Executable path not found: {hostPath ?? "<null>"}");
        }

        string fileName = Path.GetFileName(hostPath);
        bool isDotnetHost = fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        if (!isDotnetHost)
        {
            // NativeAOT or apphost launch: start executable directly.
            return LaunchTarget.Ok(hostPath, null);
        }

        // dotnet-hosted (e.g. dotnet run / framework-dependent without apphost):
        // find the managed entry assembly path and launch "dotnet <entry>.dll".
        string? managedPath = ResolveManagedEntryPath();
        if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath))
        {
            return LaunchTarget.Fail($"dotnet host detected, but managed entry .dll was not found: {managedPath ?? "<null>"}");
        }

        return LaunchTarget.Ok(hostPath, managedPath);
    }

    private static string? ResolveManagedEntryPath()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 0)
        {
            var arg0 = args[0];
            if (!string.IsNullOrWhiteSpace(arg0) &&
                arg0.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (Path.IsPathRooted(arg0))
                {
                    return arg0;
                }

                try
                {
                    return Path.GetFullPath(arg0);
                }
                catch
                {
                    // Ignore and try fallback below.
                }
            }
        }

        // Fallback for typical build outputs.
        var candidate = Path.Combine(AppContext.BaseDirectory, "NewAxis.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore termination errors.
        }
    }

    private static int SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private readonly struct LaunchAttempt
    {
        public bool Success { get; }
        public Process? Process { get; }
        public string? ErrorMessage { get; }

        private LaunchAttempt(bool success, Process? process, string? errorMessage)
        {
            Success = success;
            Process = process;
            ErrorMessage = errorMessage;
        }

        public static LaunchAttempt Ok(Process process) => new(true, process, null);
        public static LaunchAttempt Fail(string errorMessage) => new(false, null, errorMessage);
    }

    private readonly struct LaunchTarget
    {
        public bool Success { get; }
        public string? HostPath { get; }
        public string? ManagedAppPath { get; }
        public string? ErrorMessage { get; }

        private LaunchTarget(bool success, string? hostPath, string? managedAppPath, string? errorMessage)
        {
            Success = success;
            HostPath = hostPath;
            ManagedAppPath = managedAppPath;
            ErrorMessage = errorMessage;
        }

        public static LaunchTarget Ok(string hostPath, string? managedAppPath) => new(true, hostPath, managedAppPath, null);
        public static LaunchTarget Fail(string errorMessage) => new(false, null, null, errorMessage);
    }
}
