using System;
using System.Collections.Generic;
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

            // Bind TextChanged events for real-time validation and stats update
            TxtSetNewservPath.TextChanged += (s, e) => { ValidatePaths(); UpdateServerStats(); };
            TxtSetNewservDir.TextChanged += (s, e) => { ValidatePaths(); UpdateServerStats(); };
            TxtSetPhpPath.TextChanged += (s, e) => { ValidatePaths(); UpdateServerStats(); };
            TxtSetWebsitePath.TextChanged += (s, e) => { ValidatePaths(); UpdateServerStats(); };
            ValidatePaths();

            // Bind events
            BindServiceEvents();

            // Setup system tray
            SetupSystemTray();

            // Load Server Config if present
            LoadServerConfig();

            // Load Backups, Statistics, Cron Tasks, and Quest Categories
            LoadBackupsList();
            UpdateServerStats();
            LoadCronTasksList();
            LoadQuestCategories();

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
            if (_settings.AutoScheduleLobbyEvents)
            {
                ApplyScheduledLobbyEventSilently();
            }
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

                    // Load multipliers
                    TxtCfgGlobalExpMult.Text = (_serverConfigNode["BBGlobalEXPMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    TxtCfgExpShareMult.Text = (_serverConfigNode["BBEXPShareMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    TxtCfgGlobalDropMult.Text = (_serverConfigNode["ServerGlobalDropRateMultiplier"]?.GetValue<double>() ?? 1.0).ToString();

                    // Load HappyHour
                    var happyHourNode = _serverConfigNode["HappyHour"]?.AsObject();
                    if (happyHourNode != null)
                    {
                        ChkCfgHappyHourEnabled.IsChecked = happyHourNode["Enabled"]?.GetValue<bool>() ?? false;
                        TxtCfgHappyHourMin.Text = (happyHourNode["MinDropMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                        TxtCfgHappyHourMax.Text = (happyHourNode["MaxDropMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    }
                    else
                    {
                        ChkCfgHappyHourEnabled.IsChecked = false;
                        TxtCfgHappyHourMin.Text = "1.0";
                        TxtCfgHappyHourMax.Text = "1.0";
                    }

                    // Load PartyHour
                    var partyHourNode = _serverConfigNode["PartyHour"]?.AsObject();
                    if (partyHourNode != null)
                    {
                        ChkCfgPartyHourEnabled.IsChecked = partyHourNode["Enabled"]?.GetValue<bool>() ?? false;
                        TxtCfgPartyHourMin.Text = (partyHourNode["MinEXPMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                        TxtCfgPartyHourMax.Text = (partyHourNode["MaxEXPMultiplier"]?.GetValue<double>() ?? 1.0).ToString();
                    }
                    else
                    {
                        ChkCfgPartyHourEnabled.IsChecked = false;
                        TxtCfgPartyHourMin.Text = "1.0";
                        TxtCfgPartyHourMax.Text = "1.0";
                    }

                    // Load Event loop times
                    TxtCfgEventInterval.Text = (_serverConfigNode["HourEventIntervalSeconds"]?.GetValue<long>() ?? 18000).ToString();
                    TxtCfgEventDuration.Text = (_serverConfigNode["HourEventDurationSeconds"]?.GetValue<long>() ?? 5400).ToString();

                    // Load scheduler
                    ChkCfgAutoLobbyEvent.IsChecked = _settings.AutoScheduleLobbyEvents;
                    if (_settings.AutoScheduleLobbyEvents)
                    {
                        CboCfgLobbyEvent.IsEnabled = false;
                        SelectEventInComboBox(GetCalendarLobbyEvent());
                    }
                    else
                    {
                        CboCfgLobbyEvent.IsEnabled = true;
                        SelectEventInComboBox(_serverConfigNode["MenuEvent"]?.GetValue<string>() ?? "none");
                    }
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

            // New config controls
            TxtCfgGlobalExpMult.IsEnabled = enabled;
            TxtCfgExpShareMult.IsEnabled = enabled;
            TxtCfgGlobalDropMult.IsEnabled = enabled;
            ChkCfgHappyHourEnabled.IsEnabled = enabled;
            TxtCfgHappyHourMin.IsEnabled = enabled;
            TxtCfgHappyHourMax.IsEnabled = enabled;
            ChkCfgPartyHourEnabled.IsEnabled = enabled;
            TxtCfgPartyHourMin.IsEnabled = enabled;
            TxtCfgPartyHourMax.IsEnabled = enabled;
            TxtCfgEventInterval.IsEnabled = enabled;
            TxtCfgEventDuration.IsEnabled = enabled;
            ChkCfgAutoLobbyEvent.IsEnabled = enabled;
            CboCfgLobbyEvent.IsEnabled = enabled && (ChkCfgAutoLobbyEvent.IsChecked != true);
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
                !double.TryParse(TxtCfgRareMonsterMultiplier.Text, out double rareMonster) ||
                !double.TryParse(TxtCfgGlobalExpMult.Text, out double globalExp) ||
                !double.TryParse(TxtCfgExpShareMult.Text, out double expShare) ||
                !double.TryParse(TxtCfgGlobalDropMult.Text, out double globalDrop) ||
                !double.TryParse(TxtCfgHappyHourMin.Text, out double hhMin) ||
                !double.TryParse(TxtCfgHappyHourMax.Text, out double hhMax) ||
                !double.TryParse(TxtCfgPartyHourMin.Text, out double phMin) ||
                !double.TryParse(TxtCfgPartyHourMax.Text, out double phMax) ||
                !long.TryParse(TxtCfgEventInterval.Text, out long evInterval) ||
                !long.TryParse(TxtCfgEventDuration.Text, out long evDuration))
            {
                MessageBox.Show("Please enter valid numeric values for all boost rates, global multipliers, Happy/Party hours, and hourly event settings.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                // Save new multipliers
                _serverConfigNode["BBGlobalEXPMultiplier"] = globalExp;
                _serverConfigNode["BBEXPShareMultiplier"] = expShare;
                _serverConfigNode["ServerGlobalDropRateMultiplier"] = globalDrop;

                // Save HappyHour
                var happyHourNode = _serverConfigNode["HappyHour"]?.AsObject();
                if (happyHourNode == null)
                {
                    happyHourNode = new JsonObject();
                    _serverConfigNode["HappyHour"] = happyHourNode;
                }
                happyHourNode["Enabled"] = ChkCfgHappyHourEnabled.IsChecked == true;
                happyHourNode["MinDropMultiplier"] = hhMin;
                happyHourNode["MaxDropMultiplier"] = hhMax;

                // Save PartyHour
                var partyHourNode = _serverConfigNode["PartyHour"]?.AsObject();
                if (partyHourNode == null)
                {
                    partyHourNode = new JsonObject();
                    _serverConfigNode["PartyHour"] = partyHourNode;
                }
                partyHourNode["Enabled"] = ChkCfgPartyHourEnabled.IsChecked == true;
                partyHourNode["MinEXPMultiplier"] = phMin;
                partyHourNode["MaxEXPMultiplier"] = phMax;

                // Save Event loop times
                _serverConfigNode["HourEventIntervalSeconds"] = evInterval;
                _serverConfigNode["HourEventDurationSeconds"] = evDuration;

                // Save event scheduler dropdown/config
                string selectedEvent = "none";
                if (ChkCfgAutoLobbyEvent.IsChecked == true)
                {
                    selectedEvent = GetCalendarLobbyEvent();
                }
                else
                {
                    selectedEvent = (CboCfgLobbyEvent.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "none";
                }

                _serverConfigNode["MenuEvent"] = selectedEvent;

                var lobbyEventsArray = _serverConfigNode["LobbyEvents"]?.AsArray();
                if (lobbyEventsArray != null)
                {
                    for (int i = 0; i < lobbyEventsArray.Count; i++)
                    {
                        lobbyEventsArray[i] = selectedEvent;
                    }
                }

                // Save settings lobby event values
                _settings.AutoScheduleLobbyEvents = ChkCfgAutoLobbyEvent.IsChecked == true;
                _settings.SelectedLobbyEvent = selectedEvent;
                _settings.Save();

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

        private void SelectEventInComboBox(string eventName)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in CboCfgLobbyEvent.Items)
            {
                if (item.Content?.ToString() == eventName)
                {
                    CboCfgLobbyEvent.SelectedItem = item;
                    break;
                }
            }
        }

        private string GetCalendarLobbyEvent()
        {
            var now = DateTime.Now;
            if (now.Month == 12) return "xmas";
            if (now.Month == 10 && now.Day >= 15) return "hallo";
            if (now.Month == 2 && now.Day >= 8 && now.Day <= 15) return "val";
            if (now.Month == 3 && now.Day >= 8 && now.Day <= 15) return "bval";
            if (now.Month == 1 && now.Day <= 5) return "newyear";
            if (now.Month >= 3 && now.Month <= 5) return "spring";
            if (now.Month >= 6 && now.Month <= 8) return "summer";
            if (now.Month >= 9 && now.Month <= 11) return "fall";
            return "none";
        }

        private void ApplyScheduledLobbyEventSilently()
        {
            string configPath = GetServerConfigPath();
            if (!File.Exists(configPath)) return;

            try
            {
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

                var configNode = JsonNode.Parse(cleanJson, null, options)?.AsObject();
                if (configNode != null)
                {
                    string targetEvent = GetCalendarLobbyEvent();
                    configNode["MenuEvent"] = targetEvent;

                    var lobbyEventsArray = configNode["LobbyEvents"]?.AsArray();
                    if (lobbyEventsArray != null)
                    {
                        for (int i = 0; i < lobbyEventsArray.Count; i++)
                        {
                            lobbyEventsArray[i] = targetEvent;
                        }
                    }

                    var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                    string newJson = configNode.ToJsonString(writeOptions);
                    File.WriteAllText(configPath, newJson);
                }
            }
            catch { }
        }

        private void ChkCfgAutoLobbyEvent_Click(object sender, RoutedEventArgs e)
        {
            if (ChkCfgAutoLobbyEvent.IsChecked == true)
            {
                CboCfgLobbyEvent.IsEnabled = false;
                SelectEventInComboBox(GetCalendarLobbyEvent());
            }
            else
            {
                CboCfgLobbyEvent.IsEnabled = true;
                if (_serverConfigNode != null)
                {
                    SelectEventInComboBox(_serverConfigNode["MenuEvent"]?.GetValue<string>() ?? "none");
                }
            }
        }

        private void TxtNewservCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnSendNewservCommand_Click(this, new RoutedEventArgs());
            }
        }

        private void BtnSendNewservCommand_Click(object sender, RoutedEventArgs e)
        {
            string cmd = TxtNewservCommand.Text;
            if (string.IsNullOrWhiteSpace(cmd)) return;

            if (!_newservService.IsRunning)
            {
                MessageBox.Show("newserv process is not running. Please start the server first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _newservService.SendCommand(cmd);
            TxtNewservCommand.Clear();
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

                // 1. Copy player character, systems, banks, and cards files from system/players/
                string playersSource = Path.Combine(_settings.NewservWorkingDirectory, "system", "players");
                if (Directory.Exists(playersSource))
                {
                    string playersDest = Path.Combine(tempDir, "players");
                    Directory.CreateDirectory(playersDest);
                    
                    // Copy all files matching player data patterns
                    string[] extensions = { "*.psochar", "*.psobank", "*.psocard", "*.psosys" };
                    foreach (string ext in extensions)
                    {
                        foreach (string file in Directory.GetFiles(playersSource, ext))
                        {
                            File.Copy(file, Path.Combine(playersDest, Path.GetFileName(file)), true);
                        }
                    }
                }

                // 2. Copy system/accounts.json
                string accountsSource = Path.Combine(_settings.NewservWorkingDirectory, "system", "accounts.json");
                if (File.Exists(accountsSource))
                {
                    File.Copy(accountsSource, Path.Combine(tempDir, "accounts.json"), true);
                }

                // 3. Copy system/config.json
                string configSource = Path.Combine(_settings.NewservWorkingDirectory, "system", "config.json");
                if (File.Exists(configSource))
                {
                    File.Copy(configSource, Path.Combine(tempDir, "config.json"), true);
                }

                // 4. Copy website.db from WebsitePath/db/website.db
                string dbSource = Path.Combine(_settings.WebsitePath, "db", "website.db");
                if (File.Exists(dbSource))
                {
                    File.Copy(dbSource, Path.Combine(tempDir, "website.db"), true);
                }

                // 5. Compress to ZIP
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string zipPath = Path.Combine(backupsDir, $"backup_{timestamp}.zip");
                
                ZipFile.CreateFromDirectory(tempDir, zipPath);
                
                // Cleanup temp folder
                try { Directory.Delete(tempDir, true); } catch { }

                // 6. Prune old backups
                PruneOldBackups();

                // 7. Refresh list and stats
                LoadBackupsList();
                UpdateServerStats();

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

        private void LoadBackupsList()
        {
            try
            {
                LstBackups.Items.Clear();
                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                if (!Directory.Exists(backupsDir))
                {
                    return;
                }
                var files = Directory.GetFiles(backupsDir, "backup_*.zip")
                                     .Select(f => new FileInfo(f))
                                     .OrderByDescending(f => f.CreationTime)
                                     .ToList();
                foreach (var file in files)
                {
                    double sizeKb = file.Length / 1024.0;
                    string sizeStr = sizeKb >= 1024.0 ? $"{(sizeKb / 1024.0):F2} MB" : $"{sizeKb:F1} KB";
                    LstBackups.Items.Add(new BackupItem
                    {
                        Filename = file.Name,
                        DateCreated = file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        Size = sizeStr
                    });
                }
            }
            catch { }
        }

        private void UpdateServerStats()
        {
            try
            {
                // 1. Registered Accounts (*.psosys in system/players/)
                int accountsCount = 0;
                int charactersCount = 0;
                string playersDir = Path.Combine(_settings.NewservWorkingDirectory, "system", "players");
                if (Directory.Exists(playersDir))
                {
                    accountsCount = Directory.GetFiles(playersDir, "*.psosys").Length;
                    
                    // Filter out *.bak files
                    charactersCount = Directory.GetFiles(playersDir, "*.psochar")
                                               .Count(f => !f.Contains(".bak", StringComparison.OrdinalIgnoreCase));
                }
                TxtStatsAccounts.Text = accountsCount.ToString();
                TxtStatsCharacters.Text = charactersCount.ToString();

                // 2. Backup Archives count
                int backupsCount = 0;
                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                if (Directory.Exists(backupsDir))
                {
                    backupsCount = Directory.GetFiles(backupsDir, "backup_*.zip").Length;
                }
                TxtStatsBackups.Text = backupsCount.ToString();

                // 3. Database size
                string dbPath = Path.Combine(_settings.WebsitePath, "db", "website.db");
                if (File.Exists(dbPath))
                {
                    long len = new FileInfo(dbPath).Length;
                    double kb = len / 1024.0;
                    TxtStatsDbSize.Text = kb >= 1024.0 ? $"{(kb / 1024.0):F2} MB" : $"{kb:F1} KB";
                }
                else
                {
                    TxtStatsDbSize.Text = "Not found";
                }
            }
            catch
            {
                TxtStatsAccounts.Text = "Error";
                TxtStatsCharacters.Text = "Error";
                TxtStatsBackups.Text = "Error";
                TxtStatsDbSize.Text = "Error";
            }
        }

        private void BtnRefreshStats_Click(object sender, RoutedEventArgs e)
        {
            UpdateServerStats();
            MessageBox.Show("Statistics refreshed!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCreateBackupTab_Click(object sender, RoutedEventArgs e)
        {
            RunBackupProcess(true);
        }

        private void BtnOpenBackupsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                if (!Directory.Exists(backupsDir))
                {
                    Directory.CreateDirectory(backupsDir);
                }
                Process.Start("explorer.exe", $"\"{backupsDir}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstBackups.SelectedItem as BackupItem;
            if (selected == null)
            {
                MessageBox.Show("Please select a backup to delete.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Are you sure you want to delete backup '{selected.Filename}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                    string filePath = Path.Combine(backupsDir, selected.Filename);
                    if (File.Exists(filePath))
                        File.Delete(filePath);

                    LoadBackupsList();
                    UpdateServerStats();
                    MessageBox.Show("Backup deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete backup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstBackups.SelectedItem as BackupItem;
            if (selected == null)
            {
                MessageBox.Show("Please select a backup to restore.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirmMsg = $"Are you sure you want to restore '{selected.Filename}'?\n\n" +
                             "WARNING: This will overwrite your current character files, accounts, server configurations, and database. " +
                             "Any active server or cron services will be stopped during restoration.";
            var confirm = MessageBox.Show(confirmMsg, "Confirm Restoration", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            bool restartNewserv = _newservService.IsRunning;
            bool restartCrons = _cronService.IsRunning;

            try
            {
                // 1. Stop services
                if (restartNewserv)
                {
                    _newservService.Stop();
                }
                if (restartCrons)
                {
                    _cronService.Stop();
                }

                // Wait a brief moment for locks to clear
                System.Threading.Thread.Sleep(1000);

                string backupsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                string zipPath = Path.Combine(backupsDir, selected.Filename);
                string tempExtractDir = Path.Combine(backupsDir, "temp_restore");

                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, true);
                }
                Directory.CreateDirectory(tempExtractDir);

                // Extract zip
                ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

                // 2. Restore players files
                string playersSource = Path.Combine(tempExtractDir, "players");
                string playersDest = Path.Combine(_settings.NewservWorkingDirectory, "system", "players");
                if (Directory.Exists(playersSource))
                {
                    if (!Directory.Exists(playersDest))
                    {
                        Directory.CreateDirectory(playersDest);
                    }
                    // Clean existing player files first to avoid leftovers
                    foreach (string file in Directory.GetFiles(playersDest))
                    {
                        try { File.Delete(file); } catch { }
                    }
                    // Copy files
                    foreach (string file in Directory.GetFiles(playersSource))
                    {
                        string destFile = Path.Combine(playersDest, Path.GetFileName(file));
                        File.Copy(file, destFile, true);
                    }
                }

                // 3. Restore accounts.json (if present)
                string accountsSource = Path.Combine(tempExtractDir, "accounts.json");
                string accountsDest = Path.Combine(_settings.NewservWorkingDirectory, "system", "accounts.json");
                if (File.Exists(accountsSource))
                {
                    File.Copy(accountsSource, accountsDest, true);
                }

                // 4. Restore config.json (if present)
                string configSource = Path.Combine(tempExtractDir, "config.json");
                string configDest = Path.Combine(_settings.NewservWorkingDirectory, "system", "config.json");
                if (File.Exists(configSource))
                {
                    File.Copy(configSource, configDest, true);
                }

                // 5. Restore website.db (if present)
                string dbSource = Path.Combine(tempExtractDir, "website.db");
                string dbDest = Path.Combine(_settings.WebsitePath, "db", "website.db");
                if (File.Exists(dbSource))
                {
                    string dbDir = Path.GetDirectoryName(dbDest) ?? "";
                    if (!Directory.Exists(dbDir))
                    {
                        Directory.CreateDirectory(dbDir);
                    }
                    File.Copy(dbSource, dbDest, true);
                }

                // Cleanup temp folder
                try { Directory.Delete(tempExtractDir, true); } catch { }

                // Reload config & UI
                LoadServerConfig();
                UpdateServerStats();

                // 6. Restart services if they were running
                if (restartNewserv)
                {
                    _newservService.Configure(_settings.NewservPath, _settings.NewservWorkingDirectory);
                    _newservService.Start();
                }
                if (restartCrons)
                {
                    _cronService.Start();
                }

                MessageBox.Show("Backup restored successfully!", "Restoration Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restore backup: {ex.Message}", "Restoration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- CRON MANAGER METHODS ---

        private void LoadCronTasksList()
        {
            try
            {
                LstCronTasks.ItemsSource = null;
                LstCronTasks.ItemsSource = _settings.CronTasks;
            }
            catch { }
        }

        private void LstCronTasks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selected = LstCronTasks.SelectedItem as CronTaskSettings;
            if (selected != null)
            {
                TxtCronEditorTitle.Text = "EDIT CRON TASK";
                TxtCronName.Text = selected.Name;
                TxtCronScriptPath.Text = selected.ScriptPath;
                TxtCronIntervalSec.Text = selected.IntervalSeconds.ToString();
                ChkCronTaskEnabled.IsChecked = selected.Enabled;
                BtnDeleteCronTask.IsEnabled = true;
            }
            else
            {
                BtnCancelCronEdit_Click(this, new RoutedEventArgs());
            }
        }

        private void BtnSaveCronTask_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtCronName.Text))
            {
                MessageBox.Show("Please enter a task name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(TxtCronScriptPath.Text))
            {
                MessageBox.Show("Please enter a script path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!int.TryParse(TxtCronIntervalSec.Text, out int interval) || interval < 1)
            {
                MessageBox.Show("Please enter a valid interval in seconds (minimum 1).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var selected = LstCronTasks.SelectedItem as CronTaskSettings;
            if (selected != null)
            {
                lock (_settings.CronTasks)
                {
                    selected.Name = TxtCronName.Text;
                    selected.ScriptPath = TxtCronScriptPath.Text;
                    selected.IntervalSeconds = interval;
                    selected.Enabled = ChkCronTaskEnabled.IsChecked == true;
                }
            }
            else
            {
                lock (_settings.CronTasks)
                {
                    _settings.CronTasks.Add(new CronTaskSettings
                    {
                        Name = TxtCronName.Text,
                        ScriptPath = TxtCronScriptPath.Text,
                        IntervalSeconds = interval,
                        Enabled = ChkCronTaskEnabled.IsChecked == true
                    });
                }
            }

            _settings.Save();
            LoadCronTasksList();
            BtnCancelCronEdit_Click(this, e);
            MessageBox.Show("Cron task saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDeleteCronTask_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstCronTasks.SelectedItem as CronTaskSettings;
            if (selected == null)
            {
                MessageBox.Show("Please select a task to delete.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Are you sure you want to delete cron task '{selected.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                lock (_settings.CronTasks)
                {
                    _settings.CronTasks.Remove(selected);
                }
                _settings.Save();
                LoadCronTasksList();
                BtnCancelCronEdit_Click(this, e);
                MessageBox.Show("Cron task deleted successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCancelCronEdit_Click(object sender, RoutedEventArgs e)
        {
            LstCronTasks.SelectedItem = null;
            TxtCronEditorTitle.Text = "ADD / EDIT CRON TASK";
            TxtCronName.Clear();
            TxtCronScriptPath.Clear();
            TxtCronIntervalSec.Clear();
            ChkCronTaskEnabled.IsChecked = true;
            BtnDeleteCronTask.IsEnabled = false;
        }

        private void BtnResetCronsDefault_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to reset all cron tasks to Pioneer II PSOBB defaults?\n\nAny custom cron tasks you added will be removed.", "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                lock (_settings.CronTasks)
                {
                    _settings.CronTasks = LauncherSettings.GetDefaultCronTasks();
                }
                _settings.Save();
                LoadCronTasksList();
                BtnCancelCronEdit_Click(this, e);
                MessageBox.Show("Cron tasks reset to defaults successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ChkCronEnabled_Click(object sender, RoutedEventArgs e)
        {
            _settings.Save();
        }

        // --- QUEST MANAGER METHODS ---

        private void LoadQuestCategories()
        {
            try
            {
                string questsDir = Path.Combine(_settings.NewservWorkingDirectory, "system", "quests");
                if (!Directory.Exists(questsDir))
                {
                    LstQuestCategories.ItemsSource = null;
                    return;
                }

                var categories = new List<string>();
                foreach (string dir in Directory.GetDirectories(questsDir))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".") || name.StartsWith("_") || name.ToLower() == "includes")
                    {
                        continue;
                    }
                    categories.Add(name);
                }

                LstQuestCategories.ItemsSource = categories.OrderBy(c => c).ToList();
                if (categories.Count > 0)
                {
                    LstQuestCategories.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load quest categories: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LstQuestCategories_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            string selected = LstQuestCategories.SelectedItem as string;
            if (!string.IsNullOrEmpty(selected))
            {
                LoadQuestsForCategory(selected);
            }
            else
            {
                LstQuests.ItemsSource = null;
                TxtQuestsHeader.Text = "QUESTS IN SELECTED CATEGORY";
            }
        }

        private string GetQuestBaseName(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename);
            if (name.EndsWith("-e") || name.EndsWith("-j") || name.EndsWith("-f") || name.EndsWith("-g") || name.EndsWith("-s"))
            {
                name = name.Substring(0, name.Length - 2);
            }
            return name;
        }

        private void LoadQuestsForCategory(string category)
        {
            try
            {
                string categoryDir = Path.Combine(_settings.NewservWorkingDirectory, "system", "quests", category);
                if (!Directory.Exists(categoryDir)) return;

                var questMap = new Dictionary<string, QuestItem>();

                // 1. Scan enabled files (main category folder)
                foreach (string filePath in Directory.GetFiles(categoryDir))
                {
                    string ext = Path.GetExtension(filePath).ToLower();
                    if (ext != ".dat" && ext != ".bin" && ext != ".json") continue;

                    string baseName = GetQuestBaseName(filePath);
                    if (!questMap.ContainsKey(baseName))
                    {
                        questMap[baseName] = new QuestItem
                        {
                            BaseName = baseName,
                            Enabled = true,
                            Category = category,
                            LastModified = File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm")
                        };
                    }
                    
                    questMap[baseName].FilePaths.Add(filePath);
                    if (ext == ".json") questMap[baseName].HasConfig = "Yes";
                }

                // 2. Scan disabled files (categoryDir/.disabled folder)
                string disabledDir = Path.Combine(categoryDir, ".disabled");
                if (Directory.Exists(disabledDir))
                {
                    foreach (string filePath in Directory.GetFiles(disabledDir))
                    {
                        string ext = Path.GetExtension(filePath).ToLower();
                        if (ext != ".dat" && ext != ".bin" && ext != ".json") continue;

                        string baseName = GetQuestBaseName(filePath);
                        if (!questMap.ContainsKey(baseName))
                        {
                            questMap[baseName] = new QuestItem
                            {
                                BaseName = baseName,
                                Enabled = false,
                                Category = category,
                                LastModified = File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm")
                            };
                        }
                        
                        questMap[baseName].FilePaths.Add(filePath);
                        if (ext == ".json") questMap[baseName].HasConfig = "Yes";
                    }
                }

                // Complete Extensions list for each item
                foreach (var item in questMap.Values)
                {
                    var exts = item.FilePaths.Select(p => Path.GetExtension(p).ToLower()).Distinct().OrderBy(e => e);
                    item.Extension = string.Join("/", exts);
                }

                LstQuests.ItemsSource = null;
                LstQuests.ItemsSource = questMap.Values.OrderBy(q => q.BaseName).ToList();
                TxtQuestsHeader.Text = $"QUESTS IN '{category.ToUpper()}' ({questMap.Count} TOTAL)";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading quests: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LstQuests_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selected = LstQuests.SelectedItem as QuestItem;
            if (selected == null)
            {
                TxtQuestAvailableIf.Text = "";
                TxtQuestAvailableIf.IsEnabled = false;
                BtnSaveQuestConfig.IsEnabled = false;
                return;
            }

            TxtQuestAvailableIf.IsEnabled = true;
            BtnSaveQuestConfig.IsEnabled = true;

            string jsonPath = selected.FilePaths.FirstOrDefault(p => Path.GetExtension(p).ToLower() == ".json");
            if (jsonPath != null && File.Exists(jsonPath))
            {
                try
                {
                    string jsonText = File.ReadAllText(jsonPath);
                    var node = JsonNode.Parse(jsonText)?.AsObject();
                    TxtQuestAvailableIf.Text = node?["AvailableIf"]?.GetValue<string>() ?? "";
                }
                catch
                {
                    TxtQuestAvailableIf.Text = "";
                }
            }
            else
            {
                TxtQuestAvailableIf.Text = "";
            }
        }

        private void ChkQuestEnabled_Click(object sender, RoutedEventArgs e)
        {
            var checkBox = sender as System.Windows.Controls.CheckBox;
            var item = checkBox?.DataContext as QuestItem;
            if (item == null) return;

            bool targetEnabled = checkBox.IsChecked == true;
            string categoryDir = Path.Combine(_settings.NewservWorkingDirectory, "system", "quests", item.Category);
            string disabledDir = Path.Combine(categoryDir, ".disabled");

            try
            {
                if (targetEnabled)
                {
                    // Move from .disabled to main folder
                    foreach (string path in item.FilePaths)
                    {
                        if (path.Contains(".disabled"))
                        {
                            string filename = Path.GetFileName(path);
                            string destPath = Path.Combine(categoryDir, filename);
                            if (File.Exists(path))
                            {
                                File.Move(path, destPath, true);
                            }
                        }
                    }
                }
                else
                {
                    // Move from main folder to .disabled
                    if (!Directory.Exists(disabledDir))
                    {
                        Directory.CreateDirectory(disabledDir);
                    }

                    foreach (string path in item.FilePaths)
                    {
                        if (!path.Contains(".disabled"))
                        {
                            string filename = Path.GetFileName(path);
                            string destPath = Path.Combine(disabledDir, filename);
                            if (File.Exists(path))
                            {
                                File.Move(path, destPath, true);
                            }
                        }
                    }
                }

                // Reload the quest list
                LoadQuestsForCategory(item.Category);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to toggle quest state: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                // Reset checkbox state
                checkBox.IsChecked = !targetEnabled;
            }
        }

        private void BtnSaveQuestConfig_Click(object sender, RoutedEventArgs e)
        {
            var selected = LstQuests.SelectedItem as QuestItem;
            if (selected == null) return;

            string categoryDir = Path.Combine(_settings.NewservWorkingDirectory, "system", "quests", selected.Category);
            string targetFolder = selected.Enabled ? categoryDir : Path.Combine(categoryDir, ".disabled");
            string jsonPath = Path.Combine(targetFolder, selected.BaseName + ".json");

            try
            {
                JsonObject node = null;
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        string jsonText = File.ReadAllText(jsonPath);
                        node = JsonNode.Parse(jsonText)?.AsObject();
                    }
                    catch { }
                }

                if (node == null)
                {
                    node = new JsonObject();
                }

                if (string.IsNullOrWhiteSpace(TxtQuestAvailableIf.Text))
                {
                    if (File.Exists(jsonPath))
                    {
                        File.Delete(jsonPath);
                    }
                }
                else
                {
                    node["AvailableIf"] = TxtQuestAvailableIf.Text.Trim();
                    var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                    string newJson = node.ToJsonString(writeOptions);
                    File.WriteAllText(jsonPath, newJson);
                }

                // Reload the quest lists to pick up config presence
                LoadQuestsForCategory(selected.Category);
                MessageBox.Show("Quest configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save quest config: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnUploadQuest_Click(object sender, RoutedEventArgs e)
        {
            string selectedCategory = LstQuestCategories.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedCategory))
            {
                MessageBox.Show("Please select a quest category first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Quest Files (*.dat;*.bin)|*.dat;*.bin|All Files (*.*)|*.*",
                Multiselect = true,
                Title = "Select Quest Files to Upload"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string categoryDir = Path.Combine(_settings.NewservWorkingDirectory, "system", "quests", selectedCategory);
                int count = 0;
                try
                {
                    foreach (string filename in openFileDialog.FileNames)
                    {
                        string destPath = Path.Combine(categoryDir, Path.GetFileName(filename));
                        File.Copy(filename, destPath, true);
                        count++;
                    }

                    LoadQuestsForCategory(selectedCategory);
                    MessageBox.Show($"Successfully uploaded {count} quest file(s) to '{selectedCategory}'.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to upload quest files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnOpenQuestsDir_Click(object sender, RoutedEventArgs e)
        {
            string selectedCategory = LstQuestCategories.SelectedItem as string;
            string targetPath = Path.Combine(_settings.NewservWorkingDirectory, "system", "quests");
            if (!string.IsNullOrEmpty(selectedCategory))
            {
                targetPath = Path.Combine(targetPath, selectedCategory);
            }

            if (Directory.Exists(targetPath))
            {
                try
                {
                    Process.Start("explorer.exe", targetPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open quests folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Quests directory does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public class BackupItem
    {
        public string Filename { get; set; } = "";
        public string DateCreated { get; set; } = "";
        public string Size { get; set; } = "";
    }

    public class QuestItem
    {
        public string BaseName { get; set; } = "";
        public string Extension { get; set; } = "";
        public string HasConfig { get; set; } = "No";
        public string LastModified { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public string Category { get; set; } = "";
        public System.Collections.Generic.List<string> FilePaths { get; set; } = new System.Collections.Generic.List<string>();
    }
}