using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MicaWPF.Controls;

namespace AdvanceClip.Windows
{
    public partial class TimerWindow : MicaWindow
    {
        private DispatcherTimer _timer;
        private TimeSpan _remaining;
        private TimeSpan _totalDuration;
        private bool _isRunning;
        private bool _isFinished;
        private Path _arcPath;

        // Gradient colors for the arc
        private static readonly Color StartColor = Color.FromRgb(0x8B, 0x5C, 0xF6); // #8B5CF6
        private static readonly Color MidColor = Color.FromRgb(0x3B, 0x82, 0xF6);   // #3B82F6
        private static readonly Color EndColor = Color.FromRgb(0x06, 0xB6, 0xD4);    // #06B6D4
        private static readonly Color DangerColor = Color.FromRgb(0xEF, 0x44, 0x44); // #EF4444
        private static readonly Color WarningColor = Color.FromRgb(0xF5, 0x9E, 0x0B); // #F59E0B

        public TimerWindow(string contextString)
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; // Smooth 20fps updates
            _timer.Tick += Timer_Tick;

            ParseContext(contextString);
            _totalDuration = _remaining;
            DrawProgressArc(1.0);
            Action_Click(null, null); // Auto Start
        }

        private void ParseContext(string ctx)
        {
            try {
                ctx = ctx.ToLower();
                int minutes = 5;
                
                var match = System.Text.RegularExpressions.Regex.Match(ctx, @"(\d+)\s*(min|minute|m|hour|hr|h)");
                if (match.Success) 
                {
                    if (ctx.Contains("hour") || ctx.Contains("hr") || ctx.Contains(" h")) {
                        minutes = int.Parse(match.Groups[1].Value) * 60;
                    } else {
                        minutes = int.Parse(match.Groups[1].Value);
                    }
                    _remaining = TimeSpan.FromMinutes(minutes);
                } 
                else 
                {
                    match = System.Text.RegularExpressions.Regex.Match(ctx, @"(\d+)\s*(sec|second|s)");
                    if (match.Success) {
                        _remaining = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value));
                    } else {
                        var quickMatch = System.Text.RegularExpressions.Regex.Match(ctx.Trim(), @"^\/(\d+)$");
                        if (quickMatch.Success) {
                            _remaining = TimeSpan.FromMinutes(int.Parse(quickMatch.Groups[1].Value));
                        } else {
                            _remaining = TimeSpan.FromMinutes(5);
                        }
                    }
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(ctx, @"(?:[01]?\d|2[0-3]):[0-5]\d"))
                {
                    match = System.Text.RegularExpressions.Regex.Match(ctx, @"(?<min>[01]?\d|2[0-3]):(?<sec>[0-5]\d)");
                    if (match.Success) _remaining = new TimeSpan(0, int.Parse(match.Groups["min"].Value), int.Parse(match.Groups["sec"].Value));
                }

