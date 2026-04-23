using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace AdvanceClip.Classes
{
    public class PairedDevice
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "Mobile"; // Mobile, PC, Browser
        public string PairingKey { get; set; } = "";
        public DateTime PairedAt { get; set; } = DateTime.Now;
        public DateTime LastSeen { get; set; } = DateTime.Now;
        public string LastKnownIP { get; set; } = "";
    }

    public static class DevicePairingManager
    {
        private static readonly string _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvanceClip", "paired_devices.json");

        private static List<PairedDevice> _pairedDevices = new();
        private static readonly object _lock = new();

        static DevicePairingManager()
        {
            Load();
        }

        // ═══ Pairing Key Management ═══

        /// <summary>
        /// Ensures a pairing key exists in settings. Generates one if missing.
        /// </summary>
        public static string EnsurePairingKey()
        {
            if (string.IsNullOrEmpty(SettingsManager.Current.PairingKey))
            {
                SettingsManager.Current.PairingKey = Guid.NewGuid().ToString("N"); // 32-char hex
                SettingsManager.Save();
            }
            return SettingsManager.Current.PairingKey;
        }

        /// <summary>
        /// Regenerate the pairing key (invalidates all previous QR codes).
        /// </summary>
        public static string RegeneratePairingKey()
        {
            SettingsManager.Current.PairingKey = Guid.NewGuid().ToString("N");
            SettingsManager.Save();
            return SettingsManager.Current.PairingKey;
        }

        // ═══ QR Code Generation ═══

        /// <summary>
        /// Builds the JSON payload for the QR code containing all connection info.
        /// </summary>
        public static string BuildQRPayload(string localUrl, string globalUrl, string pin)
        {
            string pairingKey = EnsurePairingKey();
            var payload = new
            {
                app = "ClipFlow",
                v = 1,
                key = pairingKey,
                local = localUrl ?? "",
                global = globalUrl ?? "",
                pin = pin ?? "",
                name = SettingsManager.Current.DeviceName ?? Environment.MachineName,
                id = SettingsManager.Current.DeviceId ?? Environment.MachineName
            };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Generates a QR code BitmapSource from the pairing payload.
        /// </summary>
        public static BitmapSource GenerateQRCode(string localUrl, string globalUrl, string pin, int size = 250)
        {
            try
            {
                string payload = BuildQRPayload(localUrl, globalUrl, pin);
                Logger.LogAction("QR", $"Generating QR with payload: {payload.Substring(0, Math.Min(80, payload.Length))}...");

                var writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new EncodingOptions
                    {
                        Width = size,
                        Height = size,
                        Margin = 1,
                        PureBarcode = false
                    }
                };

                // ZXing.Net generates a System.Drawing.Bitmap
                using var bitmap = writer.Write(payload);

                // Convert System.Drawing.Bitmap → WPF BitmapSource
                var bitmapData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                var bmpSource = BitmapSource.Create(
                    bitmapData.Width, bitmapData.Height,
                    96, 96,
                    PixelFormats.Bgra32,
                    null,
                    bitmapData.Scan0,
                    bitmapData.Stride * bitmapData.Height,
                    bitmapData.Stride);

                bitmap.UnlockBits(bitmapData);
                bmpSource.Freeze();
                return bmpSource;
            }
            catch (Exception ex)
            {
                Logger.LogAction("QR ERROR", $"Failed to generate QR: {ex.Message}");
                return null;
            }
        }

        // ═══ Paired Device Management ═══

        public static List<PairedDevice> GetPairedDevices()
        {
            lock (_lock) return _pairedDevices.ToList();
        }

        /// <summary>
        /// Validates a pairing key and registers the device if valid.
        /// Returns true if pairing succeeded.
        /// </summary>
        public static bool TryPairDevice(string pairingKey, string deviceId, string deviceName, string deviceType, string remoteIP)
        {
            string expectedKey = EnsurePairingKey();
            if (pairingKey != expectedKey)
            {
                Logger.LogAction("PAIR", $"❌ Invalid pairing key from {deviceName} ({remoteIP})");
                return false;
            }

            lock (_lock)
            {
                // Update if already paired, otherwise add
                var existing = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (existing != null)
                {
                    existing.LastSeen = DateTime.Now;
                    existing.LastKnownIP = remoteIP;
                    existing.DeviceName = deviceName;
                    Logger.LogAction("PAIR", $"🔄 Re-paired existing device: {deviceName}");
                }
                else
                {
                    _pairedDevices.Add(new PairedDevice
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        DeviceType = deviceType,
                        PairingKey = pairingKey,
                        PairedAt = DateTime.Now,
                        LastSeen = DateTime.Now,
                        LastKnownIP = remoteIP
                    });
                    Logger.LogAction("PAIR", $"✅ New device paired: {deviceName} ({deviceType}) from {remoteIP}");
                }
            }

            Save();
            return true;
        }

        /// <summary>
        /// Check if a device with this pairing key is trusted (bypass PIN).
        /// </summary>
        public static bool IsDevicePaired(string pairingKey)
        {
            if (string.IsNullOrEmpty(pairingKey)) return false;
            string expectedKey = EnsurePairingKey();
            return pairingKey == expectedKey;
        }

        /// <summary>
        /// Update the last-seen timestamp for a paired device.
        /// </summary>
        public static void TouchDevice(string deviceId, string remoteIP)
        {
            lock (_lock)
            {
                var device = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device != null)
                {
                    device.LastSeen = DateTime.Now;
                    device.LastKnownIP = remoteIP;
                }
            }
            Save();
        }

        public static void RemoveDevice(string deviceId)
        {
            lock (_lock)
            {
                _pairedDevices.RemoveAll(d => d.DeviceId == deviceId);
            }
            Save();
            Logger.LogAction("PAIR", $"Removed device: {deviceId}");
        }

        // ═══ Persistence ═══

        private static void Load()
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    string json = File.ReadAllText(_storagePath);
                    _pairedDevices = JsonSerializer.Deserialize<List<PairedDevice>>(json) ?? new();
                    Logger.LogAction("PAIR", $"Loaded {_pairedDevices.Count} paired device(s)");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR", $"Load failed: {ex.Message}");
                _pairedDevices = new();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath));
                string json = JsonSerializer.Serialize(_pairedDevices, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                Logger.LogAction("PAIR", $"Save failed: {ex.Message}");
            }
        }
    }
}
