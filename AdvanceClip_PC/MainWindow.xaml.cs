using AdvanceClip.ViewModels;
using MicaWPF.Controls;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace AdvanceClip
{
    public partial class MainWindow : MicaWindow
    {
        private Point _dragStartPoint;
        private readonly DropShelfViewModel _viewModel;
        private int _spawnToken = 0;
        private bool _isDragHovering = false;
        private bool _didDragOut = false;
        private double _lockedBottomEdge = 0;
        private bool _isEdgeLocked = false;
        private Windows.TaskbarWindow? _taskbarWidget;
        private System.Windows.Threading.DispatcherTimer? _clipboardDebounceTimer;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int VK_CONTROL = 0x11;
        private const int VK_V = 0x56;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const int WM_HOTKEY = 0x0312;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, GetWindowLong(helper.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        }

        public MainWindow()
        {
            var vm = new DropShelfViewModel();
            this.DataContext = vm;
            _viewModel = vm;
            InitializeComponent();

            this.SizeChanged += (s, e) =>
            {
                if (_isEdgeLocked && e.HeightChanged && this.ActualHeight > 0)
                {
                    this.Top = _lockedBottomEdge - this.ActualHeight;
                    
                    var workArea = SystemParameters.WorkArea;
                    if (this.Top < workArea.Top)
                    {
                        this.Top = workArea.Top + 16;
                    }
                }
            };

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DropShelfViewModel.CurrentDropShelfMaxHeight))
                {
                    this.MaxHeight = _viewModel.CurrentDropShelfMaxHeight;
                    this.UpdateLayout();
                    
                    if (_isEdgeLocked && this.ActualHeight > 0)
                    {
                        this.Top = _lockedBottomEdge - this.ActualHeight;
                        var workArea = SystemParameters.WorkArea;
                        if (this.Top < workArea.Top) this.Top = workArea.Top + 16;
                    }
                }
            };
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        private void MicaWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var interopHelper = new WindowInteropHelper(this);
            interopHelper.EnsureHandle(); // Force HWND even if Window is hidden instantly
            var handle = interopHelper.Handle;
            
            if (handle != IntPtr.Zero)
            {
                HwndSource.FromHwnd(handle)?.AddHook(HwndHook);
                AddClipboardFormatListener(handle);
                RegisterHotKey(handle, HOTKEY_ID, MOD_ALT, 0x43); // Alt+C

                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
                        {
                            int colorNone = DWMWA_COLOR_NONE;
                            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
                        }
                    });
                });
            }

            // Launch the taskbar-embedded widget
            try
            {
                _taskbarWidget = new Windows.TaskbarWindow();
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("WIDGET_FAIL", $"Failed to create taskbar widget: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    RemoveClipboardFormatListener(handle);
                    UnregisterHotKey(handle, HOTKEY_ID);
                    HwndSource.FromHwnd(handle)?.RemoveHook(HwndHook);
                }
            }
            catch { /* Window already destroyed — nothing to clean up */ }
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                var workArea = SystemParameters.WorkArea;
                ShowNearPosition(workArea.Left + workArea.Width - 380, workArea.Top + workArea.Height, 1, true, true);
                handled = true;
            }
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                // GUARD: Skip clipboard events triggered by our own writes
                if (_isWritingClipboard)
                {
                    handled = true;
                    return IntPtr.Zero;
                }

                // DEBOUNCE: Reuse a single timer to avoid GC pressure.
                // 100ms collapses burst events while staying responsive.
                if (_clipboardDebounceTimer == null)
                {
                    _clipboardDebounceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(100)
                    };
                    _clipboardDebounceTimer.Tick += (s, ev) =>
                    {
                        _clipboardDebounceTimer.Stop();
                        try
                        {
                            IDataObject data = Clipboard.GetDataObject();
                            if (data != null)
                            {
                                var vm = (DropShelfViewModel)DataContext;
                                vm.HandleDrop(data, false);
                            }
                        }
                        catch { }
                    };
                }
                _clipboardDebounceTimer.Stop();
                _clipboardDebounceTimer.Start();
                
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            this.Opacity = 1.0;
            int colorNone = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<int>());
        }

        private bool _isPersistentMode = false;
        private DateTime _spawnTime = DateTime.MinValue;
        private IntPtr _previousForegroundWindow = IntPtr.Zero;
        internal static bool _isWritingClipboard = false;

        private IntPtr GetTargetForegroundWindow()
        {
            IntPtr ptr = GetForegroundWindow();
            
            var sb = new System.Text.StringBuilder(256);
            GetClassName(ptr, sb, 256);
            string className = sb.ToString();

            if (className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" || className == "WorkerW" || className == "Progman")
            {
                IntPtr target = IntPtr.Zero;
                uint currentProcessId = GetCurrentProcessId();
                EnumWindows((wnd, param) =>
                {
                    if (IsWindowVisible(wnd))
                    {
                        uint processId;
                        GetWindowThreadProcessId(wnd, out processId);
                        if (processId != currentProcessId)
                        {
                            GetClassName(wnd, sb, 256);
                            string cName = sb.ToString();
                            if (cName != "Shell_TrayWnd" && cName != "Shell_SecondaryTrayWnd" && cName != "WorkerW" && cName != "Progman")
                            {
                                GetWindowText(wnd, sb, 256);
                                if (sb.Length > 0 && sb.ToString() != "AdvanceClip" && sb.ToString() != "DropShelf" && sb.ToString() != "Program Manager")
                                {
                                    target = wnd;
                                    return false; 
                                }
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);
                if (target != IntPtr.Zero) return target;
            }
            
            return ptr;
        }

        public void ShowNearPosition(double targetX, double targetY, int mode = 0, bool isPersistent = false, bool stealFocus = true)
        {
            _previousForegroundWindow = GetTargetForegroundWindow();
            
            // AdvanceClip Phase 2: Live AI Memory Association
            if (_previousForegroundWindow != IntPtr.Zero)
            {
                var sbTitle = new System.Text.StringBuilder(256);
                GetWindowText(_previousForegroundWindow, sbTitle, 256);
                string currentTitle = sbTitle.ToString();
                
                // Fire sorting asynchronously AFTER the UI finishes its layout!
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() => _viewModel.SortForContext(currentTitle));
                });
            }

            _spawnTime = DateTime.Now;
            _isPersistentMode = isPersistent;
            if (this.IsVisible)
            {
                this.Hide(); 
            }

            this.ShowInTaskbar = true;
            this.ShowInTaskbar = false;

            _viewModel.CurrentMode = mode;
            this.MaxHeight = _viewModel.CurrentDropShelfMaxHeight;
            this.Width = _viewModel.CurrentDropShelfWidth;

            var workArea = SystemParameters.WorkArea;
            double safeWidth = double.IsNaN(this.Width) ? 360 : this.Width;
            if (safeWidth <= 0) safeWidth = 320;

            double rawX = targetX - (safeWidth / 2);
            if (rawX + safeWidth > workArea.Left + workArea.Width - 16)
                rawX = workArea.Left + workArea.Width - safeWidth - 16;
            if (rawX < workArea.Left + 16)
                rawX = workArea.Left + 16;

            double rawY = targetY - 16;
            if (rawY > workArea.Top + workArea.Height - 16)
                rawY = workArea.Top + workArea.Height - 16;
            
            _lockedBottomEdge = rawY;
            _isEdgeLocked = true;

            this.Left = rawX;
            // Best-guess initial bound from user settings before ActualHeight resolves
            double initialSafeHeight = double.IsNaN(this.Height) ? AdvanceClip.Classes.SettingsManager.Current.MiniFormHeight : this.Height;
            this.Top = _lockedBottomEdge - initialSafeHeight - 20;

            if (stealFocus)
            {
                this.ShowActivated = true;
                this.Show();
                this.Activate();
            }
            else
            {
                this.ShowActivated = false;
                this.Show();
            }

            this.UpdateLayout();
            if (this.ActualHeight > 0)
            {
                // Push it 20px dynamically upward to completely avoid taskbar z-index clipping!
                this.Top = _lockedBottomEdge - this.ActualHeight - 20; 
                
                if (this.Top < workArea.Top)
                {
                    this.Top = workArea.Top + 20;
                }
            }

            int currentToken = ++_spawnToken;
        }

        private static bool _isInternalDragSource = false;

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            _spawnToken++; 

            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _viewModel.HandleDrop(e.Data, true);
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            _spawnToken++; 

            if (_isInternalDragSource)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            _viewModel.HandleDrop(e.Data, true);
            e.Handled = true;
        }

    

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            _isDragHovering = true;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || 
                e.Data.GetDataPresent("FileNameW") ||
                e.Data.GetDataPresent("FileName") ||
                e.Data.GetDataPresent("text/uri-list") ||
                e.Data.GetDataPresent("application/vnd.code.tree.workspaceFiles") ||
                e.Data.GetDataPresent(DataFormats.Bitmap) || 
                e.Data.GetDataPresent(DataFormats.Dib) ||
                e.Data.GetDataPresent(DataFormats.UnicodeText) || 
                e.Data.GetDataPresent(DataFormats.StringFormat) ||
                e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Performance Fix: Do NOT query 'e.Data.GetDataPresent' across cross-process COM COM-wrappers 
            // inside 'DragOver' because this fires hundreds of times a second and completely hangs the UI thread!
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            _isDragHovering = false;
            // The user explicitly requested an impenetrable UI overlay without funky Hide bugs on child-element hovers.
            // Leaving the physical window drag-space now does NOT force kill the app interface!
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Disabled explicit Opacity overrides: the OS natively handles Mica/Acrylic Transparency shaders correctly!
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 100 && e.NewSize.Height > 100)
            {
                Classes.SettingsManager.Current.MiniFormWidth = (int)e.NewSize.Width;
                Classes.SettingsManager.Current.MiniFormHeight = (int)e.NewSize.Height;
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source)
            {
                var parentBtn = FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source);
                if (parentBtn != null) return; // Ignore drag if the user explicitly clicked a child button!
            }

            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2)
                {
                    return; // Never maximize the DropShelf
                }

                _isEdgeLocked = false;
                try
                {
                    this.DragMove();
                }
                catch { } 
            }
        }

        private void ToggleGlobalSync_Click(object sender, RoutedEventArgs e)
        {
            AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync = !AdvanceClip.Classes.SettingsManager.Current.EnableGlobalFirebaseSync;
            AdvanceClip.Classes.SettingsManager.Save();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearShelf();
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            _isDragHovering = false;
        }

        private void PinSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                _viewModel.TogglePin(item);
                e.Handled = true;
            }
        }

        private void DeleteSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                // Defer removal to prevent structural DOM shifts from triggering MouseUp events on unrelated ListBox items underneath
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => 
                {
                    _viewModel.RemoveItem(item);
                }, System.Windows.Threading.DispatcherPriority.Background);
                
                e.Handled = true;
            }
        }

        private void OpenSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                if (!string.IsNullOrEmpty(item.FilePath) && System.IO.File.Exists(item.FilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                }
                else if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Url && !string.IsNullOrEmpty(item.RawContent))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.RawContent) { UseShellExecute = true });
                }
                e.Handled = true;
            }
        }

        private void QuickLookSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                var qLook = new AdvanceClip.Windows.QuickLookWindow(item);
                qLook.Show();
                e.Handled = true;
            }
        }

        private void RunTerminalSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Code)
                {
                    item.RunInTerminal();
                }
                e.Handled = true;
            }
        }

        private void SmartActionSpecific_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AdvanceClip.ViewModels.ClipboardItem item)
            {
                e.Handled = true;
                if (item.SmartActionType == "CompileAndRun")
                {
                    item.CompileAndRunNative();
                }
                else if (item.SmartActionType == "OpenPDF" || item.SmartActionType == "JoinMeeting" || item.SmartActionType == "OpenBrowser")
                {
                    string target = item.SmartActionType == "OpenPDF" ? item.FilePath : item.RawContent;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
                }
                else if (item.SmartActionType == "OpenMap")
                {
                    string target = "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(item.RawContent);
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
                }
                else if (item.SmartActionType == "ConvertToPdf")
                {
                    System.Threading.Tasks.Task.Run(() => 
                    {
                        try 
                        {
                            string targetPdf = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.FilePath) ?? System.IO.Path.GetTempPath(), System.IO.Path.GetFileNameWithoutExtension(item.FilePath) + "_Converted.pdf");
                            string script = $"$word = New-Object -ComObject Word.Application; $doc = $word.Documents.Open('{item.FilePath}'); $doc.SaveAs([ref]'{targetPdf}', [ref]17); $doc.Close(); $word.Quit();";
                            var p = new System.Diagnostics.ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"", CreateNoWindow = true, UseShellExecute = false };
                            System.Diagnostics.Process.Start(p)?.WaitForExit();
                            
                            if (System.IO.File.Exists(targetPdf))
                            {
                                Dispatcher.InvokeAsync(() => {
                                    var dropList = new System.Collections.Specialized.StringCollection(); dropList.Add(targetPdf);
                                    System.Windows.Clipboard.SetFileDropList(dropList);
                                });
                            }
                        } catch { } // Sandbox fail softly if Microsoft Word isn't installed
                    });
                }
                else if (item.SmartActionType == "SetTimer")
                {
                    var tw = new AdvanceClip.Windows.TimerWindow(item.RawContent);
                    tw.Show();
                }
                else if (item.SmartActionType == "CopyQRText")
                {
                    try { System.Windows.Clipboard.SetText(item.RawContent); AdvanceClip.Windows.ToastWindow.ShowToast("QR Text Copied!"); } catch { }
                }
            }
        }

        private void ShelfListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ShelfListView.SelectedItems.Count > 0)
            {
                var itemsToRemove = ShelfListView.SelectedItems.Cast<ClipboardItem>().ToList();
                foreach (var item in itemsToRemove)
                {
                    _viewModel.RemoveItem(item);
                }
                e.Handled = true;
            }
        }

        private void NotifyIconQuit_Click(object sender, RoutedEventArgs e)
        {
            _hubWindowInstance?.ForceShutdownRelease();
            Application.Current.Shutdown();
        }

        private void nIcon_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e)
        {
            if (this.IsVisible && _viewModel.IsFullMode)
            {
                this.Hide();
            }
            else
            {
                OpenApp_Click(sender, e);
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            T? parent = parentObject as T;
            if (parent != null) return parent;
            else return FindVisualParent<T>(parentObject);
        }

        private void ShelfListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = FindVisualChild<System.Windows.Controls.ScrollViewer>((DependencyObject)sender);
            if (sv != null)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta * 0.5));
                e.Handled = true;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T tChild) return tChild;
                else
                {
                    T? childOfChild = FindVisualChild<T>(child);
                    if (childOfChild != null) return childOfChild;
                }
            }
            return null;
        }

        private async void ShelfListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // If a drag-out just completed, skip paste-on-click entirely
            if (_didDragOut)
            {
                _didDragOut = false;
                e.Handled = true;
                return;
            }

            // Prevent accidental copy when window just spawned under cursor
            if ((DateTime.Now - _spawnTime).TotalMilliseconds < 300)
            {
                e.Handled = true;
                return;
            }

            // Allow multiple selection mechanically when modifying keys are held!
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                return;
            }

            if (e.OriginalSource is DependencyObject sourceElement)
            {
                var parentButton = FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement);
                if (parentButton != null)
                {
                    return; 
                }
            }

            var listView = sender as System.Windows.Controls.ListView;
            if (listView == null) return;
            var itemContainer = System.Windows.Controls.ItemsControl.ContainerFromElement(listView, e.OriginalSource as DependencyObject) as System.Windows.Controls.ListViewItem;
            
            if (itemContainer != null)
            {
                var clipboardObj = itemContainer.DataContext as ClipboardItem;
                if (clipboardObj != null)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(clipboardObj.FilePath))
                        {
                            var dataObj = new DataObject();
                            
                            var dropList = new System.Collections.Specialized.StringCollection();
                            dropList.Add(clipboardObj.FilePath);
                            dataObj.SetFileDropList(dropList);
                            dataObj.SetData(DataFormats.StringFormat, clipboardObj.FilePath);
                            dataObj.SetData(DataFormats.Text, clipboardObj.FilePath);
                            dataObj.SetData("FileNameW", new string[] { clipboardObj.FilePath });
                            dataObj.SetData("FileName", new string[] { clipboardObj.FilePath });
                            try { dataObj.SetData("text/uri-list", "file:///" + clipboardObj.FilePath.Replace("\\", "/")); } catch { }
                            
                            if (clipboardObj.ItemType == ClipboardItemType.Image)
                            {
                                try
                                {
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.UriSource = new Uri(clipboardObj.FilePath);
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.EndInit();
                                    dataObj.SetImage(bmp);
                                }
                                catch { }
                            }
                            
                            // Explicit Win32 Shell 'Copy' Effect override (Required for Windows Explorer Paste)
                            byte[] moveEffect = new byte[] { 5, 0, 0, 0 }; // DragDropEffects.Copy
                            System.IO.MemoryStream dropEffect = new System.IO.MemoryStream();
                            dropEffect.Write(moveEffect, 0, moveEffect.Length);
                            dataObj.SetData("Preferred DropEffect", dropEffect);

                            _isWritingClipboard = true;
                            for(int retry=0; retry<5; retry++) {
                                try { System.Windows.Clipboard.SetDataObject(dataObj, true); break; }
                                catch { System.Threading.Thread.Sleep(20); }
                            }
                            _isWritingClipboard = false;
                        }
                        else if (!string.IsNullOrEmpty(clipboardObj.RawContent))
                        {
                            _isWritingClipboard = true;
                            for(int retry=0; retry<5; retry++) {
                                try { System.Windows.Clipboard.SetText(clipboardObj.RawContent); break; }
                                catch { System.Threading.Thread.Sleep(20); }
                            }
                            _isWritingClipboard = false;
                        }
                    }
                    catch { } 

                    // Mimic Native Windows Clipboard by hiding the popup immediately upon selection!
                    this.Hide();
                    _isDragHovering = false;

                    // Wait for the Hide to fully process and window to release input focus
                    await System.Threading.Tasks.Task.Delay(200);

                    // Throw absolute unmanaged focus to the prior text box exactly before firing Native Windows Keyboard injection natively!
                    if (_previousForegroundWindow != IntPtr.Zero)
                    {
                        var sbTitle = new System.Text.StringBuilder(256);
                        GetWindowText(_previousForegroundWindow, sbTitle, 256);
                        string contextTitle = sbTitle.ToString();
                        
                        if (!string.IsNullOrWhiteSpace(contextTitle))
                        {
                            clipboardObj.AssociatedContextTitle = contextTitle;
                        }
                        
                        SetForegroundWindow(_previousForegroundWindow);
                        await System.Threading.Tasks.Task.Delay(80);
                        
                        // Retry focus if the OS didn't switch fast enough
                        if (GetForegroundWindow() != _previousForegroundWindow)
                        {
                            SetForegroundWindow(_previousForegroundWindow);
                            await System.Threading.Tasks.Task.Delay(80);
                        }
                    }

                    // Send Ctrl+V
                    keybd_event(VK_CONTROL, 0, 0, 0);
                    keybd_event(VK_V, 0, 0, 0);
                    keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);

                    e.Handled = true;
                }
            }
        }

        private void ShelfListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _didDragOut = false;

            // Pre-select the item under the cursor so it's available for drag-out in MouseMove.
            // WPF's default ListView selection happens on MouseUp, which is too late for drag initiation.
            if (e.OriginalSource is DependencyObject sourceElement)
            {
                // Don't interfere with button clicks
                var parentButton = FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(sourceElement);
                if (parentButton != null) return;

                var itemContainer = ItemsControl.ContainerFromElement(ShelfListView, sourceElement) as ListViewItem;
                if (itemContainer != null && itemContainer.DataContext is ClipboardItem)
                {
                    if (!Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl) &&
                        !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
                    {
                        ShelfListView.SelectedItems.Clear();
                    }
                    itemContainer.IsSelected = true;
                }
            }
        }

        private void ShelfListView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point position = e.GetPosition(null);
                Vector diff = _dragStartPoint - position;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (ShelfListView.SelectedItems.Count > 0)
                    {
                        var firstItem = ShelfListView.SelectedItems.Cast<ClipboardItem>().FirstOrDefault();
                        if (firstItem == null) return;

                        DataObject dataObj = new DataObject();
                        if (!string.IsNullOrEmpty(firstItem.FilePath))
                        {
                            dataObj.SetData(DataFormats.FileDrop, new string[] { firstItem.FilePath });
                            dataObj.SetData("FileNameW", new string[] { firstItem.FilePath });
                            dataObj.SetData("FileName", new string[] { firstItem.FilePath });
                            try { dataObj.SetData("text/uri-list", "file:///" + firstItem.FilePath.Replace("\\", "/")); } catch { }

                            if (firstItem.ItemType == ClipboardItemType.Image)
                            {
                                try
                                {
                                    var bmp = new BitmapImage();
                                    bmp.BeginInit();
                                    bmp.UriSource = new Uri(firstItem.FilePath);
                                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                                    bmp.EndInit();
                                    dataObj.SetImage(bmp);
                                }
                                catch { }
                            }

                            // Explicit Win32 Shell 'Copy' Effect override (Required for Windows Explorer Drag Drop)
                            byte[] moveEffect = new byte[] { 5, 0, 0, 0 }; // DragDropEffects.Copy
                            System.IO.MemoryStream dropEffect = new System.IO.MemoryStream();
                            dropEffect.Write(moveEffect, 0, moveEffect.Length);
                            dataObj.SetData("Preferred DropEffect", dropEffect);
                        }
                        else 
                        {
                            dataObj.SetData(DataFormats.UnicodeText, firstItem.RawContent);
                        }
                        
                        _isInternalDragSource = true;
                        _didDragOut = true;
                        try
                        {
                            DragDropEffects result = DragDrop.DoDragDrop(ShelfListView, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
                            
                            // Absolutely disabled automatic explicit deletion under any circumstance natively! User requires strictly persistent items!
                            if (false)
                            {
                                var itemsToRemove = ShelfListView.SelectedItems.Cast<ClipboardItem>().ToList();
                                foreach (var item in itemsToRemove) 
                                {
                                    var container = ShelfListView.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.Controls.ListViewItem;
                                    if (container != null)
                                    {
                                        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, new System.Windows.Duration(TimeSpan.FromMilliseconds(200))) 
                                        {
                                            EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                                        };
                                        anim.Completed += (s, ev) => _viewModel.RemoveItem(item);
                                        container.BeginAnimation(UIElement.OpacityProperty, anim);
                                    }
                                    else
                                    {
                                        _viewModel.RemoveItem(item);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AdvanceClip.Classes.Logger.LogAction("DRAG OUT FAULT", $"Failed UI Export: {ex.Message}");
                        }
                        finally
                        {
                            _isInternalDragSource = false;
                        }
                    }
                }
            }
        }

        private void ShelfListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ShelfListView.SelectedItem is ClipboardItem item)
            {
                item.Execute();
            }
        }

        private Windows.HubWindow? _hubWindowInstance;

        private async void ForceSendItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as System.Windows.Controls.MenuItem;
                var clipItem = menuItem?.Tag as ClipboardItem;
                if (clipItem == null) return;

                // Fetch active devices
                var devices = await AdvanceClip.Classes.FirebaseSyncManager.GetActiveDevices();
                if (devices.Count == 0)
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast("No other devices found online ⚠️");
                    return;
                }

                // Build device picker dialog
                var dialog = new System.Windows.Window
                {
                    Title = "⚡ Force Send To",
                    Width = 340, Height = 300,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 30, 38)),
                    Foreground = System.Windows.Media.Brushes.White,
                };

                var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
                stack.Children.Add(new System.Windows.Controls.TextBlock 
                { 
                    Text = $"Send \"{(clipItem.FileName ?? clipItem.RawContent ?? "item").Substring(0, Math.Min(40, (clipItem.FileName ?? clipItem.RawContent ?? "item").Length))}\" to:",
                    FontWeight = FontWeights.Bold, FontSize = 14, Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
                });

                // Send to ALL button
                var allBtn = new System.Windows.Controls.Button
                {
                    Content = $"Send to ALL Devices ({devices.Count})",
                    Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 98, 235)),
                    Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0),
                };
                allBtn.Click += async (s2, e2) =>
                {
                    dialog.Close();
                    var allIds = devices.Select(d => d.Id).ToList();
                    int count = await AdvanceClip.Classes.FirebaseSyncManager.ForceSendToDevices(
                        new List<ClipboardItem> { clipItem }, allIds);
                    AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ Force sent to {count} device(s)");
                };
                stack.Children.Add(allBtn);

                // Individual device buttons
                foreach (var dev in devices)
                {
                    string emoji = dev.Type == "PC" ? "💻" : "📱";
                    var btn = new System.Windows.Controls.Button
                    {
                        Content = $"{emoji} {dev.Name} ({(dev.IsOnline ? "Online" : "Offline")})",
                        Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4),
                        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(42, 47, 58)),
                        Foreground = System.Windows.Media.Brushes.White,
                        BorderThickness = new Thickness(0),
                        Tag = dev.Id
                    };
                    btn.Click += async (s3, e3) =>
                    {
                        dialog.Close();
                        string targetId = (s3 as System.Windows.Controls.Button)?.Tag?.ToString() ?? "";
                        int count = await AdvanceClip.Classes.FirebaseSyncManager.ForceSendToDevices(
                            new List<ClipboardItem> { clipItem }, new List<string> { targetId });
                        AdvanceClip.Windows.ToastWindow.ShowToast($"⚡ Force sent ({count} item)");
                    };
                    stack.Children.Add(btn);
                }

                var scroll = new System.Windows.Controls.ScrollViewer { Content = stack, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
                dialog.Content = scroll;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast($"Force Send Error: {ex.Message}");
            }
        }

        private void OpenApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_hubWindowInstance == null || !_hubWindowInstance.IsLoaded)
                {
                    _hubWindowInstance = new Windows.HubWindow(_viewModel);
                    _hubWindowInstance.Closed += (s, args) => _hubWindowInstance = null;
                    _hubWindowInstance.Show();
                }
                else
                {
                    if (_hubWindowInstance.WindowState == WindowState.Minimized)
                        _hubWindowInstance.WindowState = WindowState.Normal;
                    _hubWindowInstance.Show();
                }
                _hubWindowInstance.Activate();
                _hubWindowInstance.Focus();
                this.Hide();
            }
            catch (Exception ex)
            {
                _hubWindowInstance = null;
                var fullMsg = ex.ToString();
                var inner = ex.InnerException;
                while (inner != null) { fullMsg += "\n--- INNER: " + inner.Message; inner = inner.InnerException; }
                AdvanceClip.Classes.Logger.LogAction("HUBWINDOW_FAIL", fullMsg);
                AdvanceClip.Windows.ToastWindow.ShowToast($"Hub Error: {(ex.InnerException?.Message ?? ex.Message)}");
            }
        }

        private void ShelfListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ShelfListView.SelectedItems.Count > 1)
            {
                bool allPdfs = true;
                foreach (var item in ShelfListView.SelectedItems)
                {
                    if (item is ClipboardItem clipItem && clipItem.ItemType != ClipboardItemType.Pdf)
                    {
                        allPdfs = false;
                        break;
                    }
                }

                if (allPdfs)
                {
                    MergeSelectedPdfsText.Text = $"Merge {ShelfListView.SelectedItems.Count} PDFs";
                    MergeSelectedPdfsBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                MergeSelectedPdfsBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void MergeSelectedPdfsBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedPdfs = ShelfListView.SelectedItems.Cast<ClipboardItem>().ToList();
            if (selectedPdfs.Count > 1)
            {
                var mergeWindow = new AdvanceClip.Windows.PdfMergeWindow(selectedPdfs, _viewModel);
                mergeWindow.Show();
                this.Hide();
                ShelfListView.SelectedItems.Clear();
            }
        }
    }
}
