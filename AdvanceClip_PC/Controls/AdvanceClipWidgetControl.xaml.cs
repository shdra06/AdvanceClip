using System.Windows.Controls;
using System.Windows.Input;

namespace AdvanceClip.Controls
{
    public partial class AdvanceClipWidgetControl : UserControl
    {
        private MainWindow? _mainWindow;

        public AdvanceClipWidgetControl()
        {
            InitializeComponent();
        }

        public void SetMainWindow(MainWindow window)
        {
            _mainWindow = window;
        }

        public (double Width, double Height) CalculateSize(double dpiScale)
        {
            return (80, 36); 
        }

        private void WidgetGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mainWindow != null)
            {
                var point = PointToScreen(e.GetPosition(this));
                bool isMode1 = false;
                if (_mainWindow.DataContext is AdvanceClip.ViewModels.DropShelfViewModel vm && vm.CurrentMode == 1)
                {
                    isMode1 = true;
                }

                if (_mainWindow.IsVisible && isMode1)
                {
                    _mainWindow.Hide();
                }
                else
                {
                    _mainWindow.ShowNearPosition(point.X, point.Y, 1, false);
                }
            }
        }
    }
}
