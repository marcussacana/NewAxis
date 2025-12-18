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
            string targetDirectory,
            string? settingsOverridesJson = null)
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
                    installedFiles = await ApplyJsonInstructionsAsync(jsonInstructionsPath, tempExtractDir, targetDirectory, settingsOverridesJson);
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

        internal static async Task<List<string>> ApplyJsonInstructionsAsync(
            string jsonPath,
            string sourceDir,
            string targetDirectory,
            string? settingsOverridesJson)
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

                    // Parse Overrides if present
                    List<GameSettingOverride>? overrides = null;
                    if (!string.IsNullOrEmpty(settingsOverridesJson))
                    {
                        try
                        {
                            overrides = JsonSerializer.Deserialize(settingsOverridesJson, AppJsonContext.Default.ListGameSettingOverride);
                            if (overrides != null) Console.WriteLine($"[Config] Loaded {overrides.Count} settings overrides.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Config] Failed to parse settings overrides: {ex.Message}");
                        }
                    }

                    // Process definitions
                    foreach (var root in rootList.Where(x => x != null))
                    {
                        if (string.IsNullOrEmpty(root.Name) && root.ConfigFilePaths == null) continue;

                        var configPathEntry = root.ConfigFilePaths?.FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.Path));
                        if (configPathEntry != null && configPathEntry.Path != null)
                        {
                            var targetPresetPath = configPathEntry.Path
                                    .Replace("%GameRoot%", targetDirectory)
                                    .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                                    .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

                            var targetDir = Path.GetDirectoryName(targetPresetPath);
                            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                            // 1. Determine base content
                            string contentToWrite = "";

                            if (File.Exists(targetPresetPath))
                            {
                                // If file exists, read it
                                contentToWrite = await File.ReadAllTextAsync(targetPresetPath);
                                // Create backup if not exists
                                var backupPath = targetPresetPath + ".disabled";
                                if (!File.Exists(backupPath)) File.Copy(targetPresetPath, backupPath, overwrite: false);
                            }
                            else if (!string.IsNullOrEmpty(root.DefaultPreset))
                            {
                                // Use default preset
                                contentToWrite = root.DefaultPreset;
                            }

                            // 2. Apply Overrides
                            if (overrides != null && overrides.Count > 0 && !string.IsNullOrEmpty(contentToWrite))
                            {
                                contentToWrite = ApplySettingsToContent(contentToWrite, root, overrides);
                            }

                            // 3. Write file
                            if (!string.IsNullOrEmpty(contentToWrite))
                            {
                                await File.WriteAllTextAsync(targetPresetPath, contentToWrite);
                                installedFiles.Add(targetPresetPath);
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

        private static string ApplySettingsToContent(string content, Root root, List<GameSettingOverride> overrides)
        {
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            var separator = root.KeyValueSeparator == 1 ? ":" : "=";

            foreach (var setting in overrides)
            {
                if (setting.GameSettingId == null) continue;

                var definition = FindChildById(root.Children, setting.GameSettingId);
                if (definition != null)
                {
                    ProcessSetting(lines, definition, setting.Value, separator, root.KeyValueSeparator);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static void ProcessSetting(List<string> lines, Child definition, string? value, string separator, int keyValueSeparator)
        {
            // Handle Resolution (ValueRangeType == 3)
            if (definition.ValueRangeType == 3 && definition.Children != null && !string.IsNullOrEmpty(value))
            {
                var parts = value.ToLower().Split('x');
                if (parts.Length == 2)
                {
                    var width = parts[0].Trim();
                    var height = parts[1].Trim();

                    foreach (var child in definition.Children)
                    {
                        var childValue = child.OverrideValue;
                        if (!string.IsNullOrEmpty(childValue))
                        {
                            childValue = childValue.Replace("%ResWidth%", width, StringComparison.OrdinalIgnoreCase)
                                                   .Replace("%ResHeight%", height, StringComparison.OrdinalIgnoreCase);

                            ProcessSingleSetting(lines, child, definition, childValue, separator, keyValueSeparator);
                        }
                    }
                }
                return;
            }

            // Handle Standard Setting
            ProcessSingleSetting(lines, definition, definition, value, separator, keyValueSeparator);
        }

        private static void ProcessSingleSetting(List<string> lines, Child definition, Child? parent, string? rawValue, string separator, int keyValueSeparator)
        {
            string? keyToUse = definition.KeyOrSearchPattern;

            if (string.IsNullOrEmpty(keyToUse))
            {
                if (keyValueSeparator == 2 && !string.IsNullOrEmpty(definition.ID))
                {
                    keyToUse = definition.ID;
                }
                else
                {
                    return;
                }
            }

            // Determine value to write
            string valueToWrite = rawValue ?? "";

            // Check mapping on Parent first (e.g. Resolution definition holds values), then self
            var availableValues = parent?.AvailableSettingValues ?? definition.AvailableSettingValues;

            if (availableValues != null)
            {
                var predefined = availableValues.FirstOrDefault(
                    v => string.Equals(v.FriendlyName, rawValue, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(v.Value, rawValue, StringComparison.OrdinalIgnoreCase));

                if (predefined != null && predefined.Value != null)
                {
                    valueToWrite = predefined.Value;
                }
            }

            // Determine Search Range
            int startLine = 0;
            if (!string.IsNullOrEmpty(definition.PrecedingElement))
            {
                bool precedingFound = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Contains(definition.PrecedingElement, StringComparison.OrdinalIgnoreCase))
                    {
                        startLine = i + 1;
                        precedingFound = true;
                        break;
                    }
                }

                // If PrecedingElement defined but not found, we skip processing to be safe
                if (!precedingFound) return;
            }

            // Pattern Matching Mode ({0})
            if (keyToUse.Contains("{0}"))
            {
                var parts = keyToUse.Split(new[] { "{0}" }, StringSplitOptions.None);
                var prefix = parts[0];
                var suffix = parts.Length > 1 ? parts[1] : "";

                bool found = false;
                for (int i = startLine; i < lines.Count; i++)
                {
                    var line = lines[i];
                    int prefixIndex = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);

                    if (prefixIndex >= 0)
                    {
                        // Check suffix if exists
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            int suffixIndex = line.IndexOf(suffix, prefixIndex + prefix.Length, StringComparison.OrdinalIgnoreCase);
                            if (suffixIndex > prefixIndex)
                            {
                                // Replace content between prefix and suffix
                                var before = line.Substring(0, prefixIndex + prefix.Length);
                                var after = line.Substring(suffixIndex);
                                lines[i] = before + valueToWrite + after;
                                found = true;
                                break;
                            }
                        }
                        else
                        {
                            // No suffix, replace everything after prefix
                            var before = line.Substring(0, prefixIndex + prefix.Length);
                            lines[i] = before + valueToWrite;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found && startLine == 0) // Only append if we searched whole file (no preceding requirement blocking context)
                {
                    try
                    {
                        lines.Add(keyToUse.Replace("{0}", valueToWrite));
                    }
                    catch
                    {
                        // Fallback just in case
                    }
                }
            }
            else
            {
                // Standard INI Mode (Key=Value logic)
                bool found = false;
                for (int i = startLine; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith(keyToUse, StringComparison.OrdinalIgnoreCase))
                    {
                        var remainder = line.Substring(keyToUse.Length).TrimStart();
                        if (remainder.StartsWith(separator) || remainder.Length == 0)
                        {
                            lines[i] = $"{keyToUse}{separator}{valueToWrite}";
                            found = true;
                            break;
                        }
                    }
                }

                if (!found && startLine == 0)
                {
                    lines.Add($"{keyToUse}{separator}{valueToWrite}");
                }
            }
        }

        private static Child? FindChildById(List<Child>? children, string id)
        {
            if (children == null) return null;
            foreach (var child in children)
            {
                if (child.ID == id) return child;
                var found = FindChildById(child.Children, id);
                if (found != null) return found;
            }
            return null;
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
