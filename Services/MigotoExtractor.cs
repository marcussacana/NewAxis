using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.OpenGL;
using SharpCompress.Archives;
using SharpCompress.Common;
using SixLabors.ImageSharp;
using ZstdSharp.Unsafe;

namespace NewAxis.Services
{
    /// <summary>
    /// Handles extraction of Migoto archives with JSON instruction support
    /// </summary>
    public class MigotoExtractor
    {
        /// <summary>
        /// Extracts Migoto from archive to the game directory, following JSON instructions if present
        /// </summary>
        /// <param name="migoto7zPath">Path to the Migoto archive file</param>
        /// <param name="gameInstallPath">Game installation directory</param>
        /// <param name="relativeExecutablePath">Relative path to executable within game dir</param>
        /// <returns>List of files that were created or modified</returns>
        public static async Task<List<string>> ExtractMigotoAsync(
            string migoto7zPath,
            string targetDirectory,
            string? executablePath = null)
        {
            if (!File.Exists(migoto7zPath))
            {
                throw new FileNotFoundException($"Migoto archive not found: {migoto7zPath}");
            }

            var installedFiles = new List<string>();

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Migoto_{Guid.NewGuid()}");
            var sourceSubDir = tempExtractDir;
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                Console.WriteLine("[Migoto] Extracting archive...");
                using (var archive = ArchiveFactory.Open(migoto7zPath))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        await Task.Run(() =>
                        {
                            var extractPath = Path.Combine(sourceSubDir, entry.Key!);
                            var dir = Path.GetDirectoryName(extractPath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            using (var entryStream = entry.OpenEntryStream())
                            using (var fileStream = File.Create(extractPath))
                            {
                                entryStream.CopyTo(fileStream);
                            }
                        });
                    }
                }


