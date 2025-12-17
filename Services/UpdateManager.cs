using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;

namespace NewAxis.Services
{
    public static class UpdateManager
    {
        private const string UPDATE_TEMP_DIR_NAME = "UpdateTemp";

        /// <summary>
        /// Handles self-update arguments on startup. Returns true if application should exit (maintenance mode).
        /// </summary>
        public static bool HandleUpdateArgs(string[] args)
        {
            if (args.Contains("--update-target"))
            {
                var targetDir = GetArgValue(args, "--update-target");
                if (!string.IsNullOrEmpty(targetDir))
                {
                    ApplyUpdate(targetDir);
                    return true;
                }
            }

            if (args.Contains("--cleanup"))
            {
                var tempDir = GetArgValue(args, "--cleanup");
                if (!string.IsNullOrEmpty(tempDir))
                {
                    PerformCleanup(tempDir);
                    // Continue to normal startup
                    return false;
                }
            }

            return false;
        }

        private static string? GetArgValue(string[] args, string key)
        {
            int index = Array.IndexOf(args, key);
            if (index >= 0 && index < args.Length - 1)
            {
                return args[index + 1];
            }
            return null;
        }

        private static void ApplyUpdate(string targetDir)
        {
            // We are running from Temp dir. TargetDir is the original installation.

            // 1. Wait for original process to exit
            // A simple retry loop usually suffices if the OS lock is released quickly.
            // But ideally we should pass the PID. For simplicity we just retry.
            Console.WriteLine("Applying update...");

            int retries = 10;
            string currentExe = Environment.ProcessPath!;

            while (retries > 0)
            {
                try
                {
                    // Copy all files from Current Dir to Target Dir
                    var currentDir = Path.GetDirectoryName(currentExe)!;

                    if (targetDir.TrimEnd('\\') == currentDir.TrimEnd('\\'))
                    {
                        // Should not happen unless arguments messed up
                        Console.WriteLine("Target and Source are same!");
                        return;
                    }

                    CopyDirectory(currentDir, targetDir);

                    // Launch updated app
                    string originalExePath = Path.Combine(targetDir, Path.GetFileName(currentExe));
                    Process.Start(originalExePath, $"--cleanup \"{currentDir}\"");

                    Environment.Exit(0);
                    return;
                }
                catch (IOException)
                {
                    // File locked?
                    Thread.Sleep(500);
                    retries--;
                    Console.WriteLine($"Waiting for file locks... ({retries})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Update failed: {ex.Message}");
                    Environment.Exit(1);
                }
            }
        }

        private static void PerformCleanup(string tempDir)
        {
            try
            {
                // Delete the temp directory
                // Give the temp process a moment to die
                // We can't blocking wait too long or UI delays.
                // Fire and forget task?
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    try
                    {
                        if (Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch { /* Ignore cleanup errors */ }
                });
            }
            catch { }
        }

        public static async Task PerformUpdateAsync(string downloadUrl, GameRepositoryClient repoClient)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "NewAxisUpdate");
                if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
                Directory.CreateDirectory(tempPath);

                var archivePath = Path.Combine(tempPath, "update.7z");
                var extractPath = Path.Combine(tempPath, "Extracted");
                Directory.CreateDirectory(extractPath);

                // Download
                await repoClient.DownloadFileAsync(downloadUrl, archivePath);

                // Extract
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    {
                        await entry.WriteToDirectoryAsync(extractPath, new SharpCompress.Common.ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }

                // Launch extracted executable
                string currentExe = Environment.ProcessPath!;
                string exeName = Path.GetFileName(currentExe);
                string newExePath = Path.Combine(extractPath, exeName);
                string currentDir = Path.GetDirectoryName(currentExe)!;

                // Usually extraction structure might be flat or nested depending on archive.
                // Assuming flat for simplicity or same structure.
                if (!File.Exists(newExePath))
                {
                    // Try finding it?
                    var found = Directory.GetFiles(extractPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) newExePath = found;
                    else throw new Exception("Executable not found in update package");
                }

                Process.Start(newExePath, $"--update-target \"{currentDir}\"");

                // Exit current app
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update initiation failed: {ex.Message}");
                throw;
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }
    }
}
