using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace NewAxis.Services
{
    /// <summary>
    /// Handles extraction of Game Config archives with JSON instruction support
    /// </summary>
    public class ConfigExtractor
    {
        /// <summary>
        /// Extracts Config archive to the game directory
        /// </summary>
        public static async Task<List<string>> ExtractConfigAsync(
            string config7zPath,
            string targetDirectory)
        {
            if (!File.Exists(config7zPath))
            {
                throw new FileNotFoundException($"Config archive not found: {config7zPath}");
            }

            var installedFiles = new List<string>();

            // Extract to temp directory first
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Config_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                // Extract entire archive
                Console.WriteLine("[Config] Extracting archive...");
                using (var archive = ArchiveFactory.Open(config7zPath))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        await Task.Run(() =>
                        {
                            var extractPath = Path.Combine(tempExtractDir, entry.Key!);
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

                // Look for JSON instructions file or "T" file
                var allFiles = Directory.GetFiles(tempExtractDir, "*", SearchOption.TopDirectoryOnly);
                var jsonInstructionsPath = allFiles.FirstOrDefault(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                           ?? allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("T", StringComparison.OrdinalIgnoreCase));

                if (jsonInstructionsPath != null)
                {
                    Console.WriteLine($"[Config] Found instruction file: {Path.GetFileName(jsonInstructionsPath)}");
                    installedFiles = await ApplyJsonInstructionsAsync(jsonInstructionsPath, tempExtractDir, targetDirectory);
                }
                else
                {
                    // No JSON instructions, copy all files directly
                    Console.WriteLine("[Config] No JSON instructions found, copying all files...");
                    installedFiles = await CopyAllFilesAsync(tempExtractDir, targetDirectory);
                }

                Console.WriteLine($"[Config] Extraction complete! {installedFiles.Count} files installed.");
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

        private static async Task<List<string>> ApplyJsonInstructionsAsync(
            string jsonPath,
            string sourceDir,
            string targetDirectory)
        {
            var installedFiles = new List<string>();
            var jsonContent = await File.ReadAllTextAsync(jsonPath);

            // Try parsing as List<Root> (New T format)
            try
            {
                var rootList = JsonSerializer.Deserialize(jsonContent, AppJsonContext.Default.ListRoot);
                if (rootList != null && rootList.Count > 0)
                {
                    Console.WriteLine("[Config] Found valid T configuration definitions. Processing instructions.");

                    // Process Default Presets
                    foreach (var root in rootList.Where(x => x != null))
                    {
                        if (!string.IsNullOrEmpty(root.DefaultPreset) && root.ConfigFilePaths != null)
                        {
                            var configPathEntry = root.ConfigFilePaths.FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.Path));
                            if (configPathEntry != null && configPathEntry.Path != null)
                            {
                                var sourcePresetPath = configPathEntry.Path.Replace("%GameRoot%", targetDirectory);

                                if (File.Exists(sourcePresetPath))
                                {
                                    var backupPath = sourcePresetPath + ".disabled";
                                    if (!File.Exists(backupPath)) File.Copy(sourcePresetPath, backupPath, overwrite: false);
                                }

                                await File.WriteAllTextAsync(sourcePresetPath, root.DefaultPreset);
                                installedFiles.Add(sourcePresetPath);
                            }
                        }
                    }

                    return installedFiles;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] T format parsing failed: {ex.Message}");
            }

            // Fallback
            Console.WriteLine("[Config] Invalid or empty instruction file, falling back to copy all.");
            return await CopyAllFilesAsync(sourceDir, targetDirectory);
        }

        private static async Task<List<string>> CopyAllFilesAsync(string sourceDir, string targetDirectory)
        {
            var installedFiles = new List<string>();
            Directory.CreateDirectory(targetDirectory);

            var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);

            foreach (var file in allFiles)
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var targetPath = Path.Combine(targetDirectory, relativePath);

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(targetPath))
                {
                    var backupPath = targetPath + ".disabled";
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(targetPath, backupPath, overwrite: false);
                    }
                }

                File.Copy(file, targetPath, overwrite: true);
                installedFiles.Add(targetPath);
            }

            return installedFiles;
        }
    }

    public class ConfigInstructions
    {
        public List<FileInstruction>? Files { get; set; }
    }

    public class AvailableSettingValue
    {
        public string? Value { get; set; }
        public string? FriendlyName { get; set; }
    }

    public class Child
    {
        public string? ID { get; set; }
        public List<Child>? Children { get; set; }
        public string? Name { get; set; }
        public List<AvailableSettingValue>? AvailableSettingValues { get; set; }
        public int ValueRangeType { get; set; }
        public double LowerRangeLimit { get; set; }
        public double UpperRangeLimit { get; set; }
        public double StepSize { get; set; }
        public string? OverrideValue { get; set; }
        public string? KeyOrSearchPattern { get; set; }
        public string? PrecedingElement { get; set; }
        public int ModifyInstruction { get; set; }
        public int ComparisonOperator { get; set; }
        public string? ConditionalValuePlaceholder { get; set; }
        public object? ConditinalValue { get; set; }
        public bool UseCondition { get; set; }
        public int RegistryValueType { get; set; }
    }

    public class ConfigFilePath
    {
        public string? Path { get; set; }
    }

    public class Root
    {
        public List<Child>? Children { get; set; }
        public int KeyValueSeparator { get; set; }
        public int FileEncoding { get; set; }
        public bool LockConfigFile { get; set; }
        public string? Name { get; set; }
        public string? DefaultPreset { get; set; }
        public List<ConfigFilePath>? ConfigFilePaths { get; set; }
    }
}
