using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher
{
    public class CronService
    {
        private readonly LauncherSettings _settings;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private ProcessService? _batchProcessService;
        private bool _isRunning = false;
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastRunTimes = new System.Collections.Generic.Dictionary<string, DateTime>();

        public event Action<string>? OnOutputReceived;
        public event Action<bool>? OnStatusChanged; // True = Running, False = Stopped

        public bool IsRunning => _isRunning;

        public CronService(LauncherSettings settings)
        {
            _settings = settings;
        }

        public void Start()
        {
            if (_isRunning)
            {
                Log("[CRON] Crons are already running.");
                return;
            }

            _isRunning = true;
            OnStatusChanged?.Invoke(true);

            if (_settings.RunCronsDirectly)
            {
                Log("[CRON] Starting crons in direct C# execution mode...");
                _lastRunTimes.Clear();
                _cts = new CancellationTokenSource();
                _loopTask = Task.Run(() => CronLoop(_cts.Token));
            }
            else
            {
                Log("[CRON] Starting crons via batch file mode...");
                
                // Find the batch file
                string batPath = Path.Combine(_settings.WebsitePath, "run_daemons.bat");
                if (!File.Exists(batPath))
                {
                    // Look in the server directory
                    batPath = Path.Combine(_settings.NewservWorkingDirectory, "start_bounty.bat");
                }
                
                if (!File.Exists(batPath))
                {
                    Log("[CRON] Error: Batch file 'run_daemons.bat' or 'start_bounty.bat' not found.");
                    _isRunning = false;
                    OnStatusChanged?.Invoke(false);
                    return;
                }

                _batchProcessService = new ProcessService("Cron Batch");
                _batchProcessService.Configure(batPath, Path.GetDirectoryName(batPath) ?? _settings.WebsitePath);
                
                _batchProcessService.OnOutputReceived += (text) => OnOutputReceived?.Invoke(text);
                _batchProcessService.OnStatusChanged += (running) =>
                {
                    if (!running && _isRunning)
                    {
                        // Process stopped unexpectedly
                        Log("[CRON] Batch process stopped unexpectedly.");
                        _isRunning = false;
                        OnStatusChanged?.Invoke(false);
                    }
                };

                _batchProcessService.Start();
            }
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                Log("[CRON] Crons are not running.");
                return;
            }

            Log("[CRON] Stopping crons...");
            _isRunning = false;

            if (_settings.RunCronsDirectly)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    try
                    {
                        _loopTask?.Wait(1000);
                    }
                    catch { }
                    _cts.Dispose();
                    _cts = null;
                }
            }
            else
            {
                if (_batchProcessService != null)
                {
                    _batchProcessService.Stop();
                    _batchProcessService = null;
                }
            }

            Log("[CRON] Crons stopped.");
            OnStatusChanged?.Invoke(false);
        }

        private async Task CronLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string time = DateTime.Now.ToString("HH:mm:ss");
                    var tasks = new System.Collections.Generic.List<Task>();

                    // Create a local copy to prevent thread modification exceptions
                    var tasksToRun = new System.Collections.Generic.List<CronTaskSettings>();
                    lock (_settings.CronTasks)
                    {
                        tasksToRun.AddRange(_settings.CronTasks);
                    }

                    foreach (var task in tasksToRun)
                    {
                        if (!task.Enabled) continue;

                        string path = task.ScriptPath;
                        if (string.IsNullOrWhiteSpace(path)) continue;

                        if (!_lastRunTimes.TryGetValue(path, out DateTime lastRun))
                        {
                            lastRun = DateTime.MinValue;
                        }

                        if ((DateTime.Now - lastRun).TotalSeconds >= task.IntervalSeconds)
                        {
                            _lastRunTimes[path] = DateTime.Now;
                            Log($"[{time}] [{task.Name}] Starting script: {path}...");
                            tasks.Add(RunPhpScriptAsync(path, token));
                        }
                    }

                    if (tasks.Count > 0)
                    {
                        // Wait for all to finish, up to a timeout of 10s
                        var allTasks = Task.WhenAll(tasks);
                        var delayTask = Task.Delay(10000, token);
                        var completedTask = await Task.WhenAny(allTasks, delayTask);
                        
                        if (completedTask == delayTask && !allTasks.IsCompleted)
                        {
                            Log("[CRON WARNING] Some scripts took longer than 10 seconds to execute.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"[CRON ERROR] Loop error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunPhpScriptAsync(string relativeScriptPath, CancellationToken token)
        {
            string scriptName = Path.GetFileName(relativeScriptPath);
            try
            {
                if (token.IsCancellationRequested) return;

                string fullScriptPath = Path.Combine(_settings.WebsitePath, relativeScriptPath);
                if (!File.Exists(fullScriptPath))
                {
                    Log($"[CRON] Error: Script not found: {fullScriptPath}");
                    return;
                }

                if (!File.Exists(_settings.PhpPath))
                {
                    Log($"[CRON] Error: PHP executable not found at: {_settings.PhpPath}");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _settings.PhpPath,
                    Arguments = $"\"{fullScriptPath}\"",
                    WorkingDirectory = _settings.WebsitePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    Log($"[{scriptName}] Error: Failed to start process.");
                    return;
                }

                var outTask = process.StandardOutput.ReadToEndAsync(token);
                var errTask = process.StandardError.ReadToEndAsync(token);

                await Task.WhenAll(outTask, errTask);
                await process.WaitForExitAsync(token);

                string output = outTask.Result;
                string error = errTask.Result;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Log($"[{scriptName}] {output.Trim()}");
                }
                if (!string.IsNullOrWhiteSpace(error))
                {
                    Log($"[{scriptName} ERROR] {error.Trim()}");
                }
            }
            catch (OperationCanceledException)
            {
                // Task was stopped
            }
            catch (Exception ex)
            {
                Log($"[{scriptName}] Exception: {ex.Message}");
            }
        }

        private void Log(string text)
        {
            OnOutputReceived?.Invoke(text);
        }
    }
}
