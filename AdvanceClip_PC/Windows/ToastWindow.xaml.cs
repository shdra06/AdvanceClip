using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace AdvanceClip.Windows
{
    public partial class ToastWindow : Window
    {
        public ToastWindow(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
            
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left + (workArea.Width - this.Width) / 2;
            this.Top = workArea.Bottom - this.Height - 80; // Align naturally above standard Windows 11 Taskbars.
            
            this.Loaded += async (s, e) => 
            {
                await Task.Delay(2500);
                
                // Fade out memory-safe animation
                for(double i = 1; i > 0; i -= 0.1)
                {
                    this.Opacity = i;
                    await Task.Delay(20);
                }
                
                this.Close();
            };
        }
        
        public static void ShowToast(string message)
        {
            // Ensures global dispatcher captures cross-threaded Process.Start events.
            Application.Current.Dispatcher.Invoke(() => 
            {
                var toast = new ToastWindow(message);
                toast.Show();
            });
        }
    }
}
