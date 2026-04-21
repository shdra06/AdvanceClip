using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdvanceClip.Classes;

namespace AdvanceClip.Windows
{
    public partial class TableEditorWindow : MicaWPF.Controls.MicaWindow
    {
        private int _rows;
        private int _cols;
        private TextBox[,] _cells;
        private double[,] _confidence; // 0.0 - 1.0 per cell

        /// <summary>
        /// Accepts either:
        /// 1. JSON matrix format: {"(0,0)": {"text":"...", "conf":0.95}, ...}
        /// 2. Raw text: tab/pipe/comma-separated lines
        /// </summary>
        public TableEditorWindow(string input)
        {
            InitializeComponent();

            if (IsJsonMatrix(input))
                ParseJsonMatrix(input);
            else
                ParseRawText(input);

            BuildGrid();
            UpdateInfo();
        }

        // ═══════════════════════════════════════════════════════════════════
        // PARSING — Smart detection of table format
        // ═══════════════════════════════════════════════════════════════════

        private bool IsJsonMatrix(string input)
        {
            var trimmed = input.TrimStart();
            return trimmed.StartsWith("{") && trimmed.Contains("\"text\"");
        }

        private void ParseJsonMatrix(string jsonPayload)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, CellData>>(jsonPayload);
                if (dict == null || dict.Count == 0) { ParseRawText(jsonPayload); return; }

