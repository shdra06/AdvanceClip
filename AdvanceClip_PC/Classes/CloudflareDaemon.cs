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
        public string GlobalUrl { get; private set; } = "Initializing...";
        public event Action<string> GlobalUrlUpdated;

        public async Task StartAsync(int localPort)
        {
            try
            {
                string agentDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "agent");
                Directory.CreateDirectory(agentDir);
                string exePath = Path.Combine(agentDir, "cloudflared.exe");

                if (!File.Exists(exePath))
                {
                    GlobalUrl = "Downloading secure agent...";
                    GlobalUrlUpdated?.Invoke(GlobalUrl);
                    
                    using (var client = new HttpClient())
                    {
                        var bytes = await client.GetByteArrayAsync("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe");
                        File.WriteAllBytes(exePath, bytes);
                    }
                }

                KillExisting();

                _cfProcess = new Process();
                _cfProcess.StartInfo.FileName = exePath;
                _cfProcess.StartInfo.Arguments = $"tunnel --url http://localhost:{localPort} --no-autoupdate";
                _cfProcess.StartInfo.UseShellExecute = false;
                _cfProcess.StartInfo.RedirectStandardError = true;
                _cfProcess.StartInfo.CreateNoWindow = true;
                _cfProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

                _cfProcess.ErrorDataReceived += (s, e) =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        Logger.LogAction("CF_STDERR", e.Data);
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
                            Logger.LogAction("CLOUDFLARE", $"Tunnel URL: {GlobalUrl}");
                            GlobalUrlUpdated?.Invoke(GlobalUrl);
                        }
                    }
                    catch (Exception ex) { Logger.LogAction("CF_EVENT_ERROR", ex.Message); }
                };

                _cfProcess.Start();
                _cfProcess.BeginErrorReadLine();
                Logger.LogAction("CLOUDFLARE", "Spawned Global Web Tunnel.");
            }
            catch (Exception ex)
            {
                GlobalUrl = "Global link failed.";
                GlobalUrlUpdated?.Invoke(GlobalUrl);
                Logger.LogAction("CLOUDFLARE_ERROR", ex.Message);
            }
        }

        public void Stop()
        {
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
