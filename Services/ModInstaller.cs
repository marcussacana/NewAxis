using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NewAxis.Models;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Threading;

namespace NewAxis.Services
{
    public class ModInstallationSettings
    {
        public double Depth { get; set; }
        public double Popout { get; set; }
        public bool DisableBlacklistedDlls { get; set; } = true;

        public HotkeyDefinition? DepthInc { get; set; }
        public HotkeyDefinition? DepthDec { get; set; }
        public HotkeyDefinition? PopoutInc { get; set; }
        public HotkeyDefinition? PopoutDec { get; set; }
    }

    public class HotkeyDefinition
    {
        public Avalonia.Input.Key Key { get; set; }
        public Avalonia.Input.KeyModifiers Modifiers { get; set; }
    }

    public class ModProgressInfo
    {
        public string Status { get; set; } = string.Empty;
        public int Processed { get; set; }
        public int? Total { get; set; }
        public string? Detail { get; set; }
    }

    public class ModInstaller
    {
        private const string MOD_FILES_LIST = "3dfiles.txt";

        //ShaderToggler.addon must be deleted at least on God Of War for 3D+ works, not sure if is with all games
        private static readonly string[] _blacklistedFiles = { "nvngx_dlss.dll", "nvngx_dlssg.dll", "ShaderToggler.addon" };

