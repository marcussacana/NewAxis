using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

using NewAxis.Models;
using System.Text;
using System.Diagnostics; // Ensure we have Models namespace for GameIndexEntry

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
                if (!File.Exists(fullExePath))
                {
                    if (File.Exists(context.ExecutablePath)) fullExePath = context.ExecutablePath;
                    else throw new FileNotFoundException($"Game executable not found: {fullExePath}");
                }
            }

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Reshade_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                await ExtractArchiveByArchitectureAsync(context.Reshade7zPath, fullExePath, tempExtractDir);

                var extractedFiles = InstallExtractedFiles(tempExtractDir, context.TargetDirectory, context.GameEntry.TargetDllFileName ?? "dxgi.dll");
                installedFiles.AddRange(extractedFiles);

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

                UpdateReshadeIni(context.GameEntry, context.TargetDirectory, false);

                return installedFiles;
            }
            finally
            {
                CleanupTempDir(tempExtractDir);
            }
        }

        /// <summary>
        /// Extracts Native Reshade from archive and renames DLL based on architecture
        /// </summary>
        public static async Task<List<string>> ExtractNativeReshadeAsync(ReshadeExtractionContext context)
        {
            if (!File.Exists(context.Reshade7zPath))
            {
                throw new FileNotFoundException($"Native Reshade archive not found: {context.Reshade7zPath}");
            }

            var installedFiles = new List<string>();

            // Determine full path to executable
            // For native reshade, usually we pass the full path or relative.
            var fullExePath = Path.IsPathRooted(context.ExecutablePath) ? context.ExecutablePath : Path.Combine(context.TargetDirectory, context.ExecutablePath);

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"NativeReshade_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                await ExtractArchiveByArchitectureAsync(context.Reshade7zPath, fullExePath, tempExtractDir);

                // Use NativeReshadeDll from GameEntry if available, otherwise default or inferred?
                // The caller typically sets it.
                var targetDll = context.GameEntry.NativeReshadeDll ?? "dxgi.dll";

                var extractedFiles = InstallExtractedFiles(tempExtractDir, context.TargetDirectory, targetDll);
                installedFiles.AddRange(extractedFiles);

                UpdateReshadeIni(context.GameEntry, context.TargetDirectory, true);

                return installedFiles;
            }
            finally
            {
                CleanupTempDir(tempExtractDir);
            }
        }

        private static async Task ExtractArchiveByArchitectureAsync(string archivePath, string exePath, string outputDir)
        {
            // Detect architecture
            bool is64Bit = IsExecutable64Bit(exePath);
            string archFolder = is64Bit ? "x64" : "x32";
            string archFolderPrefix = $"{archFolder}/";

            Trace.WriteLine($"[ReshadeManager] Detected architecture: {archFolder}");

            await Task.Run(() =>
            {
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    var filesToExtract = archive.Entries
                        .Where(e => !e.IsDirectory && e.Key != null && e.Key.StartsWith(archFolderPrefix))
                        .ToList();

                    if (filesToExtract.Count == 0)
                    {
                        throw new Exception($"No files found in {archFolder} folder of archive");
                    }

                    Trace.WriteLine($"[ReshadeManager] Extracting {filesToExtract.Count} files from {archFolder}...");

                    foreach (var entry in filesToExtract)
                    {
                        var relativePath = entry.Key!.Substring(archFolderPrefix.Length);
                        var extractPath = Path.Combine(outputDir, relativePath);

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
            });
        }

        private static List<string> InstallExtractedFiles(string sourceDir, string targetDir, string targetDllName)
        {
            var installedFiles = new List<string>();

            // Find the main Reshade DLL
            var dllFiles = Directory.GetFiles(sourceDir, "*.dll", SearchOption.TopDirectoryOnly);
            if (dllFiles.Length == 0)
            {
                throw new Exception("No DLL files found in extracted archive");
            }

            Directory.CreateDirectory(targetDir);

            foreach (var srcDll in dllFiles)
            {
                var fileName = Path.GetFileName(srcDll);
                string targetPath;

                // Rename the main DLL (typically named reshade.dll)
                if (fileName.Contains("reshade", StringComparison.OrdinalIgnoreCase) || dllFiles.Length == 1)
                {
                    targetPath = Path.Combine(targetDir, targetDllName);
                    Trace.WriteLine($"[ReshadeManager] Copying and renaming {fileName} -> {targetDllName}");
                }
                else
                {
                    // Keep original name for supporting DLLs
                    targetPath = Path.Combine(targetDir, fileName);
                    Trace.WriteLine($"[ReshadeManager] Copying {fileName}");
                }

                File.Copy(srcDll, targetPath, true);
                installedFiles.Add(targetPath);
            }

            // Copy any other non-DLL files
            var otherFiles = Directory.GetFiles(sourceDir).Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            foreach (var srcFile in otherFiles)
            {
                var fileName = Path.GetFileName(srcFile);
                var targetPath = Path.Combine(targetDir, fileName);
                File.Copy(srcFile, targetPath, true);
                installedFiles.Add(targetPath);
            }

            return installedFiles;
        }

        private static void CleanupTempDir(string tempDir)
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { }
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

        private static List<string> UpdateReshadeIni(GameIndexEntry gameEntry, string targetDirectory, bool isNative)
        {
            var heuristics = gameEntry.UseAspectRatioHeuristics;
            var depthCopy = gameEntry.DepthCopyBeforeClears;

            var files = new List<string>();

            var iniPath = Path.Combine(targetDirectory, "ReShade.ini");

            var parser = new IniFileParser();
            if (File.Exists(iniPath))
            {
                parser.Load(iniPath);
            }

            // [INSTALL] Section
            parser.SetValue("INSTALL", "BasePath", targetDirectory);

            // [GENERAL] Section
            parser.SetValue("GENERAL", "IsNative3D", isNative ? "1" : "0");

            if (isNative)
            {
                var presetContent = "PreprocessorDefinitions = \r\nTechniques = Stereo_Format_Converter@SpatialLabs_Native3D.fx\r\nTechniqueSorting = Stereo_Format_Converter@SpatialLabs_Native3D.fx\r\n\r\n[SpatialLabs_Native3D.fx]\r\n";
                var presetPath = Path.Combine(targetDirectory, "ReShadePreset.ini");

                if (gameEntry.ReshadePresetNative != null)
                {
                    presetContent += gameEntry.ReshadePresetNative;
                }
                else
                {
                    presetContent += "Stereoscopic_Mode_Input = 1";
                }

                File.WriteAllText(presetPath, presetContent, Encoding.UTF8);
                files.Add(presetPath);
            }
            else
            {
                // [DEPTH] Section
                if (heuristics.HasValue) parser.SetValue("DEPTH", "UseAspectRatioHeuristics", heuristics.Value.ToString());
                if (depthCopy.HasValue) parser.SetValue("DEPTH", "DepthCopyBeforeClears", depthCopy.Value.ToString());
            }

            parser.Save(iniPath);

            return files;
        }
    }
}
