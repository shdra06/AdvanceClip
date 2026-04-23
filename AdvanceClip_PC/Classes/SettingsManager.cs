using System.ComponentModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdvanceClip.Classes
{
    public class AdvanceSettings : ObservableObject
    {
        private bool _keepItemOnDragOut = true;
        public bool KeepItemOnDragOut { get => _keepItemOnDragOut; set => SetProperty(ref _keepItemOnDragOut, value); }
        
        private string _geminiApiKey = "";
        public string GeminiApiKey { get => _geminiApiKey; set => SetProperty(ref _geminiApiKey, value); }

        private int _mediumFormWidth = 360;
        public int MediumFormWidth { get => _mediumFormWidth; set => SetProperty(ref _mediumFormWidth, value); }

        private int _mediumFormHeight = 380;
        public int MediumFormHeight { get => _mediumFormHeight; set => SetProperty(ref _mediumFormHeight, value); }

        private int _miniFormWidth = 260;
        public int MiniFormWidth { get => _miniFormWidth; set => SetProperty(ref _miniFormWidth, value); }

        private int _miniFormHeight = 260;
        public int MiniFormHeight { get => _miniFormHeight; set => SetProperty(ref _miniFormHeight, value); }

        private double _quickLookWidth = 0;
        public double QuickLookWidth { get => _quickLookWidth; set => SetProperty(ref _quickLookWidth, value); }

        private double _quickLookHeight = 0;
        public double QuickLookHeight { get => _quickLookHeight; set => SetProperty(ref _quickLookHeight, value); }

        private int _widgetTaskbarAlignment = 0;
        public int WidgetTaskbarAlignment { get => _widgetTaskbarAlignment; set => SetProperty(ref _widgetTaskbarAlignment, value); } // 0=Left, 1=Center, 2=Right
        
        // Tier 1 Settings
        private bool _enableLocalNetworkSync = false;
        public bool EnableLocalNetworkSync { get => _enableLocalNetworkSync; set => SetProperty(ref _enableLocalNetworkSync, value); }
        
        private bool _enableGlobalCloudflare = false;
        public bool EnableGlobalCloudflare { get => _enableGlobalCloudflare; set => SetProperty(ref _enableGlobalCloudflare, value); }
        
        private bool _enableGlobalFirebaseSync = true;
        public bool EnableGlobalFirebaseSync { get => _enableGlobalFirebaseSync; set => SetProperty(ref _enableGlobalFirebaseSync, value); }
        
        private string _webClientPinToken = "55555";
        public string WebClientPinToken { get => _webClientPinToken; set => SetProperty(ref _webClientPinToken, value); }
        
        private int _savedLocalPort = 0;
        public int SavedLocalPort { get => _savedLocalPort; set => SetProperty(ref _savedLocalPort, value); }

        private System.Collections.ObjectModel.ObservableCollection<string> _customSnifferPaths = new System.Collections.ObjectModel.ObservableCollection<string>();
        public System.Collections.ObjectModel.ObservableCollection<string> CustomSnifferPaths { get => _customSnifferPaths; set => SetProperty(ref _customSnifferPaths, value); }

        private string _customArchiveExtractionPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdvanceClip", "Extracted");
        public string CustomArchiveExtractionPath { get => _customArchiveExtractionPath; set => SetProperty(ref _customArchiveExtractionPath, value); }
        
        private string _deviceName = System.Environment.MachineName;
        public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }
        
        private string _deviceId = $"PC_{System.Environment.MachineName}_{System.Environment.UserName}".Replace(" ", "_");
        public string DeviceId { get => _deviceId; set => SetProperty(ref _deviceId, value); }

        private bool _enableQuickPasteHotkeys = true;
        public bool EnableQuickPasteHotkeys { get => _enableQuickPasteHotkeys; set => SetProperty(ref _enableQuickPasteHotkeys, value); }

        // Theme & Appearance
        private string _clipboardWallpaperPath = "";
        public string ClipboardWallpaperPath { get => _clipboardWallpaperPath; set => SetProperty(ref _clipboardWallpaperPath, value); }

        private bool _enableBlurBehind = true;
        public bool EnableBlurBehind { get => _enableBlurBehind; set => SetProperty(ref _enableBlurBehind, value); }

        private int _colorScheme = 0; // 0=Dark, 1=Light
        public int ColorScheme { get => _colorScheme; set => SetProperty(ref _colorScheme, value); }
    }

    public static class SettingsManager
    {
        public static AdvanceSettings Current { get; private set; } = new AdvanceSettings();

        private static string GetConfigPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "AdvanceClip");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }

        public static void Load()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<AdvanceSettings>(json);
                    if (settings != null) Current = settings;
                }
            }
            catch { }
            
            Current.PropertyChanged += (s, e) => Save();
            if (Current.CustomSnifferPaths != null)
            {
                Current.CustomSnifferPaths.CollectionChanged += (s, e) => Save();
            }
        }

        public static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetConfigPath(), json);
            }
            catch { }
        }
    }
}
