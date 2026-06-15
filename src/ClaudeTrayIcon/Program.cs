using System;
using System.Threading;
using Avalonia;

namespace ClaudeTrayIcon
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Single instance (named mutex works cross-platform on .NET).
            using var mutex = new Mutex(true, "ClaudeTrayIcon_SingleInstance_8f1c", out bool createdNew);
            if (!createdNew) return;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            GC.KeepAlive(mutex);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
