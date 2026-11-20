using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DiscordUnblock
{
    public partial class App : Application
    {
        const string MutexName = "DiscordUnblock.SingleInstance";
        const string ShowEventName = "DiscordUnblock.ShowWindow";

        static Mutex _single;
        static EventWaitHandle _showEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
#if RENDER
            int ri = Array.FindIndex(e.Args, a => a == "--render");
            if (ri >= 0)
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    await DiscordUnblock.MainWindow.RenderAllAsync(e.Args.Length > ri + 1 ? e.Args[ri + 1] : ".");
                    Shutdown();
                });
                return;
            }
#endif
            bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            _single = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                // Already running (probably in the tray): ask that copy to show its window.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, a) =>
            {
                MessageBox.Show(a.Exception.Message, "DiscordUnblock error", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            var window = new MainWindow();
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }) { IsBackground = true }.Start();

            window.Start(startInTray);
        }
    }
}
