using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;

namespace AdvanceClip.Classes
{
    public static class RuntimeHost
    {
        public static string ExecutionDir { get; private set; }

        public static void Initialize()
        {
            string basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvanceClip", "RuntimeCore");
            ExecutionDir = basePath;

            string trueExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;
            long currentVer = new FileInfo(trueExePath).LastWriteTimeUtc.Ticks;

            string verFile = Path.Combine(ExecutionDir, "version.txt");
            if (File.Exists(verFile) && File.ReadAllText(verFile).Trim() == currentVer.ToString())
            {
                // Already extracted the payloads for this version. Fast boot.
                return;
            }

            // Version changed or clean install. Rebuild payload directories natively.
            try { if (Directory.Exists(ExecutionDir)) Directory.Delete(ExecutionDir, true); } catch { }
            Directory.CreateDirectory(ExecutionDir);

            ExtractResource("AdvanceClip.Scripts.zip", Path.Combine(ExecutionDir, "Scripts"));
            ExtractResource("AdvanceClip.WebClient.zip", Path.Combine(ExecutionDir, "Resources", "WebClient"));

            File.WriteAllText(verFile, currentVer.ToString());
        }

        private static void ExtractResource(string resourceName, string outDir)
        {
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return;
                
                string tempZip = Path.Combine(ExecutionDir, resourceName);
                using (var fs = new FileStream(tempZip, FileMode.Create))
                {
                    stream.CopyTo(fs);
                }

                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string destinationPath = Path.GetFullPath(Path.Combine(outDir, entry.FullName));
                        if (destinationPath.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith("/")) 
                            {
                                Directory.CreateDirectory(destinationPath);
                            }
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                                entry.ExtractToFile(destinationPath, true);
                            }
                        }
                    }
                }
                try { File.Delete(tempZip); } catch { }
            }
        }
    }
}
