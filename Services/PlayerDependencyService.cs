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
        private static readonly string AppDir = AppContext.BaseDirectory;
        private static bool _checked;
        private static DependencyCheckResult _lastResult =
            new DependencyCheckResult(true, "OK", Array.Empty<string>(), string.Empty);
        private static readonly object Sync = new();

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
                    if (!EnsureRuntimeFile("libmpv-2.dll", "Global/Runtime/VideoPlayer/libmpv-2.dll"))
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

        private static bool EnsureRuntimeFile(string fileName, string relativeRepoPath)
        {
            string targetPath = Path.Combine(AppDir, fileName);
            if (File.Exists(targetPath))
            {
                return true;
            }

            string repoSource = ResolveRepositorySource();
            try
            {
                var repoClient = new GameRepositoryClient(repoSource);
                Task.Run(() => repoClient.DownloadFileAsync(relativeRepoPath, targetPath)).GetAwaiter().GetResult();
                Log($"Downloaded {fileName} from repo: {repoSource}");
                return File.Exists(targetPath);
            }
            catch (Exception ex)
            {
                Log($"Failed to download {fileName}: {ex.Message}");
                return File.Exists(targetPath);
            }
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
