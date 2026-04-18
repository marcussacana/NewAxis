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
using System.Globalization;

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
            string? settingsOverridesJson = null,
            GameIndexEntry? gameEntry = null,
            Action? onInstalled = null)
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
                Trace.WriteLine("[Config] Extracting archive...");
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
                    Trace.WriteLine($"[Config] Found instruction file: {Path.GetFileName(jsonInstructionsPath)}");
                    installedFiles = await ApplyJsonInstructionsAsync(jsonInstructionsPath, tempExtractDir, targetDirectory, settingsOverridesJson, gameEntry, onInstalled);
                }
                else
                {
                    Trace.WriteLine("[Config] No JSON instructions found, copying all files...");
                    installedFiles = await CopyAllFilesAsync(tempExtractDir, targetDirectory, onInstalled);
                }

                Trace.WriteLine($"[Config] Extraction complete! {installedFiles.Count} files installed.");
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
            string? settingsOverridesJson,
            GameIndexEntry? gameEntry,
            Action? onInstalled = null)
        {
            var installedFiles = new List<string>();
            var jsonContent = await File.ReadAllTextAsync(jsonPath);

            try
            {
                var rootList = JsonSerializer.Deserialize(jsonContent, AppJsonContext.Default.ListRoot);
                if (rootList != null && rootList.Count > 0)
                {
                    Trace.WriteLine("[Config] Found valid T configuration definitions. Processing instructions.");

                    // Parse Overrides if present
                    List<GameSettingOverride>? overrides = null;
                    if (!string.IsNullOrEmpty(settingsOverridesJson))
                    {
                        try
                        {
                            overrides = JsonSerializer.Deserialize(settingsOverridesJson, AppJsonContext.Default.ListGameSettingOverride);
                            if (overrides != null) Trace.WriteLine($"[Config] Loaded {overrides.Count} settings overrides.");
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[Config] Failed to parse settings overrides: {ex.Message}");
                        }
                    }

                    // Process definitions
                    foreach (var root in rootList.Where(x => x != null))
                    {
                        if (string.IsNullOrEmpty(root.Name) && root.ConfigFilePaths == null) continue;

                        ModInstaller.InjectCustomConfigs(gameEntry, root, overrides);

                        if (root.ConfigFilePaths != null)
                        {
                            foreach (var configPathEntry in root.ConfigFilePaths.Where(x => x != null && !string.IsNullOrEmpty(x.Path)))
                            {
                                var targetPresetPath = configPathEntry.Path!
                                    .Replace("%GameRoot%", targetDirectory, true, CultureInfo.InvariantCulture)
                                    .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), true, CultureInfo.InvariantCulture)
                                    .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), true, CultureInfo.InvariantCulture)
                                    .Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), true, CultureInfo.InvariantCulture);

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
                                            Trace.WriteLine($"[Config] Registry error: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        Trace.WriteLine($"[Config] Skipping Registry settings on non-Windows platform: {targetPresetPath}");
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
                                    contentToWrite = root.DefaultPreset.TrimEnd();
                                }

                                if (overrides != null && overrides.Count > 0)
                                {
                                    contentToWrite = ApplySettingsToContent(contentToWrite, root, overrides);
                                }

                                if (!string.IsNullOrEmpty(contentToWrite))
                                {
                                    await File.WriteAllTextAsync(targetPresetPath, contentToWrite);
                                    installedFiles.Add(targetPresetPath);
                                    onInstalled?.Invoke();
                                }
                            }
                        }
                    }

                    return installedFiles;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Config] T format parsing failed: {ex.Message}");
            }

            Trace.WriteLine("[Config] Invalid or empty instruction file, falling back to copy all.");
            return await CopyAllFilesAsync(sourceDir, targetDirectory, onInstalled);
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
                else
                {
                    Trace.WriteLine("[Config] Setting not found: " + setting.GameSettingId);
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
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines.Last()))
            {
                lines.RemoveAt(lines.Count - 1);
            }

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

        private static async Task<List<string>> CopyAllFilesAsync(string sourceDir, string targetDirectory, Action? onInstalled = null)
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

                await Task.Run(() => File.Copy(file, targetPath, overwrite: true));
                installedFiles.Add(targetPath);
                onInstalled?.Invoke();
            }

            return installedFiles;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void ApplyRegistrySettings(string registryPath, Root root, List<GameSettingOverride>? overrides)
        {
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
                Trace.WriteLine($"[Config] Unknown registry root: {rootKeyName}");
                return;
            }

            if (baseKey.OpenSubKey(subKeyPath) == null && !string.IsNullOrWhiteSpace(root.DefaultPreset))
            {
                ApplyRegistryPreset(root.DefaultPreset);
            }

            using (var key = baseKey.CreateSubKey(subKeyPath, writable: true))
            {
                if (key == null)
                {
                    Trace.WriteLine($"[Config] Failed to create/open registry key: {registryPath}");
                    return;
                }

                Trace.WriteLine($"[Config] Writing to Registry: {registryPath}");

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
        private static void ApplyRegistryPreset(string defaultPreset)
        {
            if (string.IsNullOrWhiteSpace(defaultPreset)) return;

            var rawLines = defaultPreset.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var processedLines = new List<string>();
            string currentLine = "";

            foreach (var line in rawLines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (trimmed.EndsWith("\\"))
                {
                    currentLine += trimmed.Substring(0, trimmed.Length - 1);
                }
                else
                {
                    currentLine += trimmed;
                    if (!string.IsNullOrWhiteSpace(currentLine))
                    {
                        processedLines.Add(currentLine);
                    }
                    currentLine = "";
                }
            }

            RegistryKey? currentKey = null;

            foreach (var line in processedLines)
            {
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    // Handle Key
                    currentKey?.Dispose();
                    currentKey = null;

                    string fullPath = line.Substring(1, line.Length - 2);
                    string rootKeyName = fullPath.Split('\\')[0];
                    string subKeyPath = fullPath.Contains('\\') ? fullPath.Substring(rootKeyName.Length + 1) : "";

                    RegistryKey? baseKey = rootKeyName.ToUpper() switch
                    {
                        "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
                        "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
                        "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
                        "HKEY_USERS" or "HKU" => Registry.Users,
                        "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
                        _ => null
                    };

                    if (baseKey != null)
                    {
                        try
                        {
                            currentKey = baseKey.CreateSubKey(subKeyPath, writable: true);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[Config] Failed to create registry key {fullPath}: {ex.Message}");
                        }
                    }
                }
                else if (currentKey != null && line.Contains('='))
                {
                    // Handle Value
                    int eqIndex = line.IndexOf('=');
                    string nameRaw = line.Substring(0, eqIndex).Trim();
                    string valueRaw = line.Substring(eqIndex + 1).Trim();

                    string valueName = nameRaw.StartsWith("\"") && nameRaw.EndsWith("\"")
                        ? nameRaw.Substring(1, nameRaw.Length - 2)
                        : nameRaw;

                    if (valueName == "@") valueName = ""; // Default value

                    try
                    {
                        object? valueToSet = null;
                        RegistryValueKind kind = RegistryValueKind.String;

                        if (valueRaw.StartsWith("\"") && valueRaw.EndsWith("\""))
                        {
                            // String
                            valueToSet = valueRaw.Substring(1, valueRaw.Length - 2)
                                .Replace("\\\\", "\\")
                                .Replace("\\\"", "\"");
                            kind = RegistryValueKind.String;
                        }
                        else if (valueRaw.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                        {
                            // DWORD
                            string hex = valueRaw.Substring(6);
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int dword))
                            {
                                valueToSet = dword;
                                kind = RegistryValueKind.DWord;
                            }
                        }
                        else if (valueRaw.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
                        {
                            // Binary (REG_BINARY)
                            valueToSet = ParseHexData(valueRaw.Substring(4));
                            kind = RegistryValueKind.Binary;
                        }
                        else if (valueRaw.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
                        {
                            // Expandable String (REG_EXPAND_SZ)
                            byte[] data = ParseHexData(valueRaw.Substring(7));
                            valueToSet = System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
                            kind = RegistryValueKind.ExpandString;
                        }
                        else if (valueRaw.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
                        {
                            // Multi-String (REG_MULTI_SZ)
                            byte[] data = ParseHexData(valueRaw.Substring(7));
                            string fullStr = System.Text.Encoding.Unicode.GetString(data);
                            valueToSet = fullStr.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                            kind = RegistryValueKind.MultiString;
                        }
                        else if (valueRaw.StartsWith("hex(b):", StringComparison.OrdinalIgnoreCase))
                        {
                            // QWORD (REG_QWORD)
                            byte[] data = ParseHexData(valueRaw.Substring(7));
                            if (data.Length >= 8)
                            {
                                valueToSet = BitConverter.ToInt64(data, 0);
                                kind = RegistryValueKind.QWord;
                            }
                        }
                        else
                        {
                            Trace.WriteLine($"[Config] Unknown registry value kind: {valueRaw}");
                        }

                        if (valueToSet != null)
                        {
                            Trace.WriteLine($"[Config] Setting registry value {valueName} to {valueToSet} ({kind})");
                            currentKey.SetValue(valueName, valueToSet, kind);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[Config] Error setting registry value {valueName}: {ex.Message}");
                    }
                }
            }

            currentKey?.Dispose();
        }

        private static byte[] ParseHexData(string hexPart)
        {
            var bytes = new List<byte>();
            var hexValues = hexPart.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var hv in hexValues)
            {
                if (byte.TryParse(hv.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    bytes.Add(b);
                }
            }
            return bytes.ToArray();
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void ProcessRegistrySetting(RegistryKey key, Child definition, string? value)
        {
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

                            ProcessSingleRegistrySetting(key, definition, child, childValue);
                        }
                    }
                }
                return;
            }

            foreach (var child in definition!.Children!.Where(x => x != null))
            {
                var childValue = child.OverrideValue?.Replace("%InputValue%", value, StringComparison.OrdinalIgnoreCase) ?? value;
                ProcessSingleRegistrySetting(key, definition, child, childValue);
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void ProcessSingleRegistrySetting(RegistryKey key, Child parent, Child definition, string? value)
        {
            if (string.IsNullOrEmpty(definition.KeyOrSearchPattern) && string.IsNullOrEmpty(definition.Name)) return;

            string valueName = definition.KeyOrSearchPattern ?? definition.Name!;

            // Check mapping on Parent first (e.g. Resolution definition holds values), then self
            var availableValues = parent?.AvailableSettingValues ?? definition.AvailableSettingValues;

            if (availableValues != null)
            {
                var predefined = availableValues.FirstOrDefault(
                    v => string.Equals(v.FriendlyName, value, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase));

                if (predefined != null && predefined.Value != null)
                {
                    value = predefined.Value;
                }
            }

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
                        else if (long.TryParse(value, out long longVal))
                        {
                            valueToWrite = longVal;
                            kind = RegistryValueKind.DWord;
                        }
                        break;
                    case 1: // String
                        valueToWrite = value;
                        kind = RegistryValueKind.String;
                        break;
                    default:
                        if (int.TryParse(value, out int v)) { valueToWrite = v; kind = RegistryValueKind.DWord; }
                        else { valueToWrite = value; kind = RegistryValueKind.String; }
                        break;
                }

                if (valueToWrite != null)
                {
                    key.SetValue(valueName, valueToWrite, kind);
                    Trace.WriteLine($"[Config] Set REG {valueName} = {valueToWrite} ({kind})");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Config] Error setting registry value {valueName}: {ex.Message}");
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
