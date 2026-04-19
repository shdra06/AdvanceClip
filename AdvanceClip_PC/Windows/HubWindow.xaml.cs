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

namespace AdvanceClip.Windows
{
    public partial class HubWindow : MicaWindow
    {
        private DropShelfViewModel _viewModel;
        private UpdateManager _updateManager = new UpdateManager();
        private bool _updateDownloaded = false;

        public HubWindow(DropShelfViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.DroppedItems.CollectionChanged += DroppedItems_CollectionChanged;

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
                }
            });
        }

        private void DroppedItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyFilters();
            });
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                try { Clipboard.SetText(url); btn.Content = "Copied!"; System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => btn.Content = "Copy")); } catch { }
            }
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
                if (tag == "Network") RefreshDevices_Click(null, null);
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

        private void ResetDropShelfSize_Click(object sender, RoutedEventArgs e)
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

        // DropShelf +/- steppers
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

        private void PreviewDropShelfSize_Click(object sender, RoutedEventArgs e)
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
            }
            catch (Exception ex)
            {
                LogsTextBox.Text = $"Failed to parse telemetry: {ex.Message}";
            }
        }

        private string _currentFilterTag = "All";

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
                var p2pItems = new System.Collections.Generic.List<DeviceDisplayItem>();

                // Always add self to LAN
                lanItems.Add(new DeviceDisplayItem
                {
                    DeviceName = myName + " (You)",
                    DeviceType = "PC",
                    IsOnline = true,
                    ConnectionType = "Local",
                    LastSeen = "Online now"
                });

                foreach (var d in devices)
                {
                    var item = new DeviceDisplayItem
                    {
                        DeviceName = d.Name,
                        DeviceType = d.Type,
                        IsOnline = d.IsOnline,
                        LastSeen = d.IsOnline ? "Online now" : "Offline"
                    };

                    // Check LAN: same subnet
                    bool isLan = !string.IsNullOrEmpty(d.LocalIp) && !string.IsNullOrEmpty(mySubnet) && GetSubnet(d.LocalIp) == mySubnet;
                    if (isLan)
                        lanItems.Add(new DeviceDisplayItem { DeviceName = item.DeviceName, DeviceType = item.DeviceType, IsOnline = item.IsOnline, ConnectionType = "Local" });

                    // Cloud: ALL online devices are cloud-synced via Firebase
                    if (d.IsOnline)
                        cloudItems.Add(new DeviceDisplayItem { DeviceName = item.DeviceName, DeviceType = item.DeviceType, IsOnline = item.IsOnline, ConnectionType = "Cloud" });


                }

                LanDevicesPanel.ItemsSource = lanItems;
                CloudDevicesPanel.ItemsSource = cloudItems;
                P2PDevicesPanel.ItemsSource = p2pItems;

                // Show/hide empty text for each column
                LanEmptyText.Visibility = lanItems.Count <= 1 ? Visibility.Visible : Visibility.Collapsed; // 1 = just self
                CloudEmptyText.Visibility = cloudItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                P2PEmptyText.Visibility = p2pItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                NoDevicesPanel.Visibility = (lanItems.Count <= 1 && cloudItems.Count == 0 && p2pItems.Count == 0) ? Visibility.Visible : Visibility.Collapsed;

                // Also refresh groups
                RefreshGroups();
            }
            catch (Exception ex)
            {
                Logger.LogAction("DEVICES UI", $"Failed to refresh: {ex.Message}");
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

            // Default: Check for updates (auto-download triggers via UpdateCheckCompleted)
            UpdateBtn.Content = "Checking...";
            UpdateBtn.IsEnabled = false;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            ChangelogPanel.Visibility = Visibility.Collapsed;
            LatestVersionText.Text = "";

            await _updateManager.CheckForUpdateAsync();

            if (!_updateManager.IsUpdateAvailable)
            {
                UpdateBtn.IsEnabled = true;
                UpdateBtn.Content = "Check Again";
            }
        }
    } // end HubWindow class

    public class DeviceDisplayItem
    {
        public string DeviceName { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public bool IsOnline { get; set; }
        public string ConnectionType { get; set; } = "Local";
        public string LastSeen { get; set; } = "";
    }

    public class GroupDisplayItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string DeviceList { get; set; } = "";
    }
}