        public static async Task<List<string>> InstallModAsync(
            Models.Game game,
            NewAxis.Models.ModType modType,
            GameRepositoryClient repoClient,
            ModInstallationSettings settings,
            IProgress<ModProgressInfo>? progress = null)
        {
            var installedFiles = new List<string>();

            var gameInstallPath = game.InstallPath;
            if (string.IsNullOrEmpty(gameInstallPath)) throw new Exception("Game install path is empty");

            if (!(game.Tag is GameIndexEntry gameEntry))
            {
                throw new Exception("Game metadata (Tag) is missing or invalid");
            }

            var executablePath = gameEntry.ExecutablePath ?? "";
            var relativeExecutablePath = gameEntry.RelativeExecutablePath ?? "";
            var targetDirectory = Path.Combine(gameInstallPath, relativeExecutablePath);
            var fullExecutablePath = Path.Combine(gameInstallPath, relativeExecutablePath, Path.GetFileName(executablePath));

            if (!File.Exists(fullExecutablePath) && File.Exists(Path.Combine(gameInstallPath, executablePath)))
            {
                fullExecutablePath = Path.Combine(gameInstallPath, executablePath);
            }
            else if (!File.Exists(fullExecutablePath))
            {
                var executables = Directory.EnumerateFiles(gameInstallPath, "*.exe", SearchOption.AllDirectories)
                    .Where(x => !Path.GetFileName(x).Contains("launch", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("web", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("crash", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("install", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("editor", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("physx", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("ENU", StringComparison.InvariantCultureIgnoreCase))
                    .OrderByDescending(x => new FileInfo(x).Length);

                if (executables.Any())
                {
                    fullExecutablePath = executables.First();
                }
            }

            if (File.Exists(fullExecutablePath))
            {
                // Install next to the resolved executable to avoid wrong installs when metadata path is stale/missing.
                targetDirectory = Path.GetDirectoryName(fullExecutablePath) ?? targetDirectory;
            }

            Trace.WriteLine($"[ModInstaller] Installing {modType.GetDescription()} mod for {game.Name}...");
            Trace.WriteLine($"[ModInstaller] Target directory: {targetDirectory}");
            Trace.WriteLine($"[ModInstaller] Resolved executable: {fullExecutablePath}");

            var progressTracker = new ModInstallProgressTracker("Preparing", progress);

            try
            {
                string? settingsJson = null;
                if (modType == ModType.ThreeDPlus)
                {
                    settingsJson = gameEntry.SettingsPlus;
                }
                else if (modType == ModType.ThreeDUltra)
                {
                    settingsJson = gameEntry.SettingsUltra;
                }
                else if (modType == ModType.Native)
                {
                    settingsJson = gameEntry.SettingsNative;
                }

                if (!string.IsNullOrEmpty(gameEntry.ConfigArchivePath))
                {
                    Trace.WriteLine($"[ModInstaller] Found ConfigArchive (Mode: {modType}), installing...");
                    var configLocalPath = await DownloadFileAsync(repoClient, gameEntry.ConfigArchivePath);
                    progressTracker.AddToTotal(CountArchiveFiles(configLocalPath));
                    var configFiles = await ConfigExtractor.ExtractConfigAsync(
                        configLocalPath,
                        targetDirectory,
                        settingsJson,
                        gameEntry,
                        () => progressTracker.Advance());
                    installedFiles.AddRange(configFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));
                }
                if (modType == ModType.ThreeDPlus)
                {
                    // Hardcode reshade.zip for 3D+ as it's now static
                    var reshadePath = "Global/Reshade/reshade.zip";

                    var reshadeLocalPath = await DownloadFileAsync(repoClient, reshadePath);

                    string? shaderLocalPath = null;
                    if (!string.IsNullOrEmpty(gameEntry.ShaderPath))
                    {
                        Trace.WriteLine($"[ModInstaller] Found Shader (Hash: {gameEntry.ShaderPath}), Skipping...");
                        //shaderLocalPath = await DownloadFileAsync(repoClient, gameEntry.ShaderPath);
                    }

                    progressTracker.AddToTotal(CountArchiveFiles(reshadeLocalPath, entry =>
                        !ReshadeExtractor.IsObsoleteReshadeFile(Path.GetFileName(entry.Key ?? string.Empty))) + 2);

                    if (!string.IsNullOrEmpty(shaderLocalPath))
                    {
                        progressTracker.AddToTotal(1);
                    }

                    var reshadeFiles = await ReshadeExtractor.ExtractReshadeAsync(new ReshadeExtractionContext
                    {
                        Reshade7zPath = reshadeLocalPath,
                        TargetDirectory = targetDirectory,
                        ExecutablePath = fullExecutablePath,
                        GameEntry = gameEntry,
                        ShaderPath = shaderLocalPath
                    }, () => progressTracker.Advance());

                    installedFiles.AddRange(reshadeFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));

                    // Install 3DGameBridge
                    try
                    {
                        Trace.WriteLine("[ModInstaller] Installing 3DGameBridge...");

                        var bridgeUrl = "Global/3DGameBridge.addon";
                        Trace.WriteLine($"[ModInstaller] Downloading {bridgeUrl}...");
                        var gameBridge = await DownloadFileAsync(repoClient, bridgeUrl);
                        progressTracker.AddToTotal(1);

                        // Install 3DGameBridge.addon to game directory
                        var bridgeDest = Path.Combine(targetDirectory, "3DGameBridge.addon");

                        File.Copy(gameBridge, bridgeDest, true);
                        installedFiles.Add(Path.GetRelativePath(gameInstallPath, bridgeDest));
                        progressTracker.Advance();

                        Trace.WriteLine($"[ModInstaller] Installed 3DGameBridge.addon to {bridgeDest}");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[ModInstaller] Failed to install 3DGameBridge: {ex}");
                        System.Diagnostics.Debug.WriteLine($"[ModInstaller] Exception details: {ex.ToString()}");
                    }
                }
                else if (modType == ModType.ThreeDUltra)
                {
                    if (string.IsNullOrEmpty(gameEntry.MigotoPath))
                    {
                        throw new Exception("MigotoPath not configured");
                    }

                    var migotoLocalPath = await DownloadFileAsync(repoClient, gameEntry.MigotoPath);
                    progressTracker.AddToTotal(CountArchiveFiles(migotoLocalPath));
                    var migotoFiles = await MigotoExtractor.ExtractMigotoAsync(
                        migotoLocalPath,
                        targetDirectory,
                        fullExecutablePath,
                        () => progressTracker.Advance());

                    installedFiles.AddRange(migotoFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));

                    if (!string.IsNullOrEmpty(gameEntry.ShaderMod))
                    {
                        var shaderLocalPath = await DownloadFileAsync(repoClient, gameEntry.ShaderMod);
                        progressTracker.AddToTotal(CountArchiveFiles(shaderLocalPath));
                        var shaderFiles = await MigotoExtractor.ExtractMigotoAsync(
                            shaderLocalPath,
                            targetDirectory,
                            fullExecutablePath,
                            () => progressTracker.Advance());

                        installedFiles.AddRange(shaderFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));
                    }

                    await CreateTrueGameIniAsync(targetDirectory, settings);
                    progressTracker.AddToTotal(1);
                    progressTracker.Advance();

                    var d3dxPath = Path.Combine(targetDirectory, "d3dx.ini");
                    var d3dxRelPath = Path.GetRelativePath(gameInstallPath, d3dxPath);


                    if (File.Exists(d3dxPath))
                    {
                        var backupPath = d3dxPath + ".disabled";
                        if (File.Exists(backupPath)) File.Delete(backupPath);
                        File.Move(d3dxPath, backupPath);
                        Trace.WriteLine($"[ModInstaller] Backed up d3dx.ini to .disabled");
                    }

                    var specialPath = HasUnicodeChars(targetDirectory);

                    var d3dxContent = specialPath ? $"" : $"[Rendering]\r\nbase_path_override={targetDirectory}";

                    if (!string.IsNullOrEmpty(gameEntry.D3DXSettings))
                    {
                        d3dxContent = gameEntry.D3DXSettings + "\r\n\r\n" + d3dxContent;
                        Trace.WriteLine($"[ModInstaller] Applied D3DXSettings override (Length: {gameEntry.D3DXSettings.Length})");
                    }

                    if (!string.IsNullOrWhiteSpace(d3dxContent))
                    {
                        await File.WriteAllTextAsync(d3dxPath, d3dxContent, new UTF8Encoding(false));
                        Trace.WriteLine($"[ModInstaller] Generated d3dx.ini pointing to {targetDirectory}");
                        progressTracker.AddToTotal(1);
                        progressTracker.Advance();

                        if (!installedFiles.Contains(d3dxRelPath))
                        {
                            installedFiles.Add(d3dxRelPath);
                        }
                    }
                    else if (File.Exists(d3dxPath)) // for users with old d3dx.ini
                    {
                        try
                        {
                            File.Delete(d3dxPath);
                            Trace.WriteLine($"[ModInstaller] Deleted old d3dx.ini");
                        }
                        catch (Exception e)
                        {
                            Trace.WriteLine($"[ModInstaller] Failed to delete d3dx.ini: {e.Message}");
                        }
                    }
                }
                else if (modType == ModType.Native)
                {
                    if (!string.IsNullOrEmpty(gameEntry.CommunityModPath))
                    {
                        Trace.WriteLine("[ModInstaller] Installing community mod package...");
                        var communityArchivePath = await DownloadFileAsync(repoClient, gameEntry.CommunityModPath);
                        progressTracker.AddToTotal(CountArchiveFiles(
                            communityArchivePath,
                            entry => !string.Equals(Path.GetFileName(entry.Key ?? string.Empty), gameEntry.CommunityReshadeEntryPoint, StringComparison.OrdinalIgnoreCase),
                            logEntries: true,
                            logContext: "community mod package"));
                        var communityFiles = await ExtractCommunityModAsync(
                            communityArchivePath,
                            targetDirectory,
                            gameEntry.CommunityReshadeEntryPoint,
                            () => progressTracker.Advance());

                        installedFiles.AddRange(communityFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));

                        if (!string.IsNullOrWhiteSpace(gameEntry.CommunityReshadeEntryPoint))
                        {
                            var reshadeLocalPath = await DownloadFileAsync(repoClient, "Global/Reshade/reshade.zip");
                            progressTracker.AddToTotal(CountArchiveFiles(reshadeLocalPath, entry =>
                                !ReshadeExtractor.IsObsoleteReshadeFile(Path.GetFileName(entry.Key ?? string.Empty))));
                            var reshadeFiles = await ReshadeExtractor.ExtractRuntimeOnlyAsync(new ReshadeExtractionContext
                            {
                                Reshade7zPath = reshadeLocalPath,
                                TargetDirectory = targetDirectory,
                                ExecutablePath = fullExecutablePath,
                                GameEntry = gameEntry
                            }, gameEntry.CommunityReshadeEntryPoint!, () => progressTracker.Advance());

                            installedFiles.AddRange(reshadeFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));

                            try
                            {
                                Trace.WriteLine("[ModInstaller] Installing 3DGameBridge for community mod...");
                                var bridgePath = await DownloadFileAsync(repoClient, "Global/3DGameBridge.addon");
                                progressTracker.AddToTotal(1);
                                var bridgeDest = Path.Combine(targetDirectory, "3DGameBridge.addon");
                                File.Copy(bridgePath, bridgeDest, true);
                                installedFiles.Add(Path.GetRelativePath(gameInstallPath, bridgeDest));
                                progressTracker.Advance();
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine($"[ModInstaller] Failed to install 3DGameBridge for community mod: {ex}");
                            }
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(gameEntry.NativeReshade) || string.IsNullOrEmpty(gameEntry.NativeReshadeDll))
                        {
                            throw new Exception("NativeReshade or NativeReshadeDll not configured");
                        }

                        Trace.WriteLine($"[ModInstaller] Installing Native Reshade mode...");
                        var nativeReshadeLocalPath = await DownloadFileAsync(repoClient, gameEntry.NativeReshade);
                        progressTracker.AddToTotal(CountArchiveFiles(nativeReshadeLocalPath) + 2);
                        var nativeFiles = await ReshadeExtractor.ExtractNativeReshadeAsync(new ReshadeExtractionContext
                        {
                            Reshade7zPath = nativeReshadeLocalPath,
                            TargetDirectory = targetDirectory,
                            ExecutablePath = fullExecutablePath,
                            GameEntry = gameEntry
                        }, () => progressTracker.Advance());

                        installedFiles.AddRange(nativeFiles.Select(p => Path.GetRelativePath(gameInstallPath, p)));
                    }
                }

                ProcessBlacklist(installedFiles, gameInstallPath, targetDirectory, settings.DisableBlacklistedDlls);


                var filesListPath = Path.Combine(gameInstallPath, MOD_FILES_LIST);
                await File.WriteAllLinesAsync(filesListPath, installedFiles);
                progressTracker.AddToTotal(1);
                progressTracker.Advance();
                Trace.WriteLine($"[ModInstaller] Created {MOD_FILES_LIST} with {installedFiles.Count} entries");
                progressTracker.Complete();

                return installedFiles;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ModInstaller] Error: {ex.Message}");
                throw;
            }
        }

