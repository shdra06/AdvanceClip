using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using AdvanceClip.ViewModels;

namespace AdvanceClip.Classes
{
    public class DocumentSniffer
    {
        private List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private DropShelfViewModel _viewModel;
        private System.Collections.Concurrent.ConcurrentDictionary<string, byte> _recentlyTriggeredFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        public DocumentSniffer(DropShelfViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public void StartSniffing()
        {
            StopSniffing();

            var pathsToWatch = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent"),
                // Watch Screenshots folder for Win+PrtScn captures
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots")
            };

            // Add manual custom bounds
            pathsToWatch.AddRange(SettingsManager.Current.CustomSnifferPaths);

            foreach (var path in pathsToWatch.Distinct())
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(path);
                        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
                        watcher.Filter = "*.*";
                        watcher.Created += OnFileDetected;
                        watcher.Changed += OnFileDetected;
                        watcher.Renamed += OnFileDetected;
                        watcher.EnableRaisingEvents = true;
                        _watchers.Add(watcher);
                        
                        Logger.LogAction("SNIFFER", $"Active listening on: {path}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("SNIFFER ERROR", $"Watch failed on {path}: {ex.Message}");
                    }
                }
            }
        }

        public void StopSniffing()
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
        }

        private void OnFileDetected(object sender, FileSystemEventArgs e)
        {
            // Wrap in Task.Run to avoid async void and prevent event thread crashes
            _ = Task.Run(async () =>
            {
                try
                {
                    await OnFileDetectedCore(e);
                }
                catch (Exception ex)
                {
                    Logger.LogAction("SNIFFER ERROR", $"OnFileDetected crash: {ex.Message}");
                }
            });
        }

        private async Task OnFileDetectedCore(FileSystemEventArgs e)
        {
            string ext = Path.GetExtension(e.FullPath).ToLower();
            if (ext != ".pdf" && ext != ".docx" && ext != ".doc" && ext != ".lnk" && ext != ".png" && ext != ".jpg" && ext != ".jpeg") return;

            // For screenshots: only accept from Screenshots folder
            bool isImage = ext == ".png" || ext == ".jpg" || ext == ".jpeg";
            if (isImage)
            {
                string screenshotDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                if (!e.FullPath.StartsWith(screenshotDir, StringComparison.OrdinalIgnoreCase)) return;
            }

            string fileName = Path.GetFileName(e.FullPath);
            if (fileName.StartsWith("~$")) return;

            // Debouncing fast duplicate events from web browsers downloading chunks
            if (_recentlyTriggeredFiles.ContainsKey(e.FullPath)) return;
            
            _recentlyTriggeredFiles.TryAdd(e.FullPath, 0);
            
            // Wait for file lock release
            await Task.Delay(2000);

            string targetPath = e.FullPath;

            if (ext == ".lnk")
            {
                try
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t != null)
                    {
                        dynamic shell = Activator.CreateInstance(t);
                        var shortcut = shell.CreateShortcut(e.FullPath);
                        targetPath = shortcut.TargetPath;
                        
                        if (string.IsNullOrEmpty(targetPath)) return;
                        
                        string targetExt = Path.GetExtension(targetPath).ToLower();
                        if (targetExt != ".docx" && targetExt != ".doc" && targetExt != ".pdf") return;
                    }
                    else return;
                }
                catch { return; }
            }

            if (File.Exists(targetPath))
            {
                try
                {
                    using (var fs = File.Open(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                    
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dataObj = new System.Windows.DataObject();
                        var dropList = new System.Collections.Specialized.StringCollection { targetPath };
                        dataObj.SetFileDropList(dropList);
                        _viewModel.HandleDrop(dataObj, true);
                        
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Sniffed Document: {Path.GetFileName(targetPath)} 📄");
                    });
                }
                catch 
                {
                    _recentlyTriggeredFiles.TryRemove(e.FullPath, out _);
                }
            }

            await Task.Delay(13000);
            _recentlyTriggeredFiles.TryRemove(e.FullPath, out _);
        }
    }
}
