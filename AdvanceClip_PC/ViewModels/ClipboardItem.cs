using System;
using System.IO;
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
        Pdf
    }

    public class ClipboardItem : INotifyPropertyChanged
    {
        public DateTime DateCopied { get; set; } = DateTime.Now;
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        
        public string AssociatedContextTitle { get; set; } = string.Empty;

        public string SourceDeviceName { get; set; } = "Local";
        public string SourceDeviceType { get; set; } = "PC";

        /// <summary>
        /// Creates a lightweight copy for Firebase sync, overriding RawContent with a download URL
        /// without mutating the original item displayed in the DropShelf.
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
                SourceDeviceType = this.SourceDeviceType
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
        public string RawContent { get; set; } = string.Empty;
        public bool IsImagePreview => ItemType == ClipboardItemType.Image || ItemType == ClipboardItemType.QRCode;
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
                    Extension = "FOLDER";
                    FormattedSize = "Folder";
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
                    sandboxDir = Path.Combine(Path.GetTempPath(), "DropShelf_Sandbox", Guid.NewGuid().ToString().Substring(0, 6));
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
                                    (mainWin?.DataContext as AdvanceClip.ViewModels.DropShelfViewModel)?.HandleDrop(dataObj, true);
                                    
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

                AdvanceClip.Windows.ToastWindow.ShowToast("Extracting Offline AI Table... Wait!");

                string finalJsonPayload = string.Empty;

                await System.Threading.Tasks.Task.Run(async () => 
                {
                    // PHASE 1: Attempt offline local OpenCV + Pytesseract grid resolution
                    var proc = new System.Diagnostics.Process {
                        StartInfo = new System.Diagnostics.ProcessStartInfo {
                            FileName = "python",
                            Arguments = $"\"extract_matrix.py\" \"{FilePath}\"",
                            WorkingDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AdvanceClip.Classes.RuntimeHost.ExecutionDir, "Scripts", "TableExtractor")),
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    proc.Start();
                    string pythonOutput = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(pythonOutput) && pythonOutput.StartsWith("{"))
                    {
                        finalJsonPayload = pythonOutput;
                        Console.WriteLine("[TELEMETRY] Local OpenCV extracted grid offline seamlessly.");
                    }
                    else
                    {
                        // PHASE 2: Fallback to Gemini LLM Contextual Grid Mapping
                        System.Windows.Application.Current.Dispatcher.Invoke(() => 
                            AdvanceClip.Windows.ToastWindow.ShowToast("Local CV Failed. Engaging Gemini AI Fallback!")
                        );
                        
                        string apiKey = AdvanceClip.Classes.SettingsManager.Current.GeminiApiKey;
                        if (string.IsNullOrWhiteSpace(apiKey))
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => 
                                AdvanceClip.Windows.ToastWindow.ShowToast("Gemini Fallback Missing API Key! Aborting.")
                            );
                            return;
                        }

                        finalJsonPayload = await AdvanceClip.Classes.GeminiEngine.ExtractFormattedTableFromImageAsync(FilePath, apiKey);
                        Console.WriteLine("[TELEMETRY] Gemini Cloud fallback extracted matrix gracefully.");
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
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TELEMETRY] Engine Hybrid Fault: " + ex.Message);
                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                    AdvanceClip.Windows.ToastWindow.ShowToast("Extraction Failed. Check Logs.")
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
                                this.ItemType = ClipboardItemType.QRCode;
                                this.RawContent = result.Text;
                                this.EvaluateSmartActions();
                                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ItemType"));
                                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("RawContent"));
                                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsImagePreview"));
                                AdvanceClip.Windows.ToastWindow.ShowToast("QR Code Extracted! 🔍");
                            });
                        }
                    }
                } catch { }
            });
        }
    }
}
