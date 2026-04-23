using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Persists clipboard history (text + images) to disk so items survive app restarts.
    /// Images are stored permanently in %AppData%\AdvanceClip\Images\.
    /// Metadata is serialized to %AppData%\AdvanceClip\clipboard_history.json.
    /// </summary>
    public static class ClipboardHistoryManager
    {
        private static readonly string _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip");
        private static readonly string _historyPath = Path.Combine(_appDataDir, "clipboard_history.json");
        private static readonly string _imagesDir = Path.Combine(_appDataDir, "Images");

        private static Timer? _debounceTimer;
        private static readonly object _lock = new object();

        /// <summary>
        /// Returns the permanent image storage directory, creating it if needed.
        /// </summary>
        public static string GetPersistentImageDir()
        {
            Directory.CreateDirectory(_imagesDir);
            return _imagesDir;
        }

        /// <summary>
        /// Generates a unique permanent path for a clipboard image.
        /// </summary>
        public static string GetPersistentImagePath()
        {
            Directory.CreateDirectory(_imagesDir);
            return Path.Combine(_imagesDir, $"ClipFlow_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString().Substring(0, 4)}.png");
        }

        /// <summary>
        /// Loads persisted clipboard history from disk.
        /// Returns empty list if no history exists or on error.
        /// </summary>
        public static List<ViewModels.ClipboardItem> LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyPath))
                    return new List<ViewModels.ClipboardItem>();

                var json = File.ReadAllText(_historyPath);
                var items = JsonSerializer.Deserialize<List<ViewModels.ClipboardItem>>(json);
                
                if (items == null)
                    return new List<ViewModels.ClipboardItem>();

                // Filter out items whose files no longer exist (for file-based items)
                var validItems = new List<ViewModels.ClipboardItem>();
                foreach (var item in items)
                {
                    // Text/Code/URL items are always valid (they store RawContent)
                    if (item.ItemType == ViewModels.ClipboardItemType.Text ||
                        item.ItemType == ViewModels.ClipboardItemType.Code ||
                        item.ItemType == ViewModels.ClipboardItemType.Url)
                    {
                        validItems.Add(item);
                        continue;
                    }

                    // Image items — check if the persistent image file still exists
                    if (item.ItemType == ViewModels.ClipboardItemType.Image ||
                        item.ItemType == ViewModels.ClipboardItemType.QRCode)
                    {
                        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                        {
                            validItems.Add(item);
                        }
                        continue;
                    }

                    // File-based items — keep regardless (FilePath may be on external drive etc.)
                    validItems.Add(item);
                }

                Logger.LogAction("HISTORY_LOAD", $"Loaded {validItems.Count} items from clipboard history");
                return validItems;
            }
            catch (Exception ex)
            {
                Logger.LogAction("HISTORY_LOAD_ERROR", $"Failed to load history: {ex.Message}");
                return new List<ViewModels.ClipboardItem>();
            }
        }

        /// <summary>
        /// Saves clipboard history to disk. Debounced — waits 500ms after last call to avoid disk thrashing.
        /// </summary>
        public static void SaveHistoryDebounced(ObservableCollection<ViewModels.ClipboardItem> items)
        {
            lock (_lock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(_ => SaveHistoryNow(items), null, 500, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Immediately saves clipboard history to disk.
        /// </summary>
        public static void SaveHistoryNow(ObservableCollection<ViewModels.ClipboardItem> items)
        {
            try
            {
                Directory.CreateDirectory(_appDataDir);

                // Take a snapshot to avoid collection-modified exceptions
                List<ViewModels.ClipboardItem> snapshot;
                try
                {
                    snapshot = items.ToList();
                }
                catch
                {
                    return; // Collection was being modified, skip this save
                }

                var options = new JsonSerializerOptions { WriteIndented = false };
                var json = JsonSerializer.Serialize(snapshot, options);
                
                // Write to temp file first, then atomic rename for safety
                var tempPath = _historyPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _historyPath, true);
            }
            catch (Exception ex)
            {
                Logger.LogAction("HISTORY_SAVE_ERROR", $"Failed to save history: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes the persistent image file for a clipboard item (when user deletes an item).
        /// </summary>
        public static void DeletePersistentImage(ViewModels.ClipboardItem item)
        {
            try
            {
                if (item.ItemType == ViewModels.ClipboardItemType.Image ||
                    item.ItemType == ViewModels.ClipboardItemType.QRCode)
                {
                    if (!string.IsNullOrEmpty(item.FilePath) && 
                        item.FilePath.Contains(_imagesDir) && 
                        File.Exists(item.FilePath))
                    {
                        File.Delete(item.FilePath);
                    }
                }
            }
            catch { }
        }
    }
}
