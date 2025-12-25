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
            Trace.WriteLine("Applying update...");

            int retries = 10;
            string currentExe = Environment.ProcessPath!;

            while (retries > 0)
            {
                try
                {
                    var currentDir = Path.GetDirectoryName(currentExe)!;

                    if (targetDir.TrimEnd('\\') == currentDir.TrimEnd('\\'))
                    {
                        Trace.WriteLine("Target and Source are same!");
                        return;
                    }

                    CopyDirectory(currentDir, targetDir);

                    string originalExePath = Path.Combine(targetDir, Path.GetFileName(currentExe));
                    Process.Start(originalExePath, $"--cleanup \"{currentDir}\"");

                    Environment.Exit(0);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(500);
                    retries--;
                    Trace.WriteLine($"Waiting for file locks... ({retries})");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Update failed: {ex.Message}");
                    Environment.Exit(1);
                }
            }
        }

        private static void PerformCleanup(string tempDir)
        {
            try
            {
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
                    catch { }
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


                await repoClient.DownloadFileAsync(downloadUrl, archivePath);


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


                string currentExe = Environment.ProcessPath!;
                string exeName = Path.GetFileName(currentExe);
                string newExePath = Path.Combine(extractPath, exeName);
                string currentDir = Path.GetDirectoryName(currentExe)!;

                if (!File.Exists(newExePath))
                {
                    var found = Directory.GetFiles(extractPath, exeName, SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) newExePath = found;
                    else throw new Exception("Executable not found in update package");
                }

                Process.Start(newExePath, $"--update-target \"{currentDir}\"");

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Update initiation failed: {ex.Message}");
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
