using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;

namespace NewAxis.Services
{
    public class GameRepositoryClient
    {
        public string REPO_BASE = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ModRepository");

        private HttpClient? _httpClient;
        private readonly string _baseUrl;
        private readonly bool _isForceLocalMode;

        public GameRepositoryClient(string baseUrlOrPath)
        {
            // If the input is essentially the local repo path, force local mode
            bool inputIsLocal = !baseUrlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !baseUrlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (inputIsLocal)
            {
                _baseUrl = Path.GetFullPath(baseUrlOrPath);
                _isForceLocalMode = true;
            }
            else
            {
                _baseUrl = baseUrlOrPath.TrimEnd('/', '\\');
                _httpClient = new HttpClient();
                _httpClient.Timeout = TimeSpan.FromSeconds(10); // Fast timeout for fallback
                _isForceLocalMode = false;
            }

            Trace.WriteLine($"Repository Mode: {(_isForceLocalMode ? "LOCAL-ONLY" : "HYBRID (Online -> Local)")}");
            Trace.WriteLine($"Base URL: {_baseUrl}");
        }

        public async Task<GameIndex> GetGameIndexAsync()
        {
            if (_isForceLocalMode)
            {
                return await GetLocalGameIndexAsync();
            }

            try
            {
                var indexUrl = $"{_baseUrl}/index.json";
                Trace.WriteLine($"Downloading index: {indexUrl}");
                var json = await _httpClient!.GetStringAsync(indexUrl);
                Trace.WriteLine("Parsing online index data");
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.GameIndex)!;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to get online index ({ex.Message}). Falling back to local.");
                return await GetLocalGameIndexAsync();
            }
        }

        private async Task<GameIndex> GetLocalGameIndexAsync()
        {
            var indexPath = Path.Combine(REPO_BASE, "index.json");
            if (File.Exists(indexPath))
            {
                Trace.WriteLine($"Reading local index: {indexPath}");
                var json = await File.ReadAllTextAsync(indexPath);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.GameIndex)!;
            }

