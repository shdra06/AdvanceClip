using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    public class FirebaseListener
    {
        // Separate clients: SSE stream needs infinite timeout, forced sync polls use short timeout
        private static readonly HttpClient _streamClient = new HttpClient() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static readonly HttpClient _pollClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
        private const string FIREBASE_BASE = "https://advance-sync-default-rtdb.firebaseio.com";
        private const string CLIPBOARD_URL = FIREBASE_BASE + "/clipboard.json";
        private FlyShelfViewModel _viewModel;
        private long _lastProcessedTimestamp = 0;
        private CancellationTokenSource? _cts = null;
        private HashSet<string> _processedIds = new HashSet<string>();

        public FirebaseListener(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public void StartPolling()
        {
            StopPolling();

            _cts = new CancellationTokenSource();
            // Backlog: process items from the last 5 minutes (catch-up for devices that connect late)
            _lastProcessedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
            _processedIds.Clear();

            Logger.LogAction("FIREBASE LISTENER", "Starting SSE real-time stream + forced sync poller.");

            // 1. Main clipboard feed: SSE streaming (near-instant delivery)
            Task.Run(() => RunSSEStream(_cts.Token));

            // 2. Forced sync: lightweight poll every 5 seconds
            Task.Run(() => RunForcedSyncPoller(_cts.Token));
        }

        public void StopPolling()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                _cts = null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // SSE STREAM: Firebase REST API with Accept: text/event-stream
        // Delivers new clipboard items in ~100-300ms instead of 3s polling
        // ═══════════════════════════════════════════════════════════════════
        private async Task RunSSEStream(CancellationToken ct)
        {
            int reconnectDelay = 1000; // Start with 1s, exponential backoff on failures
            const int MAX_RECONNECT_DELAY = 30_000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string streamUrl = CLIPBOARD_URL;

                    var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                    request.Headers.Add("Accept", "text/event-stream");

                    using var response = await _streamClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.LogAction("FIREBASE SSE", $"Stream HTTP {(int)response.StatusCode} — retrying in {reconnectDelay}ms");
                        await Task.Delay(reconnectDelay, ct);
                        reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
                        continue;
                    }

                    // Connected successfully — reset backoff
                    reconnectDelay = 1000;
                    Logger.LogAction("FIREBASE SSE", "Real-time stream CONNECTED ✓");

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);

                    string currentEvent = "";
                    string currentData = "";

                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break; // Stream ended

                        if (line.StartsWith("event:"))
                        {
                            currentEvent = line.Substring(6).Trim();
                        }
                        else if (line.StartsWith("data:"))
                        {
                            currentData = line.Substring(5).Trim();
                        }
                        else if (string.IsNullOrEmpty(line))
                        {
                            // Empty line = end of SSE message block
                            if (!string.IsNullOrEmpty(currentData) && currentData != "null")
                            {
                                ProcessSSEEvent(currentEvent, currentData);
                            }
                            currentEvent = "";
                            currentData = "";
                        }
                    }

                    // Stream ended (server closed connection) — reconnect
                    Logger.LogAction("FIREBASE SSE", "Stream closed by server — reconnecting...");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("FIREBASE SSE", $"Stream error: {ex.Message} — retrying in {reconnectDelay}ms");
                    try { await Task.Delay(reconnectDelay, ct); } catch { break; }
                    reconnectDelay = Math.Min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
                }
            }

            Logger.LogAction("FIREBASE SSE", "Real-time stream STOPPED.");
        }

        private void ProcessSSEEvent(string eventType, string jsonData)
        {
            // Firebase SSE events:
            // "put"   → { "path": "/key" or "/", "data": { ... } }
            // "patch" → { "path": "/key", "data": { ... } }
            // "keep-alive" → ignore

            if (eventType == "keep-alive") return;
            if (eventType != "put" && eventType != "patch") return;

            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                var root = doc.RootElement;

                string path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/";
                if (!root.TryGetProperty("data", out var data)) return;
                
                // data could be null (deletion event)
                if (data.ValueKind == JsonValueKind.Null) return;

                if (path == "/")
                {
                    // Full payload refresh — process all items
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        ProcessFullPayload(data);
                    }
                }
                else
                {
                    // Single item update — path is "/{key}"
                    string itemKey = path.TrimStart('/');
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        ProcessSingleItem(itemKey, data);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"Parse error: {ex.Message}");
            }
        }

        private void ProcessFullPayload(JsonElement data)
        {
            var sortedItems = new List<CloudItem>();

            foreach (JsonProperty property in data.EnumerateObject())
            {
                var item = TryParseCloudItem(property.Name, property.Value);
                if (item != null) sortedItems.Add(item);
            }

            if (sortedItems.Count == 0) return;

            var newItems = sortedItems.OrderBy(x => x.Timestamp).ToList();
            _lastProcessedTimestamp = newItems.Last().Timestamp;

            foreach (var cloudItem in newItems)
            {
                _processedIds.Add(cloudItem.Id);
                InjectCloudItem(cloudItem);
            }
        }

        private void ProcessSingleItem(string key, JsonElement data)
        {
            var item = TryParseCloudItem(key, data);
            if (item != null)
            {
                _processedIds.Add(item.Id);
                if (item.Timestamp > _lastProcessedTimestamp)
                    _lastProcessedTimestamp = item.Timestamp;
                InjectCloudItem(item);
            }
        }

        private CloudItem? TryParseCloudItem(string key, JsonElement data)
        {
            if (!data.TryGetProperty("Timestamp", out JsonElement tsElement)) return null;

            long timestamp = 0;
            if (tsElement.ValueKind == JsonValueKind.Number)
                timestamp = tsElement.GetInt64();

            // Skip already processed
            if (_processedIds.Contains(key)) return null;

            // Skip items older than session start
            if (timestamp <= _lastProcessedTimestamp) return null;

            // Self-echo prevention
            string sourceDevice = data.TryGetProperty("SourceDeviceName", out var srcName) ? srcName.GetString() ?? "" : "";
            string sourceType = data.TryGetProperty("SourceDeviceType", out var srcType) ? srcType.GetString() ?? "" : "";
            string myDeviceName = SettingsManager.Current.DeviceName ?? "";

            if (!string.IsNullOrEmpty(myDeviceName) &&
                string.Equals(sourceDevice, myDeviceName, StringComparison.OrdinalIgnoreCase) &&
                sourceType == "PC")
            {
                _processedIds.Add(key);
                return null;
            }

            string rawContent = data.TryGetProperty("Raw", out var t3) ? t3.GetString() : "";
            string itemType = data.TryGetProperty("Type", out var t) ? t.GetString() : "Text";

            // Skip empty text items — never allow blank cards
            bool isFileType = itemType == "Image" || itemType == "ImageLink" || itemType == "Pdf" ||
                              itemType == "Archive" || itemType == "Video" || itemType == "Document" ||
                              itemType == "File" || itemType == "Presentation" || itemType == "Audio";
            if (!isFileType && string.IsNullOrWhiteSpace(rawContent)) return null;

            return new CloudItem
            {
                Id = key,
                Timestamp = timestamp,
                Type = itemType,
                Title = data.TryGetProperty("Title", out var t2) ? t2.GetString() : "Cloud Payload",
                Raw = rawContent,
                DownloadUrl = data.TryGetProperty("DownloadUrl", out var t6) ? t6.GetString() : "",
                SenderUrl = data.TryGetProperty("SenderUrl", out var t7) ? t7.GetString() : "",
                SourceDeviceName = sourceDevice
            };
        }

        private void InjectCloudItem(CloudItem cloudItem)
        {
            Logger.LogAction("FIREBASE SSE", $"⚡ INSTANT from '{cloudItem.SourceDeviceName}': {cloudItem.Type}");

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Strict duplicate enforcement — catches P2P + Firebase race condition
                // Check by exact Raw match
                var existsLocally = _viewModel.DroppedItems.Any(i => i.RawContent == cloudItem.Raw && !string.IsNullOrWhiteSpace(cloudItem.Raw));
                
                // Also check by title/filename match for images (P2P may set Raw differently)
                if (cloudItem.Type == "ImageLink" && _viewModel.DroppedItems.Any(i => i.FileName == cloudItem.Title))
                    existsLocally = true;
                
                // P2P text dedup: if text arrived via P2P first, it's already in the shelf
                // Check by content prefix match (first 100 chars) to handle minor formatting differences
                if (!existsLocally && cloudItem.Type == "Text" && !string.IsNullOrWhiteSpace(cloudItem.Raw))
                {
                    string prefix = cloudItem.Raw.Length > 100 ? cloudItem.Raw.Substring(0, 100) : cloudItem.Raw;
                    existsLocally = _viewModel.DroppedItems.Any(i => 
                        i.RawContent != null && i.RawContent.StartsWith(prefix) && i.RawContent.Length == cloudItem.Raw.Length);
                }

                if (existsLocally)
                {
                    Logger.LogAction("FIREBASE SSE", "Skipped duplicate — already exists locally (P2P or local copy).");
                    return;
                }

                bool isFilePayload = cloudItem.Type == "ImageLink" || cloudItem.Type == "Image" || cloudItem.Type == "Pdf" ||
                                    cloudItem.Type == "Archive" || cloudItem.Type == "Video" || cloudItem.Type == "Document" ||
                                    cloudItem.Type == "Presentation" || cloudItem.Type == "Audio" || cloudItem.Type == "File";

                // Resolve download URL: try every possible combination to get a valid HTTP URL
                string resolvedUrl = cloudItem.Raw ?? "";

                // Step 1: If Raw is already a full HTTP URL, use it
                if (!resolvedUrl.StartsWith("http"))
                {
                    // Step 2: DownloadUrl might be a full URL
                    if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && cloudItem.DownloadUrl.StartsWith("http"))
                        resolvedUrl = cloudItem.DownloadUrl;
                    // Step 3: DownloadUrl is relative but SenderUrl is absolute
                    else if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && !string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.StartsWith("http"))
                        resolvedUrl = cloudItem.SenderUrl.TrimEnd('/') + (cloudItem.DownloadUrl.StartsWith("/") ? cloudItem.DownloadUrl : "/" + cloudItem.DownloadUrl);
                    // Step 4: Raw is a relative path like /download?path=..., combine with SenderUrl
                    else if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.StartsWith("http") && resolvedUrl.StartsWith("/"))
                        resolvedUrl = cloudItem.SenderUrl.TrimEnd('/') + resolvedUrl;
                    // Step 5: Raw contains a file path like C:\... — try to build URL from SenderUrl
                    else if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.StartsWith("http") && isFilePayload)
                        resolvedUrl = cloudItem.SenderUrl.TrimEnd('/') + "/download?path=" + Uri.EscapeDataString(resolvedUrl);
                }
                cloudItem.Raw = resolvedUrl;

                if (isFilePayload && resolvedUrl.StartsWith("http"))
                {
                    _ = FetchAndInjectCloudFile(cloudItem);
                }
                else
                {
                    // Skip blank text items — never create empty cards
                    if (string.IsNullOrWhiteSpace(cloudItem.Raw))
                    {
                        Logger.LogAction("FIREBASE SSE", "Skipped empty/whitespace-only text item from cloud.");
                        return;
                    }

                    // Detect transfer method: Cloudflare tunnel vs Firebase cloud
                    bool isCloudflare = (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com")) ||
                                        (!string.IsNullOrEmpty(cloudItem.Raw) && cloudItem.Raw.Contains(".trycloudflare.com"));
                    var clip = new ClipboardItem
                    {
                        RawContent = cloudItem.Raw,
                        FileName = cloudItem.Title,
                        Extension = cloudItem.Type == "Url" ? "LINK" : "CLOUD",
                        ItemType = cloudItem.Type == "Url" ? ClipboardItemType.Url : ClipboardItemType.Text,
                        SourceDeviceName = cloudItem.SourceDeviceName ?? "Remote",
                        SourceDeviceType = "Mobile",
                        TransferMethod = isCloudflare ? "Cloudflare" : "Cloud"
                    };
                    clip.EvaluateSmartActions();
                    _viewModel.DroppedItems.Insert(0, clip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));

                    // Auto-copy text to system clipboard for instant paste
                    try
                    {
                        if (!string.IsNullOrEmpty(cloudItem.Raw))
                            System.Windows.Clipboard.SetText(cloudItem.Raw);
                    }
                    catch { }

                    AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ {cloudItem.SourceDeviceName}: {(cloudItem.Raw?.Length > 40 ? cloudItem.Raw.Substring(0, 40) + "..." : cloudItem.Raw)}");
                }
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        // FORCED SYNC POLLER: Lightweight check for items force-sent to us
        // ═══════════════════════════════════════════════════════════════════
        private async Task RunForcedSyncPoller(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string deviceId = SettingsManager.Current.DeviceId;
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        string forcedUrl = $"{FIREBASE_BASE}/forced_sync/{deviceId}.json";
                        var forcedRes = await _pollClient.GetAsync(forcedUrl, ct);
                        if (forcedRes.IsSuccessStatusCode)
                        {
                            var forcedJson = await forcedRes.Content.ReadAsStringAsync();
                            if (!string.IsNullOrWhiteSpace(forcedJson) && forcedJson != "null")
                            {
                                ProcessForcedSyncPayload(forcedJson, deviceId);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogAction("FORCED SYNC", "Poll error: " + ex.Message);
                }

                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }

        private async Task FetchAndInjectCloudFile(CloudItem cloudItem)
        {
            ClipboardItem? progressClip = null;
            try
            {
                string senderName = string.IsNullOrWhiteSpace(cloudItem.SourceDeviceName) ? "CloudSync" : cloudItem.SourceDeviceName.Replace(" ", "_");
                string extractPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Synced", senderName);
                Directory.CreateDirectory(extractPath);

                string fallbackExt = cloudItem.Type == "Pdf" ? ".pdf" : cloudItem.Type == "Archive" ? ".zip" : cloudItem.Type == "Video" ? ".mp4" : cloudItem.Type == "Audio" ? ".mp3" : cloudItem.Type == "Document" ? ".docx" : cloudItem.Type == "Presentation" ? ".pptx" : ".jpg";
                string safeTitle = (cloudItem.Title ?? "file").Replace("/", "_").Replace("\\", "_");
                string filePath = Path.Combine(extractPath, safeTitle);
                if (!Path.HasExtension(safeTitle)) filePath += fallbackExt;

                int counter = 1;
                string basePath = filePath;
                while (File.Exists(filePath))
                {
                    filePath = Path.Combine(extractPath, $"{Path.GetFileNameWithoutExtension(basePath)}_{counter++}{Path.GetExtension(basePath)}");
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    progressClip = new ClipboardItem
                    {
                        RawContent = $"⏳ Downloading from {cloudItem.SourceDeviceName}...",
                        FileName = cloudItem.Title,
                        Extension = "DOWNLOADING",
                        ItemType = ClipboardItemType.Text
                    };
                    _viewModel.DroppedItems.Insert(0, progressClip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                });

                // No auth header needed — /download is now public (before auth barrier)
                // Enhanced download with fallback: try primary URL, then alternative URLs
                HttpResponseMessage response = null;
                int maxRetries = 3;
                int[] retryDelays = { 2000, 5000, 8000 }; // 2s, 5s, 8s

                using var downloadClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
                
                // Build fallback URL list: primary first, then alternatives
                var urlsToTry = new List<string> { cloudItem.Raw };
                
                // If primary is Cloudflare, add DownloadUrl and SenderUrl-based alternatives
                if (cloudItem.Raw.Contains(".trycloudflare.com"))
                {
                    // DownloadUrl might be a Firebase Storage URL (firebasestorage.googleapis.com)
                    if (!string.IsNullOrEmpty(cloudItem.DownloadUrl) && cloudItem.DownloadUrl.StartsWith("http") && cloudItem.DownloadUrl != cloudItem.Raw)
                        urlsToTry.Add(cloudItem.DownloadUrl);
                    
                    // SenderUrl might be a different Cloudflare URL (tunnel restarted)
                    if (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com") && !cloudItem.Raw.Contains(cloudItem.SenderUrl))
                    {
                        // Rebuild download URL with the sender's current tunnel URL
                        var pathMatch = System.Text.RegularExpressions.Regex.Match(cloudItem.Raw, @"/download\?path=(.+)$");
                        if (pathMatch.Success)
                            urlsToTry.Add($"{cloudItem.SenderUrl.TrimEnd('/')}/download?path={pathMatch.Groups[1].Value}");
                    }
                }

                string successUrl = null;
                foreach (var tryUrl in urlsToTry)
                {
                    bool succeeded = false;
                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            if (attempt > 0)
                            {
                                Logger.LogAction("FIREBASE SSE", $"Download retry {attempt + 1}/{maxRetries} after {retryDelays[attempt - 1]}ms...");
                                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (progressClip != null)
                                        progressClip.RawContent = $"🔄 Retry {attempt + 1}/{maxRetries} — {cloudItem.Title}";
                                });
                                await Task.Delay(retryDelays[attempt - 1]);
                            }

                            var request = new HttpRequestMessage(HttpMethod.Get, tryUrl);
                            response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                            if (response.IsSuccessStatusCode)
                            {
                                Logger.LogAction("FIREBASE SSE", $"Download connected on attempt {attempt + 1}: {tryUrl}");
                                successUrl = tryUrl;
                                succeeded = true;
                                break;
                            }

                            Logger.LogAction("FIREBASE SSE", $"Download attempt {attempt + 1} failed: HTTP {(int)response.StatusCode} from {tryUrl}");
                        }
                        catch (Exception retryEx)
                        {
                            Logger.LogAction("FIREBASE SSE", $"Download attempt {attempt + 1} error: {retryEx.Message}");
                        }
                    }
                    
                    if (succeeded) break;
                    
                    // Log that we're trying the next fallback URL
                    if (urlsToTry.IndexOf(tryUrl) < urlsToTry.Count - 1)
                    {
                        string nextUrl = urlsToTry[urlsToTry.IndexOf(tryUrl) + 1];
                        Logger.LogAction("FIREBASE SSE", $"Primary URL failed — trying fallback: {nextUrl}");
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (progressClip != null)
                                progressClip.RawContent = $"🔄 Trying alternate download source — {cloudItem.Title}";
                        });
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    int code = response != null ? (int)response.StatusCode : 0;
                    string tried = string.Join(", ", urlsToTry.Select(u => u.Length > 60 ? u.Substring(0, 60) + "..." : u));
                    throw new Exception($"File Download Error: HTTP {code} after {maxRetries} attempts from {tried}");
                }

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                string totalSizeStr = totalBytes > 0
                    ? (totalBytes > 1_073_741_824 ? $"{totalBytes / 1_073_741_824.0:F1}GB" : $"{totalBytes / 1_048_576.0:F1}MB")
                    : "unknown";

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
                {
                    byte[] buffer = new byte[262144]; // 256KB buffer for better throughput
                    long totalRead = 0;
                    int bytesRead;
                    DateTime lastProgressUpdate = DateTime.MinValue;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 400 && progressClip != null)
                        {
                            lastProgressUpdate = DateTime.Now;
                            string readStr = totalRead > 1_073_741_824 ? $"{totalRead / 1_073_741_824.0:F1}GB" : $"{totalRead / 1_048_576.0:F1}MB";
                            int pct = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : -1;
                            string statusText = pct >= 0
                                ? $"⬇️ {pct}% — {readStr}/{totalSizeStr} — {cloudItem.Title}"
                                : $"⬇️ {readStr} — {cloudItem.Title}";

                            // Non-blocking — don't stall the download waiting for UI
                            progressClip.RawContent = statusText;
                            progressClip.FileName = $"{cloudItem.Title} ({pct}%)";
                        }
                    }
                }

                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (progressClip != null)
                    {
                        _viewModel.DroppedItems.Remove(progressClip);
                    }

                    var fileInfo = new FileInfo(filePath);
                    string sizeStr = fileInfo.Length > 1_073_741_824
                        ? $"{fileInfo.Length / 1_073_741_824.0:F1} GB"
                        : $"{fileInfo.Length / 1_048_576.0:F1} MB";

                    try { System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath }); } catch { }
                    AdvanceClip.Windows.ToastWindow.ShowToast($"✅ {cloudItem.Title} ({sizeStr}) from {cloudItem.SourceDeviceName}");

                    var clip = new ClipboardItem(filePath);
                    clip.SourceDeviceName = cloudItem.SourceDeviceName ?? "Remote";
                    clip.SourceDeviceType = "Mobile";
                    bool isCfDownload = (!string.IsNullOrEmpty(cloudItem.Raw) && cloudItem.Raw.Contains(".trycloudflare.com")) ||
                                        (!string.IsNullOrEmpty(cloudItem.SenderUrl) && cloudItem.SenderUrl.Contains(".trycloudflare.com"));
                    clip.TransferMethod = isCfDownload ? "Cloudflare" : "Cloud";

                    if (clip.ItemType == ClipboardItemType.Image && clip.Icon == null)
                    {
                        try
                        {
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit();
                            bmp.UriSource = new Uri(filePath);
                            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bmp.DecodePixelWidth = 400;
                            bmp.EndInit();
                            bmp.Freeze();
                            clip.Icon = bmp;
                        }
                        catch (Exception imgEx)
                        {
                            Logger.LogAction("FIREBASE SSE", $"Image preview load failed: {imgEx.Message}");
                        }
                    }

                    clip.EvaluateSmartActions();
                    _viewModel.DroppedItems.Insert(0, clip);
                    _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                });
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE SSE", $"File Download Error: {ex.Message} | URL: {cloudItem.Raw}");
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (progressClip != null)
                    {
                        progressClip.RawContent = $"❌ Download failed: {cloudItem.Title}";
                        progressClip.FileName = cloudItem.Title;
                    }
                    AdvanceClip.Windows.ToastWindow.ShowToast($"❌ Download failed: {cloudItem.Title}");
                });
            }
        }

        private class CloudItem
        {
            public string Id { get; set; }
            public long Timestamp { get; set; }
            public string Type { get; set; }
            public string Title { get; set; }
            public string Raw { get; set; }
            public string DownloadUrl { get; set; }
            public string SenderUrl { get; set; }
            public string SourceDeviceName { get; set; }
        }

        private void ProcessForcedSyncPayload(string json, string deviceId)
        {
            _ = Task.Run(async () =>
            {
                try { await ProcessForcedSyncPayloadCore(json, deviceId); }
                catch (Exception ex) { Logger.LogAction("FIREBASE", $"ProcessForcedSyncPayload crash: {ex.Message}"); }
            });
        }

        private async Task ProcessForcedSyncPayloadCore(string json, string deviceId)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    var keysToDelete = new List<string>();

                    foreach (JsonProperty prop in root.EnumerateObject())
                    {
                        var data = prop.Value;
                        string type = data.TryGetProperty("Type", out var t) ? t.GetString() ?? "Text" : "Text";
                        string title = data.TryGetProperty("Title", out var t2) ? t2.GetString() ?? "" : "";
                        string raw = data.TryGetProperty("Raw", out var t3) ? t3.GetString() ?? "" : "";
                        string source = data.TryGetProperty("ForcedBy", out var t4) ? t4.GetString() ?? "" :
                                       (data.TryGetProperty("SourceDeviceName", out var t5) ? t5.GetString() ?? "" : "");
                        string downloadUrl = data.TryGetProperty("DownloadUrl", out var t6) ? t6.GetString() ?? "" : "";
                        string senderUrl = data.TryGetProperty("SenderUrl", out var t7) ? t7.GetString() ?? "" : "";

                        Logger.LogAction("FORCED SYNC", $"Received from '{source}': {type} - {title}");

                        // Resolve relative URLs using SenderUrl
                        string resolvedUrl = raw;
                        if (!resolvedUrl.StartsWith("http") && !string.IsNullOrEmpty(downloadUrl))
                        {
                            if (downloadUrl.StartsWith("http"))
                                resolvedUrl = downloadUrl;
                            else if (!string.IsNullOrEmpty(senderUrl) && senderUrl.StartsWith("http"))
                                resolvedUrl = senderUrl + downloadUrl;
                        }
                        if (!resolvedUrl.StartsWith("http") && !string.IsNullOrEmpty(senderUrl) && senderUrl.StartsWith("http") && resolvedUrl.StartsWith("/"))
                            resolvedUrl = senderUrl + resolvedUrl;

                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            bool isFilePayload = type == "Image" || type == "ImageLink" || type == "Pdf" || type == "Archive" || type == "Video" || type == "Document" || type == "File";

                            if (isFilePayload && resolvedUrl.StartsWith("http"))
                            {
                                var cloudItem = new CloudItem { Id = prop.Name, Type = type, Title = title, Raw = resolvedUrl, DownloadUrl = downloadUrl, SenderUrl = senderUrl, SourceDeviceName = source };
                                _ = FetchAndInjectCloudFile(cloudItem);
                            }
                            else
                            {
                                // Skip blank items — never allow empty cards
                                if (string.IsNullOrWhiteSpace(raw)) return;

                                var clip = new ClipboardItem
                                {
                                    RawContent = raw,
                                    FileName = title,
                                    Extension = "FORCED",
                                    ItemType = type == "Url" ? ClipboardItemType.Url : ClipboardItemType.Text,
                                    SourceDeviceName = source,
                                    SourceDeviceType = "Mobile",
                                    TransferMethod = "ForceSend"
                                };
                                clip.EvaluateSmartActions();
                                _viewModel.DroppedItems.Insert(0, clip);
                                _viewModel.OnPropertyChanged(nameof(_viewModel.ShelfVisibility));
                            }

                            AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ Force Sync from {source}");
                        });

                        keysToDelete.Add(prop.Name);
                    }

                    foreach (var key in keysToDelete)
                    {
                        string deleteUrl = $"{FIREBASE_BASE}/forced_sync/{deviceId}/{key}.json";
                        try { await _pollClient.DeleteAsync(deleteUrl); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FORCED SYNC", "Parse Error: " + ex.Message);
            }
        }
    }
}
