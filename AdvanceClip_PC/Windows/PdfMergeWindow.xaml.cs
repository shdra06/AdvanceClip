using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
