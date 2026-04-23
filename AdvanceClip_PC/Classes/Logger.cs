using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace AdvanceClip.Classes
{
    public static class Logger
    {
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "Logs");
        private static readonly string LogFile = Path.Combine(LogDirectory, "activity_log.txt");
        private static readonly string NetLogFile = Path.Combine(LogDirectory, "network_diagnostics.txt");
        
        // Async buffered logging — never blocks the UI thread
        private static readonly ConcurrentQueue<string> _buffer = new();
        private static readonly ConcurrentQueue<string> _netBuffer = new();
        private static Timer _flushTimer;
        private static Timer _cleanupTimer;
        private static readonly object _flushLock = new();
        private const int MAX_LOG_LINES = 500; // Keep last 500 lines per file
        private const int CLEANUP_INTERVAL_MS = 5 * 60_000; // 5 minutes

        // Network log categories — any LogAction with these prefixes goes to network_diagnostics.txt
        private static readonly string[] NET_CATEGORIES = {
            "CLOUDFLARE", "CF_", "FIREBASE", "FORCED SYNC", "BIND", "NETWORK",
            "HTTP", "HEARTBEAT", "CLOUDFLARE HEALTH", "CLOUDFLARE_ERROR",
            "DRAG IN", "CLIPBOARD", "FIREBASE SSE", "FIREBASE SYNC",
            "FIREBASE STORAGE", "FIREBASE CLEANUP", "FIREBASE ERROR",
            "LISTENER", "SERVER", "DOWNLOAD"
        };

        static Logger()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
            
            // Flush buffer to disk every 2 seconds on a background thread
            _flushTimer = new Timer(_ => FlushBuffer(), null, 2000, 2000);
            
            // Auto-clean logs every 5 minutes — keep only last 500 lines
            _cleanupTimer = new Timer(_ => TruncateLogs(), null, CLEANUP_INTERVAL_MS, CLEANUP_INTERVAL_MS);
        }

        private static void TruncateLogs()
        {
            TruncateLogFile(LogFile);
            TruncateLogFile(NetLogFile);
        }

        private static void TruncateLogFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                if (lines.Length > MAX_LOG_LINES)
                {
                    File.WriteAllLines(path, lines.Skip(lines.Length - MAX_LOG_LINES));
                }
            }
            catch { }
        }

        public static void LogAction(string actionType, string details)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] [{actionType.ToUpper()}] {details}";
                
                // Enqueue to main log — zero-allocation on the hot path, never blocks
                _buffer.Enqueue(logEntry);

                // Also enqueue to network diagnostics log if it's a network-related category
                string upperAction = actionType.ToUpper();
                foreach (var cat in NET_CATEGORIES)
                {
                    if (upperAction.Contains(cat))
                    {
                        _netBuffer.Enqueue(logEntry);
                        break;
                    }
                }

                // Also push to the in-memory live monitor (lightweight, already on correct thread)
                NetworkActivityLog.Instance.Log(upperAction, details);
            }
            catch 
            {
                // Failsafe so logging doesn't crash the app
            }
        }

        private static void FlushBuffer()
        {
            // Drain main log
            if (!_buffer.IsEmpty)
            {
                lock (_flushLock)
                {
                    try
                    {
                        using var writer = new StreamWriter(LogFile, append: true);
                        while (_buffer.TryDequeue(out string entry))
                        {
                            writer.WriteLine(entry);
                        }
                    }
                    catch { }
                }
            }

            // Drain network diagnostics log
            if (!_netBuffer.IsEmpty)
            {
                try
                {
                    using var writer = new StreamWriter(NetLogFile, append: true);
                    while (_netBuffer.TryDequeue(out string entry))
                    {
                        writer.WriteLine(entry);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Dumps complete network state snapshot to the network diagnostics log.
        /// Call on startup and whenever user wants to diagnose sync issues.
        /// </summary>
        public static void DumpNetworkDiagnostics()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
                sb.AppendLine("║           ADVANCECLIP NETWORK DIAGNOSTICS SNAPSHOT          ║");
                sb.AppendLine($"║  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}                            ║");
                sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
                sb.AppendLine();

                // Device Identity
                sb.AppendLine("── DEVICE IDENTITY ──");
                sb.AppendLine($"  DeviceName:   {SettingsManager.Current.DeviceName ?? "(not set)"}");
                sb.AppendLine($"  DeviceId:     {SettingsManager.Current.DeviceId ?? "(not set)"}");
                sb.AppendLine($"  MachineName:  {Environment.MachineName}");
                sb.AppendLine($"  UserName:     {Environment.UserName}");
                sb.AppendLine($"  OS:           {Environment.OSVersion}");
                sb.AppendLine();

                // Network Interfaces
                sb.AppendLine("── NETWORK INTERFACES ──");
                try
                {
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        if (nic.Description.ToLower().Contains("virtualbox") || nic.Description.ToLower().Contains("vmware") ||
                            nic.Description.ToLower().Contains("hyper-v") || nic.Description.ToLower().Contains("wsl")) continue;
                        
                        var ipProps = nic.GetIPProperties();
                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                sb.AppendLine($"  [{nic.NetworkInterfaceType}] {nic.Name}: {addr.Address} (Mask: {addr.IPv4Mask})");
                            }
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine($"  Error enumerating NICs: {ex.Message}"); }
                sb.AppendLine();

                // Sync Settings
                sb.AppendLine("── SYNC SETTINGS ──");
                sb.AppendLine($"  GlobalFirebaseSync:    {SettingsManager.Current.EnableGlobalFirebaseSync}");
                sb.AppendLine($"  GlobalCloudflare:      {SettingsManager.Current.EnableGlobalCloudflare}");
                sb.AppendLine();

                // Cloudflare State
                sb.AppendLine("── CLOUDFLARE STATE ──");
                sb.AppendLine($"  CachedGlobalUrl:  {FirebaseSyncManager.CachedGlobalUrl ?? "(empty)"}");
                sb.AppendLine($"  CachedLocalUrl:   {FirebaseSyncManager.CachedLocalUrl ?? "(empty)"}");
                sb.AppendLine($"  IsTunnelActive:   {(!string.IsNullOrEmpty(FirebaseSyncManager.CachedGlobalUrl) && FirebaseSyncManager.CachedGlobalUrl.Contains("trycloudflare.com"))}");
                sb.AppendLine();

                // Cloudflared process check
                sb.AppendLine("── CLOUDFLARED PROCESS ──");
                try
                {
                    var cfProcesses = System.Diagnostics.Process.GetProcessesByName("cloudflared");
                    sb.AppendLine($"  Running instances: {cfProcesses.Length}");
                    foreach (var p in cfProcesses)
                    {
                        try { sb.AppendLine($"    PID {p.Id}: {p.ProcessName} (Started: {p.StartTime:HH:mm:ss}, Memory: {p.WorkingSet64 / 1048576.0:F1}MB)"); }
                        catch { sb.AppendLine($"    PID {p.Id}: (access denied for details)"); }
                    }
                }
                catch (Exception ex) { sb.AppendLine($"  Error checking processes: {ex.Message}"); }
                sb.AppendLine();

                // cloudflared.exe binary check
                sb.AppendLine("── CLOUDFLARED BINARY ──");
                string exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "agent", "cloudflared.exe");
                if (File.Exists(exePath))
                {
                    var fi = new FileInfo(exePath);
                    sb.AppendLine($"  Path:     {exePath}");
                    sb.AppendLine($"  Size:     {fi.Length / 1048576.0:F1} MB");
                    sb.AppendLine($"  Modified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"  Valid:    {fi.Length > 10_000_000}");
                }
                else
                {
                    sb.AppendLine($"  NOT FOUND at {exePath}");
                }
                sb.AppendLine();

                // Firewall / Port Check
                sb.AppendLine("── PORT ACCESSIBILITY ──");
                try
                {
                    int port = 8999;
                    bool portListening = false;
                    var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                    foreach (var ep in listeners)
                    {
                        if (ep.Port == port) { portListening = true; break; }
                    }
                    sb.AppendLine($"  Port {port}: {(portListening ? "LISTENING ✓" : "NOT LISTENING ✗")}");
                }
                catch (Exception ex) { sb.AppendLine($"  Port check error: {ex.Message}"); }
                sb.AppendLine();

                // Internet Connectivity
                sb.AppendLine("── INTERNET CONNECTIVITY ──");
                try
                {
                    using var client = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(5) };
                    var t = client.GetAsync("https://advance-sync-default-rtdb.firebaseio.com/.json?shallow=true").Result;
                    sb.AppendLine($"  Firebase RTDB:     HTTP {(int)t.StatusCode} {(t.IsSuccessStatusCode ? "✓" : "✗")}");
                }
                catch (Exception ex) { sb.AppendLine($"  Firebase RTDB:     FAILED — {ex.InnerException?.Message ?? ex.Message}"); }

                // Test Cloudflare tunnel reachability
                if (!string.IsNullOrEmpty(FirebaseSyncManager.CachedGlobalUrl) && FirebaseSyncManager.CachedGlobalUrl.Contains("trycloudflare.com"))
                {
                    try
                    {
                        using var client = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
                        var t = client.GetAsync($"{FirebaseSyncManager.CachedGlobalUrl}/api/health").Result;
                        sb.AppendLine($"  Cloudflare Tunnel: HTTP {(int)t.StatusCode} {(t.IsSuccessStatusCode ? "✓" : "✗")}");
                    }
                    catch (Exception ex) { sb.AppendLine($"  Cloudflare Tunnel: FAILED — {ex.InnerException?.Message ?? ex.Message}"); }
                }
                else
                {
                    sb.AppendLine($"  Cloudflare Tunnel: NOT CONFIGURED");
                }
                sb.AppendLine();

                // DNS Resolution check (common cause of cloudflared failure)
                sb.AppendLine("── DNS RESOLUTION ──");
                try
                {
                    var addrs = System.Net.Dns.GetHostAddresses("region1.v2.argotunnel.com");
                    sb.AppendLine($"  argotunnel.com:    {string.Join(", ", addrs.Select(a => a.ToString()))} ✓");
                }
                catch (Exception ex) { sb.AppendLine($"  argotunnel.com:    FAILED — {ex.Message} (Cloudflare tunnel WILL fail!)"); }
                try
                {
                    var addrs = System.Net.Dns.GetHostAddresses("api.trycloudflare.com");
                    sb.AppendLine($"  trycloudflare.com: {string.Join(", ", addrs.Select(a => a.ToString()))} ✓");
                }
                catch (Exception ex) { sb.AppendLine($"  trycloudflare.com: FAILED — {ex.Message}"); }
                sb.AppendLine();
                
                sb.AppendLine("══════════════════════════════════════════════════════════════");

                // Write to network log file
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                foreach (var line in sb.ToString().Split('\n'))
                {
                    _netBuffer.Enqueue($"[{timestamp}] [DIAGNOSTICS] {line.TrimEnd('\r')}");
                }
                
                // Also log to main activity log
                LogAction("DIAGNOSTICS", "Network diagnostics snapshot captured → " + NetLogFile);
            }
            catch (Exception ex)
            {
                LogAction("DIAGNOSTICS ERROR", $"Failed to capture network diagnostics: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the path to the network diagnostics log file.
        /// </summary>
        public static string GetNetworkLogPath() => NetLogFile;

        /// <summary>
        /// Returns the last N lines of the network diagnostics log as a string (for clipboard copy).
        /// </summary>
        public static string GetRecentNetworkLogs(int lineCount = 200)
        {
            try
            {
                FlushBuffer(); // Ensure pending entries are written first
                if (!File.Exists(NetLogFile)) return "(No network logs found)";
                
                var lines = File.ReadAllLines(NetLogFile);
                int start = Math.Max(0, lines.Length - lineCount);
                return string.Join(Environment.NewLine, lines.Skip(start));
            }
            catch (Exception ex)
            {
                return $"(Error reading network logs: {ex.Message})";
            }
        }

        /// <summary>
        /// Call on app shutdown to ensure all buffered logs are written.
        /// </summary>
        public static void Shutdown()
        {
            _flushTimer?.Dispose();
            FlushBuffer();
        }
    }
}
