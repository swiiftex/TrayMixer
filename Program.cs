using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TrayMixer
{
    static class Program
    {
        const string MutexName = @"Local\TrayMixer.SingleInstance";
        public const string QuitEventName = @"Local\TrayMixer.Quit";
        public const string ShowEventName = @"Local\TrayMixer.Show";

        // TrayMixer.exe          start in the tray and open the mixer (or open it in the copy already running)
        // TrayMixer.exe --tray   start in the tray only (used when Windows starts)
        // TrayMixer.exe --quit   close the running copy (used by the installer)
        [STAThread]
        static void Main(string[] args)
        {
            bool quit = HasFlag(args, "--quit");
            bool trayOnly = HasFlag(args, "--tray");
            using (var mutex = new Mutex(true, MutexName, out bool isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    if (quit)
                    {
                        Signal(QuitEventName);
                        WaitForExit(mutex);
                    }
                    else if (!trayOnly)
                    {
                        // Let the running copy bring its mixer to the front; this process holds the right to do so.
                        AllowSetForegroundWindow(AsfwAny);
                        Signal(ShowEventName);
                    }
                    return;
                }
                if (quit)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(openMixer: !trayOnly));
                mutex.ReleaseMutex();
            }
        }

        static bool HasFlag(string[] args, string flag) =>
            Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        static void Signal(string eventName)
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(eventName))
                    handle.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        static void WaitForExit(Mutex mutex)
        {
            try
            {
                if (mutex.WaitOne(5000))
                    mutex.ReleaseMutex();
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
            }
        }

        const int AsfwAny = -1;

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);
    }
}
