using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using Microsoft.Win32;
using System.Runtime.InteropServices;

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

            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"Config_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
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
                    Console.WriteLine("[Config] No JSON instructions found, copying all files...");
                    installedFiles = await CopyAllFilesAsync(tempExtractDir, targetDirectory);
                }

                Console.WriteLine($"[Config] Extraction complete! {installedFiles.Count} files installed.");
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

        internal static async Task<List<string>> ApplyJsonInstructionsAsync(
            string jsonPath,
            string sourceDir,
            string targetDirectory,
            string? settingsOverridesJson)
        {
            var installedFiles = new List<string>();
            var jsonContent = await File.ReadAllTextAsync(jsonPath);

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

                        if (root.ConfigFilePaths != null)
                        {
                            foreach (var configPathEntry in root.ConfigFilePaths.Where(x => x != null && !string.IsNullOrEmpty(x.Path)))
                            {
                                var targetPresetPath = configPathEntry.Path!
                                    .Replace("%GameRoot%", targetDirectory)
                                    .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                                    .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

                                // Registry Mode
                                if (targetPresetPath.StartsWith("HK", StringComparison.OrdinalIgnoreCase) ||
                                    targetPresetPath.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    {
                                        try
                                        {
                                            ApplyRegistrySettings(targetPresetPath, root, overrides);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[Config] Registry error: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[Config] Skipping Registry settings on non-Windows platform: {targetPresetPath}");
                                    }
                                    continue;
                                }

                                var targetDir = Path.GetDirectoryName(targetPresetPath);
                                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

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

                                if (overrides != null && overrides.Count > 0)
                                {
                                    contentToWrite = ApplySettingsToContent(contentToWrite, root, overrides);
                                }

                                if (!string.IsNullOrEmpty(contentToWrite))
                                {
                                    await File.WriteAllTextAsync(targetPresetPath, contentToWrite);
                                    installedFiles.Add(targetPresetPath);
                                }
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

            Console.WriteLine("[Config] Invalid or empty instruction file, falling back to copy all.");
            return await CopyAllFilesAsync(sourceDir, targetDirectory);
        }

        private static string ApplySettingsToContent(string content, Root root, List<GameSettingOverride> overrides)
        {
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            var separator = root.KeyValueSeparator == 1 ? ":" : "=";

            ///KeyValueSeparator == 0 if is registry? 


            foreach (var setting in overrides)
            {
                if (setting.GameSettingId == null) continue;

                var definition = FindChildById(root.Children, setting.GameSettingId);
                if (definition != null)
                {
                    ProcessSetting(lines, definition, setting.Value, separator, root.KeyValueSeparator);
                }
                else
                {
                    Console.WriteLine("[Config] Setting not found: " + setting.GameSettingId);
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

            foreach (var child in definition!.Children!.Where(x => x != null))
            {
                var childValue = child.OverrideValue?.Replace("%InputValue%", value, StringComparison.OrdinalIgnoreCase) ?? value;
                ProcessSingleSetting(lines, child, definition, childValue, separator, keyValueSeparator);
            }
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

                // If PrecedingElement defined but not found
                if (!precedingFound)
                {
                    // Auto-create section if it looks like one
                    if (definition.PrecedingElement.StartsWith("[") && definition.PrecedingElement.EndsWith("]"))
                    {
                        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                        {
                            lines.Add("");
                        }
                        lines.Add(definition.PrecedingElement);
                        startLine = lines.Count;
                    }
                    else
                    {
                        return;
                    }
                }
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
                    var nextLine = i + 1 < lines.Count ? lines[i + 1].Trim() : "";
                    if (nextLine != null && nextLine.StartsWith("[") && nextLine.EndsWith("]") && definition.PrecedingElement != null)
                    {
                        lines.Insert(i, $"{keyToUse}{separator}{valueToWrite}");
                        found = true;
                        break;
                    }
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

                if (!found)
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void ApplyRegistrySettings(string registryPath, Root root, List<GameSettingOverride>? overrides)
        {
            // Parse Root and SubKey
            // Expected format: HKEY_CURRENT_USER\Software\...\...
            string rootKeyName = registryPath.Split('\\')[0];
            string subKeyPath = registryPath.Substring(rootKeyName.Length).TrimStart('\\');

            RegistryKey? baseKey = rootKeyName.ToUpper() switch
            {
                "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
                "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
                "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
                "HKEY_USERS" or "HKU" => Registry.Users,
                "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
                _ => null
            };

            if (baseKey == null)
            {
                Console.WriteLine($"[Config] Unknown registry root: {rootKeyName}");
                return;
            }

            using (var key = baseKey.CreateSubKey(subKeyPath, writable: true))
            {
                if (key == null)
                {
                    Console.WriteLine($"[Config] Failed to create/open registry key: {registryPath}");
                    return;
                }

                Console.WriteLine($"[Config] Writing to Registry: {registryPath}");

                // Apply DefaultPreset if exists (assuming it's a list of values? No, usually DefaultPreset is a string file content)
                // For Reg mode, we iterate Children/Overrides instead.

                if (overrides == null || overrides.Count == 0) return;

                foreach (var setting in overrides)
                {
                    if (setting.GameSettingId == null) continue;
                    var definition = FindChildById(root.Children, setting.GameSettingId);
                    if (definition == null) continue;

                    ProcessRegistrySetting(key, definition, setting.Value);
                }
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void ProcessRegistrySetting(RegistryKey key, Child definition, string? value)
        {
            // Handle Resolution Special Case
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

                            ProcessSingleRegistrySetting(key, child, childValue);
                        }
                    }
                }
                return;
            }

            foreach (var child in definition!.Children!.Where(x => x != null))
            {
                var childValue = child.OverrideValue?.Replace("%InputValue%", value, StringComparison.OrdinalIgnoreCase) ?? value;
                ProcessSingleRegistrySetting(key, child, childValue);
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void ProcessSingleRegistrySetting(RegistryKey key, Child definition, string? value)
        {
            if (string.IsNullOrEmpty(definition.KeyOrSearchPattern) && string.IsNullOrEmpty(definition.Name)) return;

            string valueName = definition.KeyOrSearchPattern ?? definition.Name!; // KeyOrSearchPattern holds the Value Name

            // RegistryValueType: 
            // 4 = DWORD (REG_DWORD)
            // 1 = String (REG_SZ)
            // 0/Default -> infer? or assume String? Check user JSON: "RegistryValueType": 4

            try
            {
                object? valueToWrite = null;
                RegistryValueKind kind = RegistryValueKind.Unknown;

                switch (definition.RegistryValueType)
                {
                    case 4: // DWORD
                        if (int.TryParse(value, out int intVal))
                        {
                            valueToWrite = intVal;
                            kind = RegistryValueKind.DWord;
                        }
                        else if (long.TryParse(value, out long longVal)) // Handle potential unsigned stuff?
                        {
                            valueToWrite = longVal; // Registry.SetValue handles this
                            kind = RegistryValueKind.DWord;
                        }
                        break;
                    case 1: // String
                        valueToWrite = value;
                        kind = RegistryValueKind.String;
                        break;
                    default:
                        // Fallback or assuming string or letting .NET decide
                        if (int.TryParse(value, out int v)) { valueToWrite = v; kind = RegistryValueKind.DWord; }
                        else { valueToWrite = value; kind = RegistryValueKind.String; }
                        break;
                }

                if (valueToWrite != null)
                {
                    key.SetValue(valueName, valueToWrite, kind);
                    Console.WriteLine($"[Config] Set REG {valueName} = {valueToWrite} ({kind})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Error setting registry value {valueName}: {ex.Message}");
            }
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
