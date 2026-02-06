using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace NewAxis.Services
{
    public class UpdateInfo
    {
        public double Version { get; set; }
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
                var tempPath = System.IO.Path.GetTempFileName();
                await _repoClient.DownloadFileAsync("update.json", tempPath);

                var json = await System.IO.File.ReadAllTextAsync(tempPath);
                System.IO.File.Delete(tempPath);

                var info = JsonSerializer.Deserialize<UpdateInfo>(json, AppJsonContext.Default.UpdateInfo);
                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Update check failed: {ex.Message}");
                return null;
            }
        }
    }
}
