using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NewAxis.Services
{
    public static class GamePathScanner
    {
        private static readonly string[] SteamLibrarySuffixes = new[]
        {
            Path.Combine("SteamLibrary", "steamapps", "common"),
            Path.Combine("Program Files (x86)", "Steam", "steamapps", "common"),
            Path.Combine("Program Files", "Steam", "steamapps", "common"),
            Path.Combine("Steam", "steamapps", "common")
        };

        /// <summary>
        /// Scans all available drives for the game directory using common Steam paths.
        /// </summary>
        /// <param name="gameEntry">The game index entry containing directory name, executable info, and Steam App ID.</param>
        /// <returns>The full path if found, or null.</returns>
        public static string? FindGameDirectory(GameIndexEntry gameEntry)
        {
            if (gameEntry == null || string.IsNullOrWhiteSpace(gameEntry.DirectoryName))
                return null;

            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);

            foreach (var driveRoot in drives)
            {
                foreach (var suffix in SteamLibrarySuffixes)
                {
                    var potentialPath = Path.Combine(driveRoot, suffix, gameEntry.DirectoryName);

                    if (Directory.Exists(potentialPath))
                    {
                        // If executable name is provided, verify it exists
                        if (!string.IsNullOrEmpty(gameEntry.ExecutablePath))
                        {
                            var fullExePath = Path.Combine(potentialPath, gameEntry.RelativeExecutablePath ?? "", gameEntry.ExecutablePath);
                            if (File.Exists(fullExePath))
                            {
                                return potentialPath;
                            }
                            // If exe doesn't exist, continue searching other locations
                            continue;
                        }

                        // If no exe specified, just return the directory (legacy behavior)
                        return potentialPath;
                    }
                }
            }

            // Fallback: Try Steam appmanifest detection if Steam App ID is provided
            if (!string.IsNullOrEmpty(gameEntry.SteamAppId))
            {
                var steamPath = FindGameViaSteamAppmanifest(gameEntry.SteamAppId, gameEntry.ExecutablePath, gameEntry.RelativeExecutablePath);
                if (steamPath != null)
                {
                    return steamPath;
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to find game path using Steam's appmanifest ACF files.
        /// </summary>
        private static string? FindGameViaSteamAppmanifest(string steamAppId, string? executableName, string? relativeExecutablePath)
        {
            // Check all drives for Steam installations
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);

            var steamAppsPaths = new List<string>();

            foreach (var drive in drives)
            {
                steamAppsPaths.Add(Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps"));
                steamAppsPaths.Add(Path.Combine(drive, "Program Files", "Steam", "steamapps"));
                steamAppsPaths.Add(Path.Combine(drive, "SteamLibrary", "steamapps"));
            }

            foreach (var steamAppsPath in steamAppsPaths)
            {
                if (!Directory.Exists(steamAppsPath)) continue;

                var manifestPath = Path.Combine(steamAppsPath, $"appmanifest_{steamAppId}.acf");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var installDir = ParseAcfFile(manifestPath, "installdir");
                    if (string.IsNullOrEmpty(installDir)) continue;

                    var gamePath = Path.Combine(steamAppsPath, "common", installDir);
                    if (!Directory.Exists(gamePath)) continue;

                    // Verify executable if specified
                    if (!string.IsNullOrEmpty(executableName))
                    {
                        var fullExePath = Path.Combine(gamePath, relativeExecutablePath ?? "", executableName);
                        if (!File.Exists(fullExePath)) continue;
                    }

                    return gamePath;
                }
                catch
                {
                    // Continue to next path on any error
                }
            }

            return null;
        }

        /// <summary>
        /// Simple parser for Valve's ACF (ASCII Configuration File) format.
        /// </summary>
        private static string? ParseAcfFile(string filePath, string key)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith($"\"{key}\""))
                    {
                        // Format: "installdir"		"Game Folder Name"
                        var value = trimmed.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries).Last();
                        return value.Trim('"');
                    }
                }
            }
            catch
            {
                // Return null on any parsing error
            }

            return null;
        }
    }
}