                UpdateTimeDisplay();
            } catch { _remaining = TimeSpan.FromMinutes(5); UpdateTimeDisplay(); }
        }

        private DateTime _lastTickSecond = DateTime.MinValue;

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_remaining.TotalSeconds <= 0)
            {
                _timer.Stop();
                _isRunning = false;
                _isFinished = true;
                ActionText.Text = "✓  Dismiss";
                PrimaryBtnBorder.Background = new SolidColorBrush(DangerColor);
                TimeDisplay.Text = "00:00";
                TimeDisplay.Foreground = new SolidColorBrush(DangerColor);
                PercentText.Text = "0%";
                PercentText.Foreground = new SolidColorBrush(DangerColor);
                StatusText.Text = "TIME'S UP!";
                StatusText.Opacity = 1.0;
                StatusText.Foreground = new SolidColorBrush(DangerColor);
                DrawProgressArc(0);
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                this.Topmost = true;
                this.Activate();
                // Flash effect
                StartFlashAnimation();
                return;
            }

            // Subtract elapsed time since last tick for accuracy
            var now = DateTime.Now;
            if (_lastTickSecond == DateTime.MinValue) _lastTickSecond = now;
            var elapsed = now - _lastTickSecond;
            _lastTickSecond = now;
            _remaining = _remaining.Subtract(elapsed);
            if (_remaining.TotalSeconds < 0) _remaining = TimeSpan.Zero;

            UpdateTimeDisplay();
            double progress = _totalDuration.TotalSeconds > 0 ? _remaining.TotalSeconds / _totalDuration.TotalSeconds : 0;
            DrawProgressArc(progress);

            // Update percentage
            int pct = (int)(progress * 100);
            PercentText.Text = $"{pct}%";

            // Color transitions based on remaining time
            if (progress < 0.1)
            {
                TimeDisplay.Foreground = new SolidColorBrush(DangerColor);
                PercentText.Foreground = new SolidColorBrush(DangerColor);
                StatusText.Text = "HURRY!";
                StatusText.Opacity = 0.8;
            }
            else if (progress < 0.25)
            {
                TimeDisplay.Foreground = new SolidColorBrush(WarningColor);
                StatusText.Text = "LOW";
                StatusText.Opacity = 0.5;
            }
            else
            {
                TimeDisplay.Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary");
                StatusText.Text = "RUNNING";
                StatusText.Opacity = 0.3;
            }
        }

        private void UpdateTimeDisplay()
        {
            if (_remaining.TotalHours >= 1) {
                TimeDisplay.Text = _remaining.ToString(@"hh\:mm\:ss");
            } else {
                TimeDisplay.Text = _remaining.ToString(@"mm\:ss");
            }
        }

        private void DrawProgressArc(double progress)
        {
            ArcCanvas.Children.Clear();
            if (progress <= 0) return;

            double size = 220;
            double strokeWidth = 6;
            double radius = (size - strokeWidth) / 2;
            double cx = size / 2;
            double cy = size / 2;

            double angleDeg = progress * 360;
            if (angleDeg > 359.9) angleDeg = 359.9;

            double startAngle = -90; // Start from top
            double endAngle = startAngle + angleDeg;

            double startRad = startAngle * Math.PI / 180;
            double endRad = endAngle * Math.PI / 180;

            double x1 = cx + radius * Math.Cos(startRad);
            double y1 = cy + radius * Math.Sin(startRad);
            double x2 = cx + radius * Math.Cos(endRad);
            double y2 = cy + radius * Math.Sin(endRad);

            bool largeArc = angleDeg > 180;

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(x2, y2),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = largeArc
            });
            geometry.Figures.Add(figure);

            // Choose color based on progress
            Brush strokeBrush;
            if (progress < 0.1)
            {
                strokeBrush = new SolidColorBrush(DangerColor);
            }
            else if (progress < 0.25)
            {
                strokeBrush = new SolidColorBrush(WarningColor);
            }
            else
            {
                strokeBrush = new LinearGradientBrush(
                    new GradientStopCollection {
                        new GradientStop(StartColor, 0),
                        new GradientStop(MidColor, 0.5),
                        new GradientStop(EndColor, 1)
                    }, new Point(0, 0), new Point(1, 1));
            }

            var path = new Path
            {
                Data = geometry,
                Stroke = strokeBrush,
                StrokeThickness = strokeWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            // Glow behind arc
            var glowPath = new Path
            {
                Data = geometry.Clone(),
                Stroke = strokeBrush.Clone(),
                StrokeThickness = strokeWidth + 12,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.15,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 16 }
            };

            ArcCanvas.Children.Add(glowPath);
            ArcCanvas.Children.Add(path);

            // Update glow ring opacity
            GlowRing.Opacity = 0.08 + progress * 0.12;
        }

        private void StartFlashAnimation()
        {
            var flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            int flashCount = 0;
            flashTimer.Tick += (s, e) =>
            {
                flashCount++;
                TimeDisplay.Opacity = flashCount % 2 == 0 ? 1.0 : 0.3;
                if (flashCount >= 10)
                {
                    flashTimer.Stop();
                    TimeDisplay.Opacity = 1.0;
                }
            };
            flashTimer.Start();
        }

        private void Action_Click(object? sender, RoutedEventArgs? e)
        {
            if (_isFinished) {
                this.Close();
                return;
            }

            if (_isRunning)
            {
                _timer.Stop();
                _isRunning = false;
                _lastTickSecond = DateTime.MinValue;
                ActionText.Text = "▶  Resume";
                StatusText.Text = "PAUSED";
                StatusText.Opacity = 0.6;
                PrimaryBtnBorder.Background = new LinearGradientBrush(
                    new GradientStopCollection {
                        new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0),
                        new GradientStop(Color.FromRgb(0x16, 0xA3, 0x4A), 1)
                    }, new Point(0, 0), new Point(1, 1));
            }
            else
            {
                _lastTickSecond = DateTime.Now;
                _timer.Start();
                _isRunning = true;
                ActionText.Text = "❚❚  Pause";
                StatusText.Text = "RUNNING";
                StatusText.Opacity = 0.3;
                PrimaryBtnBorder.Background = new LinearGradientBrush(
                    new GradientStopCollection {
                        new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 0),
                        new GradientStop(Color.FromRgb(0x63, 0x66, 0xF1), 1)
                    }, new Point(0, 0), new Point(1, 1));
            }
        }

        private void Action_Click(object sender, MouseButtonEventArgs e)
        {
            Action_Click(sender, (RoutedEventArgs?)null);
        }

        private void Reset_Click(object sender, MouseButtonEventArgs e)
        {
            _timer.Stop();
            _isRunning = false;
            _isFinished = false;
            _lastTickSecond = DateTime.MinValue;
            _remaining = _totalDuration;
            UpdateTimeDisplay();
            DrawProgressArc(1.0);
            ActionText.Text = "▶  Start";
            StatusText.Text = "READY";
            StatusText.Opacity = 0.3;
            PercentText.Text = "100%";
            PercentText.Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorSecondary");
            TimeDisplay.Foreground = (Brush)FindResource("MicaWPF.Brushes.TextFillColorPrimary");
            TimeDisplay.Opacity = 1.0;
            PrimaryBtnBorder.Background = new LinearGradientBrush(
                new GradientStopCollection {
                    new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 0),
                    new GradientStop(Color.FromRgb(0x63, 0x66, 0xF1), 1)
                }, new Point(0, 0), new Point(1, 1));
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            this.Close();
        }
    }
}