        private static async Task<List<string>> ExtractCommunityModAsync(string archivePath, string targetDirectory, string? excludedDllName, Action? onInstalled = null)
        {
            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException($"Community mod archive not found: {archivePath}");
            }

            return await ExtractCommunityModWithSharpCompressAsync(archivePath, targetDirectory, excludedDllName, onInstalled);
        }

        public static async Task<HashSet<string>> GetPendingInstallFilesAsync(
            Models.Game game,
            NewAxis.Models.ModType modType,
            GameRepositoryClient repoClient)
        {
            var gameInstallPath = game.InstallPath;
            if (string.IsNullOrEmpty(gameInstallPath) || game.Tag is not GameIndexEntry gameEntry)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var executablePath = gameEntry.ExecutablePath ?? "";
            var relativeExecutablePath = gameEntry.RelativeExecutablePath ?? "";
            var targetDirectory = Path.Combine(gameInstallPath, relativeExecutablePath);
            var fullExecutablePath = Path.Combine(gameInstallPath, relativeExecutablePath, Path.GetFileName(executablePath));

            if (!File.Exists(fullExecutablePath) && File.Exists(Path.Combine(gameInstallPath, executablePath)))
            {
                fullExecutablePath = Path.Combine(gameInstallPath, executablePath);
            }
            else if (!File.Exists(fullExecutablePath))
            {
                var executables = Directory.EnumerateFiles(gameInstallPath, "*.exe", SearchOption.AllDirectories)
                    .Where(x => !Path.GetFileName(x).Contains("launch", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("web", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("crash", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("install", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("editor", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("physx", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !Path.GetFileName(x).Contains("ENU", StringComparison.InvariantCultureIgnoreCase))
                    .OrderByDescending(x => new FileInfo(x).Length);

                if (executables.Any())
                {
                    fullExecutablePath = executables.First();
                }
            }

            if (File.Exists(fullExecutablePath))
            {
                targetDirectory = Path.GetDirectoryName(fullExecutablePath) ?? targetDirectory;
            }

            return await Task.Run(async () =>
            {
                var pendingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (modType == ModType.Native && !string.IsNullOrEmpty(gameEntry.CommunityModPath))
                {
                    var communityArchivePath = await DownloadFileAsync(repoClient, gameEntry.CommunityModPath);
                    foreach (var relativePath in EnumerateArchiveRelativePaths(
                        communityArchivePath,
                        entry => !string.Equals(Path.GetFileName(entry.Key ?? string.Empty), gameEntry.CommunityReshadeEntryPoint, StringComparison.OrdinalIgnoreCase),
                        logEntries: true,
                        logContext: "community mod package"))
                    {
                        pendingFiles.Add(Path.GetRelativePath(gameInstallPath, Path.Combine(targetDirectory, relativePath)));
                    }

                    if (!string.IsNullOrWhiteSpace(gameEntry.CommunityReshadeEntryPoint))
                    {
                        var reshadeLocalPath = await DownloadFileAsync(repoClient, "Global/Reshade/reshade.zip");
                        foreach (var relativePath in EnumerateReshadeRuntimeRelativePaths(reshadeLocalPath, fullExecutablePath, gameEntry.CommunityReshadeEntryPoint!))
                        {
                            pendingFiles.Add(Path.GetRelativePath(gameInstallPath, Path.Combine(targetDirectory, relativePath)));
                        }

                        pendingFiles.Add(Path.GetRelativePath(gameInstallPath, Path.Combine(targetDirectory, "3DGameBridge.addon")));
                    }
                }

                return pendingFiles;
            });
        }

        private static async Task<List<string>> ExtractCommunityModWithSharpCompressAsync(string archivePath, string targetDirectory, string? excludedDllName, Action? onInstalled = null)
        {
            return await Task.Run(() =>
            {
                var installedFiles = new List<string>();
                Directory.CreateDirectory(targetDirectory);

                using var archive = SevenZipArchive.Open(archivePath);
                using var reader = archive.ExtractAllEntries();

                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory || string.IsNullOrEmpty(reader.Entry.Key))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(reader.Entry.Key);
                    if (!string.IsNullOrWhiteSpace(excludedDllName) &&
                        string.Equals(fileName, excludedDllName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    reader.WriteEntryToDirectory(targetDirectory, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });

                    string extractPath = Path.Combine(
                        targetDirectory,
                        reader.Entry.Key.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                    installedFiles.Add(extractPath);
                    onInstalled?.Invoke();
                }

                return installedFiles;
            });
        }

        private static bool TryIsArchiveDataError(Exception ex)
        {
            if (ex.Message.Contains("Data Error", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ex.InnerException != null && TryIsArchiveDataError(ex.InnerException);
        }

        /// <summary>
        /// Injects custom configurations for specific games based on Steam AppId and Config Root
        /// </summary>
        public static void InjectCustomConfigs(GameIndexEntry? gameEntry, Root root, List<GameSettingOverride>? overrides)
        {
            if (gameEntry == null || overrides == null) return;


            IEnumerable<Child> UEGameUserSetting = null;

            try
            {
                UEGameUserSetting = root.Children
                                    .SelectMany(x => x.Children.Where(x =>

                                                    ((x.PrecedingElement?.Contains("Script/") ?? false) &&
                                                    !(x.PrecedingElement?.Contains("GameUserSettings") ?? true))
                                                    ||
                                                    (x.Name?.Contains("DLSS") ?? false)
                                    )).ToArray();
            }
            catch { }


            if (gameEntry.SteamAppId == "2138710")//Sifu
            {
                UEGameUserSetting = GetDummyPrecedingElement("[/Script/Sifu.WGGameUserSettings]");
            }

            if (gameEntry.SteamAppId == "1330470")//F.I.S.T Forged In Shadow Torch
            {
                if (root.ConfigFilePaths.First().Path.EndsWith("Engine.ini", true, null))
                {
                    Child newSetup = CreateConfig("UNREAL_DX11", "DefaultGraphicsRHI", "[/Script/WindowsTargetPlatform.WindowsTargetSettings]");
                    root.Children.Add(newSetup);

                    overrides.Add(new GameSettingOverride { GameSettingId = "UNREAL_DX11", Value = "DefaultGraphicsRHI_DX11" });
                }
            }

            if (UEGameUserSetting?.Any() ?? false)
            {
                foreach (var setting in UEGameUserSetting)
                {
                    var preceding = setting.Children?.Select(x => x.PrecedingElement).FirstOrDefault() ?? setting.PrecedingElement ?? null;
                    if (preceding == null)
                        continue;

                    Child newSetup = CreateConfig("UNREAL_DLSS_DISABLER", "DLSSQuality", preceding);
                    root.Children.Add(newSetup);

                    newSetup = CreateConfig("UNREAL_DLSS_DISABLER_2", "DLSSMode", preceding);
                    root.Children.Add(newSetup);

                    newSetup = CreateConfig("UNREAL_DLSS_DISABLER_3", "DLSSEnabled", preceding);
                    root.Children.Add(newSetup);
                }

                overrides.Add(new GameSettingOverride { GameSettingId = "UNREAL_DLSS_DISABLER", Value = "Off" });
                overrides.Add(new GameSettingOverride { GameSettingId = "UNREAL_DLSS_DISABLER_2", Value = "Off" });
                overrides.Add(new GameSettingOverride { GameSettingId = "UNREAL_DLSS_DISABLER_3", Value = "False" });
            }
        }

        private static Child CreateConfig(string? ID, string? Name, string? preceding)
        {
            var newSetup = new Child();
            newSetup.ID = ID;
            newSetup.ValueRangeType = 2;
            newSetup.Name = "Custom Setting " + Name;
            newSetup.Children =
            [
                new Child
                        {
                            Name = Name,
                            KeyOrSearchPattern = Name,
                            PrecedingElement = preceding,
                            OverrideValue = "%InputValue%"
                        },
                    ];
            return newSetup;
        }

        private static IEnumerable<Child> GetDummyPrecedingElement(string PrecedingElement)
        {
            return new List<Child>() {
                    new Child() {
                        ID = "Dummy",
                        Children = new List<Child>() {
                            new Child() {
                                Name = "Dummy",
                                KeyOrSearchPattern = "Dummy",
                                PrecedingElement = PrecedingElement,
                                OverrideValue = "%InputValue%"
                            }
                        }
                    }
                }.AsEnumerable();
        }

        private static bool HasUnicodeChars(string path) => path.Any(c => c > 0x7F);
        private static void ProcessBlacklist(List<string> installedFiles, string gameInstallPath, string targetDirectory, bool enabled)
        {
            if (!enabled) return;

            Trace.WriteLine($"[ModInstaller] Processing blacklist...");

            var dllList = Directory.GetFiles(gameInstallPath, "*.dll", SearchOption.AllDirectories).ToList();

            foreach (var blacklistFile in _blacklistedFiles)
            {
                //Find for the blacklist on exe directory
                var fullPath = Path.Combine(targetDirectory, blacklistFile);
                if (File.Exists(fullPath))
                {
                    DisableFile(fullPath);
                    installedFiles.Add(fullPath);
                }

                //Find for the blacklist on entire game directory
                var entries = dllList.Where(x => Path.GetFileName(x).Equals(blacklistFile, StringComparison.InvariantCultureIgnoreCase));
                if (entries.Any())
                {
                    foreach (var targetFile in entries)
                    {
                        DisableFile(targetFile);
                        installedFiles.Add(targetFile);
                    }
                }
            }
        }

        private static void DisableFile(string fileToDisable)
        {
            var fn = Path.GetFileName(fileToDisable);
            var froot = Path.GetDirectoryName(fileToDisable);
            var backupPath = Path.Combine(froot, fn + ".disabled");
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            File.Move(fileToDisable, backupPath);
            Trace.WriteLine($"[ModInstaller] Disabled blacklisted file: {fn}");
        }

        public static async Task UninstallModAsync(string gameInstallPath, bool deleteBackups = false, IProgress<ModProgressInfo>? progress = null, IReadOnlySet<string>? skipDeleteRelativePaths = null)
        {
            var filesListPath = Path.Combine(gameInstallPath, MOD_FILES_LIST);
            if (!File.Exists(filesListPath))
            {
                Trace.WriteLine("[ModInstaller] No 3dfiles.txt found, nothing to uninstall");
                return;
            }

            Trace.WriteLine("[ModInstaller] Restoring original files...");
            var installedFiles = await File.ReadAllLinesAsync(filesListPath);
            int totalFiles = installedFiles.Count(x => !string.IsNullOrWhiteSpace(x));
            int processedFiles = 0;

            progress?.Report(new ModProgressInfo
            {
                Status = "Preparing",
                Processed = 0,
                Total = totalFiles
            });

            await Task.Run(() =>
            {
                foreach (var relativePath in installedFiles)
                {
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;

                    var fullPath = Path.Combine(gameInstallPath, relativePath);
                    bool shouldSkipDelete = skipDeleteRelativePaths?.Contains(relativePath) == true;
                    if (File.Exists(fullPath))
                    {
                        var backupPath = fullPath + ".disabled";
                        if (File.Exists(backupPath))
                        {
                            File.Copy(backupPath, fullPath, overwrite: true);
                            Trace.WriteLine($"[ModInstaller] Restored: {relativePath}");

                            if (deleteBackups)
                            {
                                File.Delete(backupPath);
                            }
                        }
                        else if (!shouldSkipDelete)
                        {
                            File.Delete(fullPath);
                            Trace.WriteLine($"[ModInstaller] Deleted: {relativePath}");
                        }
                        else
                        {
                            Trace.WriteLine($"[ModInstaller] Skipped delete (will be overwritten): {relativePath}");
                        }
                    }

                    processedFiles++;
                    progress?.Report(new ModProgressInfo
                    {
                        Status = "Preparing",
                        Processed = processedFiles,
                        Total = totalFiles
                    });
                }
            });

            File.Delete(filesListPath);
            Trace.WriteLine("[ModInstaller] Mod uninstalled successfully");
            progress?.Report(new ModProgressInfo
            {
                Status = "Preparing",
                Processed = totalFiles,
                Total = totalFiles
            });
        }

        public static int GetTrackedModFileCount(string gameInstallPath)
        {
            var filesListPath = Path.Combine(gameInstallPath, MOD_FILES_LIST);
            if (!File.Exists(filesListPath))
            {
                return 0;
            }

            return File.ReadLines(filesListPath).Count(x => !string.IsNullOrWhiteSpace(x));
        }

        private static int CountArchiveFiles(string archivePath, Func<IArchiveEntry, bool>? predicate = null, bool logEntries = false, string? logContext = null)
        {
            return EnumerateArchiveRelativePaths(archivePath, predicate, logEntries, logContext).Count;
        }

        private static List<string> EnumerateArchiveRelativePaths(string archivePath, Func<IArchiveEntry, bool>? predicate = null, bool logEntries = false, string? logContext = null)
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var results = archive.Entries
                .Where(entry => !entry.IsDirectory && !string.IsNullOrEmpty(entry.Key) && (predicate == null || predicate(entry)))
                .Select(entry => entry.Key!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
                .ToList();

            if (logEntries && results.Count > 0)
            {
                Trace.WriteLine($"[ModInstaller] Counted {results.Count} files from {Path.GetFileName(archivePath)}{(string.IsNullOrWhiteSpace(logContext) ? string.Empty : $" ({logContext})")}:");
            }
            else if (logEntries)
            {
                Trace.WriteLine($"[ModInstaller] No files found in {Path.GetFileName(archivePath)}{(string.IsNullOrWhiteSpace(logContext) ? string.Empty : $" ({logContext})")}");
            }

            return results;
        }

        private static IEnumerable<string> EnumerateReshadeRuntimeRelativePaths(string archivePath, string executablePath, string targetDllName)
        {
            string tempExtractDir = Path.Combine(Path.GetTempPath(), $"ReshadePreview_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempExtractDir);

            try
            {
                ReshadeExtractor.ExtractArchiveByArchitecturePreview(archivePath, executablePath, tempExtractDir);
                return EnumerateInstalledReshadeRelativePaths(tempExtractDir, targetDllName, true).ToArray();
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempExtractDir))
                    {
                        Directory.Delete(tempExtractDir, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<string> EnumerateInstalledReshadeRelativePaths(string sourceDir, string targetDllName, bool excludeSpatialLabs)
        {
            var allFiles = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);
            var dllFiles = allFiles.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(f => !excludeSpatialLabs || !ReshadeExtractor.IsObsoleteReshadeFile(Path.GetFileName(f)))
                .ToArray();

            var otherFiles = allFiles.Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(f => !excludeSpatialLabs || !ReshadeExtractor.IsObsoleteReshadeFile(Path.GetFileName(f)))
                .ToArray();

            foreach (var srcDll in dllFiles)
            {
                var fileName = Path.GetFileName(srcDll);
                if (fileName.Contains("reshade", StringComparison.OrdinalIgnoreCase) || dllFiles.Length == 1)
                {
                    yield return targetDllName;
                }
                else
                {
                    yield return fileName;
                }
            }

            foreach (var srcFile in otherFiles)
            {
                yield return Path.GetFileName(srcFile);
            }
        }

        private sealed class ModInstallProgressTracker
        {
            private readonly string _status;
            private readonly IProgress<ModProgressInfo>? _progress;
            private readonly object _sync = new();
            private int _processed;
            private int? _total;
            private int _lastReportedProcessed = -1;
            private long _lastReportTick;

            public ModInstallProgressTracker(string status, IProgress<ModProgressInfo>? progress)
            {
                _status = status;
                _progress = progress;
            }

            public void AddToTotal(int amount)
            {
                if (amount <= 0)
                {
                    return;
                }

                lock (_sync)
                {
                    _total = (_total ?? 0) + amount;
                    ReportLocked();
                }
            }

            public void Advance(int amount = 1)
            {
                if (amount <= 0)
                {
                    return;
                }

                lock (_sync)
                {
                    _processed += amount;
                    if (_total.HasValue && _processed > _total.Value)
                    {
                        _total = _processed;
                    }

                    ReportLocked();
                }
            }

            public void Complete()
            {
                lock (_sync)
                {
                    _total = Math.Max(_processed, _total ?? 0);
                    _processed = _total.Value;
                    ReportLocked();
                }
            }

            private void ReportLocked()
            {
                if (_progress == null)
                {
                    return;
                }

                bool isComplete = _total.HasValue && _processed >= _total.Value;
                long now = Environment.TickCount64;
                bool shouldReport = _lastReportedProcessed < 0
                    || isComplete
                    || (_processed - _lastReportedProcessed) >= 32
                    || (now - _lastReportTick) >= 100;

                if (!shouldReport)
                {
                    return;
                }

                _lastReportedProcessed = _processed;
                _lastReportTick = now;

                _progress?.Report(new ModProgressInfo
                {
                    Status = _status,
                    Processed = _processed,
                    Total = _total
                });
            }
        }

        private static async Task<string> DownloadFileAsync(GameRepositoryClient repoClient, string urlOrPath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "NewAxisMods");
            Directory.CreateDirectory(tempDir);

            var match = System.Text.RegularExpressions.Regex.Match(urlOrPath, @"^(.*)\.(\d{3})-(\d{3})$");

            if (match.Success)
            {
                var basePath = match.Groups[1].Value;
                var startPart = int.Parse(match.Groups[2].Value);
                var totalParts = int.Parse(match.Groups[3].Value);

                var finalFileName = Path.GetFileName(basePath);
                var mergePath = Path.Combine(tempDir, finalFileName);

                bool allPartsCached = true;
                for (int i = 1; i <= totalParts; i++)
                {
                    var partUrl = $"{basePath}.{i:D3}-{totalParts:D3}";
                    var partCacheName = Path.GetFileName(partUrl);
                    var partCachePath = Path.Combine(tempDir, partCacheName);
                    if (!File.Exists(partCachePath))
                    {
                        allPartsCached = false;
                        break;
                    }
                }

                if (File.Exists(mergePath) && allPartsCached)
                {
                    Trace.WriteLine($"[ModInstaller] Using cached merged file: {finalFileName}");
                    return mergePath;
                }

                Trace.WriteLine($"[ModInstaller] Detected split file ({totalParts} parts). Downloading...");

                for (int i = 1; i <= totalParts; i++)
                {
                    var partUrl = $"{basePath}.{i:D3}-{totalParts:D3}";
                    var partCacheName = Path.GetFileName(partUrl);
                    var partCachePath = Path.Combine(tempDir, partCacheName);

                    if (!File.Exists(partCachePath))
                    {
                        Trace.WriteLine($"  - Downloading part {i}/{totalParts}: {partCacheName}");
                        await repoClient.DownloadFileAsync(partUrl, partCachePath);
                    }
                }


                Trace.WriteLine($"[ModInstaller] Merging {totalParts} parts...");
                if (File.Exists(mergePath)) File.Delete(mergePath);

                using (var destStream = new FileStream(mergePath, FileMode.Create, FileAccess.Write))
                {
                    for (int i = 1; i <= totalParts; i++)
                    {
                        var partUrl = $"{basePath}.{i:D3}-{totalParts:D3}";
                        var partCacheName = Path.GetFileName(partUrl);
                        var partCachePath = Path.Combine(tempDir, partCacheName);

                        using (var srcStream = new FileStream(partCachePath, FileMode.Open, FileAccess.Read))
                        {
                            await srcStream.CopyToAsync(destStream);
                        }
                    }
                }

                Trace.WriteLine($"[ModInstaller] Merge complete: {mergePath}");
                return mergePath;
            }
            else
            {
                // Use hash of URL to avoid collisions
                var urlHash = ComputeHash(urlOrPath);
                var fileName = Path.GetFileName(urlOrPath);
                var cachePath = Path.Combine(tempDir, $"{urlHash}_{fileName}");


                if (!File.Exists(cachePath))
                {
                    Trace.WriteLine($"[ModInstaller] Downloading: {urlOrPath}");
                    await repoClient.DownloadFileAsync(urlOrPath, cachePath);
                }
                else
                {
                    Trace.WriteLine($"[ModInstaller] Using cached: {Path.GetFileName(urlOrPath)}");
                }

                return cachePath;
            }
        }

        private static string ComputeHash(string input)
        {
            using (var md5 = MD5.Create())
            {
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = md5.ComputeHash(inputBytes);
                return Convert.ToHexString(hashBytes).ToLower();
            }
        }

        /// <summary>
        /// Creates truegame.ini file with default content and updates Depth/Popout values
        /// </summary>
        private static async Task CreateTrueGameIniAsync(string targetDirectory, ModInstallationSettings settings)
        {
            var iniPath = Path.Combine(targetDirectory, "truegame.ini");

            if (File.Exists(iniPath))
            {
                ApplyTrueGameSettings(settings, iniPath);
                return;
            }


            var defaultContent = Encoding.UTF8.GetString(Convert.FromBase64String("W0dFTkVSQUxdDQojIEZvciBmdXR1cmUgdXNlDQoNCltERVBUSF0NCiMgQ29udHJvbHMgdGhlIHN0ZXJlbyBzZXBhcmF0aW9uIHZhbHVlLCB3aXRoIGEgdmFsaWQgcmFuZ2Ugb2YgMCUgLSAxNTAlLCBpbmRpY2F0ZWQgYXMgYW4gaW50DQpEZXB0aCA9IDMwDQoNCiMgQ29udHJvbHMgdGhlIHN0ZXJlbyBjb252ZXJnZW5jZSB2YWx1ZSwgd2l0aCBhIHZhbGlkIHJhbmdlIG9mIDUwJSAtIDE1MCUsIGluZGljYXRlZCBhcyBhbiBpbnQNClBvcG91dCA9IDEwMA0KIA0KIyBOb3RlOiAzRCBPbi9PZmYgc3RhdHVzIHNob3VsZCBvbmx5IGJlIGFjdGl2ZSBmb3IgdGhlIGR1cmF0aW9uIG9mIGEgZ2FtZSBzZXNzaW9uIGFuZCBzaG91bGQgbm90IGJlIHBlcnNpc3RlZCwgDQojIG1lYW5pbmcgdGhhdCAzRCBzaG91bGQgYWx3YXlzIGJlIGVuYWJsZWQgd2hlbiBydW5uaW5nIHRocm91Z2ggVEcNCg0KDQpbSU5QVVRdDQojIEhvdGtleXMgYXJlIHNwZWNpZmllZCBpbiB0aGUgZm9ybWF0IFcuWC5ZLlogd2hlcmU6DQojIC0gVyBpbmRpY2F0ZXMgdGhlIHByaW1hcnkga2V5Y29kZQ0KIyAtIFggaW5kaWNhdGVzIHdoZXRoZXIgQUxUIHNob3VsZCBiZSBwcmVzc2VkIA0KIyAtIFkgaW5kaWNhdGVzIHdoZXRoZXIgQ1RSTCBzaG91bGQgYmUgcHJlc3NlZCANCiMgLSBaIGluZGljYXRlcyB3aGV0aGVyIFNISUZUIHNob3VsZCBiZSBwcmVzc2VkDQojIGUuZy4gQ3RybCtGMTIgPSAxMjMsMCwxLDAgDQojIFNlZSBoZXJlIGZvciBrZXljb2Rlczogc2hvcnR1cmwuYXQvQkdNTzgNCg0KQ3ljbGVQYW5lbERpc3BsYXlNb2RlID0gOTAsMCwxLDANCkluY3JlYXNlRGVwdGggID0gMTE1LDAsMSwwDQpEZWNyZWFzZURlcHRoICA9IDExNCwwLDEsMA0KSW5jcmVhc2VQb3BvdXQgPSAxMTcsMCwxLDANCkRlY3JlYXNlUG9wb3V0ID0gMTE2LDAsMSwwDQpDeWNsZVBhbmVsRG9ja1Bvc2l0aW9uID0gMTE4LDAsMSwwDQpJbmNyZWFzZVBhbmVsT3BhY2l0eSA9IDExMywwLDEsMA0KRGVjcmVhc2VQYW5lbE9wYWNpdHkgPSAxMTIsMCwxLDANClRvZ2dsZVN0ZXJlbyA9IDg0LDAsMSwwDQoNCiMgR2VuZXJhbCB0ZXJtaW5vbG9neToNCiMgLSAiT3ZlcmxheSIgcmVmZXJzIHRvIHRoZSBmdWxsIG92ZXJsYXkgc3lzdGVtLCB3aGljaCBjdXJyZW50bHkgaW5jbHVkZXMgYSBzaW5nbGUgcGFuZWwgYnV0IG1heSBpbiB0aGUgZnV0dXJlIGluY2x1ZGUgYWxlcnRzLCBvdGhlciB3aWRnZXRzLi5ldGMNCiMgLSAiUGFuZWwiIHJlZmVycyB0byB0aGUgcHJpbWFyeSB3aWRnZXQgY29udGFpbmluZyB0aGUgYnVsayBvZiB0aGUgb3ZlcmxheSBzeXN0ZW0gVUkgYW5kIGxvZ2ljDQpbVUldDQojIENvbnRyb2xzIHRoZSBvcGFjaXR5IG9mIHRoZSBvdmVybGF5IHBhbmVsLCB3aGVyZSAwIGlzIGZ1bGx5IHRyYW5zcGFyZW50IGFuZCAxIGlzIGZ1bGx5IG9wYXF1ZQ0KIyBCb3RoIFRHIGFuZCBzdGVyZW8gZHJpdmVycyBzaG91bGQgcmVzcGVjdCB0aGUgbWluIGFuZCBtYXggdmFsdWVzLiANClBhbmVsT3BhY2l0eU1pbiA9IDAuMg0KUGFuZWxPcGFjaXR5TWF4ID0gMS4wDQpQYW5lbE9wYWNpdHkgPSAwLjgNCg0KIyBDb250cm9scyB3aGVyZSB0aGUgb3ZlcmxheSBwYW5lbCBpcyBkb2NrZWQgaW4gdGhlIHZpZXdwb3J0DQojIE9uZSBvZjogVG9wTGVmdCwgVG9wUmlnaHQsIEJvdHRvbUxlZnQsIEJvdHRvbVJpZ2h0DQpQYW5lbERvY2tQb3NpdGlvbiA9IFRvcExlZnQNCg0KIyBUaGUgcGFuZWwgY2FuIGJlIGluIG9uZSBvZiB0aHJlZSBtb2RlcywgTWluaW1hbCwgRnVsbCwgYW5kIEhpZGRlbg0KIyBUaGlzIHZhbHVlIHNob3VsZCBiZSBwZXJzaXN0ZWQNClBhbmVsRGlzcGxheU1vZGUgPSBNaW5pbWFsDQoNCltJTUdVSV0NCltXaW5kb3ddW0RlYnVnIyNEZWZhdWx0XQ0KUG9zPTYwLDYwDQpTaXplPTQwMCw0MDANCkNvbGxhcHNlZD0wDQoNCltXaW5kb3ddW0dlbzExXQ0KUG9zPTAsMA0KU2l6ZT0zODQwLDIxNjANCkNvbGxhcHNlZD0wDQoNCg=="));


            await File.WriteAllTextAsync(iniPath, defaultContent);
            Trace.WriteLine($"[ModInstaller] Created truegame.ini");

            ApplyTrueGameSettings(settings, iniPath);

            Trace.WriteLine($"[ModInstaller] Updated truegame.ini: Depth={settings.Depth}, Popout={settings.Popout}");

            return;
        }

        /// <summary>
        /// Determines if an executable is 64-bit by reading its PE header
        /// </summary>
        private static bool IsExecutable64Bit(string exePath)
        {
            using (var stream = File.OpenRead(exePath))
            using (var peReader = new System.Reflection.PortableExecutable.PEReader(stream))
            {
                var headers = peReader.PEHeaders;
                return headers.PEHeader != null && headers.PEHeader.Magic == System.Reflection.PortableExecutable.PEMagic.PE32Plus;
            }
        }

        private static void ApplyTrueGameSettings(ModInstallationSettings settings, string iniPath)
        {

            var iniParser = new IniFileParser();
            iniParser.Load(iniPath);
            iniParser.SetValue("DEPTH", "Depth", ((int)settings.Depth).ToString());
            iniParser.SetValue("DEPTH", "Popout", ((int)settings.Popout).ToString());


            if (settings.DepthInc != null) iniParser.SetValue("INPUT", "IncreaseDepth", GetTrueGameHotkeyString(settings.DepthInc));
            if (settings.DepthDec != null) iniParser.SetValue("INPUT", "DecreaseDepth", GetTrueGameHotkeyString(settings.DepthDec));
            if (settings.PopoutInc != null) iniParser.SetValue("INPUT", "IncreasePopout", GetTrueGameHotkeyString(settings.PopoutInc));
            if (settings.PopoutDec != null) iniParser.SetValue("INPUT", "DecreasePopout", GetTrueGameHotkeyString(settings.PopoutDec));

            iniParser.Save(iniPath);
        }

        private static string GetTrueGameHotkeyString(HotkeyDefinition def)
        {


            int vk = KeyInterop.VirtualKeyFromKey(def.Key);
            int alt = def.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? 1 : 0;
            int ctrl = def.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) ? 1 : 0;
            int shift = def.Modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? 1 : 0;

            return $"{vk},{alt},{ctrl},{shift}";
        }
    }

    public static class KeyInterop
    {
        public static int VirtualKeyFromKey(Avalonia.Input.Key key)
        {
            // This is a simplified mapping. For a full mapping we might need a library or extensive switch.
            // Avalonia Key enum often matches VK codes for A-Z, 0-9, F1-F12 but offset issues exist.
            // Let's try to trust a simple cast for common keys or implement a basic switch for important ones.
            // Actually Avalonia Key does NOT match VK 1:1.

            // Basic Lookup for Function Keys
            if (key >= Avalonia.Input.Key.F1 && key <= Avalonia.Input.Key.F24)
                return 112 + (int)(key - Avalonia.Input.Key.F1);

            // Numbers
            if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9)
                return 48 + (int)(key - Avalonia.Input.Key.D0);

            // Numpad
            if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9)
                return 96 + (int)(key - Avalonia.Input.Key.NumPad0);

            // Letters A-Z
            if (key >= Avalonia.Input.Key.A && key <= Avalonia.Input.Key.Z)
                return 65 + (int)(key - Avalonia.Input.Key.A);

            // Arrows
            if (key == Avalonia.Input.Key.Left) return 37;
            if (key == Avalonia.Input.Key.Up) return 38;
            if (key == Avalonia.Input.Key.Right) return 39;
            if (key == Avalonia.Input.Key.Down) return 40;

            // Modifiers
            if (key == Avalonia.Input.Key.LeftCtrl || key == Avalonia.Input.Key.RightCtrl) return 17;
            if (key == Avalonia.Input.Key.LeftAlt || key == Avalonia.Input.Key.RightAlt) return 18;
            if (key == Avalonia.Input.Key.LeftShift || key == Avalonia.Input.Key.RightShift) return 16;

            // Common
            if (key == Avalonia.Input.Key.Space) return 32;
            if (key == Avalonia.Input.Key.Enter) return 13;
            if (key == Avalonia.Input.Key.Escape) return 27;
            if (key == Avalonia.Input.Key.Back) return 8;
            if (key == Avalonia.Input.Key.Tab) return 9;

            // Fallback: try cast, though unreliable for special keys
            return (int)key;
        }
    }
}
