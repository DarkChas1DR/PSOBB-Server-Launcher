using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Launcher
{
    public partial class MainWindow : Window
    {
        private readonly LauncherSettings _settings;
        private readonly ProcessService _newservService;
        private readonly CronService _cronService;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isExplicitExit = false;
        private JsonObject? _serverConfigNode;
        private bool _isLoadingConfig = false;

        public MainWindow()
        {
            InitializeComponent();

            // Load settings
            _settings = LauncherSettings.Load();

            // Initialize services
            _newservService = new ProcessService("newserv");
            _cronService = new CronService(_settings);

            // Set up UI fields
            PopulateSettingsUI();
            UpdateDashboardUI();

            // Bind TextChanged events for real-time validation
            TxtSetNewservPath.TextChanged += (s, e) => ValidatePaths();
            TxtSetNewservDir.TextChanged += (s, e) => ValidatePaths();
            TxtSetPhpPath.TextChanged += (s, e) => ValidatePaths();
            TxtSetWebsitePath.TextChanged += (s, e) => ValidatePaths();
            ValidatePaths();

            // Bind events
            BindServiceEvents();

            // Setup system tray
            SetupSystemTray();

            // Load Server Config if present
            LoadServerConfig();

            // Handle auto-start
            if (_settings.AutoStart)
            {
                Dispatcher.BeginInvoke(new Action(() => BtnStartAll_Click(this, new RoutedEventArgs())), DispatcherPriority.Background);
            }
        }

        private void PopulateSettingsUI()
        {
            TxtSetNewservPath.Text = _settings.NewservPath;
            TxtSetNewservDir.Text = _settings.NewservWorkingDirectory;
            TxtSetPhpPath.Text = _settings.PhpPath;
            TxtSetWebsitePath.Text = _settings.WebsitePath;
            TxtSetCronInterval.Text = _settings.CronIntervalSeconds.ToString();
            ChkRunDirectly.IsChecked = _settings.RunCronsDirectly;
            ChkAutoStart.IsChecked = _settings.AutoStart;
            ChkMinimizeToTray.IsChecked = _settings.MinimizeToTray;
            ChkAutoBackupOnStart.IsChecked = _settings.AutoBackupOnStart;
            ChkAutoBackupOnStop.IsChecked = _settings.AutoBackupOnStop;
            TxtKeepMaxBackups.Text = _settings.KeepMaxBackups.ToString();
        }

        private void UpdateDashboardUI()
        {
            TxtNewservDir.Text = string.IsNullOrEmpty(_settings.NewservWorkingDirectory) ? "Not set" : _settings.NewservWorkingDirectory;
            TxtCronInterval.Text = $"{_settings.CronIntervalSeconds} seconds";
            TxtCronMode.Text = _settings.RunCronsDirectly ? "Direct C# (Background)" : "Batch Script (run_daemons.bat)";
            
            if (_settings.MinimizeToTray)
            {
                TxtTrayNotice.Visibility = Visibility.Visible;
            }
            else
            {
                TxtTrayNotice.Visibility = Visibility.Collapsed;
            }
        }

        private void BindServiceEvents()
        {
            // newserv events
            _newservService.OnOutputReceived += (text) => AppendLog(TxtNewservLog, ChkNewservScroll, text);
            _newservService.OnStatusChanged += (running) => Dispatcher.BeginInvoke(() =>
            {
                if (running)
                {
                    TxtNewservStatus.Text = "Running";
                    TxtNewservStatus.Foreground = (Brush)FindResource("AccentGreen");
                    IndicatorNewserv.Fill = (Brush)FindResource("AccentGreen");
                    BorderNewserv.BorderBrush = (Brush)FindResource("AccentGreen");
                    BtnStartNewserv.IsEnabled = false;
                    BtnStopNewserv.IsEnabled = true;
                }
                else
                {
                    TxtNewservStatus.Text = "Stopped";
                    TxtNewservStatus.Foreground = (Brush)FindResource("AccentRed");
                    IndicatorNewserv.Fill = (Brush)FindResource("AccentRed");
                    BorderNewserv.BorderBrush = (Brush)FindResource("BorderBrush");
                    BtnStartNewserv.IsEnabled = true;
                    BtnStopNewserv.IsEnabled = false;
                }
            });

            // cron events
            _cronService.OnOutputReceived += (text) => AppendLog(TxtCronLog, ChkCronScroll, text);
            _cronService.OnStatusChanged += (running) => Dispatcher.BeginInvoke(() =>
            {
                if (running)
                {
                    TxtCronsStatus.Text = "Running";
                    TxtCronsStatus.Foreground = (Brush)FindResource("AccentGreen");
                    IndicatorCrons.Fill = (Brush)FindResource("AccentGreen");
                    BorderCrons.BorderBrush = (Brush)FindResource("AccentGreen");
                    BtnStartCrons.IsEnabled = false;
                    BtnStopCrons.IsEnabled = true;
                }
                else
                {
                    TxtCronsStatus.Text = "Stopped";
                    TxtCronsStatus.Foreground = (Brush)FindResource("AccentRed");
                    IndicatorCrons.Fill = (Brush)FindResource("AccentRed");
                    BorderCrons.BorderBrush = (Brush)FindResource("BorderBrush");
                    BtnStartCrons.IsEnabled = true;
                    BtnStopCrons.IsEnabled = false;
                }
            });
        }

        private void SetupSystemTray()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "Pioneer II Server Launcher",
                    Visible = true
                };

                // Use the application's icon, or extract a default network/server icon from shell32
                _notifyIcon.Icon = System.Drawing.SystemIcons.Shield;

                _notifyIcon.DoubleClick += (s, e) => RestoreWindow();

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("Restore Dashboard", null, (s, e) => RestoreWindow());
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Start All Services", null, (s, e) => BtnStartAll_Click(this, new RoutedEventArgs()));
                contextMenu.Items.Add("Stop All Services", null, (s, e) => BtnStopAll_Click(this, new RoutedEventArgs()));
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Exit Launcher", null, (s, e) => ShutdownApplication());

                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize system tray: {ex.Message}", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AppendLog(System.Windows.Controls.TextBox textBox, System.Windows.Controls.CheckBox autoScrollCheck, string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Safety length check to prevent infinite memory usage
                if (textBox.Text.Length > 200000)
                {
                    textBox.Text = textBox.Text.Substring(100000);
                }

                textBox.AppendText(text + Environment.NewLine);

                if (autoScrollCheck.IsChecked == true)
                {
                    textBox.ScrollToEnd();
                }
            }));
        }

        private void RestoreWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ShutdownApplication()
        {
            _isExplicitExit = true;
            
            // Stop services
            if (_newservService.IsRunning) _newservService.Stop();
            if (_cronService.IsRunning) _cronService.Stop();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isExplicitExit)
            {
                base.OnClosing(e);
                return;
            }

            if (_settings.MinimizeToTray)
            {
                e.Cancel = true;
                this.Hide();
                
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(3000, "Pioneer II Launcher", "The Server Launcher is running in the background.", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                ShutdownApplication();
            }
        }

        // --- BUTTON EVENTS ---

        private void BtnStartNewserv_Click(object sender, RoutedEventArgs e)
        {
            _newservService.Configure(_settings.NewservPath, _settings.NewservWorkingDirectory);
            _newservService.Start();
        }

        private void BtnStopNewserv_Click(object sender, RoutedEventArgs e)
        {
            _newservService.Stop();
        }

        private void BtnStartCrons_Click(object sender, RoutedEventArgs e)
        {
            _cronService.Start();
        }

        private void BtnStopCrons_Click(object sender, RoutedEventArgs e)
        {
            _cronService.Stop();
        }

        private void BtnStartAll_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.AutoBackupOnStart)
            {
                RunBackupProcess(false);
            }
            BtnStartNewserv_Click(sender, e);
            BtnStartCrons_Click(sender, e);
        }

        private void BtnStopAll_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.AutoBackupOnStop)
            {
                RunBackupProcess(false);
            }
            BtnStopNewserv_Click(sender, e);
            BtnStopCrons_Click(sender, e);
        }

        private void BtnClearNewservLog_Click(object sender, RoutedEventArgs e)
        {
            TxtNewservLog.Clear();
        }

        private void BtnClearCronLog_Click(object sender, RoutedEventArgs e)
        {
            TxtCronLog.Clear();
        }

        // --- SETTINGS BROWSE BUTTONS ---

        private void BtnBrowseNewserv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = "newserv.exe"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtSetNewservPath.Text = dialog.FileName;
                
                // Automatically set working directory to that folder if empty
                if (string.IsNullOrEmpty(TxtSetNewservDir.Text))
                {
                    TxtSetNewservDir.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
                }
            }
        }

        private void BtnBrowseNewservDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select newserv Working Directory"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtSetNewservDir.Text = dialog.FolderName;
            }
        }

        private void BtnBrowsePhp_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                FileName = "php.exe"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtSetPhpPath.Text = dialog.FileName;
            }
        }

        private void BtnBrowseWebsite_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Website (www) Directory containing api/"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtSetWebsitePath.Text = dialog.FolderName;
            }
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            // Validate interval
            if (!int.TryParse(TxtSetCronInterval.Text, out int interval) || interval < 1)
            {
                MessageBox.Show("Please enter a valid cron interval (minimum 1 second).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Validate keep max backups
            if (!int.TryParse(TxtKeepMaxBackups.Text, out int keepMax) || keepMax < 1)
            {
                MessageBox.Show("Please enter a valid number of backups to keep (minimum 1).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Update model
            _settings.NewservPath = TxtSetNewservPath.Text;
            _settings.NewservWorkingDirectory = TxtSetNewservDir.Text;
            _settings.PhpPath = TxtSetPhpPath.Text;
            _settings.WebsitePath = TxtSetWebsitePath.Text;
            _settings.CronIntervalSeconds = interval;
            _settings.RunCronsDirectly = ChkRunDirectly.IsChecked == true;
            _settings.AutoStart = ChkAutoStart.IsChecked == true;
            _settings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
            _settings.AutoBackupOnStart = ChkAutoBackupOnStart.IsChecked == true;
            _settings.AutoBackupOnStop = ChkAutoBackupOnStop.IsChecked == true;
            _settings.KeepMaxBackups = keepMax;

            // Save settings to JSON
            _settings.Save();

            // Refresh UI labels
            UpdateDashboardUI();

            // Reload server config since newserv directory might have changed
            LoadServerConfig();

            MessageBox.Show("Configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // --- SERVER CONFIG METHODS ---

        private string GetServerConfigPath()
        {
            return Path.Combine(_settings.NewservWorkingDirectory, "system", "config.json");
        }

        private void LoadServerConfig()
        {
            string configPath = GetServerConfigPath();
            if (!File.Exists(configPath))
            {
                SetServerConfigEnabled(false);
                TxtCfgServerName.Text = "system/config.json not found. Please configure the correct newserv folder in Settings.";
                return;
            }

            _isLoadingConfig = true;
            try
            {
                SetServerConfigEnabled(true);
                string jsonText = File.ReadAllText(configPath);
                
                // Preprocess to replace hex literals (0x...) outside strings with decimal integers
                string cleanJson = Regex.Replace(jsonText, @"""[^""\\]*(?:\\.[^""\\]*)*""|\b0[xX]([0-9a-fA-F]+)\b", match =>
                {
                    if (match.Groups[1].Success)
                    {
                        try
                        {
                            long val = Convert.ToInt64(match.Groups[1].Value, 16);
                            return val.ToString();
                        }
                        catch
                        {
                            return match.Value;
                        }
                    }
                    return match.Value;
                });

                var options = new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                };

                _serverConfigNode = JsonNode.Parse(cleanJson, null, options)?.AsObject();

                if (_serverConfigNode != null)
                {
                    TxtCfgServerName.Text = _serverConfigNode["ServerName"]?.GetValue<string>() ?? "";
                    TxtCfgLocalAddress.Text = _serverConfigNode["LocalAddress"]?.GetValue<string>() ?? "";
                    TxtCfgExternalAddress.Text = _serverConfigNode["ExternalAddress"]?.GetValue<string>() ?? "";
                    TxtCfgWelcomeMessage.Text = _serverConfigNode["WelcomeMessage"]?.GetValue<string>()?.Replace("\n", Environment.NewLine) ?? "";

                    TxtCfgExpMultiplier.Text = (_serverConfigNode["EventExpBoostMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    TxtCfgDarMultiplier.Text = (_serverConfigNode["EventDarBoostMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    TxtCfgRdrMultiplier.Text = (_serverConfigNode["EventRdrBoostMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    TxtCfgRareMonsterMultiplier.Text = (_serverConfigNode["EventRareMonsterBoostMultiplier"]?.GetValue<double>() ?? 1.0).ToString();

                    ChkCfgAllowUnregistered.IsChecked = _serverConfigNode["AllowUnregisteredUsers"]?.GetValue<bool>() ?? false;
                    ChkCfgAllowConcurrent.IsChecked = _serverConfigNode["AllowSameAccountConcurrentLogins"]?.GetValue<bool>() ?? false;
                    ChkCfgChatCommands.IsChecked = _serverConfigNode["EnableChatCommands"]?.GetValue<bool>() ?? true;
                    ChkCfgBotSimulator.IsChecked = GetBotSimulatorState();
                }
            }
            catch (Exception ex)
            {
                SetServerConfigEnabled(false);
                TxtCfgServerName.Text = $"Error loading config: {ex.Message}";
            }
            finally
            {
                _isLoadingConfig = false;
            }
        }

        private void SetServerConfigEnabled(bool enabled)
        {
            TxtCfgServerName.IsEnabled = enabled;
            TxtCfgLocalAddress.IsEnabled = enabled;
            TxtCfgExternalAddress.IsEnabled = enabled;
            TxtCfgWelcomeMessage.IsEnabled = enabled;
            TxtCfgExpMultiplier.IsEnabled = enabled;
            TxtCfgDarMultiplier.IsEnabled = enabled;
            TxtCfgRdrMultiplier.IsEnabled = enabled;
            TxtCfgRareMonsterMultiplier.IsEnabled = enabled;
            ChkCfgAllowUnregistered.IsEnabled = enabled;
            ChkCfgAllowConcurrent.IsEnabled = enabled;
            ChkCfgChatCommands.IsEnabled = enabled;
            ChkCfgBotSimulator.IsEnabled = enabled;
            BtnSaveServerConfig.IsEnabled = enabled;
        }

        private void BtnSaveServerConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_serverConfigNode == null)
            {
                MessageBox.Show("No server configuration is loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Validate multipliers
            if (!double.TryParse(TxtCfgExpMultiplier.Text, out double exp) ||
                !double.TryParse(TxtCfgDarMultiplier.Text, out double dar) ||
                !double.TryParse(TxtCfgRdrMultiplier.Text, out double rdr) ||
                !double.TryParse(TxtCfgRareMonsterMultiplier.Text, out double rareMonster))
            {
                MessageBox.Show("Please enter valid numeric values for all boost rates.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                _serverConfigNode["ServerName"] = TxtCfgServerName.Text;
                _serverConfigNode["LocalAddress"] = TxtCfgLocalAddress.Text;
                _serverConfigNode["ExternalAddress"] = TxtCfgExternalAddress.Text;
                _serverConfigNode["WelcomeMessage"] = TxtCfgWelcomeMessage.Text.Replace(Environment.NewLine, "\n");

                _serverConfigNode["EventExpBoostMultiplier"] = exp;
                _serverConfigNode["EventDarBoostMultiplier"] = dar;
                _serverConfigNode["EventRdrBoostMultiplier"] = rdr;
                _serverConfigNode["EventRareMonsterBoostMultiplier"] = rareMonster;

                _serverConfigNode["AllowUnregisteredUsers"] = ChkCfgAllowUnregistered.IsChecked == true;
                _serverConfigNode["AllowSameAccountConcurrentLogins"] = ChkCfgAllowConcurrent.IsChecked == true;
                _serverConfigNode["EnableChatCommands"] = ChkCfgChatCommands.IsChecked == true;

                // Save back to config.json
                string configPath = GetServerConfigPath();
                var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                string jsonText = _serverConfigNode.ToJsonString(writeOptions);
                File.WriteAllText(configPath, jsonText);

                MessageBox.Show("Server configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save server config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReloadServerConfig_Click(object sender, RoutedEventArgs e)
        {
            LoadServerConfig();
        }

        private void ValidatePaths()
        {
            var brushOk = (Brush)FindResource("BorderBrush");
            var brushError = (Brush)FindResource("AccentRed");

            bool newservOk = File.Exists(TxtSetNewservPath.Text);
            TxtSetNewservPath.BorderBrush = newservOk ? brushOk : brushError;

            bool newservDirOk = Directory.Exists(TxtSetNewservDir.Text);
            TxtSetNewservDir.BorderBrush = newservDirOk ? brushOk : brushError;

            bool phpOk = File.Exists(TxtSetPhpPath.Text);
            TxtSetPhpPath.BorderBrush = phpOk ? brushOk : brushError;

            bool websiteOk = Directory.Exists(TxtSetWebsitePath.Text) && Directory.Exists(Path.Combine(TxtSetWebsitePath.Text, "api"));
            TxtSetWebsitePath.BorderBrush = websiteOk ? brushOk : brushError;
        }

        private bool GetBotSimulatorState()
        {
            if (!File.Exists(_settings.PhpPath) || !Directory.Exists(_settings.WebsitePath)) return false;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _settings.PhpPath,
                    Arguments = "-r \"require 'api/db.php'; $db = get_db(); echo $db->querySingle('SELECT value FROM site_settings WHERE key=\\'bot_autocomplete\\'') ?? '0';\"",
                    WorkingDirectory = _settings.WebsitePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return output.Trim() == "1";
                }
            }
            catch { }
            return false;
        }

        private void SetBotSimulatorState(bool enabled)
        {
            if (!File.Exists(_settings.PhpPath) || !Directory.Exists(_settings.WebsitePath)) return;
            try
            {
                string val = enabled ? "1" : "0";
                var startInfo = new ProcessStartInfo
                {
                    FileName = _settings.PhpPath,
                    Arguments = $"-r \"require 'api/db.php'; $db = get_db(); $db->exec('INSERT INTO site_settings (key, value) VALUES (\\'bot_autocomplete\\', \\'{val}\\') ON CONFLICT(key) DO UPDATE SET value=\\'{val}\\'');\"",
                    WorkingDirectory = _settings.WebsitePath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
            }
            catch { }
        }

        private void ChkCfgBotSimulator_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingConfig) return;
            SetBotSimulatorState(true);
        }

        private void ChkCfgBotSimulator_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingConfig) return;
            SetBotSimulatorState(false);
        }

        private void BtnBackupNow_Click(object sender, RoutedEventArgs e)
        {
            RunBackupProcess(true);
        }

        private void RunBackupProcess(bool interactive)
        {
            try
            {
                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                if (!Directory.Exists(backupsDir))
                {
                    Directory.CreateDirectory(backupsDir);
                }

                string tempDir = Path.Combine(backupsDir, "temp_backup");
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                Directory.CreateDirectory(tempDir);

                // 1. Copy .psochar character files from system/players/
                string playersSource = Path.Combine(_settings.NewservWorkingDirectory, "system", "players");
                if (Directory.Exists(playersSource))
                {
                    string playersDest = Path.Combine(tempDir, "players");
                    Directory.CreateDirectory(playersDest);
                    foreach (string file in Directory.GetFiles(playersSource, "*.psochar"))
                    {
                        File.Copy(file, Path.Combine(playersDest, Path.GetFileName(file)), true);
                    }
                }

                // 2. Copy system/accounts.json
                string accountsSource = Path.Combine(_settings.NewservWorkingDirectory, "system", "accounts.json");
                if (File.Exists(accountsSource))
                {
                    File.Copy(accountsSource, Path.Combine(tempDir, "accounts.json"), true);
                }

                // 3. Copy website.db from WebsitePath/db/website.db
                string dbSource = Path.Combine(_settings.WebsitePath, "db", "website.db");
                if (File.Exists(dbSource))
                {
                    File.Copy(dbSource, Path.Combine(tempDir, "website.db"), true);
                }

                // 4. Compress to ZIP
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string zipPath = Path.Combine(backupsDir, $"backup_{timestamp}.zip");
                
                ZipFile.CreateFromDirectory(tempDir, zipPath);
                
                // Cleanup temp folder
                try { Directory.Delete(tempDir, true); } catch { }

                // 5. Prune old backups
                PruneOldBackups();

                if (interactive)
                {
                    MessageBox.Show($"Backup created successfully!\nSaved to: {zipPath}", "Backup Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (interactive)
                {
                    MessageBox.Show($"Failed to create backup: {ex.Message}", "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void PruneOldBackups()
        {
            try
            {
                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                if (!Directory.Exists(backupsDir)) return;
                
                var files = Directory.GetFiles(backupsDir, "backup_*.zip")
                                     .Select(f => new FileInfo(f))
                                     .OrderByDescending(f => f.CreationTime)
                                     .Skip(_settings.KeepMaxBackups)
                                     .ToList();
                                     
                foreach (var file in files)
                {
                    try { file.Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}