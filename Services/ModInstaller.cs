using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NewAxis.Models;

namespace NewAxis.Services
{
    public class ModInstallationSettings
    {
        public double Depth { get; set; }
        public double Popout { get; set; }
        public bool DisableBlacklistedDlls { get; set; } = true;

        public HotkeyDefinition? DepthInc { get; set; }
        public HotkeyDefinition? DepthDec { get; set; }
        public HotkeyDefinition? PopoutInc { get; set; }
        public HotkeyDefinition? PopoutDec { get; set; }
    }

    public class HotkeyDefinition
    {
        public Avalonia.Input.Key Key { get; set; }
        public Avalonia.Input.KeyModifiers Modifiers { get; set; }
    }

    public class ModInstaller
    {
        private const string MOD_FILES_LIST = "3dfiles.txt";

        //ShaderToggler.addon must be deleted at least on God Of War for 3D+ works, not sure if is with all games
        private static readonly string[] _blacklistedFiles = { "nvngx_dlss.dll", "nvngx_dlssg.dll", "ShaderToggler.addon" };

        public static async Task<List<string>> InstallModAsync(
            Models.Game game,
            NewAxis.Models.ModType modType,
            GameRepositoryClient repoClient,
            ModInstallationSettings settings)
        {
            var installedFiles = new List<string>();

            // Extract game info
            var gameInstallPath = game.InstallPath;
            if (string.IsNullOrEmpty(gameInstallPath)) throw new Exception("Game install path is empty");

            if (!(game.Tag is GameIndexEntry gameEntry))
            {
                throw new Exception("Game metadata (Tag) is missing or invalid");
            }

            var executablePath = gameEntry.ExecutablePath ?? "";
            var relativeExecutablePath = gameEntry.RelativeExecutablePath ?? "";
            var targetDirectory = Path.Combine(gameInstallPath, relativeExecutablePath);

            Console.WriteLine($"[ModInstaller] Installing {modType.GetDescription()} mod for {game.Name}...");

            try
            {
                // TODO: Implement Recommended Settings
                /*
                if (!string.IsNullOrEmpty(gameEntry.ConfigArchivePath))
                {
                    Console.WriteLine("[ModInstaller] Found ConfigArchive, installing...");
                    var configLocalPath = await DownloadFileAsync(repoClient, gameEntry.ConfigArchivePath);
                    var configFiles = await ConfigExtractor.ExtractConfigAsync(configLocalPath, targetDirectory);
                    installedFiles.AddRange(configFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));
                }
                */

                if (modType == ModType.ThreeDPlus)
                {
                    // Install Reshade
                    if (string.IsNullOrEmpty(gameEntry.ReshadePath) || string.IsNullOrEmpty(gameEntry.TargetDllFileName))
                    {
                        throw new Exception("ReshadePath or TargetDllFileName not configured");
                    }

                    var reshadeLocalPath = await DownloadFileAsync(repoClient, gameEntry.ReshadePath);

                    // Download Overwatch if available
                    string? overwatchLocalPath = null;
                    if (!string.IsNullOrEmpty(gameEntry.OverwatchPath))
                    {
                        overwatchLocalPath = await DownloadFileAsync(repoClient, gameEntry.OverwatchPath);
                    }

                    var reshadeFiles = await ReshadeExtractor.ExtractReshadeAsync(new ReshadeExtractionContext
                    {
                        Reshade7zPath = reshadeLocalPath,
                        TargetDirectory = targetDirectory,
                        ExecutablePath = executablePath,
                        GameEntry = gameEntry,
                        OverwatchPath = overwatchLocalPath
                    });

                    installedFiles.AddRange(reshadeFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));
                }
                else if (modType == ModType.ThreeDUltra)
                {
                    // Install Migoto and Shaders
                    if (string.IsNullOrEmpty(gameEntry.MigotoPath))
                    {
                        throw new Exception("MigotoPath not configured");
                    }

                    var migotoLocalPath = await DownloadFileAsync(repoClient, gameEntry.MigotoPath);
                    var migotoFiles = await MigotoExtractor.ExtractMigotoAsync(
                        migotoLocalPath,
                        targetDirectory);

                    installedFiles.AddRange(migotoFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));

                    // Install custom shaders if available
                    if (!string.IsNullOrEmpty(gameEntry.ShaderMod))
                    {
                        var shaderLocalPath = await DownloadFileAsync(repoClient, gameEntry.ShaderMod);
                        // Extract shaders (using Migoto extractor for now as it handles 7z generically)
                        var shaderFiles = await MigotoExtractor.ExtractMigotoAsync(
                            shaderLocalPath,
                            targetDirectory);

                        installedFiles.AddRange(shaderFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));
                    }

