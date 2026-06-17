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
            app.Initialized += (_, _) =>
            {
                try
                {
                    tray = new TrayApp();
                }
                catch (System.DllNotFoundException ex)
                {
                    // Most likely the Linux tray dependency is missing.
                    System.Console.Error.WriteLine("Could not create the tray icon: " + ex.Message);
                    System.Console.Error.WriteLine(
                        "On Linux this needs: libgtk-3-0 and libappindicator3-1 " +
                        "(e.g. `sudo apt install libgtk-3-0 libappindicator3-1`).");
                    app.Quit();
                }
                catch (System.Exception ex)
                {
                    System.Console.Error.WriteLine("Could not start: " + ex.Message);
                    app.Quit();
                }
            };
            app.Run();

            GC.KeepAlive(tray);
            GC.KeepAlive(mutex);
        }
    }
}
