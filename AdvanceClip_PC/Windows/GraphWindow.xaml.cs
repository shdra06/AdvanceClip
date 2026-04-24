using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AdvanceClip.Classes;
using MicaWPF.Controls;

namespace AdvanceClip.Windows
{
    public partial class GraphWindow : MicaWindow
    {
        private string _equation;
        private double _xMin = -10, _xMax = 10;
        private double _yMin = -10, _yMax = 10;
        private double _panStartX, _panStartY;
        private bool _isPanning = false;
        private double _panXMinStart, _panYMinStart, _panXMaxStart, _panYMaxStart;

        public GraphWindow(string equation)
        {
            InitializeComponent();
            _equation = equation;
            EquationTitle.Text = $"y = {equation}";
        }

        // ═══ Drawing ═══

        private void DrawGraph()
        {
            GraphCanvas.Children.Clear();
            double w = GraphCanvas.ActualWidth;
            double h = GraphCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            double xRange = _xMax - _xMin;
            double yRange = _yMax - _yMin;

            // Helper: world → screen coordinates
            double ToScreenX(double x) => (x - _xMin) / xRange * w;
            double ToScreenY(double y) => h - (y - _yMin) / yRange * h;

            // ── Grid Lines ──
            DrawGridLines(w, h, ToScreenX, ToScreenY);

            // ── Axes ──
            var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1.5);

            // Y-axis
            double originX = ToScreenX(0);
            if (originX >= 0 && originX <= w)
            {
                var yAxis = new Line { X1 = originX, Y1 = 0, X2 = originX, Y2 = h, Stroke = axisPen.Brush, StrokeThickness = 1.5 };
                GraphCanvas.Children.Add(yAxis);
            }

            // X-axis
            double originY = ToScreenY(0);
            if (originY >= 0 && originY <= h)
            {
                var xAxis = new Line { X1 = 0, Y1 = originY, X2 = w, Y2 = originY, Stroke = axisPen.Brush, StrokeThickness = 1.5 };
                GraphCanvas.Children.Add(xAxis);
            }

            // ── Plot the curve ──
            var curveBrush = new LinearGradientBrush(
                Color.FromRgb(99, 102, 241),  // #6366F1
                Color.FromRgb(167, 139, 250), // #A78BFA
                new Point(0, 0), new Point(1, 1));
            curveBrush.Freeze();

            var polyline = new Polyline
            {
                Stroke = curveBrush,
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round
            };

            int samples = (int)Math.Max(w * 2, 400);
            double step = xRange / samples;

            for (int i = 0; i <= samples; i++)
            {
                double x = _xMin + i * step;
                double y = MathSolver.EvaluateAtX(_equation, x);

                if (double.IsNaN(y) || double.IsInfinity(y)) continue;

                double sx = ToScreenX(x);
                double sy = ToScreenY(y);

                // Clamp to prevent huge coordinates
                sy = Math.Clamp(sy, -5000, h + 5000);

                polyline.Points.Add(new Point(sx, sy));
            }

            GraphCanvas.Children.Add(polyline);

            // Zoom label
            double zoom = 20.0 / xRange * 100;
            ZoomLabel.Text = $"Range: [{_xMin:F1}, {_xMax:F1}] × [{_yMin:F1}, {_yMax:F1}]";
        }

        private void DrawGridLines(double w, double h, Func<double, double> toScreenX, Func<double, double> toScreenY)
        {
            var gridBrush = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
            gridBrush.Freeze();
            var labelBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
            labelBrush.Freeze();

            double xRange = _xMax - _xMin;
            double yRange = _yMax - _yMin;

            // Calculate nice grid spacing
            double xStep = NiceStep(xRange / 10);
            double yStep = NiceStep(yRange / 10);

            // Vertical grid lines + X labels
            double startX = Math.Ceiling(_xMin / xStep) * xStep;
            for (double x = startX; x <= _xMax; x += xStep)
            {
                double sx = toScreenX(x);
                var line = new Line { X1 = sx, Y1 = 0, X2 = sx, Y2 = h, Stroke = gridBrush, StrokeThickness = 1 };
                GraphCanvas.Children.Add(line);

                if (Math.Abs(x) > xStep * 0.1) // Don't label origin
                {
                    var label = new TextBlock { Text = FormatNumber(x), FontSize = 9, Foreground = labelBrush, FontFamily = new FontFamily("Consolas") };
                    Canvas.SetLeft(label, sx + 3);
                    Canvas.SetTop(label, Math.Clamp(toScreenY(0) + 2, 0, h - 14));
                    GraphCanvas.Children.Add(label);
                }
            }

            // Horizontal grid lines + Y labels
            double startY = Math.Ceiling(_yMin / yStep) * yStep;
            for (double y = startY; y <= _yMax; y += yStep)
            {
                double sy = toScreenY(y);
                var line = new Line { X1 = 0, Y1 = sy, X2 = w, Y2 = sy, Stroke = gridBrush, StrokeThickness = 1 };
                GraphCanvas.Children.Add(line);

                if (Math.Abs(y) > yStep * 0.1)
                {
                    var label = new TextBlock { Text = FormatNumber(y), FontSize = 9, Foreground = labelBrush, FontFamily = new FontFamily("Consolas") };
                    Canvas.SetLeft(label, Math.Clamp(toScreenX(0) + 3, 0, w - 30));
                    Canvas.SetTop(label, sy - 14);
                    GraphCanvas.Children.Add(label);
                }
            }
        }