                    // Create truegame.ini for 3D Ultra mod
                    await CreateTrueGameIniAsync(targetDirectory, settings);

                    // Generate d3dx.ini with base_path_override
                    var d3dxPath = Path.Combine(targetDirectory, "d3dx.ini");
                    var d3dxRelPath = Path.GetRelativePath(gameInstallPath, d3dxPath);

                    // Backup existing d3dx.ini if it exists (so we can restore it later)
                    if (File.Exists(d3dxPath))
                    {
                        var backupPath = d3dxPath + ".disabled";
                        if (File.Exists(backupPath)) File.Delete(backupPath);
                        File.Move(d3dxPath, backupPath);
                        Console.WriteLine($"[ModInstaller] Backed up d3dx.ini to .disabled");
                    }

                    // Create new d3dx.ini
                    var d3dxContent = $"[Rendering]\r\nbase_path_override={targetDirectory}";
                    await File.WriteAllTextAsync(d3dxPath, d3dxContent);
                    Console.WriteLine($"[ModInstaller] Generated d3dx.ini pointing to {targetDirectory}");

                    // Track d3dx.ini for uninstallation (will either be deleted or restored from backup)
                    if (!installedFiles.Contains(d3dxRelPath))
                    {
                        installedFiles.Add(d3dxRelPath);
                    }
                }

                ProcessBlacklist(installedFiles, gameInstallPath, targetDirectory, settings.DisableBlacklistedDlls);

                // Write installed files list
                var filesListPath = Path.Combine(gameInstallPath, MOD_FILES_LIST);
                await File.WriteAllLinesAsync(filesListPath, installedFiles);
                Console.WriteLine($"[ModInstaller] Created {MOD_FILES_LIST} with {installedFiles.Count} entries");

