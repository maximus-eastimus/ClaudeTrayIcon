using System;
using System.Threading;
using Eto.Forms;

namespace ClaudeTrayIcon
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using var mutex = new Mutex(true, "ClaudeTrayIcon_SingleInstance_8f1c", out bool createdNew);
            if (!createdNew) return;

            var app = new Application(Eto.Platform.Detect);
            TrayApp? tray = null;
            app.Initialized += (_, _) => tray = new TrayApp();
            app.Run();

            GC.KeepAlive(tray);
            GC.KeepAlive(mutex);
        }
    }
}
