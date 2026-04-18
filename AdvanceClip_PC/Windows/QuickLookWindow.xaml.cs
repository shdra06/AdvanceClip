using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO;

namespace AdvanceClip.Windows
{
    public partial class QuickLookWindow : Window
    {
        private AdvanceClip.ViewModels.ClipboardItem _item;
        private Point _startPoint;
        private bool _isImageLoaded = false;

        public QuickLookWindow(AdvanceClip.ViewModels.ClipboardItem item)
        {
            InitializeComponent();
            _item = item;

            PreviewImage.Visibility = Visibility.Collapsed;
            WebPreview.Visibility = Visibility.Collapsed;
            TextPreviewScroll.Visibility = Visibility.Collapsed;
            DocumentPanel.Visibility = Visibility.Collapsed;

            string ext = Path.GetExtension(item.FilePath ?? "").ToLower();

            if (item.ItemType == AdvanceClip.ViewModels.ClipboardItemType.Image)
            {
                PreviewImage.Visibility = Visibility.Visible;
                // Dynamic High-Fidelity Rendering
                try
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(item.FilePath);
                    bitmap.EndInit();
                    PreviewImage.Source = bitmap;
                    
                    // Pre-scale intelligently based on original image aspect ratio
                    if (AdvanceClip.Classes.SettingsManager.Current.QuickLookWidth > 50 && AdvanceClip.Classes.SettingsManager.Current.QuickLookHeight > 50)
                    {
                        this.Width = AdvanceClip.Classes.SettingsManager.Current.QuickLookWidth;
                        this.Height = AdvanceClip.Classes.SettingsManager.Current.QuickLookHeight;
                    }
                    else
                    {
                        this.Width = Math.Min(bitmap.PixelWidth, SystemParameters.WorkArea.Width * 0.7);
                        this.Height = Math.Min(bitmap.PixelHeight, SystemParameters.WorkArea.Height * 0.7);
                    }
                    
                    _isImageLoaded = true;
                }
                catch { } // Image is corrupt or locked natively
            }
            else if (ext == ".pdf" || ext == ".html" || ext == ".htm" || ext == ".xml")
            {
                WebPreview.Visibility = Visibility.Visible;
                try { WebPreview.Navigate(new Uri(item.FilePath)); } catch { }
                
                this.Width = 600;
                this.Height = SystemParameters.WorkArea.Height * 0.8;
                _isImageLoaded = true; // allow dragging natively
            }
            else if (ext == ".docx" || ext == ".txt" || ext == ".log" || ext == ".md" || ext == ".cs" || ext == ".cpp" || ext == ".js" || ext == ".json")
            {
                TextPreviewScroll.Visibility = Visibility.Visible;
                
                try 
                {
                    if (ext == ".docx") 
                    {
                        using (var archive = System.IO.Compression.ZipFile.OpenRead(item.FilePath))
                        {
                            var entry = archive.GetEntry("word/document.xml");
                            if (entry != null)
                            {
                                using (var stream = entry.Open())
                                using (var reader = new System.IO.StreamReader(stream))
                                {
                                    string xml = reader.ReadToEnd();
                                    string text = System.Text.RegularExpressions.Regex.Replace(xml, @"<[^>]+>", " ");
                                    TextPreview.Text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                                }
                            }
                        }
                    }
                    else 
                    {
                        TextPreview.Text = File.ReadAllText(item.FilePath);
                    }
                } 
                catch { TextPreview.Text = "[AdvanceClip Codec Error: Cannot extract raw string payload from this artifact natively]"; }

                this.Width = 550;
                this.Height = 650;
                _isImageLoaded = true; // allow native dragging for textual representations
            }
            else
            {
                // Default Document Fallback Mode
                DocumentPanel.Visibility = Visibility.Visible;
                DocTitle.Text = Path.GetFileName(item.FilePath);
                
                try {
                    long length = new FileInfo(item.FilePath).Length;
                    DocSize.Text = $"{item.ItemType.ToString()} Document • {(length / 1024.0 / 1024.0):0.00} MB";
                } catch { DocSize.Text = "Unknown Size"; }

                this.Width = 400;
                this.Height = 350;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_item.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Could not launch the native visual previewer application: " + ex.Message); }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject && !(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase))
            {
                _startPoint = e.GetPosition(null);

                // Allows the entire floating object to act as a 100% native draggable window!
                if (e.LeftButton == MouseButtonState.Pressed && e.ClickCount == 1)
                {
                    this.DragMove();
                }
            }
        }

        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Do not maximize Document Cards, only floating raw media images!
            if (!_isImageLoaded) return; 

            if (this.WindowState == WindowState.Normal)
                this.WindowState = WindowState.Maximized;
            else
                this.WindowState = WindowState.Normal;
            
            e.Handled = true;
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _isImageLoaded)
            {
                Point mousePos = e.GetPosition(null);
                Vector diff = _startPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    // Convert the image payload into a massive Drag Source natively!
                    if (File.Exists(_item.FilePath))
                    {
                        var dataObject = new DataObject();
                        // Allows dragging directly into WhatsApp, Discord, Photoshop natively!
                        dataObject.SetData(DataFormats.FileDrop, new string[] { _item.FilePath });
                        
                        DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);
                    }
                }
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Do nothing. Let the user keep it floating on their other monitor while they work!
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_isImageLoaded)
            {
                AdvanceClip.Classes.SettingsManager.Current.QuickLookWidth = this.Width;
                AdvanceClip.Classes.SettingsManager.Current.QuickLookHeight = this.Height;
                AdvanceClip.Classes.SettingsManager.Save();
            }
            base.OnClosed(e);
        }
    }
}
