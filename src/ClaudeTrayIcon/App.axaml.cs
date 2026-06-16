using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ClaudeTrayIcon
{
    public partial class App : Application
    {
        private TrayService? _tray;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Tray app: never auto-exit just because no window is open.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                // Use the TrayIcon declared in App.axaml (required for the native
                // menu to show on Windows).
                var tray = TrayIcon.GetIcons(this)?[0];
                if (tray != null)
                    _tray = new TrayService(desktop, tray);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
