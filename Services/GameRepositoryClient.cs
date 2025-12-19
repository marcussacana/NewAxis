using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NewAxis.Services
{
    public class GameRepositoryClient
    {
        private const string REPO_BASE = @".\GameDownloader\Downloads";

        private HttpClient? _httpClient;
        private readonly string _baseUrl;
        private readonly bool _isLocalPath;

        public GameRepositoryClient(string baseUrlOrPath)
        {
            _baseUrl = (baseUrlOrPath ?? REPO_BASE).TrimEnd('/', '\\');

            // Detecta se é caminho local ou URL
            _isLocalPath = Directory.Exists(_baseUrl) ||
                           (!_baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                            !_baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            if (!_isLocalPath)
            {
                _httpClient = new HttpClient();
            }

            Console.WriteLine($"Repository Mode: {(_isLocalPath ? "LOCAL" : "HTTP")}");
            Console.WriteLine($"Base Path: {_baseUrl}");
        }

        public async Task<GameIndex> GetGameIndexAsync()
        {
            string json;

            if (_isLocalPath)
            {
                var indexPath = Path.Combine(_baseUrl, "index.json");
                Console.WriteLine($"Reading local index: {indexPath}");
                json = await File.ReadAllTextAsync(indexPath);
            }
            else
            {
                var indexUrl = $"{_baseUrl}/index.json";
                Console.WriteLine($"Downloading index: {indexUrl}");
                json = await _httpClient!.GetStringAsync(indexUrl);
            }

            Console.WriteLine("Parsing index data");
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.GameIndex)!;
        }

        public async Task<byte[]> DownloadImageAsync(string urlOrPath)
        {
            // Determine effective source (Remote URL or Local Path)
            string sourceUrl = urlOrPath;
            bool isAbsoluteUrl = urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (!isAbsoluteUrl)
            {
                // Relative path? Combine with base
                if (_isLocalPath)
                {
                    // Local Repository Mode: Read directly from file
                    var fullLocalPath = Path.Combine(_baseUrl, urlOrPath);
                    if (File.Exists(fullLocalPath))
                    {
                        return await File.ReadAllBytesAsync(fullLocalPath);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Local image not found: {fullLocalPath}");
                        return Array.Empty<byte>();
                    }
                }
                else
                {
                    // HTTP Repository Mode: Combine URL
                    sourceUrl = $"{_baseUrl}/{urlOrPath}";
                }
            }

            // --- Caching Logic (Only for remote URLs) ---

            // Cache directory relative to executable
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Generate filename from URL hash
            var fileName = GetSafeFilename(sourceUrl);
            var filePath = Path.Combine(cacheDir, fileName);

            // 1. Try Cache
            if (File.Exists(filePath))
            {
                return await File.ReadAllBytesAsync(filePath);
            }

            // 2. Try Download (Remote)
            try
            {
                if (_httpClient == null)
                {
                    _httpClient = new HttpClient();
                }

                var bytes = await _httpClient.GetByteArrayAsync(sourceUrl);

                // 3. Save to Cache
                await File.WriteAllBytesAsync(filePath, bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download failed for {sourceUrl}: {ex.Message}");
                throw;
            }
        }

        private string GetSafeFilename(string url)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
                return BitConverter.ToString(hash).Replace("-", "").ToLower() + ".png"; // Assume png or just data
            }
        }

        public async Task DownloadFileAsync(string relativeUrl, string localPath)
        {
            byte[] bytes;

            if (_isLocalPath)
            {
                var sourcePath = Path.Combine(_baseUrl, relativeUrl);
                Console.WriteLine($"Copying local file: {sourcePath}");
                bytes = await File.ReadAllBytesAsync(sourcePath);
            }
            else
            {
                var fullUrl = $"{_baseUrl}/{relativeUrl}";
                Console.WriteLine($"Downloading file: {fullUrl}");
                bytes = await _httpClient!.GetByteArrayAsync(fullUrl);
            }

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(localPath, bytes);
        }

        public bool IsLocalMode => _isLocalPath;
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

        // 3D Mod related properties
        public string? ShaderMod { get; set; }
        public string? MigotoPath { get; set; }
        public string? ReshadePath { get; set; }
        public string? TargetDllFileName { get; set; }
        public string? ReshadePresetPlus { get; set; }
        public string? OverwatchPath { get; set; }
        public long? UseAspectRatioHeuristics { get; set; }
        public long? DepthCopyBeforeClears { get; set; }
        public string? SettingsPlus { get; set; }
        public string? SettingsUltra { get; set; }
        public string? D3DXSettings { get; set; }

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
