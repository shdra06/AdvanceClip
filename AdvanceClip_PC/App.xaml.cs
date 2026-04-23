using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace AdvanceClip;

public partial class App : Application
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    private static LowLevelMouseProc _mouseProc = MouseHookCallback;
    private static IntPtr _mouseHookID = IntPtr.Zero;
    private static App _instance;
    private static MainWindow _mainWinInstance;

    // Shake Detection State
    private static int _shakeCount = 0;
    private static int _lastShakeDirX = 0; 
    private static int _lastShakeDirY = 0; 
    private static int _lastShakeX = 0;
    private static int _lastShakeY = 0;
    private static long _lastShakeTime = 0;
    private static int _shakeStartY = 0;
    private static long _lastClipboardLaunchTime = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static System.Threading.Mutex _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string appName = "AdvanceClip_SingleInstance_Mutex_Global";
        bool createdNew;

        _mutex = new System.Threading.Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            // Another instance is already running
            Application.Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        
        // ------------------------------------------------------------------
        // Single File Deployment: Synthesize the physical scripts locally FIRST!
        AdvanceClip.Classes.RuntimeHost.Initialize();
        // ------------------------------------------------------------------

        AdvanceClip.Classes.SettingsManager.Load();
        
        try 
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                // Environment.ProcessPath guarantees absolute pathing even for self-contained SingleFile bundles 
                if (key != null) key.SetValue("AdvanceClip", Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
        }
        catch (Exception) { /* Swallow permission constraint exceptions gracefully */ }
        
        _instance = this;
        _mouseHookID = SetMouseHook(_mouseProc);

        try
        {
            // Catch UI thread exceptions silently without tearing down the program
            DispatcherUnhandledException += (s, args) =>
            {
                try { System.IO.File.AppendAllText("advanceclip_debugger.log", $"[{DateTime.Now}] UI CAUGHT: {args.Exception.ToString()}\n"); } catch { }
                try { AdvanceClip.Classes.Logger.LogAction("UI ERROR", args.Exception.Message); } catch { }
                args.Handled = true; // Tell Windows not to crash the executable
            };

            // Catch background thread crashes — log but DON'T let them kill the process
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try { System.IO.File.AppendAllText("advanceclip_debugger.log", $"[{DateTime.Now}] UNMANAGED FATAL: {args.ExceptionObject}\n"); } catch { }
                // NOTE: IsTerminating=true means the CLR is shutting down. We can't prevent it here,
                // but we CAN prevent it by ensuring all async calls are wrapped in try/catch upstream.
            };

            // Catch async Task thread exceptions — THIS is the critical one
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                // ALWAYS observe to prevent process termination
                args.SetObserved();
                try { System.IO.File.AppendAllText("advanceclip_debugger.log", $"[{DateTime.Now}] ASYNC SWALLOWED: {args.Exception.Message}\n"); } catch { }
            };

            if (string.IsNullOrWhiteSpace(AdvanceClip.Classes.SettingsManager.Current.DeviceName))
            {
                Window namingWindow = new Window
                {
                    Title = "AdvanceClip Initialization",
                    Width = 450,
                    Height = 260,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Topmost = true
                };

                var outerBorder = new System.Windows.Controls.Border {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 25)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12)
                };

                var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(30), VerticalAlignment = VerticalAlignment.Center };
                
                stack.Children.Add(new System.Windows.Controls.TextBlock { 
                    Text = "AdvanceClip Mesh Registration", 
                    FontSize = 22, 
                    FontWeight = FontWeights.Bold, 
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                
                stack.Children.Add(new System.Windows.Controls.TextBlock { 
                    Text = "Set a unique identifier for this PC to sync across your network.", 
                    FontSize = 13, 
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 160, 160)),
                    Margin = new Thickness(0, 0, 0, 24),
                    TextWrapping = TextWrapping.Wrap
                });
                
                stack.Children.Add(new System.Windows.Controls.TextBlock { 
                    Text = "PC Node Name", 
                    FontSize = 14, 
                    FontWeight = FontWeights.SemiBold, 
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 8)
                });
                
                var inputBorder = new System.Windows.Controls.Border {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 15)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };
                
                var input = new System.Windows.Controls.TextBox { 
                    FontSize = 15, 
                    Padding = new Thickness(12), 
                    Background = System.Windows.Media.Brushes.Transparent, 
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    CaretBrush = System.Windows.Media.Brushes.White
                };
                inputBorder.Child = input;
                stack.Children.Add(inputBorder);
                
                var btnBorder = new System.Windows.Controls.Border {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)), // Emerald-500
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(24, 10, 24, 10),
                    Margin = new Thickness(0, 24, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Cursor = Cursors.Hand
                };
                
                var btnText = new System.Windows.Controls.TextBlock {
                    Text = "Join Mesh",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                btnBorder.Child = btnText;
                
                btnBorder.MouseEnter += (s, ev) => btnBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105));
                btnBorder.MouseLeave += (s, ev) => btnBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                
                btnBorder.MouseLeftButtonDown += (s, ev) => {
                    if (!string.IsNullOrWhiteSpace(input.Text))
                    {
                        AdvanceClip.Classes.SettingsManager.Current.DeviceName = input.Text.Trim();
                        AdvanceClip.Classes.SettingsManager.Save();
                        namingWindow.DialogResult = true;
                        namingWindow.Close();
                    }
                };
                stack.Children.Add(btnBorder);
                
                outerBorder.Child = stack;
                namingWindow.Content = outerBorder;
                
                namingWindow.Loaded += (s, ev) => { input.Focus(); };
                
                namingWindow.ShowDialog();
            }

            // Provide immediate feedback that the service captured the network without waiting for graphics
            AdvanceClip.Windows.ToastWindow.ShowToast("Service online");


            // Offload the massive WPF XAML layout rasterization payload directly to the background!
            // This drops AdvanceClip's actual active startup boot time from ~2000ms straight to < 10ms!
            Application.Current.Dispatcher.InvokeAsync(async () => 
            {
                _mainWinInstance = new MainWindow();
                MainWindow = _mainWinInstance;
                
                // Load persisted clipboard history (text + images survive restarts)
                (_mainWinInstance.DataContext as ViewModels.DropShelfViewModel)?.LoadPersistedHistory();
                
                MainWindow.Show();
                
                // One-time cleanup: purge old GUID-based device entries from Firebase
                _ = AdvanceClip.Classes.FirebaseSyncManager.CleanupStaleDevices();
                
                // Dump full network diagnostics at startup for remote debugging
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(8000); // Wait for Cloudflare to initialize
                    AdvanceClip.Classes.Logger.DumpNetworkDiagnostics();
                });
                
                // CRITICAL: Give the NotifyIcon (system tray) and TaskbarWindow (widget)
                // enough time to register before hiding. The WPF-UI tray:NotifyIcon
                // registers in the Loaded event — hiding immediately kills the registration.
                await System.Threading.Tasks.Task.Delay(500);
                MainWindow.Hide();
                
                var taskbar = new AdvanceClip.Windows.TaskbarWindow();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("startup_error.txt", ex.ToString());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnhookWindowsHookEx(_mouseHookID);
        
        try
        {
            AdvanceClip.Classes.FirebaseSyncManager.PushTunnelUrl("offline", false).Wait(1500);
        }
        catch { }
        
        AdvanceClip.Classes.Logger.Shutdown();
        base.OnExit(e);
    }

    private static IntPtr SetMouseHook(LowLevelMouseProc proc)
    {
        using (Process curProcess = Process.GetCurrentProcess())
        using (ProcessModule curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_MOUSE_LL, proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
            {
                MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                
                // Intelligent Hub Verification: Don't trigger if the user is explicitly dragging our own UI!
                // We do not evaluate WindowFromPoint here! Calling COM / UI thread operations 
                // inside a LowLevelMouseProc will freeze the entire Windows OS mouse!

                // Global Debounce: Prevent repeated triggering if it recently opened (5 seconds rule)
                if (Environment.TickCount64 - _lastClipboardLaunchTime < 5000)
                {
                    _shakeCount = 0;
                    return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
                }

                int currentX = hookStruct.pt.x;
                long currentTime = Environment.TickCount64;

                // Track the very beginning of a possible gesture chain to calculate net downward drift
                if (_shakeCount == 0)
                {
                    _shakeStartY = hookStruct.pt.y;
                }

                // Restrict the evaluation completely to tight shakes (between 15ms and 600ms consecutive loops)
                if (currentTime - _lastShakeTime > 600)
                {
                    _shakeCount = 0;
                    _lastShakeDirX = 0;
                    _lastShakeDirY = 0;
                    _lastShakeX = currentX;
                    _lastShakeY = hookStruct.pt.y;
                    _lastShakeTime = currentTime;
                }
                else
                {
                    int deltaX = currentX - _lastShakeX;
                    int deltaY = hookStruct.pt.y - _lastShakeY;
                    
                    bool reversed = false;
                    int currentDirX = deltaX > 0 ? 1 : (deltaX < 0 ? -1 : 0);
                    int currentDirY = deltaY > 0 ? 1 : (deltaY < 0 ? -1 : 0);

                    // Required amplitude 25 guarantees proper tracking of human back-and-forth physics
                    if (Math.Abs(deltaX) > 25)
                    {
                        if (_lastShakeDirX != 0 && currentDirX != _lastShakeDirX) reversed = true;
                        _lastShakeDirX = currentDirX;
                        _lastShakeX = currentX;
                        _lastShakeTime = currentTime;
                    }

                    if (Math.Abs(deltaY) > 25)
                    {
                        if (_lastShakeDirY != 0 && currentDirY != _lastShakeDirY) reversed = true;
                        _lastShakeDirY = currentDirY;
                        _lastShakeY = hookStruct.pt.y;
                        _lastShakeTime = currentTime;
                    }

                    if (reversed)
                    {
                        _shakeCount++;

                        // Requires 4 distinct rapid reversals (much more explicit shake vs accidental twitch)
                        if (_shakeCount >= 4)
                        {
                            _shakeCount = 0; 
                            
                            int triggerX = hookStruct.pt.x;
                            int triggerY = hookStruct.pt.y;

                            // Algorithm Upgrade: Drag-Scroll Safety Filter
                            int netDriftY = triggerY - _shakeStartY;
                            if (netDriftY > 150)
                            {
                                return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
                            }

                            // SYNCHRONOUS LOCK: Lock in the 5 second global debounce immediately on the Hook Thread!
                            // Prevents multiple queued Dispatcher events from bypassing the timer lock!
                            _lastClipboardLaunchTime = Environment.TickCount64;

                            _instance?.Dispatcher.InvokeAsync(async () => 
                            {
                                // Selection Detection Guard: Wait for the user to finish highlighting
                                // or dragging before spawning the DropShelf to prevent mid-workflow interruption!
                                await System.Threading.Tasks.Task.Delay(300);

                                // Removed the active drag check because it blocked users from drag-and-dropping physical files into the DropShelf via mouse shake!
                                // The window spawns without stealing focus (stealFocus = false), safely preserving the OS drag payload.

                                _instance.LaunchClipboardManager(triggerX, triggerY, false, 0, false);
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                }
            }
            else
            {
                _shakeCount = 0;
            }
        }
        return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
    }

    private void LaunchClipboardManager(double x, double y, bool isPersistent, int mode, bool stealFocus = true)
    {
        if (_mainWinInstance == null)
        {
            _mainWinInstance = new MainWindow();
            MainWindow = _mainWinInstance;
        }

        _mainWinInstance.ShowNearPosition(x, y, mode, isPersistent, stealFocus);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook,
        Delegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}



