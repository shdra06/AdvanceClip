using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdvanceClip.Classes;
using AdvanceClip.ViewModels;
using MicaWPF.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AdvanceClip.Windows
{
    public partial class HubWindow : MicaWindow
    {
        private FlyShelfViewModel _viewModel;
        private UpdateManager _updateManager = new UpdateManager();
        private bool _updateDownloaded = false;

        public HubWindow(FlyShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.DroppedItems.CollectionChanged += DroppedItems_CollectionChanged;
            ApplyTheme();

            // Show real version from assembly
            string v = UpdateManager.CurrentVersion;
            VersionBadgeText.Text = $"v{v}";
            CurrentVersionText.Text = $"v{v}";

            // Wire up UpdateManager events
            _updateManager.StatusChanged += (msg) => Dispatcher.Invoke(() =>
            {
                UpdateStatusText.Text = msg;
                UpdateProgressPanel.Visibility = Visibility.Visible;
            });
            _updateManager.DownloadProgressChanged += (pct) => Dispatcher.Invoke(() =>
            {
                UpdatePctText.Text = $"{pct}%";
                // Animate progress bar width
                double parentWidth = UpdateProgressPanel.ActualWidth - 24; // minus padding
                UpdateProgressBar.Width = Math.Max(0, parentWidth * pct / 100.0);
            });
            _updateManager.UpdateCheckCompleted += (hasUpdate) => Dispatcher.Invoke(async () =>
            {
                if (hasUpdate)
                {
                    LatestVersionText.Text = $"→ v{_updateManager.LatestVersion} available!";
                    ChangelogText.Text = _updateManager.Changelog;
                    ChangelogPanel.Visibility = Visibility.Visible;
                    UpdateBtn.Content = "Downloading...";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Visible;

                    // Auto-download immediately
                    bool success = await _updateManager.DownloadAndApplyUpdateAsync();
                    if (success)
                    {
                        UpdateBtn.Content = "Restarting...";
                        UpdateStatusText.Text = "✅ Update downloaded! Restarting now...";
                        UpdatePctText.Text = "100%";

                        // Auto-apply after a brief moment so user sees the status
                        await Task.Delay(1500);
                        _updateManager.ApplyUpdateAndRestart();
                    }
                    else
                    {
                        UpdateBtn.Content = "Retry Download";
                        UpdateBtn.IsEnabled = true;
                    }
                }
                else
                {
                    UpdateBtn.Content = "✓ Up to Date";
                    UpdateBtn.IsEnabled = false;
                    UpdateProgressPanel.Visibility = Visibility.Collapsed;

                    // Re-enable after 3s so user can re-check for newer updates
                    await Task.Delay(3000);
                    UpdateBtn.Content = "Check Again";
                    UpdateBtn.IsEnabled = true;
                }
            });

            // No auto-update at startup — manual only via the button

            // Auto-refresh device list every 30 seconds + on initial load
            var deviceRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            deviceRefreshTimer.Tick += (s, ev) => RefreshDevices_Click(null, null);
            deviceRefreshTimer.Start();
            Loaded += (s, ev) => RefreshDevices_Click(null, null);
        }

        private void DroppedItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyFilters();
                UpdateEmptyState();
            });
        }

        private void UpdateEmptyState()
        {
            if (EmptyStatePanel != null)
            {
                EmptyStatePanel.Visibility = _viewModel.DroppedItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                try { Clipboard.SetText(url); btn.Content = "Copied!"; System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => btn.Content = "Copy")); } catch { }
            }
        }

        private void DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.DroppedItems.Count == 0) return;
            int count = _viewModel.DroppedItems.Count;
            _viewModel.ClearShelf();
            UpdateEmptyState();
            ToastWindow.ShowToast($"Cleared {count} items 🗑️");
        }

        private bool _isApplicationShuttingDown = false;

        public void ForceShutdownRelease()
        {
            _isApplicationShuttingDown = true;
            this.Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isApplicationShuttingDown)
            {
                e.Cancel = true;
                this.Hide();
            }
            base.OnClosing(e);
        }
        
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
        }


        private void Nav_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                string tag = fe.Tag as string;
                
                foreach (var item in RootNavigation.MenuItems)
                {
                    if (item is Wpf.Ui.Controls.NavigationViewItem navItem)
                    {
                        navItem.IsActive = ((navItem.Tag as string) == tag);
                    }
                }
                
                if (DashboardGrid != null) DashboardGrid.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
                if (HistoryGrid != null) HistoryGrid.Visibility = tag == "History" ? Visibility.Visible : Visibility.Collapsed;
                if (AutomationGrid != null) AutomationGrid.Visibility = tag == "Automation" ? Visibility.Visible : Visibility.Collapsed;
                if (NetworkGrid != null) NetworkGrid.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
                if (SettingsGrid != null) SettingsGrid.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
                if (LogsGrid != null) LogsGrid.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
                
                if (tag == "Logs") RefreshLogs_Click(null, null);
                if (tag == "Network")
                {
                    RefreshDevices_Click(null, null);
                    RefreshQRCode();
                    RefreshPairedDevicesList();
                    // Auto-populate server diagnostics
                    if (ServerDiagnosticsLog != null)
                    {
                        ServerDiagnosticsLog.Text = GetServerDiagnostics();
                    }
                }
            }
        }

        private void DashboardCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                foreach (var item in RootNavigation.MenuItems)
                {
                    if (item is FrameworkElement navItem && navItem.Tag as string == tag)
                    {
                        navItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
                        Nav_Click(navItem, null);
                        break;
                    }
                }
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            MessageBox.Show("Configuration updated successfully.", "AdvanceClip", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ResetClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MediumFormWidth = 400;
            SettingsManager.Current.MediumFormHeight = 650;
            SettingsManager.Save();
        }

        private void ResetFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.MiniFormWidth = 260;
            SettingsManager.Current.MiniFormHeight = 260;
            SettingsManager.Save();
        }

        // Clipboard +/- steppers
        private void ClipW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Min(500, SettingsManager.Current.MediumFormWidth + 25); }
        private void ClipW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormWidth = Math.Max(200, SettingsManager.Current.MediumFormWidth - 25); }
        private void ClipH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Min(700, SettingsManager.Current.MediumFormHeight + 25); }
        private void ClipH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MediumFormHeight = Math.Max(300, SettingsManager.Current.MediumFormHeight - 25); }

        // FlyShelf +/- steppers
        private void DropW_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Min(400, SettingsManager.Current.MiniFormWidth + 20); }
        private void DropW_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormWidth = Math.Max(180, SettingsManager.Current.MiniFormWidth - 20); }
        private void DropH_Plus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Min(350, SettingsManager.Current.MiniFormHeight + 25); }
        private void DropH_Minus(object sender, RoutedEventArgs e) { SettingsManager.Current.MiniFormHeight = Math.Max(100, SettingsManager.Current.MiniFormHeight - 25); }

        // Live Preview buttons
        private void PreviewClipboardSize_Click(object sender, RoutedEventArgs e)
        {
            // The HubWindow itself IS the clipboard preview — just flash to show effect
            this.Width = SettingsManager.Current.MediumFormWidth;
            this.Height = SettingsManager.Current.MediumFormHeight;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void PreviewFlyShelfSize_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mainWin = Application.Current.MainWindow as MainWindow;
                if (mainWin != null)
                {
                    var screen = SystemParameters.WorkArea;
                    mainWin.ShowNearPosition(screen.Width / 2, screen.Height / 2, 0, false, false);
                }
            }
            catch { }
        }

        // Smooth scrolling for clipboard ListView
        private void HubListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ListView listView)
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(listView);
                if (scrollViewer != null)
                {
                    // Scroll 60 pixels per wheel tick for smooth feel
                    double scrollAmount = e.Delta > 0 ? -60 : 60;
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + scrollAmount);
                    e.Handled = true;
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void RefreshLogs_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                // Main activity log
                string logFile = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "AdvanceClip", "Logs", "activity_log.txt");
                if (System.IO.File.Exists(logFile))
                {
                    LogsTextBox.Text = System.IO.File.ReadAllText(logFile);
                    LogsTextBox.ScrollToEnd();
                }
                else
                {
                    LogsTextBox.Text = "System Telemetry clean. No hardware logs recorded.";
                }

                // Network diagnostics log
                string netLogFile = Logger.GetNetworkLogPath();
                if (System.IO.File.Exists(netLogFile))
                {
                    NetLogsTextBox.Text = System.IO.File.ReadAllText(netLogFile);
                    NetLogsTextBox.ScrollToEnd();
                }
                else
                {
                    NetLogsTextBox.Text = "No network diagnostics recorded yet.\nClick 'Run Diagnostics' to capture a snapshot.";
                }
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = $"Failed to parse telemetry: {ex.Message}";
            }
        }

        private void CopyNetworkLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logs = Logger.GetRecentNetworkLogs(200);
                Clipboard.SetText(logs);
                ToastWindow.ShowToast("📋 Network logs copied to clipboard (last 200 lines)");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed to copy: {ex.Message}");
            }
        }

        private void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.DumpNetworkDiagnostics();
                ToastWindow.ShowToast("🔍 Network diagnostics captured!");
                // Refresh the log view after a brief delay to let the buffer flush
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    Dispatcher.Invoke(() => RefreshLogs_Click(null, null));
                });
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Diagnostics failed: {ex.Message}");
            }
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logsDir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "AdvanceClip", "Logs");
                if (!System.IO.Directory.Exists(logsDir)) System.IO.Directory.CreateDirectory(logsDir);
                System.Diagnostics.Process.Start("explorer.exe", logsDir);
            }
            catch { }
        }

        private string _currentFilterTag = "All";

        private void RestartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var vm = DataContext as AdvanceClip.ViewModels.FlyShelfViewModel;
                if (vm?.LocalServer == null) { ToastWindow.ShowToast("❌ Server instance not found"); return; }

                ServerDiagnosticsLog.Text = "⏳ Stopping server...\n";
                ServerDiagnosticsLog.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber

                vm.LocalServer.Stop();
                ServerDiagnosticsLog.Text += "✅ Server stopped.\n⏳ Starting server...\n";

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000); // Brief cooldown
                    Dispatcher.Invoke(() =>
                    {
                        vm.LocalServer.Start();
                        vm.RefreshLocalServerData();

                        // Read the BIND/PROXY/NETWORK log lines from the activity log
                        string diagnostics = GetServerDiagnostics();
                        ServerDiagnosticsLog.Text = diagnostics;
                        ServerDiagnosticsLog.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // green
                        ToastWindow.ShowToast("🔄 Server restarted — check diagnostics below");
                    });
                });
            }
            catch (Exception ex)
            {
                ServerDiagnosticsLog.Text = $"❌ Restart failed: {ex.Message}";
                ServerDiagnosticsLog.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
            }
        }

        private void CopyServerDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string diagnostics = GetServerDiagnostics();
                string systemInfo = $"=== AdvanceClip Server Diagnostics ===\n" +
                    $"PC Name: {Environment.MachineName}\n" +
                    $"OS: {Environment.OSVersion}\n" +
                    $"User: {Environment.UserName}\n" +
                    $"Is Admin: {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"======================================\n\n{diagnostics}";
                Clipboard.SetText(systemInfo);
                ToastWindow.ShowToast("📋 Server diagnostics copied — share this with the developer!");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"❌ Failed: {ex.Message}");
            }
        }

        private string GetServerDiagnostics()
        {
            try
            {
                string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "Logs", "activity_log.txt");
                if (!System.IO.File.Exists(logPath)) return "No log file found.";

                // Read last 500 lines and filter for server-related entries
                var allLines = System.IO.File.ReadAllLines(logPath);
                int startIdx = Math.Max(0, allLines.Length - 500);
                var relevantLines = new System.Collections.Generic.List<string>();
                for (int i = startIdx; i < allLines.Length; i++)
                {
                    string line = allLines[i];
                    if (line.Contains("[BIND]") || line.Contains("[NETWORK") || line.Contains("[TCP PROXY]") ||
                        line.Contains("[CLOUDFLARE]") || line.Contains("[CF_STDERR]") || line.Contains("[HEARTBEAT]") ||
                        line.Contains("[FIREBASE SYNC]") || line.Contains("[DIAGNOSTICS]") || line.Contains("[HTTP]") && line.Contains("health"))
                    {
                        relevantLines.Add(line);
                    }
                }

                if (relevantLines.Count == 0) return "No server log entries found in last 500 lines.";

                // Take last 50 relevant lines
                var output = relevantLines.Count > 50
                    ? relevantLines.GetRange(relevantLines.Count - 50, 50)
                    : relevantLines;

                return string.Join("\n", output);
            }
            catch (Exception ex)
            {
                return $"Error reading logs: {ex.Message}";
            }
        }

        private void Filter_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb && HubListView != null)
            {
                _currentFilterTag = rb.Tag as string ?? "All";
                ApplyFilters();
            }
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (SearchPlaceholderPanel != null)
                SearchPlaceholderPanel.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            
            if (HubListView != null)
            {
                ApplyFilters();
            }
        }

        private void ApplyFilters()
        {
            if (HubListView.ItemsSource == null) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(HubListView.ItemsSource);
            
            string query = SearchBox?.Text?.ToLowerInvariant() ?? "";

            view.Filter = item =>
            {
                if (item is ClipboardItem clip)
                {
                    bool passesType = true;
                    switch (_currentFilterTag)
                    {
                        case "Code": passesType = clip.ItemType == ClipboardItemType.Code; break;
                        case "Image": passesType = clip.ItemType == ClipboardItemType.Image || clip.ItemType == ClipboardItemType.QRCode; break;
                        case "Url": passesType = clip.ItemType == ClipboardItemType.Url; break;
                        case "Pdf": passesType = clip.ItemType == ClipboardItemType.Pdf; break;
                        case "Document": passesType = clip.ItemType == ClipboardItemType.Document; break;
                        case "Video": passesType = clip.ItemType == ClipboardItemType.Video; break;
                        case "Text": passesType = clip.ItemType == ClipboardItemType.Text; break;
                        case "All": passesType = true; break;
                    }

                    bool passesSearch = true;
                    if (!string.IsNullOrWhiteSpace(query))
                    {
                        passesSearch = (clip.FileName?.ToLowerInvariant().Contains(query) == true) ||
                                       (clip.RawContent?.ToLowerInvariant().Contains(query) == true) ||
                                       (clip.FormatIdentifier?.ToLowerInvariant().Contains(query) == true);
                    }

                    return passesType && passesSearch;
                }
                return false;
            };
            view.Refresh();
        }

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                _viewModel.TogglePin(item);
                e.Handled = true;
            }
        }

        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ClipboardItem item)
            {
                _viewModel.RemoveItem(item);
                e.Handled = true;
            }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool anyUnselected = _viewModel.DroppedItems.Any(i => !i.IsSelected);
            foreach (var item in _viewModel.DroppedItems)
            {
                item.IsSelected = anyUnselected; // Toggle: if any unselected, select all; otherwise deselect all
            }
            UpdateMergeButton();
        }

        private void HubListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateMergeButton();
        }

        private void UpdateMergeButton()
        {
            var selectedPdfs = _viewModel.DroppedItems.Where(i => i.IsSelected && i.ItemType == ClipboardItemType.Pdf).ToList();
            if (selectedPdfs.Count > 1)
            {
                MergeSelectedPdfsText.Text = $"Merge {selectedPdfs.Count} PDFs";
                MergeSelectedPdfsBtn.Visibility = Visibility.Visible;
            }
            else
            {
                MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedPdfs = _viewModel.DroppedItems.Where(i => i.IsSelected && i.ItemType == ClipboardItemType.Pdf).ToList();

            if (selectedPdfs.Count > 1)
            {
                var mergeWindow = new AdvanceClip.Windows.PdfMergeWindow(selectedPdfs, _viewModel);
                mergeWindow.Show();
                this.Hide();
                foreach (var item in _viewModel.DroppedItems) item.IsSelected = false;
            }
        }

        private void AddSnifferPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Folder to Auto-Sniff for PDF / Word Documents",
                Multiselect = false
            };
            
            if (dialog.ShowDialog() == true)
            {
                string path = dialog.FolderName;
                if (!SettingsManager.Current.CustomSnifferPaths.Contains(path))
                {
                    SettingsManager.Current.CustomSnifferPaths.Add(path);
                    SettingsManager.Save();
                    
                    if (_viewModel.Sniffer != null)
                    {
                        _viewModel.Sniffer.StopSniffing();
                        _viewModel.Sniffer.StartSniffing();
                    }
                }
            }
        }

        private void ClearSnifferPaths_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.CustomSnifferPaths.Clear();
            SettingsManager.Save();
            
            if (_viewModel.Sniffer != null)
            {
                _viewModel.Sniffer.StopSniffing();
                _viewModel.Sniffer.StartSniffing();
            }
        }

        private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var devices = await FirebaseSyncManager.GetActiveDevices();
                string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;

                // Get this PC's local IP for subnet comparison
                string myLocalIp = "";
                try
                {
                    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0);
                    socket.Connect("8.8.8.8", 65530);
                    myLocalIp = (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? "";
                }
                catch { }
                
                string mySubnet = GetSubnet(myLocalIp); // e.g., "192.168.1"


                var lanItems = new System.Collections.Generic.List<DeviceDisplayItem>();
                var cloudItems = new System.Collections.Generic.List<DeviceDisplayItem>();

                // Get this PC's own URLs for the self entry
                string myLocalUrl = _viewModel.LocalServer?.ServerUrl ?? "";
                string myGlobalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";

                // Always add self to LAN
                lanItems.Add(new DeviceDisplayItem
                {
                    DeviceName = myName + " (You)",
                    DeviceType = "PC",
                    IsOnline = true,
                    ConnectionType = "Local",
                    LastSeen = "Online now",
                    LocalIp = myLocalUrl,
                    GlobalUrl = myGlobalUrl
                });

                foreach (var d in devices)
                {
                    // Check LAN: actually try to reach the device's local IP (subnet matching is unreliable)
                    bool isLan = false;
                    if (!string.IsNullOrEmpty(d.LocalIp) && d.LocalIp.StartsWith("http"))
                    {
                        try
                        {
                            using var pingClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
                            var resp = await pingClient.GetAsync(d.LocalIp + "/ping");
                            isLan = resp.IsSuccessStatusCode;
                        }
                        catch { isLan = false; }
                    }

                    if (isLan)
                        lanItems.Add(new DeviceDisplayItem { DeviceName = d.Name, DeviceType = d.Type, IsOnline = d.IsOnline, ConnectionType = "Local", LocalIp = d.LocalIp, GlobalUrl = d.GlobalUrl });

                    // Cloud: ALL online devices are cloud-synced via Firebase
                    if (d.IsOnline)
                        cloudItems.Add(new DeviceDisplayItem { DeviceName = d.Name, DeviceType = d.Type, IsOnline = d.IsOnline, ConnectionType = "Cloud", LocalIp = d.LocalIp, GlobalUrl = d.GlobalUrl });
                }

                LanDevicesPanel.ItemsSource = lanItems;
                CloudDevicesPanel.ItemsSource = cloudItems;

                // Show/hide empty text for each column
                LanEmptyText.Visibility = lanItems.Count <= 1 ? Visibility.Visible : Visibility.Collapsed; // 1 = just self
                CloudEmptyText.Visibility = cloudItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                NoDevicesPanel.Visibility = (lanItems.Count <= 1 && cloudItems.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

                // Also refresh groups
                RefreshGroups();
            }
            catch (Exception ex)
            {
                Logger.LogAction("DEVICES UI", $"Failed to refresh: {ex.Message}");
            }
        }

        private void DeviceInfo_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DeviceDisplayItem device)
            {
                string info = $"Device: {device.DeviceName}\n" +
                              $"Type: {device.DeviceType}\n" +
                              $"Status: {(device.IsOnline ? "Online" : "Offline")}\n";

                if (!string.IsNullOrEmpty(device.LocalIp))
                    info += $"\nLocal URL: {device.LocalIp}";
                if (!string.IsNullOrEmpty(device.GlobalUrl))
                    info += $"\nCloudflare URL: {device.GlobalUrl}";

                if (string.IsNullOrEmpty(device.LocalIp) && string.IsNullOrEmpty(device.GlobalUrl))
                    info += "\nNo connection URLs available.";

                // Copy to clipboard on right-click for convenience
                string copyUrl = !string.IsNullOrEmpty(device.GlobalUrl) ? device.GlobalUrl : device.LocalIp;
                if (!string.IsNullOrEmpty(copyUrl))
                {
                    try { Clipboard.SetText(copyUrl); info += "\n\n✅ URL copied to clipboard!"; } catch { }
                }

                MessageBox.Show(info, $"Device Info — {device.DeviceName}", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Extracts the subnet prefix from an IP address (first 3 octets): "192.168.1.106" → "192.168.1"
        /// </summary>
        private static string GetSubnet(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            // Strip port and protocol
            ip = ip.Replace("http://", "").Replace("https://", "");
            int colonIdx = ip.IndexOf(':');
            if (colonIdx > 0) ip = ip.Substring(0, colonIdx);
            
            var parts = ip.Split('.');
            if (parts.Length >= 3) return $"{parts[0]}.{parts[1]}.{parts[2]}";
            return "";
        }

        // ═══ Device Groups (Firebase-synced) ═══

        private async void RefreshGroups()
        {
            try
            {
                var groups = await FirebaseSyncManager.GetDeviceGroups();
                var displayItems = groups.Select(g => new GroupDisplayItem
                {
                    Id = g.Id,
                    Name = g.Name,
                    DeviceList = string.Join(", ", g.DeviceNames ?? new List<string>())
                }).ToList();
                
                GroupsPanel.ItemsSource = displayItems;
                NoGroupsText.Visibility = displayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.LogAction("GROUPS UI", $"Failed to refresh groups: {ex.Message}");
            }
        }

        private async void CreateGroupBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = ShowInputDialog("Enter group name:", "Create Device Group", "");
                if (string.IsNullOrWhiteSpace(name)) return;

                var devices = await FirebaseSyncManager.GetActiveDevices();
                var deviceNames = devices.Where(d => d.IsOnline).Select(d => d.Name).ToList();
                string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                if (!deviceNames.Contains(myName)) deviceNames.Insert(0, myName);

                var prompt = "Available devices (enter numbers separated by commas):\n";
                for (int i = 0; i < deviceNames.Count; i++)
                    prompt += $"  {i + 1}. {deviceNames[i]}\n";

                var input = ShowInputDialog(prompt, "Select Devices for Group", string.Join(",", Enumerable.Range(1, deviceNames.Count)));
                if (string.IsNullOrWhiteSpace(input)) return;

                var selected = new List<string>();
                foreach (var numStr in input.Split(','))
                {
                    if (int.TryParse(numStr.Trim(), out int idx) && idx >= 1 && idx <= deviceNames.Count)
                        selected.Add(deviceNames[idx - 1]);
                }
                if (selected.Count == 0) { MessageBox.Show("No devices selected."); return; }

                var groupId = $"grp_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                await FirebaseSyncManager.SaveDeviceGroup(groupId, name.Trim(), selected);
                RefreshGroups();
            }
            catch (Exception ex) { Logger.LogAction("GROUPS UI", $"Create error: {ex.Message}"); }
        }

        private async void EditGroupBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.Button btn && btn.Tag is string groupId)
                {
                    var groups = await FirebaseSyncManager.GetDeviceGroups();
                    var group = groups.FirstOrDefault(g => g.Id == groupId);
                    if (group == null) return;

                    var name = ShowInputDialog("Edit group name:", "Edit Group", group.Name);
                    if (string.IsNullOrWhiteSpace(name)) return;

                    var devices = await FirebaseSyncManager.GetActiveDevices();
                    var deviceNames = devices.Where(d => d.IsOnline).Select(d => d.Name).ToList();
                    string myName = SettingsManager.Current.DeviceName ?? Environment.MachineName;
                    if (!deviceNames.Contains(myName)) deviceNames.Insert(0, myName);
                    foreach (var dn in group.DeviceNames ?? new List<string>())
                        if (!deviceNames.Contains(dn)) deviceNames.Add(dn);

                    var prompt = "Select devices (enter numbers, comma-separated):\n";
                    var preSelected = new List<int>();
                    for (int i = 0; i < deviceNames.Count; i++)
                    {
                        bool inGroup = (group.DeviceNames ?? new List<string>()).Contains(deviceNames[i]);
                        prompt += $"  {i + 1}. {deviceNames[i]}{(inGroup ? " ★" : "")}\n";
                        if (inGroup) preSelected.Add(i + 1);
                    }

                    var input = ShowInputDialog(prompt, "Edit Devices", string.Join(",", preSelected));
                    if (string.IsNullOrWhiteSpace(input)) return;

                    var selected = new List<string>();
                    foreach (var numStr in input.Split(','))
                    {
                        if (int.TryParse(numStr.Trim(), out int idx) && idx >= 1 && idx <= deviceNames.Count)
                            selected.Add(deviceNames[idx - 1]);
                    }

                    await FirebaseSyncManager.SaveDeviceGroup(groupId, name.Trim(), selected);
                    RefreshGroups();
                }
            }
            catch (Exception ex) { Logger.LogAction("GROUPS UI", $"Edit error: {ex.Message}"); }
        }

        private async void DeleteGroupBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string groupId)
            {
                var result = MessageBox.Show("Delete this group?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;

                await FirebaseSyncManager.DeleteDeviceGroup(groupId);
                RefreshGroups();
            }
        }

        /// <summary>
        /// Pure WPF input dialog — no System.Windows.Forms dependency.
        /// </summary>
        private static string ShowInputDialog(string message, string title, string defaultValue)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 420, Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(26, 31, 46))
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var input = new System.Windows.Controls.TextBox
            {
                Text = defaultValue,
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(15, 17, 24)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 47, 58)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new System.Windows.Controls.Button { Content = "OK", Width = 80, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Padding = new Thickness(0, 6, 0, 6), IsCancel = true };

            string result = null;
            okBtn.Click += (s, ev) => { result = input.Text; dlg.Close(); };
            cancelBtn.Click += (s, ev) => { dlg.Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            sp.Children.Add(tb);
            sp.Children.Add(input);
            sp.Children.Add(btnPanel);
            dlg.Content = sp;
            dlg.ShowDialog();
            return result;
        }

        private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            string btnContent = UpdateBtn.Content?.ToString() ?? "";

            if (btnContent.Contains("Restart"))
            {
                _updateManager.ApplyUpdateAndRestart();
                return;
            }

            if (btnContent.Contains("Retry"))
            {
                // Retry: re-download + auto-apply
                UpdateBtn.IsEnabled = false;
                UpdateBtn.Content = "Downloading...";
                UpdateProgressPanel.Visibility = Visibility.Visible;

                bool success = await _updateManager.DownloadAndApplyUpdateAsync();
                if (success)
                {
                    UpdateBtn.Content = "Restarting...";
                    UpdateStatusText.Text = "✅ Update downloaded! Restarting now...";
                    await Task.Delay(1500);
                    _updateManager.ApplyUpdateAndRestart();
                }
                else
                {
                    UpdateBtn.Content = "Retry Download";
                    UpdateBtn.IsEnabled = true;
                }
                return;
            }

            // Default: Check for updates (UpdateCheckCompleted event handles the UI)
            UpdateBtn.Content = "Checking...";
            UpdateBtn.IsEnabled = false;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            ChangelogPanel.Visibility = Visibility.Collapsed;
            LatestVersionText.Text = "";

            await _updateManager.CheckForUpdateAsync();
            // UpdateCheckCompleted event handler above will update the button
        }
    } // end HubWindow class

    public class DeviceDisplayItem
    {
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public bool IsOnline { get; set; }
        public string ConnectionType { get; set; } = "Local";
        public string LastSeen { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public string GlobalUrl { get; set; } = "";
        public string ConnectionInfo => !string.IsNullOrEmpty(GlobalUrl) ? "🌐 Cloudflare Active" : !string.IsNullOrEmpty(LocalIp) ? "📡 LAN" : "";
    }

    public partial class HubWindow
    {
        // ═══ Theme & Appearance Handlers ═══

        private void ApplyTheme()
        {
            try
            {
                // Wallpaper preview
                string wallpaperPath = SettingsManager.Current.ClipboardWallpaperPath;
                if (!string.IsNullOrEmpty(wallpaperPath) && System.IO.File.Exists(wallpaperPath))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(wallpaperPath, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 400;
                    bmp.EndInit();
                    bmp.Freeze();
                    WallpaperPreviewImg.Source = bmp;
                    NoWallpaperText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    WallpaperPreviewImg.Source = null;
                    NoWallpaperText.Visibility = Visibility.Visible;
                }

                // Blur
                if (SettingsManager.Current.EnableBlurBehind)
                    this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.Mica;
                else
                    this.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;

                // Color scheme — swap theme dictionaries at runtime
                string targetTheme = SettingsManager.Current.ColorScheme == 1 ? "Light" : "Dark";
                try
                {
                    var mergedDicts = Application.Current.Resources.MergedDictionaries;
                    // Find and replace the WPF-UI ThemeDictionary
                    for (int i = 0; i < mergedDicts.Count; i++)
                    {
                        var dict = mergedDicts[i];
                        if (dict is Wpf.Ui.Markup.ThemesDictionary td)
                        {
                            td.Theme = targetTheme == "Light" ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark;
                        }
                        else if (dict is MicaWPF.Styles.ThemeDictionary md)
                        {
                            md.Theme = targetTheme == "Light" ? MicaWPF.Core.Enums.WindowsTheme.Light : MicaWPF.Core.Enums.WindowsTheme.Dark;
                        }
                    }
                }
                catch { /* Theme switching may not be supported on all versions */ }
            }
            catch (Exception ex)
            {
                Logger.LogAction("THEME", $"Apply failed: {ex.Message}");
            }
        }

        private void ChooseWallpaper_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose Clipboard Wallpaper",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                SettingsManager.Current.ClipboardWallpaperPath = dialog.FileName;
                SettingsManager.Save();
                ApplyTheme();
            }
        }

        private void RemoveWallpaper_Click(object sender, RoutedEventArgs e)
        {
            SettingsManager.Current.ClipboardWallpaperPath = "";
            SettingsManager.Save();
            ApplyTheme();
        }

        private void BlurToggle_Changed(object sender, RoutedEventArgs e)
        {
            SettingsManager.Save();
            ApplyTheme();
        }

        private void ColorScheme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SettingsManager.Save();
            ApplyTheme();
        }

        // ═══ QR Code Pairing Handlers ═══

        private void RefreshQRCode()
        {
            try
            {
                if (PairingQRImage == null) return;
                string localUrl = _viewModel.LocalServer?.DisplayUrl ?? "";
                string globalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";
                string pin = SettingsManager.Current.WebClientPinToken;

                var qr = DevicePairingManager.GenerateQRCode(localUrl, globalUrl, pin, 250);
                if (qr != null)
                {
                    PairingQRImage.Source = qr;
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("QR", $"Refresh failed: {ex.Message}");
            }
        }

        private void RefreshPairedDevicesList()
        {
            try
            {
                var devices = DevicePairingManager.GetPairedDevices();
                PairedDevicesPanel.ItemsSource = devices;
                NoPairedDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.LogAction("QR", $"Refresh paired list failed: {ex.Message}");
            }
        }

        private void RegenerateQR_Click(object sender, RoutedEventArgs e)
        {
            DevicePairingManager.RegeneratePairingKey();
            RefreshQRCode();
            Windows.ToastWindow.ShowToast("New QR code generated! ✅");
        }

        private void CopyPairingInfo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localUrl = _viewModel.LocalServer?.DisplayUrl ?? "";
                string globalUrl = _viewModel.LocalServer?.GlobalUrl ?? "";
                string pin = SettingsManager.Current.WebClientPinToken;
                string payload = DevicePairingManager.BuildQRPayload(localUrl, globalUrl, pin);
                System.Windows.Clipboard.SetText(payload);
                Windows.ToastWindow.ShowToast("Pairing info copied! 📋");
            }
            catch { }
        }

        private void RemovePairedDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string deviceId)
            {
                DevicePairingManager.RemoveDevice(deviceId);
                RefreshPairedDevicesList();
                Windows.ToastWindow.ShowToast("Device removed ✕");
            }
        }

        private async void GeneratePairingCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PairingCodeDisplay.Text = "...";
                string code = await DevicePairingManager.PublishPairingCode();
                PairingCodeDisplay.Text = code;
                Windows.ToastWindow.ShowToast($"Code generated: {code} (expires in 5 min) 🔑");
            }
            catch (Exception ex)
            {
                PairingCodeDisplay.Text = "ERROR";
                Logger.LogAction("PAIR CODE", $"Generate failed: {ex.Message}");
            }
        }

        private async void ConnectByCode_Click(object sender, RoutedEventArgs e)
        {
            string code = RemoteCodeInput?.Text?.Trim().ToUpper() ?? "";
            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                Windows.ToastWindow.ShowToast("⚠️ Enter a 6-character code");
                return;
            }

            Windows.ToastWindow.ShowToast($"Looking up {code}...");

            try
            {
                var (success, deviceName) = await DevicePairingManager.ConnectByCode(code);
                if (success)
                {
                    Windows.ToastWindow.ShowToast($"✅ Paired with {deviceName}!");
                    RefreshPairedDevicesList();
                    RemoteCodeInput.Text = "";
                }
                else if (!string.IsNullOrEmpty(deviceName))
                {
                    Windows.ToastWindow.ShowToast($"⚠️ Found {deviceName} but couldn't connect — make sure it's online");
                }
                else
                {
                    Windows.ToastWindow.ShowToast("❌ Code not found or expired");
                }
            }
            catch (Exception ex)
            {
                Windows.ToastWindow.ShowToast($"❌ Connection failed: {ex.Message}");
                Logger.LogAction("PAIR CODE", $"ConnectByCode UI error: {ex.Message}");
            }
        }

        // ═══ Color Copy Handlers ═══

        private void CopyColorHex_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                System.Windows.Clipboard.SetText(Classes.ColorHelper.ToHex(item.ColorR, item.ColorG, item.ColorB));
                Windows.ToastWindow.ShowToast($"Hex copied: {item.DetectedColor} 🎨");
            }
        }

        private void CopyColorRgb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                string rgb = Classes.ColorHelper.ToRgb(item.ColorR, item.ColorG, item.ColorB);
                System.Windows.Clipboard.SetText(rgb);
                Windows.ToastWindow.ShowToast($"RGB copied: {rgb} 🎨");
            }
        }

        private void CopyColorHsl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ClipboardItem item && item.HasDetectedColor)
            {
                string hsl = Classes.ColorHelper.ToHsl(item.ColorR, item.ColorG, item.ColorB);
                System.Windows.Clipboard.SetText(hsl);
                Windows.ToastWindow.ShowToast($"HSL copied: {hsl} 🎨");
            }
        }
    }

    public class GroupDisplayItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DeviceList { get; set; } = "";
    }
}
