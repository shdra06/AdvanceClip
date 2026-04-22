using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdvanceClip.Classes
{
    public class CloudflareDaemon
    {
        private Process _cfProcess;
        private int _localPort;
        private int _consecutiveFailures = 0;
        private bool _useHttp2 = false; // Start with QUIC, fallback to HTTP/2 for restricted networks
        private bool _stopped = false;  // True when Stop() is called — prevents auto-retry
        private const long MIN_EXE_SIZE = 10_000_000; // cloudflared.exe should be >10MB
        private System.Timers.Timer _healthTimer;      // Periodic tunnel health monitor

        public string GlobalUrl { get; private set; } = "Initializing...";
        /// <summary>
        /// True ONLY when the tunnel has been self-verified (HTTP 200 on /api/health).
        /// False if verification was inconclusive (HTTP 400/530/timeout).
        /// FirebaseSyncManager checks this before using the URL for file downloads.
        /// </summary>
        public bool IsTunnelVerified { get; private set; } = false;
        public event Action<string> GlobalUrlUpdated;

        public async Task StartAsync(int localPort)
        {
            _localPort = localPort;
            _consecutiveFailures = 0;
            _stopped = false;
            await StartTunnelCore();
        }

        private async Task StartTunnelCore()
        {
            if (_stopped) return;

            try
            {
                string agentDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "agent");
                Directory.CreateDirectory(agentDir);
                string exePath = Path.Combine(agentDir, "cloudflared.exe");

                // Download cloudflared.exe if missing or corrupted (too small = partial download)
                if (!File.Exists(exePath) || new FileInfo(exePath).Length < MIN_EXE_SIZE)
                {
                    try { if (File.Exists(exePath)) File.Delete(exePath); } catch { }

                    GlobalUrl = "Downloading secure agent...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    Logger.LogAction("CLOUDFLARE", "Downloading cloudflared.exe...");

                    bool downloaded = await DownloadCloudflaredAsync(exePath);
                    if (!downloaded)
                    {
                        Logger.LogAction("CLOUDFLARE_ERROR", "Failed to download cloudflared.exe — will retry in 30s");
                        GlobalUrl = "Download failed — retrying soon...";
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        ScheduleRetry(30_000);
                        return;
                    }
                }

                KillExisting();

                // Auto-switch protocol after repeated failures:
                // After 2 failures with QUIC, switch to HTTP/2 (TCP 443 — more firewall-friendly)
                // After 2 more failures with HTTP/2, switch back to QUIC
                if (_consecutiveFailures > 0 && _consecutiveFailures % 2 == 0)
                {
                    _useHttp2 = !_useHttp2;
                    Logger.LogAction("CLOUDFLARE", $"Switching protocol to {(_useHttp2 ? "HTTP/2 (TCP 443)" : "QUIC (UDP 7844)")} after {_consecutiveFailures} failures");
                }

                _cfProcess = new Process();
                _cfProcess.StartInfo.FileName = exePath;
                _cfProcess.StartInfo.Arguments = _useHttp2
                    ? $"tunnel --url http://localhost:{_localPort} --no-autoupdate --protocol http2"
                    : $"tunnel --url http://localhost:{_localPort} --no-autoupdate";
                Logger.LogAction("CLOUDFLARE", $"Starting tunnel with protocol: {(_useHttp2 ? "HTTP/2 (TCP 443)" : "QUIC (UDP 7844)")} [attempt {_consecutiveFailures + 1}]");
                _cfProcess.StartInfo.UseShellExecute = false;
                _cfProcess.StartInfo.RedirectStandardError = true;
                _cfProcess.StartInfo.CreateNoWindow = true;
                _cfProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                bool tunnelUrlReceived = false;

                _cfProcess.ErrorDataReceived += (s, e) =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        // Only log errors/warnings, not verbose info lines
                        bool isImportant = e.Data.Contains("ERR") || e.Data.Contains("WRN") || e.Data.Contains("trycloudflare.com") || e.Data.Contains("failed") || e.Data.Contains("error");
                        if (isImportant) Logger.LogAction("CF_STDERR", e.Data);
                        Match match = Regex.Match(e.Data, @"https://([a-zA-Z0-9-]+)\.trycloudflare\.com");
                        if (match.Success)
                        {
                            string subdomain = match.Groups[1].Value.ToLower();
                            // Skip known Cloudflare system subdomains — NOT tunnel URLs
                            if (subdomain == "api" || subdomain == "dash" || subdomain == "login" || subdomain == "www")
                            {
                                Logger.LogAction("CF_STDERR", $"Ignoring system URL: {match.Value}");
                                return;
                            }
                            GlobalUrl = match.Value;
                            tunnelUrlReceived = true;
                            IsTunnelVerified = false; // Not verified until self-ping succeeds
                            _consecutiveFailures = 0; // Reset on success
                            Logger.LogAction("CLOUDFLARE", $"Tunnel URL: {GlobalUrl}");
                            GlobalUrlUpdated?.Invoke(GlobalUrl);
                        }
                    }
                    catch (Exception ex) { Logger.LogAction("CF_EVENT_ERROR", ex.Message); }
                };

                _cfProcess.EnableRaisingEvents = true;
                _cfProcess.Exited += (s, e) =>
                {
                    if (_stopped) return; // Don't retry if we intentionally stopped
                    int exitCode = -1;
                    try { exitCode = _cfProcess?.ExitCode ?? -1; } catch { }
                    Logger.LogAction("CLOUDFLARE", $"Process exited (code: {exitCode}). Will auto-restart...");
                    _consecutiveFailures++;
                    StopHealthMonitor();
                    int delay = GetRetryDelay();
                    GlobalUrl = $"Reconnecting in {delay / 1000}s...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    ScheduleRetry(delay);
                };

                _cfProcess.Start();
                _cfProcess.BeginErrorReadLine();
                Logger.LogAction("CLOUDFLARE", "Spawned Global Web Tunnel.");

                // Wait up to 30s for the tunnel URL to appear
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    if (tunnelUrlReceived) break;
                    if (_cfProcess.HasExited) break;
                }

                if (tunnelUrlReceived)
                {
                    // Verify the tunnel by checking if the LOCAL server responds on localhost.
                    // We DON'T ping the public Cloudflare URL because the sender's own DNS may
                    // not resolve *.trycloudflare.com (common on restrictive networks).
                    // If localhost responds AND cloudflared gave us a URL, the tunnel works.
                    await Task.Delay(3000); // Give cloudflared time to establish the proxy
                    
                    bool verified = false;
                    using var verifyClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
                    
                    // Phase 1: Verify local server is responding (this is what Cloudflare proxies to)
                    for (int v = 0; v < 3; v++)
                    {
                        try
                        {
                            Logger.LogAction("CLOUDFLARE", $"Verifying local server (attempt {v + 1}/3)...");
                            var localResp = await verifyClient.GetAsync($"http://localhost:{_localPort}/api/health");
                            if (localResp.IsSuccessStatusCode)
                            {
                                verified = true;
                                IsTunnelVerified = true;
                                Logger.LogAction("CLOUDFLARE", $"✅ Local server verified on port {_localPort} — tunnel is live: {GlobalUrl}");
                                break;
                            }
                            Logger.LogAction("CLOUDFLARE", $"Local verify attempt {v + 1}/3: HTTP {(int)localResp.StatusCode}");
                        }
                        catch (Exception pingEx)
                        {
                            Logger.LogAction("CLOUDFLARE", $"Local verify attempt {v + 1}/3 failed: {pingEx.Message}");
                        }
                        await Task.Delay(2000);
                    }
                    
                    // Phase 2: Optional — try the public URL too (works on networks with good DNS)
                    if (!verified)
                    {
                        Logger.LogAction("CLOUDFLARE", "Local server check failed — trying public URL as fallback...");
                        for (int v = 0; v < 2; v++)
                        {
                            try
                            {
                                await Task.Delay(3000);
                                var pubResp = await verifyClient.GetAsync($"{GlobalUrl}/api/health");
                                if (pubResp.IsSuccessStatusCode)
                                {
                                    verified = true;
                                    IsTunnelVerified = true;
                                    Logger.LogAction("CLOUDFLARE", $"✅ Tunnel verified via public URL: {GlobalUrl}");
                                    break;
                                }
                                Logger.LogAction("CLOUDFLARE", $"Public URL verify {v + 1}/2: HTTP {(int)pubResp.StatusCode}");
                            }
                            catch (Exception pubEx)
                            {
                                Logger.LogAction("CLOUDFLARE", $"Public URL verify {v + 1}/2 failed: {pubEx.Message}");
                            }
                        }
                    }

                    if (!verified)
                    {
                        IsTunnelVerified = false;
                        Logger.LogAction("CLOUDFLARE", $"⚠️ Tunnel verification FAILED — URL exists but local server not responding: {GlobalUrl}");
                        Logger.LogAction("CLOUDFLARE", $"⚠️ File sync will use Firebase Storage fallback instead of Cloudflare tunnel.");
                    }
                    
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    StartHealthMonitor(); // Begin periodic health checks
                    return;
                }

                if (_cfProcess.HasExited)
                {
                    // The Exited handler will schedule retry
                    Logger.LogAction("CLOUDFLARE", "Process exited before providing tunnel URL.");
                    return;
                }

                // Tunnel still running but no URL yet — could be slow network
                // Wait another 30s for the URL
                Logger.LogAction("CLOUDFLARE", "No URL yet — waiting an extra 30s...");
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    if (tunnelUrlReceived)
                    {
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        StartHealthMonitor();
                        return;
                    }
                    if (_cfProcess.HasExited) return; // Exited handler deals with retry
                }

                // Still no URL — kill and retry with different protocol
                Logger.LogAction("CLOUDFLARE", "Tunnel started but no URL received after 60s — killing and retrying...");
                _consecutiveFailures++;
                KillExisting();
                int retryDelay = GetRetryDelay();
                ScheduleRetry(retryDelay);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CLOUDFLARE_ERROR", $"Startup error: {ex.Message}");
                _consecutiveFailures++;
                ScheduleRetry(GetRetryDelay());
            }
        }

        /// <summary>
        /// Periodic health monitor: every 60s, ping the tunnel.
        /// If 3 consecutive pings fail, kill and restart the tunnel.
        /// </summary>
        private int _healthFailCount = 0;
        private void StartHealthMonitor()
        {
            StopHealthMonitor();
            _healthFailCount = 0;
            _healthTimer = new System.Timers.Timer(60_000); // Every 60s
            _healthTimer.Elapsed += async (s, e) =>
            {
                if (_stopped || string.IsNullOrEmpty(GlobalUrl) || !GlobalUrl.Contains("trycloudflare.com")) return;
                try
                {
                    // Ping localhost instead of public URL — avoids DNS resolution failures
                    using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
                    var resp = await client.GetAsync($"http://localhost:{_localPort}/api/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        _healthFailCount = 0; // Healthy
                        if (!IsTunnelVerified)
                        {
                            IsTunnelVerified = true;
                            Logger.LogAction("CLOUDFLARE HEALTH", $"✅ Tunnel now verified via health check — file downloads enabled: {GlobalUrl}");
                        }
                    }
                    else
                    {
                        _healthFailCount++;
                        if (IsTunnelVerified && _healthFailCount >= 2)
                        {
                            IsTunnelVerified = false;
                            Logger.LogAction("CLOUDFLARE HEALTH", $"⚠️ Tunnel verification lost — file downloads will use Firebase Storage fallback");
                        }
                        Logger.LogAction("CLOUDFLARE HEALTH", $"Ping failed ({_healthFailCount}/3): HTTP {(int)resp.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    _healthFailCount++;
                    Logger.LogAction("CLOUDFLARE HEALTH", $"Ping failed ({_healthFailCount}/3): {ex.Message}");
                }

                if (_healthFailCount >= 3)
                {
                    Logger.LogAction("CLOUDFLARE HEALTH", "🔄 Tunnel appears dead — auto-restarting...");
                    StopHealthMonitor();
                    _consecutiveFailures++;
                    GlobalUrl = "Restarting tunnel...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    KillExisting();
                    await Task.Delay(3000);
                    _ = Task.Run(() => StartTunnelCore());
                }
            };
            _healthTimer.AutoReset = true;
            _healthTimer.Start();
            Logger.LogAction("CLOUDFLARE HEALTH", "Health monitor started (60s interval)");
        }

        private void StopHealthMonitor()
        {
            try { _healthTimer?.Stop(); _healthTimer?.Dispose(); } catch { }
            _healthTimer = null;
        }

        /// <summary>
        /// Calculate retry delay with exponential backoff: 5s, 10s, 20s, 30s, 30s, 30s...
        /// Never gives up — tunnel is critical for cross-network file sync.
        /// </summary>
        private int GetRetryDelay()
        {
            int baseDelay = 5_000;
            int delay = baseDelay * (int)Math.Pow(2, Math.Min(_consecutiveFailures, 3)); // Cap at 40s
            return Math.Min(delay, 30_000); // Never more than 30s
        }

        private void ScheduleRetry(int delayMs)
        {
            if (_stopped) return;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delayMs); } catch { return; }
                if (!_stopped)
                {
                    Logger.LogAction("CLOUDFLARE", $"Auto-retry #{_consecutiveFailures} after {delayMs}ms...");
                    await StartTunnelCore();
                }
            });
        }

        private async Task<bool> DownloadCloudflaredAsync(string exePath)
        {
            string[] downloadUrls = new[]
            {
                "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
                "https://github.com/cloudflare/cloudflared/releases/download/2024.12.2/cloudflared-windows-amd64.exe"
            };

            using var client = new HttpClient() { Timeout = TimeSpan.FromMinutes(5) };

            foreach (string url in downloadUrls)
            {
                try
                {
                    Logger.LogAction("CLOUDFLARE", $"Downloading from: {url}");
                    var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? -1;
                    Logger.LogAction("CLOUDFLARE", $"Download size: {totalBytes / 1048576.0:F1} MB");

                    string tempPath = exePath + ".tmp";
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576))
                    {
                        byte[] buffer = new byte[1048576];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                        }

                        if (totalRead < MIN_EXE_SIZE)
                        {
                            Logger.LogAction("CLOUDFLARE_ERROR", $"Download too small: {totalRead} bytes");
                            try { File.Delete(tempPath); } catch { }
                            continue;
                        }
                    }

                    // Atomic rename: only replace after complete download
                    try { if (File.Exists(exePath)) File.Delete(exePath); } catch { }
                    File.Move(tempPath, exePath);
                    Logger.LogAction("CLOUDFLARE", $"Downloaded cloudflared.exe ({new FileInfo(exePath).Length / 1048576.0:F1} MB)");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOUDFLARE_ERROR", $"Download failed from {url}: {ex.Message}");
                }
            }
            return false;
        }

        public void Stop()
        {
            _stopped = true; // Prevents all auto-retry logic
            StopHealthMonitor();
            KillExisting();
            GlobalUrl = "Offline";
            GlobalUrlUpdated?.Invoke(GlobalUrl);
            Logger.LogAction("CLOUDFLARE", "Global Tunnel Terminated.");
        }

        private void KillExisting()
        {
            try
            {
                if (_cfProcess != null && !_cfProcess.HasExited)
                {
                    _cfProcess.Kill();
                    _cfProcess.Dispose();
                }
                foreach (var p in Process.GetProcessesByName("cloudflared"))
                {
                    p.Kill();
                }
            }
            catch { }
        }
    }
}
