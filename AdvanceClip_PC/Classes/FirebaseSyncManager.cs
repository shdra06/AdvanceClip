using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AdvanceClip.ViewModels;
using System.Linq;
using System.IO;
using Firebase.Storage;

namespace AdvanceClip.Classes
{
    public class FirebaseSyncManager
    {
        private static readonly HttpClient _client = new HttpClient();
        private const string FIREBASE_URL = "https://advance-sync-default-rtdb.firebaseio.com/clipboard.json";
        
        // Public Cloudflare URL for constructing file download links
        public static string CachedGlobalUrl { get; set; } = "";
        // Whether the Cloudflare tunnel has been verified working (HTTP 200 on self-ping)
        public static bool CachedTunnelVerified { get; set; } = false;
        // Local LAN server URL as fallback when Cloudflare is off
        public static string CachedLocalUrl { get; set; } = "";
        // Firebase Storage bucket for global file uploads when Cloudflare is unavailable
        private const string FIREBASE_STORAGE_BUCKET = "advance-sync.appspot.com";
        
        // Time-windowed dedup: track fingerprint → last push time (10s cooldown)
        private static readonly Dictionary<string, long> _recentPushTimes = new();
        private const int DEDUP_COOLDOWN_MS = 10_000; // 10 seconds — same content within this window is skipped
        private const int AUTO_DELETE_TEXT_MS = 5 * 60_000; // 5 minutes — matches backlog catch-up window
        private const int AUTO_DELETE_FILE_MS = 24 * 60 * 60_000; // 24 hours for file items (large files need time to download)

        public static async Task PushToGlobalSync(ClipboardItem item)
        {
            if (!SettingsManager.Current.EnableGlobalFirebaseSync)
                return;

            // Time-windowed dedup: skip if same content was pushed within last 10 seconds
            string fingerprint = $"{item.ItemType}::{(item.RawContent ?? "").Substring(0, Math.Min(200, (item.RawContent ?? "").Length))}";
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_recentPushTimes)
            {
                if (_recentPushTimes.TryGetValue(fingerprint, out long lastPushTime))
                {
                    if (nowMs - lastPushTime < DEDUP_COOLDOWN_MS)
                    {
                        Logger.LogAction("FIREBASE SYNC", "Skipped rapid-fire duplicate (same content within 10s cooldown)");
                        return;
                    }
                }
                _recentPushTimes[fingerprint] = nowMs;
                
                // Clean old fingerprints (older than 60s)
                var stale = _recentPushTimes.Where(kv => nowMs - kv.Value > 60_000).Select(kv => kv.Key).ToList();
                foreach (var key in stale) _recentPushTimes.Remove(key);
            }

            // Safety: If no DeviceName is set, use the machine name so we can always filter self-echoes
            string deviceName = SettingsManager.Current.DeviceName;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                deviceName = Environment.MachineName;
            }