                return installedFiles;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModInstaller] Error: {ex.Message}");
                throw;
            }
        }

        private static void ProcessBlacklist(List<string> installedFiles, string gameInstallPath, string targetDirectory, bool enabled)
        {
            if (!enabled) return;

            // Handle blacklisted files (e.g. disable DLSS)
            foreach (var blacklistFile in _blacklistedFiles)
            {
                var fullPath = Path.Combine(targetDirectory, blacklistFile);
                if (File.Exists(fullPath))
                {
                    var backupPath = fullPath + ".disabled";
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath); // Ensure clean backup state
                    }
                    File.Move(fullPath, backupPath);
                    Console.WriteLine($"[ModInstaller] Disabled blacklisted file: {blacklistFile}");

                    // Add original file to installed files list so Uninstall knows to look for it/restore it
                    // We use the relative path from gameInstallPath just like other files
                    installedFiles.Add(Path.GetRelativePath(gameInstallPath, fullPath));
                }
            }
        }

        public static async Task UninstallModAsync(string gameInstallPath, bool deleteBackups = false)
        {
            var filesListPath = Path.Combine(gameInstallPath, MOD_FILES_LIST);
            if (!File.Exists(filesListPath))
            {
                Console.WriteLine("[ModInstaller] No 3dfiles.txt found, nothing to uninstall");
                return;
            }

            Console.WriteLine("[ModInstaller] Restoring original files...");
            var installedFiles = await File.ReadAllLinesAsync(filesListPath);

            foreach (var relativePath in installedFiles)
            {
                if (string.IsNullOrWhiteSpace(relativePath)) continue;

                var fullPath = Path.Combine(gameInstallPath, relativePath);
                if (File.Exists(fullPath))
                {
                    var backupPath = fullPath + ".disabled";
                    if (File.Exists(backupPath))
                    {
                        // Restore from backup
                        File.Copy(backupPath, fullPath, overwrite: true);
                        Console.WriteLine($"[ModInstaller] Restored: {relativePath}");

                        if (deleteBackups)
                        {
                            File.Delete(backupPath);
                        }
                    }
                    else
                    {
                        // No backup, just delete the mod file
                        File.Delete(fullPath);
                        Console.WriteLine($"[ModInstaller] Deleted: {relativePath}");
                    }
                }
            }

            // Delete the files list
            File.Delete(filesListPath);
            Console.WriteLine("[ModInstaller] Mod uninstalled successfully");
        }

        private static async Task<string> DownloadFileAsync(GameRepositoryClient repoClient, string urlOrPath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "NewAxisMods");
            Directory.CreateDirectory(tempDir);

            var cachePath = Path.Combine(tempDir, Path.GetFileName(urlOrPath));

            // Download if not already cached
            if (!File.Exists(cachePath))
            {
                Console.WriteLine($"[ModInstaller] Downloading: {urlOrPath}");
                await repoClient.DownloadFileAsync(urlOrPath, cachePath);
            }
            else
            {
                Console.WriteLine($"[ModInstaller] Using cached: {Path.GetFileName(urlOrPath)}");
            }

            return cachePath;
        }

        /// <summary>
        /// Creates truegame.ini file with default content and updates Depth/Popout values
        /// </summary>
        private static async Task CreateTrueGameIniAsync(string targetDirectory, ModInstallationSettings settings)
        {
            var iniPath = Path.Combine(targetDirectory, "truegame.ini");

            if (File.Exists(iniPath))
            {
                ApplyTrueGameSettings(settings, iniPath);
                return;
            }

            // Default truegame.ini template
            var defaultContent = Encoding.UTF8.GetString(Convert.FromBase64String("W0dFTkVSQUxdDQojIEZvciBmdXR1cmUgdXNlDQoNCltERVBUSF0NCiMgQ29udHJvbHMgdGhlIHN0ZXJlbyBzZXBhcmF0aW9uIHZhbHVlLCB3aXRoIGEgdmFsaWQgcmFuZ2Ugb2YgMCUgLSAxNTAlLCBpbmRpY2F0ZWQgYXMgYW4gaW50DQpEZXB0aCA9IDMwDQoNCiMgQ29udHJvbHMgdGhlIHN0ZXJlbyBjb252ZXJnZW5jZSB2YWx1ZSwgd2l0aCBhIHZhbGlkIHJhbmdlIG9mIDUwJSAtIDE1MCUsIGluZGljYXRlZCBhcyBhbiBpbnQNClBvcG91dCA9IDEwMA0KIA0KIyBOb3RlOiAzRCBPbi9PZmYgc3RhdHVzIHNob3VsZCBvbmx5IGJlIGFjdGl2ZSBmb3IgdGhlIGR1cmF0aW9uIG9mIGEgZ2FtZSBzZXNzaW9uIGFuZCBzaG91bGQgbm90IGJlIHBlcnNpc3RlZCwgDQojIG1lYW5pbmcgdGhhdCAzRCBzaG91bGQgYWx3YXlzIGJlIGVuYWJsZWQgd2hlbiBydW5uaW5nIHRocm91Z2ggVEcNCg0KDQpbSU5QVVRdDQojIEhvdGtleXMgYXJlIHNwZWNpZmllZCBpbiB0aGUgZm9ybWF0IFcuWC5ZLlogd2hlcmU6DQojIC0gVyBpbmRpY2F0ZXMgdGhlIHByaW1hcnkga2V5Y29kZQ0KIyAtIFggaW5kaWNhdGVzIHdoZXRoZXIgQUxUIHNob3VsZCBiZSBwcmVzc2VkIA0KIyAtIFkgaW5kaWNhdGVzIHdoZXRoZXIgQ1RSTCBzaG91bGQgYmUgcHJlc3NlZCANCiMgLSBaIGluZGljYXRlcyB3aGV0aGVyIFNISUZUIHNob3VsZCBiZSBwcmVzc2VkDQojIGUuZy4gQ3RybCtGMTIgPSAxMjMsMCwxLDAgDQojIFNlZSBoZXJlIGZvciBrZXljb2Rlczogc2hvcnR1cmwuYXQvQkdNTzgNCg0KQ3ljbGVQYW5lbERpc3BsYXlNb2RlID0gOTAsMCwxLDANCkluY3JlYXNlRGVwdGggID0gMTE1LDAsMSwwDQpEZWNyZWFzZURlcHRoICA9IDExNCwwLDEsMA0KSW5jcmVhc2VQb3BvdXQgPSAxMTcsMCwxLDANCkRlY3JlYXNlUG9wb3V0ID0gMTE2LDAsMSwwDQpDeWNsZVBhbmVsRG9ja1Bvc2l0aW9uID0gMTE4LDAsMSwwDQpJbmNyZWFzZVBhbmVsT3BhY2l0eSA9IDExMywwLDEsMA0KRGVjcmVhc2VQYW5lbE9wYWNpdHkgPSAxMTIsMCwxLDANClRvZ2dsZVN0ZXJlbyA9IDg0LDAsMSwwDQoNCiMgR2VuZXJhbCB0ZXJtaW5vbG9neToNCiMgLSAiT3ZlcmxheSIgcmVmZXJzIHRvIHRoZSBmdWxsIG92ZXJsYXkgc3lzdGVtLCB3aGljaCBjdXJyZW50bHkgaW5jbHVkZXMgYSBzaW5nbGUgcGFuZWwgYnV0IG1heSBpbiB0aGUgZnV0dXJlIGluY2x1ZGUgYWxlcnRzLCBvdGhlciB3aWRnZXRzLi5ldGMNCiMgLSAiUGFuZWwiIHJlZmVycyB0byB0aGUgcHJpbWFyeSB3aWRnZXQgY29udGFpbmluZyB0aGUgYnVsayBvZiB0aGUgb3ZlcmxheSBzeXN0ZW0gVUkgYW5kIGxvZ2ljDQpbVUldDQojIENvbnRyb2xzIHRoZSBvcGFjaXR5IG9mIHRoZSBvdmVybGF5IHBhbmVsLCB3aGVyZSAwIGlzIGZ1bGx5IHRyYW5zcGFyZW50IGFuZCAxIGlzIGZ1bGx5IG9wYXF1ZQ0KIyBCb3RoIFRHIGFuZCBzdGVyZW8gZHJpdmVycyBzaG91bGQgcmVzcGVjdCB0aGUgbWluIGFuZCBtYXggdmFsdWVzLiANClBhbmVsT3BhY2l0eU1pbiA9IDAuMg0KUGFuZWxPcGFjaXR5TWF4ID0gMS4wDQpQYW5lbE9wYWNpdHkgPSAwLjgNCg0KIyBDb250cm9scyB3aGVyZSB0aGUgb3ZlcmxheSBwYW5lbCBpcyBkb2NrZWQgaW4gdGhlIHZpZXdwb3J0DQojIE9uZSBvZjogVG9wTGVmdCwgVG9wUmlnaHQsIEJvdHRvbUxlZnQsIEJvdHRvbVJpZ2h0DQpQYW5lbERvY2tQb3NpdGlvbiA9IFRvcExlZnQNCg0KIyBUaGUgcGFuZWwgY2FuIGJlIGluIG9uZSBvZiB0aHJlZSBtb2RlcywgTWluaW1hbCwgRnVsbCwgYW5kIEhpZGRlbg0KIyBUaGlzIHZhbHVlIHNob3VsZCBiZSBwZXJzaXN0ZWQNClBhbmVsRGlzcGxheU1vZGUgPSBNaW5pbWFsDQoNCltJTUdVSV0NCltXaW5kb3ddW0RlYnVnIyNEZWZhdWx0XQ0KUG9zPTYwLDYwDQpTaXplPTQwMCw0MDANCkNvbGxhcHNlZD0wDQoNCltXaW5kb3ddW0dlbzExXQ0KUG9zPTAsMA0KU2l6ZT0zODQwLDIxNjANCkNvbGxhcHNlZD0wDQoNCg=="));

            // Write default content to file
            await File.WriteAllTextAsync(iniPath, defaultContent);
            Console.WriteLine($"[ModInstaller] Created truegame.ini");

            ApplyTrueGameSettings(settings, iniPath);

            Console.WriteLine($"[ModInstaller] Updated truegame.ini: Depth={settings.Depth}, Popout={settings.Popout}");

            return;
        }

        private static void ApplyTrueGameSettings(ModInstallationSettings settings, string iniPath)
        {
            // Update Depth and Popout values using IniFileParser
            var iniParser = new IniFileParser();
            iniParser.Load(iniPath);
            iniParser.SetValue("DEPTH", "Depth", ((int)settings.Depth).ToString());
            iniParser.SetValue("DEPTH", "Popout", ((int)settings.Popout).ToString());

            // Write Hotkeys
            if (settings.DepthInc != null) iniParser.SetValue("INPUT", "IncreaseDepth", GetTrueGameHotkeyString(settings.DepthInc));
            if (settings.DepthDec != null) iniParser.SetValue("INPUT", "DecreaseDepth", GetTrueGameHotkeyString(settings.DepthDec));
            if (settings.PopoutInc != null) iniParser.SetValue("INPUT", "IncreasePopout", GetTrueGameHotkeyString(settings.PopoutInc));
            if (settings.PopoutDec != null) iniParser.SetValue("INPUT", "DecreasePopout", GetTrueGameHotkeyString(settings.PopoutDec));

            iniParser.Save(iniPath);
        }

        private static string GetTrueGameHotkeyString(HotkeyDefinition def)
        {
            // Format: W,X,Y,Z
            // W = Keycode (VirtualKey)
            // X = Alt (1/0)
            // Y = Ctrl (1/0)
            // Z = Shift (1/0)

            int vk = KeyInterop.VirtualKeyFromKey(def.Key);
            int alt = def.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? 1 : 0;
            int ctrl = def.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) ? 1 : 0;
            int shift = def.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 1 : 0;

            return $"{vk},{alt},{ctrl},{shift}";
        }
    }

    public static class KeyInterop
    {
        public static int VirtualKeyFromKey(Avalonia.Input.Key key)
        {
            // This is a simplified mapping. For a full mapping we might need a library or extensive switch.
            // Avalonia Key enum often matches VK codes for A-Z, 0-9, F1-F12 but offset issues exist.
            // Let's try to trust a simple cast for common keys or implement a basic switch for important ones.
            // Actually Avalonia Key does NOT match VK 1:1.

            // Basic Lookup for Function Keys
            if (key >= Avalonia.Input.Key.F1 && key <= Avalonia.Input.Key.F24)
                return 112 + (int)(key - Avalonia.Input.Key.F1);

            // Numbers
            if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9)
                return 48 + (int)(key - Avalonia.Input.Key.D0);

            // Numpad
            if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9)
                return 96 + (int)(key - Avalonia.Input.Key.NumPad0);

            // Letters A-Z
            if (key >= Avalonia.Input.Key.A && key <= Avalonia.Input.Key.Z)
                return 65 + (int)(key - Avalonia.Input.Key.A);

            // Arrows
            if (key == Avalonia.Input.Key.Left) return 37;
            if (key == Avalonia.Input.Key.Up) return 38;
            if (key == Avalonia.Input.Key.Right) return 39;
            if (key == Avalonia.Input.Key.Down) return 40;

            // Modifiers
            if (key == Avalonia.Input.Key.LeftCtrl || key == Avalonia.Input.Key.RightCtrl) return 17;
            if (key == Avalonia.Input.Key.LeftAlt || key == Avalonia.Input.Key.RightAlt) return 18;
            if (key == Avalonia.Input.Key.LeftShift || key == Avalonia.Input.Key.RightShift) return 16;

            // Common
            if (key == Avalonia.Input.Key.Space) return 32;
            if (key == Avalonia.Input.Key.Enter) return 13;
            if (key == Avalonia.Input.Key.Escape) return 27;
            if (key == Avalonia.Input.Key.Back) return 8;
            if (key == Avalonia.Input.Key.Tab) return 9;

            // Fallback: try cast, though unreliable for special keys
            return (int)key;
        }
    }
}