                int maxRow = -1, maxCol = -1;
                foreach (var key in dict.Keys)
                {
                    string cleaned = key.Replace("(", "").Replace(")", "");
                    var parts = cleaned.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int r) && int.TryParse(parts[1].Trim(), out int c))
                    {
                        if (r > maxRow) maxRow = r;
                        if (c > maxCol) maxCol = c;
                    }
                }

                if (maxRow < 0 || maxCol < 0) { ParseRawText(jsonPayload); return; }

                _rows = maxRow + 1;
                _cols = maxCol + 1;
                _cells = new TextBox[_rows, _cols];
                _confidence = new double[_rows, _cols];

                for (int i = 0; i < _rows; i++)
                    for (int j = 0; j < _cols; j++)
                    {
                        string key = $"({i},{j})";
                        if (dict.ContainsKey(key))
                        {
                            _confidence[i, j] = dict[key].conf;
                        }
                        else
                        {
                            _confidence[i, j] = 1.0; // No data = neutral
                        }
                    }

                // Store values temporarily for BuildGrid
                _tempValues = new string[_rows, _cols];
                for (int i = 0; i < _rows; i++)
                    for (int j = 0; j < _cols; j++)
                    {
                        string key = $"({i},{j})";
                        _tempValues[i, j] = dict.ContainsKey(key) ? dict[key].text : "";
                    }
            }
            catch
            {
                ParseRawText(jsonPayload);
            }
        }

        private string[,] _tempValues;

        private void ParseRawText(string text)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) { _rows = 1; _cols = 1; _tempValues = new string[1, 1]; _confidence = new double[1, 1]; _confidence[0, 0] = 1.0; _tempValues[0, 0] = text; return; }

            // Detect separator: tab > pipe > comma > spaces
            char separator = DetectSeparator(lines);
            
            var parsed = new List<string[]>();
            int maxCols = 0;

            foreach (var line in lines)
            {
                string[] cells;
                if (separator == '|')
                {
                    // Pipe: |col1|col2|col3| — trim outer pipes
                    var trimmed = line.Trim().Trim('|');
                    cells = trimmed.Split('|').Select(c => c.Trim()).ToArray();
                }
                else
                {
                    cells = line.Split(separator).Select(c => c.Trim()).ToArray();
                }

                // Skip separator lines like "---+---+---" or "| --- | --- |"
                if (cells.All(c => c.All(ch => ch == '-' || ch == '+' || ch == '=' || ch == ' ')))
                    continue;

                parsed.Add(cells);
                if (cells.Length > maxCols) maxCols = cells.Length;
            }

            if (parsed.Count == 0) { _rows = 1; _cols = 1; _tempValues = new string[1, 1]; _confidence = new double[1, 1]; _confidence[0, 0] = 1.0; _tempValues[0, 0] = text; return; }

            _rows = parsed.Count;
            _cols = maxCols;
            _tempValues = new string[_rows, _cols];
            _confidence = new double[_rows, _cols];

            for (int i = 0; i < _rows; i++)
            {
                for (int j = 0; j < _cols; j++)
                {
                    _tempValues[i, j] = (j < parsed[i].Length) ? parsed[i][j] : "";
                    _confidence[i, j] = 1.0; // Raw text = full confidence
                }
            }
        }

        private char DetectSeparator(string[] lines)
        {
            // Count occurrences of each separator across all lines
            int tabs = lines.Sum(l => l.Count(c => c == '\t'));
            int pipes = lines.Sum(l => l.Count(c => c == '|'));
            int commas = lines.Sum(l => l.Count(c => c == ','));

            if (tabs > 0 && tabs >= pipes && tabs >= commas) return '\t';
            if (pipes > 0 && pipes >= commas) return '|';
            if (commas > 0) return ',';
            return '\t'; // Default
        }

        // ═══════════════════════════════════════════════════════════════════
        // GRID BUILDING — Flexible grid with row numbers
        // ═══════════════════════════════════════════════════════════════════

        private void BuildGrid()
        {
            TableGrid.Children.Clear();
            TableGrid.RowDefinitions.Clear();
            TableGrid.ColumnDefinitions.Clear();

            if (_rows == 0 || _cols == 0) return;

            // Row number column (narrow)
            TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            // Data columns (star-sized = flexible)
            for (int j = 0; j < _cols; j++)
                TableGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });

            // Column header row
            TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Empty corner cell
            var cornerCell = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(4)
            };
            cornerCell.Child = new TextBlock { Text = "#", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(cornerCell, 0);
            Grid.SetColumn(cornerCell, 0);
            TableGrid.Children.Add(cornerCell);

            // Column headers (A, B, C, ...)
            for (int j = 0; j < _cols; j++)
            {
                var header = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(25, 59, 130, 246)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                string colName = j < 26 ? ((char)('A' + j)).ToString() : $"C{j + 1}";
                header.Child = new TextBlock { Text = colName, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(200, 96, 165, 250)), HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(header, 0);
                Grid.SetColumn(header, j + 1);
                TableGrid.Children.Add(header);
            }

            _cells = new TextBox[_rows, _cols];

            // Data rows
            for (int i = 0; i < _rows; i++)
            {
                TableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 36 });

                // Row number cell
                var rowNumBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(15, 34, 197, 94)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(4)
                };
                rowNumBorder.Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, 34, 197, 94)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(rowNumBorder, i + 1);
                Grid.SetColumn(rowNumBorder, 0);
                TableGrid.Children.Add(rowNumBorder);

                // Data cells
                for (int j = 0; j < _cols; j++)
                {
                    double conf = _confidence[i, j];
                    Color bg;
                    if (conf >= 0.95) bg = Color.FromArgb(30, 16, 185, 129);
                    else if (conf >= 0.85) bg = Color.FromArgb(40, 234, 179, 8);
                    else bg = Color.FromArgb(40, 239, 68, 68);
                    if (conf >= 1.0) bg = Color.FromArgb(8, 255, 255, 255);

                    var border = new Border
                    {
                        Background = new SolidColorBrush(bg),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(2)
                    };

                    var tb = new TextBox
                    {
                        Text = _tempValues != null ? _tempValues[i, j] : "",
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        Background = Brushes.Transparent,
                        Foreground = Brushes.White,
                        BorderThickness = new Thickness(0),
                        MinHeight = 32,
                        FontSize = 13,
                        Padding = new Thickness(6, 4, 6, 4),
                        FontWeight = (i == 0) ? FontWeights.Bold : FontWeights.Normal,
                        VerticalContentAlignment = VerticalAlignment.Center
                    };

                    _cells[i, j] = tb;
                    border.Child = tb;
                    Grid.SetRow(border, i + 1);
                    Grid.SetColumn(border, j + 1);
                    TableGrid.Children.Add(border);
                }
            }

            _tempValues = null; // Free memory
        }

        private void UpdateInfo()
        {
            InfoText.Text = $"{_rows} rows × {_cols} columns";
        }

        // ═══════════════════════════════════════════════════════════════════
        // EXPORT — HTML (Word), CSV, TSV
        // ═══════════════════════════════════════════════════════════════════

        private void ExportHtml_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sbHtml = new StringBuilder();
                sbHtml.Append("<table style=\"border-collapse: collapse; width: 100%; font-family: Calibri, Arial, sans-serif; font-size: 11pt;\">\n");

                for (int i = 0; i < _rows; i++)
                {
                    sbHtml.Append("<tr>\n");
                    for (int j = 0; j < _cols; j++)
                    {
                        string cellVal = _cells[i, j].Text;
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

                // CF_HTML for Word paste
                string header = "Version:0.9\r\nStartHTML:SSSSSSSS\r\nEndHTML:EEEEEEEE\r\nStartFragment:FFFFFFFF\r\nEndFragment:GGGGGGGG\r\n";
                string htmlStart = "<html><body>\r\n<!--StartFragment-->\r\n";
                string htmlEnd = "\r\n<!--EndFragment-->\r\n</body></html>";

                int headerLen = Encoding.UTF8.GetByteCount(header);
                int htmlStartLen = Encoding.UTF8.GetByteCount(htmlStart);
                int fragmentLen = Encoding.UTF8.GetByteCount(fragment);
                int htmlEndLen = Encoding.UTF8.GetByteCount(htmlEnd);

                header = header.Replace("SSSSSSSS", headerLen.ToString("D8"));
                header = header.Replace("EEEEEEEE", (headerLen + htmlStartLen + fragmentLen + htmlEndLen).ToString("D8"));
                header = header.Replace("FFFFFFFF", (headerLen + htmlStartLen).ToString("D8"));
                header = header.Replace("GGGGGGGG", (headerLen + htmlStartLen + fragmentLen).ToString("D8"));

                string cfHtml = header + htmlStart + fragment + htmlEnd;

                // Also build TSV fallback
                string tsv = BuildTsv();

                MainWindow._isWritingClipboard = true;
                try
                {
                    var dataObj = new DataObject();
                    dataObj.SetData(DataFormats.Html, cfHtml);
                    dataObj.SetData(DataFormats.Text, tsv);
                    Clipboard.SetDataObject(dataObj, true);
                }
                finally { MainWindow._isWritingClipboard = false; }

                ToastWindow.ShowToast("Table copied! Paste into Word 📋");
            }
            catch (Exception ex)
            {
                ToastWindow.ShowToast($"Export failed: {ex.Message}");
                Logger.LogAction("TABLE_EXPORT", ex.Message);
            }
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            MainWindow._isWritingClipboard = true;
            try
            {
                Clipboard.SetText(BuildCsv());
                ToastWindow.ShowToast("Table copied as CSV 📋");
            }
            finally { MainWindow._isWritingClipboard = false; }
        }

        private void ExportTsv_Click(object sender, RoutedEventArgs e)
        {
            MainWindow._isWritingClipboard = true;
            try
            {
                Clipboard.SetText(BuildTsv());
                ToastWindow.ShowToast("Table copied as TSV 📋");
            }
            finally { MainWindow._isWritingClipboard = false; }
        }

        private string BuildCsv()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _rows; i++)
            {
                for (int j = 0; j < _cols; j++)
                {
                    if (j > 0) sb.Append(',');
                    string val = _cells[i, j].Text;
                    // Escape CSV: quote if contains comma, newline, or quote
                    if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
                        sb.Append($"\"{val.Replace("\"", "\"\"")}\"");
                    else
                        sb.Append(val);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string BuildTsv()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _rows; i++)
            {
                for (int j = 0; j < _cols; j++)
                {
                    if (j > 0) sb.Append('\t');
                    sb.Append(_cells[i, j].Text);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════
        // ROW/COLUMN MANIPULATION
        // ═══════════════════════════════════════════════════════════════════

        private void AddRow_Click(object sender, RoutedEventArgs e)
        {
            var newValues = new string[_rows + 1, _cols];
            var newConf = new double[_rows + 1, _cols];
            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols; j++)
                {
                    newValues[i, j] = _cells[i, j].Text;
                    newConf[i, j] = _confidence[i, j];
                }
            for (int j = 0; j < _cols; j++) { newValues[_rows, j] = ""; newConf[_rows, j] = 1.0; }

            _rows++;
            _tempValues = newValues;
            _confidence = newConf;
            BuildGrid();
            UpdateInfo();
        }

        private void AddCol_Click(object sender, RoutedEventArgs e)
        {
            var newValues = new string[_rows, _cols + 1];
            var newConf = new double[_rows, _cols + 1];
            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols; j++)
                {
                    newValues[i, j] = _cells[i, j].Text;
                    newConf[i, j] = _confidence[i, j];
                }
            for (int i = 0; i < _rows; i++) { newValues[i, _cols] = ""; newConf[i, _cols] = 1.0; }

            _cols++;
            _tempValues = newValues;
            _confidence = newConf;
            BuildGrid();
            UpdateInfo();
        }

        private void DelRow_Click(object sender, RoutedEventArgs e)
        {
            if (_rows <= 1) return;
            var newValues = new string[_rows - 1, _cols];
            var newConf = new double[_rows - 1, _cols];
            for (int i = 0; i < _rows - 1; i++)
                for (int j = 0; j < _cols; j++)
                {
                    newValues[i, j] = _cells[i, j].Text;
                    newConf[i, j] = _confidence[i, j];
                }

            _rows--;
            _tempValues = newValues;
            _confidence = newConf;
            BuildGrid();
            UpdateInfo();
        }

        private void DelCol_Click(object sender, RoutedEventArgs e)
        {
            if (_cols <= 1) return;
            var newValues = new string[_rows, _cols - 1];
            var newConf = new double[_rows, _cols - 1];
            for (int i = 0; i < _rows; i++)
                for (int j = 0; j < _cols - 1; j++)
                {
                    newValues[i, j] = _cells[i, j].Text;
                    newConf[i, j] = _confidence[i, j];
                }

            _cols--;
            _tempValues = newValues;
            _confidence = newConf;
            BuildGrid();
            UpdateInfo();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Grid auto-resizes via star-sized columns — no manual work needed
        }

        private class CellData
        {
            public string text { get; set; } = string.Empty;
            public double conf { get; set; } = 1.0;
        }
    }
}