            try
            {

                // For files: always wait for Cloudflare tunnel first — it's the only reliable cross-network URL
                bool isFile = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
                string downloadUrl = "";
                string raw = item.RawContent ?? "";

                if (isFile)
                {
                    // Skip incomplete/locked download files
                    string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
                    if (ext is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial")
                    {
                        Logger.LogAction("FIREBASE SYNC", $"Skipped incomplete download: {item.FileName}");
                        return;
                    }
                    // If tunnel not ready yet, wait up to 30s before proceeding
                    if (string.IsNullOrEmpty(CachedGlobalUrl) || !CachedGlobalUrl.Contains("trycloudflare.com"))
                    {
                        Logger.LogAction("FIREBASE SYNC", $"Waiting for Cloudflare tunnel before sending '{item.FileName}'...");
                        for (int i = 0; i < 60; i++) // 60 x 500ms = 30s max
                        {
                            await Task.Delay(500);
                            if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com"))
                            {
                                Logger.LogAction("FIREBASE SYNC", $"Cloudflare ready after {(i + 1) * 500}ms");
                                break;
                            }
                        }
                    }
                }

                if (isFile && !string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com") && CachedTunnelVerified)
                {
                    // Only use Cloudflare URL if the tunnel has been VERIFIED working (HTTP 200 self-ping)
                    downloadUrl = $"{CachedGlobalUrl}/download?path={Uri.EscapeDataString(item.FilePath)}";
                    raw = downloadUrl;
                    Logger.LogAction("FIREBASE SYNC", $"File '{item.FileName}' → Cloudflare (verified): {downloadUrl}");
                }
                else if (isFile && !string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com") && !CachedTunnelVerified)
                {
                    // Tunnel URL exists but NOT verified — skip it and use Firebase Storage
                    Logger.LogAction("FIREBASE SYNC", $"⚠️ Cloudflare tunnel exists but NOT verified — skipping for '{item.FileName}', using Firebase Storage fallback");
                }
                if (isFile && string.IsNullOrEmpty(downloadUrl))
                {
                    // No working Cloudflare — try Firebase Storage upload

                    Logger.LogAction("FIREBASE SYNC", $"Cloudflare unavailable — uploading '{item.FileName}' to Firebase Storage...");
                    string storageUrl = await UploadFileToStorageAsync(item.FilePath);
                    if (!string.IsNullOrEmpty(storageUrl))
                    {
                        downloadUrl = storageUrl;
                        raw = storageUrl;
                        Logger.LogAction("FIREBASE SYNC", $"File '{item.FileName}' → Firebase Storage: {storageUrl}");
                    }
                    else
                    {
                        // Both Cloudflare and Firebase Storage failed — don't write useless LAN URL
                        Logger.LogAction("FIREBASE SYNC", $"⚠️ Cannot sync file '{item.FileName}' — no Cloudflare tunnel and Firebase Storage upload failed. File is only available on LAN.");
                        
                        // Show toast on PC so user knows
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                            AdvanceClip.Windows.ToastWindow.ShowToast($"⚠️ {item.FileName} — Cloudflare offline, can't share remotely");
                        });

                        return; // Skip this file — don't push an unreachable URL to Firebase
                    }
                }
                
                var payload = new
                {
                    Title = string.IsNullOrEmpty(item.FileName) ? (item.RawContent?.Length > 30 ? item.RawContent.Substring(0, 30) + "..." : item.RawContent) : item.FileName,
                    Type = item.ItemType.ToString(),
                    Raw = raw,
                    PreviewUrl = isFile && downloadUrl != "" ? downloadUrl : "",
                    DownloadUrl = downloadUrl,
                    FileName = item.FileName ?? "",
                    FileSize = isFile ? new FileInfo(item.FilePath).Length : 0,
                    SenderUrl = !string.IsNullOrEmpty(CachedGlobalUrl) ? CachedGlobalUrl : CachedLocalUrl ?? "", // Cloudflare or LAN URL so receivers can build download links
                    Time = item.DateCopied.ToString("HH:mm:ss"),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SourceDeviceName = deviceName,
                    SourceDeviceType = "PC"
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _client.PostAsync(FIREBASE_URL, content);
                
                if (response.IsSuccessStatusCode)
                {

                    Logger.LogAction("FIREBASE SYNC", $"Pushed item to global cloud as '{deviceName}'");
                    
                    // Auto-delete: 90s for text, 30min for files (need time to download large files)
                    string responseBody = await response.Content.ReadAsStringAsync();
                    try
                    {
                        var responseObj = JsonSerializer.Deserialize<Dictionary<string, string>>(responseBody);
                        if (responseObj != null && responseObj.TryGetValue("name", out string? entryKey) && !string.IsNullOrEmpty(entryKey))
                        {
                            _ = Task.Run(async () =>
                            {
                                int deleteDelay = isFile ? AUTO_DELETE_FILE_MS : AUTO_DELETE_TEXT_MS;
                                await Task.Delay(deleteDelay);
                                try
                                {
                                    string deleteUrl = $"https://advance-sync-default-rtdb.firebaseio.com/clipboard/{entryKey}.json";
                                    await _client.DeleteAsync(deleteUrl);
                                    Logger.LogAction("FIREBASE CLEANUP", $"Auto-deleted entry '{entryKey}'");
                                }
                                catch { }
                            });
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {

                Logger.LogAction("FIREBASE ERROR", ex.Message);
            }
        }

        public static async Task<string> UploadFileToStorageAsync(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var safeName = "archives/" + fileName + "_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Try multiple bucket names — Firebase project naming can vary
            string[] buckets = new[]
            {
                "advance-sync-default-rtdb.firebasestorage.app",
                "advance-sync.firebasestorage.app",
                "advance-sync-default-rtdb.appspot.com",
                "advance-sync.appspot.com"
            };

            foreach (var bucket in buckets)
            {
                try
                {
                    Logger.LogAction("FIREBASE STORAGE", $"Trying bucket: {bucket}");
                    using var stream = File.OpenRead(filePath);
                    var task = new FirebaseStorage(bucket)
                        .Child(safeName)
                        .PutAsync(stream);

                    var downloadUrl = await task;
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        Logger.LogAction("FIREBASE STORAGE", $"Upload success via {bucket}: {downloadUrl}");
                        return downloadUrl;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogAction("FIREBASE STORAGE", $"Bucket {bucket} failed: {ex.Message}");
                }
            }

            Logger.LogAction("FIREBASE STORAGE", "All buckets failed — file upload not possible");
            return "";
        }

        public static async Task PushTunnelUrl(string url, bool isOnline, string localIp = "")
        {
            try
            {
                var payload = new
                {
                    DeviceId = SettingsManager.Current.DeviceId,
                    DeviceName = SettingsManager.Current.DeviceName,
                    DeviceType = "PC",
                    Url = localIp.Contains("http") ? localIp : url,
                    LocalIp = localIp,
                    GlobalUrl = url.Contains("trycloudflare.com") ? url : "",
                    IsOnline = isOnline,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Use PUT to register or update our specific Device node
                string tunnelNodeUrl = $"https://advance-sync-default-rtdb.firebaseio.com/active_devices/{SettingsManager.Current.DeviceId}.json";
                var response = await _client.PutAsync(tunnelNodeUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    Logger.LogAction("FIREBASE SYNC", $"Tunnel DNS updated: {url} [{isOnline}]");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE ERROR", $"Tunnel DNS Failure: {ex.Message}");
            }
        }

        /// <summary>
        /// Force-send clipboard items to specific target devices via Firebase forced_sync node.
        /// Files of ANY size are supported — uses Cloudflare download URLs (no upload needed).
        /// </summary>
        public static async Task<int> ForceSendToDevices(List<ClipboardItem> items, List<string> targetDeviceIds)
        {
            int sent = 0;
            string deviceName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

            foreach (var targetId in targetDeviceIds)
            {
                foreach (var item in items)
                {
                    try
                    {
                        bool isFile = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
                        string downloadUrl = "";
                        string raw = item.RawContent ?? "";

                        if (isFile)
                        {
                            // Use Cloudflare URL (preferred — no size limit, instant)
                            if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com"))
                            {
                                downloadUrl = $"{CachedGlobalUrl}/download?path={Uri.EscapeDataString(item.FilePath)}";
                                raw = downloadUrl;
                                long fileSize = new FileInfo(item.FilePath).Length;
                                Logger.LogAction("FORCED SYNC", $"File '{item.FileName}' ({fileSize / (1024*1024)}MB) → Cloudflare URL");
                            }
                            else
                            {
                                // Wait for Cloudflare tunnel
                                Logger.LogAction("FORCED SYNC", $"No Cloudflare yet — waiting up to 20s...");
                                for (int i = 0; i < 40; i++)
                                {
                                    await Task.Delay(500);
                                    if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com")) break;
                                }

                                if (!string.IsNullOrEmpty(CachedGlobalUrl) && CachedGlobalUrl.Contains("trycloudflare.com"))
                                {
                                    downloadUrl = $"{CachedGlobalUrl}/download?path={Uri.EscapeDataString(item.FilePath)}";
                                    raw = downloadUrl;
                                    Logger.LogAction("FORCED SYNC", $"File '{item.FileName}' → Cloudflare URL (delayed)");
                                }
                                else
                                {
                                    // Firebase Storage fallback
                                    Logger.LogAction("FORCED SYNC", $"Uploading '{item.FileName}' to Firebase Storage...");
                                    string storageUrl = await UploadFileToStorageAsync(item.FilePath);
                                    if (!string.IsNullOrEmpty(storageUrl))
                                    {
                                        downloadUrl = storageUrl;
                                        raw = storageUrl;
                                        Logger.LogAction("FORCED SYNC", $"File '{item.FileName}' → Firebase Storage");
                                    }
                                    else
                                    {
                                        // Both Cloudflare and Firebase Storage failed
                                        Logger.LogAction("FORCED SYNC", $"⚠️ Cannot send file '{item.FileName}' remotely — no tunnel, no storage");
                                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                            AdvanceClip.Windows.ToastWindow.ShowToast($"⚠️ {item.FileName} — can't share remotely (no tunnel)");
                                        });
                                        continue;
                                    }
                                }
                            }
                        }

                        var payload = new
                        {
                            Title = string.IsNullOrEmpty(item.FileName) ? (raw.Length > 30 ? raw.Substring(0, 30) + "..." : raw) : item.FileName,
                            Type = item.ItemType.ToString(),
                            Raw = raw,
                            DownloadUrl = downloadUrl,
                            FileName = item.FileName ?? "",
                            FileSize = isFile ? new FileInfo(item.FilePath).Length : 0,
                            SenderUrl = !string.IsNullOrEmpty(CachedGlobalUrl) ? CachedGlobalUrl : CachedLocalUrl ?? "",
                            ForcedBy = deviceName,
                            ForcedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            SourceDeviceName = deviceName,
                            SourceDeviceType = "PC",
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };

                        string json = JsonSerializer.Serialize(payload);
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        string url2 = $"https://advance-sync-default-rtdb.firebaseio.com/forced_sync/{targetId}.json";
                        var response = await _client.PostAsync(url2, content);
                        if (response.IsSuccessStatusCode) sent++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("FORCED SYNC", $"Send error: {ex.Message}");
                    }
                }
            }
            return sent;
        }

        /// <summary>
        /// Fetch all active devices from Firebase for the forced sync device picker.
        /// </summary>
        public static async Task<List<(string Id, string Name, string Type, bool IsOnline, string LocalIp, string GlobalUrl)>> GetActiveDevices()
        {
            var devices = new List<(string Id, string Name, string Type, bool IsOnline, string LocalIp, string GlobalUrl)>();
            try
            {
                string url = "https://advance-sync-default-rtdb.firebaseio.com/active_devices.json";
                var response = await _client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        using var doc = JsonDocument.Parse(json);
                        string myId = SettingsManager.Current.DeviceId;
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == myId) continue; // Skip self
                            string name = prop.Value.TryGetProperty("DeviceName", out var n) ? n.GetString() ?? "" : "";
                            string type = prop.Value.TryGetProperty("DeviceType", out var dt) ? dt.GetString() ?? "" : "";
                            bool online = prop.Value.TryGetProperty("IsOnline", out var on) && on.GetBoolean();
                            string localIp = prop.Value.TryGetProperty("LocalIp", out var lip) ? lip.GetString() ?? "" : "";
                            string globalUrl = prop.Value.TryGetProperty("GlobalUrl", out var gurl) ? gurl.GetString() ?? "" : "";
                            
                            // TTL check: treat devices with heartbeat older than 2 minutes as offline
                            if (online && prop.Value.TryGetProperty("Timestamp", out var ts))
                            {
                                long deviceTs = ts.GetInt64();
                                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                if (nowMs - deviceTs > 120_000) online = false; // Stale — hasn't heartbeated in 2 min
                            }
                            
                            devices.Add((prop.Name, name, type, online, localIp, globalUrl));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"GetActiveDevices error: {ex.Message}");
            }
            return devices;
        }
        /// <summary>
        /// Purge old GUID-based device entries from Firebase that were created by the old NewGuid() logic.
        /// Only removes entries that are genuinely stale (offline for 24+ hours).
        /// Active old-version devices are preserved so they remain visible for sync.
        /// </summary>
        public static async Task CleanupStaleDevices()
        {
            try
            {
                string url = "https://advance-sync-default-rtdb.firebaseio.com/active_devices.json";
                var response = await _client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(json) && json != "null")
                    {
                        using var doc = JsonDocument.Parse(json);
                        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        const long STALE_THRESHOLD_MS = 24 * 60 * 60_000; // 24 hours

                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            // Old format: a raw GUID like "a1b2c3d4-e5f6-7890-..."
                            // New format: "PC_MACHINENAME_USERNAME"
                            if (prop.Name.Contains('-') && !prop.Name.StartsWith("PC_") && !prop.Name.StartsWith("Mobile_"))
                            {
                                // Only delete if genuinely stale (no heartbeat in 24 hours)
                                long deviceTs = 0;
                                if (prop.Value.TryGetProperty("Timestamp", out var ts))
                                    deviceTs = ts.GetInt64();

                                if (deviceTs > 0 && (nowMs - deviceTs) < STALE_THRESHOLD_MS)
                                {
                                    // Old-format device is still active — keep it
                                    Logger.LogAction("FIREBASE CLEANUP", $"Keeping active old-format device: {prop.Name}");
                                    continue;
                                }

                                // Stale GUID-based entry — safe to remove
                                string deleteUrl = $"https://advance-sync-default-rtdb.firebaseio.com/active_devices/{prop.Name}.json";
                                await _client.DeleteAsync(deleteUrl);
                                Logger.LogAction("FIREBASE CLEANUP", $"Removed stale device: {prop.Name}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"CleanupStaleDevices error: {ex.Message}");
            }
        }

        // ═══ Device Groups CRUD ═══

        public static async Task<List<DeviceGroupInfo>> GetDeviceGroups()
        {
            var result = new List<DeviceGroupInfo>();
            try
            {
                string url = "https://advance-sync-default-rtdb.firebaseio.com/device_groups.json";
                var response = await _client.GetStringAsync(url);
                if (!string.IsNullOrWhiteSpace(response) && response != "null")
                {
                    using var doc = JsonDocument.Parse(response);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var group = new DeviceGroupInfo { Id = prop.Name };
                        if (prop.Value.TryGetProperty("name", out var nameProp))
                            group.Name = nameProp.GetString() ?? "";
                        if (prop.Value.TryGetProperty("deviceNames", out var devsProp) && devsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var dev in devsProp.EnumerateArray())
                                group.DeviceNames.Add(dev.GetString() ?? "");
                        }
                        result.Add(group);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"GetDeviceGroups error: {ex.Message}");
            }
            return result;
        }

        public static async Task SaveDeviceGroup(string groupId, string name, List<string> deviceNames)
        {
            try
            {
                string url = $"https://advance-sync-default-rtdb.firebaseio.com/device_groups/{groupId}.json";
                var payload = new { name, deviceNames };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _client.PutAsync(url, content);
                Logger.LogAction("FIREBASE", $"Saved group '{name}' with {deviceNames.Count} devices");
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"SaveDeviceGroup error: {ex.Message}");
            }
        }

        public static async Task DeleteDeviceGroup(string groupId)
        {
            try
            {
                string url = $"https://advance-sync-default-rtdb.firebaseio.com/device_groups/{groupId}.json";
                await _client.DeleteAsync(url);
                Logger.LogAction("FIREBASE", $"Deleted group {groupId}");
            }
            catch (Exception ex)
            {
                Logger.LogAction("FIREBASE", $"DeleteDeviceGroup error: {ex.Message}");
            }
        }
    }

    public class DeviceGroupInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> DeviceNames { get; set; } = new();
    }
}
