using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AdvanceClip.ViewModels
{
    public enum ClipboardItemType
    {
        File,
        Url,
        Text,
        Image,
        Code,
        Document,
        Archive,
        Video,
        Audio,
        Presentation,
        QRCode,
        Pdf,
        Folder
    }

    public class ClipboardItem : INotifyPropertyChanged
    {
        public DateTime DateCopied { get; set; } = DateTime.Now;
        public string FilePath { get; set; } = string.Empty;
        
        /// <summary>
        /// For Folder items: path to the auto-generated temp zip for transfer.
        /// </summary>
        public string ZippedArchivePath { get; set; } = string.Empty;

        private string _fileName = string.Empty;
        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
                }
            }
        }

        public string Extension { get; set; } = string.Empty;
        
        public string AssociatedContextTitle { get; set; } = string.Empty;

        public string SourceDeviceName { get; set; } = "Local";
        public string SourceDeviceType { get; set; } = "PC";
        public string TransferMethod { get; set; } = "Local"; // Local, LAN, Cloud, Cloudflare, ForceSend

        /// <summary>
        /// Computed display badge combining transfer method emoji + device name.
        /// Used in XAML for the transfer badge overlay.
        /// </summary>
        public string TransferBadge
        {
            get
            {
                string emoji = TransferMethod switch
                {
                    "LAN" => "📡",
                    "Cloud" => "☁️",
                    "Cloudflare" => "🌐",
                    "ForceSend" => "🎯",
                    _ => "📋"
                };
                string deviceEmoji = SourceDeviceType switch
                {
                    "Mobile" => "📱",
                    "PC" => "💻",
                    _ => ""
                };
                if (SourceDeviceName == "Local") return $"{emoji} Local";
                return $"{deviceEmoji} {SourceDeviceName} · {emoji} {TransferMethod}";
            }
        }
        public bool HasTransferBadge => SourceDeviceName != "Local";

        /// <summary>
        /// Creates a lightweight copy for Firebase sync, overriding RawContent with a download URL
        /// without mutating the original item displayed in the FlyShelf.
        /// </summary>
        public ClipboardItem CloneForSync(string downloadUrl)
        {
            return new ClipboardItem
            {
                DateCopied = this.DateCopied,
                FilePath = this.FilePath,
                FileName = this.FileName,
                Extension = this.Extension,
                ItemType = this.ItemType,
                FormattedSize = this.FormattedSize,
                RawContent = downloadUrl, // Override Raw with the download URL for remote sync
                SourceDeviceName = this.SourceDeviceName,
                SourceDeviceType = this.SourceDeviceType,
                TransferMethod = this.TransferMethod
            };
        }

        private BitmapImage? _icon;
        
        [JsonIgnore]
        public BitmapImage? Icon
        {
            get => _icon;
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                }
            }
        }
        private string _formattedSize = string.Empty;
        public string FormattedSize 
        { 
            get => _formattedSize; 
            set 
            { 
                if (_formattedSize != value) 
                {
                    _formattedSize = value; 
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize))); 
                }
            } 
        }
        
        // Universal Support enhancements
        public ClipboardItemType ItemType { get; set; } = ClipboardItemType.File;

        private string _rawContent = string.Empty;
        public string RawContent
        {
            get => _rawContent;
            set
            {
                if (_rawContent != value)
                {
                    _rawContent = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawContent)));
                }
            }
        }
        public bool IsImagePreview => ItemType == ClipboardItemType.Image || ItemType == ClipboardItemType.QRCode;
        public bool IsGifPreview => IsImagePreview && !string.IsNullOrEmpty(FilePath) && FilePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        public bool IsStaticImagePreview => IsImagePreview && !IsGifPreview;
        public string GifFilePath => IsGifPreview ? FilePath : "";
        public bool IsDocPreview => ItemType == ClipboardItemType.Document && (Extension == ".DOCX" || Extension == ".DOC" || Extension == ".TXT");
        public bool IsPdfPreview => ItemType == ClipboardItemType.Pdf;
        public bool IsUrlPreview => ItemType == ClipboardItemType.Url;
        public bool IsCodePreview => ItemType == ClipboardItemType.Code;
        public bool IsShareablePreview => true;
        
        // Context Menu Discriminators
        public bool IsTerminalPreview => Extension == ".BAT" || Extension == ".CMD" || Extension == ".PS1";
        public bool IsCPlusPlusPreview => Extension == ".CPP" || Extension == ".C";
        public string FormatIdentifier 
        { 
            get 
            {
                if (ItemType == ClipboardItemType.Image) return "Image/Bitmap";
                if (ItemType == ClipboardItemType.Text) return "Raw Text";
                if (ItemType == ClipboardItemType.Code) return "Code Snippet";
                if (ItemType == ClipboardItemType.Folder) return "Folder";
                if (ItemType == ClipboardItemType.Archive) return "Archive";
                return string.IsNullOrEmpty(Extension) ? "Unknown File" : Extension + " Object";
            }
        }
        
        private bool _isPinned;
        public bool IsPinned 
        {
            get => _isPinned;
            set
            {
                if (_isPinned != value)
                {
                    _isPinned = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
                }
            }
        }
        
        private bool _isSelected;
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public bool IsQRCodePreview => ItemType == ClipboardItemType.QRCode;
        public bool IsVideoPreview => ItemType == ClipboardItemType.Video;
        public bool IsArchivePreview => ItemType == ClipboardItemType.Archive;
        public bool IsFolderPreview => ItemType == ClipboardItemType.Folder;
        public bool IsTextPreview => ItemType == ClipboardItemType.Text;

        private bool _isSuggestedContext;
        [JsonIgnore]
        public bool IsSuggestedContext
        {
            get => _isSuggestedContext;
            set
            {
                if (_isSuggestedContext != value)
                {
                    _isSuggestedContext = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSuggestedContext)));
                }
            }
        }

        // --- P2P FILE TRANSFER PROGRESS ---
        private double _transferProgress;
        [JsonIgnore]
        public double TransferProgress 
        { 
            get => _transferProgress; 
            set { if(_transferProgress!=value){ _transferProgress=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferProgress))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTransferring))); } } 
        }
        
        private string _transferStatusText = "";
        [JsonIgnore]
        public string TransferStatusText 
        { 
            get => _transferStatusText; 
            set { if(_transferStatusText!=value){ _transferStatusText=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferStatusText))); } } 
        }
        
        [JsonIgnore]
        public bool IsTransferring => _transferProgress > 0 && _transferProgress < 100;
        
        // --- SMART CHIPS PROPERTIES ---
        private bool _hasSmartAction;
        public bool HasSmartAction { get => _hasSmartAction; set { if(_hasSmartAction!=value){_hasSmartAction=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSmartAction)));} } }
        
        private string _smartActionName = "";
        public string SmartActionName { get => _smartActionName; set { if(_smartActionName!=value){_smartActionName=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartActionName)));} } }
        
        private string _smartActionIcon = "Play24";
        public string SmartActionIcon { get => _smartActionIcon; set { if(_smartActionIcon!=value){_smartActionIcon=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartActionIcon)));} } }
        
        private string _smartActionType = "";
        public string SmartActionType { get => _smartActionType; set { if(_smartActionType!=value){_smartActionType=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SmartActionType)));} } }

        // --- MATH SOLVER PROPERTIES ---
        private string _mathResult = "";
        public string MathResult { get => _mathResult; set { if(_mathResult!=value){_mathResult=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MathResult))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMathResult)));} } }
        public bool HasMathResult => !string.IsNullOrEmpty(_mathResult);

        private bool _isPlottable;
        public bool IsPlottable { get => _isPlottable; set { if(_isPlottable!=value){_isPlottable=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlottable)));} } }

        private string _plotEquation = "";
        [JsonIgnore]
        public string PlotEquation { get => _plotEquation; set { _plotEquation = value; } }

        // --- COLOR DETECTION PROPERTIES ---
        private string _detectedColor = "";
        public string DetectedColor { get => _detectedColor; set { if(_detectedColor!=value){_detectedColor=value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedColor))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetectedColor))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedColorBrush)));} } }
        public bool HasDetectedColor => !string.IsNullOrEmpty(_detectedColor);

        private byte _colorR, _colorG, _colorB;
        [JsonIgnore] public byte ColorR => _colorR;
        [JsonIgnore] public byte ColorG => _colorG;
        [JsonIgnore] public byte ColorB => _colorB;

        [JsonIgnore]
        public System.Windows.Media.SolidColorBrush DetectedColorBrush => HasDetectedColor ? AdvanceClip.Classes.ColorHelper.ToBrush(_detectedColor) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);

        public void EvaluateSmartActions()
        {
            HasSmartAction = false;
            
            if (ItemType == ClipboardItemType.Pdf)
            {
                SmartActionName = "Open PDF";
                SmartActionIcon = "Eye24";
                SmartActionType = "OpenPDF";
                HasSmartAction = true;
            }
            else if (ItemType == ClipboardItemType.Document)
            {
                if (Extension == ".DOCX" || Extension == ".DOC")
                {
                    SmartActionName = "Convert to PDF";
                    SmartActionIcon = "DocumentPdf24";
                    SmartActionType = "ConvertToPdf";
                    HasSmartAction = true;
                }
            }
            else if (ItemType == ClipboardItemType.Url || (!string.IsNullOrEmpty(RawContent) && RawContent.StartsWith("http")))
            {
                string r = RawContent.ToLower();
                if (r.Contains("zoom.us/j/") || r.Contains("meet.google.com/"))
                {
                    SmartActionName = "Join Meeting";
                    SmartActionIcon = "Video24";
                    SmartActionType = "JoinMeeting";
                }
                else
                {
                    SmartActionName = "Navigate QR Link";
                    SmartActionIcon = "QRCode24";
                    SmartActionType = "OpenBrowser";
                }
                HasSmartAction = true;
            }
            else if (ItemType == ClipboardItemType.QRCode)
            {
                if (!string.IsNullOrEmpty(RawContent) && RawContent.ToLower().StartsWith("http"))
                {
                    SmartActionName = "Open QR Link";
                    SmartActionIcon = "Globe24";
                    SmartActionType = "OpenBrowser";
                }
                else
                {
                    SmartActionName = "Copy QR Text";
                    SmartActionIcon = "Copy24";
                    SmartActionType = "CopyQRText";
                }
                HasSmartAction = true;
            }
            else if ((ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code) && !string.IsNullOrEmpty(RawContent))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(RawContent, @"(#include\s*<[a-z.]+>|int\s+main\s*\()"))
                {
                    SmartActionName = "Run C/C++";
                    SmartActionIcon = "Play24";
                    SmartActionType = "CompileAndRun";
                    HasSmartAction = true;
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(RawContent, @"\b(?:[01]?\d|2[0-3]):[0-5]\d\b") || 
                    System.Text.RegularExpressions.Regex.IsMatch(RawContent.ToLower(), @"\d+\s*(sec|min|hour|hr|minute|second)s?\b") ||
                    System.Text.RegularExpressions.Regex.IsMatch(RawContent.Trim(), @"^\/\d+$"))
                {
                    SmartActionName = "Set Timer";
                    SmartActionIcon = "Clock24";
                    SmartActionType = "SetTimer";
                    HasSmartAction = true;
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(RawContent.ToLower(), @"\d{1,5}\s+\w+\s+(st|street|ave|avenue|blvd|boulevard|rd|road|dr|drive|lane|ln)\b"))
                {
                    SmartActionName = "Open Maps";
                    SmartActionIcon = "Map24";
                    SmartActionType = "OpenMap";
                    HasSmartAction = true;
                }
            }

            // ═══ MATH SOLVER (independent of smart action — always evaluate) ═══
            if (!string.IsNullOrEmpty(RawContent) && AdvanceClip.Classes.SettingsManager.Current.EnableMathSolver)
            {
                // Check for plottable equations (contains x variable)
                if (AdvanceClip.Classes.MathSolver.IsPlottableEquation(RawContent))
                {
                    IsPlottable = true;
                    PlotEquation = RawContent;
                    SmartActionName = "📊 Plot Graph";
                    SmartActionIcon = "DataLine24";
                    SmartActionType = "PlotGraph";
                    HasSmartAction = true;
                }
                // Try to solve simple expressions
                else if (AdvanceClip.Classes.MathSolver.TrySolveExpression(RawContent, out double mathResult))
                {
                    // Format nicely: integers as integers, decimals with reasonable precision
                    MathResult = mathResult == Math.Floor(mathResult) && Math.Abs(mathResult) < 1e15
                        ? $"= {(long)mathResult}" 
                        : $"= {mathResult:G10}";
                }
            }

            // ═══ COLOR DETECTION (always evaluate) ═══
            if (!string.IsNullOrEmpty(RawContent))
            {
                if (AdvanceClip.Classes.ColorHelper.TryDetectColor(RawContent, out string hex, out byte cr, out byte cg, out byte cb))
                {
                    DetectedColor = hex;
                    _colorR = cr;
                    _colorG = cg;
                    _colorB = cb;
                }
            }
        }

        
        // Default constructor for standard objects
        public ClipboardItem() { }

        public ClipboardItem(string path)
        {
            FilePath = path;
            FileName = Path.GetFileName(path);
            Extension = Path.GetExtension(path)?.ToUpperInvariant() ?? "FILE";
            
            try
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Exists)
                {
                    FormattedSize = FormatBytes(fileInfo.Length);
                    // Classify obvious extensions
                    string ext = Extension.ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp")
                    {
                        ItemType = ClipboardItemType.Image;
                        ScanForQRCodeAsync(path);
                    }
                    else if (ext == ".pdf")
                    {
                        ItemType = ClipboardItemType.Pdf;
                    }
                    else if (ext == ".doc" || ext == ".docx" || ext == ".txt")
                    {
                        ItemType = ClipboardItemType.Document;
                    }
                    else if (ext == ".cpp" || ext == ".c" || ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".js" || ext == ".py" || ext == ".cs")
                    {
                        ItemType = ClipboardItemType.Code;
                    }
                    else if (ext == ".ppt" || ext == ".pptx")
                    {
                        ItemType = ClipboardItemType.Presentation;
                    }
                    else if (ext == ".zip" || ext == ".rar" || ext == ".7z" || ext == ".tar" || ext == ".gz" || ext == ".apk")
                    {
                        ItemType = ClipboardItemType.Archive;
                        // List archive contents for .zip files
                        if (ext == ".zip" || ext == ".apk")
                        {
                            try
                            {
                                using var archive = ZipFile.OpenRead(path);
                                var entries = archive.Entries
                                    .Where(e => !string.IsNullOrEmpty(e.Name))
                                    .Take(50)
                                    .ToList();
                                var listing = new System.Text.StringBuilder();
                                listing.AppendLine($"📦 {entries.Count} file(s) in archive:");
                                long totalSize = 0;
                                foreach (var entry in entries)
                                {
                                    string entrySize = entry.Length > 0 ? $" ({FormatBytes(entry.Length)})" : "";
                                    listing.AppendLine($"  • {entry.FullName}{entrySize}");
                                    totalSize += entry.Length;
                                }
                                if (archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name)) > 50)
                                    listing.AppendLine($"  ... and {archive.Entries.Count(e => !string.IsNullOrEmpty(e.Name)) - 50} more");
                                listing.AppendLine($"\nTotal uncompressed: {FormatBytes(totalSize)}");
                                RawContent = listing.ToString();
                            }
                            catch { }
                        }
                    }
                    else if (ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov")
                    {
                        ItemType = ClipboardItemType.Video;
                    }
                    else if (ext == ".mp3" || ext == ".wav" || ext == ".flac" || ext == ".ogg")
                    {
                        ItemType = ClipboardItemType.Audio;
                    }
                    else
                    {
                        // Explicit Fallback for any unknown binary payload to physically guarantee Web Client Distribution capability!
                        ItemType = ClipboardItemType.Document;
                    }
                }
                else if (Directory.Exists(path))
                {
                    // Folder copied — set lightweight properties immediately, defer heavy I/O
                    ItemType = ClipboardItemType.Folder;
                    Extension = "FOLDER";
                    FileName = Path.GetFileName(path);
                    FormattedSize = "Scanning...";
                    
                    // Heavy enumeration + zip runs on background thread
                    string capturedPath = path;
                    string capturedName = FileName;
                    Task.Run(() => {
                        try
                        {
                            var allFiles = Directory.GetFiles(capturedPath, "*", SearchOption.AllDirectories);
                            var allDirs = Directory.GetDirectories(capturedPath, "*", SearchOption.AllDirectories);
                            long folderSize = allFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                            FormattedSize = $"{FormatBytes(folderSize)} • {allFiles.Length} files";
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                            
                            // Build contents listing
                            var listing = new System.Text.StringBuilder();
                            listing.AppendLine($"📁 {capturedName}/");
                            listing.AppendLine($"   {allFiles.Length} file(s), {allDirs.Length} subfolder(s)");
                            listing.AppendLine();
                            
                            var topItems = Directory.GetFileSystemEntries(capturedPath).Take(30).ToArray();
                            foreach (var entry in topItems)
                            {
                                bool isDir = Directory.Exists(entry);
                                string name = Path.GetFileName(entry);
                                if (isDir)
                                {
                                    int subCount = 0;
                                    try { subCount = Directory.GetFileSystemEntries(entry).Length; } catch { }
                                    listing.AppendLine($"  📂 {name}/ ({subCount} items)");
                                }
                                else
                                {
                                    long fSize = 0;
                                    try { fSize = new FileInfo(entry).Length; } catch { }
                                    listing.AppendLine($"  📄 {name} ({FormatBytes(fSize)})");
                                }
                            }
                            if (Directory.GetFileSystemEntries(capturedPath).Length > 30)
                                listing.AppendLine($"  ... and more");
                            
                            RawContent = listing.ToString();
                            
                            // Zip for cross-device transfer
                            string tempZip = Path.Combine(Path.GetTempPath(), $"FlyShelf_{capturedName}_{DateTime.Now:HHmmss}.zip");
                            if (File.Exists(tempZip)) File.Delete(tempZip);
                            ZipFile.CreateFromDirectory(capturedPath, tempZip, CompressionLevel.Fastest, true);
                            ZippedArchivePath = tempZip;
                            var zipInfo = new FileInfo(tempZip);
                            FormattedSize = $"{FormatBytes(folderSize)} → {FormatBytes(zipInfo.Length)} zipped • {allFiles.Length} files";
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ZippedArchivePath)));
                        }
                        catch (Exception ex)
                        {
                            Classes.Logger.LogAction("FOLDER ZIP", $"Failed: {ex.Message}");
                            FormattedSize = "Folder";
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedSize)));
                        }
                    });
                }

                // Explicitly bind the Raw Content buffer natively securely mapping the File Execution Constraints!
                string xExt = Extension.ToLowerInvariant();
                bool isPlainText = xExt == ".txt" || xExt == ".json" || xExt == ".md" || xExt == ".csv" || xExt == ".xml" || ItemType == ClipboardItemType.Code;
                
                if (isPlainText && fileInfo != null && fileInfo.Exists && fileInfo.Length < 1000000)
                {
                    try { RawContent = File.ReadAllText(path); } catch { }
                }
            }
            catch
            {
                FormattedSize = "Unknown";
            }
            EvaluateSmartActions();
        }

        public void Execute()
        {
            try
            {
                string target = string.Empty;
                if (!string.IsNullOrEmpty(FilePath))
                    target = FilePath;
                else if (ItemType == ClipboardItemType.Url)
                    target = RawContent; // URL
                else if (ItemType == ClipboardItemType.Text || ItemType == ClipboardItemType.Code)
                {
                    // Create a scratch temp file to open Text in notepad
                    string tempFile = Path.Combine(Path.GetTempPath(), $"AdvanceClip_TextDrop_{Guid.NewGuid().ToString().Substring(0, 4)}.txt");
                    File.WriteAllText(tempFile, RawContent);
                    target = tempFile;
                }

                if (!string.IsNullOrEmpty(target))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to execute drop item: {ex.Message}");
            }
        }

        public void RefreshPhysicalStats()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            try
            {
                var fileInfo = new FileInfo(FilePath);
                if (fileInfo.Exists)
                {
                    FormattedSize = FormatBytes(fileInfo.Length);
                }
            }
            catch { }
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < suffixes.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            return $"{dblSByte:0.##} {suffixes[i]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        
        public void OpenSandbox()
        {
            try
            {
                if (ItemType != ClipboardItemType.Code) return;
                
                // Do not block execution if FilePath is populated and RawContent is explicitly empty 
                if (string.IsNullOrEmpty(RawContent) && string.IsNullOrEmpty(FilePath)) return;

                string sandboxDir;
                string fullPath;

                // [PATH REMEMBRANCE]: Validate if the copied sequence is a physical HDD File natively!
                if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                {
                    sandboxDir = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();
                    fullPath = FilePath;
                }
                else
                {
                    // Fallback to anonymous Temp Storage explicitly for Text Blocks dragged natively from Non-Path Apps 
                    sandboxDir = Path.Combine(Path.GetTempPath(), "FlyShelf_Sandbox", Guid.NewGuid().ToString().Substring(0, 6));
                    Directory.CreateDirectory(sandboxDir);
                    
                    string filename = string.IsNullOrEmpty(FileName) ? "snippet.txt" : FileName;
                    fullPath = Path.Combine(sandboxDir, filename);
                    
                    File.WriteAllText(fullPath, RawContent);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C code \"{sandboxDir}\" \"{fullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                AdvanceClip.Classes.Logger.LogAction("SANDBOX EXECUTION", $"Launching VS Code payload. Target: {fullPath}");
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TELEMETRY] Sandbox Launch Failed: {ex.Message}");
            }
        }

        public void RunInTerminal()
        {
            try
            {
                if (string.IsNullOrEmpty(RawContent) && string.IsNullOrEmpty(FilePath)) return;

                bool isPhysicalScript = !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);
                System.Windows.MessageBoxResult result = System.Windows.MessageBoxResult.Yes;

                if (!isPhysicalScript)
                {
                    result = System.Windows.MessageBox.Show(
                        "You are about to execute raw clipboard text directly in your native Command Prompt.\n\n" +
                        "Are you absolutely sure you want to run this command? Malicious scripts can heavily damage your operating system:\n\n" +
                        (RawContent?.Length > 200 ? RawContent.Substring(0, 200) + "..." : RawContent),
                        "Security Warning: Terminal Hook Execution",
                        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                }

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        UseShellExecute = true,
                        CreateNoWindow = false
                    };

                    // [PATH REMEMBRANCE]: If it's a physical file, simply open configuring CMD exactly in its native folder directory!
                    if (!string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                    {
                        startInfo.WorkingDirectory = Path.GetDirectoryName(FilePath) ?? "";
                        
                        // Dynamically Bootstrap the Engine based on Extension!
                        if (Extension == ".JS")
                            startInfo.Arguments = $"/k node \"{FileName}\"";
                        else if (Extension == ".PY")
                            startInfo.Arguments = $"/k python \"{FileName}\"";
                        else if (Extension == ".BAT" || Extension == ".CMD")
                            startInfo.Arguments = $"/c \"{FileName}\"";
                    }
                    else
                    {
                        // Fallback Behavior: Execute text blocks natively
                        startInfo.Arguments = $"/k {RawContent}";
                    }

                    AdvanceClip.Classes.Logger.LogAction("TERMINAL EXECUTION", $"Spawned native command prompt. Args: {startInfo.Arguments} | WorkingDir: {startInfo.WorkingDirectory}");
                    Process.Start(startInfo);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TELEMETRY] Terminal Hook Failed: {ex.Message}");
            }
        }
        public void OpenInBrowser()
        {
            try
            {
                if (IsUrlPreview && !string.IsNullOrEmpty(RawContent))
                {
                    Process.Start(new ProcessStartInfo { FileName = RawContent, UseShellExecute = true });
                }
            }
            catch (Exception ex) { Console.WriteLine($"Browser Hook Failed: {ex.Message}"); }
        }

        public void RunAdminTerminal()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath)) return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = Extension == ".PS1" ? "powershell.exe" : "cmd.exe",
                    Arguments = Extension == ".PS1" ? $"-NoExit -ExecutionPolicy Bypass -File \"{FilePath}\"" : $"/k \"{FilePath}\"",
                    UseShellExecute = true,
                    Verb = "runas" // Forces UAC Admin Elevation intelligently!
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to launch elevated terminal: {ex.Message}", "AdvanceClip OS Hook Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        public void CompileAndRunNative()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath) && string.IsNullOrEmpty(RawContent)) return;
                
                string sourceFile = FilePath;
                string exeDir = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();
                string exeName = Path.Combine(exeDir, Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(FilePath) ? "AdvanceClipTempCompile" : FilePath) + ".exe");

                if (string.IsNullOrEmpty(FilePath))
                {
                    sourceFile = Path.Combine(Path.GetTempPath(), "AdvanceClipRuntime_" + Guid.NewGuid().ToString().Substring(0, 4) + ".cpp");
                    File.WriteAllText(sourceFile, RawContent);
                    exeName = Path.Combine(Path.GetTempPath(), "AdvanceClipRuntime.exe");
                }
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k title AdvanceClip C/C++ Compiler && echo [AdvanceClip Engine] Executing g++ on payload... && g++ \"{sourceFile}\" -o \"{exeName}\" && echo ----------------------------------------- && \"{exeName}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(startInfo);
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Hardware Compiler Error"); }
        }

        public void ConvertDocumentTask()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;
                    
                    System.Windows.Application.Current.Dispatcher.Invoke(() => 
                        AdvanceClip.Windows.ToastWindow.ShowToast("Synthesizing Document Format natively... ♻️")
                    );

                    string targetPdf = Path.Combine(Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(), Path.GetFileNameWithoutExtension(FilePath) + "_Converted.pdf");
                    
                    // Native COM Script for high-fidelity conversion without python dependencies
                    string script = $"$word = New-Object -ComObject Word.Application; $word.Visible = $false; $doc = $word.Documents.Open('{FilePath}'); $doc.SaveAs([ref]'{targetPdf}', [ref]17); $doc.Close(); $word.Quit();";
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    using (var process = Process.Start(startInfo))
                    {
                        if (process != null)
                        {
                            process.WaitForExit();
                            if (File.Exists(targetPdf))
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                                {
                                    // Drop the synthesized PDF back locally
                                    var dataObj = new System.Windows.DataObject();
                                    dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { targetPdf });
                                    var mainWin = System.Windows.Application.Current.MainWindow as AdvanceClip.MainWindow;
                                    (mainWin?.DataContext as AdvanceClip.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);
                                    
                                    AdvanceClip.Windows.ToastWindow.ShowToast("Format Synthesized Successfully ✅");
                                });
                            }
                            else
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                                    AdvanceClip.Windows.ToastWindow.ShowToast("Synthesis Failed: Could not output file ❌")
                                );
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => 
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Synthesis Exception: {ex.Message} ❌")
                    );
                }
            });
        }

        /// <summary>
        /// Convert an image to a single-page PDF (A4 size). No external dependencies.
        /// Uses raw PDF specification writing with embedded JPEG stream.
        /// </summary>
        public void ConvertImageToPdf()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        AdvanceClip.Windows.ToastWindow.ShowToast("Converting Image to PDF... 📄")
                    );

                    string outputPdf = Path.Combine(
                        Path.GetDirectoryName(FilePath) ?? Path.GetTempPath(),
                        Path.GetFileNameWithoutExtension(FilePath) + ".pdf");

                    // Load image to get dimensions
                    byte[] jpegBytes;
                    int imgWidth, imgHeight;
                    using (var bmp = new System.Drawing.Bitmap(FilePath))
                    {
                        imgWidth = bmp.Width;
                        imgHeight = bmp.Height;

                        // Convert to JPEG for PDF embedding
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                            jpegBytes = ms.ToArray();
                        }
                    }

                    // A4 page size in points (72 dpi): 595.28 x 841.89
                    double pageW = 595.28, pageH = 841.89;
                    double margin = 36; // 0.5 inch margin
                    double usableW = pageW - 2 * margin;
                    double usableH = pageH - 2 * margin;

                    // Scale image to fit page while maintaining aspect ratio
                    double scale = Math.Min(usableW / imgWidth, usableH / imgHeight);
                    double drawW = imgWidth * scale;
                    double drawH = imgHeight * scale;
                    double drawX = margin + (usableW - drawW) / 2;
                    double drawY = margin + (usableH - drawH) / 2;

                    // Write a minimal valid PDF
                    using (var fs = new FileStream(outputPdf, FileMode.Create))
                    using (var writer = new StreamWriter(fs, System.Text.Encoding.ASCII))
                    {
                        var offsets = new List<long>();

                        writer.Write("%PDF-1.4\n");
                        writer.Flush();

                        // Object 1: Catalog
                        offsets.Add(fs.Position);
                        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                        writer.Flush();

                        // Object 2: Pages
                        offsets.Add(fs.Position);
                        writer.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
                        writer.Flush();

                        // Object 3: Page
                        offsets.Add(fs.Position);
                        writer.Write($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW:F2} {pageH:F2}] /Contents 4 0 R /Resources << /XObject << /Img1 5 0 R >> >> >>\nendobj\n");
                        writer.Flush();

                        // Object 4: Content stream (draw image)
                        string contentStream = $"q\n{drawW:F2} 0 0 {drawH:F2} {drawX:F2} {drawY:F2} cm\n/Img1 Do\nQ\n";
                        offsets.Add(fs.Position);
                        writer.Write($"4 0 obj\n<< /Length {contentStream.Length} >>\nstream\n{contentStream}endstream\nendobj\n");
                        writer.Flush();

                        // Object 5: Image XObject (JPEG)
                        offsets.Add(fs.Position);
                        writer.Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgWidth} /Height {imgHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegBytes.Length} >>\nstream\n");
                        writer.Flush();
                        fs.Write(jpegBytes, 0, jpegBytes.Length);
                        writer.Write("\nendstream\nendobj\n");
                        writer.Flush();

                        // Cross-reference table
                        long xrefOffset = fs.Position;
                        writer.Write($"xref\n0 {offsets.Count + 1}\n");
                        writer.Write("0000000000 65535 f \n");
                        foreach (var off in offsets)
                            writer.Write($"{off:D10} 00000 n \n");

                        writer.Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
                        writer.Flush();
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Drop the PDF back into the clipboard shelf
                        var dataObj = new System.Windows.DataObject();
                        dataObj.SetData(System.Windows.DataFormats.FileDrop, new string[] { outputPdf });
                        var mainWin = System.Windows.Application.Current.MainWindow as AdvanceClip.MainWindow;
                        (mainWin?.DataContext as AdvanceClip.ViewModels.FlyShelfViewModel)?.HandleDrop(dataObj, true);

                        AdvanceClip.Windows.ToastWindow.ShowToast($"Image → PDF converted! ✅ {Path.GetFileName(outputPdf)}");
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        AdvanceClip.Windows.ToastWindow.ShowToast($"Image→PDF failed: {ex.Message} ❌")
                    );
                }
            });
        }

        public async void ExtractText()
        {
            if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;

            try
            {
                AdvanceClip.Windows.ToastWindow.ShowToast("Scanning Native Hardware OCR...");

                await Task.Run(async () => 
                {
                    using (var stream = File.OpenRead(FilePath))
                    {
                        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                        
                        var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
                        if (ocrEngine != null)
                        {
                            var result = await ocrEngine.RecognizeAsync(softwareBitmap);
                            if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                                {
                                    System.Windows.Clipboard.SetText(result.Text);
                                    AdvanceClip.Windows.ToastWindow.ShowToast("OCR Text Copied to Clipboard! 📋");
                                });
                            }
                            else
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                                    AdvanceClip.Windows.ToastWindow.ShowToast("No Text Detected in Image.")
                                );
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    AdvanceClip.Windows.ToastWindow.ShowToast($"OCR Engine Missing/Failed")
                );
            }
        }

        public async void ExtractTable()
        {
            try
            {
                if (!IsImagePreview || string.IsNullOrEmpty(FilePath)) return;

                AdvanceClip.Windows.ToastWindow.ShowToast("Extracting Table from Image... ⏳");

                string finalJsonPayload = string.Empty;

                await System.Threading.Tasks.Task.Run(async () => 
                {
                    // ═══ PHASE 1: Windows Native OCR + Smart Grid Detection ═══
                    try
                    {
                        using (var stream = File.OpenRead(FilePath))
                        {
                            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                            
                            var ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                                new global::Windows.Globalization.Language("en-US"));
                            
                            if (ocrEngine == null)
                            {
                                ocrEngine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                            }
                            
                            if (ocrEngine != null)
                            {
                                var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
                                
                                if (ocrResult != null && ocrResult.Lines.Count >= 2)
                                {
                                    // Collect all words with their bounding boxes
                                    var allWords = new List<(string Text, double X, double Y, double W, double H, double Right)>();
                                    foreach (var line in ocrResult.Lines)
                                    {
                                        foreach (var word in line.Words)
                                        {
                                            var rect = word.BoundingRect;
                                            allWords.Add((word.Text, rect.X, rect.Y, rect.Width, rect.Height, rect.X + rect.Width));
                                        }
                                    }
                                    
                                    if (allWords.Count >= 4)
                                    {
                                        // ── STEP 1: Group words into rows by Y-coordinate ──
                                        var sorted = allWords.OrderBy(w => w.Y).ToList();
                                        double avgHeight = sorted.Average(w => w.H);
                                        double rowThreshold = avgHeight * 0.7;
                                        
                                        var rows = new List<List<(string Text, double X, double W, double Right)>>();
                                        var currentRow = new List<(string Text, double X, double W, double Right)>();
                                        double lastY = sorted[0].Y;
                                        
                                        foreach (var word in sorted)
                                        {
                                            if (Math.Abs(word.Y - lastY) > rowThreshold && currentRow.Count > 0)
                                            {
                                                rows.Add(currentRow.OrderBy(w => w.X).ToList());
                                                currentRow = new List<(string Text, double X, double W, double Right)>();
                                            }
                                            currentRow.Add((word.Text, word.X, word.W, word.Right));
                                            lastY = word.Y;
                                        }
                                        if (currentRow.Count > 0)
                                            rows.Add(currentRow.OrderBy(w => w.X).ToList());
                                        
                                        if (rows.Count >= 2)
                                        {
                                            // ── STEP 2: Detect column separators via gap clustering ──
                                            double avgW = allWords.Average(w => w.W);
                                            double minGap = avgW * 1.2;
                                            
                                            var allGaps = new List<(double Center, double Size)>();
                                            foreach (var row in rows)
                                                for (int gi = 0; gi < row.Count - 1; gi++)
                                                {
                                                    double gap = row[gi + 1].X - row[gi].Right;
                                                    if (gap > minGap)
                                                        allGaps.Add(((row[gi].Right + row[gi + 1].X) / 2.0, gap));
                                                }
                                            
                                            var separators = new List<double>();
                                            double clusterDist = avgW * 2.0;
                                            foreach (var g in allGaps.OrderBy(g => g.Center))
                                            {
                                                bool merged = false;
                                                for (int si = 0; si < separators.Count; si++)
                                                    if (Math.Abs(g.Center - separators[si]) < clusterDist)
                                                    { separators[si] = (separators[si] + g.Center) / 2.0; merged = true; break; }
                                                if (!merged) separators.Add(g.Center);
                                            }
                                            
                                            separators = separators
                                                .Where(s => allGaps.Count(g => Math.Abs(g.Center - s) < clusterDist) >= Math.Max(2, rows.Count * 0.3))
                                                .OrderBy(s => s).ToList();
                                            int numCols = separators.Count + 1;
                                            
                                            if (numCols >= 2)
                                            {
                                                var jsonDict = new Dictionary<string, object>();
                                                for (int ri = 0; ri < rows.Count; ri++)
                                                {
                                                    var buckets = new string[numCols];
                                                    for (int c = 0; c < numCols; c++) buckets[c] = "";
                                                    foreach (var word in rows[ri])
                                                    {
                                                        double wc = word.X + word.W / 2.0;
                                                        int col = 0;
                                                        for (int si = 0; si < separators.Count; si++)
                                                        { if (wc > separators[si]) col = si + 1; else break; }
                                                        if (col >= numCols) col = numCols - 1;
                                                        buckets[col] += (buckets[col].Length > 0 ? " " : "") + word.Text;
                                                    }
                                                    for (int ci = 0; ci < numCols; ci++)
                                                        jsonDict[$"({ri},{ci})"] = new { text = buckets[ci].Trim(), conf = 0.90 };
                                                }
                                                finalJsonPayload = System.Text.Json.JsonSerializer.Serialize(jsonDict);
                                                Classes.Logger.LogAction("TABLE_EXTRACT", $"OCR: {rows.Count}x{numCols} table ({separators.Count} separators)");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ocrEx)
                    {
                        Classes.Logger.LogAction("TABLE_OCR_FAIL", ocrEx.Message);
                    }

                    // ═══ PHASE 2: Gemini AI Fallback (if OCR failed or detected no table) ═══
                    if (string.IsNullOrWhiteSpace(finalJsonPayload) || !finalJsonPayload.StartsWith("{"))
                    {
                        string apiKey = AdvanceClip.Classes.SettingsManager.Current.GeminiApiKey;
                        if (string.IsNullOrWhiteSpace(apiKey))
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => 
                                AdvanceClip.Windows.ToastWindow.ShowToast("OCR couldn't detect table structure. Set Gemini API Key in Settings for AI fallback.")
                            );
                            return;
                        }

                        System.Windows.Application.Current.Dispatcher.Invoke(() => 
                            AdvanceClip.Windows.ToastWindow.ShowToast("OCR inconclusive. Using Gemini AI for table extraction...")
                        );
                        
                        finalJsonPayload = await AdvanceClip.Classes.GeminiEngine.ExtractFormattedTableFromImageAsync(FilePath, apiKey);
                        Classes.Logger.LogAction("TABLE_EXTRACT", "Gemini AI extracted table successfully");
                    }
                });

                if (!string.IsNullOrWhiteSpace(finalJsonPayload) && finalJsonPayload.StartsWith("{"))
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    {
                        var editor = new AdvanceClip.Windows.TableEditorWindow(finalJsonPayload);
                        editor.Show();
                    });
                }
                else
                {
                    AdvanceClip.Windows.ToastWindow.ShowToast("No table structure detected in this image.");
                }
            }
            catch (Exception ex)
            {
                Classes.Logger.LogAction("TABLE_EXTRACT_FAIL", ex.Message);
                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    AdvanceClip.Windows.ToastWindow.ShowToast($"Table Extraction Failed: {ex.Message}")
                );
            }
        }
        public void ScanForQRCodeAsync(string path)
        {
            System.Threading.Tasks.Task.Run(() => {
                try {
                    using (var bmp = new System.Drawing.Bitmap(path))
                    {
                        var reader = new ZXing.Windows.Compatibility.BarcodeReader();
                        reader.Options.TryHarder = false;
                        var result = reader.Decode(bmp);
                        if (result != null && !string.IsNullOrWhiteSpace(result.Text)) {
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => {
                                // Auto-copy scanned content to clipboard
                                MainWindow._isWritingClipboard = true;
                                try { System.Windows.Clipboard.SetText(result.Text); } 
                                finally { MainWindow._isWritingClipboard = false; }

                                this.ItemType = ClipboardItemType.QRCode;
                                this.RawContent = result.Text;
                                this.EvaluateSmartActions();
                                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ItemType"));
                                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("RawContent"));
                                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsImagePreview"));
                                string preview = result.Text.Length > 50 ? result.Text.Substring(0, 50) + "..." : result.Text;
                                AdvanceClip.Windows.ToastWindow.ShowToast($"QR Copied! 🔍 {preview}");
                            });
                        }
                    }
                } catch { }
            });
        }
    }
}
