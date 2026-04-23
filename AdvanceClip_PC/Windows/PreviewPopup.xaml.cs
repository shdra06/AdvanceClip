using System;
using System.Windows;
using System.Windows.Threading;

namespace AdvanceClip.Windows
{
    public partial class PreviewPopup : Window
    {
        private DispatcherTimer _autoCloseTimer;

        public PreviewPopup(string text, double x, double y)
        {
            InitializeComponent();
            PreviewText.Text = text;

            // Position near the hovered card
            this.Left = x;
            this.Top = y;

            // Auto-close after 5 seconds
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoCloseTimer.Tick += (s, e) => { _autoCloseTimer.Stop(); Close(); };
            _autoCloseTimer.Start();

            // Fade-in animation
            this.Opacity = 0;
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            this.BeginAnimation(OpacityProperty, fadeIn);
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _autoCloseTimer?.Stop();
            Close();
        }

        public void ClosePreview()
        {
            _autoCloseTimer?.Stop();
            try { Close(); } catch { }
        }
    }
}
