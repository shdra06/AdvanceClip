using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace AdvanceClip.Windows
{
    /// <summary>
    /// Wraps a PDF file for the merge window — holds page count, selected pages, and display info.
    /// </summary>
    public class PdfMergeItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string FileSize { get; set; }
        public int TotalPages { get; private set; }
        public string Error { get; private set; }
        public bool IsValid => string.IsNullOrEmpty(Error);
        
        // Which pages are selected (1-indexed). null = all pages
        private HashSet<int> _selectedPages;
        
        public string PageRangeText
        {
            get
            {
                if (_selectedPages == null || _selectedPages.Count == TotalPages)
                    return "All";
                if (_selectedPages.Count == 0)
                    return "None";
                return FormatPageRange(_selectedPages);
            }
        }

        public string PageInfo => IsValid 
            ? $"{TotalPages} pages • {PageRangeText} selected" 
            : $"⚠ {Error}";

        // For the visual grid — which pages are toggled
        private bool[] _pageSelected;
        public bool[] PageSelected => _pageSelected;

        public bool IsWordFile { get; private set; }
        // The actual PDF path used for merging (may be a temp-converted file for Word docs)
        public string MergePath => _convertedPdfPath ?? FilePath;
        private string _convertedPdfPath;

        public PdfMergeItem(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            
            string ext = Path.GetExtension(filePath).ToLower();
            IsWordFile = ext == ".docx" || ext == ".doc";

            try
            {
                var fi = new FileInfo(filePath);
                FileSize = fi.Length > 1_048_576 
                    ? $"{fi.Length / 1_048_576.0:F1} MB" 
                    : $"{fi.Length / 1024.0:F0} KB";
            }
            catch { FileSize = ""; }

            LoadPageCount();
        }

        private void LoadPageCount()
        {
            try
            {
                string pdfPath = FilePath;

                // Convert Word files to PDF first
                if (IsWordFile)
                {
                    pdfPath = ConvertWordToPdf(FilePath);
                    _convertedPdfPath = pdfPath;
                }

                using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
                {
                    TotalPages = doc.PageCount;
                }
                _pageSelected = new bool[TotalPages];
                for (int i = 0; i < TotalPages; i++) _pageSelected[i] = true;
                _selectedPages = null;
                Error = null;
            }
            catch (Exception ex)
            {
                TotalPages = 0;
                _pageSelected = new bool[0];
                Error = ex.Message.Length > 60 ? ex.Message.Substring(0, 60) + "..." : ex.Message;
            }
        }

        private static string ConvertWordToPdf(string wordPath)
        {
            string tempPdf = Path.Combine(Path.GetTempPath(), "AdvanceClip_Merge", Path.GetFileNameWithoutExtension(wordPath) + ".pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPdf));

            dynamic wordApp = null;
            dynamic doc = null;
            try
            {
                var wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null) throw new Exception("Microsoft Word not installed");
                
                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = false;
                wordApp.DisplayAlerts = 0; // wdAlertsNone
                
                doc = wordApp.Documents.Open(wordPath, ReadOnly: true);
                doc.SaveAs2(tempPdf, 17); // wdFormatPDF = 17
                doc.Close(false);
                doc = null;
            }
            finally
            {
                if (doc != null) try { doc.Close(false); } catch { }
                if (wordApp != null) try { wordApp.Quit(false); } catch { }
            }
            return tempPdf;
        }

        /// <summary>
        /// Returns the 0-indexed page indices to include in the merge.
        /// </summary>
        public List<int> GetSelectedPageIndices()
        {
            if (_selectedPages == null)
            {
                // All pages
                return Enumerable.Range(0, TotalPages).ToList();
            }
            return _selectedPages.OrderBy(p => p).Select(p => p - 1).ToList(); // Convert 1-indexed to 0-indexed
        }

        /// <summary>
        /// Toggle a specific page (1-indexed).
        /// </summary>
        public void TogglePage(int pageNum)
        {
            EnsureSelectedPagesInitialized();
            if (_selectedPages.Contains(pageNum))
            {
                _selectedPages.Remove(pageNum);
                _pageSelected[pageNum - 1] = false;
            }
            else
            {
                _selectedPages.Add(pageNum);
                _pageSelected[pageNum - 1] = true;
            }
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Select all pages.
        /// </summary>
        public void SelectAll()
        {
            _selectedPages = null;
            for (int i = 0; i < TotalPages; i++) _pageSelected[i] = true;
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Deselect all pages.
        /// </summary>
        public void DeselectAll()
        {
            EnsureSelectedPagesInitialized();
            _selectedPages.Clear();
            for (int i = 0; i < TotalPages; i++) _pageSelected[i] = false;
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Set pages from a range string like "1-5, 8, 10-12"
        /// </summary>
        public bool SetPageRange(string rangeText)
        {
            if (string.IsNullOrWhiteSpace(rangeText) || rangeText.Trim().ToLower() == "all")
            {
                SelectAll();
                return true;
            }

            var pages = new HashSet<int>();
            try
            {
                foreach (var part in rangeText.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Contains('-'))
                    {
                        var bounds = trimmed.Split('-');
                        int start = int.Parse(bounds[0].Trim());
                        int end = int.Parse(bounds[1].Trim());
                        for (int i = start; i <= end && i <= TotalPages; i++)
                            if (i >= 1) pages.Add(i);
                    }
                    else
                    {
                        int p = int.Parse(trimmed);
                        if (p >= 1 && p <= TotalPages) pages.Add(p);
                    }
                }

                _selectedPages = pages;
                for (int i = 0; i < TotalPages; i++)
                    _pageSelected[i] = pages.Contains(i + 1);
                
                OnPropertyChanged(nameof(PageRangeText));
                OnPropertyChanged(nameof(PageInfo));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Toggle an entire row in a grid with given column count.
        /// </summary>
        public void ToggleRow(int row, int columns)
        {
            EnsureSelectedPagesInitialized();
            int start = row * columns + 1;
            int end = Math.Min(start + columns - 1, TotalPages);
            
            // If all pages in row are selected, deselect them; otherwise select all
            bool allSelected = true;
            for (int p = start; p <= end; p++)
                if (!_selectedPages.Contains(p)) { allSelected = false; break; }

            for (int p = start; p <= end; p++)
            {
                if (allSelected)
                {
                    _selectedPages.Remove(p);
                    _pageSelected[p - 1] = false;
                }
                else
                {
                    _selectedPages.Add(p);
                    _pageSelected[p - 1] = true;
                }
            }
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        /// <summary>
        /// Toggle an entire column in a grid with given column count.
        /// </summary>
        public void ToggleColumn(int col, int columns)
        {
            EnsureSelectedPagesInitialized();
            var pagesInCol = new List<int>();
            for (int p = col + 1; p <= TotalPages; p += columns)
                pagesInCol.Add(p);

            bool allSelected = pagesInCol.All(p => _selectedPages.Contains(p));
            foreach (int p in pagesInCol)
            {
                if (allSelected)
                {
                    _selectedPages.Remove(p);
                    _pageSelected[p - 1] = false;
                }
                else
                {
                    _selectedPages.Add(p);
                    _pageSelected[p - 1] = true;
                }
            }
            OnPropertyChanged(nameof(PageRangeText));
            OnPropertyChanged(nameof(PageInfo));
        }

        private void EnsureSelectedPagesInitialized()
        {
            if (_selectedPages == null)
            {
                _selectedPages = new HashSet<int>(Enumerable.Range(1, TotalPages));
            }
        }

        private static string FormatPageRange(HashSet<int> pages)
        {
            if (pages.Count == 0) return "None";
            var sorted = pages.OrderBy(p => p).ToList();
            var ranges = new List<string>();
            int start = sorted[0], end = sorted[0];
            
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] == end + 1)
                {
                    end = sorted[i];
                }
                else
                {
                    ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
                    start = end = sorted[i];
                }
            }
            ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
            
            string result = string.Join(", ", ranges);
            return result.Length > 30 ? result.Substring(0, 27) + "..." : result;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
