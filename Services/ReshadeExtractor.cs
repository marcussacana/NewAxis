using System;
using System.Collections.Generic;
using System.Diagnostics; // Ensure we have Models namespace for GameIndexEntry
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using NewAxis.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

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
        public string? ShaderPath { get; set; }
    }

    public class ReshadeExtractor
    {
        private static readonly string[] ObsoleteFilePatterns = { "SpatialLabs", "Acer", "slt", "Depth3D" };

        public static bool IsObsoleteReshadeFile(string fileName)
        {
            return ObsoleteFilePatterns.Any(p => fileName.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Extracts Reshade from a 7z archive to the game directory
        /// </summary>
        public static async Task<List<string>> ExtractReshadeAsync(ReshadeExtractionContext context, Action? onInstalled = null)
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

                var extractedFiles = InstallExtractedFiles(tempExtractDir, context.TargetDirectory, context.GameEntry.TargetDllFileName ?? "dxgi.dll", true, onInstalled);
                installedFiles.AddRange(extractedFiles);

                var presetPath = Path.Combine(context.TargetDirectory, "ReShadePreset.ini");
                var presetContent = GenerateReshadePresetIni(context.GameEntry.ReshadePresetPlus);
                await File.WriteAllTextAsync(presetPath, presetContent, Encoding.UTF8);
                installedFiles.Add(presetPath);
                onInstalled?.Invoke();

                if (!string.IsNullOrEmpty(context.ShaderPath) && File.Exists(context.ShaderPath))
                {
                    var shaderFiles = await ExtractShaderAsync(context.ShaderPath, context.TargetDirectory, onInstalled);
                    installedFiles.AddRange(shaderFiles);
                }

                var iniFiles = UpdateReshadeIni(context.GameEntry, context.TargetDirectory, false);
                installedFiles.AddRange(iniFiles);
                foreach (var _ in iniFiles)
                {
                    onInstalled?.Invoke();
                }

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
        public static async Task<List<string>> ExtractNativeReshadeAsync(ReshadeExtractionContext context, Action? onInstalled = null)
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

                var extractedFiles = InstallExtractedFiles(tempExtractDir, context.TargetDirectory, targetDll, false, onInstalled);
                installedFiles.AddRange(extractedFiles);

                var iniFiles = UpdateReshadeIni(context.GameEntry, context.TargetDirectory, true);
                installedFiles.AddRange(iniFiles);
                foreach (var _ in iniFiles)
                {
                    onInstalled?.Invoke();
                }

                return installedFiles;
            }
            finally
            {
                CleanupTempDir(tempExtractDir);
            }
        }

        /// <summary>
        /// Extracts only the ReShade runtime files without generating presets or ini files.
        /// </summary>
        public static async Task<List<string>> ExtractRuntimeOnlyAsync(ReshadeExtractionContext context, string targetDllName, Action? onInstalled = null)
        {
            if (!File.Exists(context.Reshade7zPath))
            {
                throw new FileNotFoundException($"ReShade runtime archive not found: {context.Reshade7zPath}");
            }

            var fullExePath = Path.IsPathRooted(context.ExecutablePath) ? context.ExecutablePath : Path.Combine(context.TargetDirectory, context.ExecutablePath);
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"ReshadeRuntime_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                await ExtractArchiveByArchitectureAsync(context.Reshade7zPath, fullExePath, tempExtractDir);
                return InstallExtractedFiles(tempExtractDir, context.TargetDirectory, targetDllName, true, onInstalled);
            }
            finally
            {
                CleanupTempDir(tempExtractDir);
            }
        }

        public static void ExtractArchiveByArchitecturePreview(string archivePath, string exePath, string outputDir)
        {
            ExtractArchiveByArchitectureAsync(archivePath, exePath, outputDir).GetAwaiter().GetResult();
        }

        private static async Task ExtractArchiveByArchitectureAsync(string archivePath, string exePath, string outputDir)
        {
            // Detect architecture
            bool is64Bit = IsExecutable64Bit(exePath);
            string archFolder = is64Bit ? "x64" : "x32";
            string archFolderPrefix = $"{archFolder}/";

            Trace.WriteLine($"[ReshadeManager] Detecting files for architecture: {archFolder}");

            await Task.Run(() =>
            {
                using (var archive = ArchiveFactory.Open(archivePath))
                {
                    Trace.WriteLine($"[ReshadeManager] Archive opened: {archivePath}");
                    Trace.WriteLine($"[ReshadeManager] Entries count: {archive.Entries.Count()}");

                    string normalizedPrefix = archFolderPrefix.Replace('\\', '/').ToLowerInvariant();
                    Trace.WriteLine($"[ReshadeManager] Searching for prefix: {normalizedPrefix}");

                    // Try to find files in the x64/x32 folders first
                    var filesToExtract = archive.Entries
                        .Where(e => !e.IsDirectory && e.Key != null)
                        .Where(e => e.Key.Replace('\\', '/').ToLowerInvariant().StartsWith(normalizedPrefix))
                        .ToList();

                    bool foundInFolder = filesToExtract.Any();

                    if (!foundInFolder)
                    {
                        Trace.WriteLine($"[ReshadeManager] {archFolder} folder not found in archive. Searching root for architecture-specific files...");

                        // If folders are missing, we look for ReShade64.dll/ReShade32.dll at the root
                        // and take all files that aren't other architecture's specific files
                        var otherDllMatch = is64Bit ? "32" : "64";

                        filesToExtract = archive.Entries
                            .Where(e => !e.IsDirectory && e.Key != null)
                            .Where(e => !e.Key.Contains('/') && !e.Key.Contains('\\')) // Only root files
                            .Where(e => !e.Key.Contains(otherDllMatch)) // Exclude other architecture files
                            .ToList();
                    }

                    if (filesToExtract.Count == 0)
                    {
                        throw new Exception($"Could not find any suitable ReShade files for {archFolder} in the archive.");
                    }

                    Trace.WriteLine($"[ReshadeManager] Found {filesToExtract.Count} files to extract.");

                    foreach (var entry in filesToExtract)
                    {
                        string entryKey = entry.Key!;
                        string relativePath;

                        if (foundInFolder)
                        {
                            // Remove prefix more safely
                            string normalizedKey = entryKey.Replace('\\', '/');
                            if (normalizedKey.ToLowerInvariant().StartsWith(normalizedPrefix))
                            {
                                relativePath = entryKey.Substring(archFolderPrefix.Length).TrimStart('/', '\\');
                            }
                            else
                            {
                                relativePath = entryKey;
                            }
                        }
                        else
                        {
                            relativePath = entryKey;
                        }

                        var extractPath = Path.Combine(outputDir, relativePath);

                        var dir = Path.GetDirectoryName(extractPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        Trace.WriteLine($"[ReshadeManager] Extracting: {entryKey} -> {relativePath}");
                        using (var entryStream = entry.OpenEntryStream())
                        using (var fileStream = File.Create(extractPath))
                        {
                            entryStream.CopyTo(fileStream);
                        }
                    }
                }
            });
        }

        private static List<string> InstallExtractedFiles(string sourceDir, string targetDir, string targetDllName, bool excludeSpatialLabs = false, Action? onInstalled = null)
        {
            var installedFiles = new List<string>();

            // Find all files recursively in the temp directory to ensure we don't miss anything
            var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
            Trace.WriteLine($"[ReshadeManager] Found {allFiles.Length} extracted files in temp directory:");
            foreach (var f in allFiles) Trace.WriteLine($"[ReshadeManager]   - {f}");

            Directory.CreateDirectory(targetDir);

            // Separate DLLs and other files
            var dllFiles = allFiles.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(f => !excludeSpatialLabs || !ObsoleteFilePatterns.Any(p => Path.GetFileName(f).Contains(p, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var otherFiles = allFiles.Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(f => !excludeSpatialLabs || !ObsoleteFilePatterns.Any(p => Path.GetFileName(f).Contains(p, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (dllFiles.Length == 0)
            {
                throw new Exception("No DLL files found in extracted archive");
            }

            // Handle DLLs
            foreach (var srcDll in dllFiles)
            {
                var fileName = Path.GetFileName(srcDll);
                string targetPath;

                // Rename the main DLL (typically named ReShade64.dll or ReShade32.dll now)
                if (fileName.Contains("reshade", StringComparison.OrdinalIgnoreCase) || dllFiles.Length == 1)
                {
                    targetPath = Path.Combine(targetDir, targetDllName);
                    Trace.WriteLine($"[ReshadeManager] Installing main DLL: {fileName} -> {targetDllName}");
                }
                else
                {
                    // Keep original name for supporting DLLs
                    targetPath = Path.Combine(targetDir, fileName);
                    Trace.WriteLine($"[ReshadeManager] Installing supporting DLL: {fileName}");
                }

                File.Copy(srcDll, targetPath, true);
                installedFiles.Add(targetPath);
                onInstalled?.Invoke();
            }

            // Handle Other Files
            foreach (var srcFile in otherFiles)
            {
                var fileName = Path.GetFileName(srcFile);
                var targetPath = Path.Combine(targetDir, fileName);

                Trace.WriteLine($"[ReshadeManager] Installing file: {fileName}");
                File.Copy(srcFile, targetPath, true);
                installedFiles.Add(targetPath);
                onInstalled?.Invoke();
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
        private static string GenerateReshadePresetIni(string? presetData)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Techniques=Rendepth@Rendepth.fx");
            sb.AppendLine();
            sb.AppendLine("[Rendepth.fx]");

            if (string.IsNullOrWhiteSpace(presetData))
            {
                sb.AppendLine("showDepth=0");
                sb.AppendLine("stereoDepth=50.000000");
                sb.AppendLine("stereoMode=2");
                sb.AppendLine("stereoOffset=50.000000");
                sb.AppendLine("stereoStrength=50.000000");
                sb.AppendLine("swapLeftRight=0");
            }
            else
            {
                sb.AppendLine(presetData.Trim());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts Shader from a 7z archive (file has no name inside)
        /// </summary>
        private static async Task<List<string>> ExtractShaderAsync(string shader7zPath, string targetDirectory, Action? onInstalled = null)
        {
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Shader_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);
            var extractedFiles = new List<string>();

            try
            {
                // Extract the 7z archive
                using (var archive = ArchiveFactory.Open(shader7zPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory);
                    if (entry == null)
                    {
                        throw new Exception("No file found in Shader archive");
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

                    // Copy to target directory as Shader.fxh
                    var targetPath = Path.Combine(targetDirectory, "Shader.fxh");
                    File.Copy(tempFilePath, targetPath, overwrite: true);
                    extractedFiles.Add(targetPath);
                    onInstalled?.Invoke();
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

                // [3DGameBridge.addon] Section - Ensure it's enabled if present
                parser.SetValue("3DGameBridge.addon", "Enabled", "1");
            }

            parser.Save(iniPath);

            return files;
        }
    }
}