            throw new FileNotFoundException("Game index not found locally or online.", indexPath);
        }

        public async Task<DateTime?> GetOnlineRepoDateAsync()
        {
            if (_isForceLocalMode) return null;

            try
            {
                // We fetch the index solely to check the generated date
                var index = await GetGameIndexAsync();

                if (DateTime.TryParse(index.GeneratedAt, out var date))
                {
                    return date;
                }
            }
            catch
            {
                // Ignore errors during check
            }
            return null;
        }

        public async Task<DateTime?> GetLocalRepoDateAsync()
        {
            try
            {
                var index = await GetLocalGameIndexAsync();
                if (DateTime.TryParse(index.GeneratedAt, out var date))
                {
                    return date;
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        public async Task DownloadEntireRepoAsync(IProgress<string> progress)
        {
            if (_isForceLocalMode || _httpClient == null) return;

            Directory.CreateDirectory(REPO_BASE);

            // Strategy 1: GitHub Zip Download
            if (TryGetGitHubZipUrl(_baseUrl, out string zipUrl))
            {
                try
                {
                    progress.Report(LocalizationService.Instance["LargeDownloadWarning"]);
                    await Task.Delay(2000);

                    progress.Report(LocalizationService.Instance["DownloadingRepoArchive"]);
                    Trace.WriteLine($"Attempting GitHub Zip download: {zipUrl}");

                    using var response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var tempZipPath = Path.GetTempFileName();
                    using (var fs = File.Create(tempZipPath))
                    {
                        await response.Content.CopyToAsync(fs);
                    }

                    progress.Report(LocalizationService.Instance["ExtractingRepo"]);
                    Trace.WriteLine("Extracting zip...");

                    using (var zip = ZipFile.OpenRead(tempZipPath))
                    {
                        // Identify root folder (GitHub zips often have a root folder with the repo name)
                        var firstEntry = zip.Entries.FirstOrDefault();
                        string? rootDir = firstEntry?.FullName.Split('/')[0];

                        foreach (var entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry

                            string entryPath = entry.FullName;
                            if (!string.IsNullOrEmpty(rootDir) && entryPath.StartsWith(rootDir))
                            {
                                entryPath = entryPath.Substring(rootDir.Length).TrimStart('/', '\\');
                            }

                            if (string.IsNullOrEmpty(entryPath)) continue;

                            var fullPath = Path.Combine(REPO_BASE, entryPath);
                            var directory = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(directory))
                                Directory.CreateDirectory(directory);

                            entry.ExtractToFile(fullPath, true);
                        }
                    }

                    File.Delete(tempZipPath);
                    progress.Report(LocalizationService.Instance["RepoUpdateSuccess"]);
                    return;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"GitHub Zip download failed: {ex.Message}. Falling back to crawling.");
                }
            }

            // Strategy 2: Crawling (Fallback)
            await DownloadRepoByCrawlingAsync(progress);
        }

        private bool TryGetGitHubZipUrl(string baseUrl, out string zipUrl)
        {
            zipUrl = "";
            // Expected format: https://raw.githubusercontent.com/{User}/{Repo}/refs/heads/{Branch}/
            // Target format: https://github.com/{User}/{Repo}/archive/refs/heads/{Branch}.zip

            if (baseUrl.Contains("raw.githubusercontent.com"))
            {
                var parts = baseUrl.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 7)
                {
                    var user = parts[2];
                    var repo = parts[3];
                    var branch = parts[6];

                    zipUrl = $"https://github.com/{user}/{repo}/archive/refs/heads/{branch}.zip";
                    return true;
                }
            }
            return false;
        }

        private async Task DownloadRepoByCrawlingAsync(IProgress<string> progress)
        {
            progress.Report(LocalizationService.Instance["DownloadingIndex"]);
            var index = await GetGameIndexAsync();

            // Save index
            var indexJson = JsonSerializer.Serialize(index, AppJsonContext.Default.GameIndex);
            await File.WriteAllBytesAsync(Path.Combine(REPO_BASE, "index.json"), System.Text.Encoding.UTF8.GetBytes(indexJson));

            if (index.Games == null) return;

            int total = index.Games.Count;
            int current = 0;

            foreach (var game in index.Games)
            {
                current++;
                progress.Report(string.Format(LocalizationService.Instance["ProcessingGame"], current, total, game.GameName));

                var assets = new List<string?>
                {
                    game.ConfigArchivePath,
                    game.MigotoPath,
                    game.ReshadePath,
                    game.ShaderPath,
                    game.ShaderMod,
                    game.NativeReshadeDll,
                    game.Images?.Icon,
                    game.Images?.Logo,
                    game.Images?.Wallpaper
                };

                foreach (var asset in assets)
                {
                    if (string.IsNullOrEmpty(asset)) continue;

                    try
                    {
                        await DownloadFileToLocalRepoAsync(asset);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Failed to download asset {asset}: {ex.Message}");
                    }
                }
            }
        }

        private async Task DownloadFileToLocalRepoAsync(string relativeUrl)
        {
            if (relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string fullUrl = $"{_baseUrl}/{relativeUrl}";
            string localPath = Path.Combine(REPO_BASE, relativeUrl);

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (_httpClient != null)
            {
                var bytes = await _httpClient.GetByteArrayAsync(fullUrl);
                await File.WriteAllBytesAsync(localPath, bytes);
            }
        }

        public async Task<byte[]> DownloadImageAsync(string urlOrPath)
        {
            bool isAbsoluteUrl = urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (!isAbsoluteUrl)
            {
                var fullLocalPath = Path.Combine(REPO_BASE, urlOrPath);
                if (File.Exists(fullLocalPath))
                {
                    return await File.ReadAllBytesAsync(fullLocalPath);
                }
            }

            if (!_isForceLocalMode && _httpClient != null)
            {
                try
                {
                    string sourceUrl = isAbsoluteUrl ? urlOrPath : $"{_baseUrl}/{urlOrPath}";

                    var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
                    Directory.CreateDirectory(cacheDir);
                    var fileName = GetSafeFilename(sourceUrl);
                    var cachePath = Path.Combine(cacheDir, fileName);

                    if (File.Exists(cachePath)) return await File.ReadAllBytesAsync(cachePath);

                    var bytes = await _httpClient.GetByteArrayAsync(sourceUrl);
                    await File.WriteAllBytesAsync(cachePath, bytes);
                    return bytes;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Online image download failed: {ex.Message}");
                }
            }

            return Array.Empty<byte>();
        }

        private string GetSafeFilename(string url)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
                return BitConverter.ToString(hash).Replace("-", "").ToLower() + ".png";
            }
        }

        public async Task DownloadFileAsync(string relativeUrl, string localPath)
        {
            var repoPath = Path.Combine(REPO_BASE, relativeUrl);
            if (File.Exists(repoPath))
            {
                Trace.WriteLine($"Copying from local repo: {repoPath}");
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(repoPath, localPath, true);
                return;
            }

            if (!_isForceLocalMode && _httpClient != null)
            {
                string fullUrl = relativeUrl;
                if (!relativeUrl.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
                    fullUrl = $"{_baseUrl}/{relativeUrl}";

                Trace.WriteLine($"Downloading file: {fullUrl}");
                var bytes = await _httpClient.GetByteArrayAsync(fullUrl);

                var directory = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(localPath, bytes);
                return;
            }

            throw new FileNotFoundException($"File not found in local repo or online: {relativeUrl}");
        }

        public bool IsLocalMode => _isForceLocalMode;
    }

    public class GameIndex
    {
        public string? GeneratedAt { get; set; }
        public int TotalGames { get; set; }
        public List<GameIndexEntry>? Games { get; set; }
    }

    public class GameIndexEntry
    {
        public string? GameName { get; set; }
        public string? GameDirectory { get; set; }
        public string? ExecutablePath { get; set; }
        public string? SteamAppId { get; set; }
        public string? DirectoryName { get; set; }
        public string? RelativeExecutablePath { get; set; }
        public string? InjectionScript { get; set; }
        public string? Creator { get; set; }
        public bool HasCustomConfig { get; set; }

        public string? ShaderMod { get; set; }
        public string? MigotoPath { get; set; }
        public string? ReshadePath { get; set; }
        public string? TargetDllFileName { get; set; }
        public string? ReshadePresetPlus { get; set; }
        public string? ReshadePresetNative { get; set; }
        public string? ShaderPath { get; set; }
        public long? UseAspectRatioHeuristics { get; set; }
        public long? DepthCopyBeforeClears { get; set; }
        public string? SettingsPlus { get; set; }
        public string? SettingsUltra { get; set; }
        public string? SettingsNative { get; set; }
        public string? D3DXSettings { get; set; }
        public string? NativeReshade { get; set; }
        public string? NativeReshadeDll { get; set; }
        public string? StartArgsUltra { get; set; }
        public string? StartArgsPlus { get; set; }
        public string? StartArgsNative { get; set; }

        public ImageUrls? Images { get; set; }
        public string? ConfigArchivePath { get; set; }
    }

    public class ImageUrls
    {
        public string? Logo { get; set; }
        public string? Wallpaper { get; set; }
        public string? Icon { get; set; }
    }

    public class FileUrls
    {
        public string? ConfigArchive { get; set; }
        public string? Assets { get; set; }
    }
}
