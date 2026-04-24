using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdvanceClip.ViewModels;
using MicaWPF.Controls;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Microsoft.Win32;

namespace AdvanceClip.Windows
{
    public partial class PdfMergeWindow : MicaWindow
    {
        public ObservableCollection<PdfMergeItem> MergeItems { get; set; }
        private FlyShelfViewModel _viewModel;
        private Point _dragStartPoint;
        private PdfMergeItem? _draggedItem = null;

        public PdfMergeWindow(List<ClipboardItem> pdfsToMerge, FlyShelfViewModel vm)
        {
            InitializeComponent();
            _viewModel = vm;
            MergeItems = new ObservableCollection<PdfMergeItem>(
                pdfsToMerge.Select(p => new PdfMergeItem(p.FilePath))
            );
            PdfItemsList.ItemsSource = MergeItems;
            OutputNameBox.Text = $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}";
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int totalFiles = MergeItems.Count;
            int validFiles = MergeItems.Count(m => m.IsValid);
            int totalPages = MergeItems.Where(m => m.IsValid).Sum(m => m.GetSelectedPageIndices().Count);
            int totalAllPages = MergeItems.Where(m => m.IsValid).Sum(m => m.TotalPages);

            SummaryText.Text = totalPages == totalAllPages
                ? $"📄 {validFiles} PDFs • {totalPages} total pages to merge"
                : $"📄 {validFiles} PDFs • {totalPages} of {totalAllPages} pages selected";

