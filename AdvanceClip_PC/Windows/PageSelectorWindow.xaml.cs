using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MicaWPF.Controls;
using WpfButton = System.Windows.Controls.Button;

namespace AdvanceClip.Windows
{
    public partial class PageSelectorWindow : MicaWindow
    {
        private PdfMergeItem _item;
        private const int COLUMNS = 4;
        private WpfButton[] _pageButtons;
        private int _lastClickedPage = -1; // For shift-click range selection
        public bool Confirmed { get; private set; } = false;

        public PageSelectorWindow(PdfMergeItem item)
        {
            InitializeComponent();
            _item = item;
            HeaderText.Text = $"Select Pages — {item.FileName}";
            BuildPageGrid();
            UpdateSelectionInfo();
        }

        private void BuildPageGrid()
        {
            PageGrid.Children.Clear();
            ColumnHeaders.Children.Clear();

            if (_item.TotalPages == 0) return;

            int rows = (int)Math.Ceiling(_item.TotalPages / (double)COLUMNS);
            _pageButtons = new WpfButton[_item.TotalPages];

            // Column header buttons (C1, C2, C3, C4)
            // Add a spacer for row button column
            var rowSpacer = new Border { Width = 40, Height = 28, Margin = new Thickness(0, 0, 4, 0) };
            ColumnHeaders.Children.Add(rowSpacer);

            for (int col = 0; col < COLUMNS; col++)
            {
                int c = col;
                var colBtn = new WpfButton
                {
                    Content = $"Col {col + 1}",
                    Width = 80,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 11,
                    Background = new SolidColorBrush(Color.FromArgb(20, 59, 130, 246)),
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 59, 130, 246)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Toggle all pages in column {col + 1}"
                };
                colBtn.Click += (s, e) =>
                {
                    _item.ToggleColumn(c, COLUMNS);
                    RefreshAllButtons();
                    UpdateSelectionInfo();
                };
                ColumnHeaders.Children.Add(colBtn);
            }

            // Build page grid with row buttons
            for (int row = 0; row < rows; row++)
            {
                int r = row;
                // Row toggle button
                var rowBtn = new WpfButton
                {
                    Content = $"R{row + 1}",
                    Width = 40,
                    Height = 74,
                    Margin = new Thickness(0, 0, 4, 4),
                    FontSize = 11,
                    Background = new SolidColorBrush(Color.FromArgb(20, 34, 197, 94)),
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 34, 197, 94)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 34, 197, 94)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = $"Toggle all pages in row {row + 1}"
                };
                rowBtn.Click += (s, e) =>
                {
                    _item.ToggleRow(r, COLUMNS);
                    RefreshAllButtons();
                    UpdateSelectionInfo();
                };
                PageGrid.Children.Add(rowBtn);

                // Page buttons for this row
                for (int col = 0; col < COLUMNS; col++)
                {
                    int pageNum = row * COLUMNS + col + 1; // 1-indexed
                    if (pageNum > _item.TotalPages)
                    {
                        // Empty placeholder
                        var spacer = new Border { Width = 80, Height = 74, Margin = new Thickness(0, 0, 4, 4) };
                        PageGrid.Children.Add(spacer);
                        continue;
                    }

                    int pn = pageNum;
                    bool selected = _item.PageSelected[pageNum - 1];

                    var btn = new WpfButton
                    {
                        Width = 80,
                        Height = 74,
                        Margin = new Thickness(0, 0, 4, 4),
                        FontSize = 16,
                        FontWeight = FontWeights.SemiBold,
                        Cursor = Cursors.Hand,
                        Tag = pn,
                        ToolTip = $"Page {pn}"
                    };
                    
                    // Stack: page number + small label
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    var numText = new TextBlock { Text = pn.ToString(), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
                    var labelText = new TextBlock { Text = "page", FontSize = 9, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center };
                    stack.Children.Add(numText);
                    stack.Children.Add(labelText);
                    btn.Content = stack;

                    ApplyButtonStyle(btn, selected);

                    btn.Click += (s, e) =>
                    {
                        // Shift-click for range selection
                        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                        {
                            if (_lastClickedPage > 0)
                            {
                                int from = Math.Min(_lastClickedPage, pn);
                                int to = Math.Max(_lastClickedPage, pn);
                                bool targetState = !_item.PageSelected[pn - 1];
                                for (int p = from; p <= to; p++)
                                {
                                    if (_item.PageSelected[p - 1] != targetState)
                                        _item.TogglePage(p);
                                }
                                RefreshAllButtons();
                                UpdateSelectionInfo();
                                _lastClickedPage = pn;
                                return;
                            }
                        }

                        _item.TogglePage(pn);
                        _lastClickedPage = pn;
                        ApplyButtonStyle(btn, _item.PageSelected[pn - 1]);
                        UpdateSelectionInfo();
                    };

                    _pageButtons[pageNum - 1] = btn;
                    PageGrid.Children.Add(btn);
                }
            }
        }

        private void ApplyButtonStyle(WpfButton btn, bool selected)
        {
            if (selected)
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246));
                btn.Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250));
                btn.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 59, 130, 246));
                btn.BorderThickness = new Thickness(2);
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
                btn.Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                btn.BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
                btn.BorderThickness = new Thickness(1);
            }
        }

        private void RefreshAllButtons()
        {
            if (_pageButtons == null) return;
            for (int i = 0; i < _pageButtons.Length; i++)
            {
                if (_pageButtons[i] != null)
                    ApplyButtonStyle(_pageButtons[i], _item.PageSelected[i]);
            }
        }

        private void UpdateSelectionInfo()
        {
            int selected = _item.PageSelected.Count(p => p);
            SelectionInfo.Text = $"{selected} of {_item.TotalPages} pages selected";
            RangeInput.Text = _item.PageRangeText;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _item.SelectAll();
            RefreshAllButtons();
            UpdateSelectionInfo();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            _item.DeselectAll();
            RefreshAllButtons();
            UpdateSelectionInfo();
        }

        private void SelectOdd_Click(object sender, RoutedEventArgs e)
        {
            _item.DeselectAll();
            for (int p = 1; p <= _item.TotalPages; p += 2)
                _item.TogglePage(p);
            RefreshAllButtons();
            UpdateSelectionInfo();
        }

        private void SelectEven_Click(object sender, RoutedEventArgs e)
        {
            _item.DeselectAll();
            for (int p = 2; p <= _item.TotalPages; p += 2)
                _item.TogglePage(p);
            RefreshAllButtons();
            UpdateSelectionInfo();
        }

        private void ApplyRange_Click(object sender, RoutedEventArgs e)
        {
            if (!_item.SetPageRange(RangeInput.Text))
            {
                MessageBox.Show("Invalid range format. Use: 1-5, 8, 10-12", "Invalid Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            RefreshAllButtons();
            UpdateSelectionInfo();
        }

        private void RangeInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyRange_Click(sender, e);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            this.Close();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            this.Close();
        }
    }
}