                var jsonInstructionsPath = Directory.GetFiles(sourceSubDir, "*.json", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                var subDir = Directory.GetDirectories(sourceSubDir);

                if (subDir.All(x => new[] { "x64", "x32" }.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)) && subDir.Any())
                {
                    Console.WriteLine("[Migoto] Found architecture-specific subdirectories (x64/x32)");


                    if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
                    {
                        Console.WriteLine("[Migoto] Specific executable path not provided or not found, scanning directory...");
                        executablePath = Directory.GetFiles(targetDirectory, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                    }

                    if (executablePath is null || !File.Exists(executablePath))
                    {
                        throw new FileNotFoundException($"Target executable not found in: {targetDirectory}");
                    }


                    var is64Bit = Is64bitExecutable(executablePath);
                    var archSubDir = is64Bit ? "x64" : "x32";
                    Console.WriteLine($"[Migoto] Detected {(is64Bit ? "64-bit" : "32-bit")} executable, using {archSubDir} subdirectory");


                    sourceSubDir = subDir.FirstOrDefault(x => string.Equals(Path.GetFileName(x), archSubDir, StringComparison.OrdinalIgnoreCase));

                    if (sourceSubDir == null)
                    {
                        throw new DirectoryNotFoundException($"Could not find {archSubDir} subdirectory in Migoto archive");
                    }
                }

                if (jsonInstructionsPath != null)
                {
                    Console.WriteLine($"[Migoto] Found instruction file: {Path.GetFileName(jsonInstructionsPath)}");
                    installedFiles = await ApplyJsonInstructionsAsync(jsonInstructionsPath, sourceSubDir, targetDirectory);
                }
                else
                {

                    Console.WriteLine("[Migoto] No JSON instructions found, copying all files...");
                    installedFiles = await CopyAllFilesAsync(sourceSubDir, targetDirectory);
                }

                if (installedFiles.Any(x => Path.GetFileName(x) == "nvapi64.dll"))
                {
                    Console.WriteLine("[Migoto] Found nvapi64.dll, checking for AMD GPU...");

                    if (GpuUtils.IsAmdGpu())
                    {
                        Console.WriteLine("[Migoto] AMD GPU detected. Applying AMD fix to nvapi64.dll...");

                        var fullNvapiPath = installedFiles.First(x => Path.GetFileName(x) == "nvapi64.dll");
                        installedFiles.AddRange(await ApplyAMDfix(fullNvapiPath));
                    }
                }

                Console.WriteLine("[Migoto] Extraction complete!");
                return installedFiles;
            }
            finally
            {

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

        private static async Task<string[]> ApplyAMDfix(string fullNvapiPath)
        {
            List<string> installedFiles = new();
            try
            {
                var nvapiDir = Path.GetDirectoryName(fullNvapiPath);
                if (!string.IsNullOrEmpty(nvapiDir))
                {
                    using (var ms = new MemoryStream(Binary.AMDFix))
                    using (var archive = ArchiveFactory.Open(ms))
                    {
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                        {
                            await entry.WriteToDirectoryAsync(nvapiDir, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                        }

                        installedFiles.AddRange(archive.Entries.Select(e => Path.Combine(nvapiDir, e.Key!)));
                    }
                    Console.WriteLine("[Migoto] AMD fix applied successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Migoto] Failed to apply AMD fix: {ex.Message}");
            }

            return installedFiles.ToArray();
        }

        private static async Task<List<string>> ApplyJsonInstructionsAsync(
            string jsonPath,
            string sourceDir,
            string targetDirectory)
        {
            var installedFiles = new List<string>();
            var jsonContent = await File.ReadAllTextAsync(jsonPath);
            var instructions = JsonSerializer.Deserialize(jsonContent, AppJsonContext.Default.MigotoInstructions);

            if (instructions?.Files == null || instructions.Files.Count == 0)
            {
                Console.WriteLine("[Migoto] No file instructions in JSON");
                return await CopyAllFilesAsync(sourceDir, targetDirectory);
            }

            Directory.CreateDirectory(targetDirectory);

            Console.WriteLine($"[Migoto] Processing {instructions.Files.Count} file instructions...");

            foreach (var fileInstruction in instructions.Files)
            {
                if (string.IsNullOrEmpty(fileInstruction.Source))
                {
                    Console.WriteLine("[Migoto] Skipping instruction with empty source");
                    continue;
                }

                var sourcePath = Path.Combine(sourceDir, fileInstruction.Source);
                if (!File.Exists(sourcePath))
                {
                    Console.WriteLine($"[Migoto] Warning: Source file not found: {fileInstruction.Source}");
                    continue;
                }


                var targetNames = new List<string>();
                if (!string.IsNullOrEmpty(fileInstruction.Target))
                {
                    targetNames.Add(fileInstruction.Target);
                }
                if (fileInstruction.AdditionalTargets != null)
                {
                    targetNames.AddRange(fileInstruction.AdditionalTargets);
                }


                if (targetNames.Count == 0)
                {
                    targetNames.Add(Path.GetFileName(fileInstruction.Source));
                }


                foreach (var targetName in targetNames)
                {
                    var targetPath = Path.Combine(targetDirectory, targetName);


                    if (File.Exists(targetPath))
                    {
                        var backupPath = targetPath + ".disabled";
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(targetPath, backupPath, overwrite: false);
                            Console.WriteLine($"[Migoto] Created backup: {Path.GetFileName(backupPath)}");
                        }
                    }

                    File.Copy(sourcePath, targetPath, overwrite: true);
                    Console.WriteLine($"[Migoto] {fileInstruction.Source} -> {targetName}");
                    installedFiles.Add(targetPath);
                }
            }

            return installedFiles;
        }

        private static async Task<List<string>> CopyAllFilesAsync(string sourceDir, string targetDirectory)
        {
            var installedFiles = new List<string>();
            Directory.CreateDirectory(targetDirectory);

            var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
            Console.WriteLine($"[Migoto] Copying {allFiles.Length} files...");

            foreach (var file in allFiles)
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var targetPath = Path.Combine(targetDirectory, relativePath);

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }


                if (File.Exists(targetPath))
                {
                    var backupPath = targetPath + ".disabled";
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(targetPath, backupPath, overwrite: false);
                    }
                }

                var info = new FileInfo(targetPath);

                if (info.Exists && info.IsReadOnly)
                {
                    info.IsReadOnly = false;
                }

                await Task.Run(() => File.Copy(file, targetPath, overwrite: true));
                installedFiles.Add(targetPath);
            }

            return installedFiles;
        }

        /// <summary>
        /// Detects if an executable is 64-bit or 32-bit by reading its PE header
        /// </summary>
        private static bool Is64bitExecutable(string executablePath)
        {
            try
            {
                using (var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Seek(0x3C, SeekOrigin.Begin);
                    var peHeaderOffset = reader.ReadInt32();


                    stream.Seek(peHeaderOffset, SeekOrigin.Begin);
                    var peSignature = reader.ReadUInt32();

                    if (peSignature != 0x00004550)
                    {
                        throw new InvalidDataException("Invalid PE signature");
                    }


                    var machineType = reader.ReadUInt16();

                    return machineType == 0x8664;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Migoto] Failed to detect executable architecture: {ex.Message}");
                return false;
            }
        }


    }

    // JSON Instruction Models
    public class MigotoInstructions
    {
        public List<FileInstruction>? Files { get; set; }
    }

    public class FileInstruction
    {
        public string? Source { get; set; }
        public string? Target { get; set; }
        public List<string>? AdditionalTargets { get; set; }
    }
}
