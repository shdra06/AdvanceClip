using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace AdvanceClip.ViewModels
{
    public class FlyShelfViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ClipboardItem> DroppedItems { get; } = new ObservableCollection<ClipboardItem>();
        private Stack<System.Collections.Generic.List<ClipboardItem>> _deletedItemsHistory = new Stack<System.Collections.Generic.List<ClipboardItem>>();

        /// <summary>
        /// Loads persisted clipboard history from disk and rebuilds Icon previews.
        /// Called once at app startup.
        /// </summary>
        public void LoadPersistedHistory()
        {
            var items = Classes.ClipboardHistoryManager.LoadHistory();
            foreach (var item in items)
            {
                // Rebuild BitmapImage icon from persisted FilePath
                if ((item.ItemType == ClipboardItemType.Image || item.ItemType == ClipboardItemType.QRCode)
                    && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 250;
                        bmp.UriSource = new Uri(item.FilePath);
                        bmp.EndInit();
                        bmp.Freeze();
                        item.Icon = bmp;
                    }
                    catch { }
                }
                else if (item.ItemType == ClipboardItemType.File || item.ItemType == ClipboardItemType.Document ||
                         item.ItemType == ClipboardItemType.Pdf || item.ItemType == ClipboardItemType.Archive ||
                         item.ItemType == ClipboardItemType.Video || item.ItemType == ClipboardItemType.Audio ||
                         item.ItemType == ClipboardItemType.Presentation)
                {
                    if (!string.IsNullOrEmpty(item.FilePath))
                        item.Icon = GetIcon(item.FilePath);
                }

                item.EvaluateSmartActions();
                DroppedItems.Add(item);
            }
            OnPropertyChanged(nameof(ShelfVisibility));

            // Wire up auto-save on any collection change
            DroppedItems.CollectionChanged += (s, e) =>
            {
                Classes.ClipboardHistoryManager.SaveHistoryDebounced(DroppedItems);
            };
        }

        /// <summary>
        /// Triggers a debounced save of the current clipboard history.
        /// Call after property changes on items (pin, etc.)
        /// </summary>
        private void PersistHistory()
        {
            Classes.ClipboardHistoryManager.SaveHistoryDebounced(DroppedItems);
        }

        // Pre-compiled regex patterns for text classification — avoids recompilation on every clipboard event
        private static readonly Regex _rxTerminal = new Regex(@"(PS C:\\|~\$|root@|npm run|npm install|git clone|git commit|sudo |apt-get|docker run)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxCode = new Regex(@"(#include\s|<iostream>|<stdio\.h>|std::|printf\(|public class |private void |int main\(\)|using namespace |def\s+\w+\(|import\s+(os|sys|java|React)|class\s+[A-Z]\w*|Console\.WriteLine|=>\s*\{|\{""|\[\{""|<\/?(html|div|span|script|style|body|head)|function\s+\w+\(|console\.log\(|require\()", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _rxUtmClean = new Regex(@"(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private int _currentMode = 0; // 0=Mini, 1=Medium, 2=Full
        public int CurrentMode
        {
            get => _currentMode;
            set
            {
                _currentMode = value;
                OnPropertyChanged(nameof(CurrentMode));
                OnPropertyChanged(nameof(IsMiniMode));
                OnPropertyChanged(nameof(CurrentFlyShelfMaxHeight));
                OnPropertyChanged(nameof(CurrentFlyShelfWidth));
            }
        }
        
        public bool IsMiniMode => CurrentMode == 0;
        public bool IsMediumMode => CurrentMode == 1;
        public bool IsFullMode => CurrentMode == 2;

        public int CurrentFlyShelfMaxHeight
        {
            get
            {
                if (CurrentMode == 0) return AdvanceClip.Classes.SettingsManager.Current.MiniFormHeight;
                if (CurrentMode == 1) return AdvanceClip.Classes.SettingsManager.Current.MediumFormHeight;
                return (int)SystemParameters.WorkArea.Height - 100;
            }
        }
        
        public int CurrentFlyShelfWidth
        {
            get
            {
                if (CurrentMode == 0) return AdvanceClip.Classes.SettingsManager.Current.MiniFormWidth;
                if (CurrentMode == 1) return AdvanceClip.Classes.SettingsManager.Current.MediumFormWidth;
                return 850;
            }
        }

        private bool _isSending;
        public bool IsSending { get => _isSending; set { _isSending = value; OnPropertyChanged(nameof(IsSending)); } }
        
        private string _sendingText = "Sending";
        public string SendingText { get => _sendingText; set { _sendingText = value; OnPropertyChanged(nameof(SendingText)); } }

        public ICommand ClearCommand { get; }
        public ICommand RemoveItemCommand { get; }
        public ICommand OpenItemCommand { get; }
        public ICommand ClearAllCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand TogglePinCommand { get; }
        public ICommand LaunchSandboxCommand { get; }
        public ICommand LaunchTerminalCommand { get; }
        public ICommand OpenInBrowserCommand { get; }
        public ICommand RunAdminTerminalCommand { get; }
        public ICommand ToggleGlobalFirebaseCommand { get; }
        public ICommand CompileNativeCommand { get; }
        public ICommand ConvertDocumentCommand { get; }
        public ICommand ExtractTextCommand { get; }
        public ICommand ExtractTableCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand CopyRawContentCommand { get; }
        public ICommand MergeSelectedPdfsCommand { get; }
        public ICommand OpenFileLocationCommand { get; }
        public ICommand ConvertImageToPdfCommand { get; }
        
        public AdvanceClip.Classes.NetworkSyncServer LocalServer { get; private set; }
        public AdvanceClip.Classes.DocumentSniffer Sniffer { get; private set; }
        public AdvanceClip.Classes.FirebaseListener CloudListener { get; private set; }

        public void RefreshLocalServerData()
        {
            OnPropertyChanged(nameof(LocalServer));
        }

        public FlyShelfViewModel()
        {
            ClearCommand = new RelayCommand(ClearShelf);
            RemoveItemCommand = new RelayCommand<ClipboardItem>(RemoveItem);
            OpenItemCommand = new RelayCommand<ClipboardItem>(OpenItem);
            ClearAllCommand = new RelayCommand(ClearShelf);
            UndoCommand = new RelayCommand(UndoDelete);
            TogglePinCommand = new RelayCommand<ClipboardItem>(TogglePin);
            LaunchSandboxCommand = new RelayCommand<ClipboardItem>(LaunchSandbox);
            LaunchTerminalCommand = new RelayCommand<ClipboardItem>(item => item?.RunInTerminal());
            OpenInBrowserCommand = new RelayCommand<ClipboardItem>(item => item?.OpenInBrowser());
            RunAdminTerminalCommand = new RelayCommand<ClipboardItem>(item => item?.RunAdminTerminal());
            CompileNativeCommand = new RelayCommand<ClipboardItem>(item => item?.CompileAndRunNative());
            ConvertDocumentCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertDocumentTask());
            ConvertImageToPdfCommand = new RelayCommand<ClipboardItem>(item => item?.ConvertImageToPdf());

            ExtractTextCommand = new RelayCommand<ClipboardItem>(item => item?.ExtractText());
            ExtractTableCommand = new RelayCommand<ClipboardItem>(item => item?.ExtractTable());
            SaveSettingsCommand = new RelayCommand(SaveGlobalSettings);
            ToggleGlobalFirebaseCommand = new RelayCommand(() => {
                AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync = !AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync;
            });
            CopyRawContentCommand = new RelayCommand<ClipboardItem>(item => {
                if (item == null) return;
                try
                {
                    string content = !string.IsNullOrEmpty(item.RawContent) ? item.RawContent : item.FileName;
                    if (!string.IsNullOrEmpty(content))
                        System.Windows.Clipboard.SetText(content);
                    AdvanceClip.Windows.ToastWindow.ShowToast("Copied to clipboard! 📋");
                }
                catch { }
            });
            MergeSelectedPdfsCommand = new RelayCommand(() => {
                var selected = DroppedItems.Where(i => i.IsSelected && i.ItemType == ClipboardItemType.Pdf).ToList();
                if (selected.Count > 1)
                {
                    var mergeWindow = new AdvanceClip.Windows.PdfMergeWindow(selected, this);
                    mergeWindow.Show();
                    foreach (var item in DroppedItems) item.IsSelected = false;
                }
            });
            OpenFileLocationCommand = new RelayCommand<ClipboardItem>(item => {
                if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
                
                bool exists = System.IO.File.Exists(item.FilePath) || System.IO.Directory.Exists(item.FilePath);
                if (exists)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{item.FilePath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Classes.Logger.LogAction("EXPLORER", $"Open failed: {ex.Message}");
                    }
                }
                else
                {
                    // File/folder doesn't exist — open parent folder instead
                    string dir = System.IO.Path.GetDirectoryName(item.FilePath);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                    }
                }
            });
            
            LocalServer = new AdvanceClip.Classes.NetworkSyncServer(this);
            Sniffer = new AdvanceClip.Classes.DocumentSniffer(this);
            CloudListener = new AdvanceClip.Classes.FirebaseListener(this);
            

            
            AdvanceClip.Classes.SettingsManager.Current.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.EnableLocalNetworkSync))
                {
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync) LocalServer.Start();
                    else LocalServer.Stop();
                    OnPropertyChanged(nameof(LocalServer));
                }
                else if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.EnableGlobalFirebaseSync))
                {
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                    {
                        CloudListener.StartPolling();
                    }
                    else
                    {
                        CloudListener.StopPolling();
                    }
                }
                else if (e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MiniFormWidth) ||
                         e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MiniFormHeight) ||
                         e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MediumFormWidth) ||
                         e.PropertyName == nameof(AdvanceClip.Classes.AdvanceSettings.MediumFormHeight))
                {
                    OnPropertyChanged(nameof(CurrentFlyShelfMaxHeight));
                    OnPropertyChanged(nameof(CurrentFlyShelfWidth));
                }
            };
            
            LoadPinnedItems();

            // Background Boot Optimization: Shift heavy DNS polling, port binding, and I/O sniffing completely off 
            // the main UI Constructor thread so AdvanceClip can bootstrap in under 50ms natively!
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableLocalNetworkSync) 
                    {
                        LocalServer.Start();
                    }
                    Sniffer.StartSniffing();
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                    {
                        CloudListener.StartPolling();
                    }
                });
            }, System.Windows.Threading.DispatcherPriority.Background);
        }



    

        public void RemoveItem(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                // Structural Lock: Pinned items cannot be deleted unless physically unpinned first!
                if (item.IsPinned) return; 

                _deletedItemsHistory.Push(new System.Collections.Generic.List<ClipboardItem> { item });
                DroppedItems.Remove(item);
                OnPropertyChanged(nameof(ShelfVisibility));

                // Cleanup: delete backing file (temp or persistent image)
                CleanupTempFile(item.FilePath);
                Classes.ClipboardHistoryManager.DeletePersistentImage(item);
            }
        }

        /// <summary>
        /// Deletes the backing file only if it resides inside the system temp directory.
        /// User's real files (dragged from Explorer) are never touched.
        /// </summary>
        private void CleanupTempFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                string tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
                string fileDir = Path.GetDirectoryName(filePath)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
                if (fileDir.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(filePath);
                }
            }
            catch { /* Silently ignore - file may be locked */ }
        }

        public void TogglePin(ClipboardItem item)
        {
            if (item != null && DroppedItems.Contains(item))
            {
                item.IsPinned = !item.IsPinned;
                SavePinnedItems();
                PersistHistory(); // Save pin state change
                
                // The user explicitly requested Pinned items to remain strictly invisible to the Delete feature
                // WITHOUT physically sorting them to the top of the Stack anymore.
                // We just toggle the state and let them sit natively wherever they are!
            }
        }

        public void OpenItem(ClipboardItem item)
        {
            item?.Execute();
        }

        public void ClearShelf()
        {
            var volatileItems = DroppedItems.Where(i => !i.IsPinned).ToList();
            if (volatileItems.Count > 0)
            {
                _deletedItemsHistory.Push(volatileItems);
                foreach(var vi in volatileItems) DroppedItems.Remove(vi);
                OnPropertyChanged(nameof(ShelfVisibility));
                SavePinnedItems();
            }
        }
        
        public void SortForContext(string currentContextTitle)
        {
            if (string.IsNullOrWhiteSpace(currentContextTitle)) return;
            
            var itemsList = DroppedItems.ToList();
            var sorted = itemsList.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.AssociatedContextTitle) && string.Equals(x.AssociatedContextTitle, currentContextTitle, StringComparison.OrdinalIgnoreCase))
                                  .ThenByDescending(x => x.DateCopied)
                                  .ToList();
                                  
            bool needsReorder = false;
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].IsSuggestedContext = !string.IsNullOrWhiteSpace(sorted[i].AssociatedContextTitle) && string.Equals(sorted[i].AssociatedContextTitle, currentContextTitle, StringComparison.OrdinalIgnoreCase);
                if (i < DroppedItems.Count && !object.ReferenceEquals(DroppedItems[i], sorted[i])) 
                {
                    needsReorder = true;
                }
            }

            if (needsReorder)
            {
                // AdvanceClip Phase 2.1: Use logical pointer swapping rather than destructive visual tree clears!
                // This eliminates the 1.5s visual freeze spike on large payload buffers!
                for (int i = 0; i < sorted.Count; i++)
                {
                    var actualIndex = DroppedItems.IndexOf(sorted[i]);
                    if (actualIndex != -1 && actualIndex != i)
                    {
                        DroppedItems.Move(actualIndex, i);
                    }
                }
            }
        }

        private string GetDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "AdvanceClip");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "pinned_items.json");
        }

        public void SavePinnedItems()
        {
            try
            {
                var pinned = DroppedItems.Where(i => i.IsPinned).ToList();
                File.WriteAllText(GetDbPath(), JsonSerializer.Serialize(pinned));
            }
            catch { }
        }

        public void LoadPinnedItems()
        {
            try
            {
                string path = GetDbPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var docs = JsonSerializer.Deserialize<List<ClipboardItem>>(json);
                    if (docs != null)
                    {
                        foreach (var d in docs)
                        {
                            d.IsPinned = true;
                            if (d.ItemType == ClipboardItemType.File && !string.IsNullOrEmpty(d.FilePath))
                            {
                                System.Threading.Tasks.Task.Run(() => {
                                    var icon = GetIcon(d.FilePath);
                                    if (icon != null) Application.Current.Dispatcher.Invoke(() => d.Icon = icon);
                                });
                            }
                            else if (d.ItemType == ClipboardItemType.Image && !string.IsNullOrEmpty(d.FilePath) && File.Exists(d.FilePath))
                            {
                                string imagePath = d.FilePath;
                                var capturedD = d;
                                System.Threading.Tasks.Task.Run(() => {
                                    try 
                                    {
                                        var bmp = new BitmapImage();
                                        bmp.BeginInit();
                                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                                        bmp.DecodePixelWidth = 250;
                                        bmp.UriSource = new Uri(imagePath);
                                        bmp.EndInit();
                                        bmp.Freeze();
                                        Application.Current.Dispatcher.InvokeAsync(() => capturedD.Icon = bmp);
                                    } catch { }
                                });
                            }
                            DroppedItems.Add(d);
                        }
                    }
                }
            }
            catch { }
        }
        public void UndoDelete()
        {
            if (_deletedItemsHistory.Count > 0)
            {
                var restoredItems = _deletedItemsHistory.Pop();
                foreach (var item in restoredItems)
                {
                    if (!DroppedItems.Contains(item))
                        DroppedItems.Add(item);
                }
                OnPropertyChanged(nameof(ShelfVisibility));
                SavePinnedItems();
            }
        }

        public void LaunchSandbox(ClipboardItem item)
        {
            if (item != null) item.OpenSandbox();
        }

        private string AutoFormatCode(string raw)
        {
            try
            {
                var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var formatted = new System.Text.StringBuilder();
                int indentLevel = 0;
                string tab = "    ";

                foreach (var line in lines)
                {
                    string cleanLine = line.Trim();
                    if (string.IsNullOrEmpty(cleanLine)) continue;
                    
                    if (cleanLine.StartsWith("}")) indentLevel = Math.Max(0, indentLevel - 1);
                    
                    formatted.AppendLine(string.Concat(Enumerable.Repeat(tab, indentLevel)) + cleanLine);
                    
                    if (cleanLine.EndsWith("{")) indentLevel++;
                }
                return formatted.ToString().TrimEnd();
            }
            catch { return raw; }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1}MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1}GB";
        }

        /// <summary>
        /// Unified file sync: Cloudflare tunnel → Firebase Storage → log-only fallback.
        /// Replaces 3 previously duplicated sync blocks.
        /// </summary>
        private async System.Threading.Tasks.Task SyncFileToDevicesAsync(string filePath, ClipboardItem item, long maxFirebaseBytes = 25 * 1024 * 1024, string label = "FILE")
        {
            try
            {
                long fSize = new FileInfo(filePath).Length;
                var srv = LocalServer;
                bool tunnelOk = AdvanceClip.Classes.FirebaseSyncManager.CachedTunnelVerified;

                // PRIORITY 1: Cloudflare tunnel (free, unlimited size)
                if (srv != null && !string.IsNullOrEmpty(srv.GlobalUrl) && srv.GlobalUrl.Contains("trycloudflare.com") && tunnelOk)
                {
                    string downloadUrl = $"{srv.GlobalUrl}/download?path={Uri.EscapeDataString(filePath)}";
                    AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"Sending '{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) via Cloudflare");
                    var syncItem = item.CloneForSync(downloadUrl);
                    await AdvanceClip.Classes.FirebaseSyncManager.PushToGlobalSync(syncItem);
                    Application.Current.Dispatcher.Invoke(() =>
                        AdvanceClip.Windows.ToastWindow.ShowToast($"{label} ({FormatFileSize(fSize)}) synced via Cloudflare \ud83c\udf10"));
                    return;
                }

                // PRIORITY 2: Firebase Storage (size-limited)
                if (fSize > 0 && fSize < maxFirebaseBytes)
                {
                    AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"Uploading '{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) to Firebase Storage");
                    string fbUrl = await AdvanceClip.Classes.FirebaseSyncManager.UploadFileToStorageAsync(filePath);
                    if (!string.IsNullOrEmpty(fbUrl))
                    {
                        var syncItem = item.CloneForSync(fbUrl);
                        await AdvanceClip.Classes.FirebaseSyncManager.PushToGlobalSync(syncItem);
                        Application.Current.Dispatcher.Invoke(() =>
                            AdvanceClip.Windows.ToastWindow.ShowToast($"{label} synced via Firebase \u2601\ufe0f"));
                        return;
                    }
                    AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", "Firebase Storage upload returned null");
                    return;
                }

                // Fallback: too large and no Cloudflare
                AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"'{Path.GetFileName(filePath)}' ({FormatFileSize(fSize)}) — no Cloudflare, exceeds Firebase limit");
                Application.Current.Dispatcher.Invoke(() =>
                    AdvanceClip.Windows.ToastWindow.ShowToast($"\u26a0\ufe0f {Path.GetFileName(filePath)} ({FormatFileSize(fSize)}) — needs Cloudflare tunnel"));
            }
            catch (Exception ex)
            {
                AdvanceClip.Classes.Logger.LogAction($"{label} SYNC", $"Error: {ex.Message}");
            }
        }

        private const int MAX_UNPINNED_ITEMS = 500;

        /// <summary>
        /// Prunes oldest unpinned items beyond the cap to prevent unbounded memory growth.
        /// </summary>
        private void PruneOldItems()
        {
            while (DroppedItems.Count > MAX_UNPINNED_ITEMS)
            {
                // Find the last unpinned item
                ClipboardItem? oldest = null;
                for (int i = DroppedItems.Count - 1; i >= 0; i--)
                {
                    if (!DroppedItems[i].IsPinned) { oldest = DroppedItems[i]; break; }
                }
                if (oldest != null) DroppedItems.Remove(oldest);
                else break; // all items are pinned
            }
        }

        public Visibility ShelfVisibility => DroppedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public void HandleDrop(IDataObject data, bool forceClipboardSync = false)
        {
            string[] files = null;
            
            if (data.GetDataPresent(DataFormats.FileDrop))
                files = data.GetData(DataFormats.FileDrop) as string[];
                
            if ((files == null || files.Length == 0) && data.GetDataPresent("FileNameW"))
            {
                var fName = data.GetData("FileNameW") as string[];
                if (fName != null && fName.Length > 0 && fName[0] != null) files = fName;
            }
            
            if ((files == null || files.Length == 0) && data.GetDataPresent("text/uri-list"))
            {
                try 
                {
                    string text = data.GetData("text/uri-list") as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        var parsedPaths = new System.Collections.Generic.List<string>();
                        foreach (var l in lines)
                        {
                            string p = l.Trim();
                            if (p.StartsWith("file:///")) p = new Uri(p).LocalPath;
                            if (File.Exists(p) || Directory.Exists(p)) parsedPaths.Add(p);
                        }
                        if (parsedPaths.Count > 0) files = parsedPaths.ToArray();
                    }
                } 
                catch { }
            }

            if (files != null && files.Length > 0)
            {
                foreach (string file in files)
                {
                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Extracted FileDrop payload: {file}");

                    var existingFile = DroppedItems.FirstOrDefault(i => i.FilePath == file);
                    if (existingFile != null)
                    {
                        AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Live Sync: Physical file modified, updating existing card size and pushing to top.");
                        existingFile.RefreshPhysicalStats();
                        DroppedItems.Remove(existingFile);
                        DroppedItems.Insert(0, existingFile);

                        if (forceClipboardSync)
                        {
                            Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    MainWindow._isWritingClipboard = true;
                                    var dropList = new System.Collections.Specialized.StringCollection { file };
                                    System.Windows.Clipboard.SetFileDropList(dropList);
                                }
                                catch { }
                                finally { MainWindow._isWritingClipboard = false; }
                            });
                        }
                        continue;
                    }

                    var item = new ClipboardItem(file);
                    if (item.ItemType == ClipboardItemType.Image)
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.UriSource = new Uri(file);
                                bmp.DecodePixelWidth = 250;
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                bmp.Freeze();
                                Application.Current.Dispatcher.InvokeAsync(() => item.Icon = bmp);
                            }
                            catch { } 
                        });
                    }
                    else
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            // For folders, get the shell folder icon; for files, get file icon
                            var icon = GetIcon(file);
                            if (icon != null)
                            {
                                Application.Current.Dispatcher.InvokeAsync(() => item.Icon = icon);
                            }
                        });
                    }
                    
                    if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                    {
                        var archPath = AdvanceClip.Classes.SettingsManager.Current.CustomArchiveExtractionPath;
                        if (string.IsNullOrWhiteSpace(archPath)) archPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdvanceClip", "Extracted");
                        bool isGlobalDownload = file.StartsWith(archPath, StringComparison.OrdinalIgnoreCase);

                        if (!isGlobalDownload)
                        {
                            // Determine actual sync path: for folders, wait for zip then use that
                            bool isFolder = item.ItemType == ClipboardItemType.Folder;
                            
                            if (isFolder)
                            {
                                // Folder sync — wait for zip to complete, then use unified helper
                                var capturedItem = item;
                                _ = System.Threading.Tasks.Task.Run(async () =>
                                {
                                    for (int wait = 0; wait < 120; wait++)
                                    {
                                        if (!string.IsNullOrEmpty(capturedItem.ZippedArchivePath) && File.Exists(capturedItem.ZippedArchivePath))
                                            break;
                                        await System.Threading.Tasks.Task.Delay(500);
                                    }
                                    if (string.IsNullOrEmpty(capturedItem.ZippedArchivePath) || !File.Exists(capturedItem.ZippedArchivePath))
                                    {
                                        AdvanceClip.Classes.Logger.LogAction("FOLDER SYNC", $"Zip not ready for '{capturedItem.FileName}'");
                                        return;
                                    }
                                    await SyncFileToDevicesAsync(capturedItem.ZippedArchivePath, capturedItem, label: "FOLDER");
                                });
                                goto SkipFileSync;
                            }

                            // Skip incomplete/temporary downloads
                            string fileExt = Path.GetExtension(file).ToLowerInvariant();
                            if (fileExt is ".crdownload" or ".part" or ".tmp" or ".download" or ".partial")
                            {
                                AdvanceClip.Classes.Logger.LogAction("FILE SYNC", $"Skipped incomplete download: {Path.GetFileName(file)}");
                                goto SkipFileSync;
                            }

                            // Verify file is accessible
                            try
                            {
                                using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            }
                            catch (IOException)
                            {
                                AdvanceClip.Classes.Logger.LogAction("FILE SYNC", $"Skipped locked file: {Path.GetFileName(file)}");
                                goto SkipFileSync;
                            }
                            catch { }

                            // Unified file sync
                            {
                                string capturedFile = file;
                                var capturedItem = item;
                                _ = System.Threading.Tasks.Task.Run(async () => await SyncFileToDevicesAsync(capturedFile, capturedItem, label: "FILE"));
                            }
                            SkipFileSync:;
                        }
                    }
                    
                    // Pushing natively to the top of the Stack (LIFO format)
                    DroppedItems.Insert(0, item);
                    PruneOldItems();
                    
                    if (forceClipboardSync)
                    {
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                MainWindow._isWritingClipboard = true;
                                var dropList = new System.Collections.Specialized.StringCollection();
                                dropList.Add(file);
                                System.Windows.Clipboard.SetFileDropList(dropList);
                            }
                            catch { }
                            finally { MainWindow._isWritingClipboard = false; }
                        });
                    }
                }
                OnPropertyChanged(nameof(ShelfVisibility));
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent(typeof(BitmapSource)))
            {
                BitmapSource? bmp = null;
                try { bmp = data.GetData(typeof(BitmapSource)) as BitmapSource; } catch { }
                if (bmp == null) try { bmp = data.GetData(DataFormats.Bitmap) as BitmapSource; } catch { }

                if (bmp != null)
                {
                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", "Extracted physical Bitmap image payload");
                    if (DroppedItems.Count > 0)
                    {
                        var lastItem = DroppedItems.FirstOrDefault(i => !i.IsPinned);
                        if (lastItem != null && (lastItem.ItemType == ClipboardItemType.Image || lastItem.ItemType == ClipboardItemType.QRCode) && 
                            lastItem.FormattedSize == $"{bmp.PixelWidth}x{bmp.PixelHeight}" && 
                            lastItem.FileName.StartsWith("Screenshot"))
                        {
                            if ((DateTime.Now - lastItem.DateCopied).TotalSeconds < 2.0)
                            {
                                return; 
                            }
                        }
                    }

                    var item = new ClipboardItem();
                    item.ItemType = ClipboardItemType.Image;
                    item.FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
                    item.Extension = "IMAGE";
                    item.FormattedSize = $"{bmp.PixelWidth}x{bmp.PixelHeight}";
                    
                    item.EvaluateSmartActions();
                    
                    // Standard Stack Logic (Index 0)
                    DroppedItems.Insert(0, item);
                    PruneOldItems();
                    var capturedBmp = bmp.Clone(); 
                    capturedBmp.Freeze(); 

                    System.Threading.Tasks.Task.Run(() => 
                    {
                        string tempFile = Classes.ClipboardHistoryManager.GetPersistentImagePath();
                        
                        try
                        {
                            var convertedBmp = new FormatConvertedBitmap(capturedBmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                            convertedBmp.Freeze();
                            
                            using (var fs = new FileStream(tempFile, FileMode.Create))
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(convertedBmp));
                                encoder.Save(fs);
                            }

                            Application.Current.Dispatcher.InvokeAsync(() => 
                            {
                                var bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.DecodePixelWidth = 250; // Decode at thumbnail size, not full resolution
                                bitmapImage.UriSource = new Uri(tempFile);
                                bitmapImage.EndInit();
                                bitmapImage.Freeze();
                                
                                item.Icon = bitmapImage;
                                item.FilePath = tempFile;
                                item.ScanForQRCodeAsync(tempFile);
                                OnPropertyChanged(nameof(ShelfVisibility));
                                
                                if (forceClipboardSync)
                                {
                                    try
                                    {
                                        MainWindow._isWritingClipboard = true;
                                        System.Windows.Clipboard.SetImage(bitmapImage);
                                    }
                                    catch { }
                                    finally { MainWindow._isWritingClipboard = false; }
                                }
                                // Sync image to devices via unified helper
                                if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                                {
                                    string capturedTempFile = tempFile;
                                    var capturedItem = item;
                                    _ = System.Threading.Tasks.Task.Run(async () => await SyncFileToDevicesAsync(capturedTempFile, capturedItem, maxFirebaseBytes: 5 * 1024 * 1024, label: "IMAGE"));
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            AdvanceClip.Classes.Logger.LogAction("IMAGE CORE", $"Failed to encode web palette: {ex.Message}");
                            Application.Current.Dispatcher.Invoke(() => {
                                item.ItemType = ClipboardItemType.Text;
                                item.FileName = "Image Failed to Decode!";
                                item.RawContent = "The browser exported a highly compressed or corrupted image payload that the .NET Runtime could not safely rasterize to disk.";
                                item.Extension = "ERROR";
                                OnPropertyChanged(nameof(ShelfVisibility));
                            });
                        }
                    });
                }
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.StringFormat) || data.GetDataPresent(DataFormats.Text))
            {
                string text = "";
                try { text = data.GetData(DataFormats.UnicodeText) as string ?? ""; } catch { }
                if (string.IsNullOrEmpty(text)) try { text = data.GetData(DataFormats.StringFormat) as string ?? ""; } catch { }
                if (string.IsNullOrEmpty(text)) try { text = data.GetData(DataFormats.Text) as string ?? ""; } catch { }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim().TrimEnd('\0');
                    AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Extracted string text payload length: {text.Length}");

                    // DEDUP: If ANY existing item already has this exact content, skip entirely.
                    // Prevents pinned items and recent copies from spawning duplicates.
                    var existingMatch = DroppedItems.FirstOrDefault(i => i.RawContent == text);
                    if (existingMatch != null)
                    {
                        AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Skipped — identical content already exists (dedup, pinned={existingMatch.IsPinned})");
                        return;
                    }

                    // PERF: Capture text, then offload ALL processing to background thread
                    string capturedText = text;
                    bool capturedForceSync = forceClipboardSync;
                    
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        ClipboardItem? item = null;
                    
                        try
                        {
                            string possiblePath = capturedText;
                            if (possiblePath.StartsWith("file:///"))
                            {
                                possiblePath = new Uri(possiblePath).LocalPath;
                            }
                            
                            if (File.Exists(possiblePath))
                            {
                                AdvanceClip.Classes.Logger.LogAction("DRAG IN", $"Seamlessly resolved ambiguous text format to a localized physical file: {possiblePath}");
                                item = new ClipboardItem(possiblePath);
                            }
                        }
                        catch { }

                        if (item == null)
                        {
                            item = new ClipboardItem();
                            item.RawContent = capturedText;
                            item.FormattedSize = string.Empty;
                        }

                        if (Uri.TryCreate(capturedText, UriKind.Absolute, out Uri? uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
                        {
                            string cleanUrl = _rxUtmClean.Replace(capturedText, string.Empty).TrimEnd('?', '&');
                            
                            item.RawContent = cleanUrl;
                            item.ItemType = ClipboardItemType.Url;
                            item.FileName = cleanUrl;
                            item.Extension = "LINK";
                        }
                        else
                        {
                            bool isTerminal = _rxTerminal.IsMatch(capturedText);
                            
                            bool isCode = _rxCode.IsMatch(capturedText);
                            
                            if (isTerminal || isCode)
                            {
                                item.ItemType = ClipboardItemType.Code;
                                item.RawContent = isTerminal ? capturedText : AutoFormatCode(capturedText);
                                
                                if (isTerminal) 
                                {
                                    item.Extension = "TERM";
                                }
                                else if (capturedText.Contains("std::") || capturedText.Contains("<iostream>")) 
                                {
                                    item.Extension = "C++";
                                }
                                else if (capturedText.Contains("<stdio.h>") || Regex.IsMatch(capturedText, @"\bprintf\(")) 
                                {
                                    item.Extension = "C";
                                }
                                else if (Regex.IsMatch(capturedText, @"\b(def\s+\w+\(|import\s+os|import\s+sys|print\()\b")) 
                                {
                                    item.Extension = "PYTHON";
                                }
                                else if (Regex.IsMatch(capturedText, @"(function\s+\w+\(|console\.log\(|require\(|export\s+default|module\.exports)\b")) 
                                {
                                    item.Extension = "JS";
                                }
                                else if (capturedText.Contains("public class") || capturedText.Contains("private void") || capturedText.Contains("Console.WriteLine")) 
                                {
                                    item.Extension = "C#";
                                }
                                else if (capturedText.TrimStart().StartsWith("{\"") || capturedText.TrimStart().StartsWith("[{\"")) 
                                {
                                    item.Extension = "JSON";
                                }
                                else if (Regex.IsMatch(capturedText, @"<\/?(html|div|span|body)>", RegexOptions.IgnoreCase)) 
                                {
                                    item.Extension = "HTML";
                                }
                                else 
                                {
                                    item.Extension = "CODE";
                                }
                                string shortText = capturedText.Trim();
                                item.FileName = shortText.Length > 800 ? shortText.Substring(0, 800) + "..." : shortText;
                            }
                            else
                            {
                                item.ItemType = ClipboardItemType.Text;
                                item.Extension = "TEXT";
                            }
                        }
                        
                        item.EvaluateSmartActions();
                        
                        // Sync to all devices via Firebase + Cloudflare
                        if (AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync)
                        {
                            _ = AdvanceClip.Classes.FirebaseSyncManager.PushToGlobalSync(item);
                        }

                        // Dispatch ONLY the UI mutations back to the UI thread
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var existingText = DroppedItems.FirstOrDefault(i => i.RawContent == capturedText || i.RawContent == item.RawContent);
                            if (existingText != null) DroppedItems.Remove(existingText);
                            
                            DroppedItems.Insert(0, item);
                            PruneOldItems();
                            
                            if (item.SmartActionType == "SetTimer" && System.Text.RegularExpressions.Regex.IsMatch(item.RawContent.Trim(), @"^\/\d+$"))
                            {
                                var tw = new AdvanceClip.Windows.TimerWindow(item.RawContent.Trim());
                                tw.Show();
                            }
                            
                            if (capturedForceSync)
                            {
                                try
                                {
                                    MainWindow._isWritingClipboard = true;
                                    System.Windows.Clipboard.SetText(item.RawContent);
                                }
                                catch { }
                                finally { MainWindow._isWritingClipboard = false; }
                            }
                            
                            OnPropertyChanged(nameof(ShelfVisibility));
                        });
                    });
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        };

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DestroyIcon(IntPtr hIcon);

        private BitmapImage? GetIcon(string filePath)
        {
            try
            {
                const uint SHGFI_ICON = 0x100;
                const uint SHGFI_LARGEICON = 0x0;

                SHFILEINFO shinfo = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);

                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        
                        var bitmapImage = new BitmapImage();
                        using (var memStream = new System.IO.MemoryStream())
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                            encoder.Save(memStream);
                            
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.StreamSource = memStream;
                            bitmapImage.EndInit();
                            bitmapImage.Freeze();
                        }
                        return bitmapImage;
                    }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }
            catch { }
            return null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SaveGlobalSettings()
        {
            AdvanceClip.Classes.SettingsManager.Save();
            AdvanceClip.Windows.ToastWindow.ShowToast("System Configuration Saved ✅");
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        public RelayCommand(Action<T> execute) { _execute = execute; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (parameter is T typedParameter)
            {
                _execute(typedParameter);
            }
        }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
