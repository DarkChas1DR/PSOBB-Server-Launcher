using System;
using System.IO;
using System.Configuration;
using System.Data;
using System.Windows;

namespace Launcher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                LogCrash(args.ExceptionObject as Exception);
            };

            DispatcherUnhandledException += (sender, args) =>
            {
                LogCrash(args.Exception);
                args.Handled = true;
            };

            base.OnStartup(e);
        }

        private static void LogCrash(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt");
                File.WriteAllText(logPath, $"[{DateTime.Now}] Crash report:\n{ex.ToString()}\n");
                MessageBox.Show($"The application crashed on startup. Details saved to crash.txt.\n\nError: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // Fallback if writing fails
            }
        }
}

