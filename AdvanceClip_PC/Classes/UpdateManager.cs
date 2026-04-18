using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace AdvanceClip.Classes
{
    public class UpdateManager
    {
        // ═══════════════════════════════════════════════════════════════
        // UPDATE THIS URL when your GitHub repo is created.
        // Format: https://raw.githubusercontent.com/{user}/{repo}/main/version.json
        // ═══════════════════════════════════════════════════════════════
        private const string VERSION_URL = "https://raw.githubusercontent.com/shdra06/AdvanceClip/main/version.json";

        private static readonly HttpClient _client = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };

        public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        public string LatestVersion { get; private set; } = "";
        public string Changelog { get; private set; } = "";
        public string DownloadUrl { get; private set; } = "";
        public bool IsUpdateAvailable { get; private set; }

        // Events for UI binding
        public event Action<int> DownloadProgressChanged; // 0-100
        public event Action<string> StatusChanged;
        public event Action<bool> UpdateCheckCompleted; // true = update available

        /// <summary>
        /// Checks GitHub for a newer version. Returns true if update is available.
        /// </summary>
        public async Task<bool> CheckForUpdateAsync()
        {
            try
            {
                StatusChanged?.Invoke("Checking for updates...");
                Logger.LogAction("UPDATE", $"Checking {VERSION_URL}");

                // Add cache-busting to avoid stale CDN responses
                string url = $"{VERSION_URL}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                string json = await _client.GetStringAsync(url);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                LatestVersion = root.TryGetProperty("pc_version", out var v) ? v.GetString() ?? "" : "";
                Changelog = root.TryGetProperty("changelog", out var c) ? c.GetString() ?? "" : "";
                DownloadUrl = root.TryGetProperty("pc_download", out var d) ? d.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(LatestVersion))
                {
                    StatusChanged?.Invoke("Could not read version info.");
                    UpdateCheckCompleted?.Invoke(false);
                    return false;
                }

                // Compare versions (semver)
                var current = new Version(CurrentVersion);
                var latest = new Version(LatestVersion);

                IsUpdateAvailable = latest > current;

                if (IsUpdateAvailable)
                {
                    StatusChanged?.Invoke($"Update v{LatestVersion} available!");
                    Logger.LogAction("UPDATE", $"New version available: {LatestVersion} (current: {CurrentVersion})");
                }
                else
                {
                    StatusChanged?.Invoke($"You're on the latest version (v{CurrentVersion}).");
                    Logger.LogAction("UPDATE", $"Already up to date: {CurrentVersion}");
                }

                UpdateCheckCompleted?.Invoke(IsUpdateAvailable);
                return IsUpdateAvailable;
            }
            catch (Exception ex)
            {
                Logger.LogAction("UPDATE_ERROR", ex.Message);
                StatusChanged?.Invoke("Update check failed — no internet?");
                UpdateCheckCompleted?.Invoke(false);
                return false;
            }
        }

        /// <summary>
        /// Downloads the new EXE and self-replaces via a helper batch script.
        /// </summary>
        public async Task<bool> DownloadAndApplyUpdateAsync()
        {
            if (string.IsNullOrEmpty(DownloadUrl))
            {
                StatusChanged?.Invoke("No download URL available.");
                return false;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Update");
            Directory.CreateDirectory(tempDir);
            string tempExePath = Path.Combine(tempDir, "AdvanceClip_new.exe");

            try
            {
                StatusChanged?.Invoke("Downloading update...");
                Logger.LogAction("UPDATE", $"Downloading from {DownloadUrl}");

                var response = await _client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576);
                
                byte[] buffer = new byte[1048576]; // 1MB buffer
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        int pct = (int)(totalRead * 100 / totalBytes);
                        DownloadProgressChanged?.Invoke(pct);
                        
                        string sizeMB = $"{totalRead / 1048576.0:F1}/{totalBytes / 1048576.0:F1} MB";
                        StatusChanged?.Invoke($"Downloading... {pct}% ({sizeMB})");
                    }
                }

                DownloadProgressChanged?.Invoke(100);
                StatusChanged?.Invoke("Download complete! Ready to install.");
                Logger.LogAction("UPDATE", $"Downloaded {totalRead / 1048576.0:F1} MB to {tempExePath}");

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogAction("UPDATE_ERROR", $"Download failed: {ex.Message}");
                StatusChanged?.Invoke($"Download failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Replaces the running EXE with the downloaded update and restarts.
        /// Uses a batch script to wait for the current process to exit, then swap.
        /// </summary>
        public void ApplyUpdateAndRestart()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Update");
            string tempExePath = Path.Combine(tempDir, "AdvanceClip_new.exe");

            if (!File.Exists(tempExePath))
            {
                StatusChanged?.Invoke("Update file not found. Please re-download.");
                return;
            }

            string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(currentExePath))
            {
                StatusChanged?.Invoke("Cannot determine current EXE path.");
                return;
            }

            // Create a batch script that:
            // 1. Waits for the current process to exit
            // 2. Replaces the EXE
            // 3. Launches the new EXE
            // 4. Cleans up
            string batchPath = Path.Combine(tempDir, "update.bat");
            string batchContent = $@"
@echo off
echo Applying AdvanceClip update...
timeout /t 2 /nobreak > nul
copy /Y ""{tempExePath}"" ""{currentExePath}""
if errorlevel 1 (
    echo Update failed! Could not replace EXE.
    pause
    exit /b 1
)
start """" ""{currentExePath}""
del ""{tempExePath}"" 2>nul
del ""%~f0"" 2>nul
";

            File.WriteAllText(batchPath, batchContent);

            Logger.LogAction("UPDATE", $"Launching updater: {batchPath}");
            StatusChanged?.Invoke("Restarting with update...");

            // Launch the batch script hidden
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false
            };
            Process.Start(psi);

            // Exit current app
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
        }
    }
}
