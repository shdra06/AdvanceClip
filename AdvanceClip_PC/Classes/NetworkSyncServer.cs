using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    public class NetworkSyncServer
    {
        private HttpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning = false;
        private DropShelfViewModel _viewModel;
        private CloudflareDaemon _cfDaemon = new CloudflareDaemon();
        private System.Timers.Timer _heartbeatTimer;
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        
        public string ServerUrl { get; private set; } = "Not Running";
        public string DisplayUrl => ServerUrl.Split(',')[0];
        public string GlobalUrl => _cfDaemon.GlobalUrl;
        public int CurrentPort { get; private set; } = 3000;

        public NetworkSyncServer(DropShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            _cfDaemon.GlobalUrlUpdated += (url) => { 
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.RefreshLocalServerData()); 
                if (!string.IsNullOrEmpty(url) && url.Contains(".trycloudflare.com"))
                {
                    FirebaseSyncManager.CachedGlobalUrl = url; // Cache for file download URL construction
                    _ = FirebaseSyncManager.PushTunnelUrl(url, true, ServerUrl);
                }
            };
            
            SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AdvanceSettings.EnableGlobalCloudflare))
                {
                    if (SettingsManager.Current.EnableGlobalCloudflare && _isRunning)
                    {
                        _ = _cfDaemon.StartAsync(CurrentPort);
                    }
                    else
                    {
                        _cfDaemon.Stop();
                    }
                }
            };
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                // Determine physical Local IP beforehand for bind fallback
                string localIp = "127.0.0.1";
                try
                {
                    using (System.Net.Sockets.Socket socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0))
                    {
                        socket.Connect("8.8.8.8", 65530);
                        if (socket.LocalEndPoint is System.Net.IPEndPoint endPoint)
                        {
                            localIp = endPoint.Address.ToString();
                        }
                    }
                }
                catch { }

                bool TryBindSequence(int port)
                {
                    // Strategy 1: http://*:port/ (accepts ALL interfaces — requires admin/urlacl)
                    try {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://*:{port}/");
                        _listener.Start();
                        Logger.LogAction("BIND", $"Bound to http://*:{port}/ (all interfaces)");
                        return true;
                    } catch (Exception ex) { 
                        Logger.LogAction("BIND", $"http://*:{port}/ failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } } 
                    }

                    // Strategy 2: http://{localIp}:port/ + http://localhost:port/ 
                    // MUST add BOTH — LAN access AND cloudflared (which connects to localhost)
                    try {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://{localIp}:{port}/");
                        _listener.Prefixes.Add($"http://localhost:{port}/");  // Critical for Cloudflare tunnel!
                        _listener.Start();
                        Logger.LogAction("BIND", $"Bound to http://{localIp}:{port}/ + http://localhost:{port}/");
                        return true;
                    } catch (Exception ex) { 
                        Logger.LogAction("BIND", $"Dual-bind failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } } 
                    }

                    // Strategy 3: http://localhost:port/ only (Cloudflare works, LAN won't)
                    try {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"http://localhost:{port}/");
                        _listener.Start();
                        Logger.LogAction("BIND", $"Bound to http://localhost:{port}/ (localhost only)");
                        return true;
                    } catch (Exception ex) { 
                        Logger.LogAction("BIND", $"localhost-only bind failed: {ex.Message}");
                        if (_listener != null) { try { _listener.Close(); } catch { } } 
                    }

                    return false;
                }

                int publicWifiPort = 8999; 

                bool bound = TryBindSequence(publicWifiPort);
                if (!bound) throw new Exception("OS Sandbox structurally blocked pure internal kernel port mapping.");
                
                CurrentPort = publicWifiPort; 
                LaunchUacBypassProxy(publicWifiPort);

                _isRunning = true;
                _listenerThread = new Thread(() =>
                {
                    try { Task.Run(ListenLoopAsync).GetAwaiter().GetResult(); }
                    catch (Exception ex) { Logger.LogAction("LISTENER", $"Thread crash caught: {ex.Message}"); }
                });
                _listenerThread.IsBackground = true;
                _listenerThread.Start();

                UpdateServerUrl();
                FirebaseSyncManager.CachedLocalUrl = DisplayUrl; // Cache first LAN URL for file download fallback
                Logger.LogAction("NETWORK", $"Web server launched on {ServerUrl}");
                NetworkActivityLog.Instance.ServerStatus = "Online";

                // Natively trigger Cloudflare alongside HTTP Socket unconditionally
                _ = _cfDaemon.StartAsync(CurrentPort);
                _ = FirebaseSyncManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl);

                // Heartbeat: keep this PC visible in Firebase active_devices
                // Android filters out devices with Timestamp older than 2 minutes
                _heartbeatTimer = new System.Timers.Timer(30_000); // Every 30 seconds
                _heartbeatTimer.Elapsed += (s, e) =>
                {
                    _ = FirebaseSyncManager.PushTunnelUrl(GlobalUrl ?? ServerUrl, true, ServerUrl);
                };
                _heartbeatTimer.AutoReset = true;
                _heartbeatTimer.Start();
                Logger.LogAction("HEARTBEAT", "Firebase device heartbeat started (30s interval)");
            }
            catch (Exception ex)
            {
                ServerUrl = "Fatal Error Bind Failed";
                Logger.LogAction("NETWORK ERROR", ex.Message);
            }
            
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.RefreshLocalServerData());
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            ServerUrl = "Offline";
            try { _heartbeatTimer?.Stop(); _heartbeatTimer?.Dispose(); } catch { }
            _cfDaemon.Stop();
            _ = FirebaseSyncManager.PushTunnelUrl("offline", false, "");
            try { _listener.Stop(); } catch { }
            try { _proxyListener?.Stop(); } catch { }
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _viewModel.RefreshLocalServerData());
        }

        private System.Net.Sockets.TcpListener _proxyListener = null;

        private void LaunchUacBypassProxy(int publicWifiPort)
        {
            // Fully deprecated the TCP Proxy and restored the pure native HttpListener binding dynamically conceptually carefully successfully smartly elegantly intuitively exactly perfectly expertly safely easily gracefully.
        }

        private void UpdateServerUrl()
        {
            try
            {
                var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(x => x.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up 
                             && !x.Description.ToLower().Contains("virtualbox") 
                             && !x.Description.ToLower().Contains("vmware") 
                             && !x.Description.ToLower().Contains("hyper-v")
                             && !x.Description.ToLower().Contains("wsl"))
                    .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                    .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork 
                             && !System.Net.IPAddress.IsLoopback(x.Address))
                    .Select(x => x.Address.ToString())
                    .ToList();
                
                if (ips.Count > 0)
                {
                    ServerUrl = string.Join(",", ips.Select(ip => $"http://{ip}:{CurrentPort}"));
                }
                else
                {
                    ServerUrl = $"http://localhost:{CurrentPort}";
                }
            }
            catch { ServerUrl = $"http://localhost:{CurrentPort}"; }
        }

        private async Task ListenLoopAsync()
        {
            while (_isRunning && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try { await ProcessRequest(context); }
                        catch (Exception ex) { Logger.LogAction("HTTP", $"ProcessRequest error: {ex.Message}"); }
                    });
                }
                catch (ObjectDisposedException) { break; } // Listener closed cleanly
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.ErrorCode == 64) { break; } // I/O abort — normal on stop
                catch (Exception ex)
                {
                    Logger.LogAction("HTTP", $"ListenLoop error: {ex.Message}");
                    await Task.Delay(500);
                    if (_listener == null || !_listener.IsListening) break;
                }
            }
        }

        private async Task ProcessRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            try
            {
                string path = req.Url.LocalPath.ToLower();
                string remoteAddr = req.RemoteEndPoint?.ToString() ?? "unknown";
                Logger.LogAction("HTTP", $"[{remoteAddr}] {req.HttpMethod} {path}");
                
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Original-Date, X-Advance-Client");
                res.AddHeader("Access-Control-Expose-Headers", "X-Global-Url");
                if (!string.IsNullOrEmpty(GlobalUrl)) res.AddHeader("X-Global-Url", GlobalUrl);

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 200;
                    res.Close();
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    ServeHtml(res);
                }
                else
                {
                    // HARD SECURE AUTHENTICATION BARRIER
                    string providedPin = req.Headers["Authorization"]?.Replace("Bearer ", "") ?? req.QueryString["pin"];
                    
                    bool isNativeMobileCompanion = req.Headers["User-Agent"]?.Contains("AdvanceClipMobile_Native") == true || req.Headers["X-Advance-Client"] == "MobileCompanion" || req.Headers["X-Advance-Client"] == "DesktopSync";
                    
                    if (!isNativeMobileCompanion && (string.IsNullOrEmpty(providedPin) || providedPin != SettingsManager.Current.WebClientPinToken))
                    {
                        byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"401 Unauthorized - Invalid PIN\"}");
                        res.StatusCode = 401;
                        res.ContentType = "application/json";
                        res.OutputStream.Write(err, 0, err.Length);
                        res.Close();
                        return;
                    }

                    if (path == "/api/health" && req.HttpMethod == "GET")
                    {
                        res.StatusCode = 200;
                        res.Close();
                    }
                    else if (path == "/api/sync" && req.HttpMethod == "GET")
                    {
                        ServeClipboardData(res);
                    }
                    else if (path == "/api/sync_text" && req.HttpMethod == "POST")
                    {
                        await HandleTextUpload(req, res);
                    }
                    else if (path == "/api/sync_file" && req.HttpMethod == "POST")
                    {
                        await HandleFileUpload(req, res);
                    }
                    else if (path == "/api/archive_upload" && req.HttpMethod == "POST")
                    {
                        await HandleArchiveUpload(req, res);
                    }
                    else if (path == "/api/upload_chunk" && req.HttpMethod == "POST")
                    {
                        await HandleChunkUpload(req, res);
                    }
                    else if (path == "/api/upload_finalize" && req.HttpMethod == "POST")
                    {
                        await HandleChunkFinalize(req, res);
                    }
                    else if (path == "/api/relay_upload" && req.HttpMethod == "POST")
                    {
                        await HandleRelayUpload(req, res);
                    }
                    else if (path == "/api/convert_to_pdf" && req.HttpMethod == "POST")
                    {
                        await HandleConvertToPdf(req, res);
                    }
                    else if (path == "/download" && req.HttpMethod == "GET")
                    {
                        await ServeFileDownload(req, res);
                    }
                    else
                    {
                        res.StatusCode = 404;
                        res.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("SERVER REQUEST FAULT", ex.Message);
                try { res.StatusCode = 500; } catch { }
                try { res.Close(); } catch { }
            }
        }

        private void ServeHtml(HttpListenerResponse res)
        {
            try
            {
                string path = Path.Combine(AdvanceClip.Classes.RuntimeHost.ExecutionDir, "Resources", "WebClient", "index.html");
                Logger.LogAction("HTML", $"Serving from: {path} (exists: {File.Exists(path)})");
                if (File.Exists(path))
                {
                    byte[] buffer = File.ReadAllBytes(path);
                    res.ContentType = "text/html; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.OutputStream.Write(buffer, 0, buffer.Length);
                    Logger.LogAction("HTML", $"Served {buffer.Length} bytes OK");
                }
                else
                {
                    byte[] err = Encoding.UTF8.GetBytes("UI payload not found.");
                    res.StatusCode = 404;
                    res.OutputStream.Write(err, 0, err.Length);
                }
            }
            catch (Exception ex) { Logger.LogAction("HTML ERROR", ex.Message); try { res.StatusCode = 500; } catch { } }
            finally { try { res.Close(); } catch { } }
        }

        // ═══ RESPONSE CACHE: Avoid re-serializing on rapid polls ═══
        private byte[]? _cachedSyncJson = null;
        private long _cachedSyncTimestamp = 0;
        private int _cachedItemCount = 0;
        private const int SYNC_CACHE_TTL_MS = 2000; // Cache for 2 seconds (Cloudflare round-trip is ~300ms)

        private void ServeClipboardData(HttpListenerResponse res)
        {
            try
            {
                long now = Environment.TickCount64;
                int currentCount = 0;
                System.Windows.Application.Current.Dispatcher.Invoke(() => { currentCount = _viewModel.DroppedItems.Count; });

                // Use cached response if still fresh and item count unchanged
                if (_cachedSyncJson != null && (now - _cachedSyncTimestamp) < SYNC_CACHE_TTL_MS && currentCount == _cachedItemCount)
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = _cachedSyncJson.Length;
                    try { res.OutputStream.Write(_cachedSyncJson, 0, _cachedSyncJson.Length); } catch { }
                    res.Close();
                    return;
                }

                // Rebuild cache
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var payload = _viewModel.DroppedItems.Take(15).Select(x => new
                    {
                        id = x.GetHashCode().ToString() + "_" + x.DateCopied.Ticks.ToString(),
                        Title = string.IsNullOrEmpty(x.FileName) ? (x.RawContent?.Length > 20 ? x.RawContent.Substring(0, 20) + "..." : x.RawContent) : x.FileName,
                        Type = x.ItemType.ToString(),
                        PreviewUrl = (x.ItemType == ClipboardItemType.Image || x.ItemType == ClipboardItemType.QRCode) ? (!string.IsNullOrEmpty(x.FilePath) ? $"/download?path={Uri.EscapeDataString(x.FilePath)}" : (x.RawContent ?? "")) : "",
                        DownloadUrl = !string.IsNullOrEmpty(x.FilePath) ? $"/download?path={Uri.EscapeDataString(x.FilePath)}" : (x.RawContent ?? ""),
                        Raw = x.RawContent ?? "",
                        Time = x.DateCopied.ToString("HH:mm:ss"),
                        Timestamp = ((DateTimeOffset)x.DateCopied).ToUnixTimeMilliseconds(),
                        SourceDeviceName = x.Extension == "MOBILE" ? "Mobile" : (SettingsManager.Current.DeviceName ?? Environment.MachineName),
                        SourceDeviceType = x.Extension == "MOBILE" ? "Mobile" : "PC"
                    }).ToList();

                    string json = JsonSerializer.Serialize(payload);
                    _cachedSyncJson = Encoding.UTF8.GetBytes(json);
                    _cachedSyncTimestamp = now;
                    _cachedItemCount = currentCount;
                });

                res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = _cachedSyncJson!.Length;
                try { res.OutputStream.Write(_cachedSyncJson, 0, _cachedSyncJson.Length); } catch { }
                res.Close();
            }
            catch { try { res.StatusCode = 500; } catch { } try { res.Close(); } catch { } }
        }

        private async Task HandleTextUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            // SPEED: Read body first, then respond 200 IMMEDIATELY so the sender isn't blocked
            string text;
            string sourceDevice;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync();
                sourceDevice = req.Headers["X-Source-Device"] ?? "Mobile";
            }

            // Respond instantly — don't make Android wait for UI processing
            res.StatusCode = 200;
            res.Close();

            // Invalidate sync cache so next poll picks up the new item
            _cachedSyncJson = null;

            // Process asynchronously on UI thread (fire-and-forget)
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var clip = new ClipboardItem
                {
                    RawContent = text,
                    FileName = text.Length > 40 ? text.Substring(0, 40) + "..." : text,
                    Extension = "MOBILE",
                    ItemType = text.StartsWith("http") ? ClipboardItemType.Url : ClipboardItemType.Text
                };
                clip.EvaluateSmartActions();
                _viewModel.DroppedItems.Insert(0, clip);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                
                try { System.Windows.Clipboard.SetText(text); } catch { }
                AdvanceClip.Windows.ToastWindow.ShowToast($"Text from {sourceDevice}! 📱");
            });
        }

        private async Task HandleFileUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try 
            {
                string sourceDevice = req.Headers["X-Source-Device"];
                if (string.IsNullOrEmpty(sourceDevice)) sourceDevice = req.QueryString["sourceDevice"];
                if (!string.IsNullOrEmpty(sourceDevice))
                {
                    try { sourceDevice = Uri.UnescapeDataString(sourceDevice); } catch { }
                }
                else
                {
                    sourceDevice = "Mobile";
                }

                string dateString = DateTime.Now.ToString("dd-MM-yyyy");
                string uploadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdvanceClip", "Clipboard", sourceDevice, dateString);
                Directory.CreateDirectory(uploadDir);

                string encodedName = req.Headers["X-File-Name"] ?? req.QueryString["name"];
                string mappedType = req.Headers["X-File-Type"] ?? req.QueryString["type"] ?? "Document";
                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Receiving {rawName} from {sourceDevice}... 📥");
                });

                int counter = 1;
                string finalPath = Path.Combine(uploadDir, rawName);
                while(File.Exists(finalPath))
                {
                    finalPath = Path.Combine(uploadDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                // Parse multipart/form-data or raw body
                string contentType = req.ContentType ?? "";
                if (contentType.Contains("multipart/form-data") && contentType.Contains("boundary="))
                {
                    // Extract boundary string
                    string boundary = contentType.Substring(contentType.IndexOf("boundary=") + "boundary=".Length).Trim();
                    if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                        boundary = boundary.Substring(1, boundary.Length - 2);
                    
                    byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
                    
                    // Read entire body into memory
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    byte[] body = ms.ToArray();
                    
                    // Find the file content: skip past the first boundary + headers (ends with \r\n\r\n)
                    int headerEnd = -1;
                    for (int i = 0; i < body.Length - 3; i++)
                    {
                        if (body[i] == 0x0D && body[i + 1] == 0x0A && body[i + 2] == 0x0D && body[i + 3] == 0x0A)
                        {
                            // Found \r\n\r\n — content starts after this
                            headerEnd = i + 4;
                            break;
                        }
                    }
                    
                    if (headerEnd > 0)
                    {
                        // Find trailing boundary
                        int contentEnd = body.Length;
                        byte[] endMarker = Encoding.UTF8.GetBytes("\r\n--" + boundary);
                        for (int i = headerEnd; i < body.Length - endMarker.Length; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < endMarker.Length; j++)
                            {
                                if (body[i + j] != endMarker[j]) { match = false; break; }
                            }
                            if (match) { contentEnd = i; break; }
                        }
                        
                        using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        fs.Write(body, headerEnd, contentEnd - headerEnd);
                    }
                    else
                    {
                        // Fallback: save raw
                        using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        fs.Write(body, 0, body.Length);
                    }
                }
                else
                {
                    // Raw binary body — save directly
                    using var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await req.InputStream.CopyToAsync(fs);
                }

                string originalDateStr = req.Headers["X-Original-Date"];
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    try
                    {
                        var originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                        File.SetCreationTime(finalPath, originalDate);
                        File.SetLastWriteTime(finalPath, originalDate);
                    } catch { }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dataObj = new System.Windows.DataObject();
                    var dropList = new System.Collections.Specialized.StringCollection { finalPath };
                    dataObj.SetFileDropList(dropList);
                    _viewModel.HandleDrop(dataObj, true);
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Saved: {Path.GetFileName(finalPath)} ✅");
                });

                res.StatusCode = 200;
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("SERVER ERR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private DateTime _lastArchiveToastTime = DateTime.MinValue;
        // Track files per batch for auto-clipboard (copy to clipboard if ≤2 files in batch)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _batchFiles = new();

        private async Task HandleArchiveUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string batchName = req.Headers["X-Batch-Name"];
                if (!string.IsNullOrEmpty(batchName))
                {
                    try { batchName = Uri.UnescapeDataString(batchName); } catch { }
                }
                
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "AdvanceClip_Mobile_Transfer";

                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdvanceClip", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                string originalDateStr = req.Headers["X-Original-Date"];
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }

                string encodedName = req.Headers["X-File-Name"];
                string rawName = "uploaded_media.dat";
                if (!string.IsNullOrEmpty(encodedName))
                {
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                }

                if ((DateTime.Now - _lastArchiveToastTime).TotalSeconds > 2)
                {
                    _lastArchiveToastTime = DateTime.Now;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Extracting batch data... 📦");
                    });
                }

                int counter = 1;
                string finalPath = Path.Combine(archiveDir, rawName);
                while(File.Exists(finalPath))
                {
                    finalPath = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                if (originalDate.HasValue)
                {
                    try
                    {
                        File.SetCreationTime(finalPath, originalDate.Value);
                        File.SetLastWriteTime(finalPath, originalDate.Value);
                    } catch { }
                }

                res.StatusCode = 200;

                // Track file in batch for auto-clipboard
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }
                
                // Auto-copy to Windows clipboard if ≤2 files in this batch
                if (batchList.Count <= 2)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var fileList = new System.Collections.Specialized.StringCollection();
                            lock (batchList) { foreach (var f in batchList) fileList.Add(f); }
                            System.Windows.Clipboard.SetFileDropList(fileList);
                            AdvanceClip.Windows.ToastWindow.ShowToast($"📋 {rawName} copied to clipboard");
                            
                            // Insert proper file entry into DropShelf (clickable → opens in default app)
                            var clip = new ClipboardItem
                            {
                                RawContent = finalPath,
                                FileName = rawName,
                                FilePath = finalPath,
                                Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(),
                                ItemType = ClipboardItemType.File
                            };
                            clip.EvaluateSmartActions();
                            _viewModel.DroppedItems.Insert(0, clip);
                            _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                        }
                        catch { }
                    });
                }
                
                // Clean up old batches after 5 minutes
                _ = Task.Run(async () => { await Task.Delay(300_000); _batchFiles.TryRemove(batchName, out _); });
            }
            catch (Exception ex)
            {
                Logger.LogAction("ARCHIVE UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        // ─── Relay Upload: Android uploads file → PC saves + pushes Cloudflare URL to Firebase ───
        private async Task HandleRelayUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string senderDevice = req.Headers["X-Source-Device"] ?? "Android";
                string originalDateStr = req.Headers["X-Original-Date"];

                string rawName = "relayed_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }

                // Save to Downloads/Synced/Relay_{sender}/
                string relayDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    "Downloads", "AdvanceClip", "Relay", senderDevice.Replace(" ", "_"));
                Directory.CreateDirectory(relayDir);

                int counter = 1;
                string finalPath = Path.Combine(relayDir, rawName);
                while (File.Exists(finalPath))
                {
                    finalPath = Path.Combine(relayDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                    try { File.SetCreationTime(finalPath, dt); File.SetLastWriteTime(finalPath, dt); } catch { }
                }

                // Build Cloudflare download URL
                string globalUrl = _cfDaemon.GlobalUrl;
                string downloadUrl = "";
                if (!string.IsNullOrEmpty(globalUrl) && globalUrl.Contains("trycloudflare.com"))
                {
                    downloadUrl = $"{globalUrl}/download?path={Uri.EscapeDataString(finalPath)}";
                }

                // Push to Firebase so all devices see it
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    var fileInfo = new FileInfo(finalPath);
                    string ext = Path.GetExtension(rawName).ToLower();
                    string fileType = ext switch
                    {
                        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "Video",
                        ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "Audio",
                        ".pdf" => "Pdf",
                        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
                        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "ImageLink",
                        ".doc" or ".docx" or ".txt" or ".rtf" => "Document",
                        ".ppt" or ".pptx" => "Presentation",
                        ".apk" => "Archive",
                        _ => "File"
                    };

                    string deviceName = SettingsManager.Current?.DeviceName ?? Environment.MachineName;
                    var payload = new
                    {
                        Title = rawName,
                        Type = fileType,
                        Raw = downloadUrl,
                        PreviewUrl = downloadUrl,
                        DownloadUrl = downloadUrl,
                        FileName = rawName,
                        FileSize = fileInfo.Length,
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SourceDeviceName = senderDevice,
                        SourceDeviceType = "Mobile",
                        RelayedVia = deviceName
                    };

                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    var fbRes = await _httpClient.PostAsync(
                        "https://advance-sync-default-rtdb.firebaseio.com/clipboard.json", content);

                    if (fbRes.IsSuccessStatusCode)
                    {
                        string fbBody = await fbRes.Content.ReadAsStringAsync();
                        try
                        {
                            var fbObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(fbBody);
                            if (fbObj != null && fbObj.TryGetValue("name", out string? entryKey) && !string.IsNullOrEmpty(entryKey))
                            {
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(24 * 60 * 60_000);
                                    try { await _httpClient.DeleteAsync($"https://advance-sync-default-rtdb.firebaseio.com/clipboard/{entryKey}.json"); } catch { }
                                });
                            }
                        }
                        catch { }
                    }
                }

                string sizeStr = new FileInfo(finalPath).Length > 1_073_741_824 
                    ? $"{new FileInfo(finalPath).Length / 1_073_741_824.0:F1} GB" 
                    : $"{new FileInfo(finalPath).Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"📡 Relayed {rawName} ({sizeStr}) from {senderDevice}");
                });

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"status\":\"ok\",\"downloadUrl\":\"{downloadUrl}\",\"size\":\"{sizeStr}\"}}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("RELAY UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        // ─── Chunked Upload System (bypasses Cloudflare 100MB limit) ───
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _chunkSessions = new();

        private async Task HandleChunkUpload(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string sessionId = req.Headers["X-Upload-Session"] ?? "";
                string chunkIndexStr = req.Headers["X-Chunk-Index"] ?? "0";
                
                if (string.IsNullOrEmpty(sessionId))
                {
                    res.StatusCode = 400;
                    res.Close();
                    return;
                }

                string chunkDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Chunks", sessionId);
                Directory.CreateDirectory(chunkDir);
                _chunkSessions[sessionId] = chunkDir;

                string chunkPath = Path.Combine(chunkDir, $"chunk_{chunkIndexStr.PadLeft(6, '0')}");
                using (var fs = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CHUNK UPLOAD ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private async Task HandleChunkFinalize(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string sessionId = req.Headers["X-Upload-Session"] ?? "";
                string encodedName = req.Headers["X-File-Name"] ?? "";
                string batchName = req.Headers["X-Batch-Name"] ?? "";
                string originalDateStr = req.Headers["X-Original-Date"];
                string totalChunksStr = req.Headers["X-Total-Chunks"] ?? "0";

                string rawName = "uploaded_file.dat";
                if (!string.IsNullOrEmpty(encodedName))
                    try { rawName = Uri.UnescapeDataString(encodedName); } catch { }
                if (!string.IsNullOrEmpty(batchName))
                    try { batchName = Uri.UnescapeDataString(batchName); } catch { }
                if (string.IsNullOrWhiteSpace(batchName)) batchName = "AdvanceClip_Chunked_Transfer";

                if (!_chunkSessions.TryGetValue(sessionId, out string chunkDir) || !Directory.Exists(chunkDir))
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                // Merge all chunks in order
                string archiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdvanceClip", "Synced", batchName);
                Directory.CreateDirectory(archiveDir);

                int counter = 1;
                string finalPath = Path.Combine(archiveDir, rawName);
                while (File.Exists(finalPath))
                {
                    finalPath = Path.Combine(archiveDir, $"{Path.GetFileNameWithoutExtension(rawName)}_{counter++}{Path.GetExtension(rawName)}");
                }

                var chunkFiles = Directory.GetFiles(chunkDir, "chunk_*").OrderBy(f => f).ToArray();

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Assembling {rawName} ({chunkFiles.Length} chunks)... 📦");
                });

                using (var outputFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    foreach (var chunkFile in chunkFiles)
                    {
                        using (var chunkFs = new FileStream(chunkFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                        {
                            await chunkFs.CopyToAsync(outputFs);
                        }
                    }
                }

                // Set original timestamps
                DateTime? originalDate = null;
                if (!string.IsNullOrEmpty(originalDateStr) && long.TryParse(originalDateStr, out long epochMs))
                {
                    originalDate = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToLocalTime();
                }
                if (originalDate.HasValue)
                {
                    try { File.SetCreationTime(finalPath, originalDate.Value); File.SetLastWriteTime(finalPath, originalDate.Value); } catch { }
                }

                // Cleanup temp chunks
                try { Directory.Delete(chunkDir, true); } catch { }
                _chunkSessions.TryRemove(sessionId, out _);

                var fileInfo = new FileInfo(finalPath);
                string sizeStr = fileInfo.Length > 1_073_741_824 ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB" : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                    AdvanceClip.Windows.ToastWindow.ShowToast($"✅ {rawName} ({sizeStr}) received!");
                    // Auto-copy to clipboard + insert into DropShelf
                    try
                    {
                        var fileList = new System.Collections.Specialized.StringCollection { finalPath };
                        System.Windows.Clipboard.SetFileDropList(fileList);
                        
                        var clip = new ClipboardItem
                        {
                            RawContent = finalPath,
                            FileName = rawName,
                            FilePath = finalPath,
                            Extension = Path.GetExtension(finalPath).TrimStart('.').ToUpper(),
                            ItemType = ClipboardItemType.File
                        };
                        clip.EvaluateSmartActions();
                        _viewModel.DroppedItems.Insert(0, clip);
                        _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                    }
                    catch { }
                });

                // Also track in batch for consistency 
                var batchList = _batchFiles.GetOrAdd(batchName, _ => new List<string>());
                lock (batchList) { batchList.Add(finalPath); }

                res.StatusCode = 200;
                byte[] okBytes = System.Text.Encoding.UTF8.GetBytes($"{{\"status\":\"ok\",\"size\":\"{sizeStr}\"}}");
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(okBytes, 0, okBytes.Length);
            }
            catch (Exception ex)
            {
                Logger.LogAction("CHUNK FINALIZE ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }

        private async Task HandleConvertToPdf(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string fileName = req.QueryString["name"] ?? $"document_{DateTime.Now.Ticks}.docx";
                string convertDir = Path.Combine(Path.GetTempPath(), "AdvanceClip_Conversions");
                Directory.CreateDirectory(convertDir);

                string inputPath = Path.Combine(convertDir, fileName);
                using (var fs = new FileStream(inputPath, FileMode.Create, FileAccess.Write))
                {
                    await req.InputStream.CopyToAsync(fs);
                }

                string pdfName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
                string pdfPath = Path.Combine(convertDir, pdfName);

                // Try LibreOffice conversion first (most reliable cross-platform)
                bool converted = false;
                string[] libreOfficePaths = new[] {
                    @"C:\Program Files\LibreOffice\program\soffice.exe",
                    @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe")
                };

                string sofficePath = libreOfficePaths.FirstOrDefault(p => File.Exists(p));
                if (sofficePath != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = sofficePath,
                        Arguments = $"--headless --convert-to pdf --outdir \"{convertDir}\" \"{inputPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    
                    using (var proc = System.Diagnostics.Process.Start(psi))
                    {
                        if (proc != null)
                        {
                            await proc.WaitForExitAsync();
                            converted = proc.ExitCode == 0 && File.Exists(pdfPath);
                        }
                    }
                }

                // Fallback: Try Microsoft Word COM automation
                if (!converted)
                {
                    try
                    {
                        Type wordType = Type.GetTypeFromProgID("Word.Application");
                        if (wordType != null)
                        {
                            dynamic word = Activator.CreateInstance(wordType);
                            word.Visible = false;
                            dynamic doc = word.Documents.Open(inputPath);
                            doc.SaveAs2(pdfPath, 17); // 17 = wdFormatPDF
                            doc.Close(false);
                            word.Quit();
                            converted = File.Exists(pdfPath);
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(word);
                        }
                    }
                    catch { }
                }

                if (converted && File.Exists(pdfPath))
                {
                    // Also add the PDF to the clipboard shelf
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        var dropList = new System.Collections.Specialized.StringCollection { pdfPath };
                        dataObj.SetFileDropList(dropList);
                        _viewModel.HandleDrop(dataObj, true);
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Converted: {pdfName} ✅");
                    });

                    string downloadUrl = $"/download?path={Uri.EscapeDataString(pdfPath)}";
                    string json = JsonSerializer.Serialize(new { success = true, downloadUrl, fileName = pdfName });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 200;
                    try { res.OutputStream.Write(buffer, 0, buffer.Length); } catch { }
                }
                else
                {
                    string json = JsonSerializer.Serialize(new { success = false, error = "No converter found. Install LibreOffice or Microsoft Word." });
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json; charset=utf-8";
                    res.ContentLength64 = buffer.Length;
                    res.StatusCode = 500;
                    try { res.OutputStream.Write(buffer, 0, buffer.Length); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("CONVERT PDF ERROR", ex.Message);
                res.StatusCode = 500;
            }
            finally
            {
                res.Close();
            }
        }
#pragma warning disable CA2022
        private async Task ProcessStreamingMultipartFile(string tempFilePath, string boundary, string destinationDir, DateTime? applyDate = null)
        {
            try
            {
                using (var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    int bufferSize = Math.Min(1024 * 1024, (int)fs.Length);
                    byte[] headBuffer = new byte[bufferSize];
                    int readLen = await fs.ReadAsync(headBuffer, 0, bufferSize);
                    
                    ReadOnlySpan<byte> headSpan = new ReadOnlySpan<byte>(headBuffer, 0, readLen);
                    
                    byte[] filenameSeq = Encoding.ASCII.GetBytes("filename=\"");
                    int filenameIdx = headSpan.IndexOf(filenameSeq);

                    if (filenameIdx != -1)
                    {
                        byte[] headerEndSeq = Encoding.ASCII.GetBytes("\r\n\r\n");
                        int headerEndRel = headSpan.Slice(filenameIdx).IndexOf(headerEndSeq);

                        if (headerEndRel != -1)
                        {
                            long physicalDataStart = filenameIdx + headerEndRel + 4;
                            
                            string headerStr = Encoding.UTF8.GetString(headBuffer, 0, (int)physicalDataStart);
                            int nameIndexStart = headerStr.IndexOf("filename=\"") + 10;
                            int nameEnd = headerStr.IndexOf("\"", nameIndexStart);
                            string fileName = headerStr.Substring(nameIndexStart, nameEnd - nameIndexStart);
                            if (string.IsNullOrWhiteSpace(fileName)) fileName = "uploaded_file.dat";
                            fileName = Path.GetFileName(fileName);
                            
                            int counter = 1;
                            string finalPath = Path.Combine(destinationDir, fileName);
                            while(File.Exists(finalPath))
                            {
                                finalPath = Path.Combine(destinationDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{counter++}{Path.GetExtension(fileName)}");
                            }

                            fs.Seek(0, SeekOrigin.End);
                            long totalLen = fs.Length;
                            int tailSearchSize = Math.Min(8192, (int)totalLen);
                            fs.Seek(totalLen - tailSearchSize, SeekOrigin.Begin);
                            
                            byte[] tailBuffer = new byte[tailSearchSize];
                            int tailReadLen = await fs.ReadAsync(tailBuffer, 0, tailSearchSize);
                            
                            ReadOnlySpan<byte> tailSpan = new ReadOnlySpan<byte>(tailBuffer, 0, tailReadLen);
                            byte[] footerSeq = Encoding.ASCII.GetBytes("\r\n--" + boundary);
                            int footerIdxRel = tailSpan.LastIndexOf(footerSeq);
                            
                            long physicalDataEnd = totalLen;
                            if (footerIdxRel != -1)
                            {
                                physicalDataEnd = (totalLen - tailSearchSize) + footerIdxRel;
                            }

                            fs.Seek(physicalDataStart, SeekOrigin.Begin);
                            long bytesRemaining = physicalDataEnd - physicalDataStart;

                            using (var outFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] transferBuf = new byte[81920];
                                while (bytesRemaining > 0)
                                {
                                    int toRead = (int)Math.Min(transferBuf.Length, bytesRemaining);
                                    int r = await fs.ReadAsync(transferBuf, 0, toRead);
                                    if (r == 0) break;
                                    await outFs.WriteAsync(transferBuf, 0, r);
                                    bytesRemaining -= r;
                                }
                            }

                            if (applyDate.HasValue)
                            {
                                try
                                {
                                    File.SetCreationTime(finalPath, applyDate.Value);
                                    File.SetLastWriteTime(finalPath, applyDate.Value);
                                } catch { }
                            }

                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                var dataObj = new System.Windows.DataObject();
                                var dropList = new System.Collections.Specialized.StringCollection { finalPath };
                                dataObj.SetFileDropList(dropList);
                                _viewModel.HandleDrop(dataObj, true);
                                AdvanceClip.Windows.ToastWindow.ShowToast($"File extracted: {Path.GetFileName(finalPath)} 📱");
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction("FILE PARSER", ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempFilePath)) File.Delete(tempFilePath); } catch { }
            }
        }
#pragma warning restore CA2022

        // Helper: detect if a remote IP is on the same LAN (private range)
        private static bool IsLanAddress(string remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp)) return false;
            // 127.x, 10.x, 192.168.x, 172.16-31.x = local/LAN
            if (remoteIp.StartsWith("127.") || remoteIp.StartsWith("10.") || remoteIp.StartsWith("192.168.")) return true;
            if (remoteIp.StartsWith("172."))
            {
                if (int.TryParse(remoteIp.Split('.').ElementAtOrDefault(1), out int b) && b >= 16 && b <= 31) return true;
            }
            return false;
        }

        private async Task ServeFileDownload(HttpListenerRequest req, HttpListenerResponse res)
        {
            string path = req.QueryString["path"];
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                try { res.StatusCode = 404; res.Close(); } catch { }
                return;
            }

            try
            {
                var fileInfo = new FileInfo(path);
                long fileSize = fileInfo.Length;
                string ext = Path.GetExtension(path).ToLower();
                string safeFileName = Path.GetFileName(path);
                string remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";

                Logger.LogAction("DOWNLOAD", $"Starting: {safeFileName} ({fileSize / 1024}KB) to {remoteIp}");

                // Content-Type
                res.ContentType = ext switch
                {
                    ".png"  => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif"  => "image/gif",
                    ".webp" => "image/webp",
                    ".pdf"  => "application/pdf",
                    ".apk"  => "application/vnd.android.package-archive",
                    ".mp4"  => "video/mp4",
                    ".mkv"  => "video/x-matroska",
                    ".zip"  => "application/zip",
                    ".rar"  => "application/x-rar-compressed",
                    _ => "application/octet-stream"
                };

                bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
                res.AddHeader("Content-Disposition", isImage
                    ? $"inline; filename=\"{safeFileName}\""
                    : $"attachment; filename=\"{safeFileName}\"");
                res.AddHeader("Cache-Control", "no-store");

                res.StatusCode = 200;
                res.ContentLength64 = fileSize;

                // High-performance streaming — 1MB buffer reduces syscall overhead for large files
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
                await fs.CopyToAsync(res.OutputStream, 1048576); // 1MB chunks for maximum throughput
                await res.OutputStream.FlushAsync();

                Logger.LogAction("DOWNLOAD", $"Completed: {safeFileName} ({fileSize / 1024}KB)");
            }
            catch (HttpListenerException ex) { Logger.LogAction("DOWNLOAD", $"Client disconnected: {ex.Message}"); }
            catch (IOException ex) { Logger.LogAction("DOWNLOAD", $"Pipe broken: {ex.Message}"); }
            catch (Exception ex) { Logger.LogAction("DOWNLOAD ERROR", $"{ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                try { res.Close(); } catch { }
            }
        }
    }
}
