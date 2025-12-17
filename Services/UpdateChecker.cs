using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace NewAxis.Services
{
    public class UpdateInfo
    {
        public int Version { get; set; }
        public string? DownloadUrl { get; set; }
    }

    public class UpdateChecker
    {
        private readonly GameRepositoryClient _repoClient;

        public UpdateChecker(GameRepositoryClient repoClient)
        {
            _repoClient = repoClient;
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                // We'll treat update.json as a game file download, but it returns bytes.
                // Repo Client handles local vs http.
                // We need to fetch into memory.

                // Hack: DownloadFileAsync saves to file.
                // But repoClient has internal HttpClient if http.
                // GameRepositoryClient isn't designed for "GetStringAsync" generic.
                // But we can download "update.json" to temp and read it.

                var tempPath = System.IO.Path.GetTempFileName();
                await _repoClient.DownloadFileAsync("update.json", tempPath);

                var json = await System.IO.File.ReadAllTextAsync(tempPath);
                System.IO.File.Delete(tempPath);

                var info = JsonSerializer.Deserialize<UpdateInfo>(json, AppJsonContext.Default.UpdateInfo);
                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
                return null;
            }
        }
    }
}
