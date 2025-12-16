using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace NewAxis.Services
{
    /// <summary>
    /// Handles extraction of Reshade archives with architecture detection
    /// </summary>
    public class ReshadeExtractor
    {
        /// <summary>
        /// Extracts Reshade from a 7z archive to the game directory
        /// </summary>
        /// <param name="reshade7zPath">Path to the Reshade 7z file</param>
        /// <param name="gameInstallPath">Game installation directory</param>
        /// <param name="executablePath">Executable filename (e.g., "GoW.exe")</param>
        /// <param name="relativeExecutablePath">Relative path to executable within game dir</param>
        /// <param name="targetDllFileName">Target DLL filename (e.g., "dxgi.dll")</param>
        /// <returns>List of files that were created or modified</returns>
        public static async Task<List<string>> ExtractReshadeAsync(
            string reshade7zPath,
            string targetDirectory,
            string executablePath,
            string targetDllFileName)
        {
            if (!File.Exists(reshade7zPath))
            {
                throw new FileNotFoundException($"Reshade archive not found: {reshade7zPath}");
            }

            var installedFiles = new List<string>();

            // Determine full path to executable
            var fullExePath = Path.Combine(targetDirectory, executablePath);
            if (!File.Exists(fullExePath))
            {
                throw new FileNotFoundException($"Game executable not found: {fullExePath}");
            }

            // Detect architecture
            bool is64Bit = IsExecutable64Bit(fullExePath);
            string archFolder = is64Bit ? "x64" : "x32";

            Console.WriteLine($"[Reshade] Detected architecture: {archFolder}");
            Console.WriteLine($"[Reshade] Target DLL: {targetDllFileName}");

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Reshade_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                // Extract archive
                using (var archive = ArchiveFactory.Open(reshade7zPath))
                {
                    var archFolderPrefix = $"{archFolder}/";
                    var filesToExtract = archive.Entries
                        .Where(e => !e.IsDirectory && e.Key != null && e.Key.StartsWith(archFolderPrefix))
                        .ToList();

                    if (filesToExtract.Count == 0)
                    {
                        throw new Exception($"No files found in {archFolder} folder of Reshade archive");
                    }

                    Console.WriteLine($"[Reshade] Extracting {filesToExtract.Count} files from {archFolder}...");

                    foreach (var entry in filesToExtract)
                    {
                        var relativePath = entry.Key!.Substring(archFolderPrefix.Length);
                        var extractPath = Path.Combine(tempExtractDir, relativePath);

                        var dir = Path.GetDirectoryName(extractPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        await Task.Run(() =>
                        {
                            using (var entryStream = entry.OpenEntryStream())
                            using (var fileStream = File.Create(extractPath))
                            {
                                entryStream.CopyTo(fileStream);
                            }
                        });
                    }
                }

                // Find the main Reshade DLL (typically named "reshade.dll" or similar)
                var dllFiles = Directory.GetFiles(tempExtractDir, "*.dll", SearchOption.TopDirectoryOnly);
                if (dllFiles.Length == 0)
                {
                    throw new Exception("No DLL files found in extracted Reshade archive");
                }

                // Copy files to game directory
                Directory.CreateDirectory(targetDirectory);

                foreach (var srcDll in dllFiles)
                {
                    var fileName = Path.GetFileName(srcDll);
                    string targetPath;

                    // Rename the main DLL (typically the largest one or named reshade.dll)
                    if (fileName.Contains("reshade", StringComparison.OrdinalIgnoreCase) || dllFiles.Length == 1)
                    {
                        targetPath = Path.Combine(targetDirectory, targetDllFileName);
                        Console.WriteLine($"[Reshade] Copying and renaming {fileName} -> {targetDllFileName}");
                    }
                    else
                    {
                        // Keep original name for supporting DLLs
                        targetPath = Path.Combine(targetDirectory, fileName);
                        Console.WriteLine($"[Reshade] Copying {fileName}");
                    }

                    // Create backup if file exists (never overwrite .disabled backups)
                    if (File.Exists(targetPath))
                    {
                        var backupPath = targetPath + ".disabled";
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(targetPath, backupPath, overwrite: false);
                            Console.WriteLine($"[Reshade] Created backup: {Path.GetFileName(backupPath)}");
                        }
                    }

                    File.Copy(srcDll, targetPath, overwrite: true);
                    installedFiles.Add(targetPath);
                }

                Console.WriteLine("[Reshade] Extraction complete!");
                return installedFiles;
            }
            finally
            {
                // Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempExtractDir))
                    {
                        Directory.Delete(tempExtractDir, recursive: true);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Determines if an executable is 64-bit by reading its PE header
        /// </summary>
        private static bool IsExecutable64Bit(string exePath)
        {
            using (var stream = File.OpenRead(exePath))
            using (var peReader = new PEReader(stream))
            {
                var headers = peReader.PEHeaders;
                return headers.PEHeader != null && headers.PEHeader.Magic == PEMagic.PE32Plus;
            }
        }
    }
}
