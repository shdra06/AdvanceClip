using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdvanceClip.Classes;

namespace AdvanceClip.Windows
{
    public partial class TableEditorWindow : MicaWPF.Controls.MicaWindow
    {
        private int _maxRow = -1;
        private int _maxCol = -1;
        private TextBox[,] _gridBoxes;

        public TableEditorWindow(string jsonMatrix)
        {
            InitializeComponent();
            ParseAndConstructGrid(jsonMatrix);
        }

        private void ParseAndConstructGrid(string jsonPayload)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, CellData>>(jsonPayload);
                if (dict == null || dict.Count == 0) throw new Exception("AI Node output a completely null payload array mapping.");

                // Calculate matrix boundaries
                foreach (var key in dict.Keys)
                {
                    string cleaned = key.Replace("(", "").Replace(")", "");
                    var parts = cleaned.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int r) && int.TryParse(parts[1], out int c))
                    {
                        if (r > _maxRow) _maxRow = r;
                        if (c > _maxCol) _maxCol = c;
                    }
                }

                if (_maxRow < 0 || _maxCol < 0) throw new Exception("AI Output format entirely corrupted. Valid array bounds missing.");

                int rows = _maxRow + 1;
                int cols = _maxCol + 1;
                _gridBoxes = new TextBox[rows, cols];

                var uGrid = new System.Windows.Controls.Primitives.UniformGrid
                {
                    Rows = rows,
                    Columns = cols
                };

                for (int i = 0; i < rows; i++)
                {
                    for (int j = 0; j < cols; j++)
                    {
                        string targetKey = $"({i},{j})";
                        string val = "";
                        double conf = 0.0;

                        if (dict.ContainsKey(targetKey))
                        {
                            val = dict[targetKey].text;
                            conf = dict[targetKey].conf;
                        }

                        // Build editable native layout textboxes with logic
                        var border = new Border
                        {
                            BorderThickness = new Thickness(1),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                            Padding = new Thickness(4)
                        };

                        // Determine Confidence Colors dynamically based upon Tesseract/Gemini certainty
                        Color bg;
                        if (conf >= 0.95) bg = Color.FromArgb(50, 16, 185, 129);      // Green (Extremely Confident)
                        else if (conf >= 0.85) bg = Color.FromArgb(60, 234, 179, 8);   // Yellow (Warning)
                        else bg = Color.FromArgb(60, 239, 68, 68);                     // Red (Critical Review Needed)
                        
                        // Treat exact 1.0 logic overrides (like explicit Gemini Cloud bypasses) as invisible unless specified
                        if (conf >= 1.0) bg = Color.FromArgb(10, 255, 255, 255);

                        border.Background = new SolidColorBrush(bg);

                        var tb = new TextBox
                        {
                            Text = val,
                            TextWrapping = TextWrapping.Wrap,
                            AcceptsReturn = true,
                            Background = Brushes.Transparent,
                            Foreground = Brushes.White,
                            BorderThickness = new Thickness(0),
                            MinHeight = 40,
                            FontSize = 14,
                            FontWeight = (i == 0) ? FontWeights.Bold : FontWeights.Normal // Fake Header weight styling
                        };

                        _gridBoxes[i, j] = tb;
                        border.Child = tb;
                        uGrid.Children.Add(border);
                    }
                }

                MatrixContainer.Children.Add(uGrid);
            }
            catch (Exception ex)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast("Matrix Rendering Failed. Fallback error logic activated.");
                AdvanceClip.Classes.Logger.LogAction("MATRIX FATAL", $"Parsing fault bridging AI into XAML elements: {ex.Message}");
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sbHtml = new System.Text.StringBuilder();
                sbHtml.Append("<table style=\"border-collapse: collapse; width: 100%; border: 1px solid #ddd;\">\n");

                int rows = _maxRow + 1;
                int cols = _maxCol + 1;

                for (int i = 0; i < rows; i++)
                {
                    sbHtml.Append("<tr>\n");
                    for (int j = 0; j < cols; j++)
                    {
                        string cellVal = _gridBoxes[i, j].Text;
                        string tag = (i == 0) ? "th" : "td";
                        string style = (i == 0) ? "border: 1px solid #000; padding: 8px; font-weight: bold; background-color: #f2f2f2;" : "border: 1px solid #ddd; padding: 8px;";
                        
                        sbHtml.Append($"<{tag} style=\"{style}\">");
                        sbHtml.Append(System.Net.WebUtility.HtmlEncode(cellVal));
                        sbHtml.Append($"</{tag}>\n");
                    }
                    sbHtml.Append("</tr>\n");
                }
                sbHtml.Append("</table>\n");

                string rawHtml = sbHtml.ToString();
                
                // Construct the highly strict CF_HTML formatted sequence allowing Microsoft Word targeting natively
                var sbFinal = new System.Text.StringBuilder();
                string pre = "Version:0.9\r\nStartHTML:00000000\r\nEndHTML:00000000\r\nStartFragment:00000000\r\nEndFragment:00000000\r\n<html><body>\r\n<!--StartFragment-->\r\n";
                string post = "\r\n<!--EndFragment-->\r\n</body>\r\n</html>";
                
                sbFinal.Append(pre);
                sbFinal.Append(rawHtml);
                sbFinal.Append(post);

                string textBlock = sbFinal.ToString();
                byte[] totalBytes = System.Text.Encoding.UTF8.GetBytes(textBlock);

                int startHtml = 0;
                int startFrag = System.Text.Encoding.UTF8.GetByteCount(pre);
                int endFrag = startFrag + System.Text.Encoding.UTF8.GetByteCount(rawHtml);
                int endHtml = totalBytes.Length;

                sbFinal.Replace("StartHTML:00000000", $"StartHTML:{startHtml:D8}");
                sbFinal.Replace("EndHTML:00000000",   $"EndHTML:{endHtml:D8}");
                sbFinal.Replace("StartFragment:00000000", $"StartFragment:{startFrag:D8}");
                sbFinal.Replace("EndFragment:00000000",   $"EndFragment:{endFrag:D8}");

                System.Windows.Clipboard.SetText(sbFinal.ToString(), TextDataFormat.Html);
                AdvanceClip.Windows.ToastWindow.ShowToast("Matrix CF_HTML perfectly routed to OS Clipboard! (CTRL+V anywhere)");
                
                this.Close();
            }
            catch (Exception ex)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast($"Export fatal fault: {ex.Message}");
                AdvanceClip.Classes.Logger.LogAction("MATRIX CF_HTML FATAL", ex.Message);
            }
        }

        private class CellData
        {
            public string text { get; set; } = string.Empty;
            public double conf { get; set; } = 1.0;
        }
    }
}
