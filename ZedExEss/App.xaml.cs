using System.Windows;
using ZedExEss.Diagnostics;

namespace ZedExEss
{
    /// <summary>Application entry point for both the WPF emulator and headless verification modes.</summary>
    /// <remarks>
    /// Diagnostic command-line switches are handled before a window is created, allowing the
    /// same release binary and CPU implementation to be exercised in CI without a dispatcher UI.
    /// </remarks>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (DiagnosticCommandLine.TryRun(e.Args, out int exitCode))
            {
                Shutdown(exitCode);
                return;
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }
}
