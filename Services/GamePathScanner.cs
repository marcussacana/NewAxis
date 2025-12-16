using System;
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
        /// <param name="directoryName">The expected directory name of the game (e.g. "GodOfWar").</param>
        /// <param name="executableName">Optional: The executable name to verify presence.</param>
        /// <param name="relativeExecutablePath">Optional: Relative path from game dir to executable.</param>
        /// <returns>The full path if found, or null.</returns>
        public static string? FindGameDirectory(string directoryName, string? executableName = null, string? relativeExecutablePath = null)
        {
            if (string.IsNullOrWhiteSpace(directoryName)) return null;

            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);

            foreach (var driveRoot in drives)
            {
                foreach (var suffix in SteamLibrarySuffixes)
                {
                    var potentialPath = Path.Combine(driveRoot, suffix, directoryName);

                    if (Directory.Exists(potentialPath))
                    {
                        // If executable name is provided, verify it exists
                        if (!string.IsNullOrEmpty(executableName))
                        {
                            var fullExePath = Path.Combine(potentialPath, relativeExecutablePath ?? "", executableName);
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

            return null;
        }
    }
}
