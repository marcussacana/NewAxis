using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;

namespace NewAxis.Services
{
    public class GameRepositoryClient
    {
        private const int MaxSplitPartsProbe = 64;
        private static readonly string[] GlobalRuntimeAssets =
        {
            "Global/Runtime/Leia/Leia3DBridge.dll",
            "Global/Runtime/VideoPlayer/libmpv-2.dll"
        };

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
                REPO_BASE = _baseUrl; // Update REPO_BASE to match the local path
                _isForceLocalMode = true;
            }
            else
            {
                _baseUrl = baseUrlOrPath.TrimEnd('/', '\\');
                _isForceLocalMode = false;
            }

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10); // Fast timeout for fallback
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NewAxis-Launcher");

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

        public async Task<CommunityModManifest?> GetCommunityModManifestAsync()
        {
            if (_isForceLocalMode)
            {
                return await GetLocalCommunityModManifestAsync();
            }

            try
            {
                var manifestUrl = $"{_baseUrl}/community.json";
                Trace.WriteLine($"Downloading community manifest: {manifestUrl}");
                var json = await _httpClient!.GetStringAsync(manifestUrl);
                Trace.WriteLine("Parsing online community manifest data");
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.CommunityModManifest);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to get online community manifest ({ex.Message}). Falling back to local.");
                return await GetLocalCommunityModManifestAsync();
            }
        }

        private async Task<CommunityModManifest?> GetLocalCommunityModManifestAsync()
        {
            var manifestPath = Path.Combine(REPO_BASE, "community.json");
            if (File.Exists(manifestPath))
            {
                Trace.WriteLine($"Reading local community manifest: {manifestPath}");
                var json = await File.ReadAllTextAsync(manifestPath);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.CommunityModManifest);
            }

            return null;
        }

        public async Task<DateTime?> GetOnlineRepoDateAsync()
        {
            if (_isForceLocalMode) return null;

            try
            {
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

            // Always attempt known global runtime assets used by 3D viewer and video player.
            foreach (var runtimeAsset in GlobalRuntimeAssets)
            {
                try
                {
                    await DownloadFileToLocalRepoAsync(runtimeAsset);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to download runtime asset {runtimeAsset}: {ex.Message}");
                }
            }

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

            string localPath = Path.Combine(REPO_BASE, relativeUrl);
            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await DownloadFileAsync(relativeUrl, localPath);
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
                else
                {
                    Trace.WriteLine($"[GameRepositoryClient] Image not found local: {fullLocalPath}");
                }
            }

            if (_httpClient != null && (isAbsoluteUrl || !_isForceLocalMode))
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
            bool isAbsoluteUrl = relativeUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 relativeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!isAbsoluteUrl)
            {
                var repoPath = Path.Combine(REPO_BASE, relativeUrl);
                if (File.Exists(repoPath))
                {
                    string fullRepoPath = Path.GetFullPath(repoPath);
                    string fullLocalPath = Path.GetFullPath(localPath);
                    if (string.Equals(fullRepoPath, fullLocalPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.WriteLine($"[GameRepositoryClient] File already available in local repo: {fullRepoPath}");
                        return;
                    }

                    Trace.WriteLine($"Copying from local repo: {repoPath}");
                    File.Copy(repoPath, localPath, true);
                    return;
                }

                if (await TryMergeSplitFileFromLocalRepoAsync(repoPath, relativeUrl, localPath))
                {
                    return;
                }

                Trace.WriteLine($"[GameRepositoryClient] File not found local: {Path.GetFullPath(repoPath)}");
            }

            if (!_isForceLocalMode && _httpClient != null)
            {
                string fullUrl = isAbsoluteUrl ? relativeUrl : $"{_baseUrl}/{relativeUrl}";

                try
                {
                    Trace.WriteLine($"Downloading file: {fullUrl}");
                    var bytes = await _httpClient.GetByteArrayAsync(fullUrl);
                    await File.WriteAllBytesAsync(localPath, bytes);
                    return;
                }
                catch (HttpRequestException ex) when (!isAbsoluteUrl && ex.StatusCode == HttpStatusCode.NotFound)
                {
                    Trace.WriteLine($"[GameRepositoryClient] Direct file missing online, trying split parts: {relativeUrl}");
                    if (await TryMergeSplitFileFromRemoteAsync(relativeUrl, localPath))
                    {
                        return;
                    }
                }
            }

            throw new FileNotFoundException($"File not found in local repo or online: {relativeUrl}");
        }

        private async Task<bool> TryMergeSplitFileFromLocalRepoAsync(string repoPath, string relativeUrl, string localPath)
        {
            var splitParts = FindLocalSplitParts(repoPath);
            if (splitParts.Count == 0)
            {
                return false;
            }

            Trace.WriteLine($"[GameRepositoryClient] Rebuilding split file from local repo: {relativeUrl} ({splitParts.Count} parts)");
            await MergePartsAsync(splitParts, localPath);
            return true;
        }

        private async Task<bool> TryMergeSplitFileFromRemoteAsync(string relativeUrl, string localPath)
        {
            int totalParts = await TryGetRemoteSplitPartCountAsync(relativeUrl);
            if (totalParts <= 0 || _httpClient == null)
            {
                return false;
            }

            string? tempDir = null;
            var partPaths = new List<string>(totalParts);

            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), "NewAxisRepoParts", GetSafeFilename($"{_baseUrl}/{relativeUrl}"));
                Directory.CreateDirectory(tempDir);

                for (int i = 1; i <= totalParts; i++)
                {
                    string partRelativeUrl = $"{relativeUrl}.{i:D3}-{totalParts:D3}";
                    string partUrl = $"{_baseUrl}/{partRelativeUrl}";
                    string partPath = Path.Combine(tempDir, Path.GetFileName(partRelativeUrl));

                    Trace.WriteLine($"[GameRepositoryClient] Downloading split part {i}/{totalParts}: {partRelativeUrl}");
                    using (var response = await _httpClient.GetAsync(partUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        await using var httpStream = await response.Content.ReadAsStreamAsync();
                        await using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await httpStream.CopyToAsync(fileStream);
                    }

                    partPaths.Add(partPath);
                }

                Trace.WriteLine($"[GameRepositoryClient] Rebuilding split file from remote repo: {relativeUrl} ({totalParts} parts)");
                await MergePartsAsync(partPaths, localPath);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[GameRepositoryClient] Failed to rebuild split remote file {relativeUrl}: {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task<int> TryGetRemoteSplitPartCountAsync(string relativeUrl)
        {
            for (int total = 2; total <= MaxSplitPartsProbe; total++)
            {
                string probeUrl = $"{_baseUrl}/{relativeUrl}.001-{total:D3}";
                if (await RemoteFileExistsAsync(probeUrl))
                {
                    return total;
                }
            }

            return 0;
        }

        private async Task<bool> RemoteFileExistsAsync(string url)
        {
            if (_httpClient == null)
            {
                return false;
            }

            try
            {
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead);
                if (headResponse.IsSuccessStatusCode)
                {
                    return true;
                }

                if (headResponse.StatusCode != HttpStatusCode.MethodNotAllowed &&
                    headResponse.StatusCode != HttpStatusCode.NotImplemented)
                {
                    return false;
                }
            }
            catch
            {
            }

            try
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                getRequest.Headers.Range = new RangeHeaderValue(0, 0);
                using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead);
                return getResponse.IsSuccessStatusCode || getResponse.StatusCode == HttpStatusCode.PartialContent;
            }
            catch
            {
                return false;
            }
        }

        private static IReadOnlyList<string> FindLocalSplitParts(string repoPath)
        {
            string? directory = Path.GetDirectoryName(repoPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            string fileName = Path.GetFileName(repoPath);
            string prefix = fileName + ".";
            var parts = new List<(string Path, int PartNumber, int TotalParts)>();

            foreach (var path in Directory.EnumerateFiles(directory, $"{fileName}.*-*"))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string suffix = name.Substring(prefix.Length);
                var split = suffix.Split('-', 2, StringSplitOptions.None);
                if (split.Length != 2 ||
                    !int.TryParse(split[0], out int partNumber) ||
                    !int.TryParse(split[1], out int totalParts) ||
                    partNumber <= 0 ||
                    totalParts <= 1)
                {
                    continue;
                }

                parts.Add((path, partNumber, totalParts));
            }

            if (parts.Count == 0)
            {
                return Array.Empty<string>();
            }

            parts = parts.OrderBy(x => x.PartNumber).ToList();
            int expectedTotalParts = parts[0].TotalParts;

            if (parts.Any(x => x.TotalParts != expectedTotalParts))
            {
                return Array.Empty<string>();
            }

            if (parts.Count != expectedTotalParts)
            {
                return Array.Empty<string>();
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].PartNumber != i + 1)
                {
                    return Array.Empty<string>();
                }
            }

            return parts.Select(x => x.Path).ToArray();
        }

        private static async Task MergePartsAsync(IEnumerable<string> partPaths, string destinationPath)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = destinationPath + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            await using (var destinationStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var partPath in partPaths)
                {
                    await using var sourceStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await sourceStream.CopyToAsync(destinationStream);
                }
            }

            File.Move(tempPath, destinationPath, true);
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
        public string? CommunityModPath { get; set; }
        public string? CommunityReshadeEntryPoint { get; set; }
        public string? CommunityModType { get; set; }
        public string? CommunityCredit { get; set; }

        public ImageUrls? Images { get; set; }
        public string? ConfigArchivePath { get; set; }
    }

    public class CommunityModManifest
    {
        public string? GameName { get; set; }
        public string? ModPath { get; set; }
        public string? ReshadeEntryPoint { get; set; }
        public string? ModType { get; set; }
        public string? Credit { get; set; }
        public string? SteamAppId { get; set; }
        public string? ExecutablePath { get; set; }
        public string? RelativeExecutablePath { get; set; }
        public string? LogoUrl { get; set; }
        public string? BannerUrl { get; set; }
        public string? IconUrl { get; set; }
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