            if (totalFiles > validFiles)
                SummaryText.Text += $" • ⚠ {totalFiles - validFiles} unreadable";
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfMergeItem item)
            {
                int index = MergeItems.IndexOf(item);
                if (index > 0)
                {
                    MergeItems.Move(index, index - 1);
                }
            }
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfMergeItem item)
            {
                int index = MergeItems.IndexOf(item);
                if (index >= 0 && index < MergeItems.Count - 1)
                {
                    MergeItems.Move(index, index + 1);
                }
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfMergeItem item)
            {
                MergeItems.Remove(item);
                UpdateSummary();
            }
        }

        private void Reverse_Click(object sender, RoutedEventArgs e)
        {
            var reversed = MergeItems.Reverse().ToList();
            MergeItems.Clear();
            foreach (var item in reversed) MergeItems.Add(item);
        }

        private void SelectPages_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PdfMergeItem item)
            {
                if (!item.IsValid)
                {
                    MessageBox.Show($"Cannot open this PDF:\n{item.Error}", "PDF Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var selector = new PageSelectorWindow(item);
                selector.Owner = this;
                selector.ShowDialog();
                
                // Refresh the list to show updated page selection
                PdfItemsList.Items.Refresh();
                UpdateSummary();
            }
        }

        private void AddPdf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "PDF & Word Files|*.pdf;*.docx;*.doc|PDF Files|*.pdf|Word Files|*.docx;*.doc",
                Multiselect = true,
                Title = "Add Files to Merge"
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (string file in dlg.FileNames)
                {
                    MergeItems.Add(new PdfMergeItem(file));
                }
                UpdateSummary();
            }
        }

        // ══════════════════════════════════════════
        // DRAG-DROP REORDER
        // ══════════════════════════════════════════

        private void PdfList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);

            // Find the data item under cursor
            var element = e.OriginalSource as DependencyObject;
            var listBoxItem = FindAncestor<ListBoxItem>(element);
            if (listBoxItem != null)
                _draggedItem = listBoxItem.DataContext as PdfMergeItem;
            else
                _draggedItem = null;
        }

        private void PdfList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null) return;

            Point currentPos = e.GetPosition(null);
            Vector diff = _dragStartPoint - currentPos;

            // Only start drag after a minimum threshold (prevents accidental drags)
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var data = new DataObject("PdfMergeItem", _draggedItem);
                DragDrop.DoDragDrop(PdfItemsList, data, DragDropEffects.Move);
                _draggedItem = null;
            }
        }

        private void PdfList_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PdfMergeItem"))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Highlight the drop target
            var element = e.OriginalSource as DependencyObject;
            var listBoxItem = FindAncestor<ListBoxItem>(element);
            
            // Clear all drop indicators
            foreach (var item in PdfItemsList.Items)
            {
                var container = PdfItemsList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                if (container != null)
                    container.Tag = null;
            }

            // Set drop indicator on target
            if (listBoxItem != null)
                listBoxItem.Tag = "DropTarget";
        }

        private void PdfList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("PdfMergeItem")) return;

            var droppedItem = e.Data.GetData("PdfMergeItem") as PdfMergeItem;
            if (droppedItem == null) return;

            // Find target item
            var element = e.OriginalSource as DependencyObject;
            var targetListBoxItem = FindAncestor<ListBoxItem>(element);
            if (targetListBoxItem == null) return;

            var targetItem = targetListBoxItem.DataContext as PdfMergeItem;
            if (targetItem == null || targetItem == droppedItem) return;

            int oldIndex = MergeItems.IndexOf(droppedItem);
            int newIndex = MergeItems.IndexOf(targetItem);

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                MergeItems.Move(oldIndex, newIndex);
                PdfItemsList.SelectedItem = droppedItem;
                UpdateSummary();
            }

            // Clear all drop indicators
            foreach (var item in PdfItemsList.Items)
            {
                var container = PdfItemsList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                if (container != null)
                    container.Tag = null;
            }

            e.Handled = true;
        }

        private void PdfList_DragLeave(object sender, DragEventArgs e)
        {
            // Clear all drop indicators when mouse leaves
            foreach (var item in PdfItemsList.Items)
            {
                var container = PdfItemsList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                if (container != null)
                    container.Tag = null;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var validItems = MergeItems.Where(m => m.IsValid).ToList();
            int totalSelectedPages = validItems.Sum(m => m.GetSelectedPageIndices().Count);

            if (totalSelectedPages < 1)
            {
                MessageBox.Show("No pages selected to merge.", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string defaultName = OutputNameBox.Text.Trim();
            if (string.IsNullOrEmpty(defaultName)) defaultName = $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}";
            if (!defaultName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) defaultName += ".pdf";

            var dlg = new SaveFileDialog
            {
                Filter = "PDF Files|*.pdf",
                FileName = defaultName,
                Title = "Save Merged PDF As",
                DefaultExt = ".pdf"
            };

            if (dlg.ShowDialog() == true)
            {
                DoMerge(dlg.FileName);
            }
        }

        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            var validItems = MergeItems.Where(m => m.IsValid).ToList();
            int totalSelectedPages = validItems.Sum(m => m.GetSelectedPageIndices().Count);

            if (totalSelectedPages < 1)
            {
                MessageBox.Show("No pages selected to merge.", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string userFileName = OutputNameBox.Text.Trim();
            if (string.IsNullOrEmpty(userFileName)) userFileName = $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}";
            if (!userFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) userFileName += ".pdf";
            string mergeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdvanceClip", "Merged");
            Directory.CreateDirectory(mergeDir);
            string outputPath = Path.Combine(mergeDir, userFileName);

            DoMerge(outputPath);
        }

        private async void DoMerge(string outputPath)
        {
            var validItems = MergeItems.Where(m => m.IsValid).ToList();

            MergeBtn.IsEnabled = false;
            SaveAsBtn.IsEnabled = false;
            MergeBtn.Content = "Merging...";

            bool success = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (PdfDocument outputDocument = new PdfDocument())
                    {
                        int mergedPages = 0;
                        var failedFiles = new List<string>();

                        foreach (var item in validItems)
                        {
                            try
                            {
                                var pageIndices = item.GetSelectedPageIndices();
                                if (pageIndices.Count == 0) continue;

                                using (PdfDocument inputDocument = PdfReader.Open(item.MergePath, PdfDocumentOpenMode.Import))
                                {
                                    foreach (int idx in pageIndices)
                                    {
                                        if (idx >= 0 && idx < inputDocument.PageCount)
                                        {
                                            PdfPage page = inputDocument.Pages[idx];
                                            outputDocument.AddPage(page);
                                            mergedPages++;
                                        }
                                    }
                                }
                            }
                            catch (Exception fileEx)
                            {
                                failedFiles.Add($"{item.FileName}: {fileEx.Message}");
                                AdvanceClip.Classes.Logger.LogAction("PDF MERGE", $"Skipped '{item.FileName}': {fileEx.Message}");
                            }
                        }

                        if (mergedPages == 0)
                        {
                            string allErrors = failedFiles.Count > 0 
                                ? string.Join("\n", failedFiles) 
                                : "No valid pages found.";
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show($"Could not merge:\n\n{allErrors}", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return false;
                        }

                        outputDocument.Save(outputPath);

                        if (failedFiles.Count > 0)
                        {
                            string skipped = string.Join("\n", failedFiles);
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show($"Merged {mergedPages} pages. Some files were skipped:\n\n{skipped}", "Partial Merge", MessageBoxButton.OK, MessageBoxImage.Warning);
                            });
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Error merging PDFs: {ex.Message}", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return false;
                }
            });

            if (success && File.Exists(outputPath))
            {
                // Put the merged PDF back into AdvanceClip queue
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dataObj = new DataObject();
                    dataObj.SetData(DataFormats.FileDrop, new string[] { outputPath });
                    _viewModel.HandleDrop(dataObj, true);
                    ToastWindow.ShowToast("PDFs Merged Successfully! 📄");
                });
                this.Close();
            }
            else
            {
                MergeBtn.IsEnabled = true;
                SaveAsBtn.IsEnabled = true;
                MergeBtn.Content = "Merge PDFs";
            }
        }
    }
}
