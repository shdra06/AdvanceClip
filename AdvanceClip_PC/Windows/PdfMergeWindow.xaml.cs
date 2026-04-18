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

namespace AdvanceClip.Windows
{
    public partial class PdfMergeWindow : MicaWindow
    {
        public ObservableCollection<ClipboardItem> PdfItems { get; set; }
        private DropShelfViewModel _viewModel;

        public PdfMergeWindow(List<ClipboardItem> pdfsToMerge, DropShelfViewModel vm)
        {
            InitializeComponent();
            _viewModel = vm;
            PdfItems = new ObservableCollection<ClipboardItem>(pdfsToMerge);
            PdfItemsList.ItemsSource = PdfItems;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ClipboardItem item)
            {
                int index = PdfItems.IndexOf(item);
                if (index > 0)
                {
                    PdfItems.Move(index, index - 1);
                }
            }
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ClipboardItem item)
            {
                int index = PdfItems.IndexOf(item);
                if (index >= 0 && index < PdfItems.Count - 1)
                {
                    PdfItems.Move(index, index + 1);
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (PdfItems.Count < 2)
            {
                MessageBox.Show("Please select at least 2 PDFs to merge.", "Merge Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MergeBtn.IsEnabled = false;
            MergeBtn.Content = "Merging...";

            string outputFileName = $"Merged_PDF_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), outputFileName);

            bool success = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using (PdfDocument outputDocument = new PdfDocument())
                    {
                        foreach (var item in PdfItems)
                        {
                            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath)) continue;
                            
                            using (PdfDocument inputDocument = PdfReader.Open(item.FilePath, PdfDocumentOpenMode.Import))
                            {
                                int count = inputDocument.PageCount;
                                for (int idx = 0; idx < count; idx++)
                                {
                                    PdfPage page = inputDocument.Pages[idx];
                                    outputDocument.AddPage(page);
                                }
                            }
                        }
                        outputDocument.Save(outputPath);
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
                    
                    // Directly mimic a Drop payload to Add the file
                    _viewModel.HandleDrop(dataObj, true);
                    
                    ToastWindow.ShowToast("PDFs Merged Successfully! 📄");
                });
                this.Close();
            }
            else
            {
                MergeBtn.IsEnabled = true;
                MergeBtn.Content = "Merge PDFs";
            }
        }
    }
}