        private static double NiceStep(double rough)
        {
            double pow = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            double norm = rough / pow;
            if (norm <= 1) return pow;
            if (norm <= 2) return 2 * pow;
            if (norm <= 5) return 5 * pow;
            return 10 * pow;
        }

        private static string FormatNumber(double v)
        {
            if (Math.Abs(v) < 1e-10) return "0";
            if (Math.Abs(v) >= 1000 || (Math.Abs(v) < 0.01 && Math.Abs(v) > 0)) return v.ToString("G3");
            return v.ToString("G4");
        }

        // ═══ Zoom ═══

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(0.75);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1.33);
        private void ResetZoom_Click(object sender, RoutedEventArgs e)
        {
            _xMin = -10; _xMax = 10; _yMin = -10; _yMax = 10;
            DrawGraph();
        }

        private void Zoom(double factor)
        {
            double cx = (_xMin + _xMax) / 2;
            double cy = (_yMin + _yMax) / 2;
            double hw = (_xMax - _xMin) / 2 * factor;
            double hh = (_yMax - _yMin) / 2 * factor;
            _xMin = cx - hw; _xMax = cx + hw;
            _yMin = cy - hh; _yMax = cy + hh;
            DrawGraph();
        }

        private void GraphCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 0.85 : 1.18;
            
            // Zoom toward mouse position
            var pos = e.GetPosition(GraphCanvas);
            double mx = _xMin + (pos.X / GraphCanvas.ActualWidth) * (_xMax - _xMin);
            double my = _yMax - (pos.Y / GraphCanvas.ActualHeight) * (_yMax - _yMin);
            
            _xMin = mx + (_xMin - mx) * factor;
            _xMax = mx + (_xMax - mx) * factor;
            _yMin = my + (_yMin - my) * factor;
            _yMax = my + (_yMax - my) * factor;
            
            DrawGraph();
        }

        // ═══ Pan ═══

        private void GraphCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStartX = e.GetPosition(GraphCanvas).X;
            _panStartY = e.GetPosition(GraphCanvas).Y;
            _panXMinStart = _xMin; _panXMaxStart = _xMax;
            _panYMinStart = _yMin; _panYMaxStart = _yMax;
            GraphCanvas.CaptureMouse();
            GraphCanvas.Cursor = Cursors.Hand;
        }

        private void GraphCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            GraphCanvas.ReleaseMouseCapture();
            GraphCanvas.Cursor = Cursors.Cross;
        }

        private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(GraphCanvas);

            // Coordinate display
            double xRange = _xMax - _xMin;
            double yRange = _yMax - _yMin;
            double mx = _xMin + (pos.X / GraphCanvas.ActualWidth) * xRange;
            double my = _yMax - (pos.Y / GraphCanvas.ActualHeight) * yRange;
            double fy = MathSolver.EvaluateAtX(_equation, mx);
            CoordDisplay.Text = $"x = {mx:F3}   y = {my:F3}   f(x) = {fy:F3}";

            if (_isPanning)
            {
                double dx = (pos.X - _panStartX) / GraphCanvas.ActualWidth * xRange;
                double dy = (pos.Y - _panStartY) / GraphCanvas.ActualHeight * yRange;
                _xMin = _panXMinStart - dx;
                _xMax = _panXMaxStart - dx;
                _yMin = _panYMinStart + dy;
                _yMax = _panYMaxStart + dy;
                DrawGraph();
            }
        }

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();
    }
}
