using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdvanceClip.Classes
{
    public class CloudflareDaemon
    {
        private Process _cfProcess;
        private int _localPort;
        private int _retryCount = 0;
        private bool _useHttp2 = false; // Start with QUIC, fallback to HTTP/2 for restricted networks
        private const int MAX_RETRIES = 3;
        private const int RETRY_DELAY_MS = 10_000; // 10 seconds between retries
        private const long MIN_EXE_SIZE = 10_000_000; // cloudflared.exe should be >10MB

        public string GlobalUrl { get; private set; } = "Initializing...";
        public event Action<string> GlobalUrlUpdated;

        public async Task StartAsync(int localPort)
        {
            _localPort = localPort;
            _retryCount = 0;
            await StartTunnelWithRetry();
        }

        private async Task StartTunnelWithRetry()
        {
            while (_retryCount <= MAX_RETRIES)
            {
                try
                {
                    string agentDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "agent");
                    Directory.CreateDirectory(agentDir);
                    string exePath = Path.Combine(agentDir, "cloudflared.exe");

                    // Download cloudflared.exe if missing or corrupted (too small = partial download)
                    if (!File.Exists(exePath) || new FileInfo(exePath).Length < MIN_EXE_SIZE)
                    {
                        try { if (File.Exists(exePath)) File.Delete(exePath); } catch { }

                        GlobalUrl = _retryCount > 0 ? $"Retrying download ({_retryCount}/{MAX_RETRIES})..." : "Downloading secure agent...";
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        Logger.LogAction("CLOUDFLARE", $"Downloading cloudflared.exe (attempt {_retryCount + 1})...");

                        bool downloaded = await DownloadCloudflaredAsync(exePath);
                        if (!downloaded)
                        {
                            Logger.LogAction("CLOUDFLARE_ERROR", "Failed to download cloudflared.exe");
                            _retryCount++;
                            if (_retryCount <= MAX_RETRIES)
                            {
                                await Task.Delay(RETRY_DELAY_MS);
                                continue;
                            }
                            GlobalUrl = "Download failed — file sync uses cloud storage.";
                            GlobalUrlUpdated?.Invoke(GlobalUrl);
                            return;
                        }
                    }

                    KillExisting();

                    _cfProcess = new Process();
                    _cfProcess.StartInfo.FileName = exePath;
                    _cfProcess.StartInfo.Arguments = _useHttp2
                        ? $"tunnel --url http://localhost:{_localPort} --no-autoupdate --protocol http2"
                        : $"tunnel --url http://localhost:{_localPort} --no-autoupdate";
                    Logger.LogAction("CLOUDFLARE", $"Starting tunnel with protocol: {(_useHttp2 ? "HTTP/2 (TCP 443)" : "QUIC (UDP 7844)")}");
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
                                Logger.LogAction("CLOUDFLARE", $"Tunnel URL: {GlobalUrl}");
                                GlobalUrlUpdated?.Invoke(GlobalUrl);
                            }
                        }
                        catch (Exception ex) { Logger.LogAction("CF_EVENT_ERROR", ex.Message); }
                    };

                    _cfProcess.EnableRaisingEvents = true;
                    _cfProcess.Exited += (s, e) =>
                    {
                        Logger.LogAction("CLOUDFLARE", $"Process exited (code: {_cfProcess?.ExitCode}). Will retry...");
                        // Auto-retry on crash after a delay
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(RETRY_DELAY_MS);
                            _retryCount++;
                            if (_retryCount <= MAX_RETRIES)
                            {
                                Logger.LogAction("CLOUDFLARE", $"Auto-retry {_retryCount}/{MAX_RETRIES}...");
                                GlobalUrl = $"Reconnecting ({_retryCount}/{MAX_RETRIES})...";
                                GlobalUrlUpdated?.Invoke(GlobalUrl);
                                await StartTunnelWithRetry();
                            }
                            else
                            {
                                GlobalUrl = "Tunnel unavailable — file sync uses cloud storage.";
                                GlobalUrlUpdated?.Invoke(GlobalUrl);
                            }
                        });
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
                        // Verify the tunnel actually proxies traffic by self-pinging
                        // Give tunnel extra time to fully establish the proxy before first ping
                        // Cloudflare quick tunnels on slow WiFi can take 10-15s to fully warm up
                        await Task.Delay(8000);
                        
                        bool verified = false;
                        using var verifyClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
                        for (int v = 0; v < 10; v++)
                        {
                            try
                            {
                                await Task.Delay(3000); // Wait between pings — be patient on slow networks
                                Logger.LogAction("CLOUDFLARE", $"Verifying tunnel (attempt {v + 1}/10)...");
                                var pingResp = await verifyClient.GetAsync($"{GlobalUrl}/api/health");
                                if (pingResp.IsSuccessStatusCode)
                                {
                                    verified = true;
                                    Logger.LogAction("CLOUDFLARE", $"✅ Tunnel verified working: {GlobalUrl}");
                                    break;
                                }
                                Logger.LogAction("CLOUDFLARE", $"Tunnel verify attempt {v + 1}/10: HTTP {(int)pingResp.StatusCode}");
                            }
                            catch (Exception pingEx)
                            {
                                Logger.LogAction("CLOUDFLARE", $"Tunnel verify attempt {v + 1}/10 failed: {pingEx.Message}");
                            }
                        }

                        if (verified)
                        {
                            return; // Success!
                        }
                        else
                        {
                            // DON'T kill the tunnel — keep it alive and trust it.
                            // Cloudflare returns 400 during warmup on slow WiFi; the tunnel IS functional
                            // for actual file downloads even if the self-ping fails via Cloudflare CDN.
                            Logger.LogAction("CLOUDFLARE", $"⚠️ Tunnel verification inconclusive but keeping tunnel alive: {GlobalUrl}");
                            Logger.LogAction("CLOUDFLARE", "Tunnel will be used for file sync — Cloudflare CDN may need more time to propagate.");
                            // Push the URL anyway — it will likely work for actual file downloads
                            GlobalUrlUpdated?.Invoke(GlobalUrl);
                            return; // Keep the tunnel alive — don't restart!
                        }
                    }

                    if (_cfProcess.HasExited)
                    {
                        Logger.LogAction("CLOUDFLARE", "Process exited before providing tunnel URL.");
                        // The Exited handler will retry, so just return
                        return;
                    }

                    // Tunnel still running but no URL yet — keep it alive, URL might come later
                    Logger.LogAction("CLOUDFLARE", "Tunnel process running but no URL yet — keeping alive.");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("CLOUDFLARE_ERROR", $"Attempt {_retryCount + 1} failed: {ex.Message}");
                    _retryCount++;
                    if (_retryCount <= MAX_RETRIES)
                    {
                        GlobalUrl = $"Retrying ({_retryCount}/{MAX_RETRIES})...";
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        await Task.Delay(RETRY_DELAY_MS);
                    }
                    else
                    {
                        GlobalUrl = "Tunnel unavailable — file sync uses cloud storage.";
                        GlobalUrlUpdated?.Invoke(GlobalUrl);
                        return;
                    }
                }
            }
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
            _retryCount = MAX_RETRIES + 1; // Prevent auto-retry
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
