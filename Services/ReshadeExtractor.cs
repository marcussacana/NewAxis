using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

using NewAxis.Models; // Ensure we have Models namespace for GameIndexEntry

namespace NewAxis.Services
{
    /// <summary>
    /// Handles extraction of Reshade archives with architecture detection
    /// </summary>
    public class ReshadeExtractionContext
    {
        public string Reshade7zPath { get; set; } = string.Empty;
        public string TargetDirectory { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public GameIndexEntry GameEntry { get; set; } = new();
        public string? OverwatchPath { get; set; }
    }

    public class ReshadeExtractor
    {
        /// <summary>
        /// Extracts Reshade from a 7z archive to the game directory
        /// </summary>
        public static async Task<List<string>> ExtractReshadeAsync(ReshadeExtractionContext context)
        {
            if (!File.Exists(context.Reshade7zPath))
            {
                throw new FileNotFoundException($"Reshade archive not found: {context.Reshade7zPath}");
            }

            var installedFiles = new List<string>();

            // Determine full path to executable
            var fullExePath = Path.Combine(context.TargetDirectory, context.ExecutablePath);
            if (!File.Exists(fullExePath))
            {
                throw new FileNotFoundException($"Game executable not found: {fullExePath}");
            }

            // Detect architecture
            bool is64Bit = IsExecutable64Bit(fullExePath);
            string archFolder = is64Bit ? "x64" : "x32";

            Console.WriteLine($"[Reshade] Detected architecture: {archFolder}");
            Console.WriteLine($"[Reshade] Target DLL: {context.GameEntry.TargetDllFileName}");

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Reshade_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                // Extract archive
                using (var archive = ArchiveFactory.Open(context.Reshade7zPath))
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

                        using (var entryStream = entry.OpenEntryStream())
                        using (var fileStream = File.Create(extractPath))
                        {
                            entryStream.CopyTo(fileStream);
                        }
                    }
                }

                // Find the main Reshade DLL (typically named "reshade.dll" or similar)
                var dllFiles = Directory.GetFiles(tempExtractDir, "*.dll", SearchOption.TopDirectoryOnly);
                if (dllFiles.Length == 0)
                {
                    throw new Exception("No DLL files found in extracted Reshade archive");
                }

                // Copy files to game directory
                Directory.CreateDirectory(context.TargetDirectory);

                foreach (var srcDll in dllFiles)
                {
                    var fileName = Path.GetFileName(srcDll);
                    string targetPath;

                    // Rename the main DLL (typically the largest one or named reshade.dll)
                    if (fileName.Contains("reshade", StringComparison.OrdinalIgnoreCase) || dllFiles.Length == 1)
                    {
                        targetPath = Path.Combine(context.TargetDirectory, context.GameEntry.TargetDllFileName!);
                        Console.WriteLine($"[Reshade] Copying and renaming {fileName} -> {context.GameEntry.TargetDllFileName}");
                    }
                    else
                    {
                        // Keep original name for supporting DLLs
                        targetPath = Path.Combine(context.TargetDirectory, fileName);
                        Console.WriteLine($"[Reshade] Copying {fileName}");
                    }

                    File.Copy(srcDll, targetPath, true);
                    installedFiles.Add(targetPath);
                }

                // Copy any other non-DLL files (config, shaders, etc. if any)
                var otherFiles = Directory.GetFiles(tempExtractDir).Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                foreach (var srcFile in otherFiles)
                {
                    var fileName = Path.GetFileName(srcFile);
                    var targetPath = Path.Combine(context.TargetDirectory, fileName);
                    File.Copy(srcFile, targetPath, true);
                    installedFiles.Add(targetPath);
                }

                if (!string.IsNullOrEmpty(context.GameEntry.ReshadePresetPlus))
                {
                    var presetPath = Path.Combine(context.TargetDirectory, "ReShadePreset.ini");
                    var presetContent = GenerateReshadePresetIni(context.GameEntry.ReshadePresetPlus);
                    await File.WriteAllTextAsync(presetPath, presetContent);
                    installedFiles.Add(presetPath);
                }

                if (!string.IsNullOrEmpty(context.OverwatchPath) && File.Exists(context.OverwatchPath))
                {
                    var overwatchFiles = await ExtractOverwatchAsync(context.OverwatchPath, context.TargetDirectory);
                    installedFiles.AddRange(overwatchFiles);
                }

                UpdateReshadeIni(context.TargetDirectory, context.GameEntry.UseAspectRatioHeuristics, context.GameEntry.DepthCopyBeforeClears);

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

        /// <summary>
        /// Generates the ReShadePreset.ini content from preset data
        /// </summary>
        private static string GenerateReshadePresetIni(string presetData)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PreprocessorDefinitions = ");
            sb.AppendLine("Techniques = Depth3D_Acer@SpatialLabs_Depth3D.fx");
            sb.AppendLine("TechniqueSorting = Depth3D_Acer@SpatialLabs_Depth3D.fx");
            sb.AppendLine();
            sb.AppendLine("[SpatialLabs_Depth3D.fx]");
            sb.AppendLine(presetData);

            return sb.ToString();
        }

        /// <summary>
        /// Extracts Overwatch.fxh from a 7z archive (file has no name inside)
        /// </summary>
        private static async Task<List<string>> ExtractOverwatchAsync(string overwatch7zPath, string targetDirectory)
        {
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Overwatch_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);
            var extractedFiles = new List<string>();

            try
            {
                // Extract the 7z archive
                using (var archive = ArchiveFactory.Open(overwatch7zPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory);
                    if (entry == null)
                    {
                        throw new Exception("No file found in Overwatch archive");
                    }

                    // Extract to temp with any name
                    var tempFilePath = Path.Combine(tempExtractDir, "temp_file");
                    await Task.Run(() =>
                    {
                        using (var entryStream = entry.OpenEntryStream())
                        using (var fileStream = File.Create(tempFilePath))
                        {
                            entryStream.CopyTo(fileStream);
                        }
                    });

                    // Copy to target directory as Overwatch.fxh
                    var targetPath = Path.Combine(targetDirectory, "Overwatch.fxh");
                    File.Copy(tempFilePath, targetPath, overwrite: true);
                    extractedFiles.Add(targetPath);
                }
                return extractedFiles;
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

        private static void UpdateReshadeIni(string targetDirectory, long? heuristics, long? depthCopy)
        {
            var iniPath = Path.Combine(targetDirectory, "ReShade.ini");

            var parser = new IniFileParser();
            if (File.Exists(iniPath))
            {
                parser.Load(iniPath);
            }

            // [INSTALL] Section
            parser.SetValue("INSTALL", "BasePath", targetDirectory);

            // [GENERAL] Section
            parser.SetValue("GENERAL", "IsNative3D", "0");

            // [DEPTH] Section
            if (heuristics.HasValue) parser.SetValue("DEPTH", "UseAspectRatioHeuristics", heuristics.Value.ToString());
            if (depthCopy.HasValue) parser.SetValue("DEPTH", "DepthCopyBeforeClears", depthCopy.Value.ToString());



            parser.Save(iniPath);
        }
    }
}
