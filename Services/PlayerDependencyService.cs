using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace NewAxis.Services
{
    public static class PlayerDependencyService
    {
        public readonly struct DependencyCheckResult
        {
            public DependencyCheckResult(
                bool success,
                string message,
                string[]? missingRequiredFiles = null,
                string repositorySource = "")
            {
                Success = success;
                Message = message;
                MissingRequiredFiles = missingRequiredFiles ?? Array.Empty<string>();
                RepositorySource = repositorySource;
            }

            public bool Success { get; }
            public string Message { get; }
            public string[] MissingRequiredFiles { get; }
            public string RepositorySource { get; }
        }

        private const string DefaultRepoBase = "https://raw.githubusercontent.com/marcussacana/NewAxisData/refs/heads/master/";

        // Expected SHA256 hash for the current remote libmpv-2.dll
        private const string ExpectedLibMpvHash = "9DFE29709F07948A30091102798B57A59E0C1FEE58286848F5DE5EB6C081C066";

        private static readonly string AppDir = AppContext.BaseDirectory;
        private static bool _checked;
        private static DependencyCheckResult _lastResult =
            new DependencyCheckResult(true, "OK", Array.Empty<string>(), string.Empty);
        private static readonly object Sync = new();

        private static string CalculateHash(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;
            
            try
            {
                using var stream = File.OpenRead(filePath);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hash = sha256.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch (Exception ex)
            {
                Log($"Failed to calculate SHA256 for {filePath} : {ex.Message}");
                return string.Empty;
            }
        }

        public static DependencyCheckResult EnsureNativeDependencies()
        {
            lock (Sync)
            {
                if (_checked)
                {
                    return _lastResult;
                }
            }

            DependencyCheckResult result;
            lock (Sync)
            {
                if (_checked)
                {
                    return _lastResult;
                }

                string repoSource = ResolveRepositorySource();
                var missingRequired = new System.Collections.Generic.List<string>();

                try
                {
                    if (!EnsureRuntimeFile("libmpv-2.dll", "Global/Runtime/VideoPlayer/libmpv-2.dll", ExpectedLibMpvHash))
                    {
                        missingRequired.Add("libmpv-2.dll");
                    }

                    // Optional for video player startup.
                    EnsureRuntimeFile("Leia3DBridge.dll", "Global/Runtime/Leia/Leia3DBridge.dll");
                }
                catch (Exception ex)
                {
                    Log($"Error while ensuring native dependencies: {ex.Message}");
                }

                if (missingRequired.Count == 0)
                {
                    result = new DependencyCheckResult(true, "OK", Array.Empty<string>(), repoSource);
                    _checked = true;
                    _lastResult = result;
                    return result;
                }

                string message =
                    $"Video player dependencies unavailable: {string.Join(", ", missingRequired)}.";
                Log(message);
                result = new DependencyCheckResult(false, message, missingRequired.ToArray(), repoSource);
                _lastResult = result;
                return result;
            }
        }

        private static bool EnsureRuntimeFile(string fileName, string relativeRepoPath, string? expectedHash = null)
        {
            string targetPath = Path.Combine(AppDir, fileName);
            bool exists = File.Exists(targetPath);

            if (exists && !string.IsNullOrWhiteSpace(expectedHash))
            {
                string localHash = CalculateHash(targetPath);
                if (string.Equals(localHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"File {fileName} exists and hash matches ({localHash}).");
                    return true;
                }
                Log($"File {fileName} hash mismatch. Expected: {expectedHash}, Got: {localHash}. Redownloading...");
            }
            else if (exists)
            {
                // No hash check requested, file exists
                return true;
            }

            string repoSource = ResolveRepositorySource();
            string tempPath = targetPath + ".tmp";

            try
            {
                var repoClient = new GameRepositoryClient(repoSource);
                Task.Run(() => repoClient.DownloadFileAsync(relativeRepoPath, tempPath)).GetAwaiter().GetResult();

                if (File.Exists(tempPath))
                {
                    // Move/overwrite existing file
                    File.Move(tempPath, targetPath, overwrite: true);

                    if (!string.IsNullOrWhiteSpace(expectedHash))
                    {
                        string newHash = CalculateHash(targetPath);
                        Log($"Downloaded {fileName} from repo: {repoSource} with hash: {newHash}");
                    }
                    else
                    {
                        Log($"Downloaded {fileName} from repo: {repoSource}");
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to download {fileName}: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            // If download failed and we still have the old file, just use the old file
            if (File.Exists(targetPath))
            {
                Log($"Using existing {fileName} despite failed update.");
                return true;
            }

            return false;
        }

        private static string ResolveRepositorySource()
        {
            if (!string.IsNullOrWhiteSpace(Program.CustomRepoPath))
            {
                return NormalizeRepositorySource(Program.CustomRepoPath);
            }

            string? repoOverride = TryGetRepoOverrideFromConfig();
            if (!string.IsNullOrWhiteSpace(repoOverride))
            {
                return NormalizeRepositorySource(repoOverride);
            }

            return DefaultRepoBase;
        }

        private static string NormalizeRepositorySource(string source)
        {
            source = source.Trim();
            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return source.TrimEnd('/', '\\') + "/";
            }

            return Path.GetFullPath(source);
        }

        private static string? TryGetRepoOverrideFromConfig()
        {
            foreach (string configPath in EnumerateConfigCandidates())
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                try
                {
                    var parser = new IniFileParser();
                    parser.Load(configPath);
                    string? value = parser.GetValue("Settings", "RepoOverride");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed to read repo override from {configPath}: {ex.Message}");
                }
            }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateConfigCandidates()
        {
            yield return Path.Combine(AppDir, "config.ini");
            yield return Path.Combine(Environment.CurrentDirectory, "config.ini");
            yield return Path.GetFullPath(Path.Combine(AppDir, "..", "..", "..", "config.ini"));
            yield return Path.GetFullPath(Path.Combine(AppDir, "..", "..", "..", "..", "config.ini"));
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[PlayerDependencyService] {message}");
            try
            {
                Trace.Write("PlayerDependencyService", message);
            }
            catch
            {
            }
        }
    }
}
