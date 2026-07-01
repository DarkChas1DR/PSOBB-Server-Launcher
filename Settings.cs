using System;
using System.IO;
using System.Text.Json;

namespace Launcher
{
    public class LauncherSettings
    {
        public string NewservPath { get; set; } = "newserv.exe";
        public string NewservWorkingDirectory { get; set; } = "";
        public string PhpPath { get; set; } = "";
        public string WebsitePath { get; set; } = "";
        public int CronIntervalSeconds { get; set; } = 5;
        public bool AutoStart { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool RunCronsDirectly { get; set; } = true; // If true, C# runs PHP scripts. If false, runs start_bounty.bat.
        public bool AutoBackupOnStart { get; set; } = true;
        public bool AutoBackupOnStop { get; set; } = false;
        public int KeepMaxBackups { get; set; } = 10;

        private static readonly string SettingsFileName = "launcher_settings.json";

        public static string GetSettingsPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
        }

        public static LauncherSettings Load()
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<LauncherSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
                catch (Exception)
                {
                    // Fallback to default/autodetected
                }
            }

            var defaultSettings = new LauncherSettings();
            defaultSettings.Autodetect();
            return defaultSettings;
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetSettingsPath(), json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public void Autodetect()
        {
            // 1. Detect Newserv path and working directory
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // Check if newserv.exe is in current directory
            if (File.Exists(Path.Combine(appDir, "newserv.exe")))
            {
                NewservPath = Path.Combine(appDir, "newserv.exe");
                NewservWorkingDirectory = appDir;
            }
            else
            {
                // Check parent directories (e.g. if we are in Launcher/bin/Debug/...)
                DirectoryInfo? dir = new DirectoryInfo(appDir);
                while (dir != null)
                {
                    string target = Path.Combine(dir.FullName, "newserv.exe");
                    if (File.Exists(target))
                    {
                        NewservPath = target;
                        NewservWorkingDirectory = dir.FullName;
                        break;
                    }
                    // Also check for PhantasyStarOnlineServer if we are in a subfolder
                    string targetFolder = Path.Combine(dir.FullName, "PhantasyStarOnlineServer", "newserv.exe");
                    if (File.Exists(targetFolder))
                    {
                        NewservPath = targetFolder;
                        NewservWorkingDirectory = Path.Combine(dir.FullName, "PhantasyStarOnlineServer");
                        break;
                    }
                    dir = dir.Parent;
                }
            }

            // Fallback for newserv working directory if not set
            if (string.IsNullOrEmpty(NewservWorkingDirectory))
            {
                NewservWorkingDirectory = Path.GetDirectoryName(NewservPath) ?? appDir;
            }

            // 2. Detect Website Path (i:\WAMPP\www)
            if (Directory.Exists(@"i:\WAMPP\www"))
            {
                WebsitePath = @"i:\WAMPP\www";
            }
            else
            {
                // Look for run_daemons.bat or WAMPP folders in other drives or parent folders
                DirectoryInfo? dir = new DirectoryInfo(appDir);
                while (dir != null)
                {
                    string wwwPath = Path.Combine(dir.FullName, "www");
                    if (Directory.Exists(wwwPath) && File.Exists(Path.Combine(wwwPath, "run_daemons.bat")))
                    {
                        WebsitePath = wwwPath;
                        break;
                    }
                    dir = dir.Parent;
                }

                if (string.IsNullOrEmpty(WebsitePath))
                {
                    WebsitePath = appDir; // fallback
                }
            }

            // 3. Detect PHP Path
            string phpBin = @"I:\WAMPP\bin\php\php8.2.29\php.exe";
            if (File.Exists(phpBin))
            {
                PhpPath = phpBin;
            }
            else
            {
                // Scan WAMPP php directories
                if (Directory.Exists(@"I:\WAMPP\bin\php"))
                {
                    try
                    {
                        var phpFiles = Directory.GetFiles(@"I:\WAMPP\bin\php", "php.exe", SearchOption.AllDirectories);
                        if (phpFiles.Length > 0)
                        {
                            PhpPath = phpFiles[0];
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(PhpPath))
                {
                    // Try system path
                    PhpPath = FindInPath("php.exe") ?? "php.exe";
                }
            }
        }

        private static string? FindInPath(string fileName)
        {
            try
            {
                var values = Environment.GetEnvironmentVariable("PATH");
                if (values == null) return null;

                foreach (var path in values.Split(Path.PathSeparator))
                {
                    try
                    {
                        string cleanPath = path.Trim('\"');
                        if (string.IsNullOrWhiteSpace(cleanPath)) continue;

                        var fullPath = Path.Combine(cleanPath, fileName);
                        if (File.Exists(fullPath))
                            return fullPath;
                    }
                    catch (ArgumentException) { }
                    catch (PathTooLongException) { }
                }
            }
            catch { }
            return null;
        }
    }
}
