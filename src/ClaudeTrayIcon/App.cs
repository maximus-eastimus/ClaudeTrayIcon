using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace ClaudeTrayIcon
{
    public class App : Application
    {
        private TrayService? _tray;

        // No XAML — this is a tray-only app with no windows.
        public override void Initialize() { }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Tray app: never auto-exit just because no window is open.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _tray = new TrayService(this, desktop);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
