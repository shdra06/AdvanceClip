using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    public class NetworkLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
        
        private string _colorHex = "#9CA3AF";
        public string ColorHex 
        { 
            get => _colorHex; 
            set 
            { 
                _colorHex = value; 
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                brush.Freeze(); // Makes it thread-safe for cross-thread WPF binding
                ColorBrush = brush;
            } 
        }
        
        private static readonly SolidColorBrush _defaultBrush;
        static NetworkLogEntry()
        {
            _defaultBrush = new SolidColorBrush(Colors.Gray);
            _defaultBrush.Freeze();
        }
        public SolidColorBrush ColorBrush { get; private set; } = _defaultBrush;

        public string Display => $"[{Timestamp:HH:mm:ss.fff}] [{Category}] {Message}";
    }

    public class NetworkActivityLog : INotifyPropertyChanged
    {
        public static NetworkActivityLog Instance { get; } = new();
        
        private const int MAX_ENTRIES = 500;
        
        public ObservableCollection<NetworkLogEntry> Entries { get; } = new();

        private int _httpCount;
        public int HttpRequestCount { get => _httpCount; set { _httpCount = value; OnPropertyChanged(); } }

        private int _downloadCount;
        public int DownloadCount { get => _downloadCount; set { _downloadCount = value; OnPropertyChanged(); } }

        private int _errorCount;
        public int ErrorCount { get => _errorCount; set { _errorCount = value; OnPropertyChanged(); } }

        private string _serverStatus = "Offline";
        public string ServerStatus { get => _serverStatus; set { _serverStatus = value; OnPropertyChanged(); } }

        private string _cloudflareStatus = "Offline";
        public string CloudflareStatus { get => _cloudflareStatus; set { _cloudflareStatus = value; OnPropertyChanged(); } }

        private string _lastActivity = "—";
        public string LastActivity { get => _lastActivity; set { _lastActivity = value; OnPropertyChanged(); } }

        public void Log(string category, string message, string color = null)
        {
            var entry = new NetworkLogEntry
            {
                Timestamp = DateTime.Now,
                Category = category,
                Message = message,
                ColorHex = color ?? GetColorForCategory(category)
            };

            // Update counters
            if (category.Contains("HTTP")) HttpRequestCount++;
            if (category.Contains("DOWNLOAD")) DownloadCount++;
            if (category.Contains("ERROR") || category.Contains("FAULT")) ErrorCount++;
            LastActivity = $"{category}: {(message.Length > 60 ? message.Substring(0, 60) + "..." : message)}";

            try
            {
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    Entries.Insert(0, entry);
                    while (Entries.Count > MAX_ENTRIES)
                        Entries.RemoveAt(Entries.Count - 1);
                });
            }
            catch { /* App shutting down */ }
        }

        private static string GetColorForCategory(string cat)
        {
            if (cat.Contains("ERROR") || cat.Contains("FAULT")) return "#EF4444";
            if (cat.Contains("HTTP")) return "#60A5FA";
            if (cat.Contains("DOWNLOAD")) return "#34D399";
            if (cat.Contains("CLOUDFLARE") || cat.Contains("CF_")) return "#F59E0B";
            if (cat.Contains("FIREBASE")) return "#F97316";
            if (cat.Contains("WEBRTC") || cat.Contains("P2P")) return "#06B6D4";
            if (cat.Contains("BIND") || cat.Contains("SERVER") || cat.Contains("LISTENER")) return "#8B5CF6";
            if (cat.Contains("HTML")) return "#A78BFA";
            return "#9CA3AF";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            try
            {
                Application.Current?.Dispatcher?.InvokeAsync(() =>
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
            }
            catch { }
        }
    }
}
