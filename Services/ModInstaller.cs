using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewAxis.Services
{
    /// <summary>
    /// Handles mod installation and cleanup
    /// </summary>
    public class ModInstaller
    {
        private const string MOD_FILES_LIST = "3dfiles.txt";

        public static async Task<List<string>> InstallModAsync(
            string gameInstallPath,
            string executablePath,
            string relativeExecutablePath,
            string modType,
            GameRepositoryClient repoClient,
            Services.GameIndexEntry gameEntry,
            double depth,
            double popout)
        {
            var installedFiles = new List<string>();

            Console.WriteLine($"[ModInstaller] Installing {modType} mod...");

            try
            {
                if (modType == "3D+")
                {
                    // Install Reshade
                    if (string.IsNullOrEmpty(gameEntry.ReshadePath) || string.IsNullOrEmpty(gameEntry.TargetDllFileName))
                    {
                        throw new Exception("ReshadePath or TargetDllFileName not configured");
                    }

                    var reshadeLocalPath = await DownloadFileAsync(repoClient, gameEntry.ReshadePath);
                    var reshadeFiles = await ReshadeExtractor.ExtractReshadeAsync(
                        reshadeLocalPath,
                        gameInstallPath,
                        executablePath,
                        relativeExecutablePath,
                        gameEntry.TargetDllFileName);

                    installedFiles.AddRange(reshadeFiles);
                }
                else if (modType == "3D Ultra")
                {
                    // Install Migoto and Shaders
                    if (string.IsNullOrEmpty(gameEntry.MigotoPath))
                    {
                        throw new Exception("MigotoPath not configured");
                    }

                    var migotoLocalPath = await DownloadFileAsync(repoClient, gameEntry.MigotoPath);
                    var migotoFiles = await MigotoExtractor.ExtractMigotoAsync(
                        migotoLocalPath,
                        gameInstallPath,
                        relativeExecutablePath);

                    installedFiles.AddRange(migotoFiles);

                    // Install custom shaders if available
                    if (!string.IsNullOrEmpty(gameEntry.ShaderMod))
                    {
                        var shaderLocalPath = await DownloadFileAsync(repoClient, gameEntry.ShaderMod);
                        // Extract shaders (using Migoto extractor for now as it handles 7z generically)
                        var shaderFiles = await MigotoExtractor.ExtractMigotoAsync(
                            shaderLocalPath,
                            gameInstallPath,
                            relativeExecutablePath);

                        installedFiles.AddRange(shaderFiles);
                    }

                    // Create truegame.ini for 3D Ultra mod
                    await CreateTrueGameIniAsync(gameInstallPath, relativeExecutablePath, depth, popout);
                }

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
        private static async Task CreateTrueGameIniAsync(string gameInstallPath, string relativeExecutablePath, double depth, double popout)
        {
            var iniPath = Path.Combine(gameInstallPath, relativeExecutablePath, "truegame.ini");

            if (File.Exists(iniPath))
                return;

            // Default truegame.ini template
            var defaultContent = Encoding.UTF8.GetString(Convert.FromBase64String("W0dFTkVSQUxdDQojIEZvciBmdXR1cmUgdXNlDQoNCltERVBUSF0NCiMgQ29udHJvbHMgdGhlIHN0ZXJlbyBzZXBhcmF0aW9uIHZhbHVlLCB3aXRoIGEgdmFsaWQgcmFuZ2Ugb2YgMCUgLSAxNTAlLCBpbmRpY2F0ZWQgYXMgYW4gaW50DQpEZXB0aCA9IDMwDQoNCiMgQ29udHJvbHMgdGhlIHN0ZXJlbyBjb252ZXJnZW5jZSB2YWx1ZSwgd2l0aCBhIHZhbGlkIHJhbmdlIG9mIDUwJSAtIDE1MCUsIGluZGljYXRlZCBhcyBhbiBpbnQNClBvcG91dCA9IDEwMA0KIA0KIyBOb3RlOiAzRCBPbi9PZmYgc3RhdHVzIHNob3VsZCBvbmx5IGJlIGFjdGl2ZSBmb3IgdGhlIGR1cmF0aW9uIG9mIGEgZ2FtZSBzZXNzaW9uIGFuZCBzaG91bGQgbm90IGJlIHBlcnNpc3RlZCwgDQojIG1lYW5pbmcgdGhhdCAzRCBzaG91bGQgYWx3YXlzIGJlIGVuYWJsZWQgd2hlbiBydW5uaW5nIHRocm91Z2ggVEcNCg0KDQpbSU5QVVRdDQojIEhvdGtleXMgYXJlIHNwZWNpZmllZCBpbiB0aGUgZm9ybWF0IFcuWC5ZLlogd2hlcmU6DQojIC0gVyBpbmRpY2F0ZXMgdGhlIHByaW1hcnkga2V5Y29kZQ0KIyAtIFggaW5kaWNhdGVzIHdoZXRoZXIgQUxUIHNob3VsZCBiZSBwcmVzc2VkIA0KIyAtIFkgaW5kaWNhdGVzIHdoZXRoZXIgQ1RSTCBzaG91bGQgYmUgcHJlc3NlZCANCiMgLSBaIGluZGljYXRlcyB3aGV0aGVyIFNISUZUIHNob3VsZCBiZSBwcmVzc2VkDQojIGUuZy4gQ3RybCtGMTIgPSAxMjMsMCwxLDAgDQojIFNlZSBoZXJlIGZvciBrZXljb2Rlczogc2hvcnR1cmwuYXQvQkdNTzgNCg0KQ3ljbGVQYW5lbERpc3BsYXlNb2RlID0gOTAsMCwxLDANCkluY3JlYXNlRGVwdGggID0gMTE1LDAsMSwwDQpEZWNyZWFzZURlcHRoICA9IDExNCwwLDEsMA0KSW5jcmVhc2VQb3BvdXQgPSAxMTcsMCwxLDANCkRlY3JlYXNlUG9wb3V0ID0gMTE2LDAsMSwwDQpDeWNsZVBhbmVsRG9ja1Bvc2l0aW9uID0gMTE4LDAsMSwwDQpJbmNyZWFzZVBhbmVsT3BhY2l0eSA9IDExMywwLDEsMA0KRGVjcmVhc2VQYW5lbE9wYWNpdHkgPSAxMTIsMCwxLDANClRvZ2dsZVN0ZXJlbyA9IDg0LDAsMSwwDQoNCiMgR2VuZXJhbCB0ZXJtaW5vbG9neToNCiMgLSAiT3ZlcmxheSIgcmVmZXJzIHRvIHRoZSBmdWxsIG92ZXJsYXkgc3lzdGVtLCB3aGljaCBjdXJyZW50bHkgaW5jbHVkZXMgYSBzaW5nbGUgcGFuZWwgYnV0IG1heSBpbiB0aGUgZnV0dXJlIGluY2x1ZGUgYWxlcnRzLCBvdGhlciB3aWRnZXRzLi5ldGMNCiMgLSAiUGFuZWwiIHJlZmVycyB0byB0aGUgcHJpbWFyeSB3aWRnZXQgY29udGFpbmluZyB0aGUgYnVsayBvZiB0aGUgb3ZlcmxheSBzeXN0ZW0gVUkgYW5kIGxvZ2ljDQpbVUldDQojIENvbnRyb2xzIHRoZSBvcGFjaXR5IG9mIHRoZSBvdmVybGF5IHBhbmVsLCB3aGVyZSAwIGlzIGZ1bGx5IHRyYW5zcGFyZW50IGFuZCAxIGlzIGZ1bGx5IG9wYXF1ZQ0KIyBCb3RoIFRHIGFuZCBzdGVyZW8gZHJpdmVycyBzaG91bGQgcmVzcGVjdCB0aGUgbWluIGFuZCBtYXggdmFsdWVzLiANClBhbmVsT3BhY2l0eU1pbiA9IDAuMg0KUGFuZWxPcGFjaXR5TWF4ID0gMS4wDQpQYW5lbE9wYWNpdHkgPSAwLjgNCg0KIyBDb250cm9scyB3aGVyZSB0aGUgb3ZlcmxheSBwYW5lbCBpcyBkb2NrZWQgaW4gdGhlIHZpZXdwb3J0DQojIE9uZSBvZjogVG9wTGVmdCwgVG9wUmlnaHQsIEJvdHRvbUxlZnQsIEJvdHRvbVJpZ2h0DQpQYW5lbERvY2tQb3NpdGlvbiA9IFRvcExlZnQNCg0KIyBUaGUgcGFuZWwgY2FuIGJlIGluIG9uZSBvZiB0aHJlZSBtb2RlcywgTWluaW1hbCwgRnVsbCwgYW5kIEhpZGRlbg0KIyBUaGlzIHZhbHVlIHNob3VsZCBiZSBwZXJzaXN0ZWQNClBhbmVsRGlzcGxheU1vZGUgPSBNaW5pbWFsDQoNCltJTUdVSV0NCltXaW5kb3ddW0RlYnVnIyNEZWZhdWx0XQ0KUG9zPTYwLDYwDQpTaXplPTQwMCw0MDANCkNvbGxhcHNlZD0wDQoNCltXaW5kb3ddW0dlbzExXQ0KUG9zPTAsMA0KU2l6ZT0zODQwLDIxNjANCkNvbGxhcHNlZD0wDQoNCg=="));

            // Write default content to file
            await File.WriteAllTextAsync(iniPath, defaultContent);
            Console.WriteLine($"[ModInstaller] Created truegame.ini");

            // Update Depth and Popout values using IniFileParser
            var iniParser = new IniFileParser();
            iniParser.Load(iniPath);
            iniParser.SetValue("DEPTH", "Depth", ((int)depth).ToString());
            iniParser.SetValue("DEPTH", "Popout", ((int)popout).ToString());
            iniParser.Save(iniPath);

            Console.WriteLine($"[ModInstaller] Updated truegame.ini: Depth={depth}, Popout={popout}");

            return;
        }
    }
}
