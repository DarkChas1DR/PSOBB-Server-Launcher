using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Launcher
{
    public class ProcessService
    {
        private static readonly object _fileLock = new object();
        private Process? _process;
        private bool _isUserStopped = false;
        private readonly string _name;
        private string _executablePath = "";
        private string _workingDirectory = "";
        private string _arguments = "";

        public event Action<string>? OnOutputReceived;
        public event Action<bool>? OnStatusChanged; // True = Running, False = Stopped

        public bool IsRunning => _process != null && !_process.HasExited;

        public ProcessService(string name)
        {
            _name = name;
        }

        public void Configure(string executablePath, string workingDirectory, string arguments = "")
        {
            _executablePath = executablePath;
            _workingDirectory = workingDirectory;
            _arguments = arguments;
        }

        public void Start()
        {
            if (IsRunning)
            {
                Log($"[{_name}] Process is already running.");
                return;
            }

            if (!File.Exists(_executablePath))
            {
                Log($"[{_name}] Error: Executable not found at: {_executablePath}");
                OnStatusChanged?.Invoke(false);
                return;
            }

            _isUserStopped = false;
            
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _executablePath,
                    Arguments = _arguments,
                    WorkingDirectory = string.IsNullOrEmpty(_workingDirectory) ? Path.GetDirectoryName(_executablePath) : _workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log($"[ERROR] {e.Data}"); };
                _process.Exited += Process_Exited;

                Log($"[{_name}] Starting process...");
                if (_process.Start())
                {
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                    OnStatusChanged?.Invoke(true);
                }
                else
                {
                    Log($"[{_name}] Error: Failed to start process.");
                    _process = null;
                    OnStatusChanged?.Invoke(false);
                }
            }
            catch (Exception ex)
            {
                Log($"[{_name}] Error during start: {ex.Message}");
                _process = null;
                OnStatusChanged?.Invoke(false);
            }
        }

        public void SendCommand(string command)
        {
            if (IsRunning && _process != null && _process.StartInfo.RedirectStandardInput)
            {
                try
                {
                    _process.StandardInput.WriteLine(command);
                }
                catch (Exception ex)
                {
                    Log($"[{_name}] Error sending command: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            if (!IsRunning)
            {
                Log($"[{_name}] Process is not running.");
                return;
            }

            Log($"[{_name}] Stopping process...");
            _isUserStopped = true;

            try
            {
                if (_process != null)
                {
                    // Try to shut down gracefully first
                    if (!_process.CloseMainWindow())
                    {
                        _process.Kill(true);
                    }
                    _process.Dispose();
                    _process = null;
                }
            }
            catch (Exception ex)
            {
                Log($"[{_name}] Error during stop: {ex.Message}");
            }
            finally
            {
                OnStatusChanged?.Invoke(false);
            }
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            OnStatusChanged?.Invoke(false);
            
            if (_process != null)
            {
                int exitCode = -1;
                try { exitCode = _process.ExitCode; } catch { }
                
                Log($"[{_name}] Process exited with code {exitCode}.");
                _process.Dispose();
                _process = null;
            }

            if (!_isUserStopped)
            {
                Log($"[{_name}] Process crashed or exited unexpectedly. Restarting in 3 seconds...");
                Task.Delay(3000).ContinueWith(_ =>
                {
                    if (!_isUserStopped)
                    {
                        Start();
                    }
                });
            }
        }

        private void Log(string text)
        {
            OnOutputReceived?.Invoke(text);

            try
            {
                lock (_fileLock)
                {
                    string logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    if (!Directory.Exists(logsDir))
                    {
                        Directory.CreateDirectory(logsDir);
                    }
                    string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                    string logFile = Path.Combine(logsDir, $"{_name.Replace(" ", "_").ToLower()}_{dateStr}.log");
                    
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}";
                    File.AppendAllText(logFile, logLine);
                }
            }
            catch { }
        }
    }
}
