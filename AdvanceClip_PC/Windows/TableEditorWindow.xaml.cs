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
                int rows = _maxRow + 1;
                int cols = _maxCol + 1;

                // ═══ Build HTML table ═══
                var sbHtml = new System.Text.StringBuilder();
                sbHtml.Append("<table style=\"border-collapse: collapse; width: 100%; font-family: Calibri, Arial, sans-serif; font-size: 11pt;\">\n");

                for (int i = 0; i < rows; i++)
                {
                    sbHtml.Append("<tr>\n");
                    for (int j = 0; j < cols; j++)
                    {
                        string cellVal = _gridBoxes[i, j].Text;
                        string tag = (i == 0) ? "th" : "td";
                        string style = (i == 0) 
                            ? "border: 1px solid #000; padding: 6px 10px; font-weight: bold; background-color: #D9E2F3; text-align: left;" 
                            : "border: 1px solid #999; padding: 6px 10px; text-align: left;";
                        
                        sbHtml.Append($"<{tag} style=\"{style}\">");
                        sbHtml.Append(System.Net.WebUtility.HtmlEncode(cellVal));
                        sbHtml.Append($"</{tag}>\n");
                    }
                    sbHtml.Append("</tr>\n");
                }
                sbHtml.Append("</table>\n");

                string fragment = sbHtml.ToString();
                
                // ═══ Build CF_HTML with correct byte offsets ═══
                // The header has fixed-length placeholders that we'll fill in
                string header = "Version:0.9\r\nStartHTML:SSSSSSSS\r\nEndHTML:EEEEEEEE\r\nStartFragment:FFFFFFFF\r\nEndFragment:GGGGGGGG\r\n";
                string htmlStart = "<html><body>\r\n<!--StartFragment-->\r\n";
                string htmlEnd = "\r\n<!--EndFragment-->\r\n</body></html>";
                
                // Calculate byte offsets (CF_HTML uses UTF-8 byte positions)
                int headerLen = System.Text.Encoding.UTF8.GetByteCount(header);
                int htmlStartLen = System.Text.Encoding.UTF8.GetByteCount(htmlStart);
                int fragmentLen = System.Text.Encoding.UTF8.GetByteCount(fragment);
                int htmlEndLen = System.Text.Encoding.UTF8.GetByteCount(htmlEnd);
                
                int startHtml = headerLen;
                int startFragment = headerLen + htmlStartLen;
                int endFragment = startFragment + fragmentLen;
                int endHtml = endFragment + htmlEndLen;
                
                header = header.Replace("SSSSSSSS", startHtml.ToString("D8"));
                header = header.Replace("EEEEEEEE", endHtml.ToString("D8"));
                header = header.Replace("FFFFFFFF", startFragment.ToString("D8"));
                header = header.Replace("GGGGGGGG", endFragment.ToString("D8"));
                
                string cfHtml = header + htmlStart + fragment + htmlEnd;
                
                // ═══ Build plain text TSV (tab-separated) as fallback ═══
                var sbTsv = new System.Text.StringBuilder();
                for (int i = 0; i < rows; i++)
                {
                    for (int j = 0; j < cols; j++)
                    {
                        if (j > 0) sbTsv.Append('\t');
                        sbTsv.Append(_gridBoxes[i, j].Text);
                    }
                    sbTsv.Append('\n');
                }
                
                // ═══ Set both formats on clipboard for maximum compatibility ═══
                MainWindow._isWritingClipboard = true;
                try
                {
                    var dataObj = new DataObject();
                    dataObj.SetData(DataFormats.Html, cfHtml);
                    dataObj.SetData(DataFormats.Text, sbTsv.ToString());
                    Clipboard.SetDataObject(dataObj, true);
                }
                finally { MainWindow._isWritingClipboard = false; }
                
                AdvanceClip.Windows.ToastWindow.ShowToast("Table copied! Paste into Word with Ctrl+V 📋");
                this.Close();
            }
            catch (Exception ex)
            {
                AdvanceClip.Windows.ToastWindow.ShowToast($"Export failed: {ex.Message}");
                AdvanceClip.Classes.Logger.LogAction("TABLE_EXPORT_FAIL", ex.Message);
            }
        }

        private class CellData
        {
            public string text { get; set; } = string.Empty;
            public double conf { get; set; } = 1.0;
        }
    }
}
